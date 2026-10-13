using FrameForge.Core.Viewing;
using Xunit;

namespace FrameForge.Core.Tests;

public sealed class VisualLandmarkComparisonTests
{
    [Fact]
    public void Shared_stock_landmarks_remove_only_translation_and_uniform_scale()
    {
        var samples = new[]
        {
            Sample("stock top-left", 0, 0, reference: true),
            Sample("stock top-right", 355, 0, reference: true),
            Sample("stock lower-left", 0, 410, reference: true),
            Sample("record border", 31, 58, previewDx: 3.5, clientDx: -1.25),
            Sample("idle border", 8, 156, previewDy: 2, clientDy: -0.5),
        };

        var report = VisualLandmarkComparison.Compare(samples);

        Assert.Equal(1.5, report.Preview.PixelsPerUiUnit, 10);
        Assert.Equal(1.1, report.Client.PixelsPerUiUnit, 10);
        var record = report.Landmarks.Single(item => item.Name == "record border");
        Assert.Equal(3.5, record.PreviewErrorX, 10);
        Assert.Equal(-1.25, record.ClientErrorX, 10);
        Assert.Equal(4.75, record.PreviewMinusClientX, 10);
        var idle = report.Landmarks.Single(item => item.Name == "idle border");
        Assert.Equal(2, idle.PreviewErrorY, 10);
        Assert.Equal(-0.5, idle.ClientErrorY, 10);
    }

    [Fact]
    public void Aspect_distortion_remains_visible_as_residual_error()
    {
        var samples = new[]
        {
            new VisualLandmarkSample("left", 0, 0, 10, 20, 5, 7, true),
            new VisualLandmarkSample("right", 100, 0, 210, 20, 105, 7, true),
            new VisualLandmarkSample("lower", 0, 100, 10, 170, 5, 107),
        };

        var report = VisualLandmarkComparison.Compare(samples);
        var lower = report.Landmarks.Single(item => item.Name == "lower");

        Assert.Equal(-25, lower.PreviewErrorY, 10);
        Assert.Equal(0, lower.ClientErrorY, 10);
    }

    [Fact]
    public void Requires_two_non_coincident_reference_landmarks()
    {
        Assert.Throws<ArgumentException>(() => VisualLandmarkComparison.Compare(
            [new VisualLandmarkSample("one", 0, 0, 0, 0, 0, 0, true)]));
    }

    private static VisualLandmarkSample Sample(
        string name, double uiX, double uiY, bool reference = false,
        double previewDx = 0, double previewDy = 0, double clientDx = 0, double clientDy = 0) =>
        new(name, uiX, uiY,
            40 + (uiX + previewDx) * 1.5, 70 + (uiY + previewDy) * 1.5,
            13 + (uiX + clientDx) * 1.1, 29 + (uiY + clientDy) * 1.1,
            reference);
}
