using FrameForge.Core.Models;

namespace FrameForge.Core.Geometry;

/// <summary>One multi-selection geometry operation offered by the DESIGN workspace.</summary>
public enum SelectionArrangeCommand
{
    AlignLeft,
    AlignCenterHorizontal,
    AlignRight,
    AlignTop,
    AlignCenterVertical,
    AlignBottom,
    DistributeHorizontal,
    DistributeVertical,
}

/// <summary>One selected frame that was deliberately left alone, and why.</summary>
/// <param name="Name">Frame name.</param>
/// <param name="Reason">Human-readable explanation.</param>
public readonly record struct SelectionArrangeBlockage(string Name, string Reason)
{
    /// <inheritdoc />
    public override string ToString() => $"{Name}: {Reason}";
}

/// <summary>
/// The result of an arrange or move request: the project to keep, what moved, and everything that
/// did not move together with an explanation.
/// </summary>
/// <remarks>
/// The outcome carries its own diagnostics instead of throwing or silently skipping, because the
/// interesting failure mode here is not "nothing happened" but "three of my four frames moved and
/// I do not know why the fourth did not".
/// </remarks>
/// <param name="Project">
/// The project to keep. Reference-equal to the input when nothing changed, so callers can use it as
/// a dirty flag.
/// </param>
/// <param name="Command">The command that produced this outcome.</param>
/// <param name="Moved">Frames whose <c>OffsetsX</c>/<c>OffsetsY</c> changed, in application order.</param>
/// <param name="ExcludedLocked">Locked frames that were skipped without being touched.</param>
/// <param name="Blocked">Editable frames that could not be moved safely, with the reason.</param>
public sealed record SelectionArrangeOutcome(
    Project Project,
    SelectionArrangeCommand Command,
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> ExcludedLocked,
    IReadOnlyList<SelectionArrangeBlockage> Blocked)
{
    /// <summary>True when at least one frame moved.</summary>
    public bool Changed => Moved.Count > 0;

    /// <summary>True when something was deliberately left alone, which the UI must say out loud.</summary>
    public bool HasExclusions => ExcludedLocked.Count > 0 || Blocked.Count > 0;

    /// <summary>One sentence describing the whole operation, suitable for the status bar.</summary>
    public string Message => Summarize(Moved.Count, ExcludedLocked, Blocked);

    internal static string Summarize(
        int moved,
        IReadOnlyList<string> excludedLocked,
        IReadOnlyList<SelectionArrangeBlockage> blocked)
    {
        var movedText = moved == 1 ? "1 object moved" : $"{moved} objects moved";
        if (excludedLocked.Count == 0 && blocked.Count == 0)
            return movedText;

        var parts = new List<string>();
        if (excludedLocked.Count > 0)
        {
            parts.Add(excludedLocked.Count == 1
                ? $"1 locked object left unchanged ({excludedLocked[0]})"
                : $"{excludedLocked.Count} locked objects left unchanged");
        }

        foreach (var blockage in blocked)
            parts.Add($"\"{blockage.Name}\" was not moved because {blockage.Reason}");

        return $"{movedText}; " + string.Join("; ", parts) + ".";
    }
}

/// <summary>
/// Multi-selection alignment, equal-gap distribution, and rigid translation over resolved
/// DESIGN geometry.
/// </summary>
/// <remarks>
/// The whole contract of this type is that it only ever adds <c>OffsetsX</c>/<c>OffsetsY</c> to the
/// frames it is asked to move. It never writes an absolute position, never touches
/// <c>Point</c>/<c>RelativePoint</c>, never changes size, parent, stratum, level, kind, visual or
/// text, and never touches a frame the caller did not name. Anchor relationships therefore survive
/// an align or a drag exactly as authored - which matters because these frames are imported
/// Blizzard markup where the relationship, not the number, is the source of truth.
/// <para>
/// Anything that cannot honour that contract is reported instead of approximated: a locked frame,
/// a <c>SetAllPoints</c> frame whose offsets are ignored, a multi-anchor stretch frame whose size
/// is derived from two edges, or a frame whose anchor does not resolve. Reporting beats guessing,
/// because a wrong guess is indistinguishable from a correct one until the user runs the game.
/// <para>
/// "Locked" is scoped to modification, not to participation. A locked frame is never written to, but
/// it is fully part of what an alignment measures - see <see cref="Bounds"/> and <see cref="CanRun"/>.
/// </para>
/// </remarks>
public static class SelectionArrange
{
    /// <summary>Movement smaller than this is treated as "already there", in model units.</summary>
    public const double Tolerance = 1e-6;

