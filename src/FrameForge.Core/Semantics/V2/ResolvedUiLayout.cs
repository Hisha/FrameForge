using System.Collections.ObjectModel;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Templates;
using FrameForge.Core.Viewing;

namespace FrameForge.Core.Semantics.V2;

/// <summary>The explicit module host used for a schema-v2 preview.</summary>
public sealed record UiPreviewHost
{
    public required string GlobalName { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public WowTargetProfile Target { get; init; } = WowTargetProfile.Wow335a12340;

    public FrameRect Rect => FrameRect.FromEdges(-Width / 2, Height / 2, Width / 2, -Height / 2);

    public static UiPreviewHost FromDesignRoot(CompositionRoot root) => new()
    {
        GlobalName = root.ExternalHostName,
        Width = root.DesignWidth,
        Height = root.DesignHeight,
    };
}

public sealed record ResolvedUiAnchor(
    int Index,
    UiAnchor Authored,
    SemanticId? ResolvedTargetId,
    FrameRect? TargetRect,
    ModelPoint? OwnPosition,
    ModelPoint? TargetPosition,
    bool Resolved,
    string? Diagnostic);

/// <summary>A native, non-persisted view of one schema-v2 root or node.</summary>
public sealed record ResolvedUiElement
{
    public required SemanticId Id { get; init; }
    public required string DisplayLabel { get; init; }
    public string? RuntimeName { get; init; }
    public UiNode? Node { get; init; }
    public OwnerReference? Owner { get; init; }
    public AuthoredProperties? AuthoredProperties { get; init; }
    public EffectiveNodeProperties? EffectiveProperties { get; init; }
    public FrameRect? Rect { get; init; }
    public bool OwnVisible { get; init; } = true;
    public bool EffectiveVisible { get; init; } = true;
    public IReadOnlyList<ResolvedUiAnchor> Anchors { get; init; } = [];
    public IReadOnlyList<UiDiagnostic> Diagnostics { get; init; } = [];
    public int AuthoredOrder { get; init; }
    public bool IsCompositionRoot => Node is null;
}

/// <summary>
/// Complete schema-v2 preview state. It is derived directly from <see cref="UiDocument"/> and
/// never serialised back into the authored graph.
/// </summary>
public sealed record ResolvedUiLayout
{
    public required UiDocument Document { get; init; }
    public required UiPreviewHost Host { get; init; }
    public required FrameRect HostRect { get; init; }
    public required IReadOnlyDictionary<SemanticId, ResolvedUiElement> Elements { get; init; }
    public required IReadOnlyList<SemanticId> PaintOrder { get; init; }
    public required IReadOnlyList<UiDiagnostic> Diagnostics { get; init; }
    public FrameRect? Bounds { get; init; }
    public bool IsClean => Diagnostics.All(item => item.Severity != DiagnosticSeverity.Error);
}

/// <summary>Resolves schema-v2 ownership, anchors, dimensions, visibility and paint order directly.</summary>
public static class UiLayoutResolver
{
    private const double Epsilon = 1e-6;

