using System.Xml.Linq;
using FrameForge.Core.Export;
using FrameForge.Core.Models;
using FrameForge.Core.Semantics.V2;
using FrameForge.Core.Templates;
using Xunit;

namespace FrameForge.Core.Tests;

public sealed class V2TemplateIntegrationTests
{
    private static readonly SemanticId RootId = Id(1);
    private static readonly SemanticId ButtonId = Id(2);
    private static readonly XNamespace Ui = "http://www.blizzard.com/wow/ui/";

    [Fact]
    public void Template_reference_and_button_text_round_trip_without_changing_legacy_shape()
    {
        var legacy = Document();
        var legacyJson = UiDocumentCodec.Serialize(legacy);
        Assert.DoesNotContain("blizzardTemplate", legacyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"text\"", legacyJson, StringComparison.Ordinal);

        var authored = WithButton(legacy, button => button with
        {
            BlizzardTemplate = "GameMenuButtonTemplate",
            AuthoredProperties = button.AuthoredProperties with
            {
                Button = button.AuthoredProperties.Button! with { Text = "Continue" },
            },
        });
        var json = UiDocumentCodec.Serialize(authored);
        var parsed = UiDocumentCodec.Parse(json);

        Assert.True(parsed.Ok, parsed.ErrorText);
        Assert.Equal("GameMenuButtonTemplate", parsed.Document!.Nodes[0].BlizzardTemplate);
        Assert.Equal("Continue", parsed.Document.Nodes[0].AuthoredProperties.Button!.Text);
    }

    [Fact]
    public void Unknown_template_survives_loading_and_remains_inspectable()
    {
        var document = WithButton(Document(), button => button with { BlizzardTemplate = "FutureButtonTemplate" });
        var parsed = UiDocumentCodec.Parse(UiDocumentCodec.Serialize(document));

        Assert.True(parsed.Ok, parsed.ErrorText);
        Assert.Equal("FutureButtonTemplate", parsed.Document!.Nodes[0].BlizzardTemplate);
        Assert.Contains(UiDocumentValidator.Validate(parsed.Document, Registry()),
            item => item.Code == "FFV2-TEMPLATE-001" && item.NodeId == ButtonId);
    }

    [Fact]
    public void Assign_change_and_clear_preserve_identity_anchors_and_authored_overrides()
    {
        var registry = Registry();
        var editor = new UiDocumentEditor(registry);
        var original = Document(width: 155, height: 37);
        var originalAnchor = original.Nodes[0].Anchors[0];

        var assigned = editor.AssignBlizzardTemplate(original, ButtonId, "GameMenuButtonTemplate");
        var changed = editor.AssignBlizzardTemplate(assigned.Document, ButtonId, "CharacterFrameTabButtonTemplate");
        var cleared = editor.ClearBlizzardTemplate(changed.Document, ButtonId);

        Assert.True(assigned.Success, assigned.ErrorText);
        Assert.True(changed.Success, changed.ErrorText);
        Assert.True(cleared.Success, cleared.ErrorText);
        Assert.Equal((155d, 37d), Dimensions(changed.Document.Nodes[0]));
        Assert.Equal(ButtonId, cleared.Document.Nodes[0].Id);
        Assert.Equal(originalAnchor, cleared.Document.Nodes[0].Anchors[0]);
        Assert.Null(cleared.Document.Nodes[0].BlizzardTemplate);
    }

    [Fact]
    public void Explicit_override_clearing_uses_inherited_dimensions_without_authoring_them()
    {
        var registry = Registry();
        var editor = new UiDocumentEditor(registry);
        var assigned = editor.AssignBlizzardTemplate(Document(), ButtonId, "GameMenuButtonTemplate");
        var cleared = editor.ClearTemplateEligibleOverrides(assigned.Document, ButtonId);
        var node = cleared.Document.Nodes[0];
        var effective = UiTemplateEffectiveProperties.Resolve(node, registry);

        Assert.True(cleared.Success, cleared.ErrorText);
        Assert.Equal((null, null), Dimensions(node));
        Assert.Equal((100d, 20d),
            (effective.Values.Frame!.Width, effective.Values.Frame.Height));
        Assert.Equal(EffectivePropertyOrigin.TemplateInherited, effective.Provenance["frame.width"]);
        Assert.Equal(EffectivePropertyOrigin.TemplateInherited, effective.Provenance["frame.height"]);
    }

