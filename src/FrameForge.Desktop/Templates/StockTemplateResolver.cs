using System.Globalization;
using System.Xml.Linq;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using FrameForge.Core.Models;
using FrameForge.Desktop.Assets;
using SkiaSharp;

namespace FrameForge.Desktop.Templates;

public enum StockDefinitionStatus { FullyResolved, PartiallyResolved, Unresolved }

public sealed record StockDefinitionDiagnostic(
    StockDefinitionStatus Status,
    string Code,
    string Message,
    string? Definition = null,
    string? Source = null);

public sealed record StockPropertyProvenance(string Property, string Definition, string Source);

public sealed record StockFontStyle(
    string Name,
    string? FontReference,
    string? PhysicalFontPath,
    string? FontFamilyName,
    double Size,
    ColorRgba Color,
    string JustifyH,
    string JustifyV,
    string? Outline,
    double ShadowX,
    double ShadowY,
    ColorRgba? ShadowColor,
    IReadOnlyList<StockPropertyProvenance> Provenance)
{
    public FontFamily AvaloniaFamily => PhysicalFontPath is { Length: > 0 } && FontFamilyName is { Length: > 0 }
        ? new FontFamily($"fonts:FrameForgeWoW#{FontFamilyName}")
        : FontFamily.Default;
}

public sealed record StockTextureSlice(
    string Name,
    string File,
    double Width,
    double Height,
    TexCoords TexCoords,
    string Source);

public sealed record StockButtonStyle(
    string Name,
    double DeclaredWidth,
    double Height,
    string FontStyle,
    double TextOffsetX,
    double TextOffsetY,
    IReadOnlyList<StockTextureSlice> NormalSlices,
    IReadOnlyList<StockTextureSlice> DisabledSlices,
    string? HighlightTexture,
    IReadOnlyList<StockPropertyProvenance> Provenance,
    StockDefinitionStatus Status);

public sealed record StockExternalFrameStyle(
    string Name,
    double Width,
    double Height,
    string Source,
    StockDefinitionStatus Status,
    string ScopeNote);

public interface IStockTemplateResolver
{
    int Generation { get; }
    IReadOnlyList<StockDefinitionDiagnostic> Diagnostics { get; }
    IReadOnlyList<AssetMaterializationResult> MaterializeRequired(WowClientValidation client);
    void Reload();
    Project ApplyEffectiveGeometry(Project declaredProject);
    StockFontStyle? ResolveFont(string? name);
    StockButtonStyle? ResolveButton(string? name);
    StockExternalFrameStyle? ResolveExternalFrame(string? name);
    IReadOnlyList<string> Describe(FrameDef frame);
    IReadOnlyList<string> ExpandedChildNames(string instanceName, StockButtonStyle style);
    double MeasureText(StockFontStyle style, string text);
}

/// <summary>
/// Build-12340 stock-definition index for the deliberately small Native Hunts dependency set.
/// Definitions stay in the Phase 5A cache and are never copied into a project.
/// </summary>
public sealed class StockTemplateResolver : IStockTemplateResolver, IDisposable
{
    public const string TabTemplate = "CharacterFrameTabButtonTemplate";
    public const string LfdParentFrame = "LFDParentFrame";
    private static readonly string[] RequiredResources =
    [
        @"Interface\FrameXML\LFDFrame.xml",
        @"Interface\FrameXML\LFDFrame.lua",
        @"Interface\FrameXML\CharacterFrameTemplates.xml",
        @"Interface\FrameXML\UIPanelTemplates.xml",
        @"Interface\FrameXML\UIPanelTemplates.lua",
        @"Interface\FrameXML\Fonts.xml",
        @"Interface\FrameXML\FontStyles.xml",
        @"Fonts\FRIZQT__.TTF",
        @"Interface\PaperDollInfoFrame\UI-Character-InactiveTab",
        @"Interface\PaperDollInfoFrame\UI-Character-ActiveTab",
        @"Interface\PaperDollInfoFrame\UI-Character-Tab-Highlight",
    ];

    private readonly IWoWClientAssetProvider _assets;
    private readonly Dictionary<string, XElement> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StockFontStyle?> _fonts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StockButtonStyle?> _buttons = new(StringComparer.Ordinal);
    private readonly List<StockDefinitionDiagnostic> _diagnostics = [];
    private LocalFontCollection? _fontCollection;
    private bool _fontRegistered;
    private string? _fontPath;

    public StockTemplateResolver(IWoWClientAssetProvider assets)
    {
        _assets = assets;
        Reload();
    }

