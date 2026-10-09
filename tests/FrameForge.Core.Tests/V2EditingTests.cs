using FrameForge.Core.Models;
using FrameForge.Core.Semantics.V2;
using Xunit;

namespace FrameForge.Core.Tests;

public class V2EditingTests
{
    private readonly UiDocumentEditor _editor = new();

    [Fact]
    public void NewProjectHasOneExplicitEmptyRootAndSupportedTarget()
    {
        var document = NewDocument();

        var root = Assert.Single(document.CompositionRoots);
        Assert.Empty(root.Children);
        Assert.Equal(WowTargetProfile.Wow335a12340, document.Target);
        Assert.Empty(UiDocumentValidator.Validate(document));
    }

    [Theory]
    [InlineData(UiNodeKind.Frame)]
    [InlineData(UiNodeKind.Texture)]
    [InlineData(UiNodeKind.FontString)]
    [InlineData(UiNodeKind.Button)]
    [InlineData(UiNodeKind.StatusBar)]
    public void CreatesEverySupportedKindOwnedByRootWithParentAnchor(UiNodeKind kind)
    {
        var document = NewDocument();
        var root = document.CompositionRoots[0];

        var result = _editor.CreateControl(document, kind, OwnerReference.Root(root.Id), kind.ToString());

        Assert.True(result.Success, result.ErrorText);
        var node = Assert.Single(result.Document.Nodes);
        Assert.Equal(OwnerReference.Root(root.Id), node.Owner);
        Assert.Equal(AnchorTargetKind.Parent, Assert.Single(node.Anchors).Target.Kind);
        Assert.Equal(node.Id, Assert.Single(result.Document.CompositionRoots[0].Children));
    }

    [Fact]
    public void HierarchyCreationAndDeletionAreAtomic()
    {
        var document = NewDocument();
        var root = document.CompositionRoots[0];
        var parentResult = _editor.CreateControl(document, UiNodeKind.Frame, OwnerReference.Root(root.Id), "Parent");
        var parentId = parentResult.AffectedId!.Value;
        var childResult = _editor.CreateControl(parentResult.Document, UiNodeKind.Texture, OwnerReference.Node(parentId), "Child");

        var delete = _editor.DeleteControl(childResult.Document, parentId);

        Assert.True(delete.Success, delete.ErrorText);
        Assert.Empty(delete.Document.Nodes);
        Assert.Empty(delete.Document.CompositionRoots[0].Children);
        Assert.Empty(UiDocumentValidator.Validate(delete.Document));
    }

    [Fact]
    public void ReparentingCanPreserveAbsoluteVisualPositionWithoutGlobalAnchor()
    {
        var document = NewDocument();
        var root = document.CompositionRoots[0];
        var left = Add(document, UiNodeKind.Frame, OwnerReference.Root(root.Id), "Left");
        var leftId = left.AffectedId!.Value;
        var leftPositioned = _editor.UpdateGeometry(left.Document, leftId, 200, 200, -200, 0);
        var right = Add(leftPositioned.Document, UiNodeKind.Frame, OwnerReference.Root(root.Id), "Right");
        var rightId = right.AffectedId!.Value;
        var rightPositioned = _editor.UpdateGeometry(right.Document, rightId, 200, 200, 200, 0);
        var child = Add(rightPositioned.Document, UiNodeKind.Button, OwnerReference.Node(leftId), "Child");
        var childId = child.AffectedId!.Value;
        var childPositioned = _editor.UpdateGeometry(child.Document, childId, 80, 30, 25, -10);
        var before = UiDocumentProjection.Resolve(childPositioned.Document).Frames[childId.Value].Rect;

        var moved = _editor.ChangeOwnership(childPositioned.Document, childId, OwnerReference.Node(rightId), preserveVisualPosition: true);
        var after = UiDocumentProjection.Resolve(moved.Document).Frames[childId.Value].Rect;

        Assert.True(moved.Success, moved.ErrorText);
        Assert.Equal(before, after);
        var movedNode = moved.Document.Nodes.Single(node => node.Id == childId);
        Assert.Equal(OwnerReference.Node(rightId), movedNode.Owner);
        Assert.Equal(AnchorTargetKind.Parent, movedNode.Anchors[0].Target.Kind);
    }

    [Fact]
    public void ReordersSiblingsWithoutChangingIdentity()
    {
        var document = NewDocument();
        var rootId = document.CompositionRoots[0].Id;
        var first = Add(document, UiNodeKind.Frame, OwnerReference.Root(rootId), "First");
        var second = Add(first.Document, UiNodeKind.Frame, OwnerReference.Root(rootId), "Second");

        var reordered = _editor.ReorderChild(second.Document, second.AffectedId!.Value, 0);

        Assert.True(reordered.Success, reordered.ErrorText);
        Assert.Equal([second.AffectedId.Value, first.AffectedId!.Value], reordered.Document.CompositionRoots[0].Children);
        Assert.Equal(first.AffectedId, reordered.Document.Nodes.Single(node => node.DisplayLabel == "First").Id);
    }

