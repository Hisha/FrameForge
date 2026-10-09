using FrameForge.Core.Models;
using FrameForge.Core.Semantics.V2;
using System.Text.Json.Nodes;
using Xunit;

namespace FrameForge.Core.Tests;

public class V2SemanticModelTests
{
    private static readonly SemanticId DocumentId = new("00000000-0000-0000-0000-000000000001");
    private static readonly SemanticId RootId = new("00000000-0000-0000-0000-000000000002");
    private static readonly SemanticId PanelId = new("00000000-0000-0000-0000-000000000003");
    private static readonly SemanticId TextureId = new("00000000-0000-0000-0000-000000000004");
    private static readonly SemanticId ButtonId = new("00000000-0000-0000-0000-000000000005");

    [Fact]
    public void FactoryCreatesValidDocumentWithExactlyOneRootAndCorrectProfile()
    {
        var document = UiDocumentFactory.Create("ExampleDesignRoot", "ExampleModuleHost");

        Assert.Single(document.CompositionRoots);
        Assert.Equal(WowTargetProfile.Wow335a12340, document.Target);
        Assert.Empty(UiDocumentValidator.Validate(document));
    }

    [Fact]
    public void IdentityDoesNotChangeWhenAControlIsRenamed()
    {
        var original = ValidDocument();
        var panel = original.Nodes[0];
        var renamed = panel with { DisplayLabel = "Renamed panel", RuntimeName = "RenamedPanel" };

        Assert.Equal(panel.Id, renamed.Id);
    }

    [Fact]
    public void ReportsMissingAndMultipleCompositionRoots()
    {
        var valid = ValidDocument();
        var missing = valid with { CompositionRoots = [] };
        var multiple = valid with { CompositionRoots = [valid.CompositionRoots[0], valid.CompositionRoots[0] with { Id = SemanticId.New() }] };

        Assert.Contains(UiDocumentValidator.Validate(missing), item => item.Code == "FFV2-ROOT-001");
        Assert.Contains(UiDocumentValidator.Validate(multiple), item => item.Code == "FFV2-ROOT-002");
    }

    [Fact]
    public void RejectsUnsupportedTargetBuild()
    {
        var document = ValidDocument() with
        {
            Target = new WowTargetProfile { Product = "wow-3.3.5a", Build = 99999 },
        };

        Assert.Contains(UiDocumentValidator.Validate(document), item => item.Code == "FFV2-TARGET-001");
    }

    [Fact]
    public void ReportsDuplicateInternalAndRuntimeIdentities()
    {
        var document = ValidDocument();
        var duplicate = document.Nodes[1] with { Id = PanelId, RuntimeName = "ExamplePanel" };
        document = document with { Nodes = [document.Nodes[0], duplicate, document.Nodes[2]] };

        var diagnostics = UiDocumentValidator.Validate(document);
        Assert.Contains(diagnostics, item => item.Code == "FFV2-ID-002");
        Assert.Contains(diagnostics, item => item.Code == "FFV2-ID-003");
    }

    [Fact]
    public void ValidatesReciprocalOwnershipAndMissingOwners()
    {
        var document = ValidDocument();
        var panel = document.Nodes[0] with { Owner = OwnerReference.Node(SemanticId.New()) };
        document = document with { Nodes = [panel, .. document.Nodes.Skip(1)] };

        Assert.Contains(UiDocumentValidator.Validate(document), item => item.Code == "FFV2-OWNER-002" && item.NodeId == PanelId);
    }

    [Fact]
    public void ReportsDanglingOrderedChildrenAndUndeclaredCompositionHost()
    {
        var document = ValidDocument();
        document = document with
        {
            CompositionRoots = [document.CompositionRoots[0] with { Children = [PanelId, ButtonId, SemanticId.New()] }],
            ExternalReferences = document.ExternalReferences.Where(item => item.GlobalName != "ExampleModuleHost").ToArray(),
        };

        var diagnostics = UiDocumentValidator.Validate(document);
        Assert.Contains(diagnostics, item => item.Code == "FFV2-OWNER-009");
        Assert.Contains(diagnostics, item => item.Code == "FFV2-EXT-003");
    }