    public static ResolvedUiLayout Resolve(UiDocument document, UiPreviewHost host,
        BlizzardTemplateRegistry? templates = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(host);
        var diagnostics = new List<UiDiagnostic>();
        if (!(host.Width > 0) || !(host.Height > 0) || !double.IsFinite(host.Width) || !double.IsFinite(host.Height))
            diagnostics.Add(Error("FFV2L-HOST", "The preview host requires positive finite dimensions."));
        if (!host.Target.IsSupported || host.Target != document.Target)
            diagnostics.Add(Error("FFV2L-TARGET",
                $"Preview target {host.Target.Product} build {host.Target.Build} does not match document target {document.Target.Product} build {document.Target.Build}."));

        if (document.CompositionRoots.Count != 1)
        {
            diagnostics.Add(Error("FFV2L-ROOT", "Native layout requires exactly one composition root."));
            return Empty(document, host, diagnostics);
        }

        var root = document.CompositionRoots[0];
        var hostMatches = string.Equals(root.ExternalHostName, host.GlobalName, StringComparison.Ordinal);
        if (!hostMatches)
            diagnostics.Add(Error("FFV2L-HOST-NAME",
                $"Composition root host '{root.ExternalHostName}' does not match preview host '{host.GlobalName}'.",
                root.Id, "compositionRoots[0].externalHostName"));

        var rootRect = hostMatches ? ResolveRoot(root, host.Rect, diagnostics) : null;
        var nodes = document.Nodes.ToDictionary(item => item.Id);
        var order = AuthoredOrder(document, root, nodes, diagnostics);
        var states = new Dictionary<SemanticId, ResolvedUiElement>();
        var resolving = new HashSet<SemanticId>();
        states[root.Id] = new ResolvedUiElement
        {
            Id = root.Id,
            DisplayLabel = root.RuntimeName,
            RuntimeName = root.RuntimeName,
            Rect = rootRect,
            AuthoredOrder = 0,
            Diagnostics = rootRect is null ? diagnostics.Where(item => item.NodeId == root.Id).ToArray() : [],
        };

        ResolvedUiElement ResolveNode(SemanticId id)
        {
            if (states.TryGetValue(id, out var existing)) return existing;
            if (!nodes.TryGetValue(id, out var node))
            {
                var missing = Error("FFV2L-NODE", $"Node '{id}' is not present in the semantic graph.", id);
                diagnostics.Add(missing);
                return new ResolvedUiElement { Id = id, DisplayLabel = id.Value, Diagnostics = [missing] };
            }
            if (!resolving.Add(id))
            {
                var cycle = Error("FFV2L-CYCLE", $"Layout dependency cycle reaches '{node.DisplayLabel}'.", id, "anchors");
                diagnostics.Add(cycle);
                return new ResolvedUiElement
                {
                    Id = id, DisplayLabel = node.DisplayLabel, RuntimeName = node.RuntimeName, Node = node,
                    Owner = node.Owner, AuthoredProperties = node.AuthoredProperties, Diagnostics = [cycle],
                    AuthoredOrder = order.GetValueOrDefault(id, int.MaxValue),
                };
            }

            var local = new List<UiDiagnostic>();
            var effective = UiTemplateEffectiveProperties.Resolve(node, templates);
            foreach (var templateDiagnostic in effective.Diagnostics.Where(item =>
                         item.Severity == BlizzardTemplateDiagnosticSeverity.Error))
                local.Add(Error("FFV2L-TEMPLATE", templateDiagnostic.Message, id, "blizzardTemplate"));

            var owner = ResolveOwner(node.Owner, root, ResolveNode, local, id);
            var ownVisible = node.AuthoredProperties.Frame?.Visible
                             ?? node.AuthoredProperties.Region?.Visible
                             ?? true;
            var effectiveVisible = ownVisible && (owner?.EffectiveVisible ?? false);
            var dimensions = Dimensions(node, effective);
            var anchors = new List<ResolvedUiAnchor>();
            FrameRect? rect = null;

            if (owner?.Rect is not null && node.Anchors.Count == 0)
                local.Add(Error("FFV2L-ANCHOR-MISSING", "The node has no authored anchor.", id, "anchors"));
            else if (owner?.Rect is not null)
                rect = ResolveAnchors(node, root, host, owner, ResolveNode, dimensions.Width, dimensions.Height,
                    anchors, local, node.Editor?.ReferenceOnly == true);
            if (rect is null)
            {
                if (dimensions.Width is null)
                    local.Add(Error("FFV2L-WIDTH", "No positive finite authored, template-derived, or anchor-constrained width is available.", id,
                        node.IsRegion ? "authoredProperties.region.width" : "authoredProperties.frame.width"));
                if (dimensions.Height is null)
                    local.Add(Error("FFV2L-HEIGHT", "No positive finite authored, template-derived, or anchor-constrained height is available.", id,
                        node.IsRegion ? "authoredProperties.region.height" : "authoredProperties.frame.height"));
            }

            diagnostics.AddRange(local);
            resolving.Remove(id);
            var result = new ResolvedUiElement
            {
                Id = id,
                DisplayLabel = node.DisplayLabel,
                RuntimeName = node.RuntimeName,
                Node = node,
                Owner = node.Owner,
                AuthoredProperties = node.AuthoredProperties,
                EffectiveProperties = effective,
                Rect = rect,
                OwnVisible = ownVisible,
                EffectiveVisible = effectiveVisible,
                Anchors = anchors,
                Diagnostics = local,
                AuthoredOrder = order.GetValueOrDefault(id, int.MaxValue),
            };
            states[id] = result;
            return result;
        }

        foreach (var id in order.OrderBy(item => item.Value).Select(item => item.Key)) ResolveNode(id);
        foreach (var node in document.Nodes) ResolveNode(node.Id);

        var paintOrder = BuildPaintOrder(root, nodes, states, diagnostics);
        var bounds = Union(states.Values.Select(item => item.Rect));
        return new ResolvedUiLayout
        {
            Document = document,
            Host = host,
            HostRect = host.Rect,
            Elements = new ReadOnlyDictionary<SemanticId, ResolvedUiElement>(states),
            PaintOrder = paintOrder,
            Diagnostics = diagnostics,
            Bounds = bounds,
        };
    }

