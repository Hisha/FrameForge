using FrameForge.Core.Models;

namespace FrameForge.Desktop.Templates;

/// <summary>A deliberately small authoring catalog demonstrated by the build-12340 client.</summary>
public static class WowTextStyleCatalog
{
    public static readonly IReadOnlyList<string> CuratedNames =
    [
        "GameFontNormal", "GameFontHighlight",
        "GameFontNormalSmall", "GameFontHighlightSmall",
        "GameFontNormalLarge", "GameFontHighlightLarge",
        "GameFontHighlightMedium", "GameFontDisable",
        "GameFontGreen", "GameFontRed", "GameFontNormalHuge",
    ];

    public static IReadOnlyList<StockTextStyleOption> Available(IStockTemplateResolver resolver) =>
        [.. CuratedNames.Select(resolver.ResolveFont).Where(style => style is not null)
            .Select(style => new StockTextStyleOption(style!.Name, style.Size,
                style.Provenance.LastOrDefault()?.Source ?? "Interface\\FrameXML\\FontStyles.xml"))];
}

public sealed record StockTextStyleOption(string Name, double Size, string Source)
{
    public override string ToString() => Name;
}

public sealed record EffectiveDesignTextStyle(
    string? BaseStyle,
    StockFontStyle? Style,
    IReadOnlyList<string> Overrides)
{
    public bool IsResolved => Style is not null;
}

public static class DesignTextStyleResolver
{
    public static EffectiveDesignTextStyle Resolve(
        FrameDef frame, DesignObjectMetadata? design, IStockTemplateResolver stock)
    {
        var metadata = design?.TextStyle;
        var baseName = metadata?.BaseStyle ?? frame.Visual?.Text?.FontTemplate;
        var baseStyle = stock.ResolveFont(baseName);
        if (baseStyle is null)
            return new EffectiveDesignTextStyle(baseName, null, []);

        var overrides = new List<string>();
        var effective = baseStyle;
        if (metadata?.Size is { } size) { effective = effective with { Size = size }; overrides.Add("size"); }
        if (metadata?.Color is { } color) { effective = effective with { Color = color }; overrides.Add("color"); }
        if (metadata?.Outline is { } outline)
        {
            effective = effective with { Outline = outline == "NONE" ? null : outline };
            overrides.Add("outline");
        }
        if (metadata?.Shadow is { } shadow)
        {
            effective = shadow
                ? effective with
                {
                    ShadowX = effective.ShadowColor is null ? 1 : effective.ShadowX,
                    ShadowY = effective.ShadowColor is null ? -1 : effective.ShadowY,
                    ShadowColor = effective.ShadowColor ?? new ColorRgba(0, 0, 0, 1),
                }
                : effective with { ShadowColor = null };
            overrides.Add("shadow");
        }
        var declaredAlignment = frame.Visual?.Text?.JustifyH;
        if (metadata?.JustifyH is { } justify)
        {
            effective = effective with { JustifyH = justify };
            overrides.Add("horizontal alignment");
        }
        else if (declaredAlignment is { Length: > 0 })
        {
            effective = effective with { JustifyH = declaredAlignment };
        }
        return new EffectiveDesignTextStyle(baseName, effective, overrides);
    }
}
