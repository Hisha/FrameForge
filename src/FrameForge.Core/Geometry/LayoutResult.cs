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

/// <summary>Everything the editor needs to know about one resolved frame.</summary>
/// <param name="Name">Frame name.</param>
/// <param name="Rect">Absolute model-space bounds, or null when the frame could not be resolved.</param>
/// <param name="Width">Effective width (a fraction of the parent resolves to real units).</param>
/// <param name="Height">Effective height.</param>
/// <param name="OwnVisible">The frame's own <c>visible</c> flag.</param>
/// <param name="EffectiveVisible"><c>OwnVisible</c> AND every ancestor visible; WoW does not draw hidden subtrees.</param>
/// <param name="AnchoredTo">Frame the anchor was actually resolved against; null means the screen.</param>
/// <param name="SizedTo">Frame that supplied the size; null means literal units.</param>
/// <param name="Issues">Problems reported for this frame.</param>
public readonly record struct FrameLayout(
    string Name,
    FrameRect? Rect,
    double Width,
    double Height,
    bool OwnVisible,
    bool EffectiveVisible,
    string? AnchoredTo,
    string? SizedTo,
    IReadOnlyList<string> Issues);

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