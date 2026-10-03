namespace FrameForge.Core.Models;

/// <summary>
/// A normalised RGBA colour, as FrameXML writes it: four separate attributes in 0..1.
/// </summary>
/// <remarks>
/// WoW does not use a single <c>color</c> attribute. A <c>&lt;Color&gt;</c> child element
/// carries <c>r</c>, <c>g</c>, <c>b</c> and <c>a</c>, each 0..1, and they modulate the widget.
/// This is stored raw and un-premultiplied so that what the source said is recoverable.
/// </remarks>
/// <param name="R">Red, 0..1.</param>
/// <param name="G">Green, 0..1.</param>
/// <param name="B">Blue, 0..1.</param>
/// <param name="A">Alpha, 0..1.</param>
public readonly record struct ColorRgba(double R, double G, double B, double A = 1)
{
    /// <summary>Opaque white, which is what WoW uses when no colour is declared.</summary>
    public static ColorRgba White => new(1, 1, 1, 1);

    /// <summary>True when every channel is finite and inside 0..1.</summary>
    public bool IsValid => R is >= 0 and <= 1 && G is >= 0 and <= 1 && B is >= 0 and <= 1 && A is >= 0 and <= 1;
}

/// <summary>
/// The sub-rectangle of a texture file that a widget shows, as 0..1 fractions of the file.
/// </summary>
/// <remarks>
/// WoW textures are often atlases: one file holds many pieces, and <c>&lt;TexCoords&gt;</c>
/// names which piece. NativeHuntsFrame.xml draws ten separate frame borders from the single
/// file <c>Interface\LFGFrame\UI-LFG-FRAME</c> purely by varying these four numbers, so they
/// are load-bearing visual identity rather than an optimisation detail.
/// </remarks>
/// <param name="Left">Left edge, 0..1.</param>
/// <param name="Right">Right edge, 0..1.</param>
/// <param name="Top">Top edge, 0..1 in WoW's texture space, where the top of the file is 0.</param>
/// <param name="Bottom">Bottom edge, 0..1 in WoW's texture space.</param>
public readonly record struct TexCoords(double Left, double Right, double Top, double Bottom)
{
    /// <summary>The whole texture.</summary>
    public static TexCoords Full => new(0, 1, 0, 1);

    /// <summary>True when every edge is finite and inside 0..1.</summary>
    public bool IsValid =>
        Left is >= 0 and <= 1 && Right is >= 0 and <= 1 && Top is >= 0 and <= 1 && Bottom is >= 0 and <= 1;

    /// <summary>True when this is not the whole texture, i.e. the widget shows a sub-rectangle.</summary>
    public bool IsSubRectangle => this != Full;

    /// <summary>Width as a fraction of the file, for sizing a decoded texture.</summary>
    public double Width => Right - Left;

    /// <summary>Height as a fraction of the file.</summary>
    public double Height => Bottom - Top;
}

/// <summary>What a <c>&lt;Texture&gt;</c> says it is made of.</summary>
/// <param name="File">
/// The texture path exactly as written, e.g. <c>Interface\LFGFrame\UI-LFG-FRAME</c>. Null when
/// the element declares no file: NativeHuntsFrame.xml line 6 opens a 326x314 <c>&lt;Texture&gt;</c>
/// whose only visual statement is a <c>&lt;Color&gt;</c>, which in WoW tints whatever its
/// inherited template supplies. Dropping it because there was no path would lose real information.
/// </param>
/// <param name="TexCoords">Which part of the file to show; <see cref="Models.TexCoords.Full"/> when undeclared.</param>
/// <param name="Color">Modulation from a <c>&lt;Color&gt;</c> child, or null when undeclared.</param>
/// <param name="Alpha">The <c>alpha</c> attribute, or null when undeclared.</param>
/// <param name="NormalizeTexCoords">The <c>normalizeTexCoords</c> attribute, or null when undeclared.</param>
/// <param name="BlendMode">The <c>blendMode</c> attribute, or null when undeclared.</param>
public sealed record TextureVisual(
    string? File,
    TexCoords TexCoords = default,
    ColorRgba? Color = null,
    double? Alpha = null,
    bool? NormalizeTexCoords = null,
    string? BlendMode = null)
{
    /// <summary>Effective alpha: the attribute if present, else the colour's, else fully opaque.</summary>
    public double EffectiveAlpha => Alpha ?? Color?.A ?? 1;

    /// <summary>True when the widget is drawn dimmer than the raw texture.</summary>
    public bool IsDimmed => EffectiveAlpha < 1;
}