    [Fact]
    public void RenameMoveAndResizePreserveStableIdentityAndTypedAnchor()
    {
        var document = NewDocument();
        var created = Add(document, UiNodeKind.Texture, OwnerReference.Root(document.CompositionRoots[0].Id), "Texture");
        var id = created.AffectedId!.Value;
        var renamed = _editor.RenameControl(created.Document, id, "Backdrop", "ExampleBackdrop");
        var moved = _editor.MoveBy(renamed.Document, id, 14, -9);
        var resized = _editor.UpdateGeometry(moved.Document, id, 320, 180, 14, -9);

        Assert.True(resized.Success, resized.ErrorText);
        var node = Assert.Single(resized.Document.Nodes);
        Assert.Equal(id, node.Id);
        Assert.Equal("Backdrop", node.DisplayLabel);
        Assert.Equal("ExampleBackdrop", node.RuntimeName);
        Assert.Equal((320d, 180d), (node.AuthoredProperties.Region!.Width, node.AuthoredProperties.Region.Height));
        Assert.Equal((14d, -9d), (node.Anchors[0].OffsetX, node.Anchors[0].OffsetY));
        Assert.Equal(AnchorTargetKind.Parent, node.Anchors[0].Target.Kind);
    }

    [Fact]
    public void UpdatesSupportedVisualPropertiesWithoutChangingGeometry()
    {
        var document = NewDocument();
        var created = Add(document, UiNodeKind.FontString, OwnerReference.Root(document.CompositionRoots[0].Id), "Label");
        var id = created.AffectedId!.Value;
        var node = created.Document.Nodes[0];
        var properties = node.AuthoredProperties with
        {
            FontString = new FontStringProperties { Text = "Quest progress", FontReference = "GameFontHighlight" },
            Region = node.AuthoredProperties.Region! with { Tint = new UiColor(1, 0.82, 0) },
        };

        var result = _editor.UpdateProperties(created.Document, id, properties);

        Assert.True(result.Success, result.ErrorText);
        Assert.Equal("Quest progress", result.Document.Nodes[0].AuthoredProperties.FontString!.Text);
        Assert.Equal(AnchorTargetKind.Parent, result.Document.Nodes[0].Anchors[0].Target.Kind);
    }

    [Fact]
    public void SaveAndReopenPreserveEditedGraph()
    {
        var document = NewDocument();
        var created = Add(document, UiNodeKind.StatusBar, OwnerReference.Root(document.CompositionRoots[0].Id), "Progress");
        var positioned = _editor.UpdateGeometry(created.Document, created.AffectedId!.Value, 240, 18, 33, -44);

        var text = UiDocumentCodec.Serialize(positioned.Document);
        var parsed = UiDocumentCodec.Parse(text);

        Assert.True(parsed.Ok, parsed.ErrorText);
        Assert.Equal(text, UiDocumentCodec.Serialize(parsed.Document!));
        Assert.Equal(created.AffectedId, parsed.Document!.Nodes[0].Id);
    }

    [Fact]
    public void InvalidOperationsReturnOriginalDocumentAndActionableDiagnostic()
    {
        var document = NewDocument();
        var created = Add(document, UiNodeKind.Texture, OwnerReference.Root(document.CompositionRoots[0].Id), "Region");
        var frame = Add(created.Document, UiNodeKind.Frame, OwnerReference.Root(document.CompositionRoots[0].Id), "Frame");

        var result = _editor.ChangeOwnership(frame.Document, frame.AffectedId!.Value,
            OwnerReference.Node(created.AffectedId!.Value), preserveVisualPosition: false);

        Assert.False(result.Success);
        Assert.Same(frame.Document, result.Document);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "FFV2-EDIT-OWNER" && diagnostic.Message.Contains("region", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeHuntsRegressionFortyNineEditsNeverAcquireImplicitGlobalAnchor()
    {
        var document = NewDocument();
        var rootId = document.CompositionRoots[0].Id;
        var ids = new List<SemanticId>();
        for (var index = 0; index < 49; index++)
        {
            var created = Add(document, UiNodeKind.Frame, OwnerReference.Root(rootId), $"Control {index + 1}");
            document = created.Document;
            var id = created.AffectedId!.Value;
            ids.Add(id);
            document = _editor.UpdateGeometry(document, id, 100 + index, 30, index * 3, -(index * 2)).Document;
            document = _editor.MoveBy(document, id, 1, -1).Document;
        }

        Assert.Equal(49, document.Nodes.Count);
        Assert.All(document.Nodes, node =>
        {
            Assert.Equal(OwnerReference.Root(rootId), node.Owner);
            Assert.Equal(AnchorTargetKind.Parent, Assert.Single(node.Anchors).Target.Kind);
            Assert.Null(node.Anchors[0].Target.GlobalName);
        });
        var serialized = UiDocumentCodec.Serialize(document);
        Assert.DoesNotContain("UIParent", serialized, StringComparison.Ordinal);
        var reopened = UiDocumentCodec.Parse(serialized);
        Assert.True(reopened.Ok, reopened.ErrorText);
        Assert.All(reopened.Document!.Nodes, node => Assert.Equal(AnchorTargetKind.Parent, node.Anchors[0].Target.Kind));
        Assert.Empty(UiDocumentValidator.Validate(reopened.Document));
    }

    private SemanticEditResult Add(UiDocument document, UiNodeKind kind, OwnerReference owner, string label)
    {
        var result = _editor.CreateControl(document, kind, owner, label);
        Assert.True(result.Success, result.ErrorText);
        return result;
    }

    private static UiDocument NewDocument()
    {
        var document = UiDocumentFactory.Create("TestDesignRoot", "TestModuleHost", 1024, 768);
        return document with
        {
            Editor = new DocumentEditorMetadata
            {
                Values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [UiDocumentProjection.ProjectNameMetadataKey] = "Test Project",
                },
            },
        };
    }
}
