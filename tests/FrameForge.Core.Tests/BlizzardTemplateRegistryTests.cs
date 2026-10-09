using FrameForge.Core.Templates;
using Xunit;

namespace FrameForge.Core.Tests;

public sealed class BlizzardTemplateRegistryTests
{
    [Fact]
    public void Resolves_single_and_recursive_button_inheritance()
    {
        var registry = Parse();

        var panel = AssertResolved(registry, "UIPanelButtonTemplate");
        var menu = AssertResolved(registry, "GameMenuButtonTemplate");

        Assert.Equal(["UIPanelButtonTemplate"], panel.InheritanceChain);
        Assert.Equal(["UIPanelButtonTemplate", "GameMenuButtonTemplate"], menu.InheritanceChain);
        Assert.Equal((200d, 30d), (menu.EffectiveProperties.Width, menu.EffectiveProperties.Height));
        Assert.Equal("FontMenu", menu.EffectiveProperties.NormalFont);
        Assert.Equal("Interface\\Synthetic\\Panel-Up", menu.EffectiveProperties.StateTextures[BlizzardButtonState.Normal].File);
    }

    [Fact]
    public void Reports_missing_and_cyclic_template_dependencies()
    {
        var missing = Parse(TemplatesXml().Replace(
            "inherits=\"UIPanelButtonTemplate\"", "inherits=\"MissingParent\"", StringComparison.Ordinal));
        Assert.Contains(missing.Diagnostics, item => item.Code == "missing-template-parent"
                                                     && item.Definition == "GameMenuButtonTemplate");

        var cyclicXml = TemplatesXml().Replace(
            "name=\"UIPanelButtonTemplate\" virtual=\"true\"",
            "name=\"UIPanelButtonTemplate\" virtual=\"true\" inherits=\"GameMenuButtonTemplate\"",
            StringComparison.Ordinal);
        var cyclic = Parse(cyclicXml);
        Assert.Contains(cyclic.Diagnostics, item => item.Code == "cyclic-template-inheritance");
    }

    [Fact]
    public void Child_precedence_preserves_declared_and_inherited_provenance()
    {
        var menu = AssertResolved(Parse(), "GameMenuButtonTemplate");

        Assert.Equal(200, menu.EffectiveProperties.Width);
        Assert.Equal(BlizzardTemplateValueOrigin.Declared,
            menu.EffectiveProperties.Provenance["width"].Origin);
        Assert.Equal("GameMenuButtonTemplate",
            menu.EffectiveProperties.Provenance["width"].Definition);
        Assert.Equal(BlizzardTemplateValueOrigin.InheritedTemplate,
            menu.EffectiveProperties.Provenance["stateTextures.Normal.file"].Origin);
        Assert.Equal("PanelUpTexture",
            menu.EffectiveProperties.Provenance["stateTextures.Normal.file"].Definition);
    }

    [Fact]
    public void Native_button_states_inherit_virtual_texture_values_without_defaults()
    {
        var panel = AssertResolved(Parse(), "UIPanelButtonTemplate");

        Assert.Equal(4, panel.EffectiveProperties.StateTextures.Count);
        var normal = panel.EffectiveProperties.StateTextures[BlizzardButtonState.Normal];
        Assert.Equal("PanelUpTexture", normal.Inherits);
        Assert.Equal("Interface\\Synthetic\\Panel-Up", normal.File);
        Assert.Equal(new BlizzardTexCoords(0, 0.625, 0, 0.6875), normal.TexCoords);
        Assert.Equal(BlizzardTemplateValueOrigin.InheritedDependency,
            panel.EffectiveProperties.Provenance["stateTextures.Normal.file"].Origin);
    }

    [Fact]
    public void Repeated_resolution_is_deterministic_and_definitions_are_read_only()
    {
        var first = Snapshot(Parse());
        var second = Snapshot(Parse());

        Assert.Equal(first, second);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, BlizzardResolvedTemplate>>(Parse().Templates);
        Assert.False(Parse().Templates is IDictionary<string, BlizzardResolvedTemplate> { IsReadOnly: false });
    }

    [Fact]
    public void Unsupported_multiple_inheritance_is_diagnostic_and_not_guessed()
    {
        var xml = TemplatesXml().Replace(
            "inherits=\"UIPanelButtonTemplate\"",
            "inherits=\"UIPanelButtonTemplate,CharacterFrameTabButtonTemplate\"",
            StringComparison.Ordinal);
        var registry = Parse(xml);

        Assert.Contains(registry.Diagnostics, item => item.Code == "unsupported-template-inheritance"
                                                     && item.Definition == "GameMenuButtonTemplate");
        Assert.False(registry.Resolve("GameMenuButtonTemplate")!.IsResolved);
    }

