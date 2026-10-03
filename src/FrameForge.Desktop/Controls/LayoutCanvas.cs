using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Rendering;
using FrameForge.Desktop.Assets;

namespace FrameForge.Desktop.Controls;

/// <summary>Draws and edits a FrameForge layout, in one of three view modes.</summary>
/// <remarks>
/// <para>
/// This control owns no project state and no drawing policy. It is handed a resolved
/// <see cref="LayoutResult"/>, a view mode, a visibility filter and a label policy, and reports
/// selection and drag intent back through events. All geometry lives in FrameForge.Core and all
/// "should this be drawn" decisions live in <see cref="ViewPolicy"/>, so both are testable without
/// a window. What remains here is input handling, the model/canvas conversion, and the handing of
/// work to <see cref="RenderPipeline"/>.
/// </para>
/// <para>
/// Two coordinate systems meet here and must never be mixed:
/// <list type="bullet">
/// <item>Model space: WoW semantics, origin at the UIParent centre, +Y up.</item>
/// <item>Canvas space: Avalonia pixels, origin at the control's top-left, +Y down.</item>
/// </list>
/// The conversion happens exclusively through <see cref="Viewport"/>. Dragging converts a canvas
/// delta to a model delta and hands it back as an OFFSET change; a frame's anchors are never
/// rewritten to absolute coordinates.
/// </para>
/// <para>
/// THREE BANDS, NOT ONE. The canvas draws a visual-content band, a debug wireframe and a selection
/// overlay, in that order, through <see cref="RenderPipeline"/>. The mode decides which bands are
/// active, which is why Debug, Preview and Hybrid are genuinely different views rather than the same
/// picture with a few things switched off.
/// </para>
/// </remarks>
public class LayoutCanvas : Control
{
    private static readonly IBrush CanvasFill = new SolidColorBrush(Color.Parse("#0C1116"));

    private LayoutResult? _layout;
    private Project? _project;
    private string? _selectedName;
    private bool _dragging;
    private string? _dragName;
    private double _dragLastCanvasX;
    private double _dragLastCanvasY;
    private Viewport _viewport = Viewport.Identity;

    private CanvasViewMode _mode = CanvasViewMode.DEBUG;
    private VisibilityFilter _filter = ViewPolicy.DefaultsFor(CanvasViewMode.DEBUG);
    private LabelPolicy _labels = ViewPolicy.DefaultLabelPolicyFor(CanvasViewMode.DEBUG);
    private RenderPipeline _pipeline = RenderPipeline.Default();
    private ITextureAssetResolver? _assetResolver;

    /// <summary>The completed render pass's policy, drawable, layer, and visual-paint counts.</summary>
    public CanvasRenderTrace? LastRenderTrace { get; private set; }

    /// <summary>Raised with the frame the user clicked, or null for empty canvas.</summary>
    public event EventHandler<string?>? SelectionRequested;

    /// <summary>Raised when a drag moves a frame, as a model-space delta.</summary>
    public event EventHandler<FrameDragEventArgs>? FrameDragged;

    /// <summary>Raised when a drag ends.</summary>
    public event EventHandler? DragCompleted;

    /// <summary>Raised when the user asks to fit the content to the control.</summary>
    public event EventHandler? FitRequested;

    /// <summary>
    /// Creates the canvas with rendering clipped to its own bounds.
    /// </summary>
    /// <remarks>
    /// A custom <see cref="Render"/> draws in whatever canvas coordinates the model maps to, and
    /// those are NOT confined to the control: at any zoom other than a perfect fit, and for every
    /// widget outside the framed box, model positions land outside <see cref="Bounds"/> in both
    /// directions. Without a clip that ink is drawn straight over the window's other panes.
    /// <para>
    /// <see cref="Visual.ClipToBounds"/> is the mechanism here, rather than per-draw clamps,
    /// because it contains the whole subtree: rectangles, the UIParent label, widget names,
    /// zero-area markers, anchor connectors, the Preview stand-ins and the selection decorations
    /// are all clipped without a single per-draw bounds check - and with one clip in place,
    /// whatever a future layer draws is clipped too.
    /// </para>
    /// <para>
    /// This also fixes a Phase 2 defect. The zero-area initializer handler is deliberate and
    /// unchanged; the clip is what stops the overflow.
    /// </para>
    /// </remarks>
    public LayoutCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>The project being viewed. Setting it resolves nothing; the model owns that.</summary>
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

    /// <summary>
    /// The active view mode.
    /// </summary>
    /// <remarks>
    /// Switching mode resets the visibility filter and the label policy to that mode's defaults,
    /// because a mode's defaults are the point of it. Leaving the user's toggles in place across
    /// the switch would let Preview arrive with Debug's everything-on filter and no labels
    /// suppressed, which is neither mode.
    /// </remarks>
    public CanvasViewMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;

