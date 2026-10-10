using FrameForge.Core.Semantics.V2;
using Xunit;

namespace FrameForge.Core.Tests;

public sealed class V2SemanticEditingSessionTests
{
    [Fact]
    public void SelectionUsesStableSemanticIdsAcrossRenameAndLayoutIndependentEdits()
    {
        var session = SessionWithControl(out var id);
        session.ReplaceSelection(id);

        var change = session.Execute("Rename", (editor, document) =>
            editor.RenameControl(document, id, "Renamed", "DifferentRuntimeName"));

        Assert.True(change.Success);
        Assert.Equal(id, session.Selection.PrimaryId);
        Assert.Equal(id, Assert.Single(session.Selection.OrderedIds));
    }

    [Fact]
    public void MultipleSelectionHasOneOrderedPrimaryAndPrunesDeletedIdentity()
    {
        var session = SessionWithControl(out var first);
        var second = session.Execute("Create second", (editor, document) => editor.CreateControl(document,
            UiNodeKind.Button, OwnerReference.Root(document.CompositionRoots[0].Id), "Second"));
        var secondId = second.Document.Nodes.Single(node => node.Id != first).Id;
        session.ReplaceSelection(first);
        session.ToggleSelection(secondId);
        Assert.Equal(secondId, session.Selection.PrimaryId);

        var deleted = session.Execute("Delete primary", (editor, document) => editor.DeleteControl(document, secondId));

        Assert.True(deleted.Success);
        Assert.Equal(first, session.Selection.PrimaryId);
        Assert.Equal([first], session.Selection.OrderedIds);
    }

    [Fact]
    public void CreateUndoRedoPreservesIdentityOrderAndSelection()
    {
        var document = Document();
        var session = new SemanticEditingSession(document);
        var created = session.Execute("Create", (editor, current) => editor.CreateControl(current,
            UiNodeKind.Frame, OwnerReference.Root(current.CompositionRoots[0].Id), "Frame"),
            (selection, result) => selection.Replace(result.AffectedId));
        var id = created.Selection.PrimaryId!.Value;

        Assert.True(session.Undo().Success);
        Assert.Empty(session.Document.Nodes);
        Assert.Null(session.Selection.PrimaryId);
        Assert.True(session.Redo().Success);
        Assert.Equal(id, Assert.Single(session.Document.Nodes).Id);
        Assert.Equal(id, session.Selection.PrimaryId);
    }

    [Fact]
    public void UndoThenNewEditInvalidatesRedoBranch()
    {
        var session = SessionWithControl(out var id);
        session.Execute("Rename one", (editor, document) => editor.RenameControl(document, id, "One", null));
        session.Undo();

        session.Execute("Rename two", (editor, document) => editor.RenameControl(document, id, "Two", null));

        Assert.False(session.CanRedo);
        Assert.Equal("Two", session.Document.Nodes.Single().DisplayLabel);
    }

    [Fact]
    public void FailedEditIsAtomicAndCreatesNoHistory()
    {
        var session = SessionWithControl(out var id);
        var before = session.Document;

        var result = session.Execute("Invalid geometry", (editor, document) =>
            editor.UpdateGeometry(document, id, -1, 20, 0, 0));

        Assert.False(result.Success);
        Assert.Same(before, session.Document);
        Assert.Equal(0, session.UndoCount);
    }

    [Fact]
    public void TransactionCoalescesManyDragDeltasIntoOneUndoEntry()
    {
        var session = SessionWithControl(out var id);
        var original = session.Document.Nodes.Single().Anchors[0];
        session.BeginTransaction("Drag control");
        for (var index = 0; index < 100; index++)
            Assert.True(session.Execute("Drag delta", (editor, document) => editor.MoveBy(document, id, 1, -0.5)).Success);

        var commit = session.CommitTransaction();

        Assert.True(commit.Success);
        Assert.Equal(1, session.UndoCount);
        Assert.Equal(original.OffsetX + 100, session.Document.Nodes.Single().Anchors[0].OffsetX);
        session.Undo();
        Assert.Equal(original, session.Document.Nodes.Single().Anchors[0]);
    }

