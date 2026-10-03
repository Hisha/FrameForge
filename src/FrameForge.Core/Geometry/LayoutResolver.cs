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
    /// frame with no parent and no explicit relativeTo). Every anchor is treated as pointing
    /// at this same rectangle, which is the v0.1 single-reference behaviour.
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
    public static FrameRect ResolveFrameRect(FrameDef frame, FrameRect referenceRect, FrameRect? sizeRect) =>
        ResolveFrameRect(frame, referenceRect, sizeRect, out _);

    /// <summary>
    /// As <see cref="ResolveFrameRect(FrameDef, FrameRect, FrameRect?)"/>, additionally
    /// reporting which anchors could not be honoured in full.
    /// </summary>
    public static FrameRect ResolveFrameRect(
        FrameDef frame,
        FrameRect referenceRect,
        FrameRect? sizeRect,
        out IReadOnlyList<string> notes) =>
        ResolveAnchors(frame, _ => referenceRect, sizeRect, out notes).Rect;

    /// <summary>
    /// Resolves a frame against a REFERENCE LOOKUP rather than one rectangle, which is what a
    /// multi-anchor frame needs: each of its anchors may name a different target frame.
    /// </summary>
    /// <param name="frame">The frame to position.</param>
    /// <param name="resolveReference">
    /// Given an effective target name (anchor relativeTo, else parent, else null for
    /// UIParent), returns its resolved bounds or null when the target is unknown or itself
    /// unresolvable.
    /// </param>
    /// <param name="sizeRect">Fractional-size base, or null for literal units.</param>
    public static FrameRect ResolveFrameRect(
        FrameDef frame,
        Func<string?, FrameRect?> resolveReference,
        FrameRect? sizeRect,
        out IReadOnlyList<string> notes) =>
        ResolveAnchors(frame, resolveReference, sizeRect, out notes).Rect;

    /// <summary>The rectangle plus the notes produced while getting there.</summary>
    private readonly record struct AnchorOutcome(FrameRect Rect, IReadOnlyList<string> Notes);

    /// <summary>
    /// A problem that belongs to one specific anchor, so the canvas can dash that connector
    /// instead of every connector on the frame. <paramref name="Index"/> is zero-based; -1 means
    /// the frame as a whole rather than any one anchor.
    /// </summary>
    private readonly record struct AnchorNote(int Index, string Message);

    /// <summary>
    /// The single implementation behind every <c>ResolveFrameRect</c> overload.
    /// </summary>
    /// <remarks>
    /// Multi-anchor handling is deliberately narrow and explicit rather than a general
    /// constraint solver, because the rule real FrameXML uses is exactly one rule. Calling
    /// <c>SetPoint(TOPLEFT, parent, TOPLEFT)</c> and then <c>SetPoint(TOPRIGHT, parent, TOPRIGHT)</c>
    /// is how WoW stretches a frame between two edges, and it is why imported stretch frames are
    /// authored with <c>&lt;Size x="0" y="0"/&gt;</c>.
    /// <para>
    /// Every anchor that names the same target FRAME gives one equation per axis. Writing the
    /// frame's left edge as <c>left</c>, each anchor says
    /// <c>left + unit.X(point) * width == targetX(relativePoint) + offsetX</c>, so subtracting the
    /// primary anchor's equation from any other gives
    /// <c>width * (unit.X(point) - unit.X(primary)) == targetX[i] + offsetX[i] - targetX[0] - offsetX[0]</c>.
    /// Solving that is the whole of the stretch, and it needs nothing beyond positions the
    /// resolver already has.
    /// </para>
    /// <para>
    /// An anchor naming a different target FRAME cannot be solved this way, because its own target
    /// would have to be resolved against a frame whose bounds may depend on this one. Those
    /// anchors are kept in the model and reported, never discarded.
    /// </para>
    /// </remarks>
    private static AnchorOutcome ResolveAnchors(
        FrameDef frame,
        Func<string?, FrameRect?> resolveReference,
        FrameRect? sizeRect,
        out IReadOnlyList<string> notes) =>
        ResolveAnchors(frame, resolveReference, sizeRect, out notes, out _);

    private static AnchorOutcome ResolveAnchors(
        FrameDef frame,
        Func<string?, FrameRect?> resolveReference,
        FrameRect? sizeRect,
        out IReadOnlyList<string> notes,
        out IReadOnlyList<AnchorNote> anchorNotes)
    {
        var anchors = frame.AllAnchors;
        var primary = anchors[0];
        var notes0 = new List<string>();
        var anchorNotes0 = new List<AnchorNote>();
        notes = notes0;
        anchorNotes = anchorNotes0;

        void Flag(int index, string message)
        {
            anchorNotes0.Add(new AnchorNote(index, message));
            notes0.Add(index < 0 ? message : $"Anchor {index + 1}: {message}");
        }

        var primaryReference = resolveReference(primary.RelativeTo ?? frame.Parent);

        // WoW's SetAllPoints(): the widget fills its reference frame exactly. Size and offsets
        // are irrelevant, which is why it is modelled as a relationship and not as a size.
        if (frame.SetAllPoints)
        {
            if (primaryReference is not { } setAllTarget)
            {
                Flag(-1, "setAllPoints is set but the reference frame could not be resolved.");
                return new AnchorOutcome(FrameRect.FromSize(0, 0, 0, 0), notes0);
            }

            if (anchors.Count > 1)
                Flag(-1, $"setAllPoints overrides all {anchors.Count} anchors; the extra anchors were not used for geometry.");

            return new AnchorOutcome(setAllTarget, notes0);
        }

        var sizeBase = sizeRect ?? primaryReference ?? FrameRect.FromSize(0, 0, 0, 0);
        var fractional = frame.SizeReferenceOrDefault == SizeReference.PARENT;
        var width = fractional ? sizeBase.Width * frame.Width : frame.Width;
        var height = fractional ? sizeBase.Height * frame.Height : frame.Height;

        var reference = primaryReference ?? sizeBase;

        // Where each anchor says this frame's point must land.
        var primaryAnchorX = AnchorPosition(reference, primary.RelativePoint).X + primary.OffsetX;
        var primaryAnchorY = AnchorPosition(reference, primary.RelativePoint).Y + primary.OffsetY;
        var anchorPosition = new ModelPoint(primaryAnchorX, primaryAnchorY);

        var (unitX, unitY) = primary.Point.Unit();
        var widthSolved = false;
        var heightSolved = false;
        var primaryTargetName = primary.RelativeTo ?? frame.Parent;

        for (var i = 1; i < anchors.Count; i++)
        {
            var extra = anchors[i];
            var extraTargetName = extra.RelativeTo ?? frame.Parent;

            if (!string.Equals(extraTargetName, primaryTargetName, StringComparison.Ordinal))
            {
                Flag(i,
                    $"targets \"{extraTargetName ?? "UIParent"}\" while anchor 1 targets " +
                    $"\"{primaryTargetName ?? "UIParent"}\"; a frame pinned to two different frames is not solved.");
                continue;
            }

            var extraAnchorX = AnchorPosition(reference, extra.RelativePoint).X + extra.OffsetX;
            var extraAnchorY = AnchorPosition(reference, extra.RelativePoint).Y + extra.OffsetY;
            var (extraUnitX, extraUnitY) = extra.Point.Unit();

            // unit.X: 0 = left edge, 1 = right edge.  unit.Y: 0 = bottom edge, 1 = top edge.
            var alongX = Math.Abs(extraUnitX - unitX) > 1e-9;
            var alongY = Math.Abs(extraUnitY - unitY) > 1e-9;

            if (!alongX && !alongY)
            {
                if (extra.OffsetX != primary.OffsetX || extra.OffsetY != primary.OffsetY)
                    Flag(i, $"repeats {extra.Point} with different offsets; the primary anchor wins.");

                continue;
            }

            if (alongX)
            {
                var solved = (extraAnchorX - primaryAnchorX) / (extraUnitX - unitX);

                if (widthSolved && Math.Abs(solved - width) > 1e-6)
                    Flag(i, $"implies a width of {Describe(solved)} but the earlier anchors imply {Describe(width)}.");
                else if (solved < 0)
                    Flag(i, $"implies a negative width ({Describe(solved)}); the declared width was kept.");
                else
                {
                    width = solved;
                    widthSolved = true;
                }
            }

            if (alongY)
            {
                var solved = (extraAnchorY - primaryAnchorY) / (extraUnitY - unitY);

                if (heightSolved && Math.Abs(solved - height) > 1e-6)
                    Flag(i, $"implies a height of {Describe(solved)} but the earlier anchors imply {Describe(height)}.");
                else if (solved < 0)
                    Flag(i, $"implies a negative height ({Describe(solved)}); the declared height was kept.");
                else
                {
                    height = solved;
                    heightSolved = true;
                }
            }
        }

        // unit.X: 0 = left edge, 1 = right edge.  unit.Y: 0 = bottom edge, 1 = top edge.
        // The anchor point of THIS frame lands on (anchorX, anchorY), so the frame extends
        // left/down from there according to how far the point sits from each edge.
        //
        // The (1 - unit.Y) term is the one that is easy to get wrong: with model +Y up, a
        // point at the top edge is at the LARGEST y, and the frame hangs DOWN from it. The
        // Electron v0.1 implementation shipped the sign flipped here and it silently
        // double-subtracted the height of every bottom-anchored frame.
        var left = anchorPosition.X - unitX * width;
        var top = anchorPosition.Y + (1 - unitY) * height;
        return new AnchorOutcome(FrameRect.FromSize(left, top, width, height), notes0);
    }

    private static string Describe(double value) => value == Math.Floor(value)
        ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
        : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

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
        var notesByFrame = new Dictionary<string, IReadOnlyList<AnchorNote>>(StringComparer.Ordinal);
        var issues = new List<LayoutIssue>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        void Report(string frame, string message)
        {
            if (reported.Add($"{frame}|{message}"))
                issues.Add(new LayoutIssue(frame, message));
        }

        static string? SizeNameOf(FrameDef frame) =>
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

            if (broken)
            {
                visiting.Remove(name);
                return null;
            }

            // 'name' stays in 'visiting' for the whole of the anchor resolution, including any
            // secondary anchors that name other frames. Otherwise two frames whose extra
            // anchors point at each other would re-enter Resolve forever, because the cycle
            // guard is cleared as soon as the primary target has been resolved.
            var outcome = ResolveAnchors(
                frame,
                target => target is null ? screen : Resolve(target),
                sizeRect,
                out var notes,
                out var anchorNotes);

            visiting.Remove(name);

            foreach (var note in notes)
                Report(name, note);

            rects[name] = outcome.Rect;
            notesByFrame[name] = anchorNotes;
            return outcome.Rect;
        }

        foreach (var frame in project.Frames)
            Resolve(frame.Name);

        var frames = new Dictionary<string, FrameLayout>(StringComparer.Ordinal);
        foreach (var frame in project.Frames)
        {
            var hasRect = rects.TryGetValue(frame.Name, out var rect);
            var effectiveVisible = frame.Visible && FrameHierarchy.Ancestors(project, frame.Name)
                .All(ancestor => byName.TryGetValue(ancestor, out var a) && a.Visible);

            var anchorIssues = issues.Where(i => i.Frame == frame.Name).Select(i => i.Message).ToArray();
            var frameNotes = notesByFrame.TryGetValue(frame.Name, out var foundNotes)
                ? foundNotes
                : [];
            var anchored = hasRect
                ? ResolveAnchorDetails(frame, rect, byName, rects, screen, frameNotes)
                : FrameLayout.NoAnchors;

            frames[frame.Name] = new FrameLayout(
                frame.Name,
                hasRect ? rect : null,
                hasRect ? rect.Width : frame.Width,
                hasRect ? rect.Height : frame.Height,
                frame.Visible,
                effectiveVisible,
                frame.ReferenceName,
                SizeNameOf(frame),
                anchorIssues,
                anchored,
                [.. frameNotes.Where(n => n.Index >= 0).Select(n => $"Anchor {n.Index + 1}: {n.Message}")]);
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

    /// <summary>
    /// Projects a frame's anchors into absolute model coordinates so the canvas can draw the
    /// relationship instead of leaving the user to infer it from two rectangles.
    /// </summary>
    private static IReadOnlyList<ResolvedAnchor> ResolveAnchorDetails(
        FrameDef frame,
        FrameRect ownRect,
        IReadOnlyDictionary<string, FrameDef> byName,
        IReadOnlyDictionary<string, FrameRect> rects,
        FrameRect screen,
        IReadOnlyList<AnchorNote> notes)
    {
        var anchors = frame.AllAnchors;
        var details = new List<ResolvedAnchor>(anchors.Count);

        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            var targetName = anchor.RelativeTo ?? frame.Parent;
            FrameRect? targetRect = null;

            if (targetName is null)
            {
                targetRect = screen;
            }
            else if (rects.TryGetValue(targetName, out var found))
            {
                targetRect = found;
            }

            var targetIsPlaceholder = targetName is not null
                                      && byName.TryGetValue(targetName, out var targetFrame)
                                      && targetFrame.Placeholder;

            var messages = notes
                .Where(n => n.Index == i)
                .Select(n => n.Message)
                .ToArray();

            var ownPosition = AnchorPosition(ownRect, anchor.Point);
            var targetPosition = targetRect is null
                ? (ModelPoint?)null
                : AnchorPosition(targetRect.Value, anchor.RelativePoint);

            if (targetPosition is { } target)
            {
                // The resolver reports what it could not honour; this checks the residue, because
                // an over-determined or unsolvable anchor can still leave both ends positioned
                // while disagreeing by a few pixels. A connector that does not meet its target
                // must not be drawn as if it did.
                var expected = target + new ModelPoint(anchor.OffsetX, anchor.OffsetY);
                if (Math.Abs(expected.X - ownPosition.X) > AnchorTolerance ||
                    Math.Abs(expected.Y - ownPosition.Y) > AnchorTolerance)
                {
                    if (messages.Length == 0)
                        messages =
                        [
                            $"Its {anchor.Point} sits at ({Num(ownPosition.X)}, {Num(ownPosition.Y)}) but {anchor.RelativePoint} " +
                            $"of the target plus the offsets is at ({Num(expected.X)}, {Num(expected.Y)}); this relationship does not hold."
                        ];
                }
            }

            var resolved = targetPosition is not null && messages.Length == 0;

            details.Add(new ResolvedAnchor(
                i,
                i == 0,
                targetName,
                targetIsPlaceholder,
                anchor.Point,
                anchor.RelativePoint,
                anchor.OffsetX,
                anchor.OffsetY,
                ownPosition,
                targetPosition ?? default,
                targetPosition is not null,
                resolved,
                resolved
                    ? null
                    : messages.Length > 0
                        ? string.Join(" ", messages)
                        : $"Target \"{targetName ?? "UIParent"}\" could not be positioned."));
        }

        return details;

        static string Num(double value) => value == Math.Floor(value)
            ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// How far an anchor's two ends may disagree and still count as holding, in model units.
    /// </summary>
    /// <remarks>
    /// A frame at 1024x768 built from XML numbers rounded to whole pixels accumulates error well
    /// under a thousandth, while a genuinely unsolved multi-anchor frame misses by whole pixels or
    /// by the size of the frame. Anything in between would be a judgement call, and a judgement
    /// call drawn on the canvas is exactly what this tool must not make silently.
    /// </remarks>
    private const double AnchorTolerance = 1e-6;

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
