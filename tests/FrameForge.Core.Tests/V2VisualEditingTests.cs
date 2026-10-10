using FrameForge.Core.Export;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Semantics.V2;
using FrameForge.Core.Templates;
using Xunit;

namespace FrameForge.Core.Tests;

public sealed class V2VisualEditingTests
{
    [Fact]
    public void MultiMoveUsesOneDeltaAndDoesNotDoubleMoveSelectedDescendants()
    {
        var (editor, document, ids) = Controls(UiNodeKind.Frame, UiNodeKind.Frame);
        var parent = ids[0];
        var childResult = editor.ChangeOwnership(document, ids[1], OwnerReference.Node(parent), true);
        Assert.True(childResult.Success, childResult.ErrorText);
        document = childResult.Document;
        var childAnchor = document.Nodes.Single(node => node.Id == ids[1]).Anchors[0];
        var before = Layout(document);

        var moved = editor.MoveSelection(document, ids, 17, -9);

        Assert.True(moved.Success, moved.ErrorText);
        var after = Layout(moved.Document);
        Assert.Equal(before[parent].Rect!.Value.Left + 17, after[parent].Rect!.Value.Left);
        Assert.Equal(before[ids[1]].Rect!.Value.Top - 9, after[ids[1]].Rect!.Value.Top);
        Assert.Equal(childAnchor, moved.Document.Nodes.Single(node => node.Id == ids[1]).Anchors[0]);
    }

    [Fact]
    public void MultiMoveRejectsLockedOrMultiAnchorSelectionsAtomically()
    {
        var (editor, document, ids) = Controls(UiNodeKind.Frame, UiNodeKind.Frame);
        document = editor.SetLocked(document, ids[1], true).Document;
        var locked = editor.MoveSelection(document, ids, 10, 10);
        Assert.False(locked.Success);
        Assert.Same(document, locked.Document);

        document = editor.SetLocked(document, ids[1], false).Document;
        var node = document.Nodes.Single(item => item.Id == ids[1]);
        document = document with { Nodes = [.. document.Nodes.Select(item => item.Id == node.Id ? item with
        {
            Anchors = [.. item.Anchors, item.Anchors[0] with { Point = AnchorPoint.LEFT }],
        } : item)] };
        var multiple = editor.MoveSelection(document, ids, 10, 10);
        Assert.False(multiple.Success);
        Assert.Same(document, multiple.Document);
    }

    [Theory]
    [InlineData(V2ResizeHandle.Left, -10, 0, 110, 50)]
    [InlineData(V2ResizeHandle.Right, 10, 0, 110, 50)]
    [InlineData(V2ResizeHandle.Top, 0, 10, 100, 60)]
    [InlineData(V2ResizeHandle.Bottom, 0, -10, 100, 60)]
    [InlineData(V2ResizeHandle.TopLeft, -10, 10, 110, 60)]
    [InlineData(V2ResizeHandle.TopRight, 10, 10, 110, 60)]
    [InlineData(V2ResizeHandle.BottomLeft, -10, -10, 110, 60)]
    [InlineData(V2ResizeHandle.BottomRight, 10, -10, 110, 60)]
    public void AllResizeHandlesAuthorExpectedNativeDimensions(V2ResizeHandle handle, double dx, double dy,
        double width, double height)
    {
        var (editor, document, ids) = Controls(UiNodeKind.Frame);
        var result = editor.ResizeBy(document, ids[0], handle, dx, dy);

        Assert.True(result.Success, result.ErrorText);
        var properties = result.Document.Nodes.Single().AuthoredProperties.Frame!;
        Assert.Equal(width, properties.Width);
        Assert.Equal(height, properties.Height);
    }

    [Fact]
    public void ResizeAuthorsOverridesForTemplateDerivedDimensionsAndUndoRestoresInheritance()
    {
        var registry = BlizzardTemplateParser.Parse(
        [
            new BlizzardTemplateXmlSource("Synthetic.xml", """
                <Ui><Button name="UIPanelButtonTemplate" virtual="true"><Size><AbsDimension x="188" y="42"/></Size></Button></Ui>
                """),
        ]);
        var editor = new UiDocumentEditor(registry);
        var document = UiDocumentFactory.Create("Root", "Host", 1024, 768);
        var created = editor.CreateControl(document, UiNodeKind.Button, OwnerReference.Root(document.CompositionRoots[0].Id), "Button");
        var id = created.AffectedId!.Value;
        document = editor.AssignBlizzardTemplate(created.Document, id, "UIPanelButtonTemplate", true).Document;
        var session = new SemanticEditingSession(document, registry, SemanticSelection.Empty.Replace(id));

        var resized = session.Execute("Resize", (gateway, current) => gateway.ResizeBy(current, id, V2ResizeHandle.Right, 12, 0));

        Assert.True(resized.Success);
        Assert.Equal(200, session.Document.Nodes.Single().AuthoredProperties.Frame!.Width);
        Assert.True(session.Undo().Success);
        Assert.Null(session.Document.Nodes.Single().AuthoredProperties.Frame!.Width);
    }

