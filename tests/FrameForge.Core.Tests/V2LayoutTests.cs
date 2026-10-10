using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Semantics.V2;
using FrameForge.Core.Viewing;
using FrameForge.Core.Export;
using FrameForge.Core.Templates;
using Xunit;

namespace FrameForge.Core.Tests;

public class V2LayoutTests
{
    [Fact]
    public void FillHostRootUsesExplicitPreviewHostRatherThanUiParentFallback()
    {
        var document = UiDocumentFactory.Create("Root", "ModuleHost", 1024, 768);
        var layout = UiLayoutResolver.Resolve(document, new UiPreviewHost
        {
            GlobalName = "ModuleHost", Width = 1600, Height = 900,
        });

        Assert.Equal(FrameRect.FromEdges(-800, 450, 800, -450), layout.Elements[document.CompositionRoots[0].Id].Rect);
        Assert.DoesNotContain(layout.Diagnostics, item => item.Code == "FFV2L-HOST-NAME");
    }

    [Fact]
    public void ExplicitRootAnchorResolvesAgainstDeclaredHost()
    {
        var document = UiDocumentFactory.Create("Root", "Host", 1024, 768);
        var root = document.CompositionRoots[0] with
        {
            Sizing = RootSizing.Explicit(400, 300, new RootAnchor
            {
                Point = AnchorPoint.TOPLEFT, RelativePoint = AnchorPoint.TOPLEFT, OffsetX = 25, OffsetY = -30,
            }),
        };
        document = document with { CompositionRoots = [root] };

        var rect = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root)).Elements[root.Id].Rect;

        Assert.Equal(FrameRect.FromSize(-487, 354, 400, 300), rect);
    }

    [Fact]
    public void NestedOwnershipAndParentAnchorsResolveWithoutV1Projection()
    {
        var editor = new UiDocumentEditor();
        var document = UiDocumentFactory.Create("Root", "Host", 1000, 800);
        var rootId = document.CompositionRoots[0].Id;
        var parent = editor.CreateControl(document, UiNodeKind.Frame, OwnerReference.Root(rootId), "Parent");
        var parentId = parent.AffectedId!.Value;
        document = editor.UpdateGeometry(parent.Document, parentId, 200, 100, 120, 60).Document;
        var child = editor.CreateControl(document, UiNodeKind.Texture, OwnerReference.Node(parentId), "Child");
        var childId = child.AffectedId!.Value;
        document = editor.UpdateGeometry(child.Document, childId, 40, 20, -25, 10).Document;

        var layout = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(document.CompositionRoots[0]));

        Assert.Equal(new ModelPoint(120, 60), layout.Elements[parentId].Rect!.Value.Center);
        Assert.Equal(new ModelPoint(95, 70), layout.Elements[childId].Rect!.Value.Center);
        Assert.Equal(parentId, Assert.Single(layout.Elements[childId].Anchors).ResolvedTargetId);
    }

    [Fact]
    public void MultipleAnchorsCanDeriveMissingWidthAndAreAllPreserved()
    {
        var document = UiDocumentFactory.Create("Root", "Host", 1000, 800);
        var root = document.CompositionRoots[0];
        var id = SemanticId.New();
        var node = new UiNode
        {
            Id = id, Kind = UiNodeKind.Frame, DisplayLabel = "Stretch", Owner = OwnerReference.Root(root.Id),
            AuthoredProperties = new AuthoredProperties { Frame = new FrameProperties { Height = 40 } },
            Anchors =
            [
                new UiAnchor { Point = AnchorPoint.LEFT, RelativePoint = AnchorPoint.LEFT, Target = AnchorTarget.Parent() },
                new UiAnchor { Point = AnchorPoint.RIGHT, RelativePoint = AnchorPoint.RIGHT, Target = AnchorTarget.Parent() },
            ],
        };
        document = document with
        {
            Nodes = [node], CompositionRoots = [root with { Children = [id] }],
        };

        var layout = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root));

        Assert.Equal(1000, layout.Elements[id].Rect!.Value.Width);
        Assert.Equal(2, layout.Elements[id].Anchors.Count);
        Assert.All(layout.Elements[id].Anchors, anchor => Assert.True(anchor.Resolved, anchor.Diagnostic));
    }

    [Fact]
    public void UnresolvedExternalGlobalFailsClosedWithActionableDiagnostic()
    {
        var document = OneNode(AnchorTarget.External("SomeOtherAddonHost"));
        var root = document.CompositionRoots[0];

        var layout = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root));

        var element = layout.Elements[document.Nodes[0].Id];
        Assert.Null(element.Rect);
        Assert.Contains(element.Diagnostics, item => item.Code == "FFV2L-ANCHOR-TARGET" && item.Message.Contains("SomeOtherAddonHost"));
    }

    [Fact]
    public void SiblingAndExplicitHostAnchorsResolveToTheirTypedTargets()
    {
        var editor = new UiDocumentEditor();
        var document = UiDocumentFactory.Create("Root", "Host", 1000, 800);
        var root = document.CompositionRoots[0];
        var sibling = editor.CreateControl(document, UiNodeKind.Frame, OwnerReference.Root(root.Id), "Sibling");
        var siblingId = sibling.AffectedId!.Value;
        document = editor.UpdateGeometry(sibling.Document, siblingId, 100, 50, -200, 0).Document;
        var anchored = editor.CreateControl(document, UiNodeKind.Frame, OwnerReference.Root(root.Id), "Anchored");
        var anchoredId = anchored.AffectedId!.Value;
        var anchoredNode = anchored.Document.Nodes.Single(node => node.Id == anchoredId) with
        {
            Anchors =
            [
                new UiAnchor { Point = AnchorPoint.LEFT, RelativePoint = AnchorPoint.RIGHT,
                    Target = AnchorTarget.Local(siblingId), OffsetX = 15 },
            ],
        };
        document = anchored.Document with
        {
            Nodes = [.. anchored.Document.Nodes.Select(node => node.Id == anchoredId ? anchoredNode : node)],
        };
        var hostNode = new UiNode
        {
            Id = SemanticId.New(), Kind = UiNodeKind.Frame, DisplayLabel = "Host anchored", Owner = OwnerReference.Root(root.Id),
            Anchors = [new UiAnchor { Point = AnchorPoint.TOP, RelativePoint = AnchorPoint.TOP, Target = AnchorTarget.External("Host"), OffsetY = -10 }],
            AuthoredProperties = new AuthoredProperties { Frame = new FrameProperties { Width = 80, Height = 20 } },
        };
        document = document with
        {
            Nodes = [.. document.Nodes, hostNode],
            CompositionRoots = [document.CompositionRoots[0] with { Children = [.. document.CompositionRoots[0].Children, hostNode.Id] }],
        };

        var layout = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root));

        Assert.Equal(layout.Elements[siblingId].Rect!.Value.Right + 15, layout.Elements[anchoredId].Rect!.Value.Left);
        Assert.Equal(siblingId, layout.Elements[anchoredId].Anchors[0].ResolvedTargetId);
        Assert.Equal(390, layout.Elements[hostNode.Id].Rect!.Value.Top);
        Assert.Null(layout.Elements[hostNode.Id].Anchors[0].ResolvedTargetId);
    }

    [Fact]
    public void EffectiveTemplateDimensionsRemainSeparateFromAuthoredDimensions()
    {
        var registry = BlizzardTemplateParser.Parse(
        [
            new BlizzardTemplateXmlSource("Synthetic.xml", """
                <Ui>
                  <Button name="UIPanelButtonTemplate" virtual="true"><Size><AbsDimension x="188" y="42"/></Size></Button>
                  <Button name="GameMenuButtonTemplate" virtual="true"><Size><AbsDimension x="120" y="24"/></Size></Button>
                  <Button name="CharacterFrameTabButtonTemplate" virtual="true"><Size><AbsDimension x="64" y="32"/></Size></Button>
                </Ui>
                """),
        ]);
        var editor = new UiDocumentEditor(registry);
        var document = UiDocumentFactory.Create("Root", "Host", 1000, 800);
        var root = document.CompositionRoots[0];
        var created = editor.CreateControl(document, UiNodeKind.Button, OwnerReference.Root(root.Id), "Button");
        var id = created.AffectedId!.Value;
        var assigned = editor.AssignBlizzardTemplate(created.Document, id, "UIPanelButtonTemplate", clearEligibleOverrides: true);

        var element = UiLayoutResolver.Resolve(assigned.Document, UiPreviewHost.FromDesignRoot(root), registry).Elements[id];

        Assert.Null(element.AuthoredProperties!.Frame!.Width);
        Assert.Equal(188, element.EffectiveProperties!.Values.Frame!.Width);
        Assert.Equal(188, element.Rect!.Value.Width);
        Assert.Equal(EffectivePropertyOrigin.TemplateDeclared, element.EffectiveProperties.Provenance["frame.width"]);
    }

    [Fact]
    public void HiddenOwnerMakesDescendantEffectivelyHiddenWithoutChangingAuthoredState()
    {
        var editor = new UiDocumentEditor();
        var document = UiDocumentFactory.Create("Root", "Host", 1000, 800);
        var root = document.CompositionRoots[0];
        var parent = editor.CreateControl(document, UiNodeKind.Frame, OwnerReference.Root(root.Id), "Parent");
        var parentId = parent.AffectedId!.Value;
        document = editor.UpdateProperties(parent.Document, parentId,
            parent.Document.Nodes.Single().AuthoredProperties with
            { Frame = parent.Document.Nodes.Single().AuthoredProperties.Frame! with { Visible = false } }).Document;
        var child = editor.CreateControl(document, UiNodeKind.FontString, OwnerReference.Node(parentId), "Child");
        var layout = UiLayoutResolver.Resolve(child.Document, UiPreviewHost.FromDesignRoot(root));

        Assert.True(layout.Elements[child.AffectedId!.Value].OwnVisible);
        Assert.False(layout.Elements[child.AffectedId.Value].EffectiveVisible);
    }

    [Fact]
    public void PaintOrderUsesRegionLayerSublevelAndAuthoredOrder()
    {
        var document = UiDocumentFactory.Create("Root", "Host", 1000, 800);
        var root = document.CompositionRoots[0];
        UiNode Region(string label, RegionDrawLayer layer, int sublevel) => new()
        {
            Id = SemanticId.New(), Kind = UiNodeKind.Texture, DisplayLabel = label, Owner = OwnerReference.Root(root.Id),
            Anchors = [new UiAnchor { Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER, Target = AnchorTarget.Parent() }],
            AuthoredProperties = new AuthoredProperties
            {
                Region = new RegionProperties { Width = 20, Height = 20, DrawLayer = layer, Sublevel = sublevel },
                Texture = new TextureProperties(),
            },
        };
        var overlay = Region("Overlay", RegionDrawLayer.Overlay, 0);
        var backgroundHigh = Region("Background high", RegionDrawLayer.Background, 2);
        var backgroundLow = Region("Background low", RegionDrawLayer.Background, -1);
        document = document with
        {
            Nodes = [overlay, backgroundHigh, backgroundLow],
            CompositionRoots = [root with { Children = [overlay.Id, backgroundHigh.Id, backgroundLow.Id] }],
        };

        var order = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root)).PaintOrder;

        Assert.Equal([root.Id, backgroundLow.Id, backgroundHigh.Id, overlay.Id], order);
    }

    [Fact]
    public void PaintOrderUsesFrameStrataLevelAndAuthoredOrder()
    {
        var document = UiDocumentFactory.Create("Root", "Host", 1000, 800);
        var root = document.CompositionRoots[0];
        UiNode Frame(string label, FrameStrata strata, int level) => new()
        {
            Id = SemanticId.New(), Kind = UiNodeKind.Frame, DisplayLabel = label, Owner = OwnerReference.Root(root.Id),
            Anchors = [new UiAnchor { Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER, Target = AnchorTarget.Parent() }],
            AuthoredProperties = new AuthoredProperties { Frame = new FrameProperties { Width = 20, Height = 20, Strata = strata, Level = level } },
        };
        var high = Frame("High", FrameStrata.High, 0);
        var lowTwo = Frame("Low 2", FrameStrata.Low, 2);
        var lowOne = Frame("Low 1", FrameStrata.Low, 1);
        document = document with { Nodes = [high, lowTwo, lowOne], CompositionRoots = [root with { Children = [high.Id, lowTwo.Id, lowOne.Id] }] };

        Assert.Equal([root.Id, lowOne.Id, lowTwo.Id, high.Id],
            UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root)).PaintOrder);
    }

    [Fact]
    public void NativeHitTestingUsesNativePaintOrderFrontToBack()
    {
        var editor = new UiDocumentEditor();
        var document = UiDocumentFactory.Create("Root", "Host", 1000, 800);
        var root = document.CompositionRoots[0];
        var first = editor.CreateControl(document, UiNodeKind.Frame, OwnerReference.Root(root.Id), "Back");
        var second = editor.CreateControl(first.Document, UiNodeKind.Button, OwnerReference.Root(root.Id), "Front");
        var layout = UiLayoutResolver.Resolve(second.Document, UiPreviewHost.FromDesignRoot(root));

        var hits = UiLayoutHitTester.CandidatesAt(layout, Viewport.Identity, new CanvasOrigin(0, 0),
            VisibilityFilter.ALL, 0, 0);

        Assert.Equal(second.AffectedId, hits[0]);
        Assert.Contains(first.AffectedId!.Value, hits);
    }

    [Fact]
    public void ExportConformanceChecksOwnershipAnchorsOrderingAndDimensions()
    {
        var editor = new UiDocumentEditor();
        var document = UiDocumentFactory.Create("Root", "Host", 1000, 800);
        var root = document.CompositionRoots[0];
        var created = editor.CreateControl(document, UiNodeKind.Button, OwnerReference.Root(root.Id), "Button");
        document = editor.UpdateGeometry(created.Document, created.AffectedId!.Value, 140, 32, 8, -12).Document;
        var layout = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root));
        var plan = V2FrameXmlExporter.Build(document, null);

        Assert.True(plan.IsValid, string.Join(" ", plan.Diagnostics.Select(item => item.Message)));
        Assert.Empty(V2LayoutExportConformance.Validate(layout, plan));

        var other = UiDocumentFactory.Create("OtherRoot", "Host", 1000, 800);
        var mismatch = UiLayoutResolver.Resolve(other, UiPreviewHost.FromDesignRoot(other.CompositionRoots[0]));
        Assert.Contains(V2LayoutExportConformance.Validate(mismatch, plan), item => item.Code == "FFV2C-DOCUMENT");
    }

    private static UiDocument OneNode(AnchorTarget target)
    {
        var document = UiDocumentFactory.Create("Root", "Host", 1000, 800);
        var root = document.CompositionRoots[0];
        var node = new UiNode
        {
            Id = SemanticId.New(), Kind = UiNodeKind.Frame, DisplayLabel = "Node", Owner = OwnerReference.Root(root.Id),
            Anchors = [new UiAnchor { Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER, Target = target }],
            AuthoredProperties = new AuthoredProperties { Frame = new FrameProperties { Width = 100, Height = 50 } },
        };
        return document with { Nodes = [node], CompositionRoots = [root with { Children = [node.Id] }] };
    }
}