    [Fact]
    public void CancelledTransactionRestoresDocumentWithoutHistoryEntry()
    {
        var session = SessionWithControl(out var id);
        var before = session.Document;
        session.BeginTransaction("Drag control");
        session.Execute("Drag delta", (editor, document) => editor.MoveBy(document, id, 10, 10));

        var cancelled = session.CancelTransaction();

        Assert.True(cancelled.Success);
        Assert.Same(before, session.Document);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void LockIsEnforcedForEveryMutationPathButCanBeUnlocked()
    {
        var session = SessionWithControl(out var id);
        Assert.True(session.Execute("Lock", (editor, document) => editor.SetLocked(document, id, true)).Success);
        var history = session.UndoCount;

        var rename = session.Execute("Rename", (editor, document) => editor.RenameControl(document, id, "No", null));
        var move = session.Execute("Move", (editor, document) => editor.MoveBy(document, id, 1, 1));
        var delete = session.Execute("Delete", (editor, document) => editor.DeleteControl(document, id));

        Assert.All(new[] { rename, move, delete }, result =>
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "FFV2-EDIT-LOCKED"));
        Assert.Equal(history, session.UndoCount);
        Assert.True(session.Execute("Unlock", (editor, document) => editor.SetLocked(document, id, false)).Success);
        Assert.True(session.Execute("Rename", (editor, document) => editor.RenameControl(document, id, "Yes", null)).Success);
    }

    [Fact]
    public void LockedOwnerRejectsCreateReorderDeleteAndReparentChildListChanges()
    {
        var editor = new UiDocumentEditor();
        var document = Document();
        var parent = editor.CreateControl(document, UiNodeKind.Frame,
            OwnerReference.Root(document.CompositionRoots[0].Id), "Parent");
        var parentId = parent.AffectedId!.Value;
        var child = editor.CreateControl(parent.Document, UiNodeKind.Button, OwnerReference.Node(parentId), "Child");
        var childId = child.AffectedId!.Value;
        document = editor.SetLocked(child.Document, parentId, true).Document;

        Assert.Contains(editor.CreateControl(document, UiNodeKind.Frame, OwnerReference.Node(parentId), "No").Diagnostics,
            item => item.Code == "FFV2-EDIT-LOCKED");
        Assert.Contains(editor.DeleteControl(document, childId).Diagnostics, item => item.Code == "FFV2-EDIT-LOCKED");
        Assert.Contains(editor.ReorderChild(document, childId, 0).Diagnostics, item => item.Code == "FFV2-EDIT-LOCKED");
    }