    [Fact]
    public void Effective_resolution_distinguishes_authored_declared_unresolved_and_preview_values()
    {
        var registry = Registry();
        var authoredNode = WithButton(Document(), button => button with
        {
            BlizzardTemplate = "CharacterFrameTabButtonTemplate",
        }).Nodes[0];
        var declaredNode = authoredNode with
        {
            AuthoredProperties = authoredNode.AuthoredProperties with
            {
                Frame = authoredNode.AuthoredProperties.Frame! with { Width = null, Height = null },
            },
        };
        var unresolvedNode = authoredNode with { BlizzardTemplate = "FutureTemplate" };

        var authored = UiTemplateEffectiveProperties.Resolve(authoredNode, registry);
        var declared = UiTemplateEffectiveProperties.Resolve(declaredNode, registry);
        var unresolved = UiTemplateEffectiveProperties.Resolve(unresolvedNode, registry);

        Assert.Equal(EffectivePropertyOrigin.Authored, authored.Provenance["frame.width"]);
        Assert.Equal(EffectivePropertyOrigin.TemplateDeclared, declared.Provenance["frame.width"]);
        Assert.Contains(BlizzardKnownPreviewBehavior.CharacterTabResizeToTextZeroPadding,
            declared.PreviewBehaviors);
        Assert.Equal(EffectivePropertyOrigin.Unresolved, unresolved.Provenance["template"]);
        Assert.False(unresolved.IsResolved);
    }

    [Fact]
    public void Invalid_template_edits_are_atomic()
    {
        var registry = Registry();
        var editor = new UiDocumentEditor(registry);
        var original = Document();
        var unknown = editor.AssignBlizzardTemplate(original, ButtonId, "NotVerified");

        var frame = original.Nodes[0] with
        {
            Kind = UiNodeKind.Frame,
            AuthoredProperties = new AuthoredProperties { Frame = original.Nodes[0].AuthoredProperties.Frame },
        };
        var frameDocument = original with { Nodes = [frame] };
        var incompatible = editor.AssignBlizzardTemplate(frameDocument, ButtonId, "GameMenuButtonTemplate");

        Assert.False(unknown.Success);
        Assert.Same(original, unknown.Document);
        Assert.False(incompatible.Success);
        Assert.Same(frameDocument, incompatible.Document);
        Assert.Contains(incompatible.Diagnostics, item => item.Code == "FFV2-TEMPLATE-002");
    }

    [Fact]
    public void Validation_requires_registry_compatibility_dependencies_and_effective_dimensions()
    {
        var templated = WithButton(Document(width: null, height: null),
            button => button with { BlizzardTemplate = "UIPanelButtonTemplate" });
        Assert.Contains(UiDocumentValidator.Validate(templated), item => item.Code == "FFV2-TEMPLATE-003");

        var noDimensions = UiDocumentValidator.Validate(templated, Registry(includePanelDimensions: false));
        Assert.Contains(noDimensions, item => item.Code == "FFV2-TEMPLATE-006");

        var unresolved = UiDocumentValidator.Validate(templated, Registry(resolveAssets: false));
        Assert.Contains(unresolved, item => item.Code == "FFV2-TEMPLATE-005");
    }

    [Fact]
    public void Native_export_emits_inherits_and_authored_text_without_inherited_regions_or_dimensions()
    {
        var registry = Registry();
        var document = WithButton(Document(width: null, height: null), button => button with
        {
            BlizzardTemplate = "GameMenuButtonTemplate",
            AuthoredProperties = button.AuthoredProperties with
            {
                Button = button.AuthoredProperties.Button! with { Text = "Play" },
            },
        });

        var first = V2FrameXmlExporter.Build(document, null, registry);
        var second = V2FrameXmlExporter.Build(document, null, registry);
        Assert.True(first.IsValid, Diagnostics(first));
        Assert.Equal(first.Xml, second.Xml);
        Assert.Equal(first.Manifest, second.Manifest);

        var button = XDocument.Parse(first.Xml).Descendants(Ui + "Button").Single();
        Assert.Equal("GameMenuButtonTemplate", (string?)button.Attribute("inherits"));
        Assert.Equal("Play", (string?)button.Attribute("text"));
        Assert.Null(button.Element(Ui + "Size"));
        Assert.Empty(button.Elements(Ui + "ButtonText"));
        Assert.Equal("Frames", button.Parent?.Name.LocalName);
        Assert.Equal("DesignRoot", (string?)button.Parent?.Parent?.Attribute("name"));
        Assert.Null(button.Descendants(Ui + "Anchor").Single().Attribute("relativeTo"));
        Assert.Contains(first.ExternalDependencies,
            item => item.Kind == "blizzardTemplate" && item.Identity == "GameMenuButtonTemplate");
    }

    [Fact]
    public void Native_export_fails_closed_for_missing_registry_or_template_dependencies()
    {
        var document = WithButton(Document(), button => button with
        {
            BlizzardTemplate = "UIPanelButtonTemplate",
        });

        var missingRegistry = V2FrameXmlExporter.Build(document, null);
        var missingAsset = V2FrameXmlExporter.Build(document, null, Registry(resolveAssets: false));

        Assert.False(missingRegistry.IsValid);
        Assert.Empty(missingRegistry.Xml);
        Assert.Contains(missingRegistry.Diagnostics, item => item.Code == "FFV2-TEMPLATE-003");
        Assert.False(missingAsset.IsValid);
        Assert.Empty(missingAsset.Xml);
        Assert.Contains(missingAsset.Diagnostics, item => item.Code == "FFV2-TEMPLATE-005");
    }

