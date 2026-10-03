using FrameForge.Core.Models;

namespace FrameForge.Core.Geometry;

/// <summary>A problem found while resolving a frame.</summary>
/// <param name="Frame">The frame the problem belongs to.</param>
/// <param name="Message">Human-readable explanation.</param>
public readonly record struct LayoutIssue(string Frame, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Frame}: {Message}";
}

/// <summary>
/// One resolved anchor relationship, in absolute model coordinates, ready to be drawn.
/// </summary>
/// <remarks>
/// This is what makes "what is controlling where this thing is?" answerable. For each anchor
/// the resolver records both ends of the relationship - where the frame's own anchor point sits
/// and where the target's relative point sits - plus the offsets between them, so the canvas
/// can draw a connector between the two and the inspector can print the numbers.
/// </remarks>
/// <param name="Index">Zero-based position in <see cref="FrameDef.AllAnchors"/>.</param>
/// <param name="Primary">True for the anchor the inspector edits.</param>
/// <param name="Target">Effective target frame name: relativeTo, else parent, else null (screen).</param>
/// <param name="TargetIsPlaceholder">True when <paramref name="Target"/> is a synthesized stand-in.</param>
/// <param name="Point">The point of this frame that is placed.</param>
/// <param name="RelativePoint">The point of the target it is placed on.</param>
/// <param name="OffsetX">Authored X offset; positive is right.</param>
/// <param name="OffsetY">Authored Y offset; positive is UP.</param>
/// <param name="OwnPosition">Absolute position of <paramref name="Point"/> on this frame.</param>
/// <param name="TargetPosition">
/// Absolute position of <paramref name="RelativePoint"/> on the target, or the default when
/// <paramref name="TargetPositioned"/> is false. Kept as a plain value so drawing code cannot
/// dereference nothing.
/// </param>
/// <param name="TargetPositioned">
/// True when the target frame could be positioned, which is the condition for a connector
/// between the two ends being meaningful at all. A default <paramref name="TargetPosition"/>
/// is the model origin, and the origin is a real place a frame can be.
/// </param>
/// <param name="Resolved">
/// True when the relationship actually holds for the resolved rectangle. False covers both ends
/// being unpositionable and the two ends disagreeing, because a connector drawn as though it held
/// would be a lie either way.
/// </param>
/// <param name="Note">Short explanation for an unresolved or partially handled anchor.</param>
public readonly record struct ResolvedAnchor(
    int Index,
    bool Primary,
    string? Target,
    bool TargetIsPlaceholder,
    AnchorPoint Point,
    AnchorPoint RelativePoint,
    double OffsetX,
    double OffsetY,
    ModelPoint OwnPosition,
    ModelPoint TargetPosition,
    bool TargetPositioned,
    bool Resolved,
    string? Note)
{
    /// <summary>1-based label for display.</summary>
    public string Label => $"Anchor {Index + 1}";
}

/// <summary>Everything the editor needs to know about one resolved frame.</summary>
/// <param name="Name">Frame name.</param>
/// <param name="Rect">Absolute model-space bounds, or null when the frame could not be resolved.</param>
/// <param name="Width">Effective width (a fraction of the parent resolves to real units).</param>
/// <param name="Height">Effective height.</param>
/// <param name="OwnVisible">The frame's own <c>visible</c> flag.</param>
/// <param name="EffectiveVisible"><c>OwnVisible</c> AND every ancestor visible; WoW does not draw hidden subtrees.</param>
/// <param name="AnchoredTo">Frame the primary anchor was actually resolved against; null means the screen.</param>
/// <param name="SizedTo">Frame that supplied the size; null means literal units.</param>
/// <param name="Issues">Problems reported for this frame.</param>
/// <param name="Anchors">Every anchor relationship, primary first.</param>
/// <param name="UnresolvedAnchorNotes">
/// Human-readable notes about anchors the resolver could not honour in full. These are exactly the
/// anchors whose <see cref="Anchors"/> entry has <c>Resolved == false</c>.
/// </param>
public readonly record struct FrameLayout(
    string Name,
    FrameRect? Rect,
    double Width,
    double Height,
    bool OwnVisible,
    bool EffectiveVisible,
    string? AnchoredTo,
    string? SizedTo,
    IReadOnlyList<string> Issues,
    IReadOnlyList<ResolvedAnchor> Anchors,
    IReadOnlyList<string> UnresolvedAnchorNotes)
{
    /// <summary>True when the frame declared more than one anchor.</summary>
    public bool HasMultipleAnchors => Anchors.Count > 1;

    /// <summary>An empty anchor set, for frames the engine never saw.</summary>
    public static IReadOnlyList<ResolvedAnchor> NoAnchors { get; } = [];
}


/// <summary>The full resolved state of a project.</summary>
public sealed record LayoutResult
{
    /// <summary>Absolute bounds by frame name. Frames that failed to resolve are absent.</summary>
    public required IReadOnlyDictionary<string, FrameRect> Rects { get; init; }

    /// <summary>Per-frame detail, present for every frame in the project.</summary>
    public required IReadOnlyDictionary<string, FrameLayout> Frames { get; init; }

    /// <summary>Frame names in draw order, back to front.</summary>
    public required IReadOnlyList<string> PaintOrder { get; init; }

    /// <summary>Every problem found while resolving, de-duplicated.</summary>
    public required IReadOnlyList<LayoutIssue> Issues { get; init; }

    /// <summary>Union of every resolved frame, or null when nothing resolved.</summary>
    public FrameRect? Bounds { get; init; }

    /// <summary>True when the layout resolved without any problems.</summary>
    public bool IsClean => Issues.Count == 0;
}