    [Fact]
    public void ResizeRejectsLocksAndMultiAnchorConstraintsWithoutPartialChanges()
    {
        var (editor, document, ids) = Controls(UiNodeKind.Texture);
        document = editor.SetLocked(document, ids[0], true).Document;
        var locked = editor.ResizeBy(document, ids[0], V2ResizeHandle.Right, 10, 0);
        Assert.False(locked.Success);
        Assert.Same(document, locked.Document);

        document = editor.SetLocked(document, ids[0], false).Document;
        var node = document.Nodes.Single();
        document = document with { Nodes = [node with
        {
            Anchors = [.. node.Anchors, node.Anchors[0] with { Point = AnchorPoint.LEFT }],
        }] };
        var constrained = editor.ResizeBy(document, ids[0], V2ResizeHandle.Right, 10, 0);
        Assert.False(constrained.Success);
        Assert.Same(document, constrained.Document);
    }

    [Theory]
    [InlineData(SelectionArrangeCommand.AlignLeft)]
    [InlineData(SelectionArrangeCommand.AlignCenterHorizontal)]
    [InlineData(SelectionArrangeCommand.AlignRight)]
    [InlineData(SelectionArrangeCommand.AlignTop)]
    [InlineData(SelectionArrangeCommand.AlignCenterVertical)]
    [InlineData(SelectionArrangeCommand.AlignBottom)]
    public void AlignmentUsesPrimaryAsReferenceAndPreservesItsRectangle(SelectionArrangeCommand command)
    {
        var (editor, document, ids) = Controls(UiNodeKind.Frame, UiNodeKind.Frame);
        document = editor.MoveBy(document, ids[0], -120, 80).Document;
        document = editor.UpdateGeometry(document, ids[1], 60, 30, 130, -70).Document;
        var before = Layout(document)[ids[1]].Rect;

        var result = editor.ArrangeSelection(document, ids, ids[1], command);

        Assert.True(result.Success, result.ErrorText);
        var layout = Layout(result.Document);
        Assert.Equal(before, layout[ids[1]].Rect);
        var first = layout[ids[0]].Rect!.Value;
        var primary = layout[ids[1]].Rect!.Value;
        Assert.True(command switch
        {
            SelectionArrangeCommand.AlignLeft => first.Left == primary.Left,
            SelectionArrangeCommand.AlignCenterHorizontal => first.CenterX == primary.CenterX,
            SelectionArrangeCommand.AlignRight => first.Right == primary.Right,
            SelectionArrangeCommand.AlignTop => first.Top == primary.Top,
            SelectionArrangeCommand.AlignCenterVertical => first.CenterY == primary.CenterY,
            SelectionArrangeCommand.AlignBottom => first.Bottom == primary.Bottom,
            _ => false,
        });
    }

    [Theory]
    [InlineData(SelectionArrangeCommand.DistributeHorizontal)]
    [InlineData(SelectionArrangeCommand.DistributeVertical)]
    public void DistributionPreservesOuterEdgesAndEqualizesGaps(SelectionArrangeCommand command)
    {
        var (editor, document, ids) = Controls(UiNodeKind.Frame, UiNodeKind.Frame, UiNodeKind.Frame);
        document = editor.UpdateGeometry(document, ids[0], 20, 20, -200, 150).Document;
        document = editor.UpdateGeometry(document, ids[1], 40, 30, -20, 30).Document;
        document = editor.UpdateGeometry(document, ids[2], 60, 40, 220, -170).Document;
        var before = Layout(document);

        var result = editor.ArrangeSelection(document, ids, ids[1], command);

        Assert.True(result.Success, result.ErrorText);
        var after = Layout(result.Document);
        if (command == SelectionArrangeCommand.DistributeHorizontal)
        {
            Assert.Equal(before[ids[0]].Rect!.Value.Left, after[ids[0]].Rect!.Value.Left);
            Assert.Equal(before[ids[2]].Rect!.Value.Right, after[ids[2]].Rect!.Value.Right);
            Assert.Equal(after[ids[1]].Rect!.Value.Left - after[ids[0]].Rect!.Value.Right,
                after[ids[2]].Rect!.Value.Left - after[ids[1]].Rect!.Value.Right, 8);
        }
        else
        {
            Assert.Equal(before[ids[0]].Rect!.Value.Top, after[ids[0]].Rect!.Value.Top);
            Assert.Equal(before[ids[2]].Rect!.Value.Bottom, after[ids[2]].Rect!.Value.Bottom);
            Assert.Equal(after[ids[0]].Rect!.Value.Bottom - after[ids[1]].Rect!.Value.Top,
                after[ids[1]].Rect!.Value.Bottom - after[ids[2]].Rect!.Value.Top, 8);
        }
    }