    [Fact]
    public void Existing_golden_project_and_untemplated_export_remain_compatible()
    {
        var assembly = typeof(V2TemplateIntegrationTests).Assembly;
        using var projectStream = assembly.GetManifestResourceStream(
            "FrameForge.Core.Tests.examples.frameforge-v2-golden.fforge.json");
        using var xmlStream = assembly.GetManifestResourceStream(
            "FrameForge.Core.Tests.examples.frameforge-v2-golden.Design.xml");
        Assert.NotNull(projectStream);
        Assert.NotNull(xmlStream);
        using var projectReader = new StreamReader(projectStream);
        using var xmlReader = new StreamReader(xmlStream);
        var project = projectReader.ReadToEnd();
        var parsed = UiDocumentCodec.Parse(project);

        Assert.True(parsed.Ok, parsed.ErrorText);
        Assert.Equal(NormalizeNewlines(project), NormalizeNewlines(UiDocumentCodec.Serialize(parsed.Document!)));
        Assert.Equal(NormalizeNewlines(xmlReader.ReadToEnd()),
            NormalizeNewlines(V2FrameXmlExporter.Build(parsed.Document!, null).Xml));
    }

    private static UiDocument Document(double? width = 120, double? height = 32)
    {
        var button = new UiNode
        {
            Id = ButtonId,
            Kind = UiNodeKind.Button,
            RuntimeName = "ActionButton",
            DisplayLabel = "Action",
            Owner = OwnerReference.Root(RootId),
            Anchors =
            [
                new UiAnchor
                {
                    Point = AnchorPoint.CENTER,
                    RelativePoint = AnchorPoint.CENTER,
                    Target = AnchorTarget.Parent(),
                    OffsetX = 4,
                    OffsetY = -3,
                },
            ],
            AuthoredProperties = new AuthoredProperties
            {
                Frame = new FrameProperties { Width = width, Height = height, Visible = true },
                Button = new ButtonProperties { Enabled = true },
            },
        };
        return new UiDocument
        {
            DocumentId = Id(99),
            Target = WowTargetProfile.Wow335a12340,
            CompositionRoots =
            [
                new CompositionRoot
                {
                    Id = RootId,
                    RuntimeName = "DesignRoot",
                    ExternalHostName = "ModuleHost",
                    DesignWidth = 800,
                    DesignHeight = 600,
                    Sizing = RootSizing.FillHost(),
                    Children = [ButtonId],
                },
            ],
            Nodes = [button],
            ExternalReferences = [new ExternalReference { GlobalName = "ModuleHost" }],
        };
    }

    private static BlizzardTemplateRegistry Registry(bool resolveAssets = true,
        bool includePanelDimensions = true)
    {
        var panelSize = includePanelDimensions
            ? "<Size><AbsDimension x=\"100\" y=\"20\"/></Size>"
            : string.Empty;
        var xml = $$"""
            <Ui>
              <Texture name="PanelUp" file="Interface\Synthetic\Panel-Up" virtual="true"/>
              <Button name="UIPanelButtonTemplate" virtual="true">
                {{panelSize}}
                <NormalFont style="GameFontNormal"/>
                <NormalTexture inherits="PanelUp"/>
              </Button>
              <Button name="GameMenuButtonTemplate" inherits="UIPanelButtonTemplate" virtual="true"/>
              <Button name="CharacterFrameTabButtonTemplate" virtual="true">
                <Size><AbsDimension x="10" y="32"/></Size>
                <NormalFont style="GameFontNormal"/>
                <Scripts><OnShow>PanelTemplates_TabResize(self, 0);</OnShow></Scripts>
              </Button>
              <Font name="GameFontNormal" font="Fonts\SYNTHETIC.TTF" virtual="true">
                <FontHeight><AbsValue val="12"/></FontHeight>
              </Font>
            </Ui>
            """;
        var assets = new[]
        {
            new BlizzardAssetDependency(@"Interface\Synthetic\Panel-Up", resolveAssets,
                resolveAssets ? @"C:\synthetic\Panel-Up.blp" : null,
                resolveAssets ? "texture-hash" : null,
                resolveAssets ? null : "Synthetic missing asset."),
            new BlizzardAssetDependency(@"Fonts\SYNTHETIC.TTF", resolveAssets,
                resolveAssets ? @"C:\synthetic\SYNTHETIC.TTF" : null,
                resolveAssets ? "font-hash" : null,
                resolveAssets ? null : "Synthetic missing font."),
        };
        return BlizzardTemplateParser.Parse(
            [new BlizzardTemplateXmlSource(@"Interface\FrameXML\Synthetic.xml", xml)], assets);
    }

    private static UiDocument WithButton(UiDocument document, Func<UiNode, UiNode> update) =>
        document with { Nodes = [update(document.Nodes.Single())] };

    private static (double? Width, double? Height) Dimensions(UiNode node) =>
        (node.AuthoredProperties.Frame?.Width, node.AuthoredProperties.Frame?.Height);

    private static SemanticId Id(int suffix) =>
        new($"20000000-0000-0000-0000-{suffix:000000000000}");

    private static string Diagnostics(V2FrameXmlExportPlan plan) =>
        string.Join(Environment.NewLine, plan.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
