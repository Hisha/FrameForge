using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using FrameForge.Core.Import;
using FrameForge.Core.Models;

namespace FrameForge.Core.Export;

public enum ExportSeverity { Warning, Error }

public sealed record ExportDiagnostic(ExportSeverity Severity, string Code, string Message, string? FrameName = null);

public sealed record WowExportObject(
    string SourceName,
    string RuntimeName,
    string WrapperName,
    FrameKind Kind,
    FrameDef Frame,
    DesignObjectMetadata Metadata,
    string? ParentRuntimeName,
    string? RelativeRuntimeName,
    int DrawOrder,
    string DrawLayer,
    IReadOnlyList<string> StateIds,
    string? TextureReference,
    string? Text,
    string? FontStyle,
    double? FontSize,
    ColorRgba? TextColor,
    string? Outline,
    bool? Shadow,
    string? JustifyH,
    string? JustifyV);

public sealed record WowExportAsset(
    string SourceProjectAsset,
    string LogicalTexture,
    string PackagingTarget,
    IReadOnlyList<string> Consumers);

public sealed record WowExportModel(
    string ProjectName,
    string RootRuntimeName,
    string? StockFoundationIdentity,
    string StockParentRuntimeName,
    IReadOnlyList<DesignState> States,
    IReadOnlyList<WowExportObject> Objects,
    IReadOnlyList<WowExportAsset> Assets,
    IReadOnlyList<ExportDiagnostic> Diagnostics);

public sealed record WowExportResult(
    bool Success,
    WowExportModel? Model,
    IReadOnlyList<ExportDiagnostic> Diagnostics,
    IReadOnlyList<string> WrittenFiles)
{
    public string Summary => Success
        ? $"Exported {Model!.Objects.Count} objects with {Diagnostics.Count(d => d.Severity == ExportSeverity.Warning)} warning(s)."
        : $"Export blocked by {Diagnostics.Count(d => d.Severity == ExportSeverity.Error)} error(s).";
}

/// <summary>Builds a portable, validated WoW 3.3.5a export model from editor data.</summary>
public static partial class Wow335ExportBuilder
{
    private static readonly string[] DrawLayers = ["BACKGROUND", "BORDER", "ARTWORK", "OVERLAY", "HIGHLIGHT"];

