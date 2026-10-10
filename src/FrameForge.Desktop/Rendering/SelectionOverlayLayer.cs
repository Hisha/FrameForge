using System.Globalization;
using Avalonia;
using Avalonia.Media;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Viewing;
using FrameForge.Core.Semantics.V2;

namespace FrameForge.Desktop.Rendering;

/// <summary>
/// The selection band: the selection outline and the selected widget's anchors.
/// </summary>
/// <remarks>
/// Drawn last in Design/Debug/Hybrid modes. Preview deliberately omits this editor-only chrome so
/// the composed interface can be judged without selection affordances.
/// <para>
/// Hybrid is where this earns its keep: clean rendering plus the anchors and resolved
/// bounds of the current selection is the answer to "show me the UI, but show me exactly what
/// controls the thing I have selected". Preview alone hides the geometry, which is right for
/// judging appearance and wrong for editing.
/// </para>
/// </remarks>
public sealed class SelectionOverlayLayer : ICanvasLayer
{
    /// <inheritdoc />
    public string Name => "selection-overlay";

    /// <inheritdoc />
    public int Order => 20;

    public bool AppliesTo(CanvasViewMode mode) => mode != CanvasViewMode.PREVIEW;

    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.Parse("#F2C14E")), 2);
    private static readonly IPen SecondaryPen =
        new Pen(new SolidColorBrush(Color.Parse("#7FB2C9")), 1.5, new DashStyle(new double[] { 5, 3 }, 0));
    private static readonly IPen AnchorConnectorPen =
        new Pen(new SolidColorBrush(Color.Parse("#F2C14E")), 1, new DashStyle(new double[] { 4, 3 }, 0));
    private static readonly IPen AnchorBrokenPen =
        new Pen(new SolidColorBrush(Color.Parse("#E06C6C")), 1.5, new DashStyle(new double[] { 2, 2 }, 0));
    private static readonly IPen AnchorExtraPen =
        new Pen(new SolidColorBrush(Color.Parse("#C9A227")), 1.5, new DashStyle(new double[] { 5, 3 }, 0));
    private static readonly IPen OwnPointPen = new Pen(new SolidColorBrush(Color.Parse("#F2C14E")), 1.5);
    private static readonly IPen TargetPen = new Pen(new SolidColorBrush(Color.Parse("#7FB2C9")), 1.5);
    private static readonly IBrush OwnPointFill = new SolidColorBrush(Color.Parse("#F2C14E"));
    private static readonly IBrush AnchorExtraBrush = new SolidColorBrush(Color.Parse("#C9A227"));
    private static readonly IBrush Foreground = new SolidColorBrush(Color.Parse("#DCE7EE"));
    private static readonly IBrush Unresolved = new SolidColorBrush(Color.Parse("#E06C6C"));

    /// <inheritdoc />
    public void Render(DrawingContext context, CanvasRenderContext canvas)
    {
        if (canvas.SelectionNames.Count == 0)
            return;

        DrawSecondaryOutlines(context, canvas);
        DrawOutline(context, canvas);

        // Anchors stay exclusive to the primary. Four widgets' anchor lines at once is not more
        // information, it is a hairball; the primary's anchors are the ones the inspector is
        // describing right now.
        if (canvas.SelectedName is { } primary && ViewPolicy.ShowsSelectionGeometry(canvas.Mode))
        {
            if (canvas.V2Layout is { } v2 && Guid.TryParseExact(primary, "D", out _)
                && v2.Elements.TryGetValue(new SemanticId(primary), out var native))
                DrawAnchors(context, canvas, native);
            else if (canvas.Layout.Frames.TryGetValue(primary, out var detail))
                DrawAnchors(context, canvas, detail);
        }
    }

    /// <summary>
    /// Thin dashed outlines on the members of a multi-selection that are not the primary.
    /// </summary>
    /// <remarks>
    /// A multi-selection that only outlined its primary would look exactly like a single selection,
    /// and the user would have no way to see what a subsequent align was about to act on. Dashed
    /// rather than solid, and in the anchor-target colour rather than gold, because gold means "this
    /// is the object the inspector is describing" everywhere else in the app.
    /// </remarks>
    private static void DrawSecondaryOutlines(DrawingContext context, CanvasRenderContext canvas)
    {
        foreach (var frame in canvas.VisibleFrames)
        {
            if (!frame.Selected || frame.Primary)
                continue;

            DrawBox(context, frame, SecondaryPen);
        }
    }

    /// <summary>
    /// The selection rectangle, in gold, on whichever band drew the widget.
    /// </summary>
    /// <remarks>
    /// Drawn even in Preview, where no other outline exists. Without it a selected widget is
    /// indistinguishable from an unselected one on a clean canvas, which would make canvas
    /// selection feel broken.
    /// </remarks>
    private static void DrawOutline(DrawingContext context, CanvasRenderContext canvas)
    {
        if (canvas.Selection is { } selection)
            DrawBox(context, selection, SelectedPen);
    }

    private static void DrawBox(DrawingContext context, DrawableFrame frame, IPen pen)
    {
        if (frame.Box is not { } box)
            return;

        var rect = new Rect(box.X, box.Y, box.Width, box.Height);
        if (frame.HasArea)
        {
            context.DrawRectangle(null, pen, rect);
            return;
        }

        // A zero-area selection still needs a marker, or selecting one of the six auto-sized
        // FontStrings would produce no visible feedback at all.
        const double radius = 7;
        context.DrawRectangle(null, pen,
            new Rect(rect.X - radius, rect.Y - radius, radius * 2, radius * 2));
    }

    /// <summary>
    /// Every anchor of the selected widget: its own point, the target it is pinned to, and the line
    /// between them.
    /// </summary>
    /// <remarks>
    /// The answer to "what is actually controlling where this is?". Without it a frame's position
    /// has to be inferred from two unrelated rectangles.
    /// <para>
    /// Drawn per <em>anchor</em>, not per frame, so a multi-anchor frame is legible as
    /// multi-anchor rather than appearing to have one authoritative relationship. An anchor the
    /// engine could not honour is dashed in red, because a solid line would assert something untrue.
    /// </para>
    /// </remarks>
    private static void DrawAnchors(DrawingContext context, CanvasRenderContext canvas, FrameLayout detail)
    {
        foreach (var anchor in detail.Anchors)
        {
            var own = ToCanvas(canvas, anchor.OwnPosition);

            if (!anchor.TargetPositioned)
            {
                // No second end to join to. Pointing at the model origin would be meaningless, so
                // only this end is marked.
                DrawOwnPoint(context, own, anchor.Primary);
                Label.Draw(context, $"{anchor.Label}: target could not be positioned",
                    new Point(own.X + 8, own.Y - 7), Unresolved);
                continue;
            }

            var target = ToCanvas(canvas, anchor.TargetPosition);
            var pen = !anchor.Resolved ? AnchorBrokenPen
                : anchor.Primary ? AnchorConnectorPen
                : AnchorExtraPen;

            context.DrawLine(pen, target, own);

            // The gap between the two points IS the authored offset, so it is drawn as such.
            if (anchor.OffsetX != 0 || anchor.OffsetY != 0)
                DrawOffsetLabel(context, anchor, own, target);

            // Hollow square for the target, filled dot for the frame's own end: the shapes differ so
            // the two ends stay tellable apart when a connector is only a few pixels long. Dashed
            // purple is reserved for stand-ins and is not borrowed here.
            context.DrawRectangle(null, TargetPen, new Rect(target.X - 4.5, target.Y - 4.5, 9, 9));
            DrawOwnPoint(context, own, anchor.Primary);
        }

        if (detail.Anchors.Count > 1)
        {
            var summary = detail.Anchors.Count + " anchors";
            var position = detail.Rect is { } rect
                ? ToCanvas(canvas, new ModelPoint(rect.Right, rect.Top))
                : new Point(8, 8);

            Label.Draw(context, summary, new Point(position.X + 8, position.Y + 4), Foreground);
        }
    }

    private static void DrawAnchors(DrawingContext context, CanvasRenderContext canvas, ResolvedUiElement detail)
    {
        foreach (var anchor in detail.Anchors)
        {
            if (anchor.OwnPosition is not { } ownModel) continue;
            var own = ToCanvas(canvas, ownModel);
            if (anchor.TargetPosition is not { } targetModel)
            {
                DrawOwnPoint(context, own, anchor.Index == 0);
                Label.Draw(context, $"Anchor {anchor.Index + 1}: target unresolved",
                    new Point(own.X + 8, own.Y - 7), Unresolved);
                continue;
            }
            var target = ToCanvas(canvas, targetModel);
            var pen = !anchor.Resolved ? AnchorBrokenPen : anchor.Index == 0 ? AnchorConnectorPen : AnchorExtraPen;
            context.DrawLine(pen, target, own);
            context.DrawRectangle(null, TargetPen, new Rect(target.X - 4.5, target.Y - 4.5, 9, 9));
            DrawOwnPoint(context, own, anchor.Index == 0);
        }
        if (detail.Anchors.Count > 1 && detail.Rect is { } rect)
        {
            var position = ToCanvas(canvas, new ModelPoint(rect.Right, rect.Top));
            Label.Draw(context, $"{detail.Anchors.Count} anchors", new Point(position.X + 8, position.Y + 4), Foreground);
        }
    }

    private static void DrawOwnPoint(DrawingContext context, Point position, bool primary) =>
        context.DrawEllipse(primary ? OwnPointFill : AnchorExtraBrush, OwnPointPen, position, 3.5, 3.5);

    private static void DrawOffsetLabel(
        DrawingContext context,
        ResolvedAnchor anchor,
        Point own,
        Point target)
    {
        var number = (double value) => value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

        var text = anchor is { Primary: true }
            ? $"({number(anchor.OffsetX)}, {number(anchor.OffsetY)})"
            : $"anchor {anchor.Index + 1} ({number(anchor.OffsetX)}, {number(anchor.OffsetY)})";

        Label.Draw(context, text,
            new Point((own.X + target.X) / 2 + 4, (own.Y + target.Y) / 2 - 7),
            anchor.Resolved ? Foreground : Unresolved);
    }

    private static Point ToCanvas(CanvasRenderContext canvas, ModelPoint model) => new(
        canvas.Viewport.ModelToCanvasX(model.X, canvas.Origin),
        canvas.Viewport.ModelToCanvasY(model.Y, canvas.Origin));
}