    /// <summary>True for the two distribution commands.</summary>
    public static bool IsDistribution(SelectionArrangeCommand command) =>
        command is SelectionArrangeCommand.DistributeHorizontal or SelectionArrangeCommand.DistributeVertical;

    /// <summary>
    /// How many objects the command needs in its selection before it can do anything.
    /// </summary>
    /// <remarks>
    /// For alignment this counts <em>selected</em> objects, because a locked object contributes
    /// reference geometry even though it cannot be modified. For distribution it counts
    /// <em>movable</em> objects, because equal gaps need two fixed endpoints that the tool is
    /// allowed to rely on.
    /// </remarks>
    public static int RequiredCount(SelectionArrangeCommand command) => IsDistribution(command) ? 3 : 2;

    /// <summary>Short button label.</summary>
    public static string Label(SelectionArrangeCommand command) => command switch
    {
        SelectionArrangeCommand.AlignLeft => "Left",
        SelectionArrangeCommand.AlignCenterHorizontal => "Center H",
        SelectionArrangeCommand.AlignRight => "Right",
        SelectionArrangeCommand.AlignTop => "Top",
        SelectionArrangeCommand.AlignCenterVertical => "Center V",
        SelectionArrangeCommand.AlignBottom => "Bottom",
        SelectionArrangeCommand.DistributeHorizontal => "Distribute H",
        SelectionArrangeCommand.DistributeVertical => "Distribute V",
        _ => command.ToString(),
    };

    /// <summary>Tooltip explaining exactly what the command does to the current selection.</summary>
    public static string Description(SelectionArrangeCommand command) => command switch
    {
        SelectionArrangeCommand.AlignLeft => "Align the left edges of the selected objects.",
        SelectionArrangeCommand.AlignCenterHorizontal => "Align the horizontal centers of the selected objects.",
        SelectionArrangeCommand.AlignRight => "Align the right edges of the selected objects.",
        SelectionArrangeCommand.AlignTop => "Align the top edges of the selected objects.",
        SelectionArrangeCommand.AlignCenterVertical => "Align the vertical centers of the selected objects.",
        SelectionArrangeCommand.AlignBottom => "Align the bottom edges of the selected objects.",
        SelectionArrangeCommand.DistributeHorizontal => "Space the selected objects so the gaps between them are equal. The leftmost and rightmost stay put.",
        SelectionArrangeCommand.DistributeVertical => "Space the selected objects so the gaps between them are equal. The topmost and bottom-most stay put.",
        _ => string.Empty,
    };

    /// <summary>
    /// The union of the resolved rectangles of <paramref name="names"/>, or null when none of them
    /// could be resolved.
    /// </summary>
    /// <remarks>
    /// This is what an alignment targets. It includes every named frame that resolved, including
    /// locked ones: a locked frame is not modified, but it is still part of "the current
    /// selection bounds" the user is aligning against.
    /// </remarks>
    public static FrameRect? Bounds(LayoutResult layout, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(names);

        FrameRect? bounds = null;
        foreach (var name in names)
        {
            if (layout.Rects.TryGetValue(name, out var rect))
                bounds = bounds is null ? rect : bounds.Value.Union(rect);
        }

        return bounds;
    }

    /// <summary>
    /// How many of <paramref name="names"/> actually have resolved geometry, ignoring duplicates
    /// and names that are not part of the selection any more.
    /// </summary>
    /// <remarks>
    /// This is the population an alignment measures. It counts locked frames, because they are
    /// reference geometry rather than an obstacle, and it skips frames the layout could not resolve
    /// because an alignment target derived from a frame nobody can see is not a target.
    /// </remarks>
    public static int ResolvedCount(LayoutResult layout, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(names);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var name in names)
        {
            if (!string.IsNullOrEmpty(name) && seen.Add(name) && layout.Rects.ContainsKey(name))
                count++;
        }

