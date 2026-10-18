using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using FrameForge.Core.Geometry;
using FrameForge.Core.Import;
using FrameForge.Core.Models;

namespace FrameForge.Core.Export;

public enum ExportSeverity { Warning, Error }

public enum WowTextureFormat { Blp2Dxt5, Tga }

public enum WowTextureResolutionPolicy { Conservative, PreserveSource }

/// <summary>Options for generated PNG textures. BLP2/DXT5 is the preferred WoW 3.3.5a path.</summary>
public sealed record Wow335ExportOptions
{
    public static Wow335ExportOptions Preferred { get; } = new();
    public static Wow335ExportOptions LegacyTga { get; } = new() { TextureFormat = WowTextureFormat.Tga };

    public WowTextureFormat TextureFormat { get; init; } = WowTextureFormat.Blp2Dxt5;
    public WowTextureResolutionPolicy ResolutionPolicy { get; init; } = WowTextureResolutionPolicy.Conservative;
    /// <summary>Target source texels per largest displayed UI pixel before power-of-two padding.</summary>
    public double TexelsPerUiPixel { get; init; } = 2;
}

public sealed record ExportDiagnostic(ExportSeverity Severity, string Code, string Message, string? FrameName = null);

public sealed record WowExportObject(
    string SourceName,
    string RuntimeName,
    string WrapperName,
    FrameKind Kind,
    FrameDef Frame,
    FrameDef ExportFrame,
    DesignObjectMetadata Metadata,
    int DrawOrder,
    int FrameLevel,
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
    string? JustifyV)
{
    public bool IsRegion => Kind is FrameKind.TEXTURE or FrameKind.FONTSTRING;
    public string OwnerRuntimeName => IsRegion ? WrapperName : RuntimeName;
    public string VisibilityRuntimeName => OwnerRuntimeName;
}

public sealed record WowExportRoot(double Width, double Height, FrameAnchor Anchor);

public sealed record WowExportAsset(
    IReadOnlyList<string> ProjectReferences,
    string SourcePhysicalPath,
    string PackageSource,
    string ContentSha256,
    string LogicalTexture,
    string PackagingTarget,
    string Conversion,
    int? SourceWidth,
    int? SourceHeight,
    int? PreparedWidth,
    int? PreparedHeight,
    int? TextureWidth,
    int? TextureHeight,
    int? MipCount,
    IReadOnlyList<string> Consumers);