    [Fact]
    public void Unsupported_relative_dimensions_are_diagnostic_and_not_defaulted()
    {
        var xml = TemplatesXml().Replace(
            "<AbsDimension x=\"200\" y=\"30\"/>",
            "<RelDimension x=\"0.5\" y=\"0.5\"/>",
            StringComparison.Ordinal);
        var registry = Parse(xml);

        Assert.Contains(registry.Diagnostics, item => item.Code == "unsupported-relative-dimension"
                                                     && item.Definition == "GameMenuButtonTemplate");
        Assert.False(registry.Resolve("GameMenuButtonTemplate")!.IsResolved);
        Assert.Null(registry.Resolve("GameMenuButtonTemplate")!.EffectiveProperties.Width);
    }

    [Fact]
    public void Symbolic_parent_names_and_source_lines_survive_registry_resolution()
    {
        var tab = AssertResolved(Parse(), "CharacterFrameTabButtonTemplate");
        var left = Assert.Single(tab.EffectiveProperties.VisualRegions,
            item => item.SymbolicName == "$parentLeft");
        var middle = Assert.Single(tab.EffectiveProperties.VisualRegions,
            item => item.SymbolicName == "$parentMiddle");

        Assert.Equal("$parentLeft", left.SymbolicName);
        Assert.Equal("$parentLeft", Assert.Single(middle.Anchors).RelativeTo);
        var highlight = tab.EffectiveProperties.StateTextures[BlizzardButtonState.Highlight];
        Assert.Equal("$parentHighlightTexture", highlight.SymbolicName);
        Assert.Equal("$parentMiddle", Assert.Single(highlight.Anchors).RelativeTo);
        Assert.Equal(BlizzardTemplateValueOrigin.Declared,
            tab.EffectiveProperties.Provenance["stateTextures.Highlight.anchors"].Origin);
        Assert.True(tab.Definition.Source.Line > 0);
        Assert.True(left.Source.Line > tab.Definition.Source.Line);
        Assert.Contains(BlizzardKnownPreviewBehavior.CharacterTabResizeToTextZeroPadding,
            tab.EffectiveProperties.PreviewBehaviors);
    }

    [Fact]
    public void Missing_assets_are_reported_without_substitution()
    {
        var assets = Assets().Where(item => !item.LogicalPath.EndsWith("Panel-Down", StringComparison.Ordinal));
        var registry = BlizzardTemplateParser.Parse(Sources(TemplatesXml()), assets);

        Assert.Contains(registry.Diagnostics, item => item.Code == "unresolved-template-asset"
                                                     && item.Dependency == "Interface\\Synthetic\\Panel-Down");
        Assert.Equal("Interface\\Synthetic\\Panel-Down",
            registry.Resolve("UIPanelButtonTemplate")!.EffectiveProperties
                .StateTextures[BlizzardButtonState.Pushed].File);
    }

