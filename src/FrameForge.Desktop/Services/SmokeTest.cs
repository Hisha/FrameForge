using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FrameForge.Core;
using FrameForge.Core.Geometry;
using FrameForge.Core.Serialization;
using FrameForge.Desktop.Controls;
using FrameForge.Desktop.ViewModels;
using FrameForge.Desktop.Views;

namespace FrameForge.Desktop.Services;

/// <summary>
/// The built-in application self-check.
/// </summary>
/// <remarks>
/// This is the Avalonia counterpart of the Electron v0.1 smoke test, and it carries over its
/// intent: do not trust a green compile, drive the real application. The check opens the real
/// window, then verifies that
/// <list type="number">
/// <item>the Native Hunts example resolves to the hand-computed golden bounds,</item>
/// <item>frame selection works from the canvas hit test,</item>
/// <item>inspector editing immediately re-resolves the layout,</item>
/// <item>dragging updates only the WoW offsets, in the correct sign, with anchors intact,</item>
/// <item>a save/load round trip through the on-disk format is lossless, and</item>
/// <item>the canvas actually paints (a PNG is written).</item>
/// </list>
/// It exits non-zero on the first failure so CI and the publish script can gate on it.
/// </remarks>
public static class SmokeTest
{
    /// <summary>
    /// Set by <see cref="Program"/> before Avalonia starts; consumed once the window exists.
    /// </summary>
    public static bool IsRequested { get; set; }