    [Fact]
    public void DetectsOwnershipCycles()
    {
        var document = ValidDocument();
        var panel = document.Nodes[0] with { Owner = OwnerReference.Node(ButtonId), Children = [TextureId] };
        var button = document.Nodes[2] with { Owner = OwnerReference.Node(PanelId), Children = [PanelId] };
        document = document with
        {
            CompositionRoots = [document.CompositionRoots[0] with { Children = [] }],
            Nodes = [panel, document.Nodes[1], button],
        };

        Assert.Contains(UiDocumentValidator.Validate(document), item => item.Code == "FFV2-OWNER-011");
    }

    [Fact]
    public void RegionsCannotOwnChildren()
    {
        var document = ValidDocument();
        var texture = document.Nodes[1] with { Children = [ButtonId] };
        var button = document.Nodes[2] with { Owner = OwnerReference.Node(TextureId) };
        document = document with { Nodes = [document.Nodes[0], texture, button] };

        var diagnostics = UiDocumentValidator.Validate(document);
        Assert.Contains(diagnostics, item => item.Code == "FFV2-OWNER-003");
        Assert.Contains(diagnostics, item => item.Code == "FFV2-OWNER-007");
    }

    [Fact]
    public void AcceptsParentRootSiblingAndDeclaredExternalAnchors()
    {
        var document = ValidDocument();
        var panel = document.Nodes[0] with
        {
            Anchors =
            [
                Anchor(AnchorTarget.Parent()),
                Anchor(AnchorTarget.Root()),
                Anchor(AnchorTarget.Local(ButtonId)),
                Anchor(AnchorTarget.External("UIParent")),
            ],
        };
        document = document with { Nodes = [panel, .. document.Nodes.Skip(1)] };

        Assert.DoesNotContain(UiDocumentValidator.Validate(document), item => item.Code.StartsWith("FFV2-ANCHOR", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsMissingUnresolvedAndUndeclaredAnchorTargets()
    {
        var document = ValidDocument();
        var panel = document.Nodes[0] with
        {
            Anchors =
            [
                Anchor(AnchorTarget.Local(SemanticId.New())),
                Anchor(AnchorTarget.Unresolved("$parentMystery", "template expansion is unavailable")),
                Anchor(AnchorTarget.External("NotDeclared")),
            ],
        };
        document = document with { Nodes = [panel, .. document.Nodes.Skip(1)] };

        var diagnostics = UiDocumentValidator.Validate(document);
        Assert.Contains(diagnostics, item => item.Code == "FFV2-ANCHOR-002");
        Assert.Contains(diagnostics, item => item.Code == "FFV2-ANCHOR-005" && item.Message.Contains("$parentMystery", StringComparison.Ordinal));
        Assert.Contains(diagnostics, item => item.Code == "FFV2-ANCHOR-004");
    }

    [Fact]
    public void ParentAnchorNeverIntroducesImplicitUiParent()
    {
        var document = ValidDocument();
        var panel = document.Nodes[0] with { Anchors = [Anchor(AnchorTarget.Parent())] };
        document = document with
        {
            Nodes = [panel, .. document.Nodes.Skip(1)],
            ExternalReferences = document.ExternalReferences.Where(item => item.GlobalName != "UIParent").ToArray(),
        };

        var serialized = UiDocumentCodec.Serialize(document);
        Assert.Contains("\"kind\": \"parent\"", serialized);
        Assert.DoesNotContain("UIParent", serialized);
    }

    [Theory]
    [InlineData(RootSizingKind.FillHost)]
    [InlineData(RootSizingKind.Explicit)]
    public void SupportsBothRootSizingModes(RootSizingKind kind)
    {
        var document = ValidDocument();
        var sizing = kind == RootSizingKind.FillHost
            ? RootSizing.FillHost()
            : RootSizing.Explicit(800, 600, new RootAnchor { Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER });
        document = document with { CompositionRoots = [document.CompositionRoots[0] with { Sizing = sizing }] };

        Assert.DoesNotContain(UiDocumentValidator.Validate(document), item => item.Code.StartsWith("FFV2-ROOT", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsContradictoryRootSizing()
    {
        var document = ValidDocument();
        var badSizing = new RootSizing { Kind = RootSizingKind.FillHost, Width = 100, Height = 100 };
        document = document with { CompositionRoots = [document.CompositionRoots[0] with { Sizing = badSizing }] };

        Assert.Contains(UiDocumentValidator.Validate(document), item => item.Code == "FFV2-ROOT-006");
    }

    [Fact]
    public void KeepsFrameStrataSeparateFromRegionDrawLayer()
    {
        var document = ValidDocument();
        Assert.NotNull(document.Nodes[0].AuthoredProperties.Frame?.Strata);
        Assert.Null(document.Nodes[0].AuthoredProperties.Region);
        Assert.NotNull(document.Nodes[1].AuthoredProperties.Region?.DrawLayer);
        Assert.Null(document.Nodes[1].AuthoredProperties.Frame);
        Assert.Empty(UiDocumentValidator.Validate(document));
    }

    [Fact]
    public void RejectsFramePropertiesOnRegionsAndInvalidStatusBarRanges()
    {
        var document = ValidDocument();
        var texture = document.Nodes[1] with
        {
            AuthoredProperties = document.Nodes[1].AuthoredProperties with { Frame = new FrameProperties() },
        };
        var status = new UiNode
        {
            Id = SemanticId.New(),
            Kind = UiNodeKind.StatusBar,
            RuntimeName = "InvalidStatus",
            DisplayLabel = "Invalid status",
            Owner = OwnerReference.Root(RootId),
            AuthoredProperties = new AuthoredProperties
            {
                Frame = new FrameProperties(),
                StatusBar = new StatusBarProperties { Minimum = 10, Maximum = 5, Value = 7 },
            },
        };
        document = document with
        {
            CompositionRoots = [document.CompositionRoots[0] with { Children = [PanelId, ButtonId, status.Id] }],
            Nodes = [document.Nodes[0], texture, document.Nodes[2], status],
        };

        var diagnostics = UiDocumentValidator.Validate(document);
        Assert.Contains(diagnostics, item => item.Code == "FFV2-PROP-001");
        Assert.Contains(diagnostics, item => item.Code == "FFV2-PROP-012");
    }

    [Fact]
    public void SerializationRoundTripPreservesIdentityOrderingAnchorsExternalReferencesAndMetadata()
    {
        var original = ValidDocument();
        var text = UiDocumentCodec.Serialize(original);
        var parsed = UiDocumentCodec.Parse(text);

        Assert.True(parsed.Ok, parsed.ErrorText);
        Assert.Equal(text, UiDocumentCodec.Serialize(parsed.Document!));
        Assert.Equal([PanelId, TextureId, ButtonId], parsed.Document!.Nodes.Select(node => node.Id));
        Assert.Equal([PanelId, ButtonId], parsed.Document.CompositionRoots[0].Children);
        Assert.Equal(AnchorTargetKind.LocalNode, parsed.Document.Nodes[2].Anchors[0].Target.Kind);
        Assert.Equal("UIParent", parsed.Document.ExternalReferences[1].GlobalName);
        Assert.Equal("value", parsed.Document.Editor!.Values["custom-key"]);
    }

    [Fact]
    public void SerializationIsDeterministicAndEndsWithNewline()
    {
        var document = ValidDocument();

        Assert.Equal(UiDocumentCodec.Serialize(document), UiDocumentCodec.Serialize(document));
        Assert.EndsWith("\n", UiDocumentCodec.Serialize(document));
    }

    [Fact]
    public void ParserRejectsMalformedUnknownAndV1DocumentsClearly()
    {
        Assert.Contains("Malformed", UiDocumentCodec.Parse("{ nope").ErrorText);
        Assert.Contains("not converted automatically", UiDocumentCodec.Parse("""
            { "format": "frameforge-project", "version": 1 }
            """).ErrorText);

        var withUnknown = UiDocumentCodec.Serialize(ValidDocument()).Replace(
            "\"target\":", "\"unexpected\": true,\n  \"target\":", StringComparison.Ordinal);
        Assert.Contains("$.unexpected", UiDocumentCodec.Parse(withUnknown).ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserRejectsMissingRequiredStructure()
    {
        var result = UiDocumentCodec.Parse("""
            { "format": "frameforge-ui-document", "version": 2 }
            """);

        Assert.False(result.Ok);
        Assert.Contains("required", result.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParserRejectsExplicitNullForRequiredGraphObjects()
    {
        var json = JsonNode.Parse(UiDocumentCodec.Serialize(ValidDocument()))!.AsObject();
        json["compositionRoots"] = null;

        var result = UiDocumentCodec.Parse(json.ToJsonString());
        Assert.False(result.Ok);
        Assert.Contains("$.compositionRoots", result.ErrorText, StringComparison.Ordinal);
    }

    private static UiAnchor Anchor(AnchorTarget target) => new()
    {
        Point = AnchorPoint.TOPLEFT,
        RelativePoint = AnchorPoint.TOPLEFT,
        Target = target,
    };

    private static UiDocument ValidDocument()
    {
        var root = new CompositionRoot
        {
            Id = RootId,
            RuntimeName = "ExampleDesignRoot",
            ExternalHostName = "ExampleModuleHost",
            DesignWidth = 1024,
            DesignHeight = 768,
            Sizing = RootSizing.FillHost(),
            Children = [PanelId, ButtonId],
        };
        var panel = new UiNode
        {
            Id = PanelId,
            Kind = UiNodeKind.Frame,
            RuntimeName = "ExamplePanel",
            DisplayLabel = "Panel",
            Owner = OwnerReference.Root(RootId),
            Children = [TextureId],
            Anchors = [Anchor(AnchorTarget.Parent())],
            AuthoredProperties = new AuthoredProperties
            {
                Frame = new FrameProperties { Width = 400, Height = 300, Strata = FrameStrata.Medium, Level = 2, Visible = true },
            },
            Editor = new NodeEditorMetadata { Notes = "Main panel" },
        };
        var texture = new UiNode
        {
            Id = TextureId,
            Kind = UiNodeKind.Texture,
            DisplayLabel = "Background",
            Owner = OwnerReference.Node(PanelId),
            Anchors = [Anchor(AnchorTarget.Parent())],
            AuthoredProperties = new AuthoredProperties
            {
                Region = new RegionProperties { DrawLayer = RegionDrawLayer.Background, Sublevel = 0, Tint = new UiColor(1, 1, 1) },
                Texture = new TextureProperties { TextureReference = "Interface\\Example\\Background" },
            },
        };
        var button = new UiNode
        {
            Id = ButtonId,
            Kind = UiNodeKind.Button,
            RuntimeName = "ExampleButton",
            DisplayLabel = "Action",
            Owner = OwnerReference.Root(RootId),
            Anchors = [Anchor(AnchorTarget.Local(PanelId))],
            AuthoredProperties = new AuthoredProperties
            {
                Frame = new FrameProperties { Width = 120, Height = 24, Visible = true },
                Button = new ButtonProperties { Enabled = true },
            },
        };
        return new UiDocument
        {
            Target = WowTargetProfile.Wow335a12340,
            DocumentId = DocumentId,
            CompositionRoots = [root],
            Nodes = [panel, texture, button],
            ExternalReferences =
            [
                new ExternalReference { GlobalName = "ExampleModuleHost", Description = "Module-owned host" },
                new ExternalReference { GlobalName = "UIParent", ExpectedSource = "Blizzard FrameXML" },
            ],
            Editor = new DocumentEditorMetadata
            {
                Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["custom-key"] = "value" },
            },
            Diagnostics =
            [
                new UiDiagnostic
                {
                    Code = "FFV2-NOTE-001",
                    Severity = DiagnosticSeverity.Warning,
                    Message = "Illustrative persisted diagnostic.",
                    NodeId = PanelId,
                    PropertyPath = "editor.notes",
                },
            ],
        };
    }
}