    public int Generation { get; private set; }
    public IReadOnlyList<StockDefinitionDiagnostic> Diagnostics => _diagnostics;

    public IReadOnlyList<AssetMaterializationResult> MaterializeRequired(WowClientValidation client)
    {
        var results = RequiredResources.Select(resource => _assets.Materialize(resource, client)).ToArray();
        Reload();
        return results;
    }

    public void Reload()
    {
        _definitions.Clear();
        _sources.Clear();
        _fonts.Clear();
        _buttons.Clear();
        _diagnostics.Clear();
        Generation++;

        LoadXml(@"Interface\FrameXML\LFDFrame.xml");
        LoadXml(@"Interface\FrameXML\CharacterFrameTemplates.xml");
        LoadXml(@"Interface\FrameXML\UIPanelTemplates.xml");
        LoadXml(@"Interface\FrameXML\Fonts.xml");
        LoadXml(@"Interface\FrameXML\FontStyles.xml");
        CheckLuaBoundary(@"Interface\FrameXML\LFDFrame.lua",
            "LFDParentFrame queue contents, selected identity icon, text, visibility, and live state remain Lua/runtime-dependent.");
        CheckLuaBoundary(@"Interface\FrameXML\UIPanelTemplates.lua",
            "PanelTemplates_TabResize is interpreted only for the demonstrated zero-padding Native Hunts tab call; click and selected-state behavior is not executed.");
        LoadLocalFont();
    }

    public StockFontStyle? ResolveFont(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return ResolveFont(name, []);
    }

    public StockButtonStyle? ResolveButton(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (_buttons.TryGetValue(name, out var cached))
            return cached;
        if (!_definitions.TryGetValue(name, out var element) || element.Name.LocalName != "Button")
        {
            AddOnce(StockDefinitionStatus.Unresolved, "unresolved-stock-template",
                $"Stock template '{name}' is not available in the managed definition cache.", name);
            return _buttons[name] = null;
        }

        var source = _sources[name];
        var size = Child(element, "Size");
        var width = Number(size, "x") ?? Number(Child(size, "AbsDimension"), "x") ?? 0;
        var height = Number(size, "y") ?? Number(Child(size, "AbsDimension"), "y") ?? 0;
        var normalFont = Child(element, "NormalFont")?.Attribute("style")?.Value ?? "GameFontNormalSmall";
        var buttonTextOffset = Child(Child(Child(element, "ButtonText"), "Anchors"), "Anchor")
            ?.Descendants().FirstOrDefault(item => item.Name.LocalName == "AbsDimension");
        var textOffsetX = Number(buttonTextOffset, "x") ?? 0;
        var textOffsetY = Number(buttonTextOffset, "y") ?? 0;
        var slices = element.Descendants().Where(item => item.Name.LocalName == "Texture")
            .Select(item => ParseSlice(item, source)).Where(item => item is not null).Cast<StockTextureSlice>().ToArray();
        var normal = slices.Where(item => item.Name.EndsWith("Left", StringComparison.Ordinal)
                                               || item.Name.EndsWith("Middle", StringComparison.Ordinal)
                                               || item.Name.EndsWith("Right", StringComparison.Ordinal)).ToArray();
        var disabled = slices.Where(item => item.Name.EndsWith("Disabled", StringComparison.Ordinal)).ToArray();
        var highlight = element.Descendants().FirstOrDefault(item => item.Name.LocalName == "HighlightTexture")
            ?.Attribute("file")?.Value;
        var provenance = new[]
        {
            new StockPropertyProvenance("Size", name, source),
            new StockPropertyProvenance("NormalTexture children", name, source),
            new StockPropertyProvenance("NormalFont", name, source),
        };
        var missingTextures = normal.Select(item => item.File)
            .Append(highlight).Where(item => item is not null)
            .Where(item => !HasCachedAsset(item!)).ToArray();
        foreach (var missing in missingTextures)
            AddOnce(StockDefinitionStatus.PartiallyResolved, "unresolved-inherited-texture",
                $"Inherited stock texture '{missing}' is not available in the managed cache.", name, source);
        var status = width > 0 && height > 0 && normal.Length == 3 && missingTextures.Length == 0
                     && ResolveFont(normalFont) is not null
            ? StockDefinitionStatus.FullyResolved
            : StockDefinitionStatus.PartiallyResolved;
        return _buttons[name] = new StockButtonStyle(name, width, height, normalFont, textOffsetX, textOffsetY, normal, disabled,
            highlight, provenance, status);
    }

