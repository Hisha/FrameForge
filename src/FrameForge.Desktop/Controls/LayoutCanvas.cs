using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;

namespace FrameForge.Desktop.Controls;

/// <summary>
/// Draws and edits a FrameForge layout: the modelled UIParent, the frame rectangles, their
/// names, and the current selection.
/// </summary>
/// <remarks>
/// This control owns no project state. It is handed a resolved <see cref="LayoutResult"/>
/// and reports selection and drag intent back through events. That keeps all geometry in
/// FrameForge.Core, where the tests live, and leaves this class responsible only for
/// painting and hit testing.
/// <para>
/// Two coordinate systems meet here and must never be mixed:
/// <list type="bullet">
/// <item>Model space: WoW semantics, origin at the UIParent centre, +Y up.</item>
/// <item>Canvas space: Avalonia pixels, origin at the control's top-left, +Y down.</item>
/// </list>
/// The conversion happens exclusively through <see cref="Viewport"/>. Dragging converts a
/// canvas delta to a model delta and hands it back as an OFFSET change; the frame's anchors
/// are never rewritten to absolute coordinates.
/// </para>
/// </remarks>
public class LayoutCanvas : Control
{
    private static readonly IPen ScreenPen = new Pen(new SolidColorBrush(Color.Parse("#4C8DA8")), 1);
    private static readonly IBrush ScreenFill = new SolidColorBrush(Color.Parse("#14232C"));
    private static readonly IPen RootPen = new Pen(new SolidColorBrush(Color.Parse("#7FB2C9")), 1.5);
    private static readonly IPen ChildPen = new Pen(new SolidColorBrush(Color.Parse("#4E8AA8")), 1);
    private static readonly IPen HiddenPen = new Pen(new SolidColorBrush(Color.Parse("#8A5A5A")), 1, new DashStyle(new double[] { 3, 3 }, 0));
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.Parse("#F2C14E")), 2);
    private static readonly IPen IssuePen = new Pen(new SolidColorBrush(Color.Parse("#E06C6C")), 2);
    private static readonly IPen LabelBackground = new Pen(new SolidColorBrush(Color.Parse("#0B1014")), 1);
    private static readonly IBrush LabelForeground = new SolidColorBrush(Color.Parse("#DCE7EE"));
    private static readonly IBrush LabelForegroundSelected = new SolidColorBrush(Color.Parse("#101418"));
    private static readonly IBrush LabelForegroundHidden = new SolidColorBrush(Color.Parse("#8A5A5A"));
    private static readonly IBrush UnresolvedForeground = new SolidColorBrush(Color.Parse("#E06C6C"));
    private static readonly IBrush ChevronForeground = new SolidColorBrush(Color.Parse("#7FB2C9"));
    private static readonly IBrush LabelBackdrop = new SolidColorBrush(Color.Parse("#0B1014"));

    private readonly Typeface _labelTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private readonly Dictionary<string, CanvasBox> _screenBoxes = new(StringComparer.Ordinal);

    private LayoutResult? _layout;
    private Project? _project;
    private string? _selectedName;
    private bool _dragging;
    private string? _dragName;
    private double _dragLastCanvasX;
    private double _dragLastCanvasY;
    private Viewport _viewport = Viewport.Identity;

    /// <summary>Raised when the user clicks a frame (or empty space, with a null name).</summary>
    public event EventHandler<string?>? SelectionRequested;

    /// <summary>
    /// Raised while a frame is dragged. The delta is in MODEL units: dragging down the
    /// screen produces a negative Y.
    /// </summary>
    public event EventHandler<FrameDragEventArgs>? FrameDragged;

    /// <summary>Raised after any drag ends, so the view model can mark the project dirty.</summary>
    public event EventHandler? DragCompleted;

    /// <summary>Raised when the user requests a fit, usually by double-clicking empty space.</summary>
    public event EventHandler? FitRequested;

    /// <summary>The project being edited, used for the screen rect and per-frame lookups.</summary>
    public Project? Project
    {
        get => _project;
        set
        {
            _project = value;
            InvalidateVisual();
        }
    }

    /// <summary>The resolved layout to draw. Setting it resolves nothing; the model owns that.</summary>
    public LayoutResult? Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            InvalidateVisual();
        }
    }

    /// <summary>The selected frame name, or null.</summary>
    public string? SelectedName
    {
        get => _selectedName;
        set
        {
            if (_selectedName == value)
                return;
            _selectedName = value;
            InvalidateVisual();
        }
    }

    /// <summary>Current zoom and pan, in model units.</summary>
    public Viewport Viewport
    {
        get => _viewport;
        private set => _viewport = value;
    }

    /// <summary>Zoom as a percentage, for display.</summary>
    public double ZoomPercent => _viewport.Zoom * 100;

    /// <summary>Canvas point that the model origin maps to: the centre of this control.</summary>
    public CanvasOrigin Origin => new(Bounds.Width / 2, Bounds.Height / 2);

    /// <summary>Frames the screen rectangle and every drawn frame, with a margin.</summary>
    public void FitToContent()
    {
        if (Bounds.Width <= 1 || Bounds.Height <= 1 || _layout is null)
            return;

        const double margin = 40;
        var target = _layout.Bounds;
        if (target is null)
            target = LayoutResolver.ScreenRect(_project?.Screen ?? Screen.Default);

        var rect = target.Value;
        var zoomX = (Bounds.Width - margin * 2) / Math.Max(rect.Width, 1);
        var zoomY = (Bounds.Height - margin * 2) / Math.Max(rect.Height, 1);
        var zoom = Viewport.ClampZoom(Math.Min(zoomX, zoomY));

        _viewport = new Viewport(zoom, rect.CenterX, rect.CenterY);
        InvalidateVisual();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_layout is null)
            return;

        var origin = Origin;
        DrawBackground(context);
        DrawScreen(context, origin);

        foreach (var name in _layout.PaintOrder)
        {
            if (_layout.Rects.TryGetValue(name, out var rect))
                DrawFrame(context, origin, name, rect);
        }

        // Unresolved frames cannot be drawn in place, so they get a marker strip instead of
        // silently disappearing.
        var unresolved = _layout.Frames.Values.Where(f => f.Rect is null).Select(f => f.Name).ToArray();
        if (unresolved.Length > 0)
            DrawUnresolved(context, origin, unresolved);
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetPosition(this);
        var hit = HitTestFrame(point.X, point.Y);

        // Double-clicking empty canvas space is the shortcut for Fit.
        if (hit is null && e.ClickCount >= 2)
        {
            FitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        SelectionRequested?.Invoke(this, hit);

        if (hit is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            _dragName = hit;
            _dragLastCanvasX = point.X;
            _dragLastCanvasY = point.Y;
            e.Pointer.Capture(this);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_dragging || _dragName is null)
            return;

        var point = e.GetPosition(this);
        var (dx, dy) = _viewport.CanvasDeltaToModel(point.X - _dragLastCanvasX, point.Y - _dragLastCanvasY);

        _dragLastCanvasX = point.X;
        _dragLastCanvasY = point.Y;

        if (dx != 0 || dy != 0)
            FrameDragged?.Invoke(this, new FrameDragEventArgs(_dragName, dx, dy));
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_dragging)
            return;

        _dragging = false;
        _dragName = null;
        e.Pointer.Capture(null);
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var point = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        _viewport = _viewport.ZoomAt(_viewport.Zoom * factor, point.X, point.Y, Origin);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// Converts a canvas-pixel drag into a model-space delta, exactly as a real drag does.
    /// </summary>
    /// <remarks>
    /// Exposed so the smoke self-check can verify the sign convention against the live
    /// viewport: dragging DOWN the screen must produce a NEGATIVE model Y, because that is
    /// what decreases a frame's WoW <c>offsetY</c>.
    /// </remarks>
    public (double Dx, double Dy) ModelDeltaForDrag(double canvasDx, double canvasDy) =>
        _viewport.CanvasDeltaToModel(canvasDx, canvasDy);

    /// <summary>Topmost frame whose rectangle contains the canvas point, or null.</summary>
    public string? HitTestFrame(double canvasX, double canvasY)
    {
        if (_layout is null)
            return null;

        var origin = Origin;

        // Reverse paint order so the visually topmost frame wins, which matters for the
        // deliberately overlapping Native Hunts panels.
        for (var i = _layout.PaintOrder.Count - 1; i >= 0; i--)
        {
            var name = _layout.PaintOrder[i];
            if (!_layout.Rects.TryGetValue(name, out var rect))
                continue;

            if (_viewport.RectToCanvas(rect, origin).Contains(canvasX, canvasY))
                return name;
        }

        return null;
    }

    private void DrawBackground(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0C1116")), new Rect(Bounds.Size));
    }

    private void DrawScreen(DrawingContext context, CanvasOrigin origin)
    {
        var screen = LayoutResolver.ScreenRect(_project?.Screen ?? Screen.Default);
        var box = _viewport.RectToCanvas(screen, origin);
        _screenBoxes["UIParent"] = box;

        context.FillRectangle(ScreenFill, new Rect(box.X, box.Y, box.Width, box.Height));
        context.DrawRectangle(null, ScreenPen, new Rect(box.X, box.Y, box.Width, box.Height));

        DrawLabel(context, "UIParent", new Point(box.X + 6, box.Y + 4), ChevronForeground);
    }

    private void DrawFrame(DrawingContext context, CanvasOrigin origin, string name, FrameRect rect)
    {
        var box = _viewport.RectToCanvas(rect, origin);
        var canvasRect = new Rect(box.X, box.Y, box.Width, box.Height);
        var detail = _layout!.Frames[name];
        var selected = name == _selectedName;
        var problems = _project?.Find(name);

        var pen = selected ? SelectedPen
            : problems?.Parent is null ? RootPen
            : !detail.EffectiveVisible ? HiddenPen
            : ChildPen;

        context.DrawRectangle(null, pen, canvasRect);
        DrawLabel(context, name, new Point(box.X + 5, box.Y + 4), selected ? LabelForegroundSelected : LabelForeground);

        if (selected)
        {
            // Draw the anchor marker: the exact point of THIS frame that is placed.
            var own = LayoutResolver.AnchorPosition(rect, problems?.Point ?? AnchorPoint.TOPLEFT);
            var marker = _viewport.ModelToCanvas(own, origin);
            context.DrawEllipse(null, SelectedPen, new Point(marker.X, marker.Y), 3, 3);
        }
    }

    private void DrawUnresolved(DrawingContext context, CanvasOrigin origin, IReadOnlyList<string> names)
    {
        var y = 24d;
        context.DrawRectangle(null, IssuePen, new Rect(12, y - 12, Bounds.Width - 24, 18 * names.Count + 8));
        foreach (var name in names)
            DrawLabel(context, $"{name} - could not be resolved", new Point(18, y), UnresolvedForeground);
    }

    private void DrawLabel(DrawingContext context, string text, Point origin, IBrush foreground)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _labelTypeface,
            11,
            foreground);

        var box = new Rect(origin.X, origin.Y, formatted.Width + 6, formatted.Height);
        context.FillRectangle(LabelBackdrop, box);
        context.DrawRectangle(null, LabelBackground, box);
        context.DrawText(formatted, origin);
    }
}

/// <summary>A drag of one frame, expressed as a model-space delta.</summary>
/// <param name="FrameName">The frame being dragged.</param>
/// <param name="DeltaX">Model X delta. Dragging right is positive.</param>
/// <param name="DeltaY">Model Y delta. Dragging DOWN the screen is negative.</param>
public sealed record FrameDragEventArgs(string FrameName, double DeltaX, double DeltaY);