    private static FrameRect? ResolveRoot(CompositionRoot root, FrameRect host, ICollection<UiDiagnostic> diagnostics)
    {
        if (root.Sizing.Kind == RootSizingKind.FillHost) return host;
        if (root.Sizing.Width is not (> 0) || root.Sizing.Height is not (> 0) || root.Sizing.Anchor is not { } anchor)
        {
            diagnostics.Add(Error("FFV2L-ROOT-SIZE", "Explicit root sizing requires positive dimensions and an anchor.", root.Id, "sizing"));
            return null;
        }
        var width = root.Sizing.Width.Value;
        var height = root.Sizing.Height.Value;
        var target = LayoutResolver.AnchorPosition(host, anchor.RelativePoint);
        var (ux, uy) = anchor.Point.Unit();
        return FrameRect.FromSize(target.X + anchor.OffsetX - ux * width,
            target.Y + anchor.OffsetY + (1 - uy) * height, width, height);
    }

    private static ResolvedUiElement? ResolveOwner(OwnerReference owner, CompositionRoot root,
        Func<SemanticId, ResolvedUiElement> resolve, ICollection<UiDiagnostic> diagnostics, SemanticId id)
    {
        if (owner.Kind == OwnerKind.CompositionRoot)
        {
            if (owner.Id == root.Id) return resolve(root.Id);
            diagnostics.Add(Error("FFV2L-OWNER", $"Unknown composition root owner '{owner.Id}'.", id, "owner"));
            return null;
        }
        var result = resolve(owner.Id);
        if (result.Node is null || !result.Node.CanOwnChildren)
        {
            diagnostics.Add(Error("FFV2L-OWNER", $"Owner '{owner.Id}' is missing or cannot own children.", id, "owner"));
            return null;
        }
        return result;
    }

    private static (double? Width, double? Height) Dimensions(UiNode node, EffectiveNodeProperties effective)
    {
        var width = node.IsRegion ? effective.Values.Region?.Width : effective.Values.Frame?.Width;
        var height = node.IsRegion ? effective.Values.Region?.Height : effective.Values.Frame?.Height;
        return (width is > 0 ? width : null, height is > 0 ? height : null);
    }

    private static FrameRect? ResolveAnchors(UiNode node, CompositionRoot root, UiPreviewHost host,
        ResolvedUiElement owner, Func<SemanticId, ResolvedUiElement> resolve, double? declaredWidth,
        double? declaredHeight, ICollection<ResolvedUiAnchor> resolved, ICollection<UiDiagnostic> diagnostics,
        bool referenceGeometry)
    {
        var equations = new List<(UiAnchor Anchor, SemanticId? TargetId, FrameRect Rect, ModelPoint Target)>(node.Anchors.Count);
        for (var index = 0; index < node.Anchors.Count; index++)
        {
            var anchor = node.Anchors[index];
            var target = ResolveTarget(anchor.Target, root, host, owner, resolve);
            if (target.Rect is not { } targetRect)
            {
                var message = target.Diagnostic ?? "The anchor target has unresolved geometry.";
                diagnostics.Add(Error("FFV2L-ANCHOR-TARGET", message, node.Id, $"anchors[{index}].target"));
                resolved.Add(new(index, anchor, target.Id, null, null, null, false, message));
                continue;
            }
            var point = LayoutResolver.AnchorPosition(targetRect, anchor.RelativePoint);
            point = new ModelPoint(point.X + anchor.OffsetX, point.Y + anchor.OffsetY);
            equations.Add((anchor, target.Id, targetRect, point));
        }
        if (equations.Count == 0) return null;

        var width = SolveDimension(equations, declaredWidth, horizontal: true, node.Id, diagnostics, referenceGeometry);
        var height = SolveDimension(equations, declaredHeight, horizontal: false, node.Id, diagnostics, referenceGeometry);
        if (width is null || height is null) return null;

        var primary = equations[0];
        var (ux, uy) = primary.Anchor.Point.Unit();
        var rect = FrameRect.FromSize(primary.Target.X - ux * width.Value,
            primary.Target.Y + (1 - uy) * height.Value, width.Value, height.Value);
        for (var index = 0; index < node.Anchors.Count; index++)
        {
            var authored = node.Anchors[index];
            var equation = equations.FirstOrDefault(item => ReferenceEquals(item.Anchor, authored));
            if (equation.Anchor is null) continue;
            var own = LayoutResolver.AnchorPosition(rect, authored.Point);
            var matches = Math.Abs(own.X - equation.Target.X) <= Epsilon && Math.Abs(own.Y - equation.Target.Y) <= Epsilon;
            var message = matches ? null : "The resolved rectangle cannot satisfy this anchor simultaneously with the primary constraints.";
            if (!matches)
                diagnostics.Add(new UiDiagnostic { Code = "FFV2L-ANCHOR-CONFLICT", Severity = DiagnosticSeverity.Warning,
                    Message = message!, NodeId = node.Id, PropertyPath = $"anchors[{index}]" });
            resolved.Add(new(index, authored, equation.TargetId, equation.Rect, own, equation.Target, matches, message));
        }
        return rect;
    }

