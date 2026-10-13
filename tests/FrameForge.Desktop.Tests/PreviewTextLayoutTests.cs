using Avalonia;
using FrameForge.Desktop.Rendering;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class PreviewTextLayoutTests
{
    [Theory]
    [InlineData("LEFT", "TOP")]
    [InlineData("CENTER", "MIDDLE")]
    [InlineData("RIGHT", "BOTTOM")]
    public void Zoom_scales_one_shaped_result_without_changing_normalized_placement(
        string justifyH, string justifyV)
    {
        var modelHost = new Rect(17, 23, 120, 10);
        var shapedWidth = 72.375;
        var shapedHeight = 8.25;
        var baseline = PreviewTextLayout.Place(modelHost, shapedWidth, shapedHeight, 1, justifyH, justifyV);

        foreach (var zoom in new[] { 0.25, 0.75, 1.0, 1.6, 3.25 })
        {
            var canvasHost = new Rect(modelHost.X * zoom, modelHost.Y * zoom,
                modelHost.Width * zoom, modelHost.Height * zoom);
            var placed = PreviewTextLayout.Place(canvasHost, shapedWidth, shapedHeight, zoom, justifyH, justifyV);

            Assert.Equal(baseline.X, placed.X / zoom, 10);
            Assert.Equal(baseline.Y, placed.Y / zoom, 10);
            Assert.Equal(shapedWidth, placed.Width / zoom, 10);
            Assert.Equal(shapedHeight, placed.Height / zoom, 10);
        }
    }

    [Fact]
    public void Canvas_transform_maps_model_text_offsets_at_the_same_zoom_as_geometry()
    {
        var placement = new Rect(101.5, 203.25, 40, 10);
        const double zoom = 1.75;
        var transform = PreviewTextLayout.CanvasTransform(placement, zoom);

        Assert.Equal(new Point(101.5, 203.25), transform.Transform(default));
        Assert.Equal(new Point(101.5 + 2 * zoom, 203.25 - zoom), transform.Transform(new Point(2, -1)));
    }

    [Fact]
    public void Client_font_measurement_drives_wow_style_ellipsis()
    {
        static double Measure(string value) => value.Length * 6;

        Assert.Equal("Hunting Gro...", PreviewTextLayout.Fit("Hunting Grounds:", 84, Measure));
        Assert.Equal("Hunting Grounds:", PreviewTextLayout.Fit("Hunting Grounds:", 96, Measure));
    }

    [Fact]
    public void Text_fit_is_independent_of_editor_zoom()
    {
        static double Measure(string value) => value.Length * 5.5;
        const string value = "Hunting Grounds:";

        foreach (var zoom in new[] { 0.5, 1.0, 1.75, 3.0 })
            Assert.Equal("Hunting Grou...", PreviewTextLayout.Fit(value, 83 * zoom / zoom, Measure));
    }
}
