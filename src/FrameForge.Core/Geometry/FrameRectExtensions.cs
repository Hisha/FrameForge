namespace FrameForge.Core.Geometry;

/// <summary>
/// A point in MODEL space (WoW semantics: +Y up). Deliberately a distinct type from
/// Avalonia's <c>Point</c> so the Y-down convention cannot be applied by accident.
/// </summary>
/// <param name="X">Horizontal position; positive is right.</param>
/// <param name="Y">Vertical position; positive is UP.</param>
public readonly record struct ModelPoint(double X, double Y)
{
    /// <summary>The model origin, which is the centre of the screen (WoW UIParent 0,0).</summary>
    public static ModelPoint Origin => new(0, 0);

    /// <summary>Adds a delta.</summary>
    public static ModelPoint operator +(ModelPoint p, ModelPoint delta) => new(p.X + delta.X, p.Y + delta.Y);

    /// <inheritdoc />
    public override string ToString() => $"({X}, {Y})";
}

/// <summary>
/// Rectangle <c>Union</c>, i.e. the smallest rectangle containing both inputs.
/// </summary>
/// <remarks>
/// This has to be a true union on all four edges. A shortcut that pairs the first
/// rectangle's top-left with the second's bottom-right silently SHRINKS the result when
/// one rectangle is nested inside the other, which is exactly the shape of a typical
/// frame layout (a child inside its parent). FrameForge v0.1 shipped that bug in the
/// Electron implementation and it was caught by a nested-rectangle test; keep
/// GeometryTests.UnionCoversBothRectsangles.
/// </remarks>
/// <param name="A">First rectangle.</param>
/// <param name="B">Second rectangle.</param>
public static class FrameRectExtensions
{
    public static FrameRect Union(this FrameRect a, FrameRect b) => FrameRect.FromEdges(
        Math.Min(a.Left, b.Left),
        Math.Max(a.Top, b.Top),
        Math.Max(a.Right, b.Right),
        Math.Min(a.Bottom, b.Bottom));
}