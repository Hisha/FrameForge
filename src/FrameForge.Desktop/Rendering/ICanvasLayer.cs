using Avalonia;
using Avalonia.Media;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Viewing;

namespace FrameForge.Desktop.Rendering;

/// <summary>
/// Everything a layer needs to draw one frame, decided by Core policy and handed over unchanged.
/// </summary>
/// <remarks>
/// A layer never asks whether something is visible, whether it is selected, or which mode is
/// active. Those answers come from <see cref="ViewPolicy"/> in FrameForge.Core, are computed once
/// per frame here, and are carried in this record. That is what keeps a renderer's drawing code
/// free of policy and makes the policy testable on its own.
/// </remarks>
/// <param name="Name">The widget's name.</param>
/// <param name="Model">The widget, or null if the layout knows a name the project does not.</param>
/// <param name="Rect">Resolved model-space bounds, or null when the engine could not place it.</param>
/// <param name="Box">Resolved canvas-space bounds, or null when there is no rect.</param>
/// <param name="EffectiveVisible">Whether the widget is on screen, ancestors included.</param>
/// <param name="Selected">Whether this widget is the current selection.</param>
/// <param name="HasArea">Whether the widget has both a non-zero width and height.</param>
/// <param name="DrawLabel">Whether the widget's name label belongs on screen this pass.</param>
public sealed record DrawableFrame(
    string Name,
    FrameDef? Model,
    FrameRect? Rect,
    CanvasBox? Box,
    bool EffectiveVisible,
    bool Selected,
    bool HasArea,
    bool DrawLabel);

/// <summary>
/// One pass over the canvas: everything the layers need, already resolved.
/// </summary>
/// <remarks>
/// Built once per <c>Render</c> and shared by every layer. Constructing it is where the per-frame
/// policy questions get asked, so no layer has to answer them and no two layers can disagree.
/// </remarks>
/// <param name="Project">The project being viewed.</param>
/// <param name="Layout">The resolved layout.</param>
/// <param name="Viewport">Zoom and pan.</param>
/// <param name="Origin">Canvas point the model origin maps to.</param>
/// <param name="CanvasSize">The control's size.</param>
/// <param name="Mode">The active view mode.</param>
/// <param name="Filter">The active visibility set.</param>
/// <param name="Labels">The active label policy.</param>
/// <param name="SelectedName">The current selection, or null.</param>
/// <param name="VisibleFrames">The widgets the policy says are drawn, in paint order.</param>
public sealed record CanvasRenderContext(
    Project? Project,
    LayoutResult Layout,
    Viewport Viewport,
    CanvasOrigin Origin,
    Size CanvasSize,
    CanvasViewMode Mode,
    VisibilityFilter Filter,
    LabelPolicy Labels,
    string? SelectedName,
    IReadOnlyList<DrawableFrame> VisibleFrames,
    CanvasRenderDiagnostics Diagnostics)
{
    /// <summary>The screen (UIParent) rectangle in canvas pixels.</summary>
    public CanvasBox ScreenBox => Viewport.RectToCanvas(LayoutResolver.ScreenRect(Project?.Screen ?? Screen.Default), Origin);

    /// <summary>The selected widget's drawable, or null when nothing is selected.</summary>
    public DrawableFrame? Selection => SelectedName is null
        ? null
        : VisibleFrames.FirstOrDefault(f => f.Name == SelectedName);

    /// <summary>
    /// Widgets the engine could not place, which have no rectangle to be drawn at.
    /// </summary>
    public IReadOnlyList<string> UnresolvedNames =>
        Layout.Frames.Values.Where(f => f.Rect is null).Select(f => f.Name).ToArray();
}

/// <summary>Mutable counters populated during one render pass, then exposed as an immutable trace.</summary>
public sealed class CanvasRenderDiagnostics
{
    private readonly Dictionary<FrameKind, int> _visibilityAccepted = [];
    private readonly Dictionary<FrameKind, int> _visualAttempts = [];

    public bool VisualContentExecuted { get; set; }

    public int VisualPaintOperations { get; private set; }

    public void Accept(FrameKind kind) => Increment(_visibilityAccepted, kind);

    public void AttemptVisual(FrameKind kind, int paintOperations)
    {
        Increment(_visualAttempts, kind);
        VisualPaintOperations += paintOperations;
    }

    public CanvasRenderTrace Snapshot(
        CanvasRenderContext context,
        IReadOnlyList<string> activeLayers) => new(
        context.Mode,
        context.Filter,
        context.Labels,
        context.Project?.Frames.Count ?? 0,
        context.Layout.Rects.Count,
        context.Layout.PaintOrder.Count,
        _visibilityAccepted.Values.Sum(),
        new Dictionary<FrameKind, int>(_visibilityAccepted),
        context.VisibleFrames.Count,
        context.VisibleFrames.Count(frame => frame.Box is not null),
        context.VisibleFrames.Count(frame => frame.HasArea),
        activeLayers,
        VisualContentExecuted,
        new Dictionary<FrameKind, int>(_visualAttempts),
        VisualPaintOperations);

    private static void Increment(IDictionary<FrameKind, int> counts, FrameKind kind)
    {
        counts.TryGetValue(kind, out var current);
        counts[kind] = current + 1;
    }
}

/// <summary>An evidence record for one completed canvas render pass.</summary>
public sealed record CanvasRenderTrace(
    CanvasViewMode Mode,
    VisibilityFilter Filter,
    LabelPolicy Labels,
    int ProjectFrames,
    int ResolvedRectangles,
    int PaintOrderEntries,
    int VisibilityAccepted,
    IReadOnlyDictionary<FrameKind, int> VisibilityAcceptedByKind,
    int DrawableFrames,
    int DrawableFramesWithBox,
    int DrawableFramesWithArea,
    IReadOnlyList<string> ActiveLayers,
    bool VisualContentExecuted,
    IReadOnlyDictionary<FrameKind, int> VisualAttemptsByKind,
    int VisualPaintOperations);

/// <summary>
/// One independently drawable band of the canvas.
/// </summary>
/// <remarks>
/// The bands are separated so that a change to one does not repaint decisions made for another:
/// the clean Preview content, the debug wireframe, and the selection chrome are three different
/// concerns with three different lifetimes. In Debug all three are active, and the debug band sits
/// over the content exactly as Phase 2's wireframe sat alone.
/// </remarks>
public interface ICanvasLayer
{
    /// <summary>Stable name, used in diagnostics and by the pipeline's ordering test.</summary>
    string Name { get; }

    /// <summary>Lower numbers are drawn first.</summary>
    int Order { get; }

    /// <summary>
    /// Whether this layer contributes anything for this mode, so an inactive band can be skipped
    /// entirely rather than drawn as nothing.
    /// </summary>
    bool AppliesTo(CanvasViewMode mode) => true;

    /// <summary>Draws the layer's band.</summary>
    void Render(DrawingContext context, CanvasRenderContext canvas);
}