        return count;
    }

    /// <summary>
    /// Whether <paramref name="command"/> can do real work for this selection right now.
    /// </summary>
    /// <remarks>
    /// This is the single rule behind both the align button's enabled state and the command's own
    /// refusal, so the button can never offer an operation that would do nothing and never hide one
    /// that would.
    /// <para>
    /// A locked object counts as a usable member of an alignment selection. Locked means "this
    /// object cannot be modified", not "this object does not exist": a locked Blizzard text label
    /// and an editable custom text label selected together, then Align Top, is the ordinary case of
    /// lining a new element up with the stock framework, and the editable one has to move for it.
    /// Requiring two <em>editable</em> objects would make that - and only that - case silently fail,
    /// while the same selection of two editable objects would work, which reads as a bug rather than
    /// a rule.
    /// </para>
    /// </remarks>
    public static bool CanRun(
        Project project,
        LayoutResult layout,
        IReadOnlyList<string> names,
        SelectionArrangeCommand command)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(names);

        Classify(project, layout, names, out var movable, out _, out _);
        return movable.Count > 0 && HasEnoughForCommand(layout, names, movable, command, out _);
    }

    /// <summary>
    /// Translates every selected object by one delta, as a rigid group.
    /// </summary>
    /// <remarks>
    /// A selected frame whose own anchor target is also selected does not get its own offset
    /// change: it already follows that frame, so adding one would move it twice. Every selected
    /// object still ends up one delta away from where it started, which is what dragging a group
    /// has to mean.
    /// </remarks>
    public static SelectionArrangeOutcome Move(
        Project project,
        LayoutResult layout,
        IReadOnlyList<string> names,
        double dx,
        double dy)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(names);

        var selection = Classify(project, layout, names, out var movable, out var excluded, out var blocked);
        var command = SelectionArrangeCommand.AlignLeft;
        if (movable.Count == 0 || (Math.Abs(dx) < Tolerance && Math.Abs(dy) < Tolerance))
            return new SelectionArrangeOutcome(project, command, [], excluded, blocked);

        var follows = new HashSet<string>(
            movable.Where(name => project.Find(name)?.ReferenceName is { } reference && selection.Contains(reference)),
            StringComparer.Ordinal);

        var movableSet = new HashSet<string>(movable, StringComparer.Ordinal);
        var moved = new List<string>();
        var frames = new List<FrameDef>(project.Frames);
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            if (!movableSet.Contains(frame.Name) || follows.Contains(frame.Name))
                continue;

            frames[index] = frame with { OffsetX = frame.OffsetX + dx, OffsetY = frame.OffsetY + dy };
            moved.Add(frame.Name);
        }

        if (moved.Count == 0)
            return new SelectionArrangeOutcome(project, command, [], excluded, blocked);

        return new SelectionArrangeOutcome(project with { Frames = [.. frames] }, command, moved, excluded, blocked);
    }

    /// <summary>
    /// Aligns or distributes the selected objects and returns the project to keep.
    /// </summary>
    /// <remarks>
    /// Alignment moves one edge of every movable object onto the corresponding edge of the current
    /// selection bounds, so it never changes a size. Distribution leaves the two outermost movable
    /// objects where they are and makes the gaps between consecutive movable objects equal, which
    /// needs three.
    /// <para>
    /// The alignment target is the bounds of the whole requested selection, including locked frames:
    /// a locked object is not moved, but it is part of the selection the user is aligning against,
    /// and silently ignoring its extent would move the editable objects to somewhere the user did
    /// not ask for. That is also why the size gate counts selected objects rather than editable
    /// ones - see <see cref="CanRun"/>. Distribution instead works purely among the objects it may
    /// move, because equal spacing needs two fixed endpoints and inventing one on behalf of a locked
    /// object would be a bigger guess than the situation calls for.
    /// </para>
    /// <para>
    /// Targets are computed once from the layout as it was when the user asked, then applied
    /// outermost-first with the layout re-resolved in between. That is what makes a selection
    /// containing both a container and one of its children come out right instead of double-shifted:
    /// each child is measured against where it actually is after its parent moved, and is nudged
    /// the remaining distance to the position the user asked for.
    /// </para>
    /// </remarks>
    public static SelectionArrangeOutcome Arrange(
        Project project,
        LayoutResult layout,
        IReadOnlyList<string> names,
        SelectionArrangeCommand command)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(names);

        Classify(project, layout, names, out var movable, out var excluded, out var blocked);

        // Every selected object is locked (or otherwise unmodifiable), so there is nothing this
        // command could do. Report that rather than returning an unexplained no-op.
        if (movable.Count == 0)
            return new SelectionArrangeOutcome(project, command, [], excluded, blocked);

        if (!HasEnoughForCommand(layout, names, movable, command, out var shortfall))
        {
            blocked = [.. blocked, new SelectionArrangeBlockage(
                names.Count == 1 ? names[0] : "selection", shortfall)];
            return new SelectionArrangeOutcome(project, command, [], excluded, blocked);
        }

        var targets = command switch
        {
            SelectionArrangeCommand.DistributeHorizontal or SelectionArrangeCommand.DistributeVertical
                => DistributionTargets(layout, movable, command),
            _ => AlignmentTargets(layout, movable, command, Bounds(layout, names)),
        };

        var work = project;
        var current = layout;
        var frames = new List<FrameDef>(work.Frames);
        var moved = new List<string>();
        var extraBlockages = new List<SelectionArrangeBlockage>();

        // Ancestors first: a child can only be placed once its container has settled.
        foreach (var name in OrderedByDepth(work, movable))
        {
            if (!current.Rects.TryGetValue(name, out var rect))
            {
                extraBlockages.Add(new SelectionArrangeBlockage(name, "its geometry could not be resolved while the selection was being moved"));
                continue;
            }

            var target = targets[name];
            var deltaX = Snap(target.Left - rect.Left);
            var deltaY = Snap(target.Top - rect.Top);
            if (Math.Abs(deltaX) < Tolerance && Math.Abs(deltaY) < Tolerance)
                continue;

            var index = frames.FindIndex(frame => frame.Name == name);
            var frame = frames[index];
            frames[index] = frame with { OffsetX = frame.OffsetX + deltaX, OffsetY = frame.OffsetY + deltaY };
            moved.Add(name);

            // Re-resolving per frame is what keeps a container and its child from ending up in
            // two different places. It costs one pass over the project per object that actually
            // moves, and only for the handful of objects a single command touches.
            work = work with { Frames = [.. frames] };
            current = LayoutResolver.Resolve(work);
        }

        if (moved.Count == 0)
            return new SelectionArrangeOutcome(project, command, [], excluded, [.. blocked, .. extraBlockages]);

        return new SelectionArrangeOutcome(work, command, moved, excluded, [.. blocked, .. extraBlockages]);
    }

    /// <summary>
    /// Whether the selection is big enough for the command, and if not, the sentence to say so.
    /// </summary>
    /// <remarks>
    /// The two commands count different populations, and the difference is the whole point of
    /// <see cref="CanRun"/>. Alignment counts resolved selected objects, locked ones included,
    /// because the target is the bounds of everything that was selected and the locked objects are
    /// part of those bounds. Distribution counts only movable objects, because it invents an
    /// arrangement between fixed endpoints and a locked object is not one the tool may rely on
    /// staying exactly where it is if the layout is later re-resolved.
    /// </remarks>
    private static bool HasEnoughForCommand(
        LayoutResult layout,
        IReadOnlyList<string> names,
        List<string> movable,
        SelectionArrangeCommand command,
        out string shortfall)
    {
        var required = RequiredCount(command);
        var (available, unit) = IsDistribution(command)
            ? (movable.Count, "movable objects")
            : (ResolvedCount(layout, names), "selected objects");

        if (available >= required)
        {
            shortfall = string.Empty;
            return true;
        }

        shortfall = $"{Label(command).ToLowerInvariant()} needs at least {required} {unit} and the selection has {available}";
        return false;
    }

    /// <summary>
    /// Splits a selection into movable, locked, and unsafe frames, reporting each in turn.
    /// </summary>
    private static HashSet<string> Classify(
        Project project,
        LayoutResult layout,
        IReadOnlyList<string> names,
        out List<string> movable,
        out List<string> excluded,
        out List<SelectionArrangeBlockage> blocked)
    {
        var selection = new HashSet<string>(StringComparer.Ordinal);
        movable = [];
        excluded = [];
        blocked = [];

        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name) || !selection.Add(name))
                continue;

            if (!project.Contains(name))
                continue;

            if (project.Editor.IsLocked(name))
            {
                excluded.Add(name);
                continue;
            }

            var reason = MovementBlockage(project, layout, name);
            if (reason is null)
                movable.Add(name);
            else
                blocked.Add(new SelectionArrangeBlockage(name, reason));
        }

        return selection;
    }

    /// <summary>Why this frame cannot be moved by adding offsets, or null when it can.</summary>
    private static string? MovementBlockage(Project project, LayoutResult layout, string name)
    {
        var frame = project.Find(name);
        if (frame is null)
            return "it is no longer part of the project";

        if (!layout.Rects.ContainsKey(name))
            return "its geometry could not be resolved";

        if (frame.SetAllPoints)
            return "SetAllPoints ignores OffsetsX/OffsetsY";

        if (frame.ExtraAnchors.Count > 0)
            return "its size is derived from more than one anchor, so moving it would change its size";

        if (layout.Frames.TryGetValue(name, out var resolved) && resolved.Anchors.Any(anchor => !anchor.Resolved))
            return "its anchor relationship does not resolve cleanly";

        return null;
    }

    private static Dictionary<string, FrameRect> AlignmentTargets(
        LayoutResult layout,
        List<string> movable,
        SelectionArrangeCommand command,
        FrameRect? bounds)
    {
        if (bounds is not { } reference)
            return movable.ToDictionary(name => name, name => layout.Rects[name], StringComparer.Ordinal);

        var targets = new Dictionary<string, FrameRect>(StringComparer.Ordinal);
        foreach (var name in movable)
        {
            var rect = layout.Rects[name];
            var left = rect.Left;
            var top = rect.Top;

            switch (command)
            {
                case SelectionArrangeCommand.AlignLeft:
                    left = reference.Left;
                    break;
                case SelectionArrangeCommand.AlignCenterHorizontal:
                    left = reference.CenterX - rect.Width / 2;
                    break;
                case SelectionArrangeCommand.AlignRight:
                    left = reference.Right - rect.Width;
                    break;
                case SelectionArrangeCommand.AlignTop:
                    top = reference.Top;
                    break;
                case SelectionArrangeCommand.AlignCenterVertical:
                    top = reference.CenterY + rect.Height / 2;
                    break;
                case SelectionArrangeCommand.AlignBottom:
                    top = reference.Bottom + rect.Height;
                    break;
            }

            targets[name] = FrameRect.FromSize(left, top, rect.Width, rect.Height);
        }

        return targets;
    }

    private static Dictionary<string, FrameRect> DistributionTargets(
        LayoutResult layout,
        List<string> movable,
        SelectionArrangeCommand command)
    {
        var horizontal = command == SelectionArrangeCommand.DistributeHorizontal;
        var ordered = movable
            .OrderBy(name => horizontal ? layout.Rects[name].Left : -layout.Rects[name].Top)
            .ThenBy(name => layout.Rects[name].Left)
            .ToArray();

        var targets = new Dictionary<string, FrameRect>(StringComparer.Ordinal);
        foreach (var name in movable)
            targets[name] = layout.Rects[name];

        var lead = ordered[0];
        var last = ordered[^1];
        var span = horizontal
            ? layout.Rects[last].Right - layout.Rects[lead].Left
            : layout.Rects[lead].Top - layout.Rects[last].Bottom;
        var occupied = ordered.Sum(name => horizontal ? layout.Rects[name].Width : layout.Rects[name].Height);
        var gap = (span - occupied) / (ordered.Length - 1);

        // Which way the cursor walks. Model space is +Y up, so a left-to-right pass and a
        // top-to-bottom pass step in opposite directions along their own axis; getting this
        // backwards pushes the middle objects out of the selection instead of into the gaps.
        var step = horizontal ? 1 : -1;

        // The leading edge of the box just placed. The two outermost objects are the endpoints the
        // user can already see, so they stay put and only the ones between them are distributed.
        var cursor = horizontal ? layout.Rects[lead].Left : layout.Rects[lead].Top;
        for (var i = 0; i < ordered.Length; i++)
        {
            var name = ordered[i];
            var rect = layout.Rects[name];
            var edge = horizontal ? rect.Left : rect.Top;
            var delta = i > 0 && i < ordered.Length - 1 ? cursor - edge : 0;

            if (Math.Abs(delta) >= Tolerance)
                targets[name] = horizontal ? rect.Translate(delta, 0) : rect.Translate(0, delta);

            cursor = edge + delta + step * ((horizontal ? rect.Width : rect.Height) + gap);
        }

        return targets;
    }

    /// <summary>Selected frames ordered so an ancestor is always processed before its descendants.</summary>
    private static IEnumerable<string> OrderedByDepth(Project project, List<string> movable)
    {
        var depths = FrameHierarchy.Depths(project);
        return movable
            .OrderBy(name => depths.TryGetValue(name, out var depth) ? depth : 0)
            .ThenBy(name => movable.IndexOf(name));
    }

    /// <summary>Kills floating point noise so a frame that is already aligned does not move by 1e-15.</summary>
    private static double Snap(double value) => Math.Abs(value) < Tolerance ? 0 : value;
}