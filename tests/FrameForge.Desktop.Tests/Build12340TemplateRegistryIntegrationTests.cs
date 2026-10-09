using FrameForge.Core.Templates;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Templates;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class Build12340TemplateRegistryIntegrationTests
{
    [Fact]
    public void Explicit_installed_client_verifies_approved_template_registry_when_requested()
    {
        var clientRoot = Environment.GetEnvironmentVariable("FRAMEFORGE_WOW_CLIENT");
        var cacheRoot = Environment.GetEnvironmentVariable("FRAMEFORGE_WOW_CACHE_ROOT");
        if (string.IsNullOrWhiteSpace(clientRoot) || string.IsNullOrWhiteSpace(cacheRoot))
            return;

        var provider = new WoWClientAssetProvider(cacheRoot);
        var validation = provider.ValidateClient(clientRoot);
        Assert.True(validation.IsValid, validation.Message);
        Assert.Equal(12340, validation.Build?.Build);

        var registry = new Build12340TemplateRegistryLoader(provider).Load(validation);

        Assert.Equal(BlizzardTemplateRegistry.ApprovedTemplates.OrderBy(item => item),
            registry.Templates.Keys.OrderBy(item => item));
        Assert.All(registry.Templates.Values, template => Assert.True(template.IsResolved,
            string.Join(Environment.NewLine, template.Diagnostics.Select(item => item.Message))));
        Assert.DoesNotContain(registry.Diagnostics,
            item => item.Severity == BlizzardTemplateDiagnosticSeverity.Error);

        var panel = registry.Resolve("UIPanelButtonTemplate")!;
        Assert.Equal(18, panel.Definition.Source.Line);
        Assert.Equal("08aabf325cdb0c1ef82d029e2bff7793d8d28b80ae3250dd35c817adbd751b00",
            panel.Definition.Source.Sha256);
        Assert.Equal(4, panel.EffectiveProperties.StateTextures.Count);
        Assert.Equal("GameFontNormal", panel.EffectiveProperties.NormalFont);

        var menu = registry.Resolve("GameMenuButtonTemplate")!;
        Assert.Equal(603, menu.Definition.Source.Line);
        Assert.Equal(["UIPanelButtonTemplate", "GameMenuButtonTemplate"], menu.InheritanceChain);
        Assert.Equal((144d, 21d),
            (menu.EffectiveProperties.Width, menu.EffectiveProperties.Height));
        Assert.Equal("GameFontHighlight", menu.EffectiveProperties.NormalFont);

        var characterTab = registry.Resolve("CharacterFrameTabButtonTemplate")!;
        Assert.Equal(3, characterTab.Definition.Source.Line);
        Assert.Equal("0237e89c8e4b0f4f5d2ed1e4e2d0ceeb0e3ab22882974774cbb6fb0d1e70e916",
            characterTab.Definition.Source.Sha256);
        Assert.Equal((10d, 32d),
            (characterTab.EffectiveProperties.Width, characterTab.EffectiveProperties.Height));
        Assert.Contains(BlizzardKnownPreviewBehavior.CharacterTabResizeToTextZeroPadding,
            characterTab.EffectiveProperties.PreviewBehaviors);
        Assert.Contains(characterTab.EffectiveProperties.VisualRegions,
            region => region.SymbolicName.Contains("$parent", StringComparison.Ordinal));

        var systemFont = registry.ResolveFont("SystemFont_Shadow_Med1");
        Assert.NotNull(systemFont);
        Assert.Equal("5dedf8fd311607915048e2c3d0e37201f755c146b69f2b05a0096fb26c87bfc3",
            systemFont.Source.Sha256);
        Assert.Equal("b82816657f61a4b66a2fd7242b82e23b332e5eb19cd175533ecef5a65fa5aec7",
            registry.ResolveFont("GameFontNormal")!.Source.Sha256);
        Assert.DoesNotContain(registry.Diagnostics,
            item => item.Code == "unexpected-template-source-hash");

        Assert.All(registry.Templates.Values.SelectMany(item => item.AssetDependencies),
            dependency => Assert.True(dependency.IsResolved,
                $"{dependency.LogicalPath}: {dependency.Diagnostic}"));
    }
}
