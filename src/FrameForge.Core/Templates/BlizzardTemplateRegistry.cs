using System.Collections.ObjectModel;

namespace FrameForge.Core.Templates;

/// <summary>
/// Immutable build-12340 registry for the deliberately small approved template set.
/// It contains no editor, v1 project, or rendering types.
/// </summary>
public sealed class BlizzardTemplateRegistry
{
    public const string Product = "wow-3.3.5a";
    public const int Build = 12340;

    public static IReadOnlyList<string> ApprovedTemplates { get; } = Array.AsReadOnly(
    [
        "UIPanelButtonTemplate",
        "GameMenuButtonTemplate",
        "CharacterFrameTabButtonTemplate",
    ]);

    private readonly IReadOnlyDictionary<string, BlizzardResolvedTemplate> _templates;
    private readonly IReadOnlyDictionary<string, BlizzardFontProperties> _fonts;

    internal BlizzardTemplateRegistry(
        IDictionary<string, BlizzardResolvedTemplate> templates,
        IDictionary<string, BlizzardFontProperties> fonts,
        IEnumerable<BlizzardTemplateDiagnostic> diagnostics)
    {
        _templates = new ReadOnlyDictionary<string, BlizzardResolvedTemplate>(
            new Dictionary<string, BlizzardResolvedTemplate>(templates, StringComparer.Ordinal));
        _fonts = new ReadOnlyDictionary<string, BlizzardFontProperties>(
            new Dictionary<string, BlizzardFontProperties>(fonts, StringComparer.Ordinal));
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public IReadOnlyDictionary<string, BlizzardResolvedTemplate> Templates => _templates;
    public IReadOnlyDictionary<string, BlizzardFontProperties> Fonts => _fonts;
    public IReadOnlyList<BlizzardTemplateDiagnostic> Diagnostics { get; }

    public BlizzardResolvedTemplate? Resolve(string? name) =>
        name is not null && _templates.TryGetValue(name, out var template) ? template : null;

    public BlizzardFontProperties? ResolveFont(string? name) =>
        name is not null && _fonts.TryGetValue(name, out var font) ? font : null;
}