    [Fact]
    public void HistoryIsBoundedAndStoresDocumentsNotTransientLayouts()
    {
        var session = new SemanticEditingSession(Document(), historyLimit: 3);
        for (var index = 0; index < 5; index++)
            session.Execute($"Create {index}", (editor, document) => editor.CreateControl(document,
                UiNodeKind.Frame, OwnerReference.Root(document.CompositionRoots[0].Id), $"Frame {index}"));

        Assert.Equal(3, session.UndoCount);
        session.Undo();
        session.Undo();
        session.Undo();
        Assert.Equal(2, session.Document.Nodes.Count);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void UndoRedoRestoresRenamePropertiesAnchorsReparentingAndIdentity()
    {
        var editor = new UiDocumentEditor();
        var document = Document();
        var first = editor.CreateControl(document, UiNodeKind.Frame,
            OwnerReference.Root(document.CompositionRoots[0].Id), "First");
        var firstId = first.AffectedId!.Value;
        var second = editor.CreateControl(first.Document, UiNodeKind.Frame,
            OwnerReference.Root(document.CompositionRoots[0].Id), "Second");
        var secondId = second.AffectedId!.Value;
        var child = editor.CreateControl(second.Document, UiNodeKind.Button, OwnerReference.Node(firstId), "Child");
        var childId = child.AffectedId!.Value;
        var session = new SemanticEditingSession(child.Document,
            selection: SemanticSelection.Empty.Replace(childId));

        session.Execute("Rename", (gateway, current) => gateway.RenameControl(current, childId, "Renamed", "Runtime"));
        var properties = session.Document.Nodes.Single(node => node.Id == childId).AuthoredProperties;
        session.Execute("Visibility", (gateway, current) => gateway.UpdateProperties(current, childId,
            properties with { Frame = properties.Frame! with { Visible = false } }));
        var anchor = session.Document.Nodes.Single(node => node.Id == childId).Anchors[0];
        session.Execute("Anchor", (gateway, current) => gateway.UpdateAnchors(current, childId,
            [anchor with { OffsetX = 12, OffsetY = -8 }]));
        session.Execute("Reparent", (gateway, current) => gateway.ChangeOwnership(current, childId,
            OwnerReference.Node(secondId), preserveVisualPosition: true));

        Assert.Equal(childId, session.Selection.PrimaryId);
        Assert.Equal(OwnerReference.Node(secondId), session.Document.Nodes.Single(node => node.Id == childId).Owner);
        session.Undo();
        Assert.Equal(OwnerReference.Node(firstId), session.Document.Nodes.Single(node => node.Id == childId).Owner);
        session.Undo();
        Assert.Equal(anchor, session.Document.Nodes.Single(node => node.Id == childId).Anchors[0]);
        session.Undo();
        Assert.True(session.Document.Nodes.Single(node => node.Id == childId).AuthoredProperties.Frame!.Visible);
        session.Undo();
        Assert.Equal("Child", session.Document.Nodes.Single(node => node.Id == childId).DisplayLabel);

        for (var index = 0; index < 4; index++) Assert.True(session.Redo().Success);
        var restored = session.Document.Nodes.Single(node => node.Id == childId);
        Assert.Equal(childId, restored.Id);
        Assert.Equal("Renamed", restored.DisplayLabel);
        Assert.False(restored.AuthoredProperties.Frame!.Visible);
        Assert.Equal(OwnerReference.Node(secondId), restored.Owner);
        Assert.Equal(childId, session.Selection.PrimaryId);
    }

    [Fact]
    public void DeleteUndoRedoRestoresSubtreeOrderTypedReferencesAndSelection()
    {
        var editor = new UiDocumentEditor();
        var document = Document();
        var parent = editor.CreateControl(document, UiNodeKind.Frame,
            OwnerReference.Root(document.CompositionRoots[0].Id), "Parent");
        var parentId = parent.AffectedId!.Value;
        var child = editor.CreateControl(parent.Document, UiNodeKind.Button, OwnerReference.Node(parentId), "Child");
        var childId = child.AffectedId!.Value;
        var session = new SemanticEditingSession(child.Document,
            selection: SemanticSelection.Empty.Replace(childId));

        Assert.True(session.Execute("Delete", (gateway, current) => gateway.DeleteControl(current, parentId)).Success);
        Assert.Empty(session.Document.Nodes);
        Assert.Null(session.Selection.PrimaryId);
        session.Undo();

        Assert.Equal(new[] { parentId }, session.Document.CompositionRoots[0].Children);
        Assert.Equal(new[] { childId }, session.Document.Nodes.Single(node => node.Id == parentId).Children);
        Assert.Equal(AnchorTargetKind.Parent, session.Document.Nodes.Single(node => node.Id == childId).Anchors[0].Target.Kind);
        Assert.Equal(childId, session.Selection.PrimaryId);
        session.Redo();
        Assert.Empty(session.Document.Nodes);
    }

    private static SemanticEditingSession SessionWithControl(out SemanticId id)
    {
        var editor = new UiDocumentEditor();
        var document = Document();
        var created = editor.CreateControl(document, UiNodeKind.Frame,
            OwnerReference.Root(document.CompositionRoots[0].Id), "Frame");
        id = created.AffectedId!.Value;
        return new SemanticEditingSession(created.Document);
    }

    private static UiDocument Document() => UiDocumentFactory.Create("Root", "Host", 1024, 768);
}