    public StockExternalFrameStyle? ResolveExternalFrame(string? name)
    {
        if (!string.Equals(name, LfdParentFrame, StringComparison.Ordinal)
            || !_definitions.TryGetValue(LfdParentFrame, out var element))
            return null;
        var size = Child(element, "Size");
        var width = Number(size, "x") ?? Number(Child(size, "AbsDimension"), "x") ?? 0;
        var height = Number(size, "y") ?? Number(Child(size, "AbsDimension"), "y") ?? 0;
        return new StockExternalFrameStyle(LfdParentFrame, width, height, _sources[LfdParentFrame],
            StockDefinitionStatus.PartiallyResolved,
            "Stock XML declares 355 x 440; Native Hunts Lua explicitly selects 355 x 500 for its Hunts state. " +
            "Queue children and Lua-driven Dungeon Finder state are intentionally omitted.");
    }

    public Project ApplyEffectiveGeometry(Project declaredProject)
    {
        var changed = false;
        var frames = declaredProject.Frames.Select(frame =>
        {
            if (frame.Kind == FrameKind.BUTTON && frame.Inherits is { } template
                && ResolveButton(template) is { } button)
            {
                var width = frame.Width > 0 ? frame.Width : EffectiveTabWidth(frame, button);
                var height = frame.Height > 0 ? frame.Height : button.Height;
                if (width != frame.Width || height != frame.Height)
                {
                    changed = true;
                    return frame with { Width = width, Height = height };
                }
            }
            if (frame.Kind == FrameKind.FONTSTRING && frame.Visual?.Text is { HasLiteralText: true } text
                && ResolveFont(text.FontTemplate) is { } font && (frame.Width <= 0 || frame.Height <= 0))
            {
                changed = true;
                return frame with
                {
                    Width = frame.Width > 0 ? frame.Width : Math.Ceiling(MeasureText(font, text.Text!)),
                    Height = frame.Height > 0 ? frame.Height : Math.Ceiling(font.Size),
                };
            }
            return frame;
        }).ToArray();
        return changed ? declaredProject with { Frames = frames } : declaredProject;
    }

    public IReadOnlyList<string> Describe(FrameDef frame)
    {
        var lines = new List<string>();
        if (frame.Kind == FrameKind.FONTSTRING && ResolveFont(frame.Visual?.Text?.FontTemplate) is { } font)
        {
            lines.Add($"effective font {font.Name}: {Num(font.Size)}px, {Num(font.Color.R)},{Num(font.Color.G)},{Num(font.Color.B)}");
            lines.Add($"font file {font.FontReference} ({(font.PhysicalFontPath is null ? "unavailable; system fallback" : "local client cache")})");
            lines.Add($"font provenance {string.Join(" -> ", font.Provenance.Select(item => item.Definition))}");
        }
        if (frame.Kind == FrameKind.BUTTON && ResolveButton(frame.Inherits) is { } button)
        {
            lines.Add($"effective template {button.Name} ({button.Status})");
            lines.Add($"normal state: 3-slice {button.NormalSlices.FirstOrDefault()?.File ?? "unresolved"}");
            lines.Add($"effective size {Num(EffectiveTabWidth(frame, button))} x {Num(button.Height)} (PanelTemplates_TabResize, normal/unselected)");
            lines.Add($"template provenance {button.Provenance[0].Source}");
            lines.Add("Lua click/selection state not executed; normal/unselected preview state used");
        }
        if (frame.Placeholder && ResolveExternalFrame(frame.Name) is { } external)
        {
            lines.Add($"stock external frame {external.Name}: {Num(external.Width)} x {Num(external.Height)} in {external.Source}");
            lines.Add(external.ScopeNote);
        }
        return lines;
    }

    public IReadOnlyList<string> ExpandedChildNames(string instanceName, StockButtonStyle style) =>
        style.NormalSlices.Concat(style.DisabledSlices)
            .Select(slice => slice.Name.Replace("$parent", instanceName, StringComparison.Ordinal))
            .Append($"{instanceName}Text")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public double MeasureText(StockFontStyle style, string text)
    {
        if (style.PhysicalFontPath is { } path && File.Exists(path))
        {
            using var typeface = SKTypeface.FromFile(path);
            using var font = new SKFont(typeface, (float)style.Size);
            return font.MeasureText(text);
        }
        return text.Length * style.Size * 0.58;
    }