    private static double? SolveDimension(
        IReadOnlyList<(UiAnchor Anchor, SemanticId? TargetId, FrameRect Rect, ModelPoint Target)> equations,
        double? declared, bool horizontal, SemanticId id, ICollection<UiDiagnostic> diagnostics,
        bool preferAnchorDerived)
    {
        double? solved = declared;
        var primaryUnit = horizontal ? equations[0].Anchor.Point.Unit().X : equations[0].Anchor.Point.Unit().Y;
        var primaryTarget = horizontal ? equations[0].Target.X : equations[0].Target.Y;
        foreach (var equation in equations.Skip(1))
        {
            var unit = horizontal ? equation.Anchor.Point.Unit().X : equation.Anchor.Point.Unit().Y;
            if (Math.Abs(unit - primaryUnit) <= Epsilon) continue;
            var target = horizontal ? equation.Target.X : equation.Target.Y;
            var candidate = (target - primaryTarget) / (unit - primaryUnit);
            if (!(candidate > 0) || !double.IsFinite(candidate)) continue;
            if (solved is null || preferAnchorDerived) solved = candidate;
            else if (Math.Abs(solved.Value - candidate) > Epsilon)
                diagnostics.Add(new UiDiagnostic
                {
                    Code = "FFV2L-CONSTRAINT-CONFLICT", Severity = DiagnosticSeverity.Warning,
                    Message = $"Authored anchors imply {candidate:0.###} { (horizontal ? "width" : "height") } but effective properties specify {solved:0.###}; the effective dimension is retained.",
                    NodeId = id, PropertyPath = "anchors",
                });
        }
        return solved;
    }

    private static (SemanticId? Id, FrameRect? Rect, string? Diagnostic) ResolveTarget(AnchorTarget target,
        CompositionRoot root, UiPreviewHost host, ResolvedUiElement owner, Func<SemanticId, ResolvedUiElement> resolve)
    {
        if (target.Kind == AnchorTargetKind.LocalNode && target.NodeId is { } localId)
        {
            var local = resolve(localId);
            return (localId, local.Rect, local.Rect is null ? $"Local target '{localId}' is unresolved." : null);
        }
        return target.Kind switch
        {
            AnchorTargetKind.Parent => (owner.Id, owner.Rect, owner.Rect is null ? "The structural owner is unresolved." : null),
            AnchorTargetKind.CompositionRoot => (root.Id, resolve(root.Id).Rect, null),
            AnchorTargetKind.ExternalGlobal when string.Equals(target.GlobalName, host.GlobalName, StringComparison.Ordinal) =>
                (null, host.Rect, null),
            AnchorTargetKind.ExternalGlobal => (null, null,
                $"External global '{target.GlobalName}' is not supplied by the explicit preview host."),
            AnchorTargetKind.Unresolved => (null, null, target.Diagnostic ?? $"Unresolved anchor target '{target.UnresolvedText}'."),
            _ => (null, null, "The anchor target is incomplete."),
        };
    }

    private static Dictionary<SemanticId, int> AuthoredOrder(UiDocument document, CompositionRoot root,
        IReadOnlyDictionary<SemanticId, UiNode> nodes, ICollection<UiDiagnostic> diagnostics)
    {
        var result = new Dictionary<SemanticId, int>();
        var next = 1;
        void Walk(IEnumerable<SemanticId> children)
        {
            foreach (var id in children)
            {
                if (!result.TryAdd(id, next++)) continue;
                if (nodes.TryGetValue(id, out var child)) Walk(child.Children);
                else diagnostics.Add(Error("FFV2L-ORDER", $"Ordered child '{id}' is missing from nodes.", id));
            }
        }
        Walk(root.Children);
        foreach (var node in document.Nodes)
            if (result.TryAdd(node.Id, next++)) Walk(node.Children);
        return result;
    }

