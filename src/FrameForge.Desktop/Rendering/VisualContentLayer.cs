using System.Globalization;
using Avalonia;
using Avalonia.Media;
using FrameForge.Core.Models;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Preview;
using FrameForge.Desktop.Templates;

namespace FrameForge.Desktop.Rendering;

/// <summary>
/// The Preview band: what a widget's retained visual facts say it looks like.
/// </summary>
/// <remarks>
/// <para>
/// This layer draws decoded TGA/BLP artwork when the desktop asset service can resolve it and uses
/// the user's locally materialized stock font when the focused build-12340 resolver provides it.
/// Unresolved visuals retain an explicit stand-in. It can also draw the part of the appearance
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
            var design = canvas.Project?.Editor.DesignObjectFor(frame.Name);
            var effective = canvas.StockTemplates is { } stock
                ? DesignTextStyleResolver.Resolve(frame.Model!, design, stock)
                : null;
            if (effective?.Style is { } style)
                DrawStyledText(context, canvas, rect, text.Text!, style.JustifyH, text.JustifyVertical, style,
                    canvas.PreviewOverrides?.Find(frame.Name)?.TextColor);
            else
                DrawClippedText(context, rect, text.Text!, text.JustifyHorizontal, text.JustifyVertical,
                    Color.Parse("#E8F1F5"));
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
        var stock = canvas.StockTemplates?.ResolveButton(frame.Model?.Inherits);
        var previewButton = canvas.PreviewOverrides?.Find(frame.Name)?.ButtonState ?? PreviewButtonState.Normal;
        var drewStock = stock is not null && DrawStockButton(context, canvas, rect, stock, previewButton);
        if (!drewStock)
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#C08A4A")) { Opacity = 0.16 }, rect);
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#C08A4A")) { Opacity = 0.8 }, 1), rect);
        }

        if (text is { HasLiteralText: true })
        {
            var fontName = previewButton == PreviewButtonState.Selected ? stock?.SelectedFontStyle : stock?.FontStyle;
            if (stock is not null && canvas.StockTemplates?.ResolveFont(fontName) is { } style)
                DrawStyledText(context, canvas,
                    rect.Translate(new Vector(stock.TextOffsetX * canvas.Viewport.Zoom,
                        -stock.TextOffsetY * canvas.Viewport.Zoom)),
                    text.Text!, "CENTER", "MIDDLE", style);
            else
                DrawClippedText(context, rect, text.Text!, text.JustifyHorizontal, text.JustifyVertical,
                    Color.Parse("#F0DCC0"));
        }
    }

    private static bool DrawStockButton(
        DrawingContext context,
        CanvasRenderContext canvas,
        Rect rect,
        StockButtonStyle style,
        PreviewButtonState buttonState)
    {
        var slices = buttonState == PreviewButtonState.Selected ? style.DisabledSlices : style.NormalSlices;
        if (canvas.AssetResolver is not { } resolver || slices.Count != 3)
            return false;
        var ordered = new[]
        {
            slices.FirstOrDefault(slice => slice.Name.EndsWith("Left", StringComparison.Ordinal)
                                                || slice.Name.EndsWith("LeftDisabled", StringComparison.Ordinal)),
            slices.FirstOrDefault(slice => slice.Name.EndsWith("Middle", StringComparison.Ordinal)
                                                || slice.Name.EndsWith("MiddleDisabled", StringComparison.Ordinal)),
            slices.FirstOrDefault(slice => slice.Name.EndsWith("Right", StringComparison.Ordinal)
                                                || slice.Name.EndsWith("RightDisabled", StringComparison.Ordinal)),
        };
        if (ordered.Any(slice => slice is null))
            return false;
        var asset = resolver.Resolve(ordered[0]!.File);
        if (!asset.CanRender || asset.Texture is not { } decoded)
            return false;

        var side = Math.Min(rect.Width / 2, ordered[0]!.Width * canvas.Viewport.Zoom);
        var offsetX = ordered[0]!.OffsetX * canvas.Viewport.Zoom;
        var offsetY = -ordered[0]!.OffsetY * canvas.Viewport.Zoom;
        var destinations = new[]
        {
            new Rect(rect.X + offsetX, rect.Y + offsetY, side, rect.Height),
            new Rect(rect.X + side + offsetX, rect.Y + offsetY, Math.Max(0, rect.Width - side * 2), rect.Height),
            new Rect(rect.Right - side + offsetX, rect.Y + offsetY, side, rect.Height),
        };
        for (var i = 0; i < 3; i++)
        {
            var source = TextureSourceRect.Map(ordered[i]!.TexCoords, decoded.Image.Width, decoded.Image.Height);
            context.DrawImage(decoded.Bitmap, source, destinations[i]);
        }
        return true;
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
    /// No wrapping or ellipsis is invented when the stock font is unavailable. The fallback text
    /// is drawn at a fixed readable size and clipped by the canvas.
    /// </remarks>
    private static void DrawClippedText(
        DrawingContext context,
        Rect host,
        string text,
        string? justifyH,
        string? justifyV,
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

        var y = justifyV switch
        {
            "TOP" => host.Y,
            "BOTTOM" => host.Bottom - label.Height,
            _ => host.Y + Math.Max(0, (host.Height - label.Height) / 2),
        };
        using (context.PushClip(host))
            context.DrawText(label, new Point(x, y));
    }

    private static void DrawStyledText(
        DrawingContext context,
        CanvasRenderContext canvas,
        Rect host,
        string text,
        string? declaredJustifyH,
        string? declaredJustifyV,
        StockFontStyle style,
        ColorRgba? previewColor = null)
    {
        var size = Math.Max(1, style.Size * canvas.Viewport.Zoom);
        var color = ToColor(previewColor ?? style.Color);
        var typeface = new Typeface(style.AvaloniaFamily, FontStyle.Normal, FontWeight.Normal);
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, size, new SolidColorBrush(color));
        var justifyH = declaredJustifyH ?? style.JustifyH;
        var justifyV = declaredJustifyV ?? style.JustifyV;
        var x = justifyH switch
        {
            "CENTER" => host.X + (host.Width - label.Width) / 2,
            "RIGHT" => host.Right - label.Width,
            _ => host.X,
        };
        var y = justifyV switch
        {
            "TOP" => host.Y,
            "BOTTOM" => host.Bottom - label.Height,
            _ => host.Y + (host.Height - label.Height) / 2,
        };
        using (context.PushClip(host))
        {
            if (style.ShadowColor is { } shadow)
            {
                var shadowText = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, size, new SolidColorBrush(ToColor(shadow)));
                context.DrawText(shadowText, new Point(
                    x + style.ShadowX * canvas.Viewport.Zoom,
                    y - style.ShadowY * canvas.Viewport.Zoom));
            }
            if (style.Outline is "NORMAL" or "THICK")
            {
                var radius = style.Outline == "THICK" ? 2d : 1d;
                var outline = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, size, Brushes.Black);
                foreach (var offset in new[] { new Point(-radius, 0), new Point(radius, 0), new Point(0, -radius), new Point(0, radius) })
                    context.DrawText(outline, new Point(x + offset.X, y + offset.Y));
            }
            context.DrawText(label, new Point(x, y));
        }
    }

    private static Color ToColor(ColorRgba color) => Color.FromArgb(
        (byte)Math.Round(Math.Clamp(color.A, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(color.R, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(color.G, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(color.B, 0, 1) * 255));

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