    private double EffectiveTabWidth(FrameDef frame, StockButtonStyle style)
    {
        var font = ResolveFont(style.FontStyle);
        var textWidth = frame.Visual?.Text is { HasLiteralText: true } text && font is not null
            ? MeasureText(font, text.Text!)
            : style.DeclaredWidth;
        // CharacterFrameTemplate has two 20px side slices. Native Hunts calls
        // PanelTemplates_TabResize(tab, 0), so the effective width is text + 40 with no padding.
        return Math.Ceiling(textWidth + 40);
    }

    private StockFontStyle? ResolveFont(string name, HashSet<string> chain)
    {
        if (_fonts.TryGetValue(name, out var cached))
            return cached;
        if (!chain.Add(name))
        {
            AddOnce(StockDefinitionStatus.Unresolved, "circular-stock-inheritance",
                $"Circular stock font inheritance was detected at '{name}'.", name, _sources.GetValueOrDefault(name));
            return _fonts[name] = null;
        }
        if (!_definitions.TryGetValue(name, out var element) || element.Name.LocalName != "Font")
        {
            AddOnce(StockDefinitionStatus.Unresolved, "unresolved-stock-font",
                $"Stock font style '{name}' is not available in the managed definition cache.", name);
            chain.Remove(name);
            return _fonts[name] = null;
        }

        var inheritedName = element.Attribute("inherits")?.Value;
        if (inheritedName?.Contains(',') == true)
        {
            AddOnce(StockDefinitionStatus.Unresolved, "unsupported-stock-construct",
                $"Multiple inheritance on stock font '{name}' is outside the supported build-12340 subset.",
                name, _sources.GetValueOrDefault(name));
            chain.Remove(name);
            return _fonts[name] = null;
        }
        var inherited = inheritedName is { Length: > 0 } ? ResolveFont(inheritedName, chain) : null;
        if (inheritedName is { Length: > 0 } && inherited is null)
        {
            chain.Remove(name);
            return _fonts[name] = null;
        }
        var source = _sources[name];
        var fontReference = element.Attribute("font")?.Value ?? inherited?.FontReference;
        var size = Number(Child(Child(element, "FontHeight"), "AbsValue"), "val") ?? inherited?.Size ?? 11;
        var color = ReadColor(Child(element, "Color")) ?? inherited?.Color ?? ColorRgba.White;
        var shadow = Child(element, "Shadow");
        var shadowOffset = Child(Child(shadow, "Offset"), "AbsDimension");
        var shadowX = Number(shadowOffset, "x") ?? inherited?.ShadowX ?? 0;
        var shadowY = Number(shadowOffset, "y") ?? inherited?.ShadowY ?? 0;
        var shadowColor = ReadColor(Child(shadow, "Color")) ?? inherited?.ShadowColor;
        var outline = element.Attribute("outline")?.Value ?? inherited?.Outline;
        if (outline is not null and not "NORMAL" and not "THICK")
            AddOnce(StockDefinitionStatus.PartiallyResolved, "unsupported-stock-font-flag",
                $"Font outline flag '{outline}' on '{name}' is not supported.", name, source);
        var justifyH = element.Attribute("justifyH")?.Value ?? inherited?.JustifyH ?? "CENTER";
        var justifyV = element.Attribute("justifyV")?.Value ?? inherited?.JustifyV ?? "MIDDLE";
        var provenance = new List<StockPropertyProvenance>();
        if (inherited is not null)
            provenance.AddRange(inherited.Provenance);
        provenance.Add(new StockPropertyProvenance("font style", name, source));
        chain.Remove(name);
        return _fonts[name] = new StockFontStyle(name, fontReference,
            fontReference?.Equals(@"Fonts\FRIZQT__.TTF", StringComparison.OrdinalIgnoreCase) == true ? _fontPath : null,
            _fontRegistered ? "Friz Quadrata TT" : null, size, color, justifyH, justifyV, outline,
            shadowX, shadowY, shadowColor, provenance);
    }

    private void LoadXml(string resource)
    {
        var path = CachePath(resource);
        if (!File.Exists(path))
        {
            AddOnce(StockDefinitionStatus.Unresolved, "missing-stock-definition-file",
                $"Required build-12340 definition '{resource}' is not cached.", source: resource);
            return;
        }
        try
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants().Where(element => element.Attribute("name") is not null))
            {
                var name = element.Attribute("name")!.Value;
                if (name.Contains("$parent", StringComparison.Ordinal))
                    continue;
                if (!_definitions.ContainsKey(name))
                {
                    _definitions[name] = element;
                    _sources[name] = resource;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            AddOnce(StockDefinitionStatus.Unresolved, "invalid-stock-definition-file",
                $"Could not index '{resource}': {ex.Message}", source: resource);
        }
    }

