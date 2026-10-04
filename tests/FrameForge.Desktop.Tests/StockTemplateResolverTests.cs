using FrameForge.Core.Models;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Templates;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class StockTemplateResolverTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"frameforge-stock-{Guid.NewGuid():N}");
    private readonly FakeProvider _provider;

    public StockTemplateResolverTests()
    {
        Directory.CreateDirectory(_temp);
        _provider = new FakeProvider(_temp);
        Write(@"Interface\FrameXML\Fonts.xml", """
            <Ui><Font name="SystemFont_Shadow_Small" font="Fonts\FRIZQT__.TTF" virtual="true">
              <Shadow><Offset><AbsDimension x="1" y="-1"/></Offset><Color r="0" g="0" b="0"/></Shadow>
              <FontHeight><AbsValue val="10"/></FontHeight>
            </Font></Ui>
            """);
        Write(@"Interface\FrameXML\FontStyles.xml", """
            <Ui>
              <Font name="GameFontNormalSmall" inherits="SystemFont_Shadow_Small" virtual="true">
                <Color r="1" g="0.82" b="0"/>
              </Font>
              <Font name="GameFontHighlightSmall" inherits="GameFontNormalSmall" justifyH="LEFT" virtual="true">
                <Color r="1" g="1" b="1"/>
              </Font>
            </Ui>
            """);
        Write(@"Interface\FrameXML\CharacterFrameTemplates.xml", """
            <Ui><Button name="CharacterFrameTabButtonTemplate" virtual="true">
              <Size><AbsDimension x="10" y="32"/></Size>
              <Layers><Layer level="BACKGROUND">
                <Texture name="$parentLeft" file="Interface\Tabs\Normal"><Size><AbsDimension x="20" y="32"/></Size><TexCoords left="0" right="0.15625" top="0" bottom="1"/></Texture>
                <Texture name="$parentMiddle" file="Interface\Tabs\Normal"><Size><AbsDimension x="88" y="32"/></Size><TexCoords left="0.15625" right="0.84375" top="0" bottom="1"/></Texture>
                <Texture name="$parentRight" file="Interface\Tabs\Normal"><Size><AbsDimension x="20" y="32"/></Size><TexCoords left="0.84375" right="1" top="0" bottom="1"/></Texture>
              </Layer></Layers>
              <ButtonText name="$parentText"><Anchors><Anchor point="CENTER"><Offset><AbsDimension x="0" y="2"/></Offset></Anchor></Anchors></ButtonText>
              <NormalFont style="GameFontNormalSmall"/>
            </Button></Ui>
            """);
        Write(@"Interface\Tabs\Normal.blp", "synthetic texture marker");
        Write(@"Interface\FrameXML\LFDFrame.xml", """
            <Ui><Frame name="LFDParentFrame" parent="UIParent"><Size x="355" y="440"/></Frame></Ui>
            """);
        Write(@"Interface\FrameXML\UIPanelTemplates.xml", "<Ui />");
    }

    [Fact]
    public void Font_lookup_resolves_transitive_inheritance_metrics_color_shadow_and_provenance()
    {
        using var resolver = new StockTemplateResolver(_provider);
        var style = Assert.IsType<StockFontStyle>(resolver.ResolveFont("GameFontHighlightSmall"));

        Assert.Equal(10, style.Size);
        Assert.Equal(ColorRgba.White, style.Color);
        Assert.Equal((1d, -1d), (style.ShadowX, style.ShadowY));
        Assert.Equal("LEFT", style.JustifyH);
        Assert.Equal(new[] { "SystemFont_Shadow_Small", "GameFontNormalSmall", "GameFontHighlightSmall" },
            style.Provenance.Select(item => item.Definition));
    }

    [Fact]
    public void Button_template_retains_nested_texture_children_expands_parent_and_applies_effective_size()
    {
        using var resolver = new StockTemplateResolver(_provider);
        var style = Assert.IsType<StockButtonStyle>(resolver.ResolveButton(StockTemplateResolver.TabTemplate));
        Assert.Equal(3, style.NormalSlices.Count);
        Assert.Equal((0d, 2d), (style.TextOffsetX, style.TextOffsetY));
        Assert.Equal(new[] { "Tab1Left", "Tab1Middle", "Tab1Right", "Tab1Text" },
            resolver.ExpandedChildNames("Tab1", style));

        var button = new FrameDef
        {
            Name = "Tab1", Kind = FrameKind.BUTTON, Inherits = StockTemplateResolver.TabTemplate,
            Width = 0, Height = 0, Visual = new FrameVisual { Text = new TextVisual("Hunts") },
        };
        var effective = resolver.ApplyEffectiveGeometry(new Project { Frames = [button] }).Frames[0];
        Assert.True(effective.Width > 40);
        Assert.Equal(32, effective.Height);

        var projectOverride = resolver.ApplyEffectiveGeometry(new Project { Frames = [button with { Width = 99, Height = 40 }] }).Frames[0];
        Assert.Equal((99d, 40d), (projectOverride.Width, projectOverride.Height));
    }

    [Fact]
    public void Font_auto_size_uses_effective_style_without_mutating_declared_project()
    {
        using var resolver = new StockTemplateResolver(_provider);
        var declared = new FrameDef
        {
            Name = "Label", Kind = FrameKind.FONTSTRING, Width = 0, Height = 0,
            Visual = new FrameVisual { Text = new TextVisual("Hunts", FontTemplate: "GameFontNormalSmall") },
        };
        var project = new Project { Frames = [declared] };
        var effective = resolver.ApplyEffectiveGeometry(project);
        Assert.Equal((0d, 0d), (project.Frames[0].Width, project.Frames[0].Height));
        Assert.True(effective.Frames[0].Width > 0);
        Assert.Equal(10, effective.Frames[0].Height);
    }

    [Fact]
    public void Index_reload_invalidates_cached_styles_and_increments_generation()
    {
        using var resolver = new StockTemplateResolver(_provider);
        var generation = resolver.Generation;
        Assert.Equal(10, resolver.ResolveFont("GameFontNormalSmall")!.Size);
        Write(@"Interface\FrameXML\Fonts.xml", """
            <Ui><Font name="SystemFont_Shadow_Small" font="Fonts\FRIZQT__.TTF" virtual="true">
              <FontHeight><AbsValue val="11"/></FontHeight>
            </Font></Ui>
            """);
        resolver.Reload();
        Assert.True(resolver.Generation > generation);
        Assert.Equal(11, resolver.ResolveFont("GameFontNormalSmall")!.Size);
    }

    [Fact]
    public void Missing_circular_and_unsupported_definitions_produce_explicit_diagnostics()
    {
        Write(@"Interface\FrameXML\FontStyles.xml", """
            <Ui>
              <Font name="A" inherits="B" virtual="true"/>
              <Font name="B" inherits="A" virtual="true"/>
              <Font name="Multiple" inherits="A,B" virtual="true"/>
              <Font name="Flag" outline="MONOCHROME" virtual="true"/>
            </Ui>
            """);
        using var resolver = new StockTemplateResolver(_provider);
        Assert.Null(resolver.ResolveFont("Absent"));
        Assert.Null(resolver.ResolveFont("A"));
        Assert.Null(resolver.ResolveFont("Multiple"));
        Assert.NotNull(resolver.ResolveFont("Flag"));
        Assert.Contains(resolver.Diagnostics, item => item.Code == "unresolved-stock-font");
        Assert.Contains(resolver.Diagnostics, item => item.Code == "circular-stock-inheritance");
        Assert.Contains(resolver.Diagnostics, item => item.Code == "unsupported-stock-construct");
        Assert.Contains(resolver.Diagnostics, item => item.Code == "unsupported-stock-font-flag");
    }

    [Fact]
    public void Missing_inherited_texture_marks_template_partial_and_is_diagnostic()
    {
        File.Delete(Path.Combine(_temp, "Interface", "Tabs", "Normal.blp"));
        using var resolver = new StockTemplateResolver(_provider);
        var style = Assert.IsType<StockButtonStyle>(resolver.ResolveButton(StockTemplateResolver.TabTemplate));
        Assert.Equal(StockDefinitionStatus.PartiallyResolved, style.Status);
        Assert.Contains(resolver.Diagnostics, item => item.Code == "unresolved-inherited-texture");
    }

    [Fact]
    public void Required_materialization_is_focused_and_machine_local()
    {
        using var resolver = new StockTemplateResolver(_provider);
        var validation = new WowClientValidation(WowClientValidationStatus.Valid, "/client",
            new WowClientBuild(3, 3, 5, 12340), "enUS", [], "valid");
        var results = resolver.MaterializeRequired(validation);
        Assert.Equal(11, results.Count);
        Assert.Contains(results, item => item.RequestedPath == @"Fonts\FRIZQT__.TTF");
        Assert.DoesNotContain(results, item => item.RequestedPath.Contains('*'));
        Assert.All(results, item => Assert.StartsWith(_temp, item.CachePath!, StringComparison.Ordinal));
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine([_temp, .. relative.Replace('\\', '/').Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    private sealed class FakeProvider(string cacheRoot) : IWoWClientAssetProvider
    {
        public string CacheRoot { get; } = cacheRoot;
        public WowClientValidation ValidateClient(string? clientPath) =>
            new(WowClientValidationStatus.NotConfigured, null, null, null, [], "not configured");

        public AssetMaterializationResult Materialize(string reference, WowClientValidation client)
        {
            var path = Path.Combine([CacheRoot, .. reference.Replace('\\', '/').Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
                File.WriteAllBytes(path, [1]);
            return new AssetMaterializationResult(true, reference, path,
                new StockAssetProvenance(reference, "fake.MPQ", client.ClientPath!, client.Build!.ToString(),
                    client.Locale!, 1, "00", DateTimeOffset.UtcNow, path), false, "materialized");
        }

        public StockAssetProvenance? GetProvenance(string physicalPath) => null;
        public void ClearCache() { }
    }
}
