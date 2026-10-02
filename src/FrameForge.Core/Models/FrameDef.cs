namespace FrameForge.Core.Models;

/// <summary>
/// Frame draw-order strata, mirroring WoW's frame strata. Reserved in the v1 schema so
/// later work (backdrops, art, overlays) does not require a format change.
/// Lower values are painted underneath.
/// </summary>
public enum Stratum
{
    BACKGROUND,
    LOW,
    MEDIUM,
    HIGH,
    DIALOG,
    FULLSCREEN,
    TOOLTIP,
}

/// <summary>
/// What a frame's size and anchors are measured against. WoW's default for a
/// <c>&lt;Size&gt;</c> with no explicit frame is UIParent.
/// </summary>
public enum SizeReference
{
    /// <summary>Width and height are literal model units.</summary>
    SCREEN = 0,

    /// <summary>Width and height are fractions of the parent's size.</summary>
    PARENT = 1,
}

/// <summary>UIParent resolution: the virtual screen the layout is designed against.</summary>
/// <param name="Width">Screen width in WoW model units.</param>
/// <param name="Height">Screen height in WoW model units.</param>
public readonly record struct Screen(double Width, double Height)
{
    /// <summary>The 1024x768 default that 3.3.5a layouts are authored against.</summary>
    public static Screen Default => new(1024, 768);

    /// <summary>True when the screen has a usable, positive size.</summary>
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>
/// A single frame in a FrameForge layout.
/// </summary>
/// <remarks>
/// A frame is positioned by ONE anchor (WoW allows several; multi-anchor support is
/// roadmap work). <see cref="Point"/> names the point of THIS frame that is placed, and
/// <see cref="RelativePoint"/> names the point of <see cref="RelativeTo"/> it is placed against.
/// <para>
/// <see cref="RelativeTo"/> of null means "use <see cref="Parent"/>"; if the parent is also
/// null the frame is positioned against the screen (the WoW UIParent centre, model 0,0).
/// </para>
/// </remarks>
/// <param name="Name">Unique within a project; the key for parent/relativeTo references.</param>
/// <param name="Parent">
/// Parent frame name, or null for a root frame. Purely structural: it does not by itself
/// position the frame, it only drives the tree view and draw order.
/// </param>
/// <param name="Width">Width in model units, or a fraction of the parent when <paramref name="SizeReference"/> is PARENT.</param>
/// <param name="Height">Height in model units, or a fraction of the parent when <paramref name="SizeReference"/> is PARENT.</param>
/// <param name="Point">The point of this frame that is placed.</param>
/// <param name="RelativeTo">The frame this frame is anchored against, or null to inherit <paramref name="Parent"/>.</param>
/// <param name="RelativePoint">The point of <paramref name="RelativeTo"/> that <paramref name="Point"/> is placed on.</param>
/// <param name="OffsetX">Offset in model units. Positive is right.</param>
/// <param name="OffsetY">Offset in model units. Positive is UP.</param>
/// <param name="Visible">False corresponds to WoW's <c>hidden="true"</c>.</param>
/// <param name="SizeReference">What the size is measured against. Null means SCREEN.</param>
/// <param name="Stratum">Draw-order stratum. Null means MEDIUM.</param>
/// <param name="Level">Sub-order within the stratum. Null means 0.</param>
public sealed record FrameDef
{
    public required string Name { get; init; }

    public string? Parent { get; init; }

    public double Width { get; init; } = 100;

    public double Height { get; init; } = 100;

    public AnchorPoint Point { get; init; } = AnchorPoint.TOPLEFT;

    public string? RelativeTo { get; init; }

    public AnchorPoint RelativePoint { get; init; } = AnchorPoint.TOPLEFT;

    public double OffsetX { get; init; }

    public double OffsetY { get; init; }

    public bool Visible { get; init; } = true;

    public SizeReference? SizeReference { get; init; }

    public Stratum? Stratum { get; init; }

    public int? Level { get; init; }

    /// <summary>Effective size reference, defaulting to <see cref="Models.SizeReference.SCREEN"/>.</summary>
    public SizeReference SizeReferenceOrDefault => SizeReference ?? Models.SizeReference.SCREEN;

    /// <summary>Effective stratum, defaulting to <see cref="Models.Stratum.MEDIUM"/>.</summary>
    public Stratum StratumOrDefault => Stratum ?? Models.Stratum.MEDIUM;

    /// <summary>Effective level, defaulting to 0.</summary>
    public int LevelOrDefault => Level ?? 0;

    /// <summary>
    /// The frame whose bounds this one anchors against: <see cref="RelativeTo"/> when set,
    /// otherwise <see cref="Parent"/>. Null means the screen.
    /// </summary>
    public string? ReferenceName => RelativeTo ?? Parent;

    /// <summary>True when this frame anchors against the screen rather than another frame.</summary>
    public bool IsRoot => Parent is null;
}