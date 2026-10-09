using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Templates;

namespace FrameForge.Core.Semantics.V2;

/// <summary>
/// Projects schema-v2 semantics into the existing layout/rendering model. The projection is
/// transient and never serialized; typed v2 ownership and anchors remain authoritative.
/// </summary>
public static class UiDocumentProjection
{
    public const string ProjectNameMetadataKey = "projectName";

    public static Project ToProject(UiDocument document, BlizzardTemplateRegistry? templates = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.CompositionRoots.SingleOrDefault();
        if (root is null)
            return ProjectFactory.Empty();

        var orderedNodes = OrderedNodes(document, root);
        var frames = new List<FrameDef>
        {
            new()
            {
                Name = root.Id.Value,
                Kind = FrameKind.FRAME,
                Width = root.DesignWidth,
                Height = root.DesignHeight,
                Point = AnchorPoint.CENTER,
                RelativePoint = AnchorPoint.CENTER,
                Visible = true,
            },
        };
        frames.AddRange(orderedNodes.Select(node => ToFrame(node, root, templates)));

        var displayObjects = new List<DesignObjectMetadata>
        {
            new() { FrameName = root.Id.Value, DisplayName = $"Composition Root · {root.RuntimeName}" },
        };
        displayObjects.AddRange(orderedNodes.Select(node => new DesignObjectMetadata
        {
            FrameName = node.Id.Value,
            DisplayName = node.DisplayLabel,
            DesignAsset = node.AuthoredProperties.Texture?.TextureReference,
            TextOverride = node.AuthoredProperties.FontString?.Text,
        }));

        return new Project
        {
            Name = document.Editor?.Values.GetValueOrDefault(ProjectNameMetadataKey) ?? "FrameForge v2 Project",
            Screen = new Screen(root.DesignWidth, root.DesignHeight),
            Frames = frames,
            Editor = new EditorMetadata
            {
                Workspace = "design",
                DesignObjects = displayObjects,
                DesignOrder = [.. orderedNodes.Select(node => node.Id.Value)],
            },
        };
    }

    public static LayoutResult Resolve(UiDocument document) => LayoutResolver.Resolve(ToProject(document));

    public static SemanticId? IdFromProjectionName(string? name) =>
        name is not null && Guid.TryParseExact(name, "D", out _) ? new SemanticId(name) : null;

    private static IReadOnlyList<UiNode> OrderedNodes(UiDocument document, CompositionRoot root)
    {
        var byId = document.Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First());
        var result = new List<UiNode>();
        var visited = new HashSet<SemanticId>();

        void Visit(SemanticId id)
        {
            if (!visited.Add(id) || !byId.TryGetValue(id, out var node))
                return;
            result.Add(node);
            foreach (var child in node.Children)
                Visit(child);
        }

