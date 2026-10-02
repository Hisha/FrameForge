using FrameForge.Core.Models;

namespace FrameForge.Core.Geometry;

/// <summary>
/// The WoW anchor/layout engine. This is the heart of FrameForge and is deliberately
/// pure: it takes a <see cref="Project"/> and returns a new <see cref="LayoutResult"/>,
/// with no dependency on the UI, the filesystem, or editor state.
/// </summary>
public static class LayoutResolver
{
    /// <summary>The UIParent rectangle for a screen: centred on the model origin, +Y up.</summary>
    public static FrameRect ScreenRect(Screen screen) =>
        FrameRect.FromEdges(-screen.Width / 2, screen.Height / 2, screen.Width / 2, -screen.Height / 2);

    /// <summary>Absolute model-space position of <paramref name="point"/> on <paramref name="rect"/>.</summary>
    public static ModelPoint AnchorPosition(FrameRect rect, AnchorPoint point)
    {
        var (ux, uy) = point.Unit();
        return new ModelPoint(rect.Left + ux * rect.Width, rect.Bottom + uy * rect.Height);
    }

    /// <summary>
    /// Resolves one frame's absolute bounds from its anchor relationship.
    /// </summary>
    /// <param name="frame">The frame to position.</param>
    /// <param name="referenceRect">
    /// Already-resolved bounds of the frame this one anchors against (the screen rect for a
    /// frame with no parent and no explicit relativeTo).
    /// </param>
    /// <param name="sizeRect">
    /// The frame <paramref name="frame"/>'s width and height are fractions of, when its
    /// <see cref="FrameDef.SizeReference"/> is PARENT. Null means literal units.
    /// </param>
    /// <remarks>
    /// The relationship is preserved exactly: the frame's own <see cref="FrameDef.Point"/> is
    /// placed on <see cref="FrameDef.RelativePoint"/> of the reference, displaced by
    /// (OffsetX, OffsetY) in MODEL units. Nothing here writes an absolute position back to
    /// the model.
    /// </remarks>
    public static FrameRect ResolveFrameRect(FrameDef frame, FrameRect referenceRect, FrameRect? sizeRect)
    {
        var sizeBase = sizeRect ?? referenceRect;
        var fractional = frame.SizeReferenceOrDefault == SizeReference.PARENT;
        var width = fractional ? sizeBase.Width * frame.Width : frame.Width;
        var height = fractional ? sizeBase.Height * frame.Height : frame.Height;

        var anchor = AnchorPosition(referenceRect, frame.RelativePoint);
        var anchorX = anchor.X + frame.OffsetX;
        var anchorY = anchor.Y + frame.OffsetY;

        // unit.X: 0 = left edge, 1 = right edge.  unit.Y: 0 = bottom edge, 1 = top edge.
        // The anchor point of THIS frame lands on (anchorX, anchorY), so the frame extends
        // left/down from there according to how far the point sits from each edge.
        //
        // The (1 - unit.Y) term is the one that is easy to get wrong: with model +Y up, a
        // point at the top edge is at the LARGEST y, and the frame hangs DOWN from it. The
        // Electron v0.1 implementation shipped the sign flipped here and it silently
        // double-subtracted the height of every bottom-anchored frame.
        var (unitX, unitY) = frame.Point.Unit();
        var left = anchorX - unitX * width;
        var top = anchorY + (1 - unitY) * height;
        return FrameRect.FromSize(left, top, width, height);
    }

    /// <summary>
    /// Draw order: shallower frames first, then stratum, then level, then authored order.
    /// Depth keeps parents behind their children, which is what makes nested frames
    /// readable when they overlap.
    /// </summary>
    public static IReadOnlyList<string> ComputePaintOrder(Project project)
    {
        var depth = FrameHierarchy.Depths(project);

        return project.Frames
            .Select((frame, index) => (frame, index))
            .OrderBy(e => depth.TryGetValue(e.frame.Name, out var d) ? d : 0)
            .ThenBy(e => StratumRank(e.frame.StratumOrDefault))
            .ThenBy(e => e.frame.LevelOrDefault)
            .ThenBy(e => e.index)
            .Select(e => e.frame.Name)
            .ToArray();
    }

