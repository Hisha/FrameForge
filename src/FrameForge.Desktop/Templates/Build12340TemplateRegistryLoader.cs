using FrameForge.Core.Templates;
using FrameForge.Desktop.Assets;

namespace FrameForge.Desktop.Templates;

/// <summary>
/// Materializes the exact approved template dependency closure from a validated build-12340 client,
/// then hands immutable source snapshots to the Core parser.
/// </summary>
public interface IBuild12340TemplateRegistryLoader
{
    BlizzardTemplateRegistry Load(WowClientValidation client);
}

public sealed class Build12340TemplateRegistryLoader(IWoWClientAssetProvider assets) : IBuild12340TemplateRegistryLoader
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedSourceHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"Interface\FrameXML\UIPanelTemplates.xml"] = "08aabf325cdb0c1ef82d029e2bff7793d8d28b80ae3250dd35c817adbd751b00",
            [@"Interface\FrameXML\CharacterFrameTemplates.xml"] = "0237e89c8e4b0f4f5d2ed1e4e2d0ceeb0e3ab22882974774cbb6fb0d1e70e916",
            [@"Interface\FrameXML\Fonts.xml"] = "5dedf8fd311607915048e2c3d0e37201f755c146b69f2b05a0096fb26c87bfc3",
            [@"Interface\FrameXML\FontStyles.xml"] = "b82816657f61a4b66a2fd7242b82e23b332e5eb19cd175533ecef5a65fa5aec7",
        };

    private static readonly string[] RequiredAssets =
    [
        @"Interface\Buttons\UI-Panel-Button-Up",
        @"Interface\Buttons\UI-Panel-Button-Down",
        @"Interface\Buttons\UI-Panel-Button-Disabled",
        @"Interface\Buttons\UI-Panel-Button-Highlight",
        @"Interface\PaperDollInfoFrame\UI-Character-InactiveTab",
        @"Interface\PaperDollInfoFrame\UI-Character-ActiveTab",
        @"Interface\PaperDollInfoFrame\UI-Character-Tab-Highlight",
        @"Fonts\FRIZQT__.TTF",
    ];

    public BlizzardTemplateRegistry Load(WowClientValidation client)
    {
        var sources = new List<BlizzardTemplateXmlSource>();
        var resolvedAssets = new List<BlizzardAssetDependency>();
        var diagnostics = new List<BlizzardTemplateDiagnostic>();

        foreach (var item in ExpectedSourceHashes)
        {
            var result = assets.Materialize(item.Key, client);
            if (!result.Success || result.CachePath is null)
            {
                diagnostics.Add(new BlizzardTemplateDiagnostic(BlizzardTemplateDiagnosticSeverity.Error,
                    "missing-template-source", $"Could not materialize '{item.Key}': {result.Message}",
                    Dependency: item.Key));
                continue;
            }

            string xml;
            try
            {
                xml = File.ReadAllText(result.CachePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new BlizzardTemplateDiagnostic(BlizzardTemplateDiagnosticSeverity.Error,
                    "unreadable-template-source", $"Could not read '{item.Key}': {ex.Message}",
                    Dependency: item.Key));
                continue;
            }

            var provenance = result.Provenance ?? assets.GetProvenance(result.CachePath);
            var actualHash = provenance?.Sha256 ?? BlizzardTemplateHash.Sha256(xml);
            if (!string.Equals(actualHash, item.Value, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new BlizzardTemplateDiagnostic(BlizzardTemplateDiagnosticSeverity.Warning,
                    "unexpected-template-source-hash",
                    $"'{item.Key}' has SHA-256 {actualHash}; the audited build-12340 source hash is {item.Value}. The definition will be parsed and validated semantically.",
                    Source: new BlizzardSourceProvenance(item.Key, 0, 0, actualHash,
                        provenance?.ArchivePath, provenance?.Build, provenance?.Locale)));
            }
            sources.Add(new BlizzardTemplateXmlSource(item.Key, xml, provenance?.ArchivePath,
                provenance?.Build ?? client.Build?.ToString(), provenance?.Locale ?? client.Locale, actualHash));
        }

        foreach (var reference in RequiredAssets)
        {
            var result = assets.Materialize(reference, client);
            resolvedAssets.Add(new BlizzardAssetDependency(reference, result.Success, result.CachePath,
                result.Provenance?.Sha256, result.Success ? null : result.Message));
        }

        return BlizzardTemplateParser.Parse(sources, resolvedAssets, diagnostics);
    }
}

internal static class BlizzardTemplateHash
{
    public static string Sha256(string text) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
