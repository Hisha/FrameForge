using System.Collections.ObjectModel;

namespace FrameForge.Core.Templates;

public enum BlizzardTemplateValueOrigin
{
    Declared,
    InheritedTemplate,
    InheritedDependency,
}

public enum BlizzardButtonState
{
    Normal,
    Pushed,
    Disabled,
    Highlight,
}

public enum BlizzardVisualRegionKind
{
    Texture,
    ButtonText,
}

public enum BlizzardKnownPreviewBehavior
{
    CharacterTabResizeToTextZeroPadding,
}

public sealed record BlizzardSourceProvenance(
    string LogicalPath,
    int Line,
    int Column,
    string Sha256,
    string? ArchivePath = null,
    string? Build = null,
    string? Locale = null);

public sealed record BlizzardPropertyProvenance(
    string Property,
    string Definition,
    BlizzardTemplateValueOrigin Origin,
    BlizzardSourceProvenance Source);

public sealed record BlizzardColor(double Red, double Green, double Blue, double Alpha = 1);

public sealed record BlizzardTexCoords(double Left, double Right, double Top, double Bottom);

public sealed record BlizzardTemplateAnchor(
    string? Point,
    string? RelativeTo,
    string? RelativePoint,
    double OffsetX,
    double OffsetY,
    BlizzardSourceProvenance Source);

public sealed record BlizzardTextureValue(
    string? SymbolicName,
    string? File,
    double? Width,
    double? Height,
    BlizzardTexCoords? TexCoords,
    string? AlphaMode,
    string? Inherits,
    IReadOnlyList<BlizzardTemplateAnchor> Anchors,
    IReadOnlyDictionary<string, BlizzardPropertyProvenance> Provenance,
    BlizzardSourceProvenance Source);

public sealed record BlizzardVisualRegion(
    string SymbolicName,
    BlizzardVisualRegionKind Kind,
    BlizzardButtonState? State,
    string? DrawLayer,
    double? Width,
    double? Height,
    BlizzardTextureValue? Texture,
    IReadOnlyList<BlizzardTemplateAnchor> Anchors,
    BlizzardSourceProvenance Source);

public sealed record BlizzardFontProperties(
    string Name,
    string? Parent,
    string? File,
    double? Height,
    BlizzardColor? Color,
    string? JustifyH,
    string? JustifyV,
    string? Outline,
    double? ShadowX,
    double? ShadowY,
    BlizzardColor? ShadowColor,
    IReadOnlyDictionary<string, BlizzardPropertyProvenance> Provenance,
    BlizzardSourceProvenance Source);

public sealed record BlizzardButtonProperties(
    double? Width,
    double? Height,
    string? NormalFont,
    string? HighlightFont,
    string? DisabledFont,
    IReadOnlyDictionary<BlizzardButtonState, BlizzardTextureValue> StateTextures,
    IReadOnlyList<BlizzardVisualRegion> VisualRegions,
    IReadOnlyList<BlizzardKnownPreviewBehavior> PreviewBehaviors,
    IReadOnlyDictionary<string, BlizzardPropertyProvenance> Provenance)
{
    internal static BlizzardButtonProperties Empty { get; } = new(
        null, null, null, null, null,
        new ReadOnlyDictionary<BlizzardButtonState, BlizzardTextureValue>(
            new Dictionary<BlizzardButtonState, BlizzardTextureValue>()),
        Array.Empty<BlizzardVisualRegion>(),
        Array.Empty<BlizzardKnownPreviewBehavior>(),
        new ReadOnlyDictionary<string, BlizzardPropertyProvenance>(
            new Dictionary<string, BlizzardPropertyProvenance>(StringComparer.Ordinal)));
}

public sealed record BlizzardAssetDependency(
    string LogicalPath,
    bool IsResolved,
    string? PhysicalPath = null,
    string? Sha256 = null,
    string? Diagnostic = null);

public enum BlizzardTemplateDiagnosticSeverity
{
    Warning,
    Error,
}

public sealed record BlizzardTemplateDiagnostic(
    BlizzardTemplateDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Definition = null,
    BlizzardSourceProvenance? Source = null,
    string? Dependency = null);

public sealed record BlizzardTemplateDefinition(
    string Name,
    string NativeType,
    bool IsVirtual,
    IReadOnlyList<string> Parents,
    BlizzardButtonProperties DeclaredProperties,
    BlizzardSourceProvenance Source);

public sealed record BlizzardResolvedTemplate(
    BlizzardTemplateDefinition Definition,
    BlizzardButtonProperties EffectiveProperties,
    IReadOnlyList<string> InheritanceChain,
    IReadOnlyList<BlizzardAssetDependency> AssetDependencies,
    IReadOnlyList<BlizzardTemplateDiagnostic> Diagnostics)
{
    public bool IsResolved => Diagnostics.All(item => item.Severity != BlizzardTemplateDiagnosticSeverity.Error);
}