    private static IReadOnlyList<SemanticId> BuildPaintOrder(CompositionRoot root,
        IReadOnlyDictionary<SemanticId, UiNode> nodes,
        IReadOnlyDictionary<SemanticId, ResolvedUiElement> states, ICollection<UiDiagnostic> diagnostics)
    {
        var result = new List<SemanticId> { root.Id };
        var visited = new HashSet<SemanticId>();
        void Walk(IEnumerable<SemanticId> children)
        {
            var sorted = children.Where(nodes.ContainsKey).OrderBy(id => PaintBand(nodes[id]))
                .ThenBy(id => PaintLevel(nodes[id])).ThenBy(id => states.GetValueOrDefault(id)?.AuthoredOrder ?? int.MaxValue);
            foreach (var id in sorted)
            {
                if (!visited.Add(id)) continue;
                result.Add(id);
                Walk(nodes[id].Children);
            }
        }
        Walk(root.Children);
        foreach (var node in nodes.Values.OrderBy(item => states.GetValueOrDefault(item.Id)?.AuthoredOrder ?? int.MaxValue))
            if (!visited.Contains(node.Id))
            {
                diagnostics.Add(new UiDiagnostic { Code = "FFV2L-ORDER-ORPHAN", Severity = DiagnosticSeverity.Warning,
                    Message = $"'{node.DisplayLabel}' is absent from its owner's authored child list and is painted last.", NodeId = node.Id });
                result.Add(node.Id);
            }
        return result;
    }

    private static int PaintBand(UiNode node) => node.IsRegion
        ? (int)(node.AuthoredProperties.Region?.DrawLayer ?? RegionDrawLayer.Artwork)
        : 10 + (int)(node.AuthoredProperties.Frame?.Strata ?? FrameStrata.Medium);
    private static int PaintLevel(UiNode node) => node.IsRegion
        ? node.AuthoredProperties.Region?.Sublevel ?? 0
        : node.AuthoredProperties.Frame?.Level ?? 0;

    private static FrameRect? Union(IEnumerable<FrameRect?> rectangles)
    {
        FrameRect? result = null;
        foreach (var rect in rectangles.OfType<FrameRect>())
            result = result is null ? rect : FrameRect.FromEdges(Math.Min(result.Value.Left, rect.Left),
                Math.Max(result.Value.Top, rect.Top), Math.Max(result.Value.Right, rect.Right),
                Math.Min(result.Value.Bottom, rect.Bottom));
        return result;
    }

    private static ResolvedUiLayout Empty(UiDocument document, UiPreviewHost host, IReadOnlyList<UiDiagnostic> diagnostics) => new()
    {
        Document = document, Host = host, HostRect = host.Rect,
        Elements = new ReadOnlyDictionary<SemanticId, ResolvedUiElement>(new Dictionary<SemanticId, ResolvedUiElement>()),
        PaintOrder = [], Diagnostics = diagnostics,
    };

    private static UiDiagnostic Error(string code, string message, SemanticId? id = null, string? path = null) => new()
    {
        Code = code, Severity = DiagnosticSeverity.Error, Message = message, NodeId = id, PropertyPath = path,
    };
}

/// <summary>Native schema-v2 hit testing in the same back-to-front order used for painting.</summary>
public static class UiLayoutHitTester
{
    public static IReadOnlyList<SemanticId> CandidatesAt(ResolvedUiLayout layout, Viewport viewport,
        CanvasOrigin origin, VisibilityFilter filter, double canvasX, double canvasY)
    {
        var result = new List<SemanticId>();
        for (var index = layout.PaintOrder.Count - 1; index >= 0; index--)
        {
            var id = layout.PaintOrder[index];
            if (!layout.Elements.TryGetValue(id, out var element) || element.Rect is not { } rect || !Visible(element, filter))
                continue;
            var box = viewport.RectToCanvas(rect, origin);
            var slack = box.Width <= 0 || box.Height <= 0 ? 4 : 0;
            if (canvasX >= box.X - slack && canvasX <= box.Right + slack &&
                canvasY >= box.Y - slack && canvasY <= box.Bottom + slack)
                result.Add(id);
        }
        return result;
    }

    private static bool Visible(ResolvedUiElement element, VisibilityFilter filter)
    {
        if (!filter.HasFlag(VisibilityFilter.HIDDEN) && !element.EffectiveVisible) return false;
        var flag = element.Node?.Kind switch
        {
            UiNodeKind.Texture => VisibilityFilter.TEXTURES,
            UiNodeKind.FontString => VisibilityFilter.TEXT,
            UiNodeKind.Button => VisibilityFilter.BUTTONS,
            _ => VisibilityFilter.FRAMES,
        };
        return filter.HasFlag(flag);
    }
}
