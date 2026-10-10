using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Rendering;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Preview;
using FrameForge.Desktop.Inspection;
using FrameForge.Desktop.Templates;
using FrameForge.Core.Templates;
using FrameForge.Core.Semantics.V2;

namespace FrameForge.Desktop.Controls;

/// <summary>What a canvas click asked for.</summary>
/// <param name="FrameName">The frame under the pointer, or null for empty canvas.</param>
/// <param name="Additive">
/// True when Ctrl/Cmd/Shift was held, meaning "add this to the selection" or "take it out again"
/// rather than "select only this". The canvas reports the key state and nothing more: deciding
/// what "additive" means to a selection is the view model's business.
/// </param>
public sealed record CanvasSelectionEventArgs(string? FrameName, bool Additive)
{
    public SemanticId? SemanticId => FrameName is not null && Guid.TryParseExact(FrameName, "D", out _)
        ? new SemanticId(FrameName) : null;
}

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
    // A LayoutCanvas is exercised by multiple independent headless dispatchers in one test
    // process. Shared AvaloniaObject resources such as SolidColorBrush acquire dispatcher
    // ownership when composed, so a later session cannot reuse them. This fill is constant and
    // intentionally shared; keep it dispatcher-neutral by using Avalonia's immutable brush.
    private static readonly IImmutableBrush CanvasFill =
        new ImmutableSolidColorBrush(Color.Parse("#0C1116"));
    private static readonly IImmutableBrush GridBrush = new ImmutableSolidColorBrush(Color.Parse("#24313A"));
    private static readonly IImmutableBrush HandleFill = new ImmutableSolidColorBrush(Color.Parse("#F2C14E"));
    private static readonly IImmutableBrush HandleActiveFill = new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IPen HandlePen = new ImmutablePen(
        new ImmutableSolidColorBrush(Color.Parse("#172129")), 1);

    private LayoutResult? _layout;
    private Project? _project;
    private ResolvedUiLayout? _v2Layout;
    private string? _selectedName;
    private IReadOnlyList<string> _selectedNames = [];
    private bool _dragging;
    private bool _resizing;
    private IPointer? _dragPointer;
    private string? _dragName;
    private V2ResizeHandle? _resizeHandle;
    private V2ResizeHandle? _hoverResizeHandle;
    private double _dragLastCanvasX;
    private double _dragLastCanvasY;
    private Viewport _viewport = Viewport.Identity;

    private CanvasViewMode _mode = CanvasViewMode.DEBUG;
    private VisibilityFilter _filter = ViewPolicy.DefaultsFor(CanvasViewMode.DEBUG);
    private LabelPolicy _labels = ViewPolicy.DefaultLabelPolicyFor(CanvasViewMode.DEBUG);
    private RenderPipeline _pipeline = RenderPipeline.Default();
    private ITextureAssetResolver? _assetResolver;
    private IStockTemplateResolver? _stockTemplates;
    private PreviewOverrideSet? _previewOverrides;
    private BlizzardTemplateRegistry? _v2Templates;
    private PreviewButtonState _v2ButtonState;
    private IReadOnlySet<string> _hiddenByOrigin = new HashSet<string>();
    private IReadOnlySet<string> _lockedNames = new HashSet<string>();
    private IReadOnlySet<string> _preferredSelectionNames = new HashSet<string>();
    private bool _showGrid;
    private double _gridSize = 8;

    /// <summary>The completed render pass's policy, drawable, layer, and visual-paint counts.</summary>
    public CanvasRenderTrace? LastRenderTrace { get; private set; }

    /// <summary>Raised with the frame the user clicked, or null for empty canvas.</summary>
    public event EventHandler<CanvasSelectionEventArgs>? SelectionRequested;

    /// <summary>Raised when a drag moves a frame, as a model-space delta.</summary>
    public event EventHandler<FrameDragEventArgs>? FrameDragged;

    /// <summary>Raised when a drag ends.</summary>
    public event EventHandler? DragCompleted;

    /// <summary>Raised when an in-progress drag is cancelled before commit.</summary>
    public event EventHandler? DragCancelled;

    public event EventHandler<FrameResizeEventArgs>? FrameResized;
    public event EventHandler? ResizeCompleted;
    public event EventHandler? ResizeCancelled;

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

    /// <summary>The native schema-v2 layout. When set, it is the canvas source of truth.</summary>
    public ResolvedUiLayout? V2Layout
    {
        get => _v2Layout;
        set { _v2Layout = value; InvalidateVisual(); }
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
    /// The whole selection in click order, primary last; null or empty means nothing is selected.
    /// </summary>
    /// <remarks>
    /// The primary is kept as its own property because <see cref="SelectedName"/> is what every
    /// other part of the window already binds to. <see cref="SelectedNames"/> is what the selection
    /// chrome draws, so a four-object selection can be outlined as four objects.
    /// </remarks>
    public IReadOnlyList<string> SelectedNames
    {
        get => _selectedNames;
        set
        {
            var next = value ?? [];
            if (_selectedNames.SequenceEqual(next, StringComparer.Ordinal))
                return;
            _selectedNames = next;
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

    /// <summary>Machine-local effective stock font/template presentation.</summary>
    public IStockTemplateResolver? StockTemplates
    {
        get => _stockTemplates;
        set
        {
            _stockTemplates = value;
            InvalidateVisual();
        }
    }

    /// <summary>Active non-destructive design-time values and button state.</summary>
    public PreviewOverrideSet? PreviewOverrides
    {
        get => _previewOverrides;
        set
        {
            _previewOverrides = value;
            InvalidateVisual();
        }
    }

    public BlizzardTemplateRegistry? V2Templates
    {
        get => _v2Templates;
        set { _v2Templates = value; InvalidateVisual(); }
    }

    public PreviewButtonState V2ButtonState
    {
        get => _v2ButtonState;
        set { _v2ButtonState = value; InvalidateVisual(); }
    }

    /// <summary>Presentation-only exclusions by origin; descendants are evaluated independently.</summary>
    public IReadOnlySet<string> HiddenByOrigin
    {
        get => _hiddenByOrigin;
        set { _hiddenByOrigin = value; InvalidateVisual(); }
    }

    public IReadOnlySet<string> LockedNames
    {
        get => _lockedNames;
        set { _lockedNames = value; InvalidateVisual(); }
    }

    public IReadOnlySet<string> PreferredSelectionNames
    {
        get => _preferredSelectionNames;
        set => _preferredSelectionNames = value;
    }

    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; InvalidateVisual(); }
    }

    public double GridSize
    {
        get => _gridSize;
        set { _gridSize = double.IsFinite(value) ? Math.Clamp(value, 1, 256) : 8; InvalidateVisual(); }
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
        if (Bounds.Width <= 1 || Bounds.Height <= 1 || (_layout is null && _v2Layout is null))
            return;

        const double margin = 40;
        var rect = _v2Layout?.Bounds ?? (_layout is null ? null : FitBox(_layout))
            ?? _v2Layout?.HostRect ?? LayoutResolver.ScreenRect(_project?.Screen ?? Screen.Default);
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
        FrameRect? selectedRect = null;
        if (_selectedName is not null && _v2Layout is { } v2 && Guid.TryParseExact(_selectedName, "D", out _)
            && v2.Elements.TryGetValue(new SemanticId(_selectedName), out var native))
            selectedRect = native.Rect;
        else if (_selectedName is not null && _layout is { } legacy
                 && legacy.Frames.TryGetValue(_selectedName, out var detail))
            selectedRect = detail.Rect;
        if (selectedRect is not { } rect)
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

        if (_layout is null && _v2Layout is null)
            return;

        if (_showGrid && _v2Layout is not null && _mode != CanvasViewMode.PREVIEW) DrawGrid(context);

        var canvas = BuildContext();
        var layers = _pipeline.For(_mode);
        foreach (var layer in layers)
            layer.Render(context, canvas);

        if (_mode != CanvasViewMode.PREVIEW) DrawResizeHandles(context);

        LastRenderTrace = canvas.Diagnostics.Snapshot(canvas, [.. layers.Select(layer => layer.Name)]);
    }

    private void DrawGrid(DrawingContext context)
    {
        var spacing = _gridSize * _viewport.Zoom;
        if (spacing < 4) return;
        var originX = _viewport.ModelToCanvasX(0, Origin);
        var originY = _viewport.ModelToCanvasY(0, Origin);
        var startX = originX % spacing;
        if (startX < 0) startX += spacing;
        var startY = originY % spacing;
        if (startY < 0) startY += spacing;
        var pen = new Pen(GridBrush, 1);
        for (var x = startX; x <= Bounds.Width; x += spacing) context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
        for (var y = startY; y <= Bounds.Height; y += spacing) context.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
    }

    private void DrawResizeHandles(DrawingContext context)
    {
        foreach (var (handle, rect) in GetV2ResizeHandles())
            context.DrawRectangle(handle == _resizeHandle || handle == _hoverResizeHandle ? HandleActiveFill : HandleFill,
                HandlePen, rect, 1, 1);
    }

    public IReadOnlyDictionary<V2ResizeHandle, Rect> GetV2ResizeHandles()
    {
        if (_mode == CanvasViewMode.PREVIEW || _v2Layout is null || _selectedName is null || _selectedNames.Count != 1 ||
            !Guid.TryParseExact(_selectedName, "D", out _) || _lockedNames.Contains(_selectedName)) return new Dictionary<V2ResizeHandle, Rect>();
        var id = new SemanticId(_selectedName);
        if (!_v2Layout.Elements.TryGetValue(id, out var element) || element.Rect is not { } model ||
            element.Node?.Kind is not (UiNodeKind.Frame or UiNodeKind.Texture or UiNodeKind.Button or UiNodeKind.StatusBar))
            return new Dictionary<V2ResizeHandle, Rect>();
        var box = _viewport.RectToCanvas(model, Origin);
        const double size = 9;
        Rect At(double x, double y) => new(x - size / 2, y - size / 2, size, size);
        var cx = box.X + box.Width / 2;
        var cy = box.Y + box.Height / 2;
        return new Dictionary<V2ResizeHandle, Rect>
        {
            [V2ResizeHandle.Left] = At(box.X, cy), [V2ResizeHandle.Right] = At(box.Right, cy),
            [V2ResizeHandle.Top] = At(cx, box.Y), [V2ResizeHandle.Bottom] = At(cx, box.Bottom),
            [V2ResizeHandle.TopLeft] = At(box.X, box.Y), [V2ResizeHandle.TopRight] = At(box.Right, box.Y),
            [V2ResizeHandle.BottomLeft] = At(box.X, box.Bottom), [V2ResizeHandle.BottomRight] = At(box.Right, box.Bottom),
        };
    }

    private V2ResizeHandle? ResizeHandleAt(Point point)
    {
        foreach (var pair in GetV2ResizeHandles())
            if (pair.Value.Contains(point)) return pair.Key;
        return null;
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
        if (_v2Layout is not null)
            return BuildV2Context(_v2Layout);

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
            var selected = _selectedNames.Contains(name, StringComparer.Ordinal) || name == _selectedName;
            var policyVisible = ViewPolicy.IsVisible(model, effectiveVisible, _filter);
            if (_hiddenByOrigin.Contains(name))
                policyVisible = false;

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
                ViewPolicy.ShouldDrawLabel(model, selected, _labels, _filter, effectiveVisible),
                name == _selectedName));
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
            _assetResolver,
            _stockTemplates,
            _previewOverrides,
            // Falling back to the primary alone keeps a caller that only set SelectedName - an
            // older call site, a test - drawing the same selection outline it always did.
            _selectedNames.Count > 0 || _selectedName is null
                ? _selectedNames
                : [_selectedName],
            _v2Templates,
            _v2ButtonState);
    }

    private CanvasRenderContext BuildV2Context(ResolvedUiLayout layout)
    {
        var origin = Origin;
        var drawable = new List<DrawableFrame>(layout.PaintOrder.Count);
        var diagnostics = new CanvasRenderDiagnostics();
        foreach (var id in layout.PaintOrder)
        {
            if (!layout.Elements.TryGetValue(id, out var element)) continue;
            var selected = _selectedNames.Contains(id.Value, StringComparer.Ordinal) || id.Value == _selectedName;
            var visible = V2Visible(element, _filter);
            if (visible) diagnostics.Accept(V2Kind(element));
            if (!visible && !(selected && ViewPolicy.ShowsSelectionGeometry(_mode))) continue;
            drawable.Add(new DrawableFrame(id.Value, null, element.Rect,
                element.Rect is { } rect ? _viewport.RectToCanvas(rect, origin) : null,
                element.EffectiveVisible, selected,
                element.Rect is { Width: > 0, Height: > 0 },
                visible && (_labels == LabelPolicy.ALL || _labels == LabelPolicy.SELECTED && selected),
                id.Value == _selectedName, element));
        }
        return new CanvasRenderContext(null, _layout ?? new LayoutResult
        {
            Frames = new Dictionary<string, FrameLayout>(), Rects = new Dictionary<string, FrameRect>(),
            PaintOrder = [], Issues = [],
        }, _viewport, origin, Bounds.Size, _mode, _filter, _labels, _selectedName, drawable,
            diagnostics, _assetResolver, _stockTemplates, _previewOverrides,
            _selectedNames.Count > 0 || _selectedName is null ? _selectedNames : [_selectedName],
            _v2Templates, _v2ButtonState) { V2Layout = layout };
    }

    private static FrameKind V2Kind(ResolvedUiElement element) => element.Node?.Kind switch
    {
        UiNodeKind.Texture => FrameKind.TEXTURE,
        UiNodeKind.FontString => FrameKind.FONTSTRING,
        UiNodeKind.Button => FrameKind.BUTTON,
        UiNodeKind.StatusBar => FrameKind.STATUSBAR,
        _ => FrameKind.FRAME,
    };

    private static bool V2Visible(ResolvedUiElement element, VisibilityFilter filter)
    {
        if (!filter.HasFlag(VisibilityFilter.HIDDEN) && !element.EffectiveVisible) return false;
        var flag = V2Kind(element) switch
        {
            FrameKind.TEXTURE => VisibilityFilter.TEXTURES,
            FrameKind.FONTSTRING => VisibilityFilter.TEXT,
            FrameKind.BUTTON => VisibilityFilter.BUTTONS,
            _ => VisibilityFilter.FRAMES,
        };
        return filter.HasFlag(flag);
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

        if (_v2Layout is not null && _mode == CanvasViewMode.PREVIEW)
            return;

        var point = e.GetPosition(this);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && ResizeHandleAt(point) is { } resizeHandle &&
            _selectedName is { } selected)
        {
            Focus();
            _resizing = true;
            _resizeHandle = resizeHandle;
            _dragPointer = e.Pointer;
            _dragName = selected;
            _dragLastCanvasX = point.X;
            _dragLastCanvasY = point.Y;
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }
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

        var additive = IsAdditiveModifier(e.KeyModifiers);
        if (additive || hit is null || !_selectedNames.Contains(hit, StringComparer.Ordinal))
            SelectionRequested?.Invoke(this, new CanvasSelectionEventArgs(hit, additive));

        if (hit is not null && !_lockedNames.Contains(hit) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Focus();
            _dragging = true;
            _dragPointer = e.Pointer;
            _dragName = hit;
            _dragLastCanvasX = point.X;
            _dragLastCanvasY = point.Y;
            e.Pointer.Capture(this);
        }
    }

    /// <summary>
    /// Whether the held keys mean "extend the selection" rather than "select this instead".
    /// </summary>
    /// <remarks>
    /// Ctrl and Cmd are the same gesture on different keyboards, and Shift is included because every
    /// drawing tool that teaches Ctrl-click also honours Shift-click for the same operation. Reading
    /// the modifiers here and reporting a single boolean is deliberate: the canvas must not know
    /// which key maps to what, or every future selection gesture needs a change in two places.
    /// </remarks>
    public static bool IsAdditiveModifier(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Control)
        || modifiers.HasFlag(KeyModifiers.Meta)
        || modifiers.HasFlag(KeyModifiers.Shift);

    /// <summary>
    /// Every widget under a canvas point, front to back.
    /// </summary>
    public IReadOnlyList<string> HitTestCandidates(double canvasX, double canvasY)
    {
        if (_v2Layout is { } v2)
        {
            var hits = new List<string>();
            for (var index = v2.PaintOrder.Count - 1; index >= 0; index--)
            {
                var id = v2.PaintOrder[index];
                if (!v2.Elements.TryGetValue(id, out var element) || element.Rect is not { } rect || !V2Visible(element, _filter)) continue;
                var box = _viewport.RectToCanvas(rect, Origin);
                var slack = box.Width <= 0 || box.Height <= 0 ? HitTester.ZeroAreaPickTolerance : 0;
                if (canvasX >= box.X - slack && canvasX <= box.Right + slack && canvasY >= box.Y - slack && canvasY <= box.Bottom + slack)
                    hits.Add(id.Value);
            }
            return SelectionPriority.Order(hits, _preferredSelectionNames);
        }
        if (_project is null || _layout is null) return [];

        var candidates = HitTester.CandidatesAt(_project!, _layout, _viewport, Origin, _filter, canvasX, canvasY)
            .Where(name => !_hiddenByOrigin.Contains(name))
            .ToArray();
        // Selection priority is independent of paint order: editable project content gets the
        // first click, while repeat-click cycling still exposes every overlapping piece.
        return SelectionPriority.Order(candidates, _preferredSelectionNames);
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

        if (_v2Layout is not null && _mode == CanvasViewMode.PREVIEW)
            return;

        var point = e.GetPosition(this);
        if (!_dragging && !_resizing)
        {
            var hover = ResizeHandleAt(point);
            if (_hoverResizeHandle != hover)
            {
                _hoverResizeHandle = hover;
                InvalidateVisual();
            }
            return;
        }

        if (_resizing && _dragName is not null && _resizeHandle is { } handle)
        {
            var (resizeX, resizeY) = _viewport.CanvasDeltaToModel(point.X - _dragLastCanvasX, point.Y - _dragLastCanvasY);
            _dragLastCanvasX = point.X;
            _dragLastCanvasY = point.Y;
            if (resizeX != 0 || resizeY != 0)
                FrameResized?.Invoke(this, new FrameResizeEventArgs(new SemanticId(_dragName), handle, resizeX, resizeY));
            return;
        }

        if (!_dragging || _dragName is null)
            return;
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

        if (!_dragging && !_resizing)
            return;

        var wasResize = _resizing;

        _dragging = false;
        _resizing = false;
        _dragName = null;
        _resizeHandle = null;
        _dragPointer = null;
        e.Pointer.Capture(null);
        if (wasResize) ResizeCompleted?.Invoke(this, EventArgs.Empty);
        else DragCompleted?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_v2Layout is null || (!_dragging && !_resizing) || e.Key != Key.Escape) return;
        var wasResize = _resizing;
        _dragging = false;
        _resizing = false;
        _dragName = null;
        _resizeHandle = null;
        _dragPointer?.Capture(null);
        _dragPointer = null;
        if (wasResize) ResizeCancelled?.Invoke(this, EventArgs.Empty);
        else DragCancelled?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        e.Handled = true;
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
public sealed record FrameDragEventArgs(string FrameName, double DeltaX, double DeltaY)
{
    public SemanticId? SemanticId => Guid.TryParseExact(FrameName, "D", out _) ? new SemanticId(FrameName) : null;
}

public sealed record FrameResizeEventArgs(SemanticId SemanticId, V2ResizeHandle Handle, double DeltaX, double DeltaY);
