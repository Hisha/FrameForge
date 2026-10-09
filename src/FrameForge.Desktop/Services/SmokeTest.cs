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
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Controls;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Preview;
using FrameForge.Desktop.Inspection;
using FrameForge.Desktop.Rendering;
using FrameForge.Desktop.Templates;
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

        Check("window has packaged application icon", window.Icon is not null);
        Check("normal launch opens a schema-v2 project", vm.IsV2Project && !vm.IsV1Project);
        Check("normal launch hides legacy view controls",
            window.FindControl<Border>("LegacyViewControls")?.IsVisible == false);
        Check("normal launch shows the v2 control palette",
            window.FindControl<Expander>("V2ControlPalette")?.IsVisible == true);
        Check("normal launch shows the v2 inspector",
            window.FindControl<StackPanel>("V2Inspector")?.IsVisible == true);

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

        // Phase 3 view policy and pipeline: the same canvas uses ordered content/overlay bands.
        var pipeline = canvas.Pipeline;
        Check("render pipeline is content, debug, selection",
            pipeline.Layers.Select(l => l.Name).SequenceEqual(
                ["visual-content", "debug-overlay", "selection-overlay"]));
        Check("Preview skips the global debug overlay",
            pipeline.For(CanvasViewMode.PREVIEW).Select(l => l.Name).SequenceEqual(
                ["visual-content", "selection-overlay"]));
        Check("Hybrid skips the global debug overlay",
            pipeline.For(CanvasViewMode.HYBRID).Select(l => l.Name).SequenceEqual(
                ["visual-content", "selection-overlay"]));

        var projectCountBeforeViewChanges = vm.Project.Frames.Count;
        vm.SetViewMode(CanvasViewMode.PREVIEW);
        Check("Preview defaults to no labels", vm.LabelPolicy == LabelPolicy.NONE);
        Check("Preview defaults hide helpers and hidden widgets",
            !vm.CanvasFilter.HasFlag(VisibilityFilter.HELPERS)
            && !vm.CanvasFilter.HasFlag(VisibilityFilter.HIDDEN));
        Check("canvas followed Preview mode", canvas.Mode == CanvasViewMode.PREVIEW);

        vm.SetViewMode(CanvasViewMode.HYBRID);
        Check("Hybrid defaults to selected labels", vm.LabelPolicy == LabelPolicy.SELECTED);
        Check("canvas followed Hybrid mode", canvas.Mode == CanvasViewMode.HYBRID);

        vm.SetCategoryVisible(VisibilityFilter.TEXT, false);
        Check("Text visibility toggle updates only the canvas filter",
            !vm.CanvasFilter.HasFlag(VisibilityFilter.TEXT)
            && vm.Project.Frames.Count == projectCountBeforeViewChanges);
        vm.ResetViewToModeDefaults();

        vm.TreeFilter = TreeFilter.VISUAL;
        var visualTreeCount = CountTreeNodes(vm.TreeRoots);
        Check("Visual tree filter can hide a structure-only project",
            visualTreeCount == 0 && vm.Project.Frames.Count == projectCountBeforeViewChanges,
            $"{visualTreeCount} of {projectCountBeforeViewChanges}");
        vm.TreeFilter = TreeFilter.ALL;
        vm.TreeSearch = "IDLE";
        Check("tree search is case-insensitive and keeps the match",
            FlattenTree(vm.TreeRoots).Any(n => n.Name.Contains("Idle", StringComparison.Ordinal)));
        vm.TreeSearch = string.Empty;

        vm.SetViewMode(CanvasViewMode.DEBUG);
        Check("Debug restores all visibility and labels",
            vm.CanvasFilter == VisibilityFilter.ALL && vm.LabelPolicy == LabelPolicy.ALL);
        Check("view operations did not edit the project", vm.Project.Frames.Count == projectCountBeforeViewChanges);

        // A hand-authored project has no external source, so there is nothing to disclose. If the
        // provenance box ever shows up here it is claiming a file that does not exist.
        vm.Select("Content");
        Check("a hand-authored frame claims no import origin", vm.Editor.SourceNote.Length == 0,
            vm.Editor.SourceNote);


        // 2. Selection, driven through the canvas hit test rather than the view model.
        canvas.FitToContent();

        var origin = new CanvasOrigin(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2);
        var contentBox = canvas.Viewport.RectToCanvas(layout.Rects["Content"], origin);

        IReadOnlyList<string>? overlap = null;
        Point overlapPoint = default;
        for (var y = contentBox.Y + 2; y < contentBox.Bottom - 2 && overlap is null; y += 3)
        {
            for (var x = contentBox.X + 2; x < contentBox.Right - 2; x += 3)
            {
                var candidates = canvas.HitTestCandidates(x, y);
                if (candidates.Count > 1)
                {
                    overlap = candidates;
                    overlapPoint = new Point(x, y);
                    break;
                }
            }
        }

        Check("overlap hit testing returns an ordered candidate list", overlap is { Count: > 1 });
        if (overlap is { Count: > 1 })
        {
            var first = canvas.CycleHitTestFrame(overlapPoint.X, overlapPoint.Y);
            vm.Select(first);
            var second = canvas.CycleHitTestFrame(overlapPoint.X, overlapPoint.Y);
            Check("repeated click cycles to the next overlap candidate",
                first == overlap[0] && second == overlap[1],
                $"{first} then {second}; expected {overlap[0]} then {overlap[1]}");
        }

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

            var zoomBeforeReveal = canvas.Viewport.Zoom;
            Check("Reveal Selection succeeds", canvas.RevealSelection());
            var revealed = canvas.Viewport.RectToCanvas(vm.Layout.Rects["Content"], canvas.Origin);
            Check("Reveal Selection preserves zoom", Near(canvas.Viewport.Zoom, zoomBeforeReveal));
            Check("Reveal Selection centers the selected widget",
                Near(revealed.X + revealed.Width / 2, canvas.Bounds.Width / 2)
                && Near(revealed.Y + revealed.Height / 2, canvas.Bounds.Height / 2));
        }

        // 3. Inspector editing re-resolves immediately.
        vm.Editor.Width = "300";
        vm.Editor.CommitBufferedField("width");
        Check("width edit re-resolved Content", Near(vm.Layout.Rects["Content"].Width, 300),
            $"width {vm.Layout.Rects["Content"].Width}");
        vm.Editor.Width = "296";
        vm.Editor.CommitBufferedField("width");
        Check("width restored", Near(vm.Layout.Rects["Content"].Width, 296));
        vm.Editor.OffsetX = "0";
        vm.Editor.CommitBufferedField("offsetX");
        var leftAtZeroOffset = vm.Layout.Rects["Content"].Left;
        vm.Editor.OffsetX = "40";
        vm.Editor.CommitBufferedField("offsetX");
        Check("offset edit moved Content right",
            Near(vm.Layout.Rects["Content"].Left, leftAtZeroOffset + 40),
            $"left {vm.Layout.Rects["Content"].Left}, expected {leftAtZeroOffset + 40}");
        vm.Editor.OffsetX = "12";
        vm.Editor.CommitBufferedField("offsetX");
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
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"frameforge-smoke-{Guid.NewGuid():N}");
        var xmlFixture = Path.Combine(fixtureRoot, "content", "client", "Interface", "FrameXML", "NativeHuntsFrame.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(xmlFixture)!);
        var fixtureAssetDirectory = Path.Combine(fixtureRoot, "content", "client", "Interface", "NativeHunts");
        Directory.CreateDirectory(fixtureAssetDirectory);
        File.WriteAllBytes(Path.Combine(fixtureAssetDirectory, "hunt_divider.tga"), CreateSmokeDividerTga());
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

            var dividerAsset = vm.Assets.Resolve(@"Interface\NativeHunts\hunt_divider.tga");
            Check("source-relative TGA resolved and decoded",
                dividerAsset is { Status: AssetResolutionStatus.Resolved, Width: 512, Height: 8 },
                dividerAsset.Diagnostic.Message);

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

            vm.TreeFilter = TreeFilter.VISUAL;
            Check("Visual tree filter shows real visual widgets with ancestor context",
                FlattenTree(vm.TreeRoots).Any(n => n.Frame.Kind == FrameKind.TEXTURE)
                && CountTreeNodes(vm.TreeRoots) < vm.Project.Frames.Count);
            vm.TreeSearch = "READY";
            Check("imported tree search keeps matching descendants and ancestors",
                FlattenTree(vm.TreeRoots).Any(n => n.Name.Contains("Ready", StringComparison.OrdinalIgnoreCase))
                && FlattenTree(vm.TreeRoots).Any(n => n.Name == "NativeHuntsFrame"));
            vm.TreeSearch = string.Empty;
            vm.TreeFilter = TreeFilter.ALL;

            var retainedTexture = vm.Project.Find("NativeHuntsFrameContentPanelHuntStateDecoration");
            vm.OnCanvasSelectionRequested(retainedTexture!.Name);
            Check("visual metadata appears in the inspector",
                vm.Editor.HasVisual
                && vm.Editor.VisualLines.Any(line => line.Contains("hunt_trail_prints.tga", StringComparison.Ordinal))
                && vm.Editor.VisualLines.Any(line => line.Contains("alpha 0.38", StringComparison.Ordinal)),
                string.Join(" | ", vm.Editor.VisualLines));

            // Multi-selection on the REAL imported document, because that is the only place the
            // selection has to cope with what the importer actually produced: a mixed set of
            // panels, textures and auto-sized FontStrings, most of them anchored to a locked
            // LFDParentFrame the user can never move.
            var mixedKinds = new[]
            {
                vm.Project.Find("NativeHuntsFrameContentPanelRecord"),
                vm.Project.Find("NativeHuntsFrameContentPanelIdentityIcon"),
                vm.Project.Frames.FirstOrDefault(f => f.Kind == FrameKind.FONTSTRING && !f.Anonymous),
            }.Where(f => f is not null).Select(f => f!.Name).ToArray();
            Check("a mixed Image / Frame / Text multi-selection is available", mixedKinds.Length == 3,
                string.Join(", ", mixedKinds));

            vm.OnCanvasSelectionRequested(mixedKinds[0]);
            foreach (var name in mixedKinds.Skip(1))
                vm.OnCanvasSelectionRequested(name, additive: true);
            window.SyncCanvas();
            await PumpAsync(1);

            Check("ctrl-click accumulated the whole selection in click order",
                vm.SelectedNames.SequenceEqual(mixedKinds), string.Join(", ", vm.SelectedNames));
            Check("the newest click is the primary the inspector describes",
                vm.SelectedName == mixedKinds[^1] && vm.SelectedTreeNode?.Name == mixedKinds[^1],
                $"{vm.SelectedName} / {vm.SelectedTreeNode?.Name ?? "(none)"}");
            Check("the canvas received every selected name, not just the primary",
                canvas.SelectedNames.SequenceEqual(mixedKinds) && canvas.SelectedName == mixedKinds[^1],
                string.Join(", ", canvas.SelectedNames));
            Check("the selection summary reports the whole selection",
                vm.SelectionSummary.Contains($"{mixedKinds.Length} objects selected", StringComparison.Ordinal),
                vm.SelectionSummary);
            Check("single-object editors are hidden during a multi-selection", !vm.ShowsSingleObjectEditors);
            Check("align is offered for three objects, distribute too",
                vm.CanAlignSelection && vm.CanDistributeSelection);

            var treeMarks = FlattenTree(vm.TreeRoots).Where(n => n.Name == mixedKinds[0] || n.Name == mixedKinds[^1]).ToArray();
            Check("every selected tree row carries a selection marker",
                treeMarks.Length == 2 && treeMarks.All(n => n.ShowsSelectionBadge)
                && treeMarks.Count(n => n.IsPrimarySelection) == 1,
                string.Join(", ", treeMarks.Select(n => $"{n.Name}:{n.SelectionBadge}")));

            var selectionBitmap = new RenderTargetBitmap(
                new PixelSize((int)Math.Max(1, canvas.Bounds.Width), (int)Math.Max(1, canvas.Bounds.Height)),
                new Vector(96, 96));
            selectionBitmap.Render(canvas);
            Check("a multi-selection is drawn on the canvas", CountNonBackgroundPixels(selectionBitmap) > 500,
                $"{CountNonBackgroundPixels(selectionBitmap)} non-background pixels");

            // Arrange against the imported document: offsets only, nothing outside the selection,
            // and a locked member reported rather than moved.
            var beforeArrange = vm.Project.Frames.ToDictionary(f => f.Name, f => f with { });
            var lockedMember = mixedKinds[1];
            vm.SetElementLocked(lockedMember, locked: true);
            vm.ArrangeSelection(SelectionArrangeCommand.AlignLeft);
            var movedByArrange = vm.Project.Frames.Where(f =>
                f.OffsetX != beforeArrange[f.Name].OffsetX || f.OffsetY != beforeArrange[f.Name].OffsetY)
                .Select(f => f.Name).ToArray();
            Check("aligning the imported selection moved only selected, movable frames",
                movedByArrange.Length > 0
                && movedByArrange.All(vm.SelectedNames.Contains)
                && !movedByArrange.Contains(lockedMember),
                string.Join(", ", movedByArrange));
            Check("the locked member of the selection was left unchanged",
                beforeArrange[lockedMember].OffsetX == vm.Project.Find(lockedMember)!.OffsetX
                && beforeArrange[lockedMember].OffsetY == vm.Project.Find(lockedMember)!.OffsetY,
                $"{beforeArrange[lockedMember].OffsetX} -> {vm.Project.Find(lockedMember)!.OffsetX}");
            Check("the locked member was reported rather than silently skipped",
                vm.Status.Contains("locked", StringComparison.OrdinalIgnoreCase), vm.Status);
            Check("align preserved every anchor relationship",
                vm.Project.Frames.All(f => f.Point == beforeArrange[f.Name].Point
                    && f.RelativeTo == beforeArrange[f.Name].RelativeTo
                    && f.RelativePoint == beforeArrange[f.Name].RelativePoint
                    && f.Parent == beforeArrange[f.Name].Parent));
            Check("align preserved every size", vm.Project.Frames.All(f =>
                f.Width == beforeArrange[f.Name].Width && f.Height == beforeArrange[f.Name].Height));
            Check("align changed nothing outside the selection",
                vm.Project.Frames.Where(f => !vm.SelectedNames.Contains(f.Name))
                    .All(f => f.OffsetX == beforeArrange[f.Name].OffsetX && f.OffsetY == beforeArrange[f.Name].OffsetY));
            Check("the multi-selection survived the arrange",
                vm.SelectedNames.SequenceEqual(mixedKinds), string.Join(", ", vm.SelectedNames));

            // Group drag: every selected object moves by exactly one delta, and an object that
            // only follows another selected frame is not moved twice.
            vm.SetElementLocked(lockedMember, locked: false);
            var beforeGroupDrag = LayoutResolver.Resolve(vm.Project);
            var afterGroupDragBefore = vm.Project.Frames.ToDictionary(f => f.Name, f => f with { });
            vm.DragFrame(mixedKinds[0], 30, -20);
            var afterGroupDrag = LayoutResolver.Resolve(vm.Project);
            var deltas = mixedKinds
                .Where(name => afterGroupDrag.Rects.ContainsKey(name) && beforeGroupDrag.Rects.ContainsKey(name))
                .Select(name => afterGroupDrag.Rects[name].Left - beforeGroupDrag.Rects[name].Left)
                .ToArray();
            Check("dragging one member moved every selected object by one delta",
                deltas.Length == mixedKinds.Length && deltas.All(dx => Near(dx, 30, 1e-6)),
                string.Join(", ", deltas));
            Check("a group drag left unselected objects alone",
                vm.Project.Frames.Where(f => !vm.SelectedNames.Contains(f.Name))
                    .All(f => f.OffsetX == afterGroupDragBefore[f.Name].OffsetX && f.OffsetY == afterGroupDragBefore[f.Name].OffsetY));
            Check("a group drag changed no size and no anchor",
                vm.Project.Frames.All(f => f.Width == afterGroupDragBefore[f.Name].Width
                    && f.Height == afterGroupDragBefore[f.Name].Height
                    && f.Point == afterGroupDragBefore[f.Name].Point
                    && f.RelativeTo == afterGroupDragBefore[f.Name].RelativeTo
                    && f.RelativePoint == afterGroupDragBefore[f.Name].RelativePoint));

            // Exercise the actual templated toolbar controls. Calling SetViewMode directly would
            // prove the view model and miss a broken TwoWay binding between the visible toolbar
            // and the ordinary (non-Avalonia-property) LayoutCanvas synchronization bridge.
            vm.SetViewMode(CanvasViewMode.DEBUG);
            await PumpAsync(1);
            var previewButton = window.GetVisualDescendants()
                .OfType<RadioButton>()
                .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Preview", StringComparison.Ordinal));
            Check("Preview toolbar button exists", previewButton is not null);
            Check("Native Hunts example is not primary toolbar branding",
                !window.GetVisualDescendants().OfType<Button>()
                    .Any(button => string.Equals(button.Content?.ToString(), "Load Native Hunts Example", StringComparison.Ordinal)));
            if (previewButton is not null)
            {
                previewButton.IsChecked = true;
                await PumpAsync(2);
                Check("Preview toolbar updates the view model", vm.ViewMode == CanvasViewMode.PREVIEW);
                Check("Preview toolbar updates the canvas", canvas.Mode == CanvasViewMode.PREVIEW);
            }

            var previewStateSelector = window.FindControl<ComboBox>("PreviewStateSelector");
            Check("design-time Preview State selector exists", previewStateSelector is not null);
            Check("Native Hunts exposes XML Defaults plus four evidence-backed states",
                vm.PreviewStateOptions.Select(state => state.Label).SequenceEqual(
                    new[] { "XML Defaults", "Idle", "Standard Hunt", "Elite Hunt", "Hunt Complete" }),
                string.Join(", ", vm.PreviewStateOptions.Select(state => state.Label)));

            Check("DESIGN is the default workspace", vm.IsDesignWorkspace);
            vm.SetWorkspace(WorkspaceExperience.Inspect);
            await PumpAsync(1);
            var hiddenToggle = window.GetVisualDescendants()
                .OfType<CheckBox>()
                .FirstOrDefault(checkBox => string.Equals(checkBox.Content?.ToString(), "Hidden", StringComparison.Ordinal));
            Check("Hidden toolbar toggle exists", hiddenToggle is not null);
            if (hiddenToggle is not null)
            {
                Check("Preview refreshes the visible Hidden toggle to off", hiddenToggle.IsChecked == false,
                    $"control={hiddenToggle.IsChecked}, filter={vm.CanvasFilter}");

                var beforeHiddenBitmap = new RenderTargetBitmap(
                    new PixelSize((int)Math.Max(1, canvas.Bounds.Width), (int)Math.Max(1, canvas.Bounds.Height)),
                    new Vector(96, 96));
                beforeHiddenBitmap.Render(canvas);
                var beforeHiddenTrace = canvas.LastRenderTrace;
                Console.WriteLine($"SMOKE_RENDER_BEFORE_HIDDEN {{\"filter\":\"{canvas.Filter}\"," +
                                  $"\"visibilityAccepted\":{beforeHiddenTrace?.VisibilityAccepted ?? -1}," +
                                  $"\"drawableFrames\":{beforeHiddenTrace?.DrawableFrames ?? -1}," +
                                  $"\"areas\":{beforeHiddenTrace?.DrawableFramesWithArea ?? -1}," +
                                  $"\"visualAttempts\":\"{FormatCounts(beforeHiddenTrace?.VisualAttemptsByKind
                                      ?? new Dictionary<FrameKind, int>())}\"," +
                                  $"\"paintedPixels\":{CountNonBackgroundPixels(beforeHiddenBitmap)}}}");

                hiddenToggle.IsChecked = true;
                await PumpAsync(2);
                Check("Hidden toolbar updates the view model",
                    vm.CanvasFilter.HasFlag(VisibilityFilter.HIDDEN));
                Check("Hidden toolbar updates the canvas",
                    canvas.Filter.HasFlag(VisibilityFilter.HIDDEN));

                var divider = vm.Project.Frames.First(frame =>
                    frame.Visual?.Texture?.File?.EndsWith("hunt_divider.tga", StringComparison.OrdinalIgnoreCase) == true);
                vm.OnCanvasSelectionRequested(divider.Name);
                canvas.SelectedName = divider.Name;
                canvas.FitToContent();
                canvas.InvalidateVisual();
                await PumpAsync(1);
                var artworkBitmap = new RenderTargetBitmap(
                    new PixelSize((int)Math.Max(1, canvas.Bounds.Width), (int)Math.Max(1, canvas.Bounds.Height)),
                    new Vector(96, 96));
                artworkBitmap.Render(canvas);

                var dividerLayout = vm.Layout.Frames[divider.Name].Rect!.Value;
                var dividerBox = canvas.Viewport.RectToCanvas(dividerLayout, canvas.Origin);
                var cropColors = CountDominantColors(artworkBitmap,
                    new Rect(dividerBox.X, dividerBox.Y, dividerBox.Width, dividerBox.Height));
                Check("decoded artwork contributes pixels inside the texture bounds", cropColors.Red > 100,
                    $"red={cropColors.Red}, blue={cropColors.Blue}");
                Check("texCoords crop excludes the fixture's blue atlas half",
                    cropColors.Blue < Math.Max(4, cropColors.Red / 20),
                    $"red={cropColors.Red}, blue={cropColors.Blue}");

                canvas.AssetResolver = null;
                var fallbackBitmap = new RenderTargetBitmap(artworkBitmap.PixelSize, new Vector(96, 96));
                fallbackBitmap.Render(canvas);
                Check("resolved artwork changes Preview pixels from the fallback",
                    CountPixelsChanged(artworkBitmap, fallbackBitmap) > 100);
                canvas.AssetResolver = vm.Assets;
            }
            vm.SetWorkspace(WorkspaceExperience.Design);

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

            // Real single-anchor imported geometry is editable; synthesized stand-ins are not.
            vm.OnCanvasSelectionRequested("LFDParentFrame");
            Check("an imported stand-in is locked for functional geometry export",
                !vm.CanEditSelection && vm.SelectedGeometryEditDiagnostic.Contains("stand-in", StringComparison.OrdinalIgnoreCase));
            vm.OnCanvasSelectionRequested("NativeHuntsFrameContentPanelRecord");
            vm.Editor.Width = "400";
            vm.Editor.CommitBufferedField("width");
            Check("safe imported geometry is editable",
                vm.Project.Find("NativeHuntsFrameContentPanelRecord")!.Width == 400);
            Check("editing did not touch the source file",
                Sha256(xmlFixture) == xmlDigestBefore);
            vm.Editor.Width = "280";
            vm.Editor.CommitBufferedField("width");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
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
            Check("saved project contains no absolute source-machine path",
                !File.ReadAllText(jsonSaveTarget).Contains(fixtureRoot, StringComparison.Ordinal));

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
                Check("visual metadata survived the round trip",
                    saved.Find("NativeHuntsFrameContentPanelHuntStateDecoration")?.Visual
                    == vm.Project.Find("NativeHuntsFrameContentPanelHuntStateDecoration")?.Visual);
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

        // Every first-class mode must be able to render the real fixture. The root is authored
        // hidden, so turn Hidden on in the clean modes to exercise their visual content rather
        // than correctly producing an empty runtime state.
        foreach (var mode in Enum.GetValues<CanvasViewMode>())
        {
            vm.SetViewMode(mode);
            if (mode is not CanvasViewMode.DEBUG)
                vm.SetCategoryVisible(VisibilityFilter.HIDDEN, true);

            canvas.InvalidateVisual();
            await PumpAsync(1);
            var modeBitmap = new RenderTargetBitmap(
                new PixelSize((int)Math.Max(1, canvas.Bounds.Width), (int)Math.Max(1, canvas.Bounds.Height)),
                new Vector(96, 96));
            modeBitmap.Render(canvas);
            var modePixels = CountNonBackgroundPixels(modeBitmap);
            var trace = canvas.LastRenderTrace;
            Check($"{mode.Label()} canvas received the live mode", canvas.Mode == mode);
            Check($"{mode.Label()} canvas received the live filter", canvas.Filter == vm.CanvasFilter);
            Check($"{mode.Label()} canvas received the live label policy", canvas.Labels == vm.LabelPolicy);
            Check($"{mode.Label()} executed VisualContentLayer", trace is { VisualContentExecuted: true });

            if (trace is not null)
            {
                Console.WriteLine($"SMOKE_RENDER_TRACE {{\"mode\":\"{mode.Label()}\"," +
                                  $"\"filter\":\"{trace.Filter}\"," +
                                  $"\"projectFrames\":{trace.ProjectFrames}," +
                                  $"\"resolvedRectangles\":{trace.ResolvedRectangles}," +
                                  $"\"paintOrderEntries\":{trace.PaintOrderEntries}," +
                                  $"\"visibilityAccepted\":{trace.VisibilityAccepted}," +
                                  $"\"acceptedByKind\":\"{FormatCounts(trace.VisibilityAcceptedByKind)}\"," +
                                  $"\"drawableFrames\":{trace.DrawableFrames}," +
                                  $"\"boxes\":{trace.DrawableFramesWithBox}," +
                                  $"\"areas\":{trace.DrawableFramesWithArea}," +
                                  $"\"layers\":\"{string.Join(",", trace.ActiveLayers)}\"," +
                                  $"\"visualAttempts\":\"{FormatCounts(trace.VisualAttemptsByKind)}\"," +
                                  $"\"paintOperations\":{trace.VisualPaintOperations}," +
                                  $"\"paintedPixels\":{modePixels}}}");
            }

            if (mode is CanvasViewMode.PREVIEW or CanvasViewMode.HYBRID)
            {
                Check($"{mode.Label()} Hidden override reached the canvas",
                    canvas.Filter.HasFlag(VisibilityFilter.HIDDEN));
                Check($"{mode.Label()} has drawable Native Hunts visuals",
                    trace is { DrawableFramesWithArea: > 0 }
                    && trace.VisualAttemptsByKind.GetValueOrDefault(FrameKind.TEXTURE) > 0
                    && trace.VisualAttemptsByKind.GetValueOrDefault(FrameKind.FONTSTRING) > 0);
                Check($"{mode.Label()} meaningfully changes canvas pixels", modePixels > 1000,
                    $"{modePixels} non-background pixels");
            }
        }

        vm.SetViewMode(CanvasViewMode.DEBUG);
        await PumpAsync(1);
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

        var importedOrigin = canvas.Origin;
        var fitted = canvas.Viewport.RectToCanvas(modelBox, importedOrigin);
        var view = new Rect(0, 0, canvas.Bounds.Width, canvas.Bounds.Height);
        var fittedRect = new Rect(fitted.X, fitted.Y, fitted.Width, fitted.Height);
        // FitToContent leaves a 40px margin, so the framed box must fit inside the margin box and
        // fill one axis of it. Which axis is not asserted: this layout is taller than it is wide,
        // so height is the limiting dimension and demanding width be filled too would be wrong.
        var marginBox = view.Deflate(40);
        // Fractional layout pixels and window scaling can put an edge just past the computed
        // margin even when the same formula produced both values; two device pixels is the
        // rendering tolerance, while still catching any genuinely clipped widget.
        const double fitTolerance = 2;
        var framed = fittedRect.Left >= marginBox.Left - fitTolerance
            && fittedRect.Top >= marginBox.Top - fitTolerance
            && fittedRect.Right <= marginBox.Right + fitTolerance
            && fittedRect.Bottom <= marginBox.Bottom + fitTolerance;
        Check("Fit framed the visible widgets", framed,
            $"box {fittedRect} in usable {marginBox}");
        Check("Fit used the available space",
            fittedRect.Width > marginBox.Width * 0.9 || fittedRect.Height > marginBox.Height * 0.9,
            $"box {fittedRect.Width:F0}x{fittedRect.Height:F0} in usable {marginBox.Width:F0}x{marginBox.Height:F0}");

        var initializer = new Rect(
            canvas.Viewport.ModelToCanvasX(-512, importedOrigin) - 3,
            canvas.Viewport.ModelToCanvasY(384, importedOrigin) - 3, 6, 6);
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
        var importedProjectFrameCount = vm.Project.Frames.Count;

        // Optional development acceptance against the user's real read-only addon checkout and
        // either the Phase 4B manual root or the Phase 5A local client provider. The packaged smoke
        // remains self-contained; these environment variables opt into external evidence.
        var nativeXml = Environment.GetEnvironmentVariable("FRAMEFORGE_NATIVE_HUNTS_XML");
        var stockRoot = Environment.GetEnvironmentVariable("FRAMEFORGE_NATIVE_HUNTS_ASSET_ROOT");
        var wowClient = Environment.GetEnvironmentVariable("FRAMEFORGE_WOW_CLIENT");
        if (!string.IsNullOrWhiteSpace(nativeXml)
            && (!string.IsNullOrWhiteSpace(stockRoot) || !string.IsNullOrWhiteSpace(wowClient)))
        {
            AssetMaterializationResult[] clientResults = [];
            if (!string.IsNullOrWhiteSpace(wowClient))
            {
                Check("client-provider acceptance has no manual asset root active", vm.AssetRoots.Count == 0,
                    string.Join(", ", vm.AssetRoots));
                vm.SetWoWClientPath(wowClient);
                Check("client provider validates WoW 3.3.5a build 12340",
                    vm.WoWClientVersion == "3.3.5a / 12340", vm.WoWClientStatus);
                Check("client provider detects enUS", vm.WoWClientLocale == "enUS", vm.WoWClientLocale);
                vm.ClearManagedStockCache();
            }
            vm.ImportFromFile(nativeXml);
            if (!string.IsNullOrWhiteSpace(wowClient))
            {
                Check("three unique stock dependencies are missing before explicit resolution",
                    vm.StockAssetsSummary == "Stock assets: 3 required, 0 available", vm.StockAssetsSummary);
                clientResults = [.. vm.ResolveMissingStockAssets()];
                Check("client provider materializes the focused stock definition set and three direct assets",
                    clientResults.Length == 14 && clientResults.All(result => result.Success),
                    string.Join(" | ", clientResults.Select(result => result.Message)));
                var portableJson = ProjectCodec.Serialize(vm.Project);
                Check("client/cache paths and Blizzard definition contents stay out of portable project JSON",
                    !portableJson.Contains(wowClient, StringComparison.Ordinal)
                    && !portableJson.Contains(vm.WoWAssets.CacheRoot, StringComparison.Ordinal)
                    && !portableJson.Contains("FRIZQT__", StringComparison.Ordinal)
                    && !portableJson.Contains("<Ui", StringComparison.Ordinal));
                Check("effective MPQs match 3.3.5a patch precedence",
                    clientResults.Count(result => result.Provenance?.ArchivePath == "Data/enUS/patch-enUS-3.MPQ") == 6
                    && clientResults.Count(result => result.Provenance?.ArchivePath == "Data/enUS/patch-enUS.MPQ") == 1
                    && clientResults.Count(result => result.Provenance?.ArchivePath == "Data/enUS/patch-enUS-2.MPQ") == 2
                    && clientResults.Count(result => result.Provenance?.ArchivePath == "Data/enUS/locale-enUS.MPQ") == 5,
                    string.Join(" | ", clientResults.Select(result => result.Provenance?.ArchivePath)));

                var tabStyle = vm.StockTemplates.ResolveButton(StockTemplateResolver.TabTemplate);
                Check("CharacterFrameTabButtonTemplate resolves fully with three normal slices",
                    tabStyle is { Status: StockDefinitionStatus.FullyResolved, NormalSlices.Count: 3 },
                    tabStyle?.Status.ToString() ?? "unresolved");
                var expectedFonts = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["GameFontNormal"] = 12,
                    ["GameFontHighlight"] = 12,
                    ["GameFontNormalSmall"] = 10,
                    ["GameFontNormalLarge"] = 16,
                    ["GameFontHighlightSmall"] = 10,
                    ["GameFontHighlightLarge"] = 16,
                };
                Check("all six Native Hunts stock font styles resolve with authoritative sizes and local font",
                    expectedFonts.All(pair => vm.StockTemplates.ResolveFont(pair.Key) is { PhysicalFontPath: not null } font
                                              && font.Size == pair.Value),
                    string.Join(" | ", expectedFonts.Keys.Select(name =>
                        $"{name}={vm.StockTemplates.ResolveFont(name)?.Size.ToString() ?? "missing"}")));
                var lfdStyle = vm.StockTemplates.ResolveExternalFrame(StockTemplateResolver.LfdParentFrame);
                Check("LFDParentFrame stock context records the authoritative static size and scoped runtime boundary",
                    lfdStyle is { Width: 355, Height: 440, Status: StockDefinitionStatus.PartiallyResolved }
                    && lfdStyle.ScopeNote.Contains("355 x 500", StringComparison.Ordinal),
                    lfdStyle?.ScopeNote ?? "unresolved");

                var inactiveTab = vm.Assets.Resolve(@"Interface\PaperDollInfoFrame\UI-Character-InactiveTab");
                Check("required tab atlas decodes the demonstrated BLP2 DXT3 subtype",
                    inactiveTab is { CanRender: true, Format: TextureFileFormat.Blp, Width: 128, Height: 32 }
                    && inactiveTab.Texture!.Image.Description.Contains("DXT3", StringComparison.Ordinal),
                    inactiveTab.Texture?.Image.Description ?? inactiveTab.Diagnostic.Message);
            }
            else
            {
                vm.Assets.Configure(nativeXml, [stockRoot!]);
            }
            var nativeDecodeStart = vm.Assets.DecodeCount;
            window.SyncCanvas();
            canvas.AssetResolver = vm.Assets;

            var textureFrames = vm.Project.Frames.Where(frame => frame.Kind == FrameKind.TEXTURE).ToArray();
            var declaredFrames = textureFrames.Where(frame => frame.Visual?.Texture?.File is { Length: > 0 }).ToArray();
            var resolvedAssets = declaredFrames.Select(frame => vm.Assets.Resolve(frame.Visual!.Texture!.File)).ToArray();
            Check("real acceptance has 21 Texture elements", textureFrames.Length == 21);
            Check("real acceptance has 19 declared Texture references", declaredFrames.Length == 19);
            Check("all 19 direct Texture declarations resolve and decode",
                resolvedAssets.Count(asset => asset.CanRender) == 19);
            Check("all 11 stock declarations render decoded BLP",
                resolvedAssets.Count(asset => asset.CanRender && asset.Format == TextureFileFormat.Blp) == 11);
            Check("the 8 custom declarations still render decoded TGA",
                resolvedAssets.Count(asset => asset.CanRender && asset.Format == TextureFileFormat.Tga) == 8);
            var nativeUniqueDecodes = vm.Assets.DecodeCount - nativeDecodeStart;
            if (!string.IsNullOrWhiteSpace(wowClient))
            {
                using var decodeAudit = new TextureAssetResolver();
                decodeAudit.Configure(nativeXml, [vm.WoWAssets.CacheRoot]);
                foreach (var frame in declaredFrames)
                    decodeAudit.Resolve(frame.Visual!.Texture!.File);
                nativeUniqueDecodes = decodeAudit.DecodeCount;
                foreach (var frame in declaredFrames)
                    decodeAudit.Resolve(frame.Visual!.Texture!.File);
                Check("repeated stock declarations reuse the provider cache decode",
                    decodeAudit.DecodeCount == nativeUniqueDecodes && decodeAudit.CacheHitCount > 0);
            }
            Check("eleven physical custom/stock files decoded once each", nativeUniqueDecodes == 11,
                $"decoded {nativeUniqueDecodes}");

            if (!string.IsNullOrWhiteSpace(wowClient))
            {
                Check("effective template geometry sizes both Native Hunts tabs without mutating declarations",
                    vm.Layout.Rects.TryGetValue("LFDParentFrameTab1", out var firstTab)
                    && vm.Layout.Rects.TryGetValue("LFDParentFrameTab2", out var secondTab)
                    && firstTab.Width > secondTab.Width && firstTab.Height == 32 && secondTab.Height == 32
                    && vm.Project.Find("LFDParentFrameTab1") is { Width: 0, Height: 0 }
                    && vm.Project.Find("LFDParentFrameTab2") is { Width: 0, Height: 0 },
                    $"tab1 {vm.Layout.Rects.GetValueOrDefault("LFDParentFrameTab1")}, tab2 {vm.Layout.Rects.GetValueOrDefault("LFDParentFrameTab2")}");

                vm.OnCanvasSelectionRequested("LFDParentFrameTab1");
                Check("tab inspector reports effective template state, size, and provenance",
                    vm.Editor.VisualLines.Any(line => line.Contains("effective template CharacterFrameTabButtonTemplate", StringComparison.Ordinal))
                    && vm.Editor.VisualLines.Any(line => line.Contains("PanelTemplates_TabResize", StringComparison.Ordinal))
                    && vm.Editor.VisualLines.Any(line => line.Contains("normal/unselected", StringComparison.Ordinal))
                    && vm.Editor.VisualLines.Any(line => line.Contains("CharacterFrameTemplates.xml", StringComparison.Ordinal)),
                    string.Join(" | ", vm.Editor.VisualLines));

                vm.OnCanvasSelectionRequested("NativeHuntsFrameContentPanelIdentityPrey");
                Check("font inspector reports effective style, local font, and transitive provenance",
                    vm.Editor.VisualLines.Any(line => line.Contains("effective font GameFontHighlightLarge: 16px", StringComparison.Ordinal))
                    && vm.Editor.VisualLines.Any(line => line.Contains("local client cache", StringComparison.Ordinal))
                    && vm.Editor.VisualLines.Any(line => line.Contains("SystemFont_Shadow_Large", StringComparison.Ordinal)),
                    string.Join(" | ", vm.Editor.VisualLines));
            }

            vm.OnCanvasSelectionRequested("Texture#4");
            Check("BLP inspector reports format, dimensions, and demonstrated subtype",
                vm.Editor.VisualLines.Any(line => line == "format BLP")
                && vm.Editor.VisualLines.Any(line => line == "image 512 x 512")
                && vm.Editor.VisualLines.Any(line => line.Contains("BLP2 DXT5", StringComparison.Ordinal)),
                string.Join(" | ", vm.Editor.VisualLines));
            if (!string.IsNullOrWhiteSpace(wowClient))
                Check("BLP inspector reports client archive provenance",
                    vm.Editor.VisualLines.Any(line => line == "client 3.3.5a / 12340 / enUS")
                    && vm.Editor.VisualLines.Any(line => line == "archive Data/enUS/patch-enUS-2.MPQ")
                    && vm.Editor.VisualLines.Any(line => line.StartsWith("sha256 ", StringComparison.Ordinal)),
                    string.Join(" | ", vm.Editor.VisualLines));

            vm.OnCanvasSelectionRequested("NativeHuntsFrameContentPanelHuntStateProgress");
            Check("StatusBar inspector resolves retained BarTexture metadata",
                vm.Editor.VisualLines.Any(line => line.Contains("declared barTexture", StringComparison.Ordinal))
                && vm.Editor.VisualLines.Any(line => line.Contains("BLP2 DXT1", StringComparison.Ordinal)));

            vm.SetViewMode(CanvasViewMode.PREVIEW);
            vm.SetCategoryVisible(VisibilityFilter.HIDDEN, true);
            window.SyncCanvas();
            canvas.FitToContent();
            canvas.InvalidateVisual();
            await PumpAsync(2);

            var realPreview = new RenderTargetBitmap(
                new PixelSize((int)Math.Max(1, canvas.Bounds.Width), (int)Math.Max(1, canvas.Bounds.Height)),
                new Vector(96, 96));
            realPreview.Render(canvas);

            var phase5bPixelDifference = 0;
            if (!string.IsNullOrWhiteSpace(wowClient))
            {
                canvas.StockTemplates = null;
                var phase5aBaseline = new RenderTargetBitmap(realPreview.PixelSize, new Vector(96, 96));
                phase5aBaseline.Render(canvas);
                phase5bPixelDifference = CountPixelsChanged(realPreview, phase5aBaseline);
                Check("stock template/font resolution visibly changes Preview from the Phase 5A baseline",
                    phase5bPixelDifference > 500, $"{phase5bPixelDifference} changed pixels");
                canvas.StockTemplates = vm.StockTemplates;
            }

            canvas.AssetResolver = null;
            var fallbackPreview = new RenderTargetBitmap(realPreview.PixelSize, new Vector(96, 96));
            fallbackPreview.Render(canvas);
            var realPixelDifference = CountPixelsChanged(realPreview, fallbackPreview);
            Check("real stock artwork changes Preview pixels from stand-ins", realPixelDifference > 1000,
                $"{realPixelDifference} changed pixels");
            canvas.AssetResolver = vm.Assets;

            var nativePreviewOutput = Environment.GetEnvironmentVariable("FRAMEFORGE_NATIVE_PREVIEW_OUT")
                ?? "/tmp/frameforge-phase4b-native-preview.png";
            realPreview.Save(nativePreviewOutput, new PngBitmapEncoderOptions());
            Check("real Native Hunts Preview screenshot written", File.Exists(nativePreviewOutput), nativePreviewOutput);
            Console.WriteLine($"SMOKE_NATIVE_ASSETS {{\"textures\":{textureFrames.Length}," +
                              $"\"declared\":{declaredFrames.Length},\"resolved\":{resolvedAssets.Count(a => a.CanRender)}," +
                              $"\"tga\":{resolvedAssets.Count(a => a.CanRender && a.Format == TextureFileFormat.Tga)}," +
                              $"\"blp\":{resolvedAssets.Count(a => a.CanRender && a.Format == TextureFileFormat.Blp)}," +
                              $"\"uniqueDecodes\":{nativeUniqueDecodes},\"pixelDifference\":{realPixelDifference}," +
                              $"\"screenshot\":\"{nativePreviewOutput}\"}}");
            if (!string.IsNullOrWhiteSpace(wowClient))
                Console.WriteLine($"SMOKE_NATIVE_TEMPLATES {{\"template\":\"{StockTemplateResolver.TabTemplate}\"," +
                                  $"\"fonts\":6,\"phase5bPixelDifference\":{phase5bPixelDifference}," +
                                  $"\"screenshot\":\"{nativePreviewOutput}\"}}");

            if (!string.IsNullOrWhiteSpace(wowClient))
            {
                var stateDirectory = Environment.GetEnvironmentVariable("FRAMEFORGE_PREVIEW_STATE_DIR")
                                     ?? "/tmp/frameforge-phase5c-states";
                Directory.CreateDirectory(stateDirectory);
                var projectBeforeStates = ProjectCodec.Serialize(vm.Project);
                var stateBitmaps = new Dictionary<string, RenderTargetBitmap>(StringComparer.Ordinal);

                // XML Defaults intentionally uses Hidden ON: this reproduces the accepted Phase 5B
                // static-design view. Runtime states use Hidden OFF so their effective visibility,
                // rather than the inspection override, decides what appears.
                var xmlDefaultsPath = Path.Combine(stateDirectory, "xml-defaults.png");
                realPreview.Save(xmlDefaultsPath, new PngBitmapEncoderOptions());
                Check("XML Defaults has no design-time overrides", vm.ActivePreviewOverrides.Overrides.Count == 0);
                Check("XML Defaults screenshot written", File.Exists(xmlDefaultsPath), xmlDefaultsPath);

                vm.SetCategoryVisible(VisibilityFilter.HIDDEN, false);
                foreach (var state in vm.PreviewStateOptions.Where(state => !state.IsXmlDefaults))
                {
                    vm.SelectedPreviewState = state;
                    window.SyncCanvas();
                    canvas.FitToContent();
                    canvas.InvalidateVisual();
                    await PumpAsync(2);
                    var stateBitmap = new RenderTargetBitmap(
                        new PixelSize((int)Math.Max(1, canvas.Bounds.Width), (int)Math.Max(1, canvas.Bounds.Height)),
                        new Vector(96, 96));
                    stateBitmap.Render(canvas);
                    var path = Path.Combine(stateDirectory, state.Id + ".png");
                    stateBitmap.Save(path, new PngBitmapEncoderOptions());
                    stateBitmaps[state.Id] = stateBitmap;
                    Check($"{state.Label} screenshot renders non-blank design-time content",
                        CountNonBackgroundPixels(stateBitmap) > 1000, path);
                    Check($"{state.Label} screenshot written", File.Exists(path), path);
                }

                var idleProject = vm.PreviewStateOptions.Single(state => state.Id == "idle");
                vm.SelectedPreviewState = idleProject;
                Check("Idle shows the real no-active-hunt and record presentation",
                    vm.Layout.Frames["NativeHuntsFrameContentPanelIdle"].EffectiveVisible
                    && vm.Layout.Frames["NativeHuntsFrameContentPanelRecord"].EffectiveVisible
                    && !vm.Layout.Frames["NativeHuntsFrameContentPanelIdentity"].EffectiveVisible
                    && vm.PresentationProject.Find("NativeHuntsFrameContentPanelIdleState")?.Visual?.Text?.Text == "NO ACTIVE HUNT"
                    && vm.PresentationProject.Find("NativeHuntsFrameContentPanelRecordSeals")?.Visual?.Text?.Text == "3");

                vm.SelectedPreviewState = vm.PreviewStateOptions.Single(state => state.Id == "standard-hunt");
                vm.OnCanvasSelectionRequested("NativeHuntsFrameContentPanelIdentityIcon");
                Check("preview inspector identifies runtime-selected Standard icon provenance",
                    vm.Editor.VisualLines.Any(line => line.Contains("preview override: Standard Hunt", StringComparison.Ordinal))
                    && vm.Editor.VisualLines.Any(line => line.Contains("hunt_icon_standard.tga", StringComparison.Ordinal))
                    && vm.Editor.VisualLines.Any(line => line.Contains("runtime-selected", StringComparison.Ordinal)),
                    string.Join(" | ", vm.Editor.VisualLines));
                Check("Standard Hunt applies a partial 0..100 progress value",
                    vm.PresentationProject.Find("NativeHuntsFrameContentPanelHuntStateProgress")?.Visual?.StatusBar?.DefaultFraction == .6);
                Check("Standard and Elite produce materially different icon pixels",
                    CountPixelsChanged(stateBitmaps["standard-hunt"], stateBitmaps["elite-hunt"]) > 100,
                    $"{CountPixelsChanged(stateBitmaps["standard-hunt"], stateBitmaps["elite-hunt"])} changed pixels");
                Check("Idle and active-hunt presentations differ materially",
                    CountPixelsChanged(stateBitmaps["idle"], stateBitmaps["standard-hunt"]) > 1000);
                Check("complete and tracking presentations differ materially",
                    CountPixelsChanged(stateBitmaps["hunt-complete"], stateBitmaps["standard-hunt"]) > 500);

                vm.SelectedPreviewState = vm.PreviewStateOptions.Single(state => state.IsXmlDefaults);
                vm.SetCategoryVisible(VisibilityFilter.HIDDEN, true);
                vm.OnCanvasSelectionRequested("NativeHuntsFrameContentPanelHuntStateProgress");
                window.SyncCanvas();
                canvas.FitToContent();
                canvas.InvalidateVisual();
                await PumpAsync(2);
                var restoredDefaults = new RenderTargetBitmap(realPreview.PixelSize, new Vector(96, 96));
                restoredDefaults.Render(canvas);
                Check("returning to XML Defaults restores the exact Phase 5B pixels",
                    CountPixelsChanged(realPreview, restoredDefaults) == 0,
                    $"{CountPixelsChanged(realPreview, restoredDefaults)} changed pixels");
                Check("preview-state switching never mutates or serializes the source project",
                    ProjectCodec.Serialize(vm.Project) == projectBeforeStates
                    && !projectBeforeStates.Contains("standard-hunt", StringComparison.Ordinal)
                    && !projectBeforeStates.Contains("hunt_icon_standard", StringComparison.Ordinal));
                foreach (var stateImage in stateBitmaps.Values)
                    stateImage.Dispose();
                restoredDefaults.Dispose();
                Console.WriteLine($"SMOKE_PREVIEW_STATES {{\"directory\":\"{stateDirectory}\"," +
                                  $"\"states\":5,\"xmlDefaultsExact\":true," +
                                  $"\"standardProgress\":60,\"eliteProgress\":65}}");

                // Phase 6 editor-usability acceptance: source provenance, direct composition,
                // editor-only groups/locks, origin isolation, and project-format persistence.
                var usabilityDirectory = Environment.GetEnvironmentVariable("FRAMEFORGE_USABILITY_DIR")
                                         ?? "/tmp/frameforge-usability";
                Directory.CreateDirectory(usabilityDirectory);
                vm.SelectedPreviewState = vm.PreviewStateOptions.Single(state => state.Id == "idle");
                vm.SetCategoryVisible(VisibilityFilter.HIDDEN, false);
                vm.GroupNameDraft = "Blizzard Chrome";
                vm.CreateGroup();
                var stockMembers = vm.Project.Frames.Where(frame =>
                        frame.Name == StockTemplateResolver.LfdParentFrame
                        || frame.Inherits == StockTemplateResolver.TabTemplate
                        || (frame.Visual?.Texture?.File is { } reference
                            && vm.Assets.Resolve(reference).PhysicalPath is { } physical
                            && Path.GetFullPath(physical).StartsWith(Path.GetFullPath(vm.WoWAssets.CacheRoot), StringComparison.Ordinal)))
                    .Take(8).Select(frame => frame.Name).ToArray();
                foreach (var member in stockMembers)
                {
                    vm.Select(member);
                    vm.AddSelectionToGroup();
                }
                vm.SelectedGroupLocked = true;
                Check("Blizzard Chrome group contains representative stock shell pieces",
                    vm.Project.Editor.Groups.Single(group => group.Name == "Blizzard Chrome").Members.Count >= 3,
                    string.Join(", ", stockMembers));
                Check("locking the group protects all its members",
                    stockMembers.All(member => vm.Project.Editor.IsLocked(member)));
                var lockedMember = stockMembers.First();
                var beforeLockedDrag = vm.Project.Find(lockedMember)!;
                vm.DragFrame(lockedMember, 25, -25);
                Check("canvas drag cannot move a locked stock member", vm.Project.Find(lockedMember) == beforeLockedDrag);

                vm.SetWorkspace(WorkspaceExperience.Design);
                vm.Select("NativeHuntsFrameContentPanelRecord");
                window.SyncCanvas();
                await PumpAsync(2);
                var nativeDesignPath = Path.Combine(usabilityDirectory, "native-hunts-design.png");
                var nativeDesignBitmap = new RenderTargetBitmap(new PixelSize(windowWidth, windowHeight), new Vector(96, 96));
                nativeDesignBitmap.Render(root);
                nativeDesignBitmap.Save(nativeDesignPath, new PngBitmapEncoderOptions());
                nativeDesignBitmap.Dispose();
                Check("Native Hunts DESIGN screenshot written", File.Exists(nativeDesignPath), nativeDesignPath);
                Check("friendly record artwork name is available without changing internal identity",
                    vm.Project.Frames.Any(frame => frame.Anonymous
                        && frame.Visual?.Texture?.File?.Contains("hunt_panel_record.tga", StringComparison.OrdinalIgnoreCase) == true
                        && vm.Project.Editor.DisplayNameFor(frame) == "Hunt Panel Record"));

                vm.SetWorkspace(WorkspaceExperience.Inspect);
                vm.Select("NativeHuntsFrameContentPanelRecord");
                Check("PanelRecord reports imported XML source and parser location",
                    vm.SelectedSourceFile == "NativeHuntsFrame.xml"
                    && vm.SelectedSourcePath.EndsWith("NativeHuntsFrame.xml", StringComparison.Ordinal)
                    && vm.SelectedFrame?.SourceLocation is { Line: 71 });
                Check("PanelRecord direct composition reports record and seal TGA artwork",
                    vm.VisualComposition.Any(item => item.AssetPath.Contains("hunt_panel_record.tga", StringComparison.Ordinal))
                    && vm.VisualComposition.Any(item => item.AssetPath.Contains("hunt_icon_seal.tga", StringComparison.Ordinal))
                    && vm.VisualComposition.Any(item => item.Resolution.Contains("Source-relative project asset", StringComparison.Ordinal)));
                Check("PanelRecord resize guidance distinguishes cropped artwork without claiming distortion",
                    vm.ResizeGuidance.Contains("cropped/atlas", StringComparison.Ordinal)
                    && !vm.ResizeGuidance.Contains("may stretch", StringComparison.Ordinal));

                window.SyncCanvas();
                canvas.FitToContent();
                await PumpAsync(3);
                var inspectorPath = Path.Combine(usabilityDirectory, "native-hunts-inspect.png");
                var inspectorBitmap = new RenderTargetBitmap(new PixelSize(windowWidth, windowHeight), new Vector(96, 96));
                inspectorBitmap.Render(root);
                inspectorBitmap.Save(inspectorPath, new PngBitmapEncoderOptions());
                inspectorBitmap.Dispose();
                Check("PanelRecord provenance/composition screenshot written", File.Exists(inspectorPath), inspectorPath);

                var geometryBeforeFilter = vm.Project.Frames.Select(frame =>
                    (frame.Name, frame.Width, frame.Height, frame.OffsetX, frame.OffsetY, frame.Visible)).ToArray();
                vm.SetOriginVisible(OriginVisibility.BlizzardStock, false);
                window.SyncCanvas();
                canvas.InvalidateVisual();
                await PumpAsync(3);
                Check("stock-origin presentation can be hidden without hiding custom descendants",
                    vm.HiddenByOrigin.Contains("LFDParentFrame")
                    && !vm.HiddenByOrigin.Contains("NativeHuntsFrameContentPanelRecord"));
                var isolatedPath = Path.Combine(usabilityDirectory, "native-hunts-stock-hidden.png");
                var isolatedBitmap = new RenderTargetBitmap(
                    new PixelSize((int)Math.Max(1, canvas.Bounds.Width), (int)Math.Max(1, canvas.Bounds.Height)),
                    new Vector(96, 96));
                isolatedBitmap.Render(canvas);
                isolatedBitmap.Save(isolatedPath, new PngBitmapEncoderOptions());
                isolatedBitmap.Dispose();
                Check("stock-hidden Native Hunts screenshot written", File.Exists(isolatedPath), isolatedPath);
                vm.SetOriginVisible(OriginVisibility.BlizzardStock, true);
                Check("restoring stock origin visibility does not change source geometry",
                    geometryBeforeFilter.SequenceEqual(vm.Project.Frames.Select(frame =>
                        (frame.Name, frame.Width, frame.Height, frame.OffsetX, frame.OffsetY, frame.Visible))));

                var usabilityProject = Path.Combine(Path.GetTempPath(), $"frameforge-usability-{Guid.NewGuid():N}.fforge.json");
                try
                {
                    Check("grouped/locked project saves", vm.SaveToFile(usabilityProject));
                    vm.OpenFromFile(usabilityProject);
                    Check("groups and locks reopen from .fforge.json",
                        vm.Project.Editor.Groups.SingleOrDefault(group => group.Name == "Blizzard Chrome") is { Locked: true } reopenedGroup
                        && reopenedGroup.Members.SequenceEqual(stockMembers));
                Check("all five design-time states remain available after reopening grouped project",
                        vm.PreviewStateOptions.Select(state => state.Label).SequenceEqual(
                            new[] { "XML Defaults", "Idle", "Standard Hunt", "Elite Hunt", "Hunt Complete" }));
                }
                finally
                {
                    if (File.Exists(usabilityProject))
                        File.Delete(usabilityProject);
                }

                Check("clean LFD project creation succeeds from the validated client", vm.NewDungeonFinderProject(), vm.Status);
                Check("clean LFD project has a conceptual stock framework",
                    vm.ConceptualStockFramework is { Locked: true, Expanded: false, StockIdentity: not null });
                Check("clean LFD DESIGN initially shows one conceptual stock object",
                    vm.IsDesignWorkspace && vm.TreeRoots.Count == 1
                    && vm.TreeRoots[0].DisplayName.Contains("Blizzard Dungeon Finder Frame", StringComparison.Ordinal));
                var lfdDirectArt = vm.Project.Frames.Where(frame => frame.Kind == FrameKind.TEXTURE
                    && frame.Visual?.Texture?.File is { Length: > 0 }).ToArray();
                Check("clean LFD framework retains and resolves stock artwork",
                    lfdDirectArt.Length >= 2 && lfdDirectArt.All(frame => vm.Assets.Resolve(frame.Visual!.Texture!.File).CanRender),
                    string.Join(" | ", lfdDirectArt.Select(frame =>
                        $"{frame.Name}:{frame.Visual?.Texture?.File}:{vm.Assets.Resolve(frame.Visual!.Texture!.File).Status}")));
                Check("clean LFD primary frame artwork is effectively visible",
                    lfdDirectArt.Where(frame => frame.Visual?.Texture?.File?.Contains("UI-LFG-FRAME", StringComparison.OrdinalIgnoreCase) == true)
                        .All(frame => vm.Layout.Frames[frame.Name].EffectiveVisible));
                var lfdGroup = vm.ConceptualStockFramework!;
                var lockedStockName = lfdGroup.Members.First(name => vm.Project.Find(name) is { Width: > 0, Height: > 0 });
                var lockedStockBefore = vm.Project.Find(lockedStockName)!;
                vm.DragFrame(lockedStockName, 20, -20);
                Check("locked conceptual stock cannot be dragged", vm.Project.Find(lockedStockName) == lockedStockBefore);

                vm.NewObjectName = "Hunt Record";
                vm.AddDesignFrame();
                var huntRecord = vm.SelectedName!;
                vm.NewObjectName = "Paw Emblem";
                vm.AddDesignImage();
                var pawEmblem = vm.SelectedName!;
                Check("custom Hunt Record and Paw Emblem are added without unlocking Blizzard",
                    vm.Project.Editor.DisplayNameFor(vm.Project.Find(huntRecord)!) == "Hunt Record"
                    && vm.Project.Editor.DisplayNameFor(vm.Project.Find(pawEmblem)!) == "Paw Emblem"
                    && vm.ConceptualStockFramework!.Locked);
                Check("custom objects are preferred canvas selections over locked stock",
                    vm.PreferredSelectionNames.Contains(huntRecord) && vm.PreferredSelectionNames.Contains(pawEmblem));

                vm.StateNameDraft = "Idle"; vm.CreateDesignState();
                vm.StateNameDraft = "Standard Hunt"; vm.CreateDesignState();
                vm.StateNameDraft = "Elite Hunt"; vm.CreateDesignState();
                vm.StateNameDraft = "Hunt Complete"; vm.CreateDesignState();
                vm.Select(huntRecord);
                vm.AssignSelectionToAllStates();
                vm.NewObjectName = "Standard Only Test";
                vm.AddDesignText();
                var standardOnly = vm.SelectedName!;
                vm.SelectedAuthoredState = vm.AuthoredStateOptions.Single(state => state.Name == "Standard Hunt");
                vm.AssignSelectionToSelectedState();
                vm.ActiveDesignState = vm.DesignStateOptions.Single(state => state.Name == "Idle");
                Check("authored Idle hides a Standard-Hunt-only object",
                    vm.Layout.Frames[standardOnly].EffectiveVisible == false);

                vm.Select(huntRecord);
                window.SyncCanvas();
                canvas.FitToContent();
                await PumpAsync(2);
                var lfdDesignPath = Path.Combine(usabilityDirectory, "clean-lfd-design.png");
                var lfdDesignBitmap = new RenderTargetBitmap(new PixelSize(windowWidth, windowHeight), new Vector(96, 96));
                lfdDesignBitmap.Render(root);
                lfdDesignBitmap.Save(lfdDesignPath, new PngBitmapEncoderOptions());
                lfdDesignBitmap.Dispose();
                Check("clean LFD DESIGN screenshot written", File.Exists(lfdDesignPath), lfdDesignPath);

                var idleStatePath = Path.Combine(usabilityDirectory, "design-state-idle.png");
                var idleStateBitmap = new RenderTargetBitmap(new PixelSize(windowWidth, windowHeight), new Vector(96, 96));
                idleStateBitmap.Render(root);
                idleStateBitmap.Save(idleStatePath, new PngBitmapEncoderOptions());
                idleStateBitmap.Dispose();
                vm.ActiveDesignState = vm.DesignStateOptions.Single(state => state.Name == "Standard Hunt");
                Check("authored Standard Hunt shows its assigned object",
                    vm.Layout.Frames[standardOnly].EffectiveVisible);
                vm.Select(standardOnly);
                window.SyncCanvas();
                await PumpAsync(2);
                var standardStatePath = Path.Combine(usabilityDirectory, "design-state-standard.png");
                var standardStateBitmap = new RenderTargetBitmap(new PixelSize(windowWidth, windowHeight), new Vector(96, 96));
                standardStateBitmap.Render(root);
                standardStateBitmap.Save(standardStatePath, new PngBitmapEncoderOptions());
                standardStateBitmap.Dispose();
                Check("two authored design-state screenshots written",
                    File.Exists(idleStatePath) && File.Exists(standardStatePath));

                vm.SetConceptualStockExpanded(true);
                Check("conceptual stock expands without unlocking",
                    vm.ConceptualStockFramework is { Expanded: true, Locked: true }
                    && vm.TreeRoots.First().Children.Count > 0);
                await PumpAsync(2);
                foreach (var item in window.GetVisualDescendants().OfType<TreeViewItem>()) item.IsExpanded = true;
                await PumpAsync(2);
                var expandedPath = Path.Combine(usabilityDirectory, "lfd-expanded-locked.png");
                var expandedBitmap = new RenderTargetBitmap(new PixelSize(windowWidth, windowHeight), new Vector(96, 96));
                expandedBitmap.Render(root);
                expandedBitmap.Save(expandedPath, new PngBitmapEncoderOptions());
                expandedBitmap.Dispose();
                Check("expanded-but-locked Blizzard screenshot written", File.Exists(expandedPath), expandedPath);

                vm.SetConceptualStockLocked(false);
                var editableStock = vm.Project.Find(lockedStockName)!;
                vm.DragFrame(lockedStockName, 1, 0);
                Check("explicit unlock permits an individual stock edit",
                    vm.Project.Find(lockedStockName)!.OffsetX == editableStock.OffsetX + 1);
                vm.SetConceptualStockLocked(true);
                Check("stock framework relocks in one action", vm.Project.Editor.IsLocked(lockedStockName));

                var lfdProjectPath = Path.Combine(Path.GetTempPath(), $"frameforge-lfd-design-{Guid.NewGuid():N}.fforge.json");
                try
                {
                    var geometry = vm.Project.Find(huntRecord)!;
                    Check("clean LFD design project saves", vm.SaveToFile(lfdProjectPath));
                    vm.OpenFromFile(lfdProjectPath);
                    Check("clean LFD project reopens with stock identity, presentation, locks, objects, states, memberships and geometry",
                        vm.ConceptualStockFramework is { Locked: true, Expanded: true, StockIdentity: not null }
                        && vm.Project.Editor.DisplayNameFor(vm.Project.Find(huntRecord)!) == "Hunt Record"
                        && vm.Project.Editor.DisplayNameFor(vm.Project.Find(pawEmblem)!) == "Paw Emblem"
                        && vm.Project.Editor.DesignStates.Count == 4
                        && vm.Project.Editor.DesignObjectFor(standardOnly)?.StateIds.Count == 1
                        && vm.Project.Find(huntRecord) == geometry);
                }
                finally
                {
                    if (File.Exists(lfdProjectPath)) File.Delete(lfdProjectPath);
                }
                Console.WriteLine($"SMOKE_COMPOSITION {{\"directory\":\"{usabilityDirectory}\"," +
                                  $"\"components\":11,\"group\":\"Blizzard Chrome\",\"locked\":true," +
                                  $"\"designScreenshots\":6,\"lfdStates\":4}}");
            }
        }

        // A deterministic overlap check exercises the real rendering pipeline, not only metadata:
        // stock green is the protected foundation, then custom A red, then custom B blue.
        vm.Load(new Project
        {
            Frames =
            [
                new FrameDef
                {
                    Name = "LayerStock", Kind = FrameKind.TEXTURE, Width = 120, Height = 120,
                    Visual = new FrameVisual { Texture = new TextureVisual(null, Color: new ColorRgba(0, 0.7, 0)) },
                },
                new FrameDef
                {
                    Name = "LayerA", Kind = FrameKind.TEXTURE, Width = 120, Height = 120,
                    Visual = new FrameVisual { Texture = new TextureVisual(null, Color: new ColorRgba(0.9, 0, 0)) },
                },
                new FrameDef
                {
                    Name = "LayerB", Kind = FrameKind.TEXTURE, Width = 120, Height = 120,
                    Visual = new FrameVisual { Texture = new TextureVisual(null, Color: new ColorRgba(0, 0, 0.9)) },
                },
            ],
            Editor = new EditorMetadata
            {
                Groups = [new EditorGroup
                {
                    Name = "Blizzard Dungeon Finder Frame", Members = ["LayerStock"], Locked = true,
                    Concept = "stock-framework",
                }],
                DesignObjects =
                [
                    new DesignObjectMetadata { FrameName = "LayerA", DisplayName = "Custom A" },
                    new DesignObjectMetadata { FrameName = "LayerB", DisplayName = "Custom B" },
                ],
                DesignOrder = ["LayerA", "LayerB"],
            },
        }, null, "DESIGN layer pixel acceptance.");
        vm.SetViewMode(CanvasViewMode.PREVIEW);
        vm.Select("LayerB");
        window.SyncCanvas();
        canvas.FitToContent();
        await PumpAsync(2);
        var overlapBox = canvas.Viewport.RectToCanvas(vm.Layout.Rects["LayerB"], canvas.Origin);
        var overlapRegion = new Rect(overlapBox.X, overlapBox.Y, overlapBox.Width, overlapBox.Height);
        using var blueFront = new RenderTargetBitmap(
            new PixelSize((int)Math.Max(1, canvas.Bounds.Width), (int)Math.Max(1, canvas.Bounds.Height)),
            new Vector(96, 96));
        blueFront.Render(canvas);
        var blueFrontColors = CountDominantColors(blueFront, overlapRegion);
        vm.SendSelectedToBack();
        window.SyncCanvas();
        await PumpAsync(2);
        using var redFront = new RenderTargetBitmap(blueFront.PixelSize, new Vector(96, 96));
        redFront.Render(canvas);
        var redFrontColors = CountDominantColors(redFront, overlapRegion);
        Check("DESIGN overlap pixels follow custom draw-order controls",
            blueFrontColors.Blue > blueFrontColors.Red + 100
            && redFrontColors.Red > redFrontColors.Blue + 100
            && CountPixelsChanged(blueFront, redFront) > 100,
            $"blue-front={blueFrontColors.Red}/{blueFrontColors.Blue}, " +
            $"red-front={redFrontColors.Red}/{redFrontColors.Blue}, changed={CountPixelsChanged(blueFront, redFront)}");
        Check("Send to Back keeps custom content above locked stock",
            vm.Layout.PaintOrder.SequenceEqual(["LayerStock", "LayerB", "LayerA"])
            && vm.Project.Editor.IsLocked("LayerStock"),
            string.Join(",", vm.Layout.PaintOrder));

        if (vm.StockTemplates.ResolveFont("GameFontNormal") is not null)
        {
            var textStyleProject = new Project
            {
                Frames =
                [
                    new FrameDef
                    {
                        Name = "StockText", Kind = FrameKind.FONTSTRING, Width = 140, Height = 28,
                        Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER,
                        Visual = new FrameVisual { Text = new TextVisual("Native Hunt", "CENTER", "MIDDLE", "GameFontNormal") },
                    },
                    new FrameDef
                    {
                        Name = "CustomText", Kind = FrameKind.FONTSTRING, Width = 140, Height = 28, Visible = false,
                        Point = AnchorPoint.CENTER, RelativePoint = AnchorPoint.CENTER,
                        Visual = new FrameVisual { Text = new TextVisual("Native Hunt", "LEFT", "MIDDLE", null) },
                    },
                ],
                Editor = new EditorMetadata
                {
                    Groups = [new EditorGroup { Name = "Blizzard", Members = ["StockText"], Locked = true, Concept = "stock-framework" }],
                    DesignObjects = [new DesignObjectMetadata
                    {
                        FrameName = "CustomText", DisplayName = "Header Text",
                        TextStyle = new DesignTextStyleMetadata { BaseStyle = "GameFontNormal", JustifyH = "CENTER" },
                    }],
                },
            };
            vm.Load(textStyleProject, null, "Text style pixel acceptance.");
            vm.SetViewMode(CanvasViewMode.PREVIEW);
            vm.Select(null);
            window.SyncCanvas();
            canvas.FitToContent();
            await PumpAsync(2);
            using var stockStyled = new RenderTargetBitmap(
                new PixelSize((int)Math.Max(1, canvas.Bounds.Width), (int)Math.Max(1, canvas.Bounds.Height)),
                new Vector(96, 96));
            stockStyled.Render(canvas);
            vm.Load(textStyleProject with
            {
                Frames = [.. textStyleProject.Frames.Select(frame => frame.Name switch
                {
                    "StockText" => frame with { Visible = false },
                    "CustomText" => frame with { Visible = true },
                    _ => frame,
                })],
            }, null, "Text style pixel acceptance.");
            vm.SetViewMode(CanvasViewMode.PREVIEW);
            window.SyncCanvas();
            await PumpAsync(2);
            using var customStyled = new RenderTargetBitmap(stockStyled.PixelSize, new Vector(96, 96));
            customStyled.Render(canvas);
            Check("stock and DESIGN text with the same effective WoW style render consistently",
                CountPixelsChanged(stockStyled, customStyled) == 0,
                $"{CountPixelsChanged(stockStyled, customStyled)} differing pixels");
        }

        // Optional focused DESIGN acceptance against the real divider artwork. The source file is
        // read-only evidence; FrameForge must copy it into a saved project's assets directory.
        var huntDivider = Environment.GetEnvironmentVariable("FRAMEFORGE_HUNT_DIVIDER_PNG");
        if (!string.IsNullOrWhiteSpace(huntDivider) && File.Exists(huntDivider))
        {
            var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(huntDivider)));
            var designRoot = Path.Combine(Path.GetTempPath(), $"frameforge-design-asset-{Guid.NewGuid():N}");
            Directory.CreateDirectory(designRoot);
            var designProject = Path.Combine(designRoot, "Native-Hunts.fforge.json");
            try
            {
                const string focusedStockRoot = "LFDParentFrame";
                const string stockTitle = "LFDHeaderText";
                vm.Load(new Project
                {
                    Frames =
                    [
                        new FrameDef { Name = focusedStockRoot, Width = 355, Height = 440 },
                        new FrameDef
                        {
                            Name = stockTitle, Parent = focusedStockRoot, Kind = FrameKind.FONTSTRING,
                            Width = 240, Height = 24,
                            Visual = new FrameVisual
                            {
                                Text = new TextVisual("LOOKING FOR DUNGEON", "CENTER", "MIDDLE", "GameFontNormalLarge"),
                            },
                        },
                    ],
                    Editor = new EditorMetadata
                    {
                        Groups =
                        [
                            new EditorGroup
                            {
                                Name = "Blizzard Dungeon Finder Frame", Members = [focusedStockRoot, stockTitle],
                                Locked = true, Concept = "stock-framework",
                            },
                        ],
                    },
                }, null, "Focused DESIGN acceptance.");
                vm.Select(focusedStockRoot);
                Check("conceptual lock is coherent while protected",
                    vm.SelectedElementLocked && vm.TreeRoots[0].DisplayName.StartsWith("🔒", StringComparison.Ordinal)
                    && vm.StockFrameworkAction == "Unlock for Editing");
                vm.SetConceptualStockExpanded(true);
                vm.SetConceptualStockLocked(false);
                Check("conceptual unlock is coherent and leaves expansion independent",
                    !vm.SelectedElementLocked && vm.ConceptualStockFramework is { Expanded: true, Locked: false }
                    && vm.TreeRoots[0].DisplayName.StartsWith("🔓", StringComparison.Ordinal)
                    && vm.StockFrameworkAction == "Lock Blizzard Dungeon Finder Frame");
                vm.SetConceptualStockLocked(true);
                Check("conceptual relock restores all protected indicators",
                    vm.SelectedElementLocked && vm.ConceptualStockFramework is { Expanded: true, Locked: true }
                    && vm.StockFrameworkAction == "Unlock for Editing");

                vm.Select(stockTitle);
                vm.DesignNameDraft = "Title Text";
                foreach (var typed in new[] { "N", "NA", "NAT", "NATI", "NATIV", "NATIVE", "NATIVE ", "NATIVE HUNTS" })
                    vm.ChangeSelectedDesignText(typed);
                Check("visible text accepts continuous multi-character editing independently from Name",
                    vm.Project.Editor.DisplayNameFor(vm.Project.Find(stockTitle)!) == "Title Text"
                    && vm.Project.Find(stockTitle)!.Visual?.Text?.Text == "LOOKING FOR DUNGEON"
                    && vm.PresentationProject.Find(stockTitle)!.Visual?.Text?.Text == "NATIVE HUNTS"
                    && vm.Project.Editor.DesignObjectFor(stockTitle)?.TextOverride == "NATIVE HUNTS",
                    $"name={vm.Project.Editor.DisplayNameFor(vm.Project.Find(stockTitle)!)} " +
                    $"source={vm.Project.Find(stockTitle)!.Visual?.Text?.Text} " +
                    $"presentation={vm.PresentationProject.Find(stockTitle)!.Visual?.Text?.Text} " +
                    $"override={vm.Project.Editor.DesignObjectFor(stockTitle)?.TextOverride} " +
                    $"selected={vm.SelectedName} kind={vm.SelectedFrame?.Kind}");
                vm.CopySelectedTextStyle();
                vm.NewObjectName = "Native Hunt";
                vm.AddDesignText();
                var nativeHuntText = vm.SelectedName!;
                foreach (var typed in new[] { "N", "Na", "Nat", "Nati", "Native", "Native Hunt" })
                    vm.ChangeSelectedDesignText(typed);
                var nativeHuntGeometry = vm.Project.Find(nativeHuntText)!;
                vm.PasteSelectedTextStyle();
                Check("stock style copies only its supported treatment to custom DESIGN text",
                    vm.Project.Editor.DesignObjectFor(nativeHuntText)?.TextStyle?.BaseStyle == "GameFontNormalLarge"
                    && vm.Project.Editor.DesignObjectFor(nativeHuntText)?.DisplayName == "Native Hunt"
                    && vm.Project.Editor.DesignObjectFor(nativeHuntText)?.TextOverride == "Native Hunt"
                    && vm.Project.Find(nativeHuntText) == nativeHuntGeometry
                    && vm.SelectedTextFont.Contains("Friz Quadrata", StringComparison.Ordinal)
                    && vm.SelectedTextEffectiveSize == "16 px");
                foreach (var typed in new[] { "", "1", "14" }) vm.TextStyleSizeDraft = typed;
                Check("multi-digit DESIGN text size commits at the deliberate boundary",
                    vm.CommitTextStyleSize() && vm.SelectedTextEffectiveSize == "14 px");
                vm.TextStyleColorDraft = "#CCFFD100";
                Check("DESIGN RGBA override preserves alpha", vm.CommitTextStyleColor()
                    && vm.SelectedTextEffectiveColor == "#CCFFD100");
                vm.SelectedTextAlignment = "Right";
                Check("focused DESIGN acceptance project saves before asset browse", vm.SaveToFile(designProject));
                vm.NewObjectName = "Divider";
                vm.AddDesignImage();
                var divider = vm.SelectedName!;
                window.SyncCanvas();
                canvas.FitToContent();
                await PumpAsync(2);
                var fallback = new RenderTargetBitmap(
                    new PixelSize((int)Math.Max(1, canvas.Bounds.Width), (int)Math.Max(1, canvas.Bounds.Height)),
                    new Vector(96, 96));
                fallback.Render(canvas);

                Check("external divider selection is classified for explicit import",
                    vm.TryAssessDesignAssetSelection(huntDivider, out var requiresImport) && requiresImport);
                Check("real hunt divider imports as a project-owned copy",
                    vm.ChangeSelectedDesignImageFromFile(huntDivider, importExternal: true), vm.Status);
                var assetReference = vm.Project.Editor.DesignObjectFor(divider)?.DesignAsset;
                var importedPath = Path.Combine(designRoot,
                    (assetReference ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
                Check("real hunt divider persists a portable path",
                    assetReference == "assets/hunt_divider.png", assetReference ?? "<null>");
                Check("real hunt divider copy exists and decodes",
                    File.Exists(importedPath) && vm.Assets.Resolve(assetReference).CanRender,
                    vm.Assets.Resolve(assetReference).Diagnostic.Message);

                var uncommittedWidth = vm.Project.Find(divider)!.Width;
                foreach (var typed in new[] { "", "3", "30", "300" })
                {
                    vm.Editor.Width = typed;
                    Check($"width buffer accepts intermediate '{typed}' without relayout",
                        Near(vm.Project.Find(divider)!.Width, uncommittedWidth));
                }
                Check("Enter commits Width 300 once", vm.Editor.CommitBufferedField("width")
                    && Near(vm.Project.Find(divider)!.Width, 300));
                foreach (var typed in new[] { "", "2", "24" }) vm.Editor.Height = typed;
                Check("LostFocus commit path accepts Height 24", vm.Editor.CommitBufferedField("height")
                    && Near(vm.Project.Find(divider)!.Height, 24));
                foreach (var typed in new[] { "", "-", ".", "-.", "-1", "-12", "-12.5" })
                    vm.Editor.OffsetX = typed;
                Check("signed decimal Offset X commits after intermediate states",
                    vm.Editor.CommitBufferedField("offsetX") && Near(vm.Project.Find(divider)!.OffsetX, -12.5));
                foreach (var typed in new[] { "", "4", "42" }) vm.Editor.OffsetY = typed;
                Check("Offset Y 42 commits continuously", vm.Editor.CommitBufferedField("offsetY")
                    && Near(vm.Project.Find(divider)!.OffsetY, 42));
                vm.Editor.Width = "invalid";
                Check("invalid final numeric input preserves the previous geometry",
                    !vm.Editor.CommitBufferedField("width") && Near(vm.Project.Find(divider)!.Width, 300)
                    && vm.Editor.ValidationMessage.Contains("must be a number", StringComparison.Ordinal));
                vm.Editor.CancelBufferedField("width");
                vm.DragFrame(divider, 5, -3);
                vm.Editor.Width = "301";
                Check("width remains editable after dragging", vm.Editor.CommitBufferedField("width"));
                vm.Editor.OffsetX = "-11.5";
                Check("offset remains editable after dragging", vm.Editor.CommitBufferedField("offsetX"));
                vm.Select(stockTitle);
                vm.Select(divider);
                vm.Editor.Width = "300";
                vm.Editor.CommitBufferedField("width");
                vm.Editor.OffsetX = "-12.5";
                vm.Editor.CommitBufferedField("offsetX");
                Check("selection switch returns to coherent committed geometry",
                    vm.SelectedName == divider
                    && vm.Project.Find(divider) is { Width: 300, Height: 24, OffsetX: -12.5, OffsetY: 39 });
                window.SyncCanvas();
                canvas.FitToContent();
                await PumpAsync(2);
                var artwork = new RenderTargetBitmap(fallback.PixelSize, new Vector(96, 96));
                artwork.Render(canvas);
                Check("actual imported divider artwork changes canvas pixels from the gray placeholder",
                    CountPixelsChanged(fallback, artwork) > 100,
                    $"{CountPixelsChanged(fallback, artwork)} changed pixels");
                fallback.Dispose();
                artwork.Dispose();

                Check("focused DESIGN project saves with imported divider", vm.SaveToFile(designProject));
                vm.OpenFromFile(designProject);
                vm.Select(divider);
                Check("save/reopen keeps the real divider renderable",
                    vm.SelectedDesignAssetReference == "assets/hunt_divider.png"
                    && vm.Assets.Resolve(vm.SelectedDesignAssetReference).CanRender
                    && vm.Project.Find(divider) is { Width: 300, Height: 24, OffsetX: -12.5, OffsetY: 39 });
                Check("save/reopen keeps text override while preserving imported text provenance",
                    vm.Project.Find(stockTitle)!.Visual?.Text?.Text == "LOOKING FOR DUNGEON"
                    && vm.PresentationProject.Find(stockTitle)!.Visual?.Text?.Text == "NATIVE HUNTS"
                    && vm.Project.Editor.DesignObjectFor(stockTitle)?.DisplayName == "Title Text",
                    $"source={vm.Project.Find(stockTitle)!.Visual?.Text?.Text} " +
                    $"presentation={vm.PresentationProject.Find(stockTitle)!.Visual?.Text?.Text} " +
                    $"override={vm.Project.Editor.DesignObjectFor(stockTitle)?.TextOverride} " +
                    $"name={vm.Project.Editor.DesignObjectFor(stockTitle)?.DisplayName}");
                Check("save/reopen keeps copied WoW text style and overrides",
                    vm.Project.Editor.DesignObjectFor(nativeHuntText)?.TextStyle is
                    { BaseStyle: "GameFontNormalLarge", Size: 14, Color: { A: > 0.79 and < 0.81 }, JustifyH: "RIGHT" });
                var sourceHashAfter = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(huntDivider)));
                Check("real source divider remains byte-identical", sourceHashAfter == sourceHash,
                    $"{sourceHash} -> {sourceHashAfter}");
            }
            finally
            {
                if (Directory.Exists(designRoot))
                    Directory.Delete(designRoot, recursive: true);
            }
        }

        // 9b. The STATE MEMBERSHIP chooser is the one editor that survives a multi-selection, so
        //     it is driven end to end: the button in the selection chrome, the dialog the view
        //     model actually builds, and the TwoWay checkbox bindings that keep All States and an
        //     explicit list mutually exclusive - a pair the file format cannot hold. The dialog
        //     comes from the same factory the click handler uses and is closed again here, so the
        //     run cannot end parked on it, and nothing is applied, so the project is untouched.
        vm.SetWorkspace(WorkspaceExperience.Design);
        if (vm.Project.Editor.DesignStates.Count == 0)
        {
            vm.StateNameDraft = "Idle";
            vm.CreateDesignState();
        }
        vm.NewObjectName = "Membership Probe";
        vm.AddDesignFrame();
        Check("a fresh DESIGN frame is available for the membership probe",
            vm.SelectedName is { Length: > 0 } && vm.SelectedStateMembership == "All States",
            $"{vm.SelectedName ?? "<none>"} / {vm.SelectedStateMembership}");
        var membershipTarget = vm.SelectedName;
        await PumpAsync(1);
        var chooseStatesButton = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Choose States...", StringComparison.Ordinal));
        Check("STATE MEMBERSHIP offers Choose States...",
            chooseStatesButton is { IsVisible: true },
            chooseStatesButton is null ? "<missing>" : $"visible={chooseStatesButton.IsVisible}");

        var membershipChooser = membershipTarget is null ? null : vm.CreateStateMembershipChooser();
        Check("an All States object opens a uniform, non-mixed chooser",
            membershipChooser is { IsAllStates: true, IsMixed: false, EditableTargetNames.Count: 1 });
        if (membershipChooser is not null)
        {
            var (dialog, applyRequested) = MainWindow.CreateStateMembershipDialog(membershipChooser);
            dialog.Show();
            await PumpAsync(2);
            var membershipBoxes = dialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
            var expectedRows = new[] { "All States" }
                .Concat(vm.Project.Editor.DesignStates.Select(state => state.Name))
                .ToArray();
            Check("the chooser dialog lists All States plus every authored state",
                membershipBoxes.Length > 0 && membershipBoxes.Select(box => box.Content?.ToString()).SequenceEqual(expectedRows),
                string.Join(", ", membershipBoxes.Select(box => box.Content)));
            Check("an All States selection opens with All States checked and the rows cleared",
                membershipBoxes.Length > 0 && membershipBoxes[0].IsChecked == true
                                          && membershipBoxes.Skip(1).All(box => box.IsChecked == false && box.IsEnabled == false),
                string.Join(" | ", membershipBoxes.Select(box => $"{box.IsChecked}:{box.IsEnabled}")));

            var firstStateBox = membershipBoxes.Skip(1).FirstOrDefault();
            if (firstStateBox is not null)
            {
                firstStateBox.IsChecked = true;
                await PumpAsync(1);
                Check("checking a state row in the dialog clears All States",
                    membershipBoxes.Length > 0 && membershipBoxes[0].IsChecked == false
                                              && firstStateBox.IsChecked == true
                                              && !membershipChooser.IsAllStates);
                Check("checking a state row re-enables every state row",
                    membershipBoxes.Skip(1).All(box => box.IsEnabled));
            }

            dialog.Close();
            await PumpAsync(2);
            Check("closing the chooser without Apply leaves membership untouched",
                !applyRequested() && vm.SelectedStateMembership == "All States"
                && vm.Project.Editor.DesignObjectFor(membershipTarget)?.StateIds.Count is null or 0,
                vm.SelectedStateMembership);
        }

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
                vm.Project.Frames.Count == importedProjectFrameCount,
                $"{vm.Project.Frames.Count} vs {importedProjectFrameCount}");
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

    private static string FormatCounts(IReadOnlyDictionary<FrameKind, int> counts) =>
        string.Join(",", Enum.GetValues<FrameKind>().Select(kind => $"{kind}:{counts.GetValueOrDefault(kind)}"));

    private static int CountTreeNodes(IEnumerable<FrameTreeNode> roots) =>
        FlattenTree(roots).Count();

    private static IEnumerable<FrameTreeNode> FlattenTree(IEnumerable<FrameTreeNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in FlattenTree(root.Children))
                yield return child;
        }
    }

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
                var blue = System.Runtime.InteropServices.Marshal.ReadByte(buffer, i);
                var green = System.Runtime.InteropServices.Marshal.ReadByte(buffer, i + 1);
                var red = System.Runtime.InteropServices.Marshal.ReadByte(buffer, i + 2);

                // CopyPixels returns BGRA bytes. The canvas background is RGB #0C1116, therefore
                // B=0x16, G=0x11, R=0x0C in this buffer.
                if (blue != 0x16 || green != 0x11 || red != 0x0C)
                    count++;
            }

            return count;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    private static int CountPixelsChanged(RenderTargetBitmap first, RenderTargetBitmap second)
    {
        var size = first.PixelSize;
        var stride = size.Width * 4;
        var a = Marshal.AllocHGlobal(stride * size.Height);
        var b = Marshal.AllocHGlobal(stride * size.Height);
        try
        {
            first.CopyPixels(new PixelRect(size), a, stride * size.Height, stride);
            second.CopyPixels(new PixelRect(size), b, stride * size.Height, stride);
            var changed = 0;
            for (var i = 0; i < stride * size.Height; i += 4)
            {
                if (Marshal.ReadInt32(a, i) != Marshal.ReadInt32(b, i))
                    changed++;
            }
            return changed;
        }
        finally
        {
            Marshal.FreeHGlobal(a);
            Marshal.FreeHGlobal(b);
        }
    }

    private static (int Red, int Blue) CountDominantColors(RenderTargetBitmap bitmap, Rect region)
    {
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = Marshal.AllocHGlobal(stride * size.Height);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), buffer, stride * size.Height, stride);
            var left = Math.Clamp((int)Math.Floor(region.Left), 0, size.Width);
            var right = Math.Clamp((int)Math.Ceiling(region.Right), 0, size.Width);
            var top = Math.Clamp((int)Math.Floor(region.Top), 0, size.Height);
            var bottom = Math.Clamp((int)Math.Ceiling(region.Bottom), 0, size.Height);
            var red = 0;
            var blue = 0;
            for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var offset = y * stride + x * 4;
                var b = Marshal.ReadByte(buffer, offset);
                var r = Marshal.ReadByte(buffer, offset + 2);
                if (r > b + 40) red++;
                if (b > r + 40) blue++;
            }
            return (red, blue);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] CreateSmokeDividerTga()
    {
        const int width = 512;
        const int height = 8;
        var bytes = new byte[18 + width * height * 4];
        bytes[2] = 2;
        bytes[12] = 0;
        bytes[13] = 2;
        bytes[14] = height;
        bytes[16] = 32;
        bytes[17] = 0x28;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = 18 + (y * width + x) * 4;
            var redHalf = x < 260;
            bytes[offset] = redHalf ? (byte)0 : (byte)230;
            bytes[offset + 1] = 0;
            bytes[offset + 2] = redHalf ? (byte)230 : (byte)0;
            bytes[offset + 3] = 255;
        }
        return bytes;
    }
}
