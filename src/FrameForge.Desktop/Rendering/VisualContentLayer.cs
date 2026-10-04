using System.Globalization;
using Avalonia;
using Avalonia.Media;
using FrameForge.Core.Models;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Assets;

namespace FrameForge.Desktop.Rendering;

/// <summary>
/// The Preview band: what a widget's retained visual facts say it looks like.
/// </summary>
/// <remarks>
/// <para>
/// This layer draws decoded TGA artwork when the desktop asset service can resolve it. It does not
/// decode BLP or emulate Blizzard's fonts, so unresolved visuals retain an explicit stand-in. It can
/// also draw the part of the appearance
/// the geometry and the retained values actually determine: the rectangle, the declared colour and
/// alpha, a literal text string, and a status bar's declared fill.
/// </para>
/// <para>
/// A resolved texture is drawn from its decoded pixels. Otherwise it is a neutral surface tinted by
/// its own <c>&lt;Color&gt;</c> at its own alpha. A literal FontString is drawn with that literal string,
/// and a StatusBar is filled to the fraction its declared <c>defaultValue</c> implies. Every fallback
/// is derived from a value the document stated, never from a guess at what the file probably meant.
/// </para>
/// <para>
/// A FontString whose text comes from Lua at runtime is drawn as a ruled placeholder with no
/// invented words in it. Thirteen of NativeHunts' twenty font strings are in that position, so
/// making one up would have been the single most misleading thing this layer could do.
/// </para>
/// </remarks>
public sealed class VisualContentLayer : ICanvasLayer
{
    /// <inheritdoc />
    public string Name => "visual-content";

    /// <inheritdoc />
    public int Order => 0;

