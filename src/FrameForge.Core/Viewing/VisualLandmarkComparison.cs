namespace FrameForge.Core.Viewing;

/// <summary>
/// One landmark observed in a FrameForge capture and in a WoW capture. UI coordinates use a
/// shared, y-down local system (normally LFDParentFrame's top-left), while observed coordinates
/// are screenshot pixels.
/// </summary>
public sealed record VisualLandmarkSample(
    string Name,
    double UiX,
    double UiY,
    double PreviewX,
    double PreviewY,
    double ClientX,
    double ClientY,
    bool Reference = false);

public sealed record ScreenshotUiTransform(double PixelsPerUiUnit, double TranslateX, double TranslateY)
{
    public (double X, double Y) ToUi(double x, double y) =>
        ((x - TranslateX) / PixelsPerUiUnit, (y - TranslateY) / PixelsPerUiUnit);
}

public sealed record VisualLandmarkResidual(
    string Name,
    bool Reference,
    double PreviewErrorX,
    double PreviewErrorY,
    double ClientErrorX,
    double ClientErrorY,
    double PreviewMinusClientX,
    double PreviewMinusClientY);

public sealed record VisualLandmarkReport(
    ScreenshotUiTransform Preview,
    ScreenshotUiTransform Client,
    IReadOnlyList<VisualLandmarkResidual> Landmarks);

/// <summary>
/// Removes screenshot translation and uniform scale using shared stock landmarks, then reports
/// residuals in WoW UI units. It deliberately does not permit independent X/Y scaling: a result
/// cannot hide aspect-ratio distortion behind screenshot normalization.
/// </summary>
public static class VisualLandmarkComparison
{
    public static VisualLandmarkReport Compare(IReadOnlyList<VisualLandmarkSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var references = samples.Where(sample => sample.Reference).ToArray();
        if (references.Length < 2)
            throw new ArgumentException("At least two stock reference landmarks are required.", nameof(samples));

        var preview = Fit(references, sample => (sample.PreviewX, sample.PreviewY));
        var client = Fit(references, sample => (sample.ClientX, sample.ClientY));
        var residuals = samples.Select(sample =>
        {
            var previewUi = preview.ToUi(sample.PreviewX, sample.PreviewY);
            var clientUi = client.ToUi(sample.ClientX, sample.ClientY);
            return new VisualLandmarkResidual(
                sample.Name,
                sample.Reference,
                previewUi.X - sample.UiX,
                previewUi.Y - sample.UiY,
                clientUi.X - sample.UiX,
                clientUi.Y - sample.UiY,
                previewUi.X - clientUi.X,
                previewUi.Y - clientUi.Y);
        }).ToArray();
        return new VisualLandmarkReport(preview, client, residuals);
    }

    private static ScreenshotUiTransform Fit(
        IReadOnlyList<VisualLandmarkSample> references,
        Func<VisualLandmarkSample, (double X, double Y)> observed)
    {
        var meanUiX = references.Average(item => item.UiX);
        var meanUiY = references.Average(item => item.UiY);
        var points = references.Select(observed).ToArray();
        var meanX = points.Average(item => item.X);
        var meanY = points.Average(item => item.Y);
        var numerator = 0d;
        var denominator = 0d;
        for (var i = 0; i < references.Count; i++)
        {
            var uiX = references[i].UiX - meanUiX;
            var uiY = references[i].UiY - meanUiY;
            numerator += uiX * (points[i].X - meanX) + uiY * (points[i].Y - meanY);
            denominator += uiX * uiX + uiY * uiY;
        }
        if (denominator <= 1e-12)
            throw new ArgumentException("Reference landmarks must span a non-zero UI distance.", nameof(references));
        var scale = numerator / denominator;
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentException("Reference landmarks do not define a positive uniform screenshot scale.", nameof(references));
        return new ScreenshotUiTransform(scale, meanX - scale * meanUiX, meanY - scale * meanUiY);
    }
}
