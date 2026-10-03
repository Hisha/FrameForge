namespace FrameForge.Core.Models;

/// <summary>
/// One point-to-point placement relationship: "the <see cref="Point"/> of a frame is placed on
/// the <see cref="RelativePoint"/> of <see cref="RelativeTo"/>, displaced by the offsets".
/// </summary>
/// <remarks>
/// This is the unit WoW itself models. A <see cref="FrameDef"/> with a single anchor is
/// exactly one of these; a frame with several anchors has a primary one plus
/// <see cref="FrameDef.ExtraAnchors"/>. Splitting the anchor out is what lets FrameForge
/// carry an imported multi-anchor frame without discarding anchor #2.
/// <para>
/// MODEL COORDINATE SEMANTICS are unchanged from <see cref="AnchorPoint"/>: +X right, +Y UP.
/// </para>
/// </remarks>
public sealed record FrameAnchor
{
    /// <summary>The point of THIS frame that is placed. Defaults to TOPLEFT, as WoW does.</summary>
    public AnchorPoint Point { get; init; } = AnchorPoint.TOPLEFT;

    /// <summary>
    /// The frame this one is anchored against, or null to inherit <see cref="FrameDef.Parent"/>.
    /// Null on both means UIParent, i.e. the screen.
    /// </summary>
    public string? RelativeTo { get; init; }

    /// <summary>
    /// The point of <see cref="RelativeTo"/> that <see cref="Point"/> is placed on.
    /// When FrameXML omits <c>relativePoint</c>, WoW substitutes <see cref="Point"/>, and the
    /// importer does the same so the imported value is the effective one, not a default.
    /// </summary>
    public AnchorPoint RelativePoint { get; init; } = AnchorPoint.TOPLEFT;

    /// <summary>Offset in model units. Positive is right.</summary>
    public double OffsetX { get; init; }

    /// <summary>Offset in model units. Positive is UP.</summary>
    public double OffsetY { get; init; }

    /// <summary>Convenience constructor for the common call sites.</summary>
    public static FrameAnchor Create(
        AnchorPoint point,
        string? relativeTo,
        AnchorPoint relativePoint,
        double offsetX = 0,
        double offsetY = 0) => new()
    {
        Point = point,
        RelativeTo = relativeTo,
        RelativePoint = relativePoint,
        OffsetX = offsetX,
        OffsetY = offsetY,
    };
}
