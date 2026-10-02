namespace FrameForge.Core.Geometry;

/// <summary>
/// A box in CANVAS space (Avalonia pixel coordinates, +Y DOWN).
/// </summary>
/// <remarks>
/// Deliberately a different type from the model-space <see cref="FrameRect"/> and
/// <see cref="ModelPoint"/>, so a Y-down value cannot be handed to geometry code (or the
/// other way round) by accident.
/// </remarks>
/// <param name="X">Left edge in canvas pixels.</param>
/// <param name="Y">Top edge in canvas pixels; larger means further down.</param>
/// <param name="Width">Width in canvas pixels.</param>
/// <param name="Height">Height in canvas pixels; always positive.</param>
public readonly record struct CanvasBox(double X, double Y, double Width, double Height)
{
    /// <summary>Right edge in canvas pixels.</summary>
    public double Right => X + Width;

    /// <summary>Bottom edge in canvas pixels.</summary>
    public double Bottom => Y + Height;

    /// <summary>True when the canvas point lies inside the box.</summary>
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

/// <summary>
/// The origin of the canvas element in its own pixel space: the point model space is
/// measured from.
/// </summary>
/// <param name="X">Horizontal pixel offset of the model origin.</param>
/// <param name="Y">Vertical pixel offset of the model origin.</param>
public readonly record struct CanvasOrigin(double X, double Y);

/// <summary>
/// The one and only place where WoW model space is turned into Avalonia canvas pixels.
/// </summary>
/// <remarks>
/// <code>
///   MODEL (WoW)                       CANVAS (Avalonia)
///   origin = UIParent centre          origin = the canvas control's top-left
///   +x right, +y UP                   +x right, +y DOWN
///
///   canvasX = originX + (modelX - panX) * zoom
///   canvasY = originY - (modelY - panY) * zoom
/// </code>
/// <c>Zoom</c> is model units to canvas pixels. That Y sign flip is the ONLY difference
/// between the two systems. Nothing else is allowed to assume a Y-down orientation, and
/// nothing in the saved project format is ever derived from canvas coordinates.
/// </remarks>
/// <param name="Zoom">Model units to canvas pixels.</param>
/// <param name="PanX">Model-space X pinned to the canvas origin.</param>
/// <param name="PanY">Model-space Y pinned to the canvas origin.</param>
public readonly record struct Viewport(double Zoom, double PanX, double PanY)
{
    /// <summary>1:1 model units to canvas pixels, no pan.</summary>
    public static Viewport Identity => new(1, 0, 0);

    /// <summary>Smallest and largest zoom the editor allows.</summary>
    public const double MinZoom = 0.05;

    /// <summary>Upper zoom bound.</summary>
    public const double MaxZoom = 8;

    public double ModelToCanvasX(double modelX, CanvasOrigin origin) => origin.X + (modelX - PanX) * Zoom;

    public double ModelToCanvasY(double modelY, CanvasOrigin origin) => origin.Y - (modelY - PanY) * Zoom;

    public ModelPoint ModelToCanvas(ModelPoint model, CanvasOrigin origin) =>
        new(ModelToCanvasX(model.X, origin), ModelToCanvasY(model.Y, origin));

    /// <summary>Screen-space canvas point to model space.</summary>
    public ModelPoint CanvasToModel(double canvasX, double canvasY, CanvasOrigin origin) =>
        new((canvasX - origin.X) / Zoom + PanX, (origin.Y - canvasY) / Zoom + PanY);

    /// <summary>
    /// Converts a canvas-space drag delta into a model-space delta.
    /// </summary>
    /// <remarks>
    /// This is what makes dragging feel right in WoW terms: dragging DOWN the screen
    /// produces a NEGATIVE model Y delta, so a frame dragged downwards gets a smaller
    /// <c>offsetY</c>. Screen y grows downward, WoW y grows upward.
    /// </remarks>
    public (double X, double Y) CanvasDeltaToModel(double dx, double dy) => (dx / Zoom, -dy / Zoom);

    /// <summary>Converts a model-space rectangle into a drawable canvas box.</summary>
    public CanvasBox RectToCanvas(FrameRect rect, CanvasOrigin origin) => new(
        ModelToCanvasX(rect.Left, origin),
        ModelToCanvasY(rect.Top, origin),
        rect.Width * Zoom,
        rect.Height * Zoom);

    /// <summary>Clamps a zoom value into the editor's supported range.</summary>
    public static double ClampZoom(double zoom) => double.IsFinite(zoom)
        ? Math.Clamp(zoom, MinZoom, MaxZoom)
        : 1;

    /// <summary>
    /// Zooms about a fixed canvas point so the model position under the pointer stays put.
    /// </summary>
    /// <remarks>
    /// Solving <c>newPan = oldModelPoint - canvasOffset / newZoom</c> for the pan term is what
    /// keeps the layout from sliding away from the cursor while the wheel is spinning.
    /// </remarks>
    public Viewport ZoomAt(double nextZoom, double canvasX, double canvasY, CanvasOrigin origin)
    {
        var zoom = ClampZoom(nextZoom);
        var before = CanvasToModel(canvasX, canvasY, origin);
        return new Viewport(
            zoom,
            PanX: before.X - (canvasX - origin.X) / zoom,
            PanY: before.Y - (origin.Y - canvasY) / zoom);
    }
}