        foreach (var child in root.Children)
            Visit(child);
        foreach (var node in document.Nodes)
            Visit(node.Id);
        return result;
    }

    private static FrameDef ToFrame(UiNode node, CompositionRoot root, BlizzardTemplateRegistry? templates)
    {
        var anchor = node.Anchors.FirstOrDefault() ?? new UiAnchor
        {
            Point = AnchorPoint.CENTER,
            RelativePoint = AnchorPoint.CENTER,
            Target = AnchorTarget.Parent(),
        };
        var effective = UiTemplateEffectiveProperties.Resolve(node, templates);
        var dimensions = Dimensions(node, effective);
        var frame = node.AuthoredProperties.Frame;
        var region = node.AuthoredProperties.Region;
        var tint = region?.Tint;
        var status = node.AuthoredProperties.StatusBar;
        var visual = new FrameVisual
        {
            DrawLayer = region?.DrawLayer?.ToString().ToUpperInvariant(),
            Texture = node.Kind == UiNodeKind.Texture
                ? new TextureVisual(node.AuthoredProperties.Texture?.TextureReference, TexCoords.Full,
                    tint is null ? null : new ColorRgba(tint.Red, tint.Green, tint.Blue, tint.Alpha))
                : null,
            Text = node.Kind switch
            {
                UiNodeKind.FontString => new TextVisual(node.AuthoredProperties.FontString?.Text,
                    FontTemplate: node.AuthoredProperties.FontString?.FontReference),
                UiNodeKind.Button => new TextVisual(node.AuthoredProperties.Button?.Text),
                _ => null,
            },
            StatusBar = node.Kind == UiNodeKind.StatusBar
                ? new StatusBarVisual(status?.Minimum, status?.Maximum, status?.Value, status?.TextureReference)
                : null,
        };

        return new FrameDef
        {
            Name = node.Id.Value,
            Inherits = node.BlizzardTemplate,
            Parent = node.Owner.Id.Value,
            Kind = node.Kind switch
            {
                UiNodeKind.Frame => FrameKind.FRAME,
                UiNodeKind.Texture => FrameKind.TEXTURE,
                UiNodeKind.FontString => FrameKind.FONTSTRING,
                UiNodeKind.Button => FrameKind.BUTTON,
                UiNodeKind.StatusBar => FrameKind.STATUSBAR,
                _ => FrameKind.OTHER,
            },
            Width = dimensions.Width,
            Height = dimensions.Height,
            Point = anchor.Point,
            RelativePoint = anchor.RelativePoint,
            RelativeTo = TargetName(anchor.Target, root),
            OffsetX = anchor.OffsetX,
            OffsetY = anchor.OffsetY,
            Visible = frame?.Visible ?? true,
            Stratum = frame?.Strata is { } strata ? (Stratum)strata : null,
            Level = frame?.Level,
            ExtraAnchors = [.. node.Anchors.Skip(1).Select(item => new FrameAnchor
            {
                Point = item.Point,
                RelativePoint = item.RelativePoint,
                RelativeTo = TargetName(item.Target, root),
                OffsetX = item.OffsetX,
                OffsetY = item.OffsetY,
            })],
            Visual = visual.IsEmpty ? null : visual,
        };
    }

    private static (double Width, double Height) Dimensions(UiNode node, EffectiveNodeProperties effective)
    {
        if (node.IsRegion)
            return (node.AuthoredProperties.Region?.Width ?? DefaultWidth(node.Kind),
                node.AuthoredProperties.Region?.Height ?? DefaultHeight(node.Kind));
        var width = effective.Values.Frame?.Width ?? DefaultWidth(node.Kind);
        if (node.AuthoredProperties.Frame?.Width is null &&
            effective.PreviewBehaviors.Contains(BlizzardKnownPreviewBehavior.CharacterTabResizeToTextZeroPadding))
            width = Math.Max(width, (node.AuthoredProperties.Button?.Text?.Length ?? 0) * 8 + 24);
        return (width, effective.Values.Frame?.Height ?? DefaultHeight(node.Kind));
    }

    private static double DefaultWidth(UiNodeKind kind) => kind switch
    {
        UiNodeKind.Texture => 64,
        UiNodeKind.FontString => 120,
        UiNodeKind.StatusBar => 120,
        _ => 120,
    };

    private static double DefaultHeight(UiNodeKind kind) => kind switch
    {
        UiNodeKind.FontString => 24,
        UiNodeKind.StatusBar => 16,
        _ => 64,
    };

    private static string? TargetName(AnchorTarget target, CompositionRoot root) => target.Kind switch
    {
        AnchorTargetKind.Parent => null,
        AnchorTargetKind.CompositionRoot => root.Id.Value,
        AnchorTargetKind.LocalNode => target.NodeId?.Value,
        AnchorTargetKind.ExternalGlobal => $"external-global:{target.GlobalName}",
        AnchorTargetKind.Unresolved => $"unresolved:{target.UnresolvedText}",
        _ => "invalid-anchor-target",
    };
}
