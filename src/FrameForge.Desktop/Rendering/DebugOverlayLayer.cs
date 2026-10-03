using System.Globalization;
using Avalonia;
using Avalonia.Media;
using FrameForge.Core.Models;
using FrameForge.Core.Viewing;

namespace FrameForge.Desktop.Rendering;

/// <summary>
/// The Debug band: bounds, names, badges, zero-area markers and the unresolved strip.
/// </summary>
/// <remarks>
/// This is Phase 2's diagnostic drawing moved into its own layer. Debug was already the default
/// mode, so its bounds, labels, hidden styling, placeholders, and zero-size diagnostics are kept;
/// what it draws is now decided by <see cref="ViewPolicy"/> instead of by "draw everything". In
/// Debug the policy keeps everything, with the visual-content layer beneath it.
/// </remarks>
public sealed class DebugOverlayLayer : ICanvasLayer
{
    /// <inheritdoc />
    public string Name => "debug-overlay";

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public bool AppliesTo(CanvasViewMode mode) => ViewPolicy.ShowsDebugStructures(mode);

    private static readonly IPen ScreenPen = new Pen(new SolidColorBrush(Color.Parse("#4C8DA8")), 1);
    private static readonly IPen HiddenPen =
        new Pen(new SolidColorBrush(Color.Parse("#6B7C87")), 1, new DashStyle(new double[] { 3, 3 }, 0));
    private static readonly IPen IssuePen = new Pen(new SolidColorBrush(Color.Parse("#E06C6C")), 2);
    private static readonly IBrush LabelForeground = new SolidColorBrush(Color.Parse("#DCE7EE"));
    private static readonly IBrush LabelForegroundSelected = new SolidColorBrush(Color.Parse("#101418"));
    private static readonly IBrush UnresolvedForeground = new SolidColorBrush(Color.Parse("#E06C6C"));
    private static readonly IBrush ChevronForeground = new SolidColorBrush(Color.Parse("#7FB2C9"));
    private static readonly IBrush ZeroSizeBrush = new SolidColorBrush(Color.Parse("#B08A4A"));
    private static readonly IPen ZeroSizePen = new Pen(new SolidColorBrush(Color.Parse("#0B1014")), 1);
    private static readonly IPen PlaceholderPen =
        new Pen(new SolidColorBrush(Color.Parse("#9A7BC8")), 1.5, new DashStyle(new double[] { 6, 4 }, 0));

    /// <inheritdoc />
    public void Render(DrawingContext context, CanvasRenderContext canvas)
    {
        DrawScreen(context, canvas);

        foreach (var frame in canvas.VisibleFrames)
        {
            if (frame.Box is not { } box)
                continue;

            if (!frame.HasArea)
            {
                DrawZeroSizeMarker(context, canvas, frame);
                continue;
            }

            DrawFrame(context, canvas, frame, new Rect(box.X, box.Y, box.Width, box.Height));
        }

        if (canvas.UnresolvedNames.Count > 0)
            DrawUnresolved(context, canvas);
    }

    private static void DrawScreen(DrawingContext context, CanvasRenderContext canvas)
    {
        var box = canvas.ScreenBox;

        context.DrawRectangle(null, ScreenPen, new Rect(box.X, box.Y, box.Width, box.Height));
        Label.Draw(context, "UIParent", new Point(box.X + 6, box.Y + 4), ChevronForeground);
    }

    private static void DrawFrame(
        DrawingContext context,
        CanvasRenderContext canvas,
        DrawableFrame frame,
        Rect canvasRect)
    {
        var looks = ColorFor(frame.Model);
        var pen = frame.Model?.Placeholder ?? false
            ? PlaceholderPen
            : !frame.EffectiveVisible
                ? HiddenPen
                : new Pen(new SolidColorBrush(looks), frame.Model?.Parent is null ? 1.5 : 1);

        if (Filled(frame.Model))
            context.FillRectangle(new SolidColorBrush(looks) { Opacity = 0.18 }, canvasRect);

        context.DrawRectangle(null, pen, canvasRect);

        if (!frame.DrawLabel)
            return;

        var badge = Badge(frame.Model);
        Label.Draw(context,
            badge.Length == 0 ? frame.Name : $"{frame.Name}  [{badge}]",
            new Point(canvasRect.X + 5, canvasRect.Y + 4),
            frame.Selected ? LabelForegroundSelected : LabelForeground);
    }

