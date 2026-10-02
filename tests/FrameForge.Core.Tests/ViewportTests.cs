using FrameForge.Core.Examples;
using FrameForge.Core.Geometry;
using static FrameForge.Core.Tests.TestProject;
using Xunit;

namespace FrameForge.Core.Tests;

/// <summary>
/// Model space is WoW-native (+Y up); Avalonia canvas space is +Y down. Every conversion
/// between them goes through <see cref="Viewport"/>, and these tests pin the mapping so a
/// sign error cannot creep into the canvas.
/// </summary>
public class ViewportTests
{
    private static readonly CanvasOrigin Origin = new(400, 300);

    [Fact]
    public void ModelOriginLandsOnTheCanvasOrigin()
    {
        var canvas = Viewport.Identity.ModelToCanvas(ModelPoint.Origin, Origin);

        Assert.Equal(400, canvas.X, 10);
        Assert.Equal(300, canvas.Y, 10);
    }

    [Fact]
    public void XIsUntouchedAndYIsFlipped()
    {
        var viewport = Viewport.Identity;

        // Model +Y is UP, canvas +Y is DOWN.
        var up = viewport.ModelToCanvas(new ModelPoint(100, 100), Origin);
        Assert.Equal(500, up.X, 10);
        Assert.Equal(200, up.Y, 10);

        var down = viewport.ModelToCanvas(new ModelPoint(100, -100), Origin);
        Assert.Equal(500, down.X, 10);
        Assert.Equal(400, down.Y, 10);
    }

    [Fact]
    public void ScalesByZoom()
    {
        var canvas = new Viewport(2, 0, 0).ModelToCanvas(new ModelPoint(10, 10), Origin);

        Assert.Equal(420, canvas.X, 10);
        Assert.Equal(280, canvas.Y, 10);
    }

    [Fact]
    public void AppliesPanBeforeZoom()
    {
        var canvas = new Viewport(2, 10, -5).ModelToCanvas(new ModelPoint(20, 0), Origin);

        Assert.Equal(420, canvas.X, 10);
        Assert.Equal(290, canvas.Y, 10);
    }

    [Fact]
    public void CanvasToModelIsTheExactInverseOfModelToCanvas()
    {
        var viewport = new Viewport(1.75, -120, 64);
        var model = new ModelPoint(-333.25, 91.5);

        var canvas = viewport.ModelToCanvas(model, Origin);
        var back = viewport.CanvasToModel(canvas.X, canvas.Y, Origin);

        Assert.Equal(model.X, back.X, 9);
        Assert.Equal(model.Y, back.Y, 9);
    }

    [Fact]
    public void ARightwardDragProducesAPositiveXModelDelta()
    {
        var (x, y) = Viewport.Identity.CanvasDeltaToModel(12, 0);

        Assert.Equal(12, x, 10);
        Assert.Equal(0, y, 10);
    }

    /// <summary>
    /// The behaviour that makes dragging feel correct in WoW terms: dragging DOWN the screen
    /// must DECREASE the frame's WoW offsetY, because screen Y grows downward and WoW Y grows
    /// upward.
    /// </summary>
    [Fact]
    public void ADownwardDragProducesANegativeYModelDelta()
    {
        var (x, y) = Viewport.Identity.CanvasDeltaToModel(0, 20);

        Assert.Equal(0, x, 10);
        Assert.Equal(-20, y, 10);
    }

    [Fact]
    public void DragDeltasAccountForZoom()
    {
        var (x, y) = new Viewport(3, 0, 0).CanvasDeltaToModel(0, 30);

        Assert.Equal(0, x, 10);
        Assert.Equal(-10, y, 10);
    }

    [Fact]
    public void RectToCanvasReturnsAPositiveHeightBoxWithTheYFlipApplied()
    {
        var box = Viewport.Identity.RectToCanvas(FrameRect.FromSize(0, 100, 200, 100), Origin);

        Assert.Equal(400, box.X, 10);
        Assert.Equal(200, box.Y, 10);
        Assert.Equal(200, box.Width, 10);
        Assert.Equal(100, box.Height, 10);
    }

    [Fact]
    public void RectToCanvasScalesTheBoxWithZoom()
    {
        var box = new Viewport(2, 0, 0).RectToCanvas(FrameRect.FromSize(0, 100, 200, 100), Origin);

        Assert.Equal(400, box.X, 10);
        Assert.Equal(100, box.Y, 10);
        Assert.Equal(400, box.Width, 10);
        Assert.Equal(200, box.Height, 10);
    }

    [Fact]
    public void RectToCanvasRoundTripsBackToTheSameModelRectangle()
    {
        var viewport = new Viewport(0.75, 12, -8);
        var rect = LayoutResolver.Resolve(NativeHuntsExample.CreateProject()).Rects["Identity"];

        var box = viewport.RectToCanvas(rect, Origin);
        var corners = new[]
        {
            viewport.CanvasToModel(box.X, box.Y, Origin),
            viewport.CanvasToModel(box.Right, box.Bottom, Origin),
        };

        Assert.Equal(rect.Left, Math.Min(corners[0].X, corners[1].X), 8);
        Assert.Equal(rect.Right, Math.Max(corners[0].X, corners[1].X), 8);
        Assert.Equal(rect.Top, Math.Max(corners[0].Y, corners[1].Y), 8);
        Assert.Equal(rect.Bottom, Math.Min(corners[0].Y, corners[1].Y), 8);
    }

    [Fact]
    public void ZoomAtKeepsTheModelPointUnderTheCursorStationary()
    {
        var before = Viewport.Identity;
        const double cursorX = 250, cursorY = 100;
        var modelBefore = before.CanvasToModel(cursorX, cursorY, Origin);

        var after = before.ZoomAt(2, cursorX, cursorY, Origin);
        var modelAfter = after.CanvasToModel(cursorX, cursorY, Origin);

        Assert.Equal(modelBefore.X, modelAfter.X, 9);
        Assert.Equal(modelBefore.Y, modelAfter.Y, 9);
        Assert.Equal(2, after.Zoom, 10);
    }

    [Theory]
    [InlineData(1000, Viewport.MaxZoom)]
    [InlineData(0.0001, Viewport.MinZoom)]
    [InlineData(double.NaN, 1)]
    public void ZoomIsClampedToASaneRange(double requested, double expected)
    {
        var after = Viewport.Identity.ZoomAt(requested, 0, 0, Origin);

        Assert.Equal(expected, after.Zoom, 10);
    }
}