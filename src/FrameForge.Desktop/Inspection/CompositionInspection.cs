using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Preview;
using FrameForge.Desktop.Templates;

namespace FrameForge.Desktop.Inspection;

public enum ElementOrigin
{
    ProjectSource,
    BlizzardStock,
    RuntimeDesignTime,
    StandIn,
}

public static class ElementOrigins
{
    public static string Label(this ElementOrigin origin) => origin switch
    {
        ElementOrigin.ProjectSource => "Project / imported source",
        ElementOrigin.BlizzardStock => "Blizzard stock / template",
        ElementOrigin.RuntimeDesignTime => "Runtime / design-time",
        ElementOrigin.StandIn => "Stand-in / synthesized",
        _ => "Uncertain",
    };
}

[Flags]
public enum OriginVisibility
{
    None = 0,
    Project = 1,
    BlizzardStock = 2,
    RuntimeDesignTime = 4,
    StandIn = 8,
    All = Project | BlizzardStock | RuntimeDesignTime | StandIn,
}

public static class SelectionPriority
{
    /// <summary>Stable priority only for picking; paint order remains untouched.</summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> frontToBack, IReadOnlySet<string> preferred) =>
        [.. frontToBack.Select((name, index) => (name, index))
            .OrderByDescending(item => preferred.Contains(item.name))
            .ThenBy(item => item.index)
            .Select(item => item.name)];
}

public sealed class ElementOriginClassifier(
    ITextureAssetResolver assets,
    IStockTemplateResolver stockTemplates,
    string managedCacheRoot)
{
    public ElementOrigin Classify(FrameDef frame, PreviewOverrideSet preview)
    {
        if (preview.Find(frame.Name) is { } runtime &&
            (runtime.HasTexture || runtime.HasText || runtime.StatusBarValue is not null || runtime.Visible is not null))
            return ElementOrigin.RuntimeDesignTime;

        if (frame.Placeholder)
            return stockTemplates.ResolveExternalFrame(frame.Name) is not null
                ? ElementOrigin.BlizzardStock
                : ElementOrigin.StandIn;

        if (frame.Inherits is { } template && stockTemplates.ResolveButton(template) is not null)
            return ElementOrigin.BlizzardStock;

        // Native Hunts declares the visual slices beneath Blizzard's external LFD parent in its
        // own XML, but their appearance is the surrounding stock shell rather than addon content.
        // Classifying the direct shell children individually lets the custom NativeHuntsFrame
        // descendant remain visible when stock chrome is filtered out.
        if (frame.Parent == StockTemplateResolver.LfdParentFrame && frame.Name != "NativeHuntsFrame")
            return ElementOrigin.BlizzardStock;

        var reference = frame.Visual?.Texture?.File ?? frame.Visual?.StatusBar?.BarTexture;
        if (reference is not null && assets.Resolve(reference) is { } resolved
            && resolved.SourceKind == AssetSourceKind.ConfiguredRoot
            && resolved.PhysicalPath is { } physical
            && IsInside(physical, managedCacheRoot))
            return ElementOrigin.BlizzardStock;

        return ElementOrigin.ProjectSource;
    }

    public static bool IsVisible(ElementOrigin origin, OriginVisibility filter) => origin switch
    {
        ElementOrigin.ProjectSource => filter.HasFlag(OriginVisibility.Project),
        ElementOrigin.BlizzardStock => filter.HasFlag(OriginVisibility.BlizzardStock),
        ElementOrigin.RuntimeDesignTime => filter.HasFlag(OriginVisibility.RuntimeDesignTime),
        ElementOrigin.StandIn => filter.HasFlag(OriginVisibility.StandIn),
        _ => true,
    };

    private static bool IsInside(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.Ordinal);
    }
}

public sealed record VisualComponentInfo(
    string FrameName,
    string Kind,
    string Origin,
    string AssetPath,
    string Resolution,
    string Geometry,
    string Details,
    bool IsTemplateDerived)
{
    public string Heading => $"{FrameName}  ·  {Kind}";
}

public sealed record CompositionInspection(
    IReadOnlyList<VisualComponentInfo> Components,
    string SizeSource,
    string AppearanceSource,
    string ResizeGuidance);

