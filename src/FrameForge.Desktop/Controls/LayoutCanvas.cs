using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;

namespace FrameForge.Desktop.Controls;

/// <summary>Draws and edits a FrameForge layout: the modelled UIParent, the frame rectangles, their
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
/// <para>
/// WHAT IS DRAWN IS DELIBERATELY DIFFERENT PER WIDGET KIND, because a pile of identical
/// rectangles hides the thing an imported FrameXML file is made of. A FontString is not a
/// panel; a Texture is not a frame; a synthesized stand-in is not part of the addon at all.
/// The distinctions are listed on <see cref="FrameLooks"/>.
/// </para>
/// </remarks>
public class LayoutCanvas : Control
{
    private static readonly IPen ScreenPen = new Pen(new SolidColorBrush(Color.Parse("#4C8DA8")), 1);
    private static readonly IBrush ScreenFill = new SolidColorBrush(Color.Parse("#14232C"));
    private static readonly IPen HiddenPen = new Pen(new SolidColorBrush(Color.Parse("#6B7C87")), 1, new DashStyle(new double[] { 3, 3 }, 0));
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.Parse("#F2C14E")), 2);
    private static readonly IPen IssuePen = new Pen(new SolidColorBrush(Color.Parse("#E06C6C")), 2);
    private static readonly IPen LabelBackground = new Pen(new SolidColorBrush(Color.Parse("#0B1014")), 1);
    private static readonly IBrush LabelForeground = new SolidColorBrush(Color.Parse("#DCE7EE"));
    private static readonly IBrush LabelForegroundSelected = new SolidColorBrush(Color.Parse("#101418"));
    private static readonly IBrush UnresolvedForeground = new SolidColorBrush(Color.Parse("#E06C6C"));
    private static readonly IBrush ChevronForeground = new SolidColorBrush(Color.Parse("#7FB2C9"));
    private static readonly IBrush LabelBackdrop = new SolidColorBrush(Color.Parse("#0B1014"));

    // Anchor visualization, drawn only for the selected frame so the canvas stays readable.
    private static readonly IPen AnchorConnectorPen =
        new Pen(new SolidColorBrush(Color.Parse("#F2C14E")), 1, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IPen AnchorBrokenPen =
        new Pen(new SolidColorBrush(Color.Parse("#E06C6C")), 1.5, new DashStyle(new double[] { 2, 2 }, 0));
    private static readonly IPen AnchorExtraPen =
        new Pen(new SolidColorBrush(Color.Parse("#C9A227")), 1.5, new DashStyle(new double[] { 5, 3 }, 0));
    private static readonly IPen OwnPointPen = new Pen(new SolidColorBrush(Color.Parse("#F2C14E")), 1.5);
    private static readonly IBrush OwnPointFill = new SolidColorBrush(Color.Parse("#F2C14E"));
    private static readonly IBrush AnchorExtraBrush = new SolidColorBrush(Color.Parse("#C9A227"));
    private static readonly IPen TargetPen = new Pen(new SolidColorBrush(Color.Parse("#7FB2C9")), 1.5);

    // Zero-area markers, for the widgets FrameXML never gave a size.
    private static readonly IBrush ZeroSizeBrush = new SolidColorBrush(Color.Parse("#B08A4A"));
    private static readonly IPen ZeroSizePen = new Pen(new SolidColorBrush(Color.Parse("#0B1014")), 1);
    private static readonly IPen PlaceholderPen =
        new Pen(new SolidColorBrush(Color.Parse("#9A7BC8")), 1.5, new DashStyle(new double[] { 6, 4 }, 0));

    private readonly Typeface _labelTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private LayoutResult? _layout;
    private Project? _project;
    private string? _selectedName;
    private bool _dragging;
    private string? _dragName;
    private double _dragLastCanvasX;
    private double _dragLastCanvasY;
    private Viewport _viewport = Viewport.Identity;

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
    /// zero-area markers, anchor connectors and selection decorations are all clipped without
    /// each of them having to know it is being clipped. Any future drawing added to
    /// <see cref="Render"/> is contained automatically.
    /// </para>
    /// <para>
    /// It is set in the constructor rather than in XAML so that the guarantee belongs to the
    /// control: recreating this control, or moving it under a different host, cannot lose it.
    /// Note that <c>Control.ClipToBounds</c> defaults to false in Avalonia, so this has to be
    /// turned on explicitly.
    /// </para>
    /// </remarks>
    public LayoutCanvas() => ClipToBounds = true;

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

    /// <summary>
    /// The visual identity of each widget kind.
    /// </summary>
    /// <remarks>
    /// An imported FrameXML document is mostly Textures and FontStrings, and drawing them all
    /// as rectangles makes a 52-element file look like a handful of overlapping boxes. These
    /// three signals are enough to tell them apart at a glance without pretending to be
    /// Blizzard's artwork, which FrameForge deliberately does not draw.
    /// </remarks>
    private static class FrameLooks
    {
        public static Color ColorFor(FrameDef? frame) => frame is { Placeholder: true }
            ? Color.Parse("#9A7BC8")
            : frame?.Kind switch
        {
            FrameKind.FRAME => Color.Parse("#5C93B0"),
            FrameKind.BUTTON => Color.Parse("#C08A4A"),
            FrameKind.FONTSTRING => Color.Parse("#63C2A0"),
            FrameKind.TEXTURE => Color.Parse("#4A6472"),
            FrameKind.STATUSBAR => Color.Parse("#B08AC4"),
            _ => Color.Parse("#5C93B0"),
        };

        /// <summary>Whether the rectangle is drawn hollow or filled for this kind.</summary>
        public static bool Filled(FrameDef? frame) =>
            frame?.Kind is FrameKind.FONTSTRING or FrameKind.STATUSBAR;

        /// <summary>Short tag shown next to the name, so a zero-size widget is still identifiable.</summary>
        public static string Badge(FrameDef? frame)
        {
            if (frame is null)
                return string.Empty;
            if (frame.Placeholder)
                return "stand-in";

            return frame.Kind.Badge() + (frame.SetAllPoints ? " fill" : string.Empty);
        }
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

        DrawSelectedAnchors(context, origin);

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

    /// <summary>Model point to canvas point. The only conversion used for marker placement.</summary>
    private Point ToCanvas(ModelPoint model, CanvasOrigin origin) =>
        new(_viewport.ModelToCanvasX(model.X, origin), _viewport.ModelToCanvasY(model.Y, origin));

    private void DrawBackground(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0C1116")), new Rect(Bounds.Size));
    }

    private void DrawScreen(DrawingContext context, CanvasOrigin origin)
    {
        var screen = LayoutResolver.ScreenRect(_project?.Screen ?? Screen.Default);
        var box = _viewport.RectToCanvas(screen, origin);

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
        var model = _project?.Find(name);

        // Zero-area widgets get a marker instead of an invisible rectangle.
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            DrawZeroSizeMarker(context, origin, rect, model, selected);
            return;
        }

        var looks = FrameLooks.ColorFor(model);
        var pen = selected
            ? SelectedPen
            : model?.Placeholder ?? false
                ? PlaceholderPen
                : !detail.EffectiveVisible
                    ? HiddenPen
                    : new Pen(new SolidColorBrush(looks), model?.Parent is null ? 1.5 : 1);

        if (FrameLooks.Filled(model) && !selected)
            context.FillRectangle(new SolidColorBrush(looks) { Opacity = 0.18 }, canvasRect);

        context.DrawRectangle(null, pen, canvasRect);

        var badge = FrameLooks.Badge(model);
        DrawLabel(context, badge.Length == 0 ? name : $"{name}  [{badge}]",
            new Point(box.X + 5, box.Y + 4), selected ? LabelForegroundSelected : LabelForeground);
    }

    /// <summary>
    /// Draws a widget with no area.
    /// </summary>
    /// <remarks>
    /// This is not an edge case FrameForge can shrug at: a real FrameXML file is full of them.
    /// FontStrings with no <c>&lt;Size&gt;</c> are sized by Blizzard's font metrics and Buttons
    /// that inherit a template the file does not define have no size at all. In WoW both still
    /// occupy space and are still clickable; here they are shown as a labelled cross so they
    /// stay selectable and visible instead of disappearing into nothing.
    /// </remarks>
    private void DrawZeroSizeMarker(
        DrawingContext context,
        CanvasOrigin origin,
        FrameRect rect,
        FrameDef? model,
        bool selected)
    {
        var center = ToCanvas(new ModelPoint(rect.Left, rect.Top), origin);
        var radius = 5d;
        var box = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);

        context.FillRectangle(ZeroSizeBrush, box);
        context.DrawRectangle(null, selected ? SelectedPen : ZeroSizePen, box);

        var looks = FrameLooks.ColorFor(model);
        context.DrawLine(selected ? SelectedPen : new Pen(new SolidColorBrush(looks), 1),
            new Point(center.X - radius - 3, center.Y),
            new Point(center.X + radius + 3, center.Y));
        context.DrawLine(selected ? SelectedPen : new Pen(new SolidColorBrush(looks), 1),
            new Point(center.X, center.Y - radius - 3),
            new Point(center.X, center.Y + radius + 3));

        DrawLabel(context, $"{model?.Name ?? "?"}  [no size]", new Point(center.X + radius + 6, center.Y - 7),
            selected ? LabelForegroundSelected : LabelForeground);
    }

    /// <summary>
    /// Draws every anchor of the selected frame: the point of the frame, the point of the
    /// target it is pinned to, and the line between them.
    /// </summary>
    /// <remarks>
    /// This is the answer to "what is actually controlling where this is?". Without it, a
    /// frame's position has to be inferred from two unrelated rectangles; with it, the
    /// relationship is drawn, including the <see cref="ResolvedAnchor.OffsetX"/ and
    /// <see cref="ResolvedAnchor.OffsetY"/> that push the two ends apart.
    /// <para>
    /// Anchors are drawn per <em>anchor</em>, not per frame: an extra anchor gets its own
    /// connector in its own style, so a multi-anchor frame is legible as multi-anchor rather
    /// than appearing to have one authoritative relationship. An anchor the engine could not
    /// honour is drawn dashed in red, because a solid line would assert something untrue.
    /// </para>
    /// </remarks>
    private void DrawSelectedAnchors(DrawingContext context, CanvasOrigin origin)
    {
        if (_selectedName is null || !_layout!.Frames.TryGetValue(_selectedName, out var detail))
            return;

        foreach (var anchor in detail.Anchors)
        {
            var own = ToCanvas(anchor.OwnPosition, origin);

            if (!anchor.TargetPositioned)
            {
                // There is no second end to join to. Drawing towards the default position would
                // point the user at the model origin for no reason, so only this end is marked.
                DrawOwnPoint(context, own, anchor.Primary);
                DrawLabel(context, $"{anchor.Label}: target could not be positioned",
                    new Point(own.X + 8, own.Y - 7), UnresolvedForeground);
                continue;
            }

            var target = ToCanvas(anchor.TargetPosition, origin);
            var pen = !anchor.Resolved ? AnchorBrokenPen
                : anchor.Primary ? AnchorConnectorPen
                : AnchorExtraPen;

            context.DrawLine(pen, target, own);

            // The gap between the two points IS the authored offset, so it is drawn as such.
            if (anchor.OffsetX != 0 || anchor.OffsetY != 0)
                DrawOffsetLabel(context, anchor, own, target);

            // The target end is a hollow square, the frame's own end a filled dot: the shapes
            // differ so the two ends stay tellable apart when a connector is only a few pixels
            // long. Dashed purple is reserved for stand-in frames and is not borrowed here.
            context.DrawRectangle(null, TargetPen, new Rect(target.X - 4.5, target.Y - 4.5, 9, 9));
            DrawOwnPoint(context, own, anchor.Primary);
        }

        if (detail.Anchors.Count > 1)
        {
            var summary = detail.Anchors.Count + " anchors";
            var position = detail.Rect is { } rect
                ? ToCanvas(new ModelPoint(rect.Right, rect.Top), origin)
                : new Point(8, 8);

            DrawLabel(context, summary, new Point(position.X + 8, position.Y + 4), LabelForeground);
        }
    }

    private void DrawOwnPoint(DrawingContext context, Point position, bool primary) =>
        context.DrawEllipse(primary ? OwnPointFill : AnchorExtraBrush, OwnPointPen, position, 3.5, 3.5);

    private void DrawOffsetLabel(DrawingContext context, ResolvedAnchor anchor, Point own, Point target)
    {
        var number = (double value) => value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

        var text = anchor is { Primary: true }
            ? $"({number(anchor.OffsetX)}, {number(anchor.OffsetY)})"
            : $"anchor {anchor.Index + 1} ({number(anchor.OffsetX)}, {number(anchor.OffsetY)})";

        DrawLabel(context, text,
            new Point((own.X + target.X) / 2 + 4, (own.Y + target.Y) / 2 - 7),
            anchor.Resolved ? LabelForeground : UnresolvedForeground);
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