public sealed record WowExportModel(
    string ProjectName,
    string RootRuntimeName,
    WowExportRoot Root,
    string? StockFoundationIdentity,
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
    // Draw layers are semantic within an owning frame; global stacking between the generated
    // wrapper frames is carried by parent relationships and FrameLevel. HIGHLIGHT must therefore
    // be opt-in rather than an accidental consequence of an object's design-order rank.
    private const string DefaultPersistentDrawLayer = "ARTWORK";

    public static WowExportModel Build(Project project, string? projectFilePath) =>
        Build(project, projectFilePath, Wow335ExportOptions.LegacyTga);

    public static WowExportModel Build(Project project, string? projectFilePath, Wow335ExportOptions options)
    {
        if (!double.IsFinite(options.TexelsPerUiPixel) || options.TexelsPerUiPixel <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "TexelsPerUiPixel must be finite and positive.");
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
        var rootRuntimeName = "FrameForge_" + SafeIdentifier(project.Name);
        var usedNames = new HashSet<string>(StringComparer.Ordinal) { rootRuntimeName };
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

        var ownerBySource = authored.ToDictionary(
            metadata => metadata.FrameName,
            metadata =>
            {
                var frame = project.Find(metadata.FrameName)!;
                var runtime = runtimeBySource[frame.Name];
                return frame.Kind is FrameKind.TEXTURE or FrameKind.FONTSTRING ? runtime + "__FFLayer" : runtime;
            },
            StringComparer.Ordinal);
        var layout = LayoutResolver.Resolve(project);
        var foundation = ResolveStockFoundation(project, stockGroup, stock);
        var screenRect = LayoutResolver.ScreenRect(project.Screen);
        var rootRect = ResolveRootRect(foundation, authored, layout, screenRect, diagnostics);
        var stockRuntimeParent = ResolveStockRuntimeParent(stockGroup, foundation);
        var rootParent = stockRuntimeParent ?? "UIParent";
        var rootReferenceRect = stockRuntimeParent is not null
            && foundation is not null
            && layout.Rects.TryGetValue(foundation.Name, out var foundationRect)
                ? foundationRect
                : screenRect;
        var root = new WowExportRoot(rootRect.Width, rootRect.Height, FrameAnchor.Create(
            AnchorPoint.TOPLEFT, rootParent, AnchorPoint.TOPLEFT,
            rootRect.Left - rootReferenceRect.Left, rootRect.Top - rootReferenceRect.Top));

        var projectDirectory = string.IsNullOrWhiteSpace(projectFilePath) ? null : Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        var assetConsumers = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var assetFrames = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var objects = new List<WowExportObject>();
        foreach (var metadata in authored.OrderBy(m => rank.GetValueOrDefault(m.FrameName, int.MaxValue)).ThenBy(m => m.FrameName, StringComparer.Ordinal))
        {
            var frame = project.Find(metadata.FrameName)!;
            var exportFrame = BuildExportFrame(frame, rootRuntimeName, rootRect, layout, ownerBySource, diagnostics);
            ValidateFrame(frame, exportFrame, metadata, stateIds, project, diagnostics);
            var runtime = runtimeBySource[frame.Name];
            string? texture = null;
            if (!string.IsNullOrWhiteSpace(metadata.DesignAsset))
            {
                var asset = NormalizePortablePath(metadata.DesignAsset!);
                var stem = SafePathSegment(Path.GetFileNameWithoutExtension(asset));
                texture = $@"Interface\FrameForge\{SafePathSegment(project.Name)}\{stem}";
                if (!assetConsumers.TryGetValue(asset, out var consumers)) assetConsumers[asset] = consumers = [];
                consumers.Add(runtime);
                if (!assetFrames.TryGetValue(asset, out var frames)) assetFrames[asset] = frames = [];
                frames.Add(frame.Name);
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

            foreach (var runtimeReference in metadata.RuntimeAssets)
            {
                var asset = NormalizePortablePath(runtimeReference);
                if (!assetConsumers.TryGetValue(asset, out var consumers)) assetConsumers[asset] = consumers = [];
                consumers.Add(runtime);
                if (!assetFrames.TryGetValue(asset, out var frames)) assetFrames[asset] = frames = [];
                frames.Add(frame.Name);
            }

            var style = metadata.TextStyle;
            var text = metadata.TextOverride ?? frame.Visual?.Text?.Text;
            var drawIndex = rank.GetValueOrDefault(frame.Name, objects.Count);
            objects.Add(new(
                frame.Name, runtime, runtime + "__FFLayer", frame.Kind, frame, exportFrame, metadata,
                drawIndex, 0, frame.Visual?.DrawLayer ?? DefaultPersistentDrawLayer, [.. metadata.StateIds], texture, text,
                style?.BaseStyle ?? frame.Visual?.Text?.FontTemplate, style?.Size, style?.Color, style?.Outline,
                style?.Shadow, style?.JustifyH ?? frame.Visual?.Text?.JustifyH, frame.Visual?.Text?.JustifyV));
        }

        var objectBySource = objects.ToDictionary(item => item.SourceName, StringComparer.Ordinal);
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        var resolvingLevels = new HashSet<string>(StringComparer.Ordinal);
        int ResolveLevel(WowExportObject item)
        {
            if (levels.TryGetValue(item.SourceName, out var level)) return level;
            level = Math.Max(item.DrawOrder + 1, item.Frame.LevelOrDefault);
            if (!resolvingLevels.Add(item.SourceName)) return level;
            if (item.Frame.Parent is { } parent && objectBySource.TryGetValue(parent, out var parentObject))
                level = Math.Max(level, ResolveLevel(parentObject) + 1);
            resolvingLevels.Remove(item.SourceName);
            return levels[item.SourceName] = level;
        }
        objects = objects.Select(item => item with { FrameLevel = ResolveLevel(item) }).ToList();

        var assets = BuildAssets(project.Name, projectDirectory, assetConsumers, assetFrames, objects, options, diagnostics,
            out var logicalByProjectReference, out var assetByProjectReference);
        objects = objects.Select(item => item.Metadata.DesignAsset is { Length: > 0 } reference
                && logicalByProjectReference.TryGetValue(NormalizePortablePath(reference), out var logical)
            ? ApplyTextureExportLayout(item with { TextureReference = logical },
                assetByProjectReference[NormalizePortablePath(reference)], diagnostics)
            : item).ToList();

        foreach (var conflict in objects
                     .Where(item => !string.IsNullOrWhiteSpace(item.Metadata.RuntimeBinding))
                     .GroupBy(item => item.Metadata.RuntimeBinding!, StringComparer.Ordinal)
                     .Where(group => group.Select(item => BindingValueType(item.Kind)).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            diagnostics.Add(new(ExportSeverity.Error, "BINDING_TYPE_CONFLICT",
                $"Runtime binding '{conflict.Key}' is used by incompatible value types: {string.Join(", ", conflict.Select(item => BindingValueType(item.Kind)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}."));
        }

        return new(project.Name, rootRuntimeName, root, stockGroup?.StockIdentity,
            [.. project.Editor.DesignStates], objects, assets, diagnostics);
    }

    private static FrameDef? ResolveStockFoundation(
        Project project,
        EditorGroup? stockGroup,
        IReadOnlySet<string> stock)
    {
        var identityName = stockGroup?.StockIdentity?.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return (identityName is null ? null : project.Find(identityName))
               ?? project.Frames.FirstOrDefault(frame => stock.Contains(frame.Name)
                   && (frame.Parent is null || !stock.Contains(frame.Parent)));
    }

    private static string? ResolveStockRuntimeParent(EditorGroup? stockGroup, FrameDef? foundation)
    {
        var identity = stockGroup?.StockIdentity?.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (identity is not { Length: >= 3 })
            return null;

        var source = identity[^2].Replace('\\', '/');
        var runtimeName = identity[^1];
        return source.Equals("Interface/FrameXML/LFDFrame.xml", StringComparison.OrdinalIgnoreCase)
               && runtimeName.Equals("LFDParentFrame", StringComparison.Ordinal)
               && foundation?.Name == runtimeName
            ? runtimeName
            : null;
    }

    private static FrameRect ResolveRootRect(
        FrameDef? foundation,
        IReadOnlyList<DesignObjectMetadata> authored,
        LayoutResult layout,
        FrameRect screenRect,
        List<ExportDiagnostic> diagnostics)
    {
        if (foundation is not null && layout.Rects.TryGetValue(foundation.Name, out var foundationRect)
            && foundationRect.Width > 0 && foundationRect.Height > 0)
            return foundationRect;

        FrameRect? bounds = null;
        foreach (var metadata in authored)
        {
            if (!layout.Rects.TryGetValue(metadata.FrameName, out var rect))
                continue;
            bounds = bounds is null ? rect : bounds.Value.Union(rect);
        }
        if (bounds is { Width: > 0, Height: > 0 } authoredBounds)
            return authoredBounds;

        diagnostics.Add(new(ExportSeverity.Warning, "ROOT_FALLBACK_SCREEN",
            "No stock foundation or authored bounds could define an export root; the project screen was used."));
        return screenRect;
    }

    private static FrameDef BuildExportFrame(
        FrameDef source,
        string rootRuntimeName,
        FrameRect rootRect,
        LayoutResult layout,
        IReadOnlyDictionary<string, string> ownerBySource,
        List<ExportDiagnostic> diagnostics)
    {
        if (!layout.Rects.TryGetValue(source.Name, out var sourceRect))
        {
            diagnostics.Add(new(ExportSeverity.Error, "LAYOUT_UNRESOLVED",
                "The object's authored geometry could not be resolved for native export.", source.Name));
            return source with { Parent = rootRuntimeName, RelativeTo = null };
        }

        var exportParent = source.Parent is { } parent && ownerBySource.TryGetValue(parent, out var parentOwner)
            ? parentOwner
            : rootRuntimeName;

        FrameAnchor ConvertAnchor(FrameAnchor anchor)
        {
            var sourceTarget = anchor.RelativeTo ?? source.Parent;
            if (sourceTarget is not null && ownerBySource.TryGetValue(sourceTarget, out var targetOwner))
            {
                return anchor with
                {
                    RelativeTo = anchor.RelativeTo is null && source.Parent == sourceTarget
                        ? null
                        : targetOwner,
                };
            }

            var ownPosition = LayoutResolver.AnchorPosition(sourceRect, anchor.Point);
            var rootPosition = LayoutResolver.AnchorPosition(rootRect, anchor.RelativePoint);
            return anchor with
            {
                RelativeTo = null,
                OffsetX = ownPosition.X - rootPosition.X,
                OffsetY = ownPosition.Y - rootPosition.Y,
            };
        }

        var primary = ConvertAnchor(source.PrimaryAnchor);
        var extras = source.ExtraAnchors.Select(ConvertAnchor).ToArray();
        var targetChangedToRoot = (source.RelativeTo ?? source.Parent) is not { } originalTarget
                                  || !ownerBySource.ContainsKey(originalTarget);
        var preserveSetAllPoints = source.SetAllPoints && (!targetChangedToRoot
            || (Math.Abs(sourceRect.Left - rootRect.Left) < 1e-6
                && Math.Abs(sourceRect.Top - rootRect.Top) < 1e-6
                && Math.Abs(sourceRect.Width - rootRect.Width) < 1e-6
                && Math.Abs(sourceRect.Height - rootRect.Height) < 1e-6));

        return source with
        {
            Parent = exportParent,
            Width = sourceRect.Width,
            Height = sourceRect.Height,
            SizeReference = SizeReference.SCREEN,
            Point = primary.Point,
            RelativeTo = primary.RelativeTo,
            RelativePoint = primary.RelativePoint,
            OffsetX = primary.OffsetX,
            OffsetY = primary.OffsetY,
            SetAllPoints = preserveSetAllPoints,
            ExtraAnchors = extras,
        };
    }

    private static void ValidateFrame(FrameDef frame, FrameDef exportFrame, DesignObjectMetadata metadata, HashSet<string> stateIds, Project project, List<ExportDiagnostic> diagnostics)
    {
        if (frame.Kind is FrameKind.OTHER or FrameKind.BUTTON)
            diagnostics.Add(new(ExportSeverity.Error, "OBJECT_UNSUPPORTED", "This DESIGN object type cannot be exported safely.", frame.Name));
        if (metadata.RuntimeAssets.Count > 0 && frame.Kind != FrameKind.TEXTURE)
            diagnostics.Add(new(ExportSeverity.Error, "RUNTIME_ASSET_TYPE_UNSUPPORTED", "Runtime-swapped assets can only be attached to a Texture DESIGN object.", frame.Name));
        if (frame.Visual?.DrawLayer is { } layer && layer is not ("BACKGROUND" or "BORDER" or "ARTWORK" or "OVERLAY" or "HIGHLIGHT"))
            diagnostics.Add(new(ExportSeverity.Error, "DRAW_LAYER_INVALID", $"Draw layer '{layer}' is not supported by WoW 3.3.5a.", frame.Name));
        var invalidDeclaredSize = !Finite(frame.Width) || !Finite(frame.Height) || frame.Width < 0 || frame.Height < 0
                                  || ((frame.Width == 0 || frame.Height == 0) && !frame.HasMultipleAnchors && !frame.SetAllPoints);
        if (invalidDeclaredSize || !Finite(exportFrame.Width) || !Finite(exportFrame.Height) || exportFrame.Width <= 0 || exportFrame.Height <= 0
            || exportFrame.AllAnchors.Any(anchor => !Finite(anchor.OffsetX) || !Finite(anchor.OffsetY)))
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

    private static bool HasAuthoredOverride(DesignObjectMetadata m) => m.TextOverride is not null || m.TextStyle is not null || m.DesignAsset is not null || m.RuntimeAssets.Count > 0 || m.RuntimeValueRequired || m.RuntimeBinding is not null || m.StateIds.Count > 0;
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

    private sealed record AssetCandidate(string ProjectReference, string PhysicalPath, string ContentSha256,
        string Extension, string BaseFileName, string BaseLogicalStem, IReadOnlyList<string> Consumers,
        int? SourceWidth, int? SourceHeight, int? PreparedWidth, int? PreparedHeight,
        int? TextureWidth, int? TextureHeight, int? MipCount);

    private static IReadOnlyList<WowExportAsset> BuildAssets(
        string projectName,
        string? projectDirectory,
        IReadOnlyDictionary<string, List<string>> assetConsumers,
        IReadOnlyDictionary<string, List<string>> assetFrames,
        IReadOnlyList<WowExportObject> objects,
        Wow335ExportOptions options,
        List<ExportDiagnostic> diagnostics,
        out IReadOnlyDictionary<string, string> logicalByProjectReference,
        out IReadOnlyDictionary<string, WowExportAsset> assetByProjectReference)
    {
        var candidates = new List<AssetCandidate>();
        foreach (var pair in assetConsumers.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var frameName = assetFrames[pair.Key].Order(StringComparer.Ordinal).First();
            if (!TryResolveProjectAsset(pair.Key, projectDirectory, frameName, diagnostics, out var physical, out var hash))
                continue;
            var extension = Path.GetExtension(pair.Key).ToLowerInvariant();
            if (extension is not (".png" or ".tga" or ".blp"))
            {
                diagnostics.Add(new(ExportSeverity.Error, "ASSET_FORMAT_UNSUPPORTED",
                    $"Project artwork '{pair.Key}' must be PNG, TGA, or BLP.", frameName));
                continue;
            }
            int? sourceWidth = null;
            int? sourceHeight = null;
            int? preparedWidth = null;
            int? preparedHeight = null;
            int? textureWidth = null;
            int? textureHeight = null;
            int? mipCount = null;
            if (extension == ".png")
            {
                try
                {
                    using var source = new FileStream(physical!, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (options.TextureFormat == WowTextureFormat.Tga)
                    {
                        var layout = PngTgaConverter.Validate(source);
                        sourceWidth = preparedWidth = layout.SourceWidth;
                        sourceHeight = preparedHeight = layout.SourceHeight;
                        textureWidth = layout.Width;
                        textureHeight = layout.Height;
                        mipCount = 1;
                    }
                    else
                    {
                        var dimensions = Blp2Dxt5Converter.ReadPngDimensions(source);
                        var objectBySource = objects.ToDictionary(item => item.SourceName, StringComparer.Ordinal);
                        var requiredWidth = 1d;
                        var requiredHeight = 1d;
                        foreach (var consumerFrame in assetFrames[pair.Key])
                        {
                            if (!objectBySource.TryGetValue(consumerFrame, out var consumer))
                                continue;
                            var coordinates = consumer.Frame.Visual?.Texture?.TexCoords ?? TexCoords.Full;
                            if (!coordinates.IsValid || coordinates.Width <= 0 || coordinates.Height <= 0)
                                continue;
                            requiredWidth = Math.Max(requiredWidth,
                                consumer.ExportFrame.Width * options.TexelsPerUiPixel / coordinates.Width);
                            requiredHeight = Math.Max(requiredHeight,
                                consumer.ExportFrame.Height * options.TexelsPerUiPixel / coordinates.Height);
                        }
                        var layout = Blp2Dxt5Converter.CreateLayout(dimensions.Width, dimensions.Height,
                            requiredWidth, requiredHeight,
                            options.ResolutionPolicy == WowTextureResolutionPolicy.PreserveSource);
                        sourceWidth = layout.SourceWidth;
                        sourceHeight = layout.SourceHeight;
                        preparedWidth = layout.PreparedWidth;
                        preparedHeight = layout.PreparedHeight;
                        textureWidth = layout.Width;
                        textureHeight = layout.Height;
                        mipCount = layout.MipCount;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
                {
                    diagnostics.Add(new(ExportSeverity.Error, "ASSET_TEXTURE_INVALID",
                        $"Project artwork '{pair.Key}' cannot be converted for WoW 3.3.5a: {ex.Message}", frameName));
                    continue;
                }
            }
            var stem = SafePathSegment(Path.GetFileNameWithoutExtension(pair.Key));
            if (stem.Length == 0) stem = "asset";
            candidates.Add(new(pair.Key, physical!, hash!, extension, stem + extension, stem,
                [.. pair.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                sourceWidth, sourceHeight, preparedWidth, preparedHeight, textureWidth, textureHeight, mipCount));
        }

        var fileCollisions = candidates.GroupBy(item => item.BaseFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.ContentSha256).Distinct(StringComparer.Ordinal).Count() > 1,
                StringComparer.OrdinalIgnoreCase);
        var logicalCollisions = candidates.GroupBy(item => item.BaseLogicalStem, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.ContentSha256).Distinct(StringComparer.Ordinal).Count() > 1,
                StringComparer.OrdinalIgnoreCase);

        var resolved = candidates.Select(item =>
        {
            var suffix = item.ContentSha256[..12].ToLowerInvariant();
            var fileName = fileCollisions[item.BaseFileName]
                ? $"{item.BaseLogicalStem}-{suffix}{item.Extension}"
                : item.BaseFileName;
            var logicalStem = logicalCollisions[item.BaseLogicalStem]
                ? $"{item.BaseLogicalStem}_{suffix}"
                : item.BaseLogicalStem;
            return (Candidate: item, FileName: fileName, LogicalStem: logicalStem);
        }).ToArray();

        var canonicalPackageByHash = resolved.GroupBy(item => item.Candidate.ContentSha256, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => "assets/" + group.Select(item => item.FileName).Order(StringComparer.Ordinal).First(),
                StringComparer.Ordinal);
        var logicalMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new List<WowExportAsset>();
        foreach (var group in resolved.GroupBy(item => (item.LogicalStem, item.Candidate.ContentSha256)))
        {
            var members = group.OrderBy(item => item.Candidate.ProjectReference, StringComparer.Ordinal).ToArray();
            var logical = $@"Interface\FrameForge\{SafePathSegment(projectName)}\{group.Key.LogicalStem}";
            foreach (var member in members)
                logicalMap[member.Candidate.ProjectReference] = logical;
            var canonical = members[0].Candidate;
            rows.Add(new(
                [.. members.Select(item => item.Candidate.ProjectReference).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                canonical.PhysicalPath,
                canonicalPackageByHash[group.Key.ContentSha256],
                group.Key.ContentSha256,
                logical,
                $"textures/{group.Key.LogicalStem}{(canonical.Extension == ".png" && options.TextureFormat == WowTextureFormat.Blp2Dxt5 ? ".blp" : ".tga")}",
                canonical.Extension == ".png" ? "png-to-wow-compatible-tga-or-blp" : "copy-wow-compatible-artwork",
                canonical.SourceWidth,
                canonical.SourceHeight,
                canonical.PreparedWidth,
                canonical.PreparedHeight,
                canonical.TextureWidth,
                canonical.TextureHeight,
                canonical.MipCount,
                [.. members.SelectMany(item => item.Candidate.Consumers).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]));
        }
        logicalByProjectReference = logicalMap;
        assetByProjectReference = rows
            .SelectMany(asset => asset.ProjectReferences.Select(reference => (reference, asset)))
            .ToDictionary(item => item.reference, item => item.asset, StringComparer.Ordinal);
        return [.. rows.OrderBy(item => item.LogicalTexture, StringComparer.Ordinal)];
    }

    private static WowExportObject ApplyTextureExportLayout(
        WowExportObject item,
        WowExportAsset asset,
        List<ExportDiagnostic> diagnostics)
    {
        if (item.Kind != FrameKind.TEXTURE
            || asset.PreparedWidth is not { } preparedWidth
            || asset.PreparedHeight is not { } preparedHeight
            || asset.TextureWidth is not { } textureWidth
            || asset.TextureHeight is not { } textureHeight
            || (preparedWidth == textureWidth && preparedHeight == textureHeight))
            return item;

        var visual = item.ExportFrame.Visual ?? new FrameVisual();
        var texture = visual.Texture ?? new TextureVisual(null, TexCoords.Full);
        var coordinates = texture.TexCoords;
        if (!coordinates.IsValid || coordinates.Width <= 0 || coordinates.Height <= 0)
        {
            diagnostics.Add(new(ExportSeverity.Error, "TEXTURE_COORDS_INVALID",
                "Texture coordinates must be a finite, positive rectangular mapping before power-of-two padding can be applied.", item.SourceName));
            return item;
        }

        var widthScale = (double)preparedWidth / textureWidth;
        var heightScale = (double)preparedHeight / textureHeight;
        var transformed = new TexCoords(
            coordinates.Left * widthScale,
            coordinates.Right * widthScale,
            coordinates.Top * heightScale,
            coordinates.Bottom * heightScale);
        return item with
        {
            ExportFrame = item.ExportFrame with
            {
                Visual = visual with { Texture = texture with { TexCoords = transformed } },
            },
        };
    }

    private static bool TryResolveProjectAsset(string reference, string? projectDirectory, string frameName,
        List<ExportDiagnostic> diagnostics, out string? physical, out string? hash)
    {
        physical = null;
        hash = null;
        if (projectDirectory is null)
        {
            diagnostics.Add(new(ExportSeverity.Error, "ASSET_PROJECT_UNSAVED",
                $"Project artwork '{reference}' cannot be packaged until the project has a file location.", frameName));
            return false;
        }
        if (Path.IsPathRooted(reference) || IsMachinePath(reference))
        {
            diagnostics.Add(new(ExportSeverity.Error, "ASSET_ABSOLUTE",
                $"Project artwork '{reference}' must use a portable path relative to the .fforge.json file.", frameName));
            return false;
        }
        try
        {
            physical = Path.GetFullPath(Path.Combine(projectDirectory,
                reference.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(projectDirectory, physical);
            if (Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                diagnostics.Add(new(ExportSeverity.Error, "ASSET_ESCAPE",
                    $"Project artwork '{reference}' escapes the project directory.", frameName));
                return false;
            }
            if (!File.Exists(physical))
            {
                diagnostics.Add(new(ExportSeverity.Error, "ASSET_UNRESOLVED",
                    $"Project artwork '{reference}' was not found beside the project.", frameName));
                return false;
            }
            using var stream = new FileStream(physical, FileMode.Open, FileAccess.Read, FileShare.Read);
            hash = Convert.ToHexString(SHA256.HashData(stream));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new(ExportSeverity.Error, "ASSET_UNREADABLE",
                $"Project artwork '{reference}' could not be read: {ex.Message}", frameName));
            return false;
        }
    }
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

/// <summary>Writes deterministic FrameXML, versioned contracts, and project-owned source artwork.</summary>
public static class Wow335Exporter
{
    public const string XmlFileName = "FrameForgeLayout.xml";
    public const string ManifestFileName = "frameforge-manifest.json";
    public const string AssetsFileName = "assets-manifest.json";
    public const string ReportFileName = "export-report.txt";

    private sealed record GeneratedTexture(string Hash, Blp2Verification? BlpVerification);

    /// <summary>Compatibility overload retaining the historical TGA output.</summary>
    public static WowExportResult Export(Project project, string? projectFilePath, string destination) =>
        Export(project, projectFilePath, destination, Wow335ExportOptions.LegacyTga);

    public static WowExportResult Export(Project project, string? projectFilePath, string destination,
        Wow335ExportOptions options)
    {
        var model = Wow335ExportBuilder.Build(project, projectFilePath, options);
        if (model.Diagnostics.Any(d => d.Severity == ExportSeverity.Error))
            return new(false, model, model.Diagnostics, []);
        // Folder pickers may preserve a trailing separator. Keep one canonical root
        // for both staging and final-package containment checks.
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        Directory.CreateDirectory(destinationRoot);
        var staging = Path.Combine(destinationRoot, $".frameforge-staging-{Guid.NewGuid():N}");
        var finalFiles = new[]
        {
            Path.Combine(destinationRoot, XmlFileName), Path.Combine(destinationRoot, ManifestFileName),
            Path.Combine(destinationRoot, AssetsFileName), Path.Combine(destinationRoot, ReportFileName),
        };
        try
        {
            Directory.CreateDirectory(staging);
            var generatedTextures = new Dictionary<string, GeneratedTexture>(StringComparer.Ordinal);
            var stagedFiles = new[]
            {
                Path.Combine(staging, XmlFileName), Path.Combine(staging, ManifestFileName),
                Path.Combine(staging, AssetsFileName), Path.Combine(staging, ReportFileName),
            };
            foreach (var group in model.Assets.GroupBy(asset => asset.PackageSource, StringComparer.Ordinal))
            {
                var asset = group.First();
                var target = ResolvePackagePath(staging, asset.PackageSource);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(asset.SourcePhysicalPath, target, true);
                using var copied = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                var copiedHash = Convert.ToHexString(SHA256.HashData(copied));
                if (!string.Equals(copiedHash, asset.ContentSha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Project artwork '{asset.ProjectReferences[0]}' changed while the export was being written.");
            }

            foreach (var asset in model.Assets.Where(IsPngConversion))
            {
                try
                {
                    var sourcePath = ResolvePackagePath(staging, asset.PackageSource);
                    var targetPath = ResolvePackagePath(staging, asset.PackagingTarget);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    Blp2Verification? blpVerification = null;
                    using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (IsBlpConversion(asset))
                        {
                            var layout = AssetBlpLayout(asset);
                            blpVerification = Blp2Dxt5Converter.Convert(source, target, layout);
                        }
                        else
                        {
                            var dimensions = PngTgaConverter.Convert(source, target);
                            if (dimensions.SourceWidth != asset.SourceWidth || dimensions.SourceHeight != asset.SourceHeight
                                || dimensions.Width != asset.TextureWidth || dimensions.Height != asset.TextureHeight)
                                throw new InvalidDataException("PNG dimensions changed while the export was being written.");
                        }
                    }
                    if (IsBlpConversion(asset))
                    {
                        using var verify = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        _ = Blp2Dxt5Converter.Verify(verify, AssetBlpLayout(asset));
                    }
                    else
                    {
                        ValidateGeneratedTga(targetPath, asset.TextureWidth!.Value, asset.TextureHeight!.Value);
                    }
                    using var generated = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    generatedTextures[asset.PackagingTarget] = new(
                        Convert.ToHexString(SHA256.HashData(generated)), blpVerification);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                {
                    var diagnostic = new ExportDiagnostic(ExportSeverity.Error, "ASSET_CONVERSION_FAILED",
                        $"Project artwork '{asset.ProjectReferences[0]}' could not be converted to '{asset.PackagingTarget}': {ex.Message}");
                    var failedDiagnostics = model.Diagnostics.Append(diagnostic).ToArray();
                    return new(false, model with { Diagnostics = failedDiagnostics }, failedDiagnostics, []);
                }
            }

            File.WriteAllText(stagedFiles[0], WriteXml(model), new UTF8Encoding(false));
            File.WriteAllText(stagedFiles[1], WriteManifest(model), new UTF8Encoding(false));
            File.WriteAllText(stagedFiles[2], WriteAssets(model, generatedTextures), new UTF8Encoding(false));
            var roundTrip = FrameXmlImporter.ImportFile(stagedFiles[0]);
            if (!roundTrip.Ok || roundTrip.Project is null)
                throw new InvalidDataException("Generated FrameXML failed the independent importer self-check: " +
                                               string.Join(" ", roundTrip.Errors.Select(e => e.Message)));
            var importedNames = roundTrip.Project.Frames.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            var missing = model.Objects.Select(o => o.RuntimeName).Where(name => !importedNames.Contains(name)).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException($"Generated FrameXML importer self-check lost {missing.Length} runtime object(s): {string.Join(", ", missing.Take(5))}.");
            File.WriteAllText(stagedFiles[3], WriteReport(model, options, generatedTextures)
                + $"Round-trip self-check: PASS ({model.Objects.Count}/{model.Objects.Count} runtime identities)\n", new UTF8Encoding(false));
            foreach (var path in stagedFiles)
            {
                var text = File.ReadAllText(path);
                if (Regex.IsMatch(text, @"[A-Za-z]:\\|/home/|AppData", RegexOptions.IgnoreCase))
                    throw new InvalidDataException($"Portable export audit rejected a machine-local path in {Path.GetFileName(path)}.");
            }
            using (var reader = XmlReader.Create(stagedFiles[0]))
                while (reader.Read()) { }

            PublishStagedPackage(staging, destinationRoot, stagedFiles, finalFiles, model.Assets,
                generatedTextures.ToDictionary(item => item.Key, item => item.Value.Hash, StringComparer.Ordinal));
            return new(true, model, model.Diagnostics, finalFiles);
        }
        finally
        {
            // This is the exact unique directory created above; never broaden cleanup
            // to the selected directory or to a wildcard match.
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    private static void PublishStagedPackage(string staging, string destinationRoot,
        IReadOnlyList<string> stagedFiles, IReadOnlyList<string> finalFiles, IReadOnlyList<WowExportAsset> assets,
        IReadOnlyDictionary<string, string> generatedHashes)
    {
        var previouslyManaged = ReadPreviouslyManagedAssets(destinationRoot);
        var newAssets = assets.GroupBy(asset => asset.PackageSource, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ContentSha256, StringComparer.Ordinal);
        foreach (var generated in generatedHashes)
            newAssets[generated.Key] = generated.Value;

        foreach (var pair in newAssets)
        {
            var target = ResolvePackagePath(destinationRoot, pair.Key);
            if (!File.Exists(target))
                continue;
            using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            var existingHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(existingHash, pair.Value, StringComparison.Ordinal)
                && !previouslyManaged.ContainsKey(pair.Key))
                throw new InvalidDataException($"Export asset '{pair.Key}' would overwrite a file not owned by a previous FrameForge export.");
        }

        foreach (var pair in newAssets.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var source = ResolvePackagePath(staging, pair.Key);
            var target = ResolvePackagePath(destinationRoot, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, true);
        }
        for (var index = 0; index < stagedFiles.Count; index++)
            File.Copy(stagedFiles[index], finalFiles[index], true);

        foreach (var stale in previouslyManaged.Keys.Except(newAssets.Keys, StringComparer.Ordinal))
        {
            var path = ResolvePackagePath(destinationRoot, stale);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static Dictionary<string, string> ReadPreviouslyManagedAssets(string destinationRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var manifestPath = Path.Combine(destinationRoot, AssetsFileName);
        if (!File.Exists(manifestPath))
            return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("schema", out var schema)
                || schema.GetString() != "frameforge-wow335-assets"
                || !root.TryGetProperty("version", out var version) || version.GetInt32() != 2
                || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.String
                    || !asset.TryGetProperty("contentSha256", out var hash) || hash.ValueKind != JsonValueKind.String)
                    continue;
                var relative = source.GetString();
                if (relative is null || !relative.StartsWith("assets/", StringComparison.Ordinal))
                    continue;
                _ = ResolvePackagePath(destinationRoot, relative);
                result[relative] = hash.GetString()!;

                if (!asset.TryGetProperty("generatedFile", out var generatedFile) || generatedFile.ValueKind != JsonValueKind.String
                    || !asset.TryGetProperty("generatedContentSha256", out var generatedHash) || generatedHash.ValueKind != JsonValueKind.String)
                    continue;
                var generatedRelative = generatedFile.GetString();
                if (generatedRelative is null || !generatedRelative.StartsWith("textures/", StringComparison.Ordinal))
                    continue;
                _ = ResolvePackagePath(destinationRoot, generatedRelative);
                result[generatedRelative] = generatedHash.GetString()!;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(StringComparer.Ordinal);
        }
        return result;
    }

    private static bool IsPngConversion(WowExportAsset asset) =>
        asset.Conversion == "png-to-wow-compatible-tga-or-blp";

    private static bool IsBlpConversion(WowExportAsset asset) =>
        IsPngConversion(asset) && asset.PackagingTarget.EndsWith(".blp", StringComparison.OrdinalIgnoreCase);

    private static Blp2TextureLayout AssetBlpLayout(WowExportAsset asset) => new(
        asset.SourceWidth!.Value, asset.SourceHeight!.Value,
        asset.PreparedWidth!.Value, asset.PreparedHeight!.Value,
        asset.TextureWidth!.Value, asset.TextureHeight!.Value, asset.MipCount!.Value);

    private static void ValidateGeneratedTga(string path, int width, int height)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[18];
        stream.ReadExactly(header);
        if (header[0] != 0 || header[1] != 0 || header[2] != 2
            || BinaryPrimitives.ReadUInt16LittleEndian(header[12..14]) != width
            || BinaryPrimitives.ReadUInt16LittleEndian(header[14..16]) != height
            || header[16] != 32 || header[17] != 0x28)
            throw new InvalidDataException("Generated TGA header is not uncompressed 32-bit true-color with a top-left origin.");
        var expectedLength = checked(18L + (long)width * height * 4);
        if (stream.Length != expectedLength)
            throw new InvalidDataException($"Generated TGA length is {stream.Length} bytes; expected {expectedLength}.");
    }

    internal static string ResolvePackagePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidDataException($"Export package path '{relative}' is not portable.");

        // Treat both separator spellings as package separators on every host so a
        // path unsafe on Windows cannot become a harmless-looking filename on Unix.
        var portableRelative = relative
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || Path.IsPathRooted(portableRelative)
            || Regex.IsMatch(relative, @"^[A-Za-z]:[\\/]"))
            throw new InvalidDataException($"Export package path '{relative}' is not portable.");

        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(rootPath, portableRelative));
        var fromRoot = Path.GetRelativePath(rootPath, path);
        if (Path.IsPathRooted(fromRoot)
            || string.Equals(fromRoot, "..", StringComparison.Ordinal)
            || fromRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || fromRoot.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Export package path '{relative}' escapes the selected directory.");
        return path;
    }

    public static string WriteXml(WowExportModel model)
    {
        var output = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, NewLineChars = "\n", Encoding = new UTF8Encoding(false) };
        using var x = XmlWriter.Create(output, settings);
        x.WriteStartElement("Ui", "http://www.blizzard.com/wow/ui/");
        x.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
        x.WriteAttributeString("xsi", "schemaLocation", null, "http://www.blizzard.com/wow/ui/ ..\\FrameXML\\UI.xsd");
        var rootParent = model.Root.Anchor.RelativeTo ?? "UIParent";
        x.WriteStartElement("Frame"); x.WriteAttributeString("name", model.RootRuntimeName); x.WriteAttributeString("parent", rootParent); x.WriteAttributeString("hidden", "true");
        WriteSize(x, model.Root.Width, model.Root.Height);
        x.WriteStartElement("Anchors"); WriteAnchor(x, model.Root.Anchor, rootParent); x.WriteEndElement();
        x.WriteStartElement("Frames");
        foreach (var item in model.Objects.OrderBy(o => o.DrawOrder).ThenBy(o => o.RuntimeName, StringComparer.Ordinal)) WriteObject(x, item);
        x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        x.Flush();
        return output.ToString();
    }

    private static void WriteObject(XmlWriter x, WowExportObject item)
    {
        var region = item.Kind is FrameKind.TEXTURE or FrameKind.FONTSTRING;
        if (region)
        {
            x.WriteStartElement("Frame"); x.WriteAttributeString("name", item.WrapperName); WriteCommonAttributes(x, item); WriteGeometry(x, item);
            x.WriteStartElement("Layers"); x.WriteStartElement("Layer"); x.WriteAttributeString("level", item.DrawLayer);
            WriteRegion(x, item);
            x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
            return;
        }
        x.WriteStartElement(item.Kind.TagName()); x.WriteAttributeString("name", item.RuntimeName); WriteCommonAttributes(x, item);
        if (item.Kind == FrameKind.STATUSBAR && item.Frame.Visual!.StatusBar is { } bar)
        {
            x.WriteAttributeString("minValue", N(bar.MinValue!.Value)); x.WriteAttributeString("maxValue", N(bar.MaxValue!.Value));
            WriteGeometry(x, item);
            if (item.TextureReference is { } texture) { x.WriteStartElement("BarTexture"); x.WriteAttributeString("file", texture); x.WriteEndElement(); }
            if (bar.BarColor is { } color) WriteColor(x, "BarColor", color);
        }
        else
        {
            WriteGeometry(x, item);
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
            if (item.ExportFrame.Visual?.Texture?.TexCoords is { IsSubRectangle: true } tc)
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
            // In build 12340's UI.xsd, FontString owns the font and outline attributes and a
            // FontHeight child.  A nested <Font size="..."> is not part of FontStringType.
            // Supplying the concrete font together with FontHeight makes the explicit size an
            // effective declaration instead of leaving the inherited Font object's height active.
            if (item.FontSize is not null) x.WriteAttributeString("font", @"Fonts\FRIZQT__.TTF");
            if (item.Outline is "NONE" or "NORMAL" or "THICK") x.WriteAttributeString("outline", item.Outline);
            if (item.FontSize is { } size)
            {
                x.WriteStartElement("FontHeight");
                x.WriteStartElement("AbsValue"); x.WriteAttributeString("val", N(size)); x.WriteEndElement();
                x.WriteEndElement();
            }
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
        if (item.ExportFrame.Parent is { } parent) x.WriteAttributeString("parent", parent);
        x.WriteAttributeString("frameStrata", item.Frame.StratumOrDefault.ToString()); x.WriteAttributeString("frameLevel", item.FrameLevel.ToString(CultureInfo.InvariantCulture));
        if (!item.Frame.Visible || item.StateIds.Count > 0) x.WriteAttributeString("hidden", "true");
    }
    private static void WriteGeometry(XmlWriter x, WowExportObject item)
    {
        if (item.ExportFrame.SetAllPoints) x.WriteAttributeString("setAllPoints", "true");
        else
        {
            WriteSize(x, item.ExportFrame.Width, item.ExportFrame.Height);
            x.WriteStartElement("Anchors"); WriteAnchor(x, item.ExportFrame.PrimaryAnchor, item.ExportFrame.RelativeTo ?? item.ExportFrame.Parent ?? "UIParent");
            foreach (var anchor in item.ExportFrame.ExtraAnchors)
                WriteAnchor(x, anchor, anchor.RelativeTo ?? item.ExportFrame.Parent ?? "UIParent");
            x.WriteEndElement();
        }
    }
    private static void WriteSize(XmlWriter x, double width, double height)
    {
        x.WriteStartElement("Size"); x.WriteStartElement("AbsDimension"); x.WriteAttributeString("x", N(width)); x.WriteAttributeString("y", N(height)); x.WriteEndElement(); x.WriteEndElement();
    }
    private static void WriteAnchor(XmlWriter x, FrameAnchor anchor, string relative)
    {
        x.WriteStartElement("Anchor"); x.WriteAttributeString("point", anchor.Point.ToString()); x.WriteAttributeString("relativeTo", relative); x.WriteAttributeString("relativePoint", anchor.RelativePoint.ToString());
        x.WriteStartElement("Offset"); x.WriteStartElement("AbsDimension"); x.WriteAttributeString("x", N(anchor.OffsetX)); x.WriteAttributeString("y", N(anchor.OffsetY)); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
    }
    private static void WriteColor(XmlWriter x, string element, ColorRgba c) { x.WriteStartElement(element); x.WriteAttributeString("r", N(c.R)); x.WriteAttributeString("g", N(c.G)); x.WriteAttributeString("b", N(c.B)); x.WriteAttributeString("a", N(c.A)); x.WriteEndElement(); }

    public static string WriteManifest(WowExportModel model)
    {
        var rootParent = model.Root.Anchor.RelativeTo ?? "UIParent";
        var states = model.States.Select(state => new
        {
            state.Id,
            state.Name,
            show = model.Objects.Where(o => o.Frame.Visible && (o.StateIds.Count == 0 || o.StateIds.Contains(state.Id, StringComparer.Ordinal))).Select(o => o.VisibilityRuntimeName).ToArray(),
            hide = model.Objects.Where(o => !o.Frame.Visible || (o.StateIds.Count > 0 && !o.StateIds.Contains(state.Id, StringComparer.Ordinal))).Select(o => o.VisibilityRuntimeName).ToArray(),
        }).ToArray();
        var bindings = model.Objects.Select(o => new
        {
            sourceObject = o.SourceName,
            runtimeId = o.RuntimeName,
            visibilityRuntimeId = o.VisibilityRuntimeName,
            type = o.Kind.TagName(),
            allStates = o.StateIds.Count == 0,
            states = o.StateIds,
            runtimeValueRequired = o.Metadata.RuntimeValueRequired,
            runtimeBinding = o.Metadata.RuntimeValueRequired ? o.Metadata.RuntimeBinding : null,
            valueType = o.Metadata.RuntimeValueRequired ? ManifestValueType(o.Kind) : null,
            operation = o.Metadata.RuntimeValueRequired ? o.Kind == FrameKind.STATUSBAR ? "SetValue" : "SetText" : null,
            editorPreviewDefault = o.Kind == FrameKind.STATUSBAR ? o.Frame.Visual?.StatusBar?.DefaultValue : null,
        }).ToArray();
        return JsonSerializer.Serialize(new { schema = "frameforge-wow335-manifest", version = 2, target = new { game = "World of Warcraft", version = "3.3.5a", build = 12340 }, project = model.ProjectName, layout = XmlFileName, rootRuntimeId = model.RootRuntimeName, rootParentRuntimeId = rootParent, stockFoundation = new { identity = model.StockFoundationIdentity, runtimeParent = rootParent == "UIParent" ? null : rootParent, strategy = "artwork-and-template-reference-only" }, states, bindings }, JsonOptions());
    }
    public static string WriteAssets(WowExportModel model) => WriteAssets(model, null);

    private static string WriteAssets(WowExportModel model, IReadOnlyDictionary<string, GeneratedTexture>? generatedTextures) => JsonSerializer.Serialize(new
    {
        schema = "frameforge-wow335-assets",
        version = 2,
        note = "Project-owned source artwork is packaged below this manifest; PNG sources also produce game-ready TGA or BLP2/DXT5 files. Stock client artwork is referenced and never copied.",
        assets = model.Assets.Select(a => new
        {
            source = a.PackageSource,
            projectReferences = a.ProjectReferences,
            contentSha256 = a.ContentSha256,
            logicalTexture = a.LogicalTexture,
            packagingTarget = a.PackagingTarget,
            conversion = a.Conversion,
            sourceWidth = a.SourceWidth,
            sourceHeight = a.SourceHeight,
            preparedWidth = a.PreparedWidth,
            preparedHeight = a.PreparedHeight,
            width = a.TextureWidth,
            height = a.TextureHeight,
            paddingApplied = IsPngConversion(a)
                ? a.PreparedWidth != a.TextureWidth || a.PreparedHeight != a.TextureHeight
                : (bool?)null,
            resized = IsPngConversion(a)
                ? a.SourceWidth != a.PreparedWidth || a.SourceHeight != a.PreparedHeight
                : (bool?)null,
            mipCount = IsPngConversion(a) ? a.MipCount : null,
            generatedFile = IsPngConversion(a) ? a.PackagingTarget : null,
            generatedContentSha256 = IsPngConversion(a) && generatedTextures is not null
                && generatedTextures.TryGetValue(a.PackagingTarget, out var generated) ? generated.Hash : null,
            requiredMpqDestination = IsPngConversion(a)
                ? a.LogicalTexture.Replace('\\', '/') + Path.GetExtension(a.PackagingTarget).ToLowerInvariant()
                : null,
            generatedFormat = IsPngConversion(a)
                ? IsBlpConversion(a) ? "blp2-dxt5-alpha8-mipmapped" : "tga-type-2-bgra32-top-left"
                : null,
            decodedPremultipliedColorMeanAbsoluteError = generatedTextures is not null
                && generatedTextures.TryGetValue(a.PackagingTarget, out var verifiedColor)
                ? verifiedColor.BlpVerification?.PremultipliedColorMeanAbsoluteError : null,
            decodedAlphaMeanAbsoluteError = generatedTextures is not null
                && generatedTextures.TryGetValue(a.PackagingTarget, out var verifiedAlpha)
                ? verifiedAlpha.BlpVerification?.AlphaMeanAbsoluteError : null,
            consumers = a.Consumers,
        }),
    }, JsonOptions());
    public static string WriteReport(WowExportModel model) =>
        WriteReport(model, model.Assets.Any(IsBlpConversion)
            ? Wow335ExportOptions.Preferred
            : Wow335ExportOptions.LegacyTga, null);

    private static string WriteReport(WowExportModel model, Wow335ExportOptions options,
        IReadOnlyDictionary<string, GeneratedTexture>? generatedTextures)
    {
        var rootParent = model.Root.Anchor.RelativeTo ?? "UIParent";
        var generated = model.Assets.Where(IsPngConversion).ToArray();
        var b = new StringBuilder(); b.AppendLine("FrameForge WoW 3.3.5a export report"); b.AppendLine($"Project: {model.ProjectName}"); b.AppendLine($"Objects: {model.Objects.Count}"); b.AppendLine($"Runtime bindings: {model.Objects.Count(o => o.Metadata.RuntimeValueRequired)}"); b.AppendLine($"States: {string.Join(", ", model.States.Select(s => s.Name))}"); b.AppendLine($"Project assets: {model.Assets.Count}"); b.AppendLine($"Packaged source files: {model.Assets.Select(a => a.PackageSource).Distinct(StringComparer.Ordinal).Count()}"); b.AppendLine($"Generated texture format: {options.TextureFormat}"); b.AppendLine($"Resolution policy: {options.ResolutionPolicy} ({N(options.TexelsPerUiPixel)} source texels per largest displayed UI pixel; never upscale)"); b.AppendLine($"Generated textures: {generated.Length} ({generated.Count(a => a.SourceWidth != a.PreparedWidth || a.SourceHeight != a.PreparedHeight)} resized; {generated.Count(a => a.PreparedWidth != a.TextureWidth || a.PreparedHeight != a.TextureHeight)} power-of-two padded)"); b.AppendLine($"Root: {model.RootRuntimeName}, parent {rootParent}, size {N(model.Root.Width)}x{N(model.Root.Height)}, initially hidden"); b.AppendLine(rootParent == "UIParent" ? "Stock foundation: geometry provenance only; not a runtime parent; not copied" : $"Stock foundation: runtime parent {rootParent}; stock definitions and artwork not copied"); b.AppendLine("StatusBar defaultValue: editor preview metadata only; omitted from FrameXML runtime initialization");
        if (generatedTextures is not null && generatedTextures.Values.Any(item => item.BlpVerification is not null))
        {
            var checks = generatedTextures.Values.Select(item => item.BlpVerification).Where(item => item is not null).Select(item => item!.Value).ToArray();
            b.AppendLine($"Independent DXT5 decode: PASS ({checks.Length}/{checks.Length}; worst premultiplied RGB MAE {checks.Max(item => item.PremultipliedColorMeanAbsoluteError):0.###}; worst alpha MAE {checks.Max(item => item.AlphaMeanAbsoluteError):0.###})");
        }
        b.AppendLine(); b.AppendLine("Diagnostics:");
        foreach (var d in model.Diagnostics.OrderBy(d => d.Severity).ThenBy(d => d.Code, StringComparer.Ordinal).ThenBy(d => d.FrameName, StringComparer.Ordinal)) b.AppendLine($"{d.Severity.ToString().ToUpperInvariant()} {d.Code}{(d.FrameName is null ? "" : $" [{d.FrameName}]")}: {d.Message}");
        return b.ToString().Replace("\r\n", "\n");
    }
    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static string ManifestValueType(FrameKind kind) => kind == FrameKind.STATUSBAR ? "number" : "string";
    private static string N(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);
}