    private void LoadLocalFont()
    {
        if (_fontRegistered)
            FontManager.Current.RemoveFontCollection(_fontCollection!.Key);
        _fontRegistered = false;
        _fontCollection = null;
        _fontPath = CachePath(@"Fonts\FRIZQT__.TTF");
        if (!File.Exists(_fontPath))
        {
            _fontPath = null;
            AddOnce(StockDefinitionStatus.PartiallyResolved, "unavailable-stock-font-file",
                "Fonts\\FRIZQT__.TTF is not cached; authoritative metrics are retained but rendering uses the host fallback.",
                source: @"Fonts\FRIZQT__.TTF");
            return;
        }
        try
        {
            using var typeface = SKTypeface.FromFile(_fontPath);
            if (typeface is null)
                throw new InvalidDataException("Skia rejected the local TrueType font.");
            _fontCollection = new LocalFontCollection();
            _fontCollection.Reset(_fontPath);
            FontManager.Current.AddFontCollection(_fontCollection);
            _fontRegistered = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            AddOnce(StockDefinitionStatus.PartiallyResolved, "unavailable-stock-font",
                $"The local Friz Quadrata font could not be registered: {ex.Message}", source: @"Fonts\FRIZQT__.TTF");
        }
    }

    private void CheckLuaBoundary(string resource, string message)
    {
        if (!File.Exists(CachePath(resource)))
        {
            AddOnce(StockDefinitionStatus.Unresolved, "missing-stock-definition-file",
                $"Required build-12340 definition '{resource}' is not cached.", source: resource);
            return;
        }
        AddOnce(StockDefinitionStatus.PartiallyResolved, "lua-dependent-stock-visual",
            message, source: resource);
    }

    private string CachePath(string resource) => Path.Combine([_assets.CacheRoot, .. resource.Replace('\\', '/').Split('/')]);

    private bool HasCachedAsset(string reference)
    {
        var basePath = CachePath(reference);
        var directory = Path.GetDirectoryName(basePath);
        if (directory is null || !Directory.Exists(directory))
            return false;
        var fileName = Path.GetFileName(basePath);
        return Directory.EnumerateFiles(directory).Any(path =>
            new[] { "", ".tga", ".blp", ".png" }.Any(extension =>
                string.Equals(Path.GetFileName(path), fileName + extension, StringComparison.OrdinalIgnoreCase)));
    }

    private static StockTextureSlice? ParseSlice(XElement element, string source)
    {
        var name = element.Attribute("name")?.Value;
        var file = element.Attribute("file")?.Value;
        if (name is null || file is null)
            return null;
        var size = Child(element, "Size");
        var width = Number(size, "x") ?? Number(Child(size, "AbsDimension"), "x") ?? 0;
        var height = Number(size, "y") ?? Number(Child(size, "AbsDimension"), "y") ?? 0;
        var tex = Child(element, "TexCoords");
        return new StockTextureSlice(name, file, width, height, new TexCoords(
            Number(tex, "left") ?? 0, Number(tex, "right") ?? 1,
            Number(tex, "top") ?? 0, Number(tex, "bottom") ?? 1), source);
    }

    private static XElement? Child(XElement? element, string name) =>
        element?.Elements().FirstOrDefault(child => child.Name.LocalName == name);

    private static double? Number(XElement? element, string attribute) =>
        double.TryParse(element?.Attribute(attribute)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    private static ColorRgba? ReadColor(XElement? element) => element is null ? null : new ColorRgba(
        Number(element, "r") ?? 1, Number(element, "g") ?? 1,
        Number(element, "b") ?? 1, Number(element, "a") ?? 1);

    private void AddOnce(StockDefinitionStatus status, string code, string message,
        string? definition = null, string? source = null)
    {
        if (_diagnostics.Any(item => item.Code == code && item.Definition == definition && item.Source == source))
            return;
        _diagnostics.Add(new StockDefinitionDiagnostic(status, code, message, definition, source));
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_fontRegistered)
            FontManager.Current.RemoveFontCollection(_fontCollection!.Key);
    }

    private sealed class LocalFontCollection : FontCollectionBase
    {
        public override Uri Key { get; } = new("fonts:FrameForgeWoW", UriKind.Absolute);

        public void Reset(string path)
        {
            using var stream = File.OpenRead(path);
            if (!TryAddGlyphTypeface(stream, out _))
                throw new InvalidDataException("Avalonia rejected the local TrueType font.");
        }
    }
}
