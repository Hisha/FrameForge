using FrameForge.Core.Geometry;
using FrameForge.Core.Models;

namespace FrameForge.Core.Viewing;

/// <summary>
/// Which tree nodes survive a filter and a search, and which of those actually matched.
/// </summary>
/// <param name="Visible">
/// Every node the tree should show: the matches plus the ancestors needed to reach them. An
/// ancestor shown only for context is in <see cref="Visible"/> but NOT in <see cref="Matched"/>,
/// so the tree can render a path to a hit without pretending the path matched.
/// </param>
/// <param name="Matched">Nodes that passed both the filter and the search on their own name.</param>
/// <param name="Filtering">True when anything at all is being hidden.</param>
public sealed record TreeProjection(IReadOnlySet<string> Visible, IReadOnlySet<string> Matched, bool Filtering);

/// <summary>
/// Turns a project into the set of tree nodes to show.
/// </summary>
/// <remarks>
/// The tree must stay a truthful representation of the document, so nothing here ever edits the
/// project: filtering and searching decide what is DISPLAYED, and the full hierarchy is one
/// click away in <see cref="TreeFilter.ALL"/>.
/// <para>
/// Search keeps ancestor context, because a match with no path to it is not navigable. Finding
/// <c>$parentTier</c> is only useful if the tree still shows which panel it belongs to, so the
/// projection returns the matches and their ancestor chain together.
/// </para>
/// </remarks>
public static class TreeProjectionBuilder
{
    /// <summary>Projects the project through a structural filter and an optional name search.</summary>
    /// <param name="project">The project. Never modified.</param>
    /// <param name="filter">Structural narrowing.</param>
    /// <param name="search">Case-insensitive substring; null, empty or whitespace means no search.</param>
    public static TreeProjection Resolve(Project project, TreeFilter filter, string? search)
    {
        var query = search?.Trim();
        var searching = !string.IsNullOrEmpty(query);

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in project.Frames)
        {
            if (!MatchesFilter(frame, filter))
                continue;

            if (searching && !NameMatches(frame.Name, query))
                continue;

            matched.Add(frame.Name);
        }

        if (!searching && filter == TreeFilter.ALL)
            return new TreeProjection(matched, matched, false);

        var visible = new HashSet<string>(matched, StringComparer.Ordinal);
        foreach (var name in matched)
        {
            foreach (var ancestor in FrameHierarchy.Ancestors(project, name))
                visible.Add(ancestor);
        }

        return new TreeProjection(visible, matched, visible.Count != project.Frames.Count);
    }

    /// <summary>True when the frame belongs in this structural filter.</summary>
    public static bool MatchesFilter(FrameDef frame, TreeFilter filter) => filter switch
    {
        TreeFilter.ALL => true,
        TreeFilter.STRUCTURE => ViewPolicy.CategoryOf(frame) == FrameCategory.STRUCTURE,
        TreeFilter.VISUAL => ViewPolicy.CategoryOf(frame) == FrameCategory.VISUAL,
        _ => true,
    };

    /// <summary>Case-insensitive substring match on a frame name.</summary>
    public static bool NameMatches(string name, string? query) =>
        string.IsNullOrEmpty(query)
        || name.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Decides which widget is under a point on the canvas.
/// </summary>
/// <remarks>
/// Hit testing lives in Core, not in the control, because "topmost visible widget wins" is a
/// statement about paint order and visibility policy rather than about Avalonia. The control
/// supplies a point; this returns every candidate it could mean, front to back.
/// <para>
/// A single answer is not always honest. NativeHuntsFrame deliberately overlaps its identity,
/// state, idle and record panels - they occupy the same rectangle and exactly one is shown at a
/// time in WoW - so a click there is genuinely ambiguous. Rather than pick one and hide the
/// rest, the ordered candidate list is returned and the canvas cycles through it.
/// </para>
/// </remarks>
public static class HitTester
{
    /// <summary>
    /// How close to a zero-area widget's anchor point a click still counts, in canvas pixels.
    /// </summary>
    /// <remarks>
    /// A widget with no width and no height has no interior to click. FrameXML produces plenty -
    /// nine of NativeHuntsFrame's 52 elements - and in WoW every one of them is still visible and
    /// still clickable, so refusing to select them would make them unreachable from the canvas
    /// entirely.
    /// </remarks>
    public const double ZeroAreaPickTolerance = 4;

    /// <summary>
    /// Widgets under a canvas point, front to back: index 0 is the one drawn on top.
    /// </summary>
    /// <remarks>
    /// Ordering is the reverse of <see cref="LayoutResult.PaintOrder"/>, which is the order the
    /// canvas draws in, so "topmost wins" is the last thing drawn rather than the first.
    /// </remarks>
    /// <param name="project">
    /// The project being hit-tested. Required, because a visibility filter names categories of
    /// widget and a <see cref="LayoutResult"/> only carries geometry - asking "is this point over
    /// a Texture" cannot be answered without knowing what each widget is.
    /// </param>
    /// <param name="filter">The same filter the canvas is drawing with.</param>
    public static IReadOnlyList<string> CandidatesAt(
        Project project,
        LayoutResult layout,
        Viewport viewport,
        CanvasOrigin origin,
        VisibilityFilter filter,
        double canvasX,
        double canvasY)
    {
        var hits = new List<string>();

        for (var i = layout.PaintOrder.Count - 1; i >= 0; i--)
        {
            var name = layout.PaintOrder[i];
            if (!layout.Rects.TryGetValue(name, out var rect))
                continue;

            if (project.Find(name) is not { } frame)
                continue;

            // The same predicate the renderer uses, so a click can never select something the
            // canvas is not drawing: filtering TEXT out of Preview must also make text
            // unclickable, or the toggle would appear not to work until the user clicked around.
            if (!ViewPolicy.IsVisible(frame, layout, filter))
                continue;

            var box = viewport.RectToCanvas(rect, origin);
            if (Hits(box, canvasX, canvasY, ZeroAreaPickTolerance))
                hits.Add(name);
        }

        return hits;
    }

    /// <summary>
    /// Whether a canvas point is inside the box, growing the tolerance for widgets that have no
    /// interior to click.
    /// </summary>
    /// <remarks>
    /// Avalonia's own <c>Rect.Contains</c> is exact, which is correct for a filled rectangle but
    /// makes a zero-size widget unpickable: there is no interior, so the test can only succeed by
    /// landing on the exact edge. The tolerance is therefore applied only when the widget is
    /// missing a dimension, never to a real rectangle, so ordinary hit behaviour is unchanged.
    /// </remarks>
    private static bool Hits(CanvasBox box, double canvasX, double canvasY, double tolerance)
    {
        var slack = box.Width <= 0 || box.Height <= 0 ? tolerance : 0;

        return canvasX >= box.X - slack
            && canvasX <= box.Right + slack
            && canvasY >= box.Y - slack
            && canvasY <= box.Bottom + slack;
    }

    /// <summary>The single widget a click should select, or null when nothing is there.</summary>
    /// <remarks>The project is needed to apply a visibility filter; see
    /// <see cref="CandidatesAt"/>.</remarks>
    public static string? TopmostAt(
        Project project,
        LayoutResult layout,
        Viewport viewport,
        CanvasOrigin origin,
        VisibilityFilter filter,
        double canvasX,
        double canvasY)
    {
        var candidates = CandidatesAt(project, layout, viewport, origin, filter, canvasX, canvasY);
        return candidates.Count == 0 ? null : candidates[0];
    }
}