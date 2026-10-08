using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.ViewModels;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class FunctionalLayoutExportTests
{
    private const string RecordFrame = "NativeHuntsFrameContentPanelRecord";

    [Fact]
    public void Design_tree_preserves_expansion_by_source_identity_across_refreshes_and_workspaces()
    {
        var vm = new MainWindowViewModel(Path.Combine(Path.GetTempPath(), $"frameforge-tree-{Guid.NewGuid():N}.json"));
        vm.ImportFromFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NativeHuntsFrame.xml"));

        Assert.DoesNotContain(Flatten(vm.TreeRoots), node => node.IsExpanded);
        var record = Find(vm.TreeRoots, RecordFrame);
        Assert.NotNull(record);
        record!.IsExpanded = true;

        vm.Select(RecordFrame);
        Assert.True(Find(vm.TreeRoots, RecordFrame)!.IsExpanded);
        vm.Select($"{RecordFrame}Standard");
        Assert.True(Find(vm.TreeRoots, RecordFrame)!.IsExpanded);
        Assert.Equal($"{RecordFrame}Standard", vm.SelectedTreeNode?.Name);
        vm.Select(RecordFrame);
        vm.DragFrame(RecordFrame, 3, 4);
        Assert.True(Find(vm.TreeRoots, RecordFrame)!.IsExpanded);

        vm.SetViewMode(CanvasViewMode.PREVIEW);
        Assert.True(Find(vm.TreeRoots, RecordFrame)!.IsExpanded);
        vm.SetViewMode(CanvasViewMode.HYBRID);
        Assert.True(Find(vm.TreeRoots, RecordFrame)!.IsExpanded);
        vm.SelectedPreviewState = vm.PreviewStateOptions.Single(state => state.Id == "standard-hunt");
        Assert.True(Find(vm.TreeRoots, RecordFrame)!.IsExpanded);

        vm.SetWorkspace(WorkspaceExperience.Inspect);
        Assert.True(Find(vm.TreeRoots, RecordFrame)!.IsExpanded);
        vm.SetWorkspace(WorkspaceExperience.Design);
        Assert.True(Find(vm.TreeRoots, RecordFrame)!.IsExpanded);
        Assert.Equal(RecordFrame, vm.SelectedName);
        Assert.Equal(RecordFrame, vm.SelectedTreeNode?.Name);
        Assert.Equal(vm.Project.Frames.Count, Flatten(vm.TreeRoots).Select(node => node.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Hunt_panel_record_friendly_row_targets_the_source_frame_and_reports_its_declared_anchor()
    {
        var vm = new MainWindowViewModel(Path.Combine(Path.GetTempPath(), $"frameforge-anchor-{Guid.NewGuid():N}.json"));
        vm.ImportFromFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "NativeHuntsFrame.xml"));

        var friendly = Assert.Single(Flatten(vm.TreeRoots), node => node.DisplayName == "Hunt Panel Record");
        Assert.Equal(RecordFrame, friendly.Name);
        Assert.Equal(FrameKind.FRAME, friendly.Frame.Kind);
        Assert.Contains(Flatten(friendly.Children), node => node.DisplayName == "Hunt Panel Record Artwork");

        vm.SelectedTreeNode = friendly;
        Assert.Equal(RecordFrame, vm.Editor.Frame?.Name);
        Assert.Equal(AnchorPoint.BOTTOM, vm.Editor.Point);
        Assert.Equal("0", vm.Editor.OffsetX);
        Assert.Equal("8", vm.Editor.OffsetY);
        Assert.Contains("BOTTOM", vm.SelectedSourceAnchor, StringComparison.Ordinal);
        Assert.Contains("offset (0, 8)", vm.SelectedSourceAnchor, StringComparison.Ordinal);
        Assert.False(vm.CanEditLayoutStructure);
        Assert.Equal(AnchorPoint.BOTTOM, vm.Project.Find(RecordFrame)!.Point);
        Assert.Equal(AnchorPoint.BOTTOM, vm.PresentationProject.Find(RecordFrame)!.Point);
    }

    [Fact]
    public void Visual_drag_exports_only_the_record_anchor_and_reimports_without_touching_source()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NativeHuntsFrame.xml");
        var directory = Path.Combine(Path.GetTempPath(), $"frameforge-layout-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "NativeHuntsFrame.xml");
            var destination = Path.Combine(directory, "NativeHuntsFrame.layout.xml");
            File.Copy(fixture, source);
            var original = File.ReadAllText(source);

            var vm = new MainWindowViewModel(Path.Combine(directory, "settings.json"));
            vm.ImportFromFile(source);
            Assert.True(vm.CanExportLayoutChanges);
            Assert.Equal(RecordFrame, vm.Project.Find(RecordFrame)?.Name);

            vm.Select(RecordFrame);
            vm.DragFrame(RecordFrame, 24, 40);
            vm.EndDrag();
            Assert.True(vm.ExportLayoutChanges(destination), vm.Status);

            var exported = File.ReadAllText(destination);
            Assert.Equal(original, File.ReadAllText(source));
            var before = original.Split('\n');
            var after = exported.Split('\n');
            Assert.Equal(before.Length, after.Length);
            var changed = Assert.Single(Enumerable.Range(0, before.Length), index => before[index] != after[index]);
            Assert.Equal(before[changed].Replace("x=\"0\" y=\"8\"", "x=\"24\" y=\"48\""), after[changed]);

            var reimport = FrameXmlImporter.Import(exported, Path.GetFileName(destination));
            Assert.True(reimport.Ok, reimport.SummaryText);
            var record = reimport.Project!.Find(RecordFrame)!;
            Assert.Equal(AnchorPoint.BOTTOM, record.Point);
            Assert.Equal(24, record.OffsetX);
            Assert.Equal(48, record.OffsetY);
            Assert.Contains("name=\"$parentRecord\"", exported);
            Assert.Contains("NativeHuntsFrame_OnLoad", exported);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Export_rejects_missing_source_source_overwrite_and_static_projects()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NativeHuntsFrame.xml");
        var directory = Path.Combine(Path.GetTempPath(), $"frameforge-layout-guards-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "NativeHuntsFrame.xml");
            File.Copy(fixture, source);
            var vm = new MainWindowViewModel(Path.Combine(directory, "settings.json"));

            Assert.False(vm.CanExportLayoutChanges);
            Assert.False(vm.ExportLayoutChanges(Path.Combine(directory, "static.xml")));
            Assert.Contains(vm.LayoutExportDiagnostics, d => d.Code == "SOURCE_INELIGIBLE");

            vm.ImportFromFile(source);
            Assert.False(vm.ExportLayoutChanges(source));
            Assert.Contains(vm.LayoutExportDiagnostics, d => d.Code == "SOURCE_OVERWRITE_REFUSED");

            File.Delete(source);
            Assert.False(vm.ExportLayoutChanges(Path.Combine(directory, "missing.layout.xml")));
            Assert.Contains(vm.LayoutExportDiagnostics, d => d.Code == "SOURCE_MISSING");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static FrameTreeNode? Find(IEnumerable<FrameTreeNode> nodes, string name) =>
        Flatten(nodes).FirstOrDefault(node => node.Name == name);

    private static IEnumerable<FrameTreeNode> Flatten(IEnumerable<FrameTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }
}