    /// <summary>
    /// Draws a widget with no area as a labelled cross.
    /// </summary>
    /// <remarks>
    /// Not an edge case: a real FrameXML file is full of them. FontStrings with no
    /// <c>&lt;Size&gt;</c> are sized by Blizzard's font metrics and Buttons that inherit an
    /// undefined template have no size at all. In WoW both still occupy space; here they stay
    /// visible and selectable instead of disappearing into nothing.
    /// </remarks>
    private static void DrawZeroSizeMarker(
        DrawingContext context,
        CanvasRenderContext canvas,
        DrawableFrame frame)
    {
        if (frame.Box is not { } box)
            return;

        const double radius = 5;
        var marker = new Rect(box.X - radius, box.Y - radius, radius * 2, radius * 2);
        var looks = ColorFor(frame.Model);

        context.FillRectangle(ZeroSizeBrush, marker);
        context.DrawRectangle(null, ZeroSizePen, marker);
        context.DrawLine(new Pen(new SolidColorBrush(looks), 1),
            new Point(marker.X - 3, marker.Y + radius), new Point(marker.Right + 3, marker.Y + radius));
        context.DrawLine(new Pen(new SolidColorBrush(looks), 1),
            new Point(marker.X + radius, marker.Y - 3), new Point(marker.X + radius, marker.Bottom + 3));

        Label.Draw(context, $"{frame.Name}  [no size]",
            new Point(marker.Right + 6, marker.Y - 2),
            frame.Selected ? LabelForegroundSelected : LabelForeground);
    }

    /// <summary>
    /// The marker strip for widgets the engine could not place.
    /// </summary>
    /// <remarks>
    /// These cannot be drawn in place, so they are listed rather than silently dropped. The strip
    /// is Debug-only: it is a list of problems, and a Preview has no place for a list of problems.
    /// </remarks>
    private static void DrawUnresolved(DrawingContext context, CanvasRenderContext canvas)
    {
        var names = canvas.UnresolvedNames;
        var y = 24d;

        context.DrawRectangle(null, IssuePen,
            new Rect(12, y - 12, canvas.CanvasSize.Width - 24, 18 * names.Count + 8));

        foreach (var name in names)
            Label.Draw(context, $"{name} - could not be resolved", new Point(18, y), UnresolvedForeground);
    }

    /// <summary>The visual identity of each widget kind.</summary>
    /// <remarks>
    /// An imported FrameXML document is mostly Textures and FontStrings; drawing them all the same
    /// makes a 52-element file look like a handful of overlapping boxes. Three signals tell them
    /// apart at a glance without pretending to be Blizzard's artwork.
    /// </remarks>
    internal static Color ColorFor(FrameDef? frame) => frame is { Placeholder: true }
        ? Color.Parse("#9A7BC8")
        : frame?.Kind switch
        {
            FrameKind.FRAME => Color.Parse("#5C93B0"),
            FrameKind.BUTTON => Color.Parse("#C08A4A"),
            FrameKind.FONTSTRING => Color.Parse("#63C2A0"),
            FrameKind.TEXTURE => Color.Parse("#4A6472"),
            FrameKind.STATUSBAR => Color.Parse("#B08AC4"),
            _ => Color.Parse("#5C93B0"),
        };

    /// <summary>Whether the rectangle is drawn filled for this kind.</summary>
    internal static bool Filled(FrameDef? frame) => frame?.Kind is FrameKind.FONTSTRING or FrameKind.STATUSBAR;

    /// <summary>Short tag beside the name, so a widget is identifiable without reading its kind.</summary>
    internal static string Badge(FrameDef? frame)
    {
        if (frame is null)
            return string.Empty;
        if (frame.Placeholder)
            return "stand-in";

        var kindBadge = frame.Kind.Badge();
        return kindBadge + (frame.SetAllPoints ? " fill" : string.Empty);
    }
}

/// <summary>
/// The one label style the canvas uses, in one place.
/// </summary>
/// <remarks>
/// Every band draws its text through here so a label looks like a label wherever it came from. The
/// opaque backdrop is what makes a name readable on top of a filled widget rather than dissolving
/// into it.
/// </remarks>
internal static class Label
{
    private static readonly Typeface Typeface =
        new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    public static void Draw(DrawingContext context, string text, Point origin, IBrush foreground)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface, 11, foreground);

        var box = new Rect(origin.X, origin.Y, formatted.Width + 6, formatted.Height);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0B1014")), box);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#0B1014")), 1), box);
        context.DrawText(formatted, origin);
    }
}
