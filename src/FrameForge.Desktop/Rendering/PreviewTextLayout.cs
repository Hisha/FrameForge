using Avalonia;

namespace FrameForge.Desktop.Rendering;

/// <summary>
/// Places already-shaped text in canvas space without allowing editor zoom to change shaping.
/// </summary>
/// <remarks>
/// WoW font sizes and advances live in UI units.  The editor viewport is a camera over those
/// units, so zoom must scale a single shaped result rather than asking the host font engine to
/// shape the string again at a different point size.  Re-shaping at <c>size * zoom</c> makes
/// hinting and rounding alter normalized text width, which in turn makes nearby artwork appear to
/// move even though neither object's anchor changed.
/// </remarks>
internal static class PreviewTextLayout
{
    private const string Ellipsis = "...";

    /// <summary>
    /// Mirrors build-12340 FontString overflow using measurements from the resolved client font.
    /// Avalonia still paints the glyphs, but it no longer decides whether the exported text fits.
    /// </summary>
    internal static string Fit(string text, double availableWidth, Func<string, double> measure)
    {
        if (string.IsNullOrEmpty(text) || availableWidth <= 0 || measure(text) <= availableWidth)
            return availableWidth > 0 ? text : string.Empty;
        if (measure(Ellipsis) > availableWidth)
            return string.Empty;

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var candidate = (low + high + 1) / 2;
            if (measure(text[..candidate] + Ellipsis) <= availableWidth)
                low = candidate;
            else
                high = candidate - 1;
        }
        return text[..low] + Ellipsis;
    }

    internal static Rect Place(
        Rect host,
        double shapedWidth,
        double shapedHeight,
        double zoom,
        string? justifyH,
        string? justifyV)
    {
        var width = shapedWidth * zoom;
        var height = shapedHeight * zoom;
        var x = justifyH switch
        {
            "CENTER" => host.X + (host.Width - width) / 2,
            "RIGHT" => host.Right - width,
            _ => host.X,
        };
        var y = justifyV switch
        {
            "TOP" => host.Y,
            "BOTTOM" => host.Bottom - height,
            _ => host.Y + (host.Height - height) / 2,
        };
        return new Rect(x, y, width, height);
    }

    internal static Matrix CanvasTransform(Rect placement, double zoom) =>
        new(zoom, 0, 0, zoom, placement.X, placement.Y);
}