    /// <summary>
    /// Resolves every frame to absolute model-space bounds.
    /// </summary>
    /// <remarks>
    /// Unresolvable frames (dangling references, anchor cycles) are reported in
    /// <see cref="LayoutResult.Issues"/> and omitted from
    /// <see cref="LayoutResult.Rects"/> rather than being guessed at or throwing.
    /// </remarks>
    public static LayoutResult Resolve(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var screen = ScreenRect(project.Screen);
        var byName = new Dictionary<string, FrameDef>(StringComparer.Ordinal);
        foreach (var frame in project.Frames)
            byName[frame.Name] = frame;

        var rects = new Dictionary<string, FrameRect>(StringComparer.Ordinal);
        var issues = new List<LayoutIssue>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        void Report(string frame, string message)
        {
            if (reported.Add($"{frame}|{message}"))
                issues.Add(new LayoutIssue(frame, message));
        }

        string? SizeNameOf(FrameDef frame) =>
            frame.SizeReferenceOrDefault == SizeReference.PARENT ? frame.Parent : null;

        FrameRect? Resolve(string name)
        {
            if (rects.TryGetValue(name, out var cached))
                return cached;

            if (!byName.TryGetValue(name, out var frame))
                return null;

            if (!visiting.Add(name))
            {
                Report(name, "Anchor cycle detected; frame cannot be positioned.");
                return null;
            }

            var referenceName = frame.ReferenceName;
            var sizeName = SizeNameOf(frame);
            var referenceRect = screen;
            var broken = false;

            if (referenceName is not null)
            {
                if (!byName.ContainsKey(referenceName))
                {
                    Report(name, $"Anchored to unknown frame \"{referenceName}\".");
                    broken = true;
                }
                else
                {
                    var target = Resolve(referenceName);
                    if (target is null)
                    {
                        Report(name, $"Anchor target \"{referenceName}\" could not be resolved.");
                        broken = true;
                    }
                    else
                    {
                        referenceRect = target.Value;
                    }
                }
            }

            FrameRect? sizeRect = null;
            if (sizeName is not null && sizeName != name)
            {
                if (!byName.ContainsKey(sizeName))
                {
                    Report(name, $"Sized against unknown frame \"{sizeName}\".");
                    broken = true;
                }
                else
                {
                    sizeRect = Resolve(sizeName);
                    if (sizeRect is null)
                    {
                        Report(name, $"Size reference \"{sizeName}\" could not be resolved.");
                        broken = true;
                    }
                }
            }

            visiting.Remove(name);
            if (broken)
                return null;

            var rect = ResolveFrameRect(frame, referenceRect, sizeRect);
            rects[name] = rect;
            return rect;
        }

        foreach (var frame in project.Frames)
            Resolve(frame.Name);

        var frames = new Dictionary<string, FrameLayout>(StringComparer.Ordinal);
        foreach (var frame in project.Frames)
        {
            var hasRect = rects.TryGetValue(frame.Name, out var rect);
            var effectiveVisible = frame.Visible && FrameHierarchy.Ancestors(project, frame.Name)
                .All(ancestor => byName.TryGetValue(ancestor, out var a) && a.Visible);

            frames[frame.Name] = new FrameLayout(
                frame.Name,
                hasRect ? rect : null,
                hasRect ? rect.Width : frame.Width,
                hasRect ? rect.Height : frame.Height,
                frame.Visible,
                effectiveVisible,
                frame.ReferenceName,
                SizeNameOf(frame),
                issues.Where(i => i.Frame == frame.Name).Select(i => i.Message).ToArray());
        }

        FrameRect? bounds = null;
        foreach (var rect in rects.Values)
            bounds = bounds is null ? rect : bounds.Value.Union(rect);

        return new LayoutResult
        {
            Rects = rects,
            Frames = frames,
            PaintOrder = ComputePaintOrder(project),
            Issues = issues,
            Bounds = bounds,
        };
    }

    private static int StratumRank(Stratum stratum) => stratum switch
    {
        Stratum.BACKGROUND => 0,
        Stratum.LOW => 1,
        Stratum.MEDIUM => 2,
        Stratum.HIGH => 3,
        Stratum.DIALOG => 4,
        Stratum.FULLSCREEN => 5,
        Stratum.TOOLTIP => 6,
        _ => 2,
    };
}