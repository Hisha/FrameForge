using Avalonia;

namespace FrameForge.Desktop.Rendering;

/// <summary>
/// The one place a StatusBar's fill rectangle is computed, so the pixel test suite and the canvas
/// can never disagree about what "fill" means.
/// </summary>
/// <remarks>
/// It is deliberately a pure function of the box and the fraction: the fraction comes from the
/// model's <see cref="FrameForge.Core.Models.StatusBarVisual.DefaultFraction"/>, and the box is the
/// resolved widget rectangle. Everything else about how a bar is painted - track colour, border,
/// texture, tagging - is presentation that lives in <see cref="VisualContentLayer"/>.
/// </remarks>
internal static class StatusBarRendering
{
    /// <summary>
    /// The fill rectangle: the full height of the bar from its left edge, spanning the fraction of
    /// its width. A fraction outside 0..1 is clamped, matching how the model reports it.
    /// </summary>
    public static Avalonia.Rect FillRect(Avalonia.Rect box, double fraction)
    {
        var clamped = Math.Clamp(fraction, 0, 1);
        return new Avalonia.Rect(box.X, box.Y, box.Width * clamped, box.Height);
    }
}