            _mode = value;
            _filter = ViewPolicy.DefaultsFor(value);
            _labels = ViewPolicy.DefaultLabelPolicyFor(value);
            InvalidateVisual();
        }
    }

    /// <summary>Which categories of widget the canvas draws. Viewport visibility only.</summary>
    public VisibilityFilter Filter
    {
        get => _filter;
        set
        {
            if (_filter == value)
                return;
            _filter = value;
            InvalidateVisual();
        }
    }

    /// <summary>Which widgets get a name label.</summary>
    public LabelPolicy Labels
    {
        get => _labels;
        set
        {
            if (_labels == value)
                return;
            _labels = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// The layers the canvas draws through.
    /// </summary>
    /// <remarks>
    /// Settable so the smoke self-check can run the real pipeline against a recording context and
    /// assert that Preview really does skip the debug band.
    /// </remarks>
    public RenderPipeline Pipeline
    {
        get => _pipeline;
        set
        {
            _pipeline = value;
            InvalidateVisual();
        }
    }

    /// <summary>The desktop-side service used by the visual layer to locate and decode artwork.</summary>
    public ITextureAssetResolver? AssetResolver
    {
        get => _assetResolver;
        set
        {
            _assetResolver = value;
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

    /// <summary>Frames the laid-out widgets in the view, with a margin.</summary>
    /// <remarks>
    /// This deliberately does NOT use <see cref="FrameLayout.Bounds"/>. That value is the union of
    /// every resolved frame, and a zero-area frame is a legitimate member of that union: WoW's
    /// <c>NativeHuntsFrameInitializer</c> is a widget with no size, parked at the UIParent corner,
    /// and including it would triple the width and height of the box to fit. Worse, it would be
    /// fitting to something the user cannot see.
    /// <para>
    /// So the fit box is built from frames that occupy real area, and the full bounds are only
    /// used when nothing does. The engine keeps reporting the honest union either way; the choice
    /// of what to show is a view decision and lives here.
    /// </para>
    /// </remarks>
    public void FitToContent()
    {
        if (Bounds.Width <= 1 || Bounds.Height <= 1 || _layout is null)
            return;

        const double margin = 40;
        var rect = FitBox(_layout) ?? LayoutResolver.ScreenRect(_project?.Screen ?? Screen.Default);
        var zoomX = (Bounds.Width - margin * 2) / Math.Max(rect.Width, 1);
        var zoomY = (Bounds.Height - margin * 2) / Math.Max(rect.Height, 1);
        var zoom = Viewport.ClampZoom(Math.Min(zoomX, zoomY));

        _viewport = new Viewport(zoom, rect.CenterX, rect.CenterY);
        InvalidateVisual();
    }

    /// <summary>Centers the current selection without changing the user's zoom.</summary>
    /// <returns>True when the selection had resolved bounds and was centered.</returns>
    public bool RevealSelection()
    {
        if (_selectedName is null || _layout is null
            || !_layout.Frames.TryGetValue(_selectedName, out var detail)
            || detail.Rect is not { } rect)
            return false;

        _viewport = new Viewport(_viewport.Zoom, rect.CenterX, rect.CenterY);
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// The box to fit: the union of every frame that occupies real area, or null if there is none.
    /// </summary>
    private static FrameRect? FitBox(LayoutResult layout)
    {
        FrameRect? box = null;

        foreach (var frame in layout.Frames.Values)
        {
            if (frame.Rect is not { } rect || rect.Width <= 0 || rect.Height <= 0)
                continue;

            if (box is not { } current)
            {
                box = rect;
                continue;
            }

            // Top is the LARGER model Y: +Y is up, and FrameRect keeps Top >= Bottom.
            box = new FrameRect(
                Math.Min(current.Left, rect.Left),
                Math.Max(current.Top, rect.Top),
                Math.Max(current.Right, rect.Right),
                Math.Min(current.Bottom, rect.Bottom));
        }

        return box;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        context.FillRectangle(CanvasFill, new Rect(Bounds.Size));

        if (_layout is null)
            return;

        var canvas = BuildContext();
        var layers = _pipeline.For(_mode);
        foreach (var layer in layers)
            layer.Render(context, canvas);

        LastRenderTrace = canvas.Diagnostics.Snapshot(canvas, [.. layers.Select(layer => layer.Name)]);
    }

    /// <summary>
    /// Resolves the per-frame decisions the layers draw from.
    /// </summary>
    /// <remarks>
    /// Built once per pass and shared, so every layer sees the same answers. This is where policy
    /// becomes geometry: which widgets pass the filter, whether each gets a label, and where each
    /// lands on the canvas.
    /// </remarks>
    private CanvasRenderContext BuildContext()
    {
        var origin = Origin;
        var layout = _layout!;
        var drawable = new List<DrawableFrame>(layout.PaintOrder.Count);
        var diagnostics = new CanvasRenderDiagnostics();

        foreach (var name in layout.PaintOrder)
        {
            if (!layout.Frames.TryGetValue(name, out var detail))
                continue;

            var model = _project?.Find(name);
            if (model is null)
                continue;

            var effectiveVisible = detail.EffectiveVisible;
            var selected = name == _selectedName;
            var policyVisible = ViewPolicy.IsVisible(model, effectiveVisible, _filter);

            if (policyVisible)
                diagnostics.Accept(model.Kind);

            if (!policyVisible &&
                !(selected && ViewPolicy.ShowsSelectionGeometry(_mode)))
            {
                continue;
            }

            drawable.Add(new DrawableFrame(
                name,
                model,
                detail.Rect,
                detail.Rect is { } rect ? _viewport.RectToCanvas(rect, origin) : null,
                effectiveVisible,
                selected,
                detail.Rect is { Width: > 0 and not double.MaxValue, Height: > 0 } sized && sized.Width > 0 && sized.Height > 0,
                ViewPolicy.ShouldDrawLabel(model, selected, _labels, _filter, effectiveVisible)));
        }

        return new CanvasRenderContext(
            _project,
            layout,
            _viewport,
            origin,
            Bounds.Size,
            _mode,
            _filter,
            _labels,
            _selectedName,
            drawable,
            diagnostics,
            _assetResolver);
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
        var hits = HitTestCandidates(point.X, point.Y);

        // Double-clicking empty canvas space is the shortcut for Fit.
        if (hits.Count == 0 && e.ClickCount >= 2)
        {
            FitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Every widget under the point is offered, front to back, because the overlapping Native
        // Hunts panels make a single answer ambiguous. A repeat click on the same spot steps back
        // through the stack instead of silently settling for whatever happened to be on top.
        var hit = CycleHit(hits, point.X, point.Y);

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

    /// <summary>
    /// Every widget under a canvas point, front to back.
    /// </summary>
    public IReadOnlyList<string> HitTestCandidates(double canvasX, double canvasY)
    {
        if (_project is null || _layout is null)
            return [];

        return HitTester.CandidatesAt(_project!, _layout, _viewport, Origin, _filter, canvasX, canvasY);
    }

    /// <summary>The single widget a click at this point should select, or null.</summary>
    public string? HitTestFrame(double canvasX, double canvasY) =>
        HitTestCandidates(canvasX, canvasY).FirstOrDefault();

    /// <summary>
    /// Returns the candidate a repeated click should select, cycling front-to-back at one point.
    /// </summary>
    public string? CycleHitTestFrame(double canvasX, double canvasY) =>
        CycleHit(HitTestCandidates(canvasX, canvasY), canvasX, canvasY);

    /// <summary>
    /// Chooses between overlapping candidates by cycling on repeat clicks.
    /// </summary>
    /// <remarks>
    /// When only one widget is under the point the answer is that widget and no state changes. When
    /// several are, and the current selection is one of them, the click advances to the next one
    /// behind it - which is how a user reaches the Identity panel that Idle is sitting on top of.
    /// A click somewhere else starts again from the front of the stack, so the cycling can never
    /// get stuck where the pointer is not.
    /// </remarks>
    private string? CycleHit(IReadOnlyList<string> hits, double canvasX, double canvasY)
    {
        if (hits.Count == 0)
        {
            _lastHit = null;
            return null;
        }

        var signature = string.Join('\u001f', hits);
        var repeated = _lastHit is { } previous
            && previous.Signature == signature
            && Math.Abs(previous.X - canvasX) <= 3
            && Math.Abs(previous.Y - canvasY) <= 3;

        _lastHit = (canvasX, canvasY, signature);
        if (!repeated)
        {
            return hits[0];
        }

        var current = _selectedName is null ? -1 : hits.ToList().IndexOf(_selectedName);
        return hits[(current + 1) % hits.Count];
    }

    private (double X, double Y, string Signature)? _lastHit;

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
}

/// <summary>A drag of one frame, expressed as a model-space delta.</summary>
/// <param name="FrameName">The frame being dragged.</param>
/// <param name="DeltaX">Model X delta. Dragging right is positive.</param>
/// <param name="DeltaY">Model Y delta. Dragging DOWN the screen is negative.</param>
public sealed record FrameDragEventArgs(string FrameName, double DeltaX, double DeltaY);