    /// <summary>A declared colour at full strength.</summary>
    private static IBrush Tint(ColorRgba color) => new SolidColorBrush(Color.FromArgb(
        (byte)Math.Round(Math.Clamp(color.A, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(color.R, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(color.G, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(color.B, 0, 1) * 255)));

    /// <summary>The neutral surface an un-decodeable texture is shown as.</summary>
    private static IBrush TextureSurface(ColorRgba? declared) =>
        declared is { } color ? Tint(color with { A = 1 }) : new SolidColorBrush(Color.Parse("#3C4A52"));

    /// <inheritdoc />
    public void Render(DrawingContext context, CanvasRenderContext canvas)
    {
        canvas.Diagnostics.VisualContentExecuted = true;

        foreach (var frame in canvas.VisibleFrames)
        {
            if (frame.Box is not { } box || !frame.HasArea)
                continue;

            var rect = new Rect(box.X, box.Y, box.Width, box.Height);
            var visual = frame.Model?.Visual;

            switch (frame.Model?.Kind)
            {
                case FrameKind.TEXTURE:
                    DrawTexture(context, canvas, frame, rect, visual);
                    canvas.Diagnostics.AttemptVisual(FrameKind.TEXTURE,
                        visual?.Texture is { File: null } ? 3 : 2);
                    break;
                case FrameKind.FONTSTRING:
                    DrawFontString(context, canvas, frame, rect, visual?.Text);
                    canvas.Diagnostics.AttemptVisual(FrameKind.FONTSTRING,
                        visual?.Text is { HasLiteralText: true } ? 1 : 2);
                    break;
                case FrameKind.BUTTON:
                    DrawButton(context, canvas, frame, rect, visual?.Text);
                    canvas.Diagnostics.AttemptVisual(FrameKind.BUTTON,
                        visual?.Text is { HasLiteralText: true } ? 3 : 2);
                    break;
                case FrameKind.STATUSBAR:
                    DrawStatusBar(context, canvas, frame, rect, visual?.StatusBar);
                    canvas.Diagnostics.AttemptVisual(FrameKind.STATUSBAR,
                        2
                        + (visual?.StatusBar is { MinValue: not null, MaxValue: not null } ? 1 : 0)
                        + (visual?.StatusBar?.DefaultFraction is > 0 ? 1 : 0));
                    break;
            }
        }
    }

    /// <summary>
    /// A decoded texture, or the restrained Phase 3 stand-in when resolution cannot render it.
    /// </summary>
    /// <remarks>
    /// TexCoords map directly to the decoded image's source rectangle; invalid/reversed coordinates
    /// deliberately fall back rather than silently producing a misleading crop.
    /// </remarks>
    private static void DrawTexture(
        DrawingContext context,
        CanvasRenderContext canvas,
        DrawableFrame frame,
        Rect rect,
        FrameVisual? visual)
    {
        var texture = visual?.Texture;
        var declared = texture?.Color;
        var alpha = (texture?.Alpha ?? 1) * (texture?.Color?.A ?? 1);

        if (texture?.File is { } reference && canvas.AssetResolver is { } resolver)
        {
            var asset = resolver.Resolve(reference);
            if (asset.CanRender && asset.Texture is { } decoded)
            {
                try
                {
                    var source = TextureSourceRect.Map(texture.TexCoords, decoded.Image.Width, decoded.Image.Height);
                    using (context.PushOpacity(Math.Clamp(alpha, 0, 1)))
                        context.DrawImage(decoded.BitmapFor(declared), source, rect);
                    return;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // The inspector exposes the invalid values; the fallback makes the failure
                    // visible without putting diagnostic prose across Preview.
                }
            }
        }

        var fill = TextureSurface(declared);
        fill = fill is SolidColorBrush solid ? new SolidColorBrush(solid.Color) { Opacity = alpha } : fill;

        context.FillRectangle(fill, rect);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#8FA6B4")) { Opacity = 0.55 }, 1), rect);

        // A texture with no file is tinting an inherited template, which is a materially different
        // state from one that names its own. Saying so on the canvas is more useful than another
        // grey box.
        if (texture is { File: null })
            DrawTag(context, rect, "tints template", Color.Parse("#C9A227"));
    }

    /// <summary>
    /// A FontString as its literal text, or as a ruled band when Lua supplies the text at runtime.
    /// </summary>
    private static void DrawFontString(
        DrawingContext context,
        CanvasRenderContext canvas,
        DrawableFrame frame,
        Rect rect,
        TextVisual? text)
    {
        if (text is { HasLiteralText: true })
        {
            DrawClippedText(context, rect, text.Text!, text.JustifyHorizontal, Color.Parse("#E8F1F5"));
            return;
        }

        // No literal. Draw a band that reads as "something goes here" without inventing what.
        context.FillRectangle(new SolidColorBrush(Color.Parse("#63C2A0")) { Opacity = 0.10 }, rect);
        context.DrawRectangle(null,
            new Pen(new SolidColorBrush(Color.Parse("#63C2A0")) { Opacity = 0.55 }, 1,
                new DashStyle(new double[] { 4, 3 }, 0)), rect);
    }

    /// <summary>A Button as a surface carrying its declared label.</summary>
    private static void DrawButton(
        DrawingContext context,
        CanvasRenderContext canvas,
        DrawableFrame frame,
        Rect rect,
        TextVisual? text)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#C08A4A")) { Opacity = 0.16 }, rect);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#C08A4A")) { Opacity = 0.8 }, 1), rect);

        if (text is { HasLiteralText: true })
            DrawClippedText(context, rect, text.Text!, text.JustifyHorizontal, Color.Parse("#F0DCC0"));
    }

    /// <summary>
    /// A StatusBar as its track plus a fill at the fraction its declared default implies.
    /// </summary>
    /// <remarks>
    /// The fill is the <c>defaultValue</c> the document stated, not the runtime value - which only
    /// Lua knows - and not a made-up midpoint. NativeHunts' bar declares 0 of 0..100, so this draws
    /// it empty, which is what the file actually says. The bar colour is the declared
    /// <c>&lt;BarColor&gt;</c>.
    /// </remarks>
    private static void DrawStatusBar(
        DrawingContext context,
        CanvasRenderContext canvas,
        DrawableFrame frame,
        Rect rect,
        StatusBarVisual? bar)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#1B242B")), rect);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#54636D")), 1), rect);

        // The declared range identifies an empty authored bar as a bar rather than a missing
        // renderer, so it belongs on the track even when the default fraction is zero.
        if (bar?.MinValue is { } min && bar.MaxValue is { } max)
            DrawTag(context, rect, $"{Num(min)}-{Num(max)}", Color.Parse("#8FA6B4"));

        if (bar?.DefaultFraction is not { } fraction || fraction <= 0)
            return;

        var fillRect = new Rect(rect.X, rect.Y, rect.Width * fraction, rect.Height);
        if (bar.BarTexture is { } reference && canvas.AssetResolver?.Resolve(reference) is
            { CanRender: true, Texture: { } decoded })
        {
            var source = new Rect(0, 0, decoded.Image.Width, decoded.Image.Height);
            var opacity = Math.Clamp(bar.BarColor?.A ?? 1, 0, 1);
            using (context.PushOpacity(opacity))
                context.DrawImage(decoded.BitmapFor(bar.BarColor), source, fillRect);
            return;
        }

        var fill = bar.BarColor is { } declared
            ? Tint(declared)
            : new SolidColorBrush(Color.Parse("#7FB2C9"));

        context.FillRectangle(fill, fillRect);

    }

    /// <summary>
    /// A small note tucked into the bottom-right of a widget, for a declared fact worth surfacing.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <c>Label</c>: a debug label is an opaque boxed name, and inside a
    /// Preview stand-in an opaque box would read as part of the artwork. This is plain, small, and
    /// only carries values the document actually stated.
    /// </remarks>
    private static void DrawTag(DrawingContext context, Rect host, string text, Color color)
    {
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal), 9,
            new SolidColorBrush(color));

        context.DrawText(label, new Point(
            Math.Max(host.X + 2, host.Right - label.Width - 4),
            Math.Max(host.Y, host.Bottom - label.Height - 2)));
    }

    /// <summary>
    /// Draws a literal string inside its widget's box, justified the way the document asked.
    /// </summary>
    /// <remarks>
    /// No wrapping, no ellipsis and no font substitution pretending to be GameFont. The text is
    /// drawn at a fixed readable size and simply clipped by the canvas, because the alternative -
    /// measuring it against a font FrameForge does not have - would invent line breaks.
    /// </remarks>
    private static void DrawClippedText(
        DrawingContext context,
        Rect host,
        string text,
        string? justifyH,
        Color color)
    {
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal), 11,
            new SolidColorBrush(color));

        var x = justifyH switch
        {
            "CENTER" => host.X + Math.Max(0, (host.Width - label.Width) / 2),
            "RIGHT" => host.X + Math.Max(0, host.Width - label.Width),
            _ => host.X,
        };

        context.DrawText(label, new Point(x, host.Y + Math.Max(0, (host.Height - label.Height) / 2)));
    }

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