/// <summary>What a <c>&lt;FontString&gt;</c> says it should say, and how it should be laid out.</summary>
/// <param name="Text">The literal <c>text</c> attribute. Null when the text comes from Lua at runtime.</param>
/// <param name="JustifyH">The <c>justifyH</c> attribute verbatim, e.g. <c>LEFT</c>.</param>
/// <param name="JustifyV">The <c>justifyV</c> attribute verbatim, e.g. <c>TOP</c>.</param>
/// <param name="FontTemplate">
/// The <c>inherits</c> font template, e.g. <c>GameFontHighlightSmall</c>. Unresolved: FrameForge
/// records the reference and does not emulate Blizzard's font atlas.
/// </param>
public sealed record TextVisual(string? Text, string? JustifyH = null, string? JustifyV = null, string? FontTemplate = null)
{
    /// <summary>True when the source wrote a literal string, so its text is knowable now.</summary>
    public bool HasLiteralText => !string.IsNullOrEmpty(Text);

    /// <summary>
    /// True when the text must come from Lua. NativeHuntsFrame.xml has four FontStrings with a
    /// literal <c>text</c> attribute and sixteen whose value is assigned at runtime; FrameForge
    /// must not invent a string for those.
    /// </summary>
    public bool NeedsRuntimeText => !HasLiteralText;

    /// <summary>The horizontal justification, normalized to upper case, or null.</summary>
    public string? JustifyHorizontal => string.IsNullOrWhiteSpace(JustifyH) ? null : JustifyH.Trim().ToUpperInvariant();

    /// <summary>The vertical justification, normalized to upper case, or null.</summary>
    public string? JustifyVertical => string.IsNullOrWhiteSpace(JustifyV) ? null : JustifyV.Trim().ToUpperInvariant();
}

/// <summary>What a <c>&lt;StatusBar&gt;</c> says about its value and its fill.</summary>
/// <param name="MinValue">The <c>minValue</c> attribute.</param>
/// <param name="MaxValue">The <c>maxValue</c> attribute.</param>
/// <param name="DefaultValue">The <c>defaultValue</c> attribute; WoW's value before Lua runs.</param>
/// <param name="BarTexture">The <c>file</c> of the <c>&lt;BarTexture&gt;</c> child, or null.</param>
/// <param name="BarColor">The <c>&lt;BarColor&gt;</c> child's r/g/b, or null.</param>
public sealed record StatusBarVisual(
    double? MinValue = null,
    double? MaxValue = null,
    double? DefaultValue = null,
    string? BarTexture = null,
    ColorRgba? BarColor = null)
{
    /// <summary>
    /// Fill fraction at the authored default value, in 0..1, or null when the range does not
    /// define one. This is what a Preview can honestly draw: not the runtime value, which only
    /// Lua knows, but the value the document itself states.
    /// </summary>
    public double? DefaultFraction
    {
        get
        {
            if (MinValue is not { } min || MaxValue is not { } max || DefaultValue is not { } value)
                return null;

            var span = max - min;
            if (Math.Abs(span) < double.Epsilon)
                return null;

            return Math.Clamp((value - min) / span, 0, 1);
        }
    }
}

