using FrameForge.Core.Viewing;

namespace FrameForge.Desktop.Rendering;

/// <summary>
/// The ordered set of layers the canvas draws, built once.
/// </summary>
/// <remarks>
/// The pipeline exists so that "what does Preview hide?" has one answer, held here, rather than a
/// condition inside the drawing code. A layer declares which modes it applies to, the pipeline
/// filters by the active mode, and the order is fixed at construction - so adding a renderer later
/// cannot accidentally draw beneath the debug wireframe or over the selection chrome.
/// </remarks>
public sealed class RenderPipeline
{
    private readonly List<ICanvasLayer> _layers;

    /// <summary>Builds a pipeline over the given layers, ordered by <see cref="ICanvasLayer.Order"/>.</summary>
    public RenderPipeline(IEnumerable<ICanvasLayer> layers) =>
        _layers = [.. layers.OrderBy(l => l.Order)];

    /// <summary>Every layer, in draw order.</summary>
    public IReadOnlyList<ICanvasLayer> Layers => _layers;

    /// <summary>The layers that contribute in this mode, in draw order.</summary>
    public IReadOnlyList<ICanvasLayer> For(CanvasViewMode mode) =>
        [.. _layers.Where(l => l.AppliesTo(mode))];

    /// <summary>The pipeline FrameForge uses: content, then debug wireframe, then selection chrome.</summary>
    public static RenderPipeline Default() => new(
    [
        new VisualContentLayer(),
        new DebugOverlayLayer(),
        new SelectionOverlayLayer(),
    ]);
}