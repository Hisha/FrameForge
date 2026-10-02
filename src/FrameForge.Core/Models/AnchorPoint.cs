namespace FrameForge.Core.Models;

/// <summary>
/// The nine World of Warcraft frame anchor points.
/// </summary>
/// <remarks>
/// The enum member names are the canonical WoW spellings and are also the exact
/// strings used in a FrameForge project file, so no separate name table is needed.
/// <para>
/// MODEL COORDINATE SEMANTICS (see docs/PROJECT_FORMAT.md):
/// +X points right, +Y points UP. This matches World of Warcraft exactly and is
/// never mixed with Avalonia/screen pixel coordinates anywhere in the model.
/// </para>
/// </remarks>
public enum AnchorPoint
{
    /// <summary>Left edge, top edge.</summary>
    TOPLEFT,

    /// <summary>Horizontal centre, top edge.</summary>
    TOP,

    /// <summary>Right edge, top edge.</summary>
    TOPRIGHT,

    /// <summary>Left edge, vertical centre.</summary>
    LEFT,

    /// <summary>Centre of the frame.</summary>
    CENTER,

    /// <summary>Right edge, vertical centre.</summary>
    RIGHT,

    /// <summary>Left edge, bottom edge.</summary>
    BOTTOMLEFT,

    /// <summary>Horizontal centre, bottom edge.</summary>
    BOTTOM,

    /// <summary>Right edge, bottom edge.</summary>
    BOTTOMRIGHT,
}

/// <summary>
/// Helpers for <see cref="AnchorPoint"/> that need normalized frame-relative units.
/// </summary>
public static class AnchorPoints
{
    /// <summary>Every anchor point, in the order a picker should offer them.</summary>
    public static readonly AnchorPoint[] All =
    [
        AnchorPoint.TOPLEFT,
        AnchorPoint.TOP,
        AnchorPoint.TOPRIGHT,
        AnchorPoint.LEFT,
        AnchorPoint.CENTER,
        AnchorPoint.RIGHT,
        AnchorPoint.BOTTOMLEFT,
        AnchorPoint.BOTTOM,
        AnchorPoint.BOTTOMRIGHT,
    ];

    /// <summary>
    /// Fractional position of an anchor point inside its own frame:
    /// <c>x</c> 0 = left edge, 1 = right edge; <c>y</c> 0 = bottom edge, 1 = top edge.
    /// </summary>
    public static (double X, double Y) Unit(this AnchorPoint point) => point switch
    {
        AnchorPoint.TOPLEFT => (0d, 1d),
        AnchorPoint.TOP => (0.5d, 1d),
        AnchorPoint.TOPRIGHT => (1d, 1d),
        AnchorPoint.LEFT => (0d, 0.5d),
        AnchorPoint.CENTER => (0.5d, 0.5d),
        AnchorPoint.RIGHT => (1d, 0.5d),
        AnchorPoint.BOTTOMLEFT => (0d, 0d),
        AnchorPoint.BOTTOM => (0.5d, 0d),
        AnchorPoint.BOTTOMRIGHT => (1d, 0d),
        _ => throw new ArgumentOutOfRangeException(nameof(point), point, "Unknown anchor point."),
    };

    /// <summary>Parses a project-file spelling, returning false for anything unknown.</summary>
    public static bool TryParse(string? value, out AnchorPoint point)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.Ordinal))
            {
                point = candidate;
                return true;
            }
        }

        point = default;
        return false;
    }

    /// <summary>The comma-separated list of valid spellings, for error messages.</summary>
    public static string Spelled => string.Join(", ", All);
}