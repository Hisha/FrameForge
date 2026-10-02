using FrameForge.Core.Models;

namespace FrameForge.Core.Geometry;

/// <summary>
/// Parent/child navigation over a <see cref="Project"/>.
/// </summary>
/// <remarks>
/// Every walk is cycle-safe. A project file is user-editable JSON, so a
/// <c>parent</c> cycle must terminate rather than hang or blow the stack.
/// </remarks>
public static class FrameHierarchy
{
    /// <summary>Direct children of <paramref name="name"/>, in authored order. Null selects root frames.</summary>
    public static IReadOnlyList<FrameDef> Children(Project project, string? name) =>
        project.Frames.Where(f => f.Parent == name).ToArray();

    /// <summary>
    /// <paramref name="name"/> followed by every frame beneath it, depth first.
    /// </summary>
    public static IReadOnlyList<string> Subtree(Project project, string name)
    {
        var output = new List<string>();
        Walk(project, name, output, new HashSet<string>(StringComparer.Ordinal));
        return output;
    }

    /// <summary>
    /// Alias of <see cref="Subtree"/>: the set a re-parenting control must refuse, because
    /// a frame cannot become its own descendant.
    /// </summary>
    public static IReadOnlyList<string> SelfAndDescendants(Project project, string name) => Subtree(project, name);

    /// <summary>Parent chain from the immediate parent upwards.</summary>
    public static IReadOnlyList<string> Ancestors(Project project, string name)
    {
        var output = new List<string>();
        var byName = project.Frames.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { name };

        var current = byName.TryGetValue(name, out var frame) ? frame.Parent : null;
        while (current is not null && seen.Add(current))
        {
            output.Add(current);
            current = byName.TryGetValue(current, out var next) ? next.Parent : null;
        }

        return output;
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="ancestor"/> or lives beneath it.</summary>
    public static bool IsDescendantOf(Project project, string candidate, string ancestor) =>
        candidate == ancestor || Subtree(project, ancestor).Contains(candidate, StringComparer.Ordinal);

    /// <summary>
    /// True when re-parenting <paramref name="frame"/> under <paramref name="newParent"/> would
    /// create a cycle. A null parent is always allowed.
    /// </summary>
    public static bool WouldCreateCycle(Project project, string frame, string? newParent) =>
        newParent is not null && IsDescendantOf(project, newParent, frame);

    /// <summary>Depth of each frame in the parent tree, used to paint parents behind children.</summary>
    public static IReadOnlyDictionary<string, int> Depths(Project project)
    {
        var byName = project.Frames.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var frame in project.Frames)
        {
            var depth = 0;
            var cursor = frame.Parent;
            var guard = new HashSet<string>(StringComparer.Ordinal) { frame.Name };
            while (cursor is not null && byName.TryGetValue(cursor, out var next) && guard.Add(cursor))
            {
                depth++;
                cursor = next.Parent;
            }

            depths[frame.Name] = depth;
        }

        return depths;
    }

    /// <summary>
    /// Depth-first walk guarded by a single visited set. Frame names are unique within a
    /// project and <see cref="FrameDef.Parent"/> is single-valued, so a node cannot be reached
    /// by two different branches; the visited set therefore needs no per-branch path copy and
    /// a malformed parent cycle still terminates.
    /// </summary>
    private static void Walk(
        Project project,
        string current,
        List<string> output,
        HashSet<string> seen)
    {
        if (!seen.Add(current))
            return;

        output.Add(current);

        foreach (var child in Children(project, current))
            Walk(project, child.Name, output, seen);
    }
}