/// <summary>
/// Everything FrameForge retained about a widget's VISUAL identity that geometry does not carry.
/// </summary>
/// <remarks>
/// <para>
/// This is the model's forward-facing half. FrameForge's layout engine is complete and tested, so
/// the remaining gap between "the rectangles are right" and "the screen looks like WoW" is
/// entirely paint. Rather than let the importer discard that paint and reconstruct it from
/// documentation later, it is captured here as data.
/// </para>
/// <para>
/// Every member is optional and additive, and every one of them is something a FrameXML file can
/// literally state. Nothing is inferred and nothing is emulated: an unresolved
/// <see cref="TextVisual.FontTemplate"/> stays a string, because pretending to know Blizzard's
/// font metrics would be a worse failure than admitting the gap.
/// </para>
/// <para>
/// It is deliberately a property of ONE widget, not of the project. WoW resolves inherited
/// template properties at runtime; FrameForge keeps the raw per-element facts and leaves
/// resolution to the renderers that will eventually need it.
/// </para>
/// </remarks>
public sealed record FrameVisual
{
    /// <summary>Texture paint, for a <c>&lt;Texture&gt;</c>.</summary>
    public TextureVisual? Texture { get; init; }

    /// <summary>Text content and justification, for a <c>&lt;FontString&gt;</c> or a Button's label.</summary>
    public TextVisual? Text { get; init; }

    /// <summary>Value range and fill, for a <c>&lt;StatusBar&gt;</c>.</summary>
    public StatusBarVisual? StatusBar { get; init; }

    /// <summary>
    /// The <c>drawLayer</c> attribute, which overrides the enclosing <c>&lt;Layer level&gt;</c> for
    /// this widget alone. WoW treats it as a separate concept from the stratum, so it is kept
    /// verbatim rather than folded into <see cref="FrameDef.Stratum"/>.
    /// </summary>
    public string? DrawLayer { get; init; }

    /// <summary>The <c>id</c> attribute, used by Buttons that are members of a tab group.</summary>
    public int? Id { get; init; }

    /// <summary>True when nothing visual was retained, so the property can be omitted entirely.</summary>
    public bool IsEmpty =>
        Texture is null && Text is null && StatusBar is null
        && DrawLayer is null && Id is null;

    /// <summary>Short description for the inspector, or null when there is nothing to say.</summary>
    public string? Describe()
    {
        var lines = new List<string>();

        if (Texture is { } texture)
        {
            if (texture.File is { Length: > 0 } file)
            {
                lines.Add($"texture {file}");
                if (texture.TexCoords.IsSubRectangle)
                {
                    lines.Add($"texCoords {Num(texture.TexCoords.Left)},{Num(texture.TexCoords.Right)} " +
                              $"/ {Num(texture.TexCoords.Top)},{Num(texture.TexCoords.Bottom)}");
                }
            }
            else
            {
                lines.Add("no file (tints its inherited template)");
            }

            if (texture.Color is { } color)
                lines.Add($"color {Num(color.R)},{Num(color.G)},{Num(color.B)} a={Num(color.A)}");

            if (texture.Alpha is { } alpha)
                lines.Add($"alpha {Num(alpha)}");
        }

        if (Text is { } text)
        {
            lines.Add(text.HasLiteralText ? $"text \"{text.Text}\"" : "text set by Lua at runtime");
            if (text.JustifyH is { Length: > 0 } justifyH)
                lines.Add($"justifyH {justifyH}");
            if (text.JustifyV is { Length: > 0 } justifyV)
                lines.Add($"justifyV {justifyV}");
            if (text.FontTemplate is { Length: > 0 } font)
                lines.Add($"font {font} (not resolved)");
        }

        if (StatusBar is { } bar)
        {
            lines.Add($"value {Num(bar.MinValue ?? 0)}..{Num(bar.MaxValue ?? 0)}" +
                      (bar.DefaultValue is { } value ? $" default {Num(value)}" : string.Empty));
            if (bar.BarTexture is { Length: > 0 } barTexture)
                lines.Add($"barTexture {barTexture}");
            if (bar.BarColor is { } barColor)
                lines.Add($"barColor {Num(barColor.R)},{Num(barColor.G)},{Num(barColor.B)}");
        }

        if (DrawLayer is { Length: > 0 } layer)
            lines.Add($"drawLayer {layer}");
        if (Id is { } id)
            lines.Add($"id {id}");

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string Num(double value) =>
        value == Math.Floor(value)
            ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}