public sealed class VisualCompositionInspector(
    ITextureAssetResolver assets,
    IStockTemplateResolver stockTemplates,
    ElementOriginClassifier origins,
    string managedCacheRoot)
{
    public CompositionInspection Inspect(Project source, Project effective, LayoutResult layout,
        string? selectedName, PreviewOverrideSet preview)
    {
        var selected = source.Find(selectedName);
        if (selected is null)
            return new([], string.Empty, string.Empty, string.Empty);

        var candidates = source.Frames.Where(frame => frame.Parent == selected.Name && IsUsefulVisual(frame)).ToList();
        if (selected.Kind is FrameKind.BUTTON or FrameKind.STATUSBAR || IsUsefulVisual(selected))
            candidates.Insert(0, selected);

        var components = candidates.DistinctBy(frame => frame.Name)
            .Select(frame => Describe(source, effective.Find(frame.Name) ?? frame, layout, frame, preview))
            .ToArray();

        var textureChildren = candidates.Where(frame => frame.Kind == FrameKind.TEXTURE).ToArray();
        var appearance = textureChildren.Length == 0
            ? components.Length == 0 ? "No direct visual components were discovered." : string.Join(", ", components.Select(item => item.FrameName))
            : string.Join(", ", textureChildren.Select(frame => frame.Visual?.Texture?.File ?? frame.Name));
        var guidance = ResizeGuidance(selected, textureChildren, layout);
        var sourceName = source.Source?.FileName ?? "FrameForge project";
        return new(components,
            $"{sourceName} frame geometry ({selected.Width:0.##} × {selected.Height:0.##} declared)",
            appearance,
            guidance);
    }

    private VisualComponentInfo Describe(Project source, FrameDef effective, LayoutResult layout,
        FrameDef declared, PreviewOverrideSet preview)
    {
        var runtime = preview.Find(declared.Name);
        var texture = effective.Visual?.Texture;
        var reference = texture?.File ?? effective.Visual?.StatusBar?.BarTexture;
        var stockButton = declared.Inherits is { } inherited ? stockTemplates.ResolveButton(inherited) : null;
        var templateFiles = stockButton is null
            ? []
            : stockButton.NormalSlices.Concat(stockButton.DisabledSlices).Select(slice => slice.File)
                .Append(stockButton.HighlightTexture).Where(file => !string.IsNullOrWhiteSpace(file))
                .Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().ToArray();
        var asset = assets.Resolve(reference ?? templateFiles.FirstOrDefault());
        var resolution = templateFiles.Length == 0
            ? DescribeResolution(asset, runtime?.HasTexture == true)
            : string.Join(" · ", templateFiles.Select(file => DescribeResolution(assets.Resolve(file), false)));
        var rect = layout.Rects.GetValueOrDefault(declared.Name);
        var geometry = rect == default
            ? "unresolved"
            : $"{rect.Width:0.##} × {rect.Height:0.##} at {rect.Left:0.##}, {rect.Top:0.##}";
        var detail = new List<string>();
        if (asset.Width is { } width && asset.Height is { } height)
            detail.Add($"source image {width} × {height}");
        if (texture is { } textureVisual)
        {
            detail.Add(textureVisual.TexCoords.IsSubRectangle
                ? $"texCoords {textureVisual.TexCoords.Left:0.####},{textureVisual.TexCoords.Right:0.####} / {textureVisual.TexCoords.Top:0.####},{textureVisual.TexCoords.Bottom:0.####}"
                : "full texture coordinates");
            if (textureVisual.Color is { } color)
                detail.Add($"tint {color.R:0.##},{color.G:0.##},{color.B:0.##},{color.A:0.##}");
            if (textureVisual.Alpha is { } alpha)
                detail.Add($"alpha {alpha:0.##}");
        }
        if (declared.Visual?.Text is { } text)
            detail.Add(text.Text is null ? "runtime text" : $"text “{text.Text}”");
        if (declared.Visual?.StatusBar is { } bar)
            detail.Add($"status range {bar.MinValue ?? 0:0.##}..{bar.MaxValue ?? 100:0.##}");
        var templateDerived = stockButton is not null;
        if (templateDerived)
        {
            detail.Add($"inherited from {declared.Inherits}");
            detail.Add($"{stockButton!.NormalSlices.Count} normal and {stockButton.DisabledSlices.Count} selected-state atlas slices");
        }

        return new VisualComponentInfo(
            declared.Name,
            declared.Kind.TagName(),
            origins.Classify(declared, preview).Label(),
            reference ?? (templateDerived ? $"{declared.Inherits}: {string.Join(", ", templateFiles)}" : "(no declared asset)"),
            resolution,
            geometry,
            string.Join(" · ", detail),
            templateDerived);
    }

    private string DescribeResolution(ResolvedTextureAsset asset, bool runtimeSelected)
    {
        if (runtimeSelected)
            return asset.PhysicalPath is { } runtimePath
                ? $"Runtime-selected asset → {runtimePath}"
                : "Runtime-selected asset (unresolved)";
        if (asset.PhysicalPath is not { } path)
            return asset.Reference is null ? "No physical asset" : $"Unresolved: {asset.Diagnostic.Message}";
        if (asset.SourceKind == AssetSourceKind.ProjectRelative)
            return $"Project-owned design asset → {path}";
        if (asset.SourceKind == AssetSourceKind.SourceRelative)
            return $"Source-relative project asset → {path}";
        if (IsInside(path, managedCacheRoot))
            return $"Managed WoW stock cache → {path}";
        return $"Manual asset root → {path}";
    }

    private static string ResizeGuidance(FrameDef selected, IReadOnlyList<FrameDef> textures, LayoutResult layout)
    {
        if (!layout.Rects.TryGetValue(selected.Name, out var container))
            return string.Empty;
        foreach (var texture in textures)
        {
            if (!layout.Rects.TryGetValue(texture.Name, out var visual) || visual != container)
                continue;
            var coords = texture.Visual?.Texture?.TexCoords ?? TexCoords.Full;
            if (coords.IsSubRectangle)
                return $"{texture.Name} fills this frame using cropped/atlas coordinates. Resizing changes its rendered geometry; distortion is not inferred for sliced artwork.";
            return $"Resizing this frame may stretch fixed artwork ({texture.Name}).";
        }
        return "No direct full-frame fixed texture relationship was demonstrated.";
    }

    private static bool IsUsefulVisual(FrameDef frame) =>
        frame.Kind is FrameKind.TEXTURE or FrameKind.FONTSTRING or FrameKind.STATUSBAR or FrameKind.BUTTON
        || frame.Visual is not null;

    private static bool IsInside(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.Ordinal);
    }
}