    public static WowExportModel Build(Project project, string? projectFilePath)
    {
        var diagnostics = new List<ExportDiagnostic>();
        var stateIds = project.Editor.DesignStates.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (project.Editor.DesignStates.Any(s => string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Name)))
            diagnostics.Add(new(ExportSeverity.Error, "STATE_INVALID", "Every design state must have a non-empty id and name."));
        foreach (var duplicate in project.Editor.DesignStates.GroupBy(s => s.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
            diagnostics.Add(new(ExportSeverity.Error, "STATE_DUPLICATE", $"Design state id '{duplicate.Key}' is duplicated."));

        var stockGroup = project.Editor.Groups.FirstOrDefault(g => g.Concept == "stock-framework");
        var stock = stockGroup?.Members.ToHashSet(StringComparer.Ordinal) ?? [];
        var authored = project.Editor.DesignObjects
            .Where(m => project.Contains(m.FrameName) && !stock.Contains(m.FrameName))
            .ToArray();

        foreach (var metadata in project.Editor.DesignObjects.Where(m => !project.Contains(m.FrameName)))
            diagnostics.Add(new(ExportSeverity.Error, "OBJECT_MISSING", $"Design metadata refers to missing object '{metadata.FrameName}'.", metadata.FrameName));
        foreach (var metadata in project.Editor.DesignObjects.Where(m => stock.Contains(m.FrameName) && HasAuthoredOverride(m)))
            diagnostics.Add(new(ExportSeverity.Warning, "STOCK_OVERRIDE_CONTRACT", $"'{metadata.FrameName}' is client-owned stock UI. Its authored override is recorded as a diagnostic contract and is not flattened into generated XML.", metadata.FrameName));

        var order = project.Editor.EffectiveDesignOrder(project);
        var rank = order.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index, StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var runtimeBySource = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var metadata in authored)
        {
            var frame = project.Find(metadata.FrameName)!;
            var candidate = metadata.DisplayName ?? frame.SourceName ?? frame.Name;
            var safe = SafeIdentifier(candidate);
            if (!string.Equals(candidate, safe, StringComparison.Ordinal))
                diagnostics.Add(new(ExportSeverity.Warning, "NAME_SANITIZED", $"Runtime name '{candidate}' was mapped to WoW-safe '{safe}'.", frame.Name));
            var unique = safe;
            for (var suffix = 2; !usedNames.Add(unique); suffix++) unique = $"{safe}_{suffix}";
            if (unique != safe)
                diagnostics.Add(new(ExportSeverity.Warning, "NAME_COLLISION", $"Runtime name '{safe}' collided and was mapped to '{unique}'.", frame.Name));
            runtimeBySource[frame.Name] = unique;
        }

        var projectDirectory = string.IsNullOrWhiteSpace(projectFilePath) ? null : Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        var assetConsumers = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var objects = new List<WowExportObject>();
        foreach (var metadata in authored.OrderBy(m => rank.GetValueOrDefault(m.FrameName, int.MaxValue)).ThenBy(m => m.FrameName, StringComparer.Ordinal))
        {
            var frame = project.Find(metadata.FrameName)!;
            ValidateFrame(frame, metadata, stateIds, project, diagnostics);
            var runtime = runtimeBySource[frame.Name];
            string? texture = null;
            if (!string.IsNullOrWhiteSpace(metadata.DesignAsset))
            {
                var asset = NormalizePortablePath(metadata.DesignAsset!);
                if (Path.IsPathRooted(metadata.DesignAsset!) || IsMachinePath(metadata.DesignAsset!))
                    diagnostics.Add(new(ExportSeverity.Error, "ASSET_ABSOLUTE", "Project artwork must use a portable path relative to the .fforge.json file.", frame.Name));
                var physical = projectDirectory is null ? null : Path.GetFullPath(Path.Combine(projectDirectory, asset.Replace('/', Path.DirectorySeparatorChar)));
                if (physical is not null && !physical.StartsWith(projectDirectory! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    diagnostics.Add(new(ExportSeverity.Error, "ASSET_ESCAPE", $"Project artwork '{asset}' escapes the project directory.", frame.Name));
                if (physical is null || !File.Exists(physical))
                    diagnostics.Add(new(ExportSeverity.Error, "ASSET_UNRESOLVED", $"Project artwork '{asset}' was not found beside the project.", frame.Name));
                var stem = SafePathSegment(Path.GetFileNameWithoutExtension(asset));
                texture = $@"Interface\FrameForge\{SafePathSegment(project.Name)}\{stem}";
                if (!assetConsumers.TryGetValue(asset, out var consumers)) assetConsumers[asset] = consumers = [];
                consumers.Add(runtime);
            }
            else if (frame.Kind == FrameKind.TEXTURE && frame.Visual?.Texture?.File is { Length: > 0 } stockTexture)
            {
                texture = NormalizeWowPath(stockTexture);
                if (!IsStockTexture(texture)) diagnostics.Add(new(ExportSeverity.Error, "STOCK_TEXTURE_INVALID", $"Texture '{stockTexture}' is neither declared project artwork nor a logical Interface path.", frame.Name));
            }
            else if (frame.Kind == FrameKind.STATUSBAR && frame.Visual?.StatusBar?.BarTexture is { Length: > 0 } barTexture)
            {
                texture = NormalizeWowPath(barTexture);
                if (!IsStockTexture(texture)) diagnostics.Add(new(ExportSeverity.Error, "STOCK_TEXTURE_INVALID", $"StatusBar texture '{barTexture}' is not a logical Interface path.", frame.Name));
            }

            var style = metadata.TextStyle;
            var text = metadata.TextOverride ?? frame.Visual?.Text?.Text;
            var drawIndex = rank.GetValueOrDefault(frame.Name, objects.Count);
            var layerIndex = Math.Min(DrawLayers.Length - 1, drawIndex * DrawLayers.Length / Math.Max(1, authored.Length));
            objects.Add(new(
                frame.Name, runtime, runtime + "__FFLayer", frame.Kind, frame, metadata,
                TranslateReference(frame.Parent, runtimeBySource), TranslateReference(frame.RelativeTo, runtimeBySource),
                drawIndex, frame.Visual?.DrawLayer ?? DrawLayers[layerIndex], [.. metadata.StateIds], texture, text,
                style?.BaseStyle ?? frame.Visual?.Text?.FontTemplate, style?.Size, style?.Color, style?.Outline,
                style?.Shadow, style?.JustifyH ?? frame.Visual?.Text?.JustifyH, frame.Visual?.Text?.JustifyV));
        }

        var assets = assetConsumers.Select(pair =>
        {
            var stem = SafePathSegment(Path.GetFileNameWithoutExtension(pair.Key));
            return new WowExportAsset(pair.Key, $@"Interface\FrameForge\{SafePathSegment(project.Name)}\{stem}",
                $"textures/{stem}.tga", [.. pair.Value.Order(StringComparer.Ordinal)]);
        }).ToArray();

        foreach (var conflict in objects
                     .Where(item => !string.IsNullOrWhiteSpace(item.Metadata.RuntimeBinding))
                     .GroupBy(item => item.Metadata.RuntimeBinding!, StringComparer.Ordinal)
                     .Where(group => group.Select(item => BindingValueType(item.Kind)).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            diagnostics.Add(new(ExportSeverity.Error, "BINDING_TYPE_CONFLICT",
                $"Runtime binding '{conflict.Key}' is used by incompatible value types: {string.Join(", ", conflict.Select(item => BindingValueType(item.Kind)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}."));
        }

        return new(project.Name, "FrameForge_" + SafeIdentifier(project.Name), stockGroup?.StockIdentity,
            "LFDParentFrame", [.. project.Editor.DesignStates], objects, assets, diagnostics);
    }

    private static void ValidateFrame(FrameDef frame, DesignObjectMetadata metadata, HashSet<string> stateIds, Project project, List<ExportDiagnostic> diagnostics)
    {
        if (frame.Kind is FrameKind.OTHER or FrameKind.BUTTON)
            diagnostics.Add(new(ExportSeverity.Error, "OBJECT_UNSUPPORTED", "This DESIGN object type cannot be exported safely.", frame.Name));
        if (frame.SizeReferenceOrDefault == SizeReference.PARENT)
            diagnostics.Add(new(ExportSeverity.Error, "SIZE_REFERENCE_UNSUPPORTED", "Parent-relative fractional sizes cannot yet be represented without changing authored geometry.", frame.Name));
        if (frame.Visual?.DrawLayer is { } layer && layer is not ("BACKGROUND" or "BORDER" or "ARTWORK" or "OVERLAY" or "HIGHLIGHT"))
            diagnostics.Add(new(ExportSeverity.Error, "DRAW_LAYER_INVALID", $"Draw layer '{layer}' is not supported by WoW 3.3.5a.", frame.Name));
        if (!Finite(frame.Width) || !Finite(frame.Height) || frame.Width <= 0 || frame.Height <= 0 || !Finite(frame.OffsetX) || !Finite(frame.OffsetY))
            diagnostics.Add(new(ExportSeverity.Error, "GEOMETRY_INVALID", "Width, height, and offsets must be finite; width and height must be positive.", frame.Name));
        foreach (var reference in new[] { frame.Parent, frame.RelativeTo }.Where(r => r is not null && r != "UIParent"))
            if (!project.Contains(reference)) diagnostics.Add(new(ExportSeverity.Error, "REFERENCE_INVALID", $"Anchor/parent target '{reference}' does not exist.", frame.Name));
        foreach (var id in metadata.StateIds)
            if (!stateIds.Contains(id)) diagnostics.Add(new(ExportSeverity.Error, "STATE_REFERENCE_INVALID", $"State membership refers to missing state '{id}'.", frame.Name));
        if (metadata.StateIds.Distinct(StringComparer.Ordinal).Count() != metadata.StateIds.Count)
            diagnostics.Add(new(ExportSeverity.Error, "STATE_REFERENCE_DUPLICATE", "State membership contains a duplicate id.", frame.Name));
        if (frame.Kind == FrameKind.STATUSBAR && frame.Visual?.StatusBar is not { } bar)
            diagnostics.Add(new(ExportSeverity.Error, "STATUSBAR_MISSING", "StatusBar visual metadata is required.", frame.Name));
        else if (frame.Kind == FrameKind.STATUSBAR && frame.Visual?.StatusBar is { } status &&
                 (status.MinValue is null || status.MaxValue is null || !Finite(status.MinValue.Value) || !Finite(status.MaxValue.Value) || status.MinValue >= status.MaxValue))
            diagnostics.Add(new(ExportSeverity.Error, "STATUSBAR_RANGE_INVALID", "StatusBar requires a finite minValue smaller than maxValue.", frame.Name));
        if (metadata.TextStyle?.Color is { } color && !color.IsValid)
            diagnostics.Add(new(ExportSeverity.Error, "TEXT_COLOR_INVALID", "Text colour channels must be between 0 and 1.", frame.Name));
        if (metadata.TextStyle?.BaseStyle is { Length: > 0 } font && !font.StartsWith("GameFont", StringComparison.Ordinal))
            diagnostics.Add(new(ExportSeverity.Warning, "FONT_UNVERIFIED", $"Font template '{font}' is emitted as a client reference but was not recognized as a stock GameFont style.", frame.Name));
        if (metadata.TextStyle?.Outline is { } outline && outline is not ("NONE" or "NORMAL" or "THICK"))
            diagnostics.Add(new(ExportSeverity.Error, "TEXT_OUTLINE_UNSUPPORTED", $"Outline '{outline}' is not supported by the 3.3.5a exporter.", frame.Name));
        var hasBinding = !string.IsNullOrWhiteSpace(metadata.RuntimeBinding);
        if ((metadata.RuntimeValueRequired || hasBinding) && frame.Kind is not (FrameKind.FONTSTRING or FrameKind.STATUSBAR))
            diagnostics.Add(new(ExportSeverity.Error, "BINDING_TYPE_UNSUPPORTED", "Runtime values can only bind to FontString or StatusBar DESIGN objects.", frame.Name));
        if (metadata.RuntimeValueRequired && !hasBinding)
            diagnostics.Add(new(ExportSeverity.Error, "BINDING_REQUIRED", "Runtime value is enabled but no semantic binding key is set.", frame.Name));
        if (!metadata.RuntimeValueRequired && hasBinding)
            diagnostics.Add(new(ExportSeverity.Error, "BINDING_WITHOUT_RUNTIME_VALUE", "A semantic binding key is set, but Runtime value is not enabled.", frame.Name));
        if (hasBinding && !RuntimeBindingKeys.IsValid(metadata.RuntimeBinding))
            diagnostics.Add(new(ExportSeverity.Error, "BINDING_KEY_INVALID", $"Runtime binding must use {RuntimeBindingKeys.GrammarDescription}.", frame.Name));
        if (hasBinding && (RuntimeBindingKeys.LooksLikeEditorIdentity(metadata.RuntimeBinding)
                           || string.Equals(metadata.RuntimeBinding, frame.Name, StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add(new(ExportSeverity.Error, "BINDING_KEY_EDITOR_ID", "Runtime binding keys must express application semantics, not FrameForge editor/source identities.", frame.Name));
    }

    private static string? TranslateReference(string? source, IReadOnlyDictionary<string, string> names) =>
        source is null ? null : names.TryGetValue(source, out var runtime) ? runtime + "__FFLayer" : source;
    private static bool HasAuthoredOverride(DesignObjectMetadata m) => m.TextOverride is not null || m.TextStyle is not null || m.DesignAsset is not null || m.RuntimeValueRequired || m.RuntimeBinding is not null || m.StateIds.Count > 0;
    private static string BindingValueType(FrameKind kind) => kind == FrameKind.STATUSBAR ? "number" : "string";
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool IsStockTexture(string value) => value.StartsWith("Interface\\", StringComparison.OrdinalIgnoreCase) && !IsMachinePath(value);
    private static string NormalizeWowPath(string value) => value.Replace('/', '\\').Trim();
    private static string NormalizePortablePath(string value)
    {
        var result = value.Replace('\\', '/');
        while (result.StartsWith("./", StringComparison.Ordinal)) result = result[2..];
        return result;
    }
    private static bool IsMachinePath(string value) => Regex.IsMatch(value, @"(^[A-Za-z]:[\\/])|(^/home/)|AppData|FrameForge[\\/]cache", RegexOptions.IgnoreCase);
    private static string SafePathSegment(string value) => SafeIdentifier(value).Trim('_');
    public static string SafeIdentifier(string value)
    {
        var safe = InvalidIdentifierChars().Replace(value.Trim(), "_");
        safe = RepeatedUnderscores().Replace(safe, "_");
        if (safe.Length == 0) safe = "FrameForgeObject";
        if (!char.IsLetter(safe[0]) && safe[0] != '_') safe = "_" + safe;
        return safe;
    }
    [GeneratedRegex("[^A-Za-z0-9_]")] private static partial Regex InvalidIdentifierChars();
    [GeneratedRegex("_+")] private static partial Regex RepeatedUnderscores();
}

/// <summary>Writes deterministic FrameXML and versioned contracts; it never copies texture files.</summary>
public static class Wow335Exporter
{
    public const string XmlFileName = "FrameForgeLayout.xml";
    public const string ManifestFileName = "frameforge-manifest.json";
    public const string AssetsFileName = "assets-manifest.json";
    public const string ReportFileName = "export-report.txt";

    public static WowExportResult Export(Project project, string? projectFilePath, string destination)
    {
        var model = Wow335ExportBuilder.Build(project, projectFilePath);
        if (model.Diagnostics.Any(d => d.Severity == ExportSeverity.Error))
            return new(false, model, model.Diagnostics, []);
        Directory.CreateDirectory(destination);
        var files = new[]
        {
            Path.Combine(destination, XmlFileName), Path.Combine(destination, ManifestFileName),
            Path.Combine(destination, AssetsFileName), Path.Combine(destination, ReportFileName),
        };
        File.WriteAllText(files[0], WriteXml(model), new UTF8Encoding(false));
        File.WriteAllText(files[1], WriteManifest(model), new UTF8Encoding(false));
        File.WriteAllText(files[2], WriteAssets(model), new UTF8Encoding(false));
        var roundTrip = FrameXmlImporter.ImportFile(files[0]);
        if (!roundTrip.Ok || roundTrip.Project is null)
            throw new InvalidDataException("Generated FrameXML failed the independent importer self-check: " +
                                           string.Join(" ", roundTrip.Errors.Select(e => e.Message)));
        var importedNames = roundTrip.Project.Frames.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var missing = model.Objects.Select(o => o.RuntimeName).Where(name => !importedNames.Contains(name)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"Generated FrameXML importer self-check lost {missing.Length} runtime object(s): {string.Join(", ", missing.Take(5))}.");
        File.WriteAllText(files[3], WriteReport(model) + $"Round-trip self-check: PASS ({model.Objects.Count}/{model.Objects.Count} runtime identities)\n", new UTF8Encoding(false));
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            if (Regex.IsMatch(text, @"[A-Za-z]:\\|/home/|AppData", RegexOptions.IgnoreCase))
                throw new InvalidDataException($"Portable export audit rejected a machine-local path in {Path.GetFileName(path)}.");
        }
        using (var reader = XmlReader.Create(files[0]))
            while (reader.Read()) { }
        return new(true, model, model.Diagnostics, files);
    }

    public static string WriteXml(WowExportModel model)
    {
        var output = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, NewLineChars = "\n", Encoding = new UTF8Encoding(false) };
        using var x = XmlWriter.Create(output, settings);
        x.WriteStartElement("Ui", "http://www.blizzard.com/wow/ui/");
        x.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
        x.WriteAttributeString("xsi", "schemaLocation", null, "http://www.blizzard.com/wow/ui/ ..\\FrameXML\\UI.xsd");
        x.WriteStartElement("Frame"); x.WriteAttributeString("name", model.RootRuntimeName); x.WriteAttributeString("parent", model.StockParentRuntimeName); x.WriteAttributeString("setAllPoints", "true");
        x.WriteStartElement("Frames");
        var wrappers = model.Objects.ToDictionary(o => o.SourceName, o => o.WrapperName, StringComparer.Ordinal);
        foreach (var item in model.Objects.OrderBy(o => o.DrawOrder).ThenBy(o => o.RuntimeName, StringComparer.Ordinal)) WriteObject(x, item, wrappers);
        x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        x.Flush();
        return output.ToString();
    }

    private static void WriteObject(XmlWriter x, WowExportObject item, IReadOnlyDictionary<string, string> wrappers)
    {
        var region = item.Kind is FrameKind.TEXTURE or FrameKind.FONTSTRING;
        if (region)
        {
            x.WriteStartElement("Frame"); x.WriteAttributeString("name", item.WrapperName); WriteCommonAttributes(x, item); WriteGeometry(x, item, wrappers);
            x.WriteStartElement("Layers"); x.WriteStartElement("Layer"); x.WriteAttributeString("level", item.DrawLayer);
            WriteRegion(x, item);
            x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
            return;
        }
        x.WriteStartElement(item.Kind.TagName()); x.WriteAttributeString("name", item.RuntimeName); WriteCommonAttributes(x, item);
        if (item.Kind == FrameKind.STATUSBAR && item.Frame.Visual!.StatusBar is { } bar)
        {
            x.WriteAttributeString("minValue", N(bar.MinValue!.Value)); x.WriteAttributeString("maxValue", N(bar.MaxValue!.Value));
            WriteGeometry(x, item, wrappers);
            if (item.TextureReference is { } texture) { x.WriteStartElement("BarTexture"); x.WriteAttributeString("file", texture); x.WriteEndElement(); }
            if (bar.BarColor is { } color) WriteColor(x, "BarColor", color);
        }
        else
        {
            WriteGeometry(x, item, wrappers);
        }
        x.WriteEndElement();
    }

    private static void WriteRegion(XmlWriter x, WowExportObject item)
    {
        x.WriteStartElement(item.Kind.TagName()); x.WriteAttributeString("name", item.RuntimeName); x.WriteAttributeString("setAllPoints", "true");
        if (item.Kind == FrameKind.TEXTURE)
        {
            if (item.TextureReference is { } texture) x.WriteAttributeString("file", texture);
            if (item.Frame.Visual?.Texture?.Alpha is { } alpha) x.WriteAttributeString("alpha", N(alpha));
            if (item.Frame.Visual?.Texture?.BlendMode is { Length: > 0 } blend) x.WriteAttributeString("blendMode", blend);
            if (item.Frame.Visual?.Texture?.NormalizeTexCoords is { } normalize) x.WriteAttributeString("normalizeTexCoords", normalize ? "true" : "false");
            if (item.Frame.Visual?.Texture?.Color is { } color) WriteColor(x, "Color", color);
            if (item.Frame.Visual?.Texture?.TexCoords is { IsSubRectangle: true } tc)
            {
                x.WriteStartElement("TexCoords"); x.WriteAttributeString("left", N(tc.Left)); x.WriteAttributeString("right", N(tc.Right)); x.WriteAttributeString("top", N(tc.Top)); x.WriteAttributeString("bottom", N(tc.Bottom)); x.WriteEndElement();
            }
        }
        else
        {
            if (item.Text is not null) x.WriteAttributeString("text", item.Text);
            if (item.FontStyle is { } font) x.WriteAttributeString("inherits", font);
            if (item.JustifyH is { } justify) x.WriteAttributeString("justifyH", justify.ToUpperInvariant());
            if (item.JustifyV is { } justifyV) x.WriteAttributeString("justifyV", justifyV.ToUpperInvariant());
            if (item.Outline is "NONE" or "NORMAL" or "THICK")
            {
                x.WriteStartElement("Font"); x.WriteAttributeString("font", @"Fonts\FRIZQT__.TTF");
                x.WriteAttributeString("size", N(item.FontSize ?? StockFontSize(item.FontStyle)));
                if (item.Outline != "NONE") x.WriteAttributeString("flags", item.Outline == "THICK" ? "THICKOUTLINE" : "OUTLINE");
                x.WriteEndElement();
            }
            else if (item.FontSize is { } size) { x.WriteStartElement("FontHeight"); x.WriteStartElement("AbsValue"); x.WriteAttributeString("val", N(size)); x.WriteEndElement(); x.WriteEndElement(); }
            if (item.TextColor is { } color) WriteColor(x, "Color", color);
            if (item.Shadow is { } shadow)
            {
                x.WriteStartElement("Shadow"); x.WriteStartElement("Offset"); x.WriteStartElement("AbsDimension"); x.WriteAttributeString("x", "1"); x.WriteAttributeString("y", "-1"); x.WriteEndElement(); x.WriteEndElement(); WriteColor(x, "Color", shadow ? new(0, 0, 0, 1) : new(0, 0, 0, 0)); x.WriteEndElement();
            }
        }
        x.WriteEndElement();
    }

    private static void WriteCommonAttributes(XmlWriter x, WowExportObject item)
    {
        if (item.ParentRuntimeName is { } parent) x.WriteAttributeString("parent", parent);
        x.WriteAttributeString("frameStrata", item.Frame.StratumOrDefault.ToString()); x.WriteAttributeString("frameLevel", (item.DrawOrder + 1).ToString(CultureInfo.InvariantCulture));
        if (!item.Frame.Visible || item.StateIds.Count > 0) x.WriteAttributeString("hidden", "true");
    }
    private static void WriteGeometry(XmlWriter x, WowExportObject item, IReadOnlyDictionary<string, string> wrappers)
    {
        if (item.Frame.SetAllPoints) x.WriteAttributeString("setAllPoints", "true");
        else
        {
            x.WriteStartElement("Size"); x.WriteStartElement("AbsDimension"); x.WriteAttributeString("x", N(item.Frame.Width)); x.WriteAttributeString("y", N(item.Frame.Height)); x.WriteEndElement(); x.WriteEndElement();
            x.WriteStartElement("Anchors"); WriteAnchor(x, item.Frame.PrimaryAnchor, item.RelativeRuntimeName ?? item.ParentRuntimeName ?? "UIParent");
            foreach (var anchor in item.Frame.ExtraAnchors)
            {
                var relative = anchor.RelativeTo is null ? item.ParentRuntimeName ?? "UIParent"
                    : wrappers.GetValueOrDefault(anchor.RelativeTo, anchor.RelativeTo);
                WriteAnchor(x, anchor, relative);
            }
            x.WriteEndElement();
        }
    }
    private static void WriteAnchor(XmlWriter x, FrameAnchor anchor, string relative)
    {
        x.WriteStartElement("Anchor"); x.WriteAttributeString("point", anchor.Point.ToString()); x.WriteAttributeString("relativeTo", relative); x.WriteAttributeString("relativePoint", anchor.RelativePoint.ToString());
        x.WriteStartElement("Offset"); x.WriteStartElement("AbsDimension"); x.WriteAttributeString("x", N(anchor.OffsetX)); x.WriteAttributeString("y", N(anchor.OffsetY)); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
    }
    private static void WriteColor(XmlWriter x, string element, ColorRgba c) { x.WriteStartElement(element); x.WriteAttributeString("r", N(c.R)); x.WriteAttributeString("g", N(c.G)); x.WriteAttributeString("b", N(c.B)); x.WriteAttributeString("a", N(c.A)); x.WriteEndElement(); }

    public static string WriteManifest(WowExportModel model)
    {
        var states = model.States.Select(state => new { state.Id, state.Name, show = model.Objects.Where(o => o.StateIds.Count == 0 || o.StateIds.Contains(state.Id, StringComparer.Ordinal)).Select(o => o.RuntimeName).ToArray(), hide = model.Objects.Where(o => o.StateIds.Count > 0 && !o.StateIds.Contains(state.Id, StringComparer.Ordinal)).Select(o => o.RuntimeName).ToArray() }).ToArray();
        var bindings = model.Objects.Select(o => new
        {
            sourceObject = o.SourceName,
            runtimeId = o.RuntimeName,
            type = o.Kind.TagName(),
            allStates = o.StateIds.Count == 0,
            states = o.StateIds,
            runtimeValueRequired = o.Metadata.RuntimeValueRequired,
            runtimeBinding = o.Metadata.RuntimeValueRequired ? o.Metadata.RuntimeBinding : null,
            valueType = o.Metadata.RuntimeValueRequired ? ManifestValueType(o.Kind) : null,
            operation = o.Metadata.RuntimeValueRequired ? o.Kind == FrameKind.STATUSBAR ? "SetValue" : "SetText" : null,
            editorPreviewDefault = o.Kind == FrameKind.STATUSBAR ? o.Frame.Visual?.StatusBar?.DefaultValue : null,
        }).ToArray();
        return JsonSerializer.Serialize(new { schema = "frameforge-wow335-manifest", version = 2, target = new { game = "World of Warcraft", version = "3.3.5a", build = 12340 }, project = model.ProjectName, layout = XmlFileName, rootRuntimeId = model.RootRuntimeName, stockFoundation = new { identity = model.StockFoundationIdentity, runtimeParent = model.StockParentRuntimeName, strategy = "reference-client-owned-foundation" }, states, bindings }, JsonOptions());
    }
    public static string WriteAssets(WowExportModel model) => JsonSerializer.Serialize(new { schema = "frameforge-wow335-assets", version = 1, note = "Project PNGs require conversion by a later content-packaging phase. Stock client artwork is referenced and never copied.", assets = model.Assets.Select(a => new { sourceProjectAsset = a.SourceProjectAsset, logicalTexture = a.LogicalTexture, packagingTarget = a.PackagingTarget, conversion = "png-to-wow-compatible-tga-or-blp", consumers = a.Consumers }) }, JsonOptions());
    public static string WriteReport(WowExportModel model)
    {
        var b = new StringBuilder(); b.AppendLine("FrameForge WoW 3.3.5a export report"); b.AppendLine($"Project: {model.ProjectName}"); b.AppendLine($"Objects: {model.Objects.Count}"); b.AppendLine($"Runtime bindings: {model.Objects.Count(o => o.Metadata.RuntimeValueRequired)}"); b.AppendLine($"States: {string.Join(", ", model.States.Select(s => s.Name))}"); b.AppendLine($"Project assets: {model.Assets.Count}"); b.AppendLine("Stock foundation: referenced; not copied"); b.AppendLine("StatusBar defaultValue: editor preview metadata only; omitted from FrameXML runtime initialization"); b.AppendLine(); b.AppendLine("Diagnostics:");
        foreach (var d in model.Diagnostics.OrderBy(d => d.Severity).ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.FrameName, StringComparer.Ordinal)) b.AppendLine($"{d.Severity.ToString().ToUpperInvariant()} {d.Code}{(d.FrameName is null ? "" : $" [{d.FrameName}]")}: {d.Message}");
        return b.ToString().Replace("\r\n", "\n");
    }
    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static string ManifestValueType(FrameKind kind) => kind == FrameKind.STATUSBAR ? "number" : "string";
    private static double StockFontSize(string? style) => style switch
    {
        "GameFontNormalSmall" or "GameFontHighlightSmall" => 10,
        "GameFontNormalLarge" or "GameFontHighlightLarge" => 16,
        _ => 12,
    };
    private static string N(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);
}