    /// <summary>Drives the live window, reports, then shuts the app down with the result code.</summary>
    public static void Run(IClassicDesktopStyleApplicationLifetime desktop) => _ = RunAsync(desktop);

    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var exitCode = 1;
        try
        {
            exitCode = await ExecuteAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SMOKE_FAIL unhandled: {ex}");
        }
        finally
        {
            desktop.Shutdown(exitCode);
        }
    }

    /// <remarks>
    /// The dispatcher is awaited rather than blocked on: this code runs ON the UI thread, so
    /// blocking it waiting for the dispatcher to drain its own queue would deadlock. Awaiting
    /// a low-priority operation yields back to the loop, which is what lets the window lay out
    /// and paint before it is inspected.
    /// </remarks>
    private static async Task<int> ExecuteAsync()
    {
        var failures = 0;
        var output = Environment.GetEnvironmentVariable("FRAMEFORGE_SMOKE_OUT")
                     ?? Path.Combine(AppContext.BaseDirectory, "frameforge-smoke.png");

        if (App.EditorWindow is not { } window || window.DataContext is not MainWindowViewModel vm)
        {
            Console.WriteLine("SMOKE_FAIL the editor window or its view model was not created.");
            return 1;
        }

        var canvas = window.FindControl<LayoutCanvas>("Canvas");
        if (canvas is null)
        {
            Console.WriteLine("SMOKE_FAIL the LayoutCanvas was not found in the window.");
            return 1;
        }

        window.Show();

        // Let the window go through measure, arrange and at least one render pass before the
        // canvas has a non-zero size to fit against.
        await PumpAsync(6);

        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine($"{(ok ? "SMOKE_OK  " : "SMOKE_FAIL")} {name}{(ok || detail is null ? string.Empty : "  -> " + detail)}");
            if (!ok)
                failures++;
        }

        // 1. The example loads and resolves to the golden geometry.
        vm.LoadNativeHuntsExample();
        var layout = vm.Layout;
        Check("example has six frames", vm.Project.Frames.Count == 6, $"got {vm.Project.Frames.Count}");
        Check("example layout is clean", layout.Issues.Count == 0,
            layout.Issues.Count == 0 ? null : layout.Issues[0].ToString());
        Check("Content top is 340", Near(layout.Rects["Content"].Top, 340));
        Check("Record bottom is -58", Near(layout.Rects["Record"].Bottom, -58));
        Check("Idle covers Identity",
            Near(layout.Rects["Idle"].Top, layout.Rects["Identity"].Top) && layout.Rects["Idle"].Bottom < layout.Rects["Identity"].Bottom);
        Check("canvas received the layout", canvas.Project == vm.Project && canvas.Layout == layout);


        // 2. Selection, driven through the canvas hit test rather than the view model.
        canvas.FitToContent();

        var origin = new CanvasOrigin(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2);
        var contentBox = canvas.Viewport.RectToCanvas(layout.Rects["Content"], origin);

        // Idle deliberately overlaps Identity, so the centre of Content belongs to whichever
        // frame is painted last. Sweep Content instead of trusting one coordinate: the sweep
        // must find both a point owned by Content alone and a point owned by the topmost child.
        var hits = new Dictionary<string, Point>();
        for (var y = contentBox.Y + 2; y < contentBox.Bottom - 2 && hits.Count < 8; y += 3)
        {
            for (var x = contentBox.X + 2; x < contentBox.Right - 2; x += 3)
            {
                var name = canvas.HitTestFrame(x, y);
                if (name is not null && !hits.ContainsKey(name))
                    hits[name] = new Point(x, y);
            }
        }

        Check("hit test finds Content where no child overlaps", hits.ContainsKey("Content"),
            $"hit {string.Join(", ", hits.Keys)}");
        Check("hit test prefers the topmost child", hits.ContainsKey("Idle"),
            $"hit {string.Join(", ", hits.Keys)}");

        if (hits.TryGetValue("Content", out var contentPoint))
        {
            vm.OnCanvasSelectionRequested("Content");
            Check("selection is Content", vm.SelectedName == "Content", $"selected {vm.SelectedName ?? "(none)"}");
            Check("inspector resolved geometry", vm.Editor.Resolved is not null && Near(vm.Editor.Resolved!.Value.Top, 340));
            Check("canvas selection followed", canvas.SelectedName == "Content");
            Check("hit point was inside Content",
                canvas.HitTestFrame(contentPoint.X, contentPoint.Y) == "Content");
        }

        // 3. Inspector editing re-resolves immediately.
        vm.Editor.Width = "300";
        Check("width edit re-resolved Content", Near(vm.Layout.Rects["Content"].Width, 300),
            $"width {vm.Layout.Rects["Content"].Width}");
        vm.Editor.Width = "296";
        Check("width restored", Near(vm.Layout.Rects["Content"].Width, 296));
        vm.Editor.OffsetX = "0";
        var leftAtZeroOffset = vm.Layout.Rects["Content"].Left;
        vm.Editor.OffsetX = "40";
        Check("offset edit moved Content right",
            Near(vm.Layout.Rects["Content"].Left, leftAtZeroOffset + 40),
            $"left {vm.Layout.Rects["Content"].Left}, expected {leftAtZeroOffset + 40}");
        vm.Editor.OffsetX = "12";
        Check("offset restored", Near(vm.Project.Find("Content")!.OffsetX, 12));

        // 4. Dragging: canvas pixels -> model delta -> OFFSETS ONLY. A pixel drag only means a
        // fixed number of WoW units once the zoom is known, so the expectation is derived from
        // the live zoom rather than hard-coded.
        var zoom = canvas.ZoomPercent / 100.0;
        var expectedDy = -25 / zoom;
        var before = vm.Project.Find("Content")!;
        var dragDown = canvas.ModelDeltaForDrag(0, 25);
        Check("dragging 25 canvas px down is a negative model Y", Near(dragDown.Dy, expectedDy, 1e-4),
            $"{dragDown.Dy} at zoom {zoom}");
        Check("dragging straight down is 0 model X", Near(dragDown.Dx, 0));
        vm.DragFrame("Content", dragDown.Dx, dragDown.Dy);
        var after = vm.Project.Find("Content")!;
        var expectedAfter = before.OffsetY + dragDown.Dy;
        Check("drag lowered offsetY", Near(after.OffsetY, expectedAfter),
            $"{before.OffsetY} + {dragDown.Dy} -> {after.OffsetY}");
        Check("drag left offsetX alone", Near(after.OffsetX, before.OffsetX));
        Check("drag preserved point", after.Point == before.Point);
        Check("drag preserved relativeTo", after.RelativeTo == before.RelativeTo);
        Check("drag preserved relativePoint", after.RelativePoint == before.RelativePoint);
        Check("drag preserved parent", after.Parent == before.Parent);
        var topAfterDrag = 340 + dragDown.Dy;
        Check("layout followed the drag", Near(vm.Layout.Rects["Content"].Top, topAfterDrag),
            $"{vm.Layout.Rects["Content"].Top} vs {topAfterDrag}");

        // Restore, then check the JSON round trip.
        vm.DragFrame("Content", 0, -expectedDy);
        Check("drag undone", Near(vm.Project.Find("Content")!.OffsetY, before.OffsetY),
            vm.Project.Find("Content")!.OffsetY.ToString());

        var roundTripPath = Path.Combine(Path.GetTempPath(), $"frameforge-smoke-{Guid.NewGuid():N}.fforge.json");
        try
        {
            Check("save succeeded", vm.SaveToFile(roundTripPath));
            var text = File.ReadAllText(roundTripPath);
            var parsed = ProjectCodec.Parse(text);
            Check("saved file parses", parsed.Ok, parsed.ErrorText);
            Check("round trip is lossless", parsed.Project == vm.Project);
            Check("saved file is WoW data, not canvas data", !text.Contains("canvas", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(roundTripPath))
                File.Delete(roundTripPath);
        }

        // 5. The canvas really paints.
        vm.Select("Identity");
        canvas.SelectedName = "Identity";
        canvas.InvalidateVisual();
        await PumpAsync(2);

        var width = (int)Math.Max(1, Math.Round(canvas.Bounds.Width));
        var height = (int)Math.Max(1, Math.Round(canvas.Bounds.Height));
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(canvas);
        bitmap.Save(output, new PngBitmapEncoderOptions());

        var painted = CountNonBackgroundPixels(bitmap);
        Check("canvas rendered a non-blank image", painted > 500, $"{painted} non-background pixels");
        Check("screenshot written", File.Exists(output), output);

        Console.WriteLine($"SMOKE_RESULT {{\"ok\":{failures == 0}," +
                          $"\"frames\":{vm.Project.Frames.Count}," +
                          $"\"contentTop\":{Number(vm.Layout.Rects["Content"].Top)}," +
                          $"\"recordBottom\":{Number(vm.Layout.Rects["Record"].Bottom)}," +
                          $"\"zoom\":{Number(canvas.ZoomPercent / 100)}," +
                          $"\"paintedPixels\":{painted}," +
                          $"\"screenshot\":\"{output}\"}}");
        Console.WriteLine(failures == 0 ? "SMOKE_PASS" : $"SMOKE_FAIL {failures} check(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Yields to the dispatcher <paramref name="times"/> times at background priority.</summary>
    private static async Task PumpAsync(int times)
    {
        for (var i = 0; i < times; i++)
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static bool Near(double actual, double expected, double tolerance = 5e-4) =>
        Math.Abs(actual - expected) <= tolerance;

    private static string Number(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Counts pixels that are not the canvas background, proving something was drawn.</summary>
    private static int CountNonBackgroundPixels(RenderTargetBitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(stride * size.Height);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), buffer, stride * size.Height, stride);

            var count = 0;
            for (var i = 0; i < stride * size.Height; i += 4)
            {
                var pixel = System.Runtime.InteropServices.Marshal.ReadByte(buffer, i);
                var green = System.Runtime.InteropServices.Marshal.ReadByte(buffer, i + 1);
                var blue = System.Runtime.InteropServices.Marshal.ReadByte(buffer, i + 2);

                // The canvas background is #0C1116; anything else means something was drawn.
                if (pixel != 0x0C || green != 0x11 || blue != 0x16)
                    count++;
            }

            return count;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }
}