    [Fact]
    public void ReorderingIsRestrictedToSameNativePaintBand()
    {
        var (editor, document, ids) = Controls(UiNodeKind.Frame, UiNodeKind.Frame, UiNodeKind.Texture);
        var moved = editor.ReorderControl(document, ids[0], int.MaxValue);
        Assert.True(moved.Success, moved.ErrorText);
        Assert.Equal([ids[1], ids[0], ids[2]], moved.Document.CompositionRoots[0].Children);

        var region = editor.ReorderControl(moved.Document, ids[2], -1);
        Assert.False(region.Success);
        Assert.Contains(region.Diagnostics, item => item.Code == "FFV2-EDIT-ORDER-BAND");
    }

    [Fact]
    public void NativeOrderingIsUndoableWithoutChangingPaintProperties()
    {
        var (_, document, ids) = Controls(UiNodeKind.Frame, UiNodeKind.Frame, UiNodeKind.Frame);
        var session = new SemanticEditingSession(document, selection: SemanticSelection.Empty.Replace(ids[0]));
        var properties = document.Nodes[0].AuthoredProperties.Frame;

        Assert.True(session.Execute("Front", (editor, current) => editor.ReorderControl(current, ids[0], int.MaxValue)).Success);
        Assert.Equal(ids[0], session.Document.CompositionRoots[0].Children[^1]);
        Assert.Equal(properties, session.Document.Nodes.Single(node => node.Id == ids[0]).AuthoredProperties.Frame);
        Assert.True(session.Undo().Success);
        Assert.Equal(ids, session.Document.CompositionRoots[0].Children);
    }

    [Fact]
    public void EditorGroupsRoundTripLockMembersAndNeverChangeExportXml()
    {
        var (editor, document, ids) = Controls(UiNodeKind.Frame, UiNodeKind.Button);
        var xml = V2FrameXmlExporter.Build(document, null).Xml;
        var created = editor.CreateGroup(document, "Panel tools", ids);
        Assert.True(created.Success, created.ErrorText);
        var groupId = created.AffectedId!.Value;
        document = editor.UpdateGroup(created.Document, groupId, locked: true).Document;

        Assert.False(editor.MoveSelection(document, ids, 5, 0).Success);
        Assert.Equal(xml, V2FrameXmlExporter.Build(document, null).Xml);
        var parsed = UiDocumentCodec.Parse(UiDocumentCodec.Serialize(document));
        Assert.NotNull(parsed.Document);
        Assert.Equal(ids, parsed.Document!.Editor!.Groups.Single().Members);
        Assert.True(parsed.Document.Editor.Groups.Single().Locked);
        var deleted = editor.DeleteGroup(parsed.Document, groupId);
        Assert.True(deleted.Success);
        Assert.Equal(2, deleted.Document.Nodes.Count);
    }

    [Fact]
    public void HundredMultiSelectionDeltasCommitAsOneUndoEntryAndCancelRestoresIdentity()
    {
        var (editor, document, ids) = Controls(UiNodeKind.Frame, UiNodeKind.Frame);
        var session = new SemanticEditingSession(document,
            selection: new SemanticSelection { OrderedIds = ids });
        session.BeginTransaction("Move selection");
        for (var index = 0; index < 100; index++)
            Assert.True(session.Execute("Delta", (gateway, current) => gateway.MoveSelection(current, ids, 1, -1)).Success);
        Assert.True(session.CommitTransaction().Success);
        Assert.Equal(1, session.UndoCount);
        Assert.True(session.Undo().Success);
        Assert.Equal(document, session.Document);

        session.BeginTransaction("Cancelled move");
        Assert.True(session.Execute("Delta", (gateway, current) => gateway.MoveSelection(current, ids, 5, 5)).Success);
        Assert.True(session.CancelTransaction().Success);
        Assert.Equal(document, session.Document);
    }

    private static (UiDocumentEditor Editor, UiDocument Document, SemanticId[] Ids) Controls(params UiNodeKind[] kinds)
    {
        var editor = new UiDocumentEditor();
        var document = UiDocumentFactory.Create("Root", "Host", 1024, 768);
        var ids = new List<SemanticId>();
        foreach (var kind in kinds)
        {
            var created = editor.CreateControl(document, kind, OwnerReference.Root(document.CompositionRoots[0].Id), kind.ToString());
            Assert.True(created.Success, created.ErrorText);
            document = created.Document;
            var id = created.AffectedId!.Value;
            ids.Add(id);
            var sized = editor.UpdateGeometry(document, id, 100, 50, 0, 0);
            Assert.True(sized.Success, sized.ErrorText);
            document = sized.Document;
        }
        return (editor, document, [.. ids]);
    }

    private static IReadOnlyDictionary<SemanticId, ResolvedUiElement> Layout(UiDocument document)
    {
        var root = document.CompositionRoots.Single();
        return UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root)).Elements;
    }
}
