namespace FrameForge.Core.Geometry;

/// <summary>
/// Axis-aligned rectangle in MODEL space (WoW semantics: +Y up).
/// </summary>
/// <remarks>
/// <see cref="Top"/> is always the numerically greater Y and <see cref="Bottom"/> the
/// smaller one, so <see cref="Top"/> - <see cref="Bottom"/> is a positive height. Build
/// rectangles through <see cref="FromSize"/> or <see cref="FromEdges"/> so this invariant
/// cannot be violated by hand-written code.
/// <para>
/// This type never holds Avalonia coordinates. The conversion to screen pixels lives
/// in <see cref="Viewport"/>.
/// </para>
/// </remarks>
/// <param name="Left">Smallest X.</param>
/// <param name="Top">Largest Y.</param>
/// <param name="Right">Largest X.</param>
/// <param name="Bottom">Smallest Y.</param>
public readonly record struct FrameRect(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Rectangles from a corner plus a size. <paramref name="height"/> extends DOWN.</summary>
    public static FrameRect FromSize(double left, double top, double width, double height) =>
        FromEdges(left, top, left + width, top - height);

    /// <summary>Rectangles from raw edges, normalized so the top/height invariant holds.</summary>
    public static FrameRect FromEdges(double left, double top, double right, double bottom) => new(
        Math.Min(left, right),
        Math.Max(top, bottom),
        Math.Max(left, right),
        Math.Min(top, bottom));

    /// <summary>Width; never negative.</summary>
    public double Width => Right - Left;

    /// <summary>Height; never negative.</summary>
    public double Height => Top - Bottom;

    public double CenterX => (Left + Right) / 2;

    public double CenterY => (Top + Bottom) / 2;

    /// <summary>Centre of the rectangle as a model-space point.</summary>
    public ModelPoint Center => new(CenterX, CenterY);

    /// <summary>True when the point lies inside the rectangle, edges included.</summary>
    public bool Contains(double x, double y) => x >= Left && x <= Right && y <= Top && y >= Bottom;

    /// <summary>True when <paramref name="point"/> lies inside the rectangle, edges included.</summary>
    public bool Contains(ModelPoint point) => Contains(point.X, point.Y);

    /// <summary>True when <paramref name="inner"/> lies entirely within this rectangle.</summary>
    public bool Contains(FrameRect inner) =>
        inner.Left >= Left && inner.Right <= Right && inner.Top <= Top && inner.Bottom >= Bottom;

    /// <summary>Moves the rectangle without changing its size.</summary>
    public FrameRect Translate(double dx, double dy) =>
        new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    /// <inheritdoc />
    public override string ToString() => $"[{Left}, {Top} .. {Right}, {Bottom}]";
}