using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FrameForge.Core;
using FrameForge.Core.Geometry;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
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
/// <item>the canvas actually paints (a PNG is written), and</item>
/// <item>the real NativeHuntsFrame.xml imports with its source file untouched, its findings
/// surfaced, and its stand-in frame kept.</item>
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

        // Captured for the result line at the end, because vm holds the imported project by then.
        var exampleFrames = vm.Project.Frames.Count;
        var exampleContentTop = layout.Rects["Content"].Top;
        var exampleRecordBottom = layout.Rects["Record"].Bottom;
        Check("Idle covers Identity",
            Near(layout.Rects["Idle"].Top, layout.Rects["Identity"].Top) && layout.Rects["Idle"].Bottom < layout.Rects["Identity"].Bottom);
        Check("canvas received the layout", canvas.Project == vm.Project && canvas.Layout == layout);

        // A hand-authored project has no external source, so there is nothing to disclose. If the
        // provenance box ever shows up here it is claiming a file that does not exist.
        vm.Select("Content");
        Check("a hand-authored frame claims no import origin", vm.Editor.SourceNote.Length == 0,
            vm.Editor.SourceNote);


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

        // 6. The real FrameXML file, imported through the same path the Open button uses.
        //    This is the acceptance test for Phase 2: the packaged copy of the actual
        //    NativeHuntsFrame.xml goes in, and nothing about the source file may change.
        var xmlFixture = Path.Combine(Path.GetTempPath(), $"frameforge-smoke-{Guid.NewGuid():N}.xml");
        var xmlSource = ExtractPackagedFixture(xmlFixture);
        var xmlDigestBefore = Sha256(xmlSource);
        var imported = 0;
        var partial = 0;
        var unsupported = 0;
        var errors = 0;
        var warnings = 0;

        try
        {
            vm.ImportFromFile(xmlFixture);

            var source = vm.Project.Source;
            Check("import recorded a read-only XML source",
                source is { ReadOnly: true } && source.Type == SourceTypes.WowFrameXml,
                source?.Type ?? "(none)");
            Check("import set no project path, so Save must go to Save As",
                string.IsNullOrEmpty(vm.ProjectPath), vm.ProjectPath ?? "(null)");
            Check("source file was not modified by the import",
                Sha256(xmlFixture) == xmlDigestBefore);

            // The counts the importer reports, restated independently from the project itself:
            // FrameForge must not be able to pass this by miscounting its own output.
            var last = vm.LastImport;
            Check("the import result was kept for inspection", last is not null);
            imported = last?.Elements.Count ?? 0;
            partial = last?.PartiallySupported ?? -1;
            unsupported = last?.Unsupported ?? -1;
            errors = vm.ImportDiagnostics.Count(d => d.Severity is FrameXmlSeverity.Error);
            warnings = vm.ImportDiagnostics.Count(d => d.Severity is FrameXmlSeverity.Warning);

            Check("all 51 layout elements were discovered", imported == 51, $"got {imported}");
            Check("exactly one stand-in frame was synthesized",
                vm.Project.Frames.Count(f => f.Placeholder) == 1);
            Check("LFDParentFrame is the stand-in",
                vm.Project.Find("LFDParentFrame") is { Placeholder: true });
            Check("no element was dropped entirely", unsupported == 0, $"{unsupported} unsupported");
            Check("9 elements are partially supported", partial == 9, $"got {partial}");
            Check("the import produced no errors", errors == 0, $"{errors} error(s)");
            Check("the import reported its findings", warnings > 0, $"got {warnings} warning(s)");
            Check("findings are shown in the UI", vm.HasImportDiagnostics && vm.HasSource);

            Check("the imported layout is clean", vm.Layout.Issues.Count == 0,
                vm.Layout.Issues.Count == 0 ? null : vm.Layout.Issues[0].ToString());
            Check("every frame positioned",
                vm.Layout.Rects.Count == vm.Project.Frames.Count,
                $"{vm.Layout.Rects.Count} of {vm.Project.Frames.Count}");

            // The placeholder is a stand-in, so its size is FrameForge's estimate and must be
            // labelled as such rather than presented as something the file said.
            Check("stand-in geometry is labelled as a stand-in",
                vm.Project.Find("LFDParentFrame") is { Placeholder: true } placeholder
                && placeholder.Width > 0 && placeholder.Height > 0,
                "placeholder must have a usable size");

            Check("a text widget with no declared size imported",
                vm.Project.Frames.Any(f => f.Kind is FrameKind.FONTSTRING && f.Anonymous),
                "expected an anonymous FontString");

            // Provenance is the whole point of importing: the inspector has to be able to say a
            // FontString was unnamed and sized by a font template, or "0 x 0" in the width box is
            // indistinguishable from a mistake the user made.
            var anonymous = vm.Project.Frames.First(f => f.Anonymous);
            vm.OnCanvasSelectionRequested(anonymous.Name);
            Check("the inspector explains a generated identity",
                vm.Editor.SourceNote.Contains("unnamed", StringComparison.OrdinalIgnoreCase),
                vm.Editor.SourceNote);
            Check("the inspector says which widget it is",
                vm.Editor.SourceNote.Contains(anonymous.Kind.TagName(), StringComparison.Ordinal),
                vm.Editor.SourceNote);
            vm.OnCanvasSelectionRequested("LFDParentFrame");
            Check("the inspector flags the stand-in as an estimate",
                vm.Editor.SourceNote.Contains("Stand-in", StringComparison.Ordinal) &&
                vm.Editor.IsPlaceholder,
                vm.Editor.SourceNote);

            // Editing an imported frame must work and must not reach back into the XML.
            vm.OnCanvasSelectionRequested("LFDParentFrame");
            vm.Editor.Width = "400";
            Check("an imported frame is editable",
                vm.Project.Find("LFDParentFrame")!.Width == 400);
            Check("editing did not touch the source file",
                Sha256(xmlFixture) == xmlDigestBefore);
            vm.Editor.Width = "355";
        }
        finally
        {
            if (File.Exists(xmlFixture))
                File.Delete(xmlFixture);
        }

        // Saving an imported project must produce a FrameForge project and must refuse the XML.
        var xmlSaveTarget = Path.Combine(Path.GetTempPath(), $"frameforge-smoke-{Guid.NewGuid():N}.xml");
        var jsonSaveTarget = Path.Combine(Path.GetTempPath(), $"frameforge-smoke-{Guid.NewGuid():N}.fforge.json");
        try
        {
            Check("save refuses an .xml target", !vm.SaveToFile(xmlSaveTarget));
            Check("refused save wrote nothing", !File.Exists(xmlSaveTarget));
            Check("CanSaveTo rejects .xml", !ProjectCodec.CanSaveTo("anything.xml"));

            Check("imported project saves as .fforge.json", vm.SaveToFile(jsonSaveTarget));
            Check("saved the project file",
                File.Exists(jsonSaveTarget) && new FileInfo(jsonSaveTarget).Length > 0);

            var reloaded = ProjectCodec.Parse(File.ReadAllText(jsonSaveTarget));
            Check("saved project parses", reloaded.Ok, reloaded.ErrorText);

            // Guarded rather than defaulted: falling back to the in-memory project would make
            // every check below compare a project against itself and pass.
            if (reloaded.Project is { } saved)
            {
                Check("import provenance survived the round trip",
                    saved.Source is { ReadOnly: true, Type: SourceTypes.WowFrameXml });
                Check("stand-in survived the round trip",
                    saved.Frames.Count(f => f.Placeholder) == 1);
                Check("every frame survived the round trip",
                    saved.Frames.Count == vm.Project.Frames.Count,
                    $"{saved.Frames.Count} vs {vm.Project.Frames.Count}");
                Check("geometry survived the round trip",
                    LayoutResolver.Resolve(saved).Rects.Count == vm.Layout.Rects.Count,
                    $"{LayoutResolver.Resolve(saved).Rects.Count} vs {vm.Layout.Rects.Count}");
            }
            else
            {
                Check("saved project round trips", false, reloaded.ErrorText);
            }
        }
        finally
        {
            foreach (var path in new[] { xmlSaveTarget, jsonSaveTarget })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        // 7. Paint the imported layout too: the anchor overlay and the stand-in marker only run
        //    for imported geometry, so a screenshot of the example proves nothing about them.
        vm.OnCanvasSelectionRequested("LFDParentFrame");
        canvas.SelectedName = "LFDParentFrame";
        canvas.FitToContent();

        // Fit must frame the widgets the user can actually see. NativeHuntsFrameInitializer is a
        // zero-area widget parked at the UIParent corner; including it would fit a box roughly
        // twice the size of the layout and leave the content filling half the canvas. Measured
        // through the viewport rather than asserted as a magic number.
        // The box Fit should have framed: every widget with real area, in model units.
        var boxes = vm.Layout.Frames.Values
            .Select(f => f.Rect)
            .Where(r => r is { } v && v.Width > 0 && v.Height > 0)
            .Select(r => r!.Value)
            .ToArray();
        var modelBox = new FrameRect(
            boxes.Min(b => b.Left), boxes.Max(b => b.Top),
            boxes.Max(b => b.Right), boxes.Min(b => b.Bottom));

        var fitted = canvas.Viewport.RectToCanvas(modelBox, origin);
        var view = new Rect(0, 0, canvas.Bounds.Width, canvas.Bounds.Height);
        var fittedRect = new Rect(fitted.X, fitted.Y, fitted.Width, fitted.Height);
        // FitToContent leaves a 40px margin, so the framed box must fit inside the margin box and
        // fill one axis of it. Which axis is not asserted: this layout is taller than it is wide,
        // so height is the limiting dimension and demanding width be filled too would be wrong.
        var marginBox = view.Deflate(40);
        Check("Fit framed the visible widgets", marginBox.Contains(fittedRect),
            $"box {fittedRect.Width:F0}x{fittedRect.Height:F0} in usable {marginBox.Width:F0}x{marginBox.Height:F0}");
        Check("Fit used the available space",
            fittedRect.Width > marginBox.Width * 0.9 || fittedRect.Height > marginBox.Height * 0.9,
            $"box {fittedRect.Width:F0}x{fittedRect.Height:F0} in usable {marginBox.Width:F0}x{marginBox.Height:F0}");

        var initializer = new Rect(
            canvas.Viewport.ModelToCanvasX(-512, origin) - 3,
            canvas.Viewport.ModelToCanvasY(384, origin) - 3, 6, 6);
        Check("Fit ignored the zero-area initializer",
            !view.Intersects(initializer),
            "the initializer sits outside the fitted view, so it did not drive the fit");
        Check("Fit did not shrink to fit the screen corner",
            canvas.ZoomPercent > 100 * 0.9,
            $"zoom {canvas.ZoomPercent:F1}%");

        // ---- Clipping: the canvas is a viewport, not a hole in the window -----------------
        // Regression guard for canvas ink escaping over the toolbar, import banner, tree,
        // inspector and status bar. Everything below compares whole-window renders.

        Check("canvas clips its rendering to its bounds", canvas.ClipToBounds);

        var root = window.FindControl<Grid>("RootGrid");
        Check("the window root grid is reachable", root is not null);
        if (root is null)
            return failures == 0 ? 0 : 1;

        // 1. The layout must actually give the canvas the centre pane and nothing else.
        var canvasRect = canvas.TranslatePoint(new Point(0, 0), root) is { } tl
            ? new Rect(tl, canvas.Bounds.Size)
            : new Rect(canvas.Bounds.Size);

        Check("canvas has sane non-zero bounds",
            canvas.Bounds.Width > 200 && canvas.Bounds.Height > 200,
            $"{canvas.Bounds.Width:F0}x{canvas.Bounds.Height:F0}");
        Check("canvas starts right of the frame tree",
            canvasRect.Left >= 260, $"left {canvasRect.Left:F0}");
        Check("canvas stops left of the inspector",
            canvasRect.Right <= root.Bounds.Width - 320, $"right {canvasRect.Right:F0} of {root.Bounds.Width:F0}");
        Check("canvas starts below the toolbar and import banner",
            canvasRect.Top >= root.Bounds.Height - 320 - canvasRect.Height,
            $"top {canvasRect.Top:F0}, height {canvasRect.Height:F0}, window {root.Bounds.Height:F0}");
        Check("canvas does not reach the status bar",
            canvasRect.Bottom <= root.Bounds.Height,
            $"bottom {canvasRect.Bottom:F0} of {root.Bounds.Height:F0}");

        // 2. Prove the escape exists in this exact state, so the diff below is meaningful. The
        //    zero-area initializer sits at the UIParent corner, far outside the fitted box, and
        //    its marker and label are drawn there.
        var escapeHappensHere = !view.Intersects(initializer);
        Check("this state has content outside the canvas bounds", escapeHappensHere,
            $"initializer marker at {initializer.X:F0},{initializer.Y:F0} in view {view.Width:F0}x{view.Height:F0}");

        // 3. Diff the whole window with the canvas hidden against the same window with it shown.
        var windowWidth = (int)Math.Max(1, Math.Round(root.Bounds.Width));
        var windowHeight = (int)Math.Max(1, Math.Round(root.Bounds.Height));

        canvas.IsVisible = false;
        canvas.InvalidateVisual();
        await PumpAsync(2);
        var withoutCanvas = new RenderTargetBitmap(new PixelSize(windowWidth, windowHeight), new Vector(96, 96));
        withoutCanvas.Render(root);

        canvas.IsVisible = true;
        canvas.SelectedName = "LFDParentFrame";
        canvas.InvalidateVisual();
        await PumpAsync(2);
        var withCanvas = new RenderTargetBitmap(new PixelSize(windowWidth, windowHeight), new Vector(96, 96));
        withCanvas.Render(root);

        var escaped = CountPixelsChangedOutside(withoutCanvas, withCanvas, canvasRect, out var paintedInside);
        Check("the canvas actually painted inside its bounds", paintedInside > 1000,
            $"{paintedInside} pixels changed inside {canvasRect.Width:F0}x{canvasRect.Height:F0}");
        Check("NO canvas ink escaped outside its bounds", escaped == 0,
            $"{escaped} pixels changed outside the canvas rect (toolbar, banner, tree, inspector, status bar)");

        canvas.InvalidateVisual();
        await PumpAsync(2);

        var importedOutput = Path.Combine(
            Path.GetDirectoryName(output) ?? ".", $"frameforge-smoke-import{Path.GetExtension(output)}");
        var importedWidth = (int)Math.Max(1, Math.Round(canvas.Bounds.Width));
        var importedHeight = (int)Math.Max(1, Math.Round(canvas.Bounds.Height));
        var importedBitmap = new RenderTargetBitmap(new PixelSize(importedWidth, importedHeight), new Vector(96, 96));
        importedBitmap.Render(canvas);
        importedBitmap.Save(importedOutput, new PngBitmapEncoderOptions());

        var importedPainted = CountNonBackgroundPixels(importedBitmap);
        Check("imported layout rendered a non-blank image", importedPainted > 500,
            $"{importedPainted} non-background pixels");
        Check("imported screenshot written", File.Exists(importedOutput), importedOutput);

        Console.WriteLine($"SMOKE_IMPORT {{\"elements\":{imported}," +
                          $"\"frames\":{vm.Project.Frames.Count}," +
                          $"\"partial\":{partial}," +
                          $"\"unsupported\":{unsupported}," +
                          $"\"warnings\":{warnings}," +
                          $"\"errors\":{errors}," +
                          $"\"rects\":{vm.Layout.Rects.Count}," +
                          $"\"paintedPixels\":{importedPainted}," +
                          $"\"screenshot\":\"{importedOutput}\"}}");

        // 10. The Open picker contract. Last, because re-opening replaces vm's project.
        //
        //     The Linux Open dialog is the XDG desktop portal: Avalonia hands it the whole filter
        //     list once and a remote backend owns the combo from then on, so the FIRST entry is
        //     the dialog's default and is the only part of this a client actually controls.
        //     Rebuild the exact FilePickerFileType[] MainWindow.OnOpenClick sends.
        var pickerTypes = ProjectCodec.OpenDialogFilters
            .Select(f => new FilePickerFileType(f.Label) { Patterns = [.. f.Patterns] })
            .ToArray();

        Check("picker offers four file types", pickerTypes.Length == 4,
            string.Join(" | ", pickerTypes.Select(t => t.Name)));
        Check("picker default is All Supported Files",
            pickerTypes.Length > 0 && pickerTypes[0].Name == "All Supported Files",
            pickerTypes.FirstOrDefault()?.Name ?? "<none>");
        var Globs = (FilePickerFileType t) => t.Patterns?.ToArray() ?? [];
        Check("picker default shows .fforge.json and .xml",
            pickerTypes.Length > 0
            && Globs(pickerTypes[0]).Contains("*.fforge.json")
            && Globs(pickerTypes[0]).Contains("*.xml"),
            string.Join(",", pickerTypes.FirstOrDefault() is { } f ? Globs(f) : []));
        Check("picker still offers the narrow project filter",
            pickerTypes.Any(t => t.Name.Contains("FrameForge Projects", StringComparison.Ordinal)
                && Globs(t).Contains("*.fforge.json")));
        Check("picker still offers the narrow FrameXML filter",
            pickerTypes.Any(t => t.Name.Contains("WoW FrameXML", StringComparison.Ordinal)
                && Globs(t).Contains("*.xml")));
        Check("picker still offers All Files",
            pickerTypes.Any(t => t.Name.Contains("All Files", StringComparison.Ordinal)
                && Globs(t).Contains("*.*")));
        Check("every picker type has a label and patterns",
            pickerTypes.All(t => !string.IsNullOrWhiteSpace(t.Name) && Globs(t).Length > 0));

        // 11. Both formats still open through OpenFromFile, which is what the picker calls.
        var importedFrameCount = vm.Project.Frames.Count;
        var reopenJson = Path.Combine(Path.GetTempPath(), $"frameforge-smoke-{Guid.NewGuid():N}.fforge.json");
        var reopenXml = Path.Combine(Path.GetTempPath(), $"frameforge-smoke-{Guid.NewGuid():N}.xml");
        try
        {
            Check("re-open fixture saves as a project", vm.SaveToFile(reopenJson));

            // xmlFixture was cleaned up after the import section, so re-materialise the packaged
            // copy of the real file rather than depending on a deleted temp path.
            Check("re-open fixture materialised the XML again",
                Sha256(ExtractPackagedFixture(reopenXml)) == xmlDigestBefore);

            // A project open sets ProjectPath; an XML import deliberately does not. That is the
            // discriminator between the two readers, not Project.Source, which correctly survives
            // the save and records where the geometry originally came from.
            vm.OpenFromFile(reopenJson);
            Check("OpenFromFile opens a .fforge.json project",
                vm.Project.Frames.Count > 0 && vm.ProjectPath == reopenJson,
                $"{vm.Project.Frames.Count} frames, path {vm.ProjectPath ?? "(null)"}");

            vm.OpenFromFile(reopenXml);
            Check("OpenFromFile opens a .xml document as a read-only import",
                vm.Project.Source is { ReadOnly: true, Type: SourceTypes.WowFrameXml }
                && vm.Source is { ImportFailed: false },
                $"source {vm.Project.Source?.Type.ToString() ?? "none"} readonly {vm.Project.Source?.ReadOnly}");
            Check("re-opened XML import is still complete",
                vm.Project.Frames.Count == importedFrameCount,
                $"{vm.Project.Frames.Count} vs {importedFrameCount}");
            Check("re-opened XML left the addon file untouched",
                Sha256(reopenXml) == xmlDigestBefore);
            Check("re-opened XML refuses to overwrite itself", !vm.SaveToFile(reopenXml));
        }
        finally
        {
            foreach (var path in new[] { reopenJson, reopenXml })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        // The example geometry is reported from values captured while the example was loaded.
        // vm now holds the IMPORTED project, so reading vm.Layout here would report the imported
        // document's numbers under the example's label.
        Console.WriteLine($"SMOKE_RESULT {{\"ok\":{failures == 0}," +
                          $"\"exampleFrames\":{exampleFrames}," +
                          $"\"contentTop\":{Number(exampleContentTop)}," +
                          $"\"recordBottom\":{Number(exampleRecordBottom)}," +
                          $"\"zoom\":{Number(canvas.ZoomPercent / 100)}," +
                          $"\"paintedPixels\":{painted}," +
                          $"\"screenshot\":\"{output}\"}}");
        Console.WriteLine(failures == 0 ? "SMOKE_PASS" : $"SMOKE_FAIL {failures} check(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Copies the FrameXML fixture packaged into the application out to a real file on disk.
    /// </summary>
    /// <remarks>
    /// It is written to disk rather than imported from memory on purpose: the importer's central
    /// promise is that it READS a file and leaves it alone, and the only honest way to check that
    /// is to hand it an actual file and hash the bytes afterwards. The packaged copy is linked
    /// from the single canonical fixture in the test project, so this cannot drift away from what
    /// the unit tests assert on.
    /// </remarks>
    private static string ExtractPackagedFixture(string destination)
    {
        using var stream = AssetLoader.Open(
            new Uri("avares://FrameForge/Assets/Fixtures/NativeHuntsFrame.xml"));
        using var file = File.Create(destination);
        stream.CopyTo(file);
        return destination;
    }

    /// <summary>Hex SHA-256 of a file, so "the source was not modified" is measured, not asserted.</summary>
    private static string Sha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path))).ToLowerInvariant();
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

    /// <summary>
    /// Counts pixels OUTSIDE <paramref name="canvasRect"/> that changed between two renders of the
    /// whole window: one with the canvas hidden and one with it visible.
    /// </summary>
    /// <remarks>
    /// This is the assertion that a bitmap of the canvas alone can never make. Rendering only the
    /// canvas produces a bitmap of exactly the canvas bounds, so overflow is invisible by
    /// construction. Rendering the window ROOT and diffing against a render with the canvas hidden
    /// detects ink anywhere else on screen - over the toolbar, the import banner, the frame tree,
    /// the inspector or the status bar - without any assumption about which colours the canvas
    /// uses. A count of zero is the containment guarantee, stated as a measurement.
    /// </remarks>
    private static int CountPixelsChangedOutside(
        RenderTargetBitmap hidden,
        RenderTargetBitmap shown,
        Rect canvasRect,
        out int changedInside)
    {
        var size = hidden.PixelSize;
        var stride = size.Width * 4;
        var bytes = stride * size.Height;
        var a = Marshal.AllocHGlobal(bytes);
        var b = Marshal.AllocHGlobal(bytes);

        try
        {
            hidden.CopyPixels(new PixelRect(size), a, bytes, stride);
            shown.CopyPixels(new PixelRect(size), b, bytes, stride);

            var outside = 0;
            changedInside = 0;

            for (var y = 0; y < size.Height; y++)
            {
                for (var x = 0; x < size.Width; x++)
                {
                    var i = y * stride + x * 4;
                    var same =
                        Marshal.ReadByte(a, i) == Marshal.ReadByte(b, i) &&
                        Marshal.ReadByte(a, i + 1) == Marshal.ReadByte(b, i + 1) &&
                        Marshal.ReadByte(a, i + 2) == Marshal.ReadByte(b, i + 2) &&
                        Marshal.ReadByte(a, i + 3) == Marshal.ReadByte(b, i + 3);

                    if (same)
                        continue;

                    if (x >= canvasRect.Left && x < canvasRect.Right &&
                        y >= canvasRect.Top && y < canvasRect.Bottom)
                        changedInside++;
                    else
                        outside++;
                }
            }

            return outside;
        }
        finally
        {
            Marshal.FreeHGlobal(a);
            Marshal.FreeHGlobal(b);
        }
    }

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