    private static BlizzardResolvedTemplate AssertResolved(BlizzardTemplateRegistry registry, string name)
    {
        var template = Assert.IsType<BlizzardResolvedTemplate>(registry.Resolve(name));
        Assert.True(template.IsResolved,
            string.Join(Environment.NewLine, template.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        return template;
    }

    private static BlizzardTemplateRegistry Parse(string? templates = null) =>
        BlizzardTemplateParser.Parse(Sources(templates ?? TemplatesXml()), Assets());

    private static IReadOnlyList<BlizzardTemplateXmlSource> Sources(string templates) =>
    [
        new("Interface\\FrameXML\\SyntheticTemplates.xml", templates, "synthetic.MPQ", "3.3.5a / 12340", "enUS"),
        new("Interface\\FrameXML\\SyntheticFonts.xml", FontsXml(), "synthetic.MPQ", "3.3.5a / 12340", "enUS"),
    ];

    private static IReadOnlyList<BlizzardAssetDependency> Assets() =>
    [
        Resolved("Interface\\Synthetic\\Panel-Up"),
        Resolved("Interface\\Synthetic\\Panel-Down"),
        Resolved("Interface\\Synthetic\\Panel-Disabled"),
        Resolved("Interface\\Synthetic\\Panel-Highlight"),
        Resolved("Interface\\Synthetic\\Tab"),
        Resolved("Fonts\\SYNTHETIC.TTF"),
    ];

    private static BlizzardAssetDependency Resolved(string path) =>
        new(path, true, $"C:\\synthetic\\{Path.GetFileName(path)}", "synthetic-hash");

    private static string Snapshot(BlizzardTemplateRegistry registry) => string.Join("\n",
        registry.Templates.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item =>
            $"{item.Key}|{string.Join('>', item.Value.InheritanceChain)}|" +
            $"{item.Value.EffectiveProperties.Width},{item.Value.EffectiveProperties.Height}|" +
            string.Join(',', item.Value.EffectiveProperties.StateTextures.OrderBy(state => state.Key)
                .Select(state => $"{state.Key}:{state.Value.File}:{state.Value.Inherits}"))));

    private static string FontsXml() => """
        <Ui>
          <Font name="FontBase" font="Fonts\SYNTHETIC.TTF" virtual="true">
            <FontHeight><AbsValue val="12"/></FontHeight>
            <Color r="1" g="1" b="1"/>
          </Font>
          <Font name="FontPanel" inherits="FontBase" virtual="true"/>
          <Font name="FontHighlight" inherits="FontPanel" virtual="true"/>
          <Font name="FontDisabled" inherits="FontPanel" virtual="true"><Color r="0.5" g="0.5" b="0.5"/></Font>
          <Font name="FontMenu" inherits="FontPanel" virtual="true"/>
          <Font name="FontTab" inherits="FontBase" virtual="true"/>
        </Ui>
        """;

    private static string TemplatesXml() => """
        <Ui>
          <Texture name="PanelUpTexture" file="Interface\Synthetic\Panel-Up" virtual="true">
            <TexCoords left="0" right="0.625" top="0" bottom="0.6875"/>
          </Texture>
          <Texture name="PanelDownTexture" file="Interface\Synthetic\Panel-Down" virtual="true"/>
          <Texture name="PanelDisabledTexture" file="Interface\Synthetic\Panel-Disabled" virtual="true"/>
          <Texture name="PanelHighlightTexture" file="Interface\Synthetic\Panel-Highlight" alphaMode="ADD" virtual="true"/>
          <Button name="UIPanelButtonTemplate" virtual="true">
            <NormalFont style="FontPanel"/>
            <HighlightFont style="FontHighlight"/>
            <DisabledFont style="FontDisabled"/>
            <NormalTexture inherits="PanelUpTexture"/>
            <PushedTexture inherits="PanelDownTexture"/>
            <DisabledTexture inherits="PanelDisabledTexture"/>
            <HighlightTexture inherits="PanelHighlightTexture"/>
          </Button>
          <Button name="GameMenuButtonTemplate" inherits="UIPanelButtonTemplate" virtual="true">
            <Size><AbsDimension x="200" y="30"/></Size>
            <NormalFont style="FontMenu"/>
          </Button>
          <Button name="CharacterFrameTabButtonTemplate" virtual="true">
            <Size><AbsDimension x="10" y="32"/></Size>
            <Layers><Layer level="BACKGROUND">
              <Texture name="$parentLeft" file="Interface\Synthetic\Tab">
                <Size><AbsDimension x="20" y="32"/></Size>
                <Anchors><Anchor point="TOPLEFT"/></Anchors>
                <TexCoords left="0" right="0.2" top="0" bottom="1"/>
              </Texture>
              <Texture name="$parentMiddle" file="Interface\Synthetic\Tab">
                <Anchors><Anchor point="LEFT" relativeTo="$parentLeft" relativePoint="RIGHT"/></Anchors>
              </Texture>
              <Texture name="$parentRightDisabled" file="Interface\Synthetic\Tab"/>
            </Layer></Layers>
            <ButtonText name="$parentText"><Anchors><Anchor point="CENTER"/></Anchors></ButtonText>
            <NormalFont style="FontTab"/>
            <HighlightTexture name="$parentHighlightTexture" file="Interface\Synthetic\Tab">
              <Anchors><Anchor point="LEFT" relativeTo="$parentMiddle" relativePoint="RIGHT"/></Anchors>
            </HighlightTexture>
            <Scripts><OnShow>PanelTemplates_TabResize(self, 0);</OnShow></Scripts>
          </Button>
        </Ui>
        """;
}
