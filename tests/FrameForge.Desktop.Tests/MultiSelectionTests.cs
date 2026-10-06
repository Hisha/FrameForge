using Avalonia.Input;
using FrameForge.Core;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Controls;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using Xunit;

namespace FrameForge.Desktop.Tests;

/// <summary>
/// The selection model the DESIGN workspace is built on: one ordered set, a primary that the
/// inspector describes, and operations that act on all of it.
/// </summary>
/// <remarks>
/// These tests are about the view model rather than the geometry, because the geometry already has
/// its own suite in <c>FrameForge.Core.Tests.SelectionArrangeTests</c>. What is verified here is
/// that the window's selection state survives the round trip: click order, toggling, deletion,
/// state changes, and the promise that nothing outside the selection is ever touched.
/// </remarks>
public sealed class MultiSelectionTests
{
    [Fact]
    public void Plain_click_replaces_and_additive_click_accumulates_with_the_newest_object_primary()
    {
        var vm = ViewModel(Frame("A", offsetX: 0), Frame("B", offsetX: 200), Frame("C", offsetX: 400));

        vm.OnCanvasSelectionRequested("A");
        Assert.Equal(["A"], vm.SelectedNames);
        Assert.Equal("A", vm.SelectedName);
        Assert.False(vm.IsMultiSelection);

        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.OnCanvasSelectionRequested("C", additive: true);

        Assert.Equal(["A", "B", "C"], vm.SelectedNames);
        Assert.Equal("C", vm.SelectedName);
        Assert.Equal(3, vm.SelectionCount);
        Assert.True(vm.IsMultiSelection);
    }

    [Fact]
    public void Default_argument_keeps_every_existing_single_argument_call_site_meaning_select_only()
    {
        var vm = ViewModel(Frame("A"), Frame("B"));

        vm.OnCanvasSelectionRequested("A", additive: true);
        vm.OnCanvasSelectionRequested("B");

        Assert.Equal(["B"], vm.SelectedNames);
    }

    [Fact]
    public void Toggling_the_primary_off_promotes_the_previously_selected_object()
    {
        var vm = ViewModel(Frame("A"), Frame("B"), Frame("C"));

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.OnCanvasSelectionRequested("C", additive: true);
        vm.ToggleSelection("C");

        Assert.Equal(["A", "B"], vm.SelectedNames);
        Assert.Equal("B", vm.SelectedName);
    }

    [Fact]
    public void Toggling_the_last_remaining_object_off_clears_the_selection()
    {
        var vm = ViewModel(Frame("A"));

        vm.OnCanvasSelectionRequested("A", additive: true);
        vm.ToggleSelection("A");

        Assert.Empty(vm.SelectedNames);
        Assert.Null(vm.SelectedName);
    }

    [Fact]
    public void Toggling_a_selected_object_takes_it_back_out_instead_of_duplicating_it()
    {
        var vm = ViewModel(Frame("A"), Frame("B"));

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.OnCanvasSelectionRequested("A", additive: true);

        Assert.Equal(["B"], vm.SelectedNames);
        Assert.Equal("B", vm.SelectedName);
    }

    [Fact]
    public void Single_object_editors_are_hidden_for_a_multi_selection_so_they_cannot_edit_one_frame_by_accident()
    {
        var vm = ViewModel(Frame("A"), Frame("B"));

        vm.OnCanvasSelectionRequested("A");
        Assert.True(vm.ShowsSingleObjectEditors);

        vm.OnCanvasSelectionRequested("B", additive: true);
        Assert.False(vm.ShowsSingleObjectEditors);
        Assert.Contains("2 objects selected", vm.MultiSelectionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Align_needs_two_objects_and_distribute_needs_three_before_the_button_can_be_offered()
    {
        var vm = ViewModel(Frame("A"), Frame("B"), Frame("C"));

        vm.OnCanvasSelectionRequested("A");
        Assert.False(vm.CanAlignSelection);
        Assert.False(vm.CanDistributeSelection);

        vm.OnCanvasSelectionRequested("B", additive: true);
        Assert.True(vm.CanAlignSelection);
        Assert.False(vm.CanDistributeSelection);

        vm.OnCanvasSelectionRequested("C", additive: true);
        Assert.True(vm.CanDistributeSelection);
    }

    [Fact]
    public void Arranging_moves_every_selected_object_and_nothing_else()
    {
        var vm = ViewModel(
            Frame("A", width: 100, height: 50, offsetX: 0, offsetY: 0),
            Frame("B", width: 100, height: 50, offsetX: 200, offsetY: 100),
            Frame("Untouched", width: 100, height: 50, offsetX: 400, offsetY: 200));

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);

        // Model space is +Y up, so B is the higher of the two and "align tops" means moving A up to
        // B's top edge, not dragging B down to A.
        vm.ArrangeSelection(SelectionArrangeCommand.AlignTop);

        Assert.Equal(100, vm.Project.Find("A")!.OffsetY);
        Assert.Equal(100, vm.Project.Find("B")!.OffsetY);
        Assert.Equal(200, vm.Project.Find("Untouched")!.OffsetY);
        Assert.True(vm.IsDirty);
        Assert.Contains("1 object moved", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Arrange_only_adds_to_offsets_and_never_rewrites_the_anchor_relationship()
    {
        var vm = ViewModel(
            Frame("A", width: 100, height: 50, offsetX: 0),
            Frame("B", width: 100, height: 50, relativeTo: "A", offsetX: 30));

        var before = vm.Project.Find("B")!;
        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.ArrangeSelection(SelectionArrangeCommand.AlignLeft);

        var after = vm.Project.Find("B")!;
        Assert.Equal(before.Point, after.Point);
        Assert.Equal(before.RelativeTo, after.RelativeTo);
        Assert.Equal(before.RelativePoint, after.RelativePoint);
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
    }

[Fact]
    public void Arrange_reports_a_locked_member_and_moves_only_the_editable_ones()
    {
        var vm = ViewModel(
            Frame("A", width: 100, height: 50, offsetX: 0, offsetY: 0),
            Frame("B", width: 100, height: 50, offsetX: 200, offsetY: 100),
            Frame("C", width: 100, height: 50, offsetX: 400, offsetY: -200));

        vm.SetElementLocked("C", locked: true);

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.OnCanvasSelectionRequested("C", additive: true);
        vm.ArrangeSelection(SelectionArrangeCommand.AlignBottom);

        // C is the lowest of the three, so it decides the target for everyone else. If a locked
        // member's extent were ignored, A would already be the bottom-most and nothing would move.
        Assert.Equal(-200, vm.Project.Find("A")!.OffsetY);
        Assert.Equal(-200, vm.Project.Find("B")!.OffsetY);
        Assert.Equal(-200, vm.Project.Find("C")!.OffsetY);
        Assert.Contains("locked", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Arrange_refuses_an_unsafe_frame_and_says_which_one_and_why()
    {
        var vm = ViewModel(
            Frame("A", width: 100, height: 50, offsetX: 0),
            Frame("Fills", setAllPoints: true, relativeTo: "A"));

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("Fills", additive: true);
        vm.ArrangeSelection(SelectionArrangeCommand.AlignLeft);

        Assert.Contains("SetAllPoints", vm.Status, StringComparison.Ordinal);
        Assert.Equal(0, vm.Project.Find("A")!.OffsetX);
    }

    [Fact]
    public void Dragging_one_member_of_a_multi_selection_moves_the_whole_selection_as_one_group()
    {
        var vm = ViewModel(
            Frame("A", width: 100, height: 50, offsetX: 0, offsetY: 0),
            Frame("B", width: 100, height: 50, offsetX: 200, offsetY: 0),
            Frame("C", width: 100, height: 50, offsetX: 400, offsetY: 0));

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.DragFrame("B", 25, -10);
        vm.EndDrag();

        Assert.Equal(25, vm.Project.Find("A")!.OffsetX);
        Assert.Equal(225, vm.Project.Find("B")!.OffsetX);
        Assert.Equal(400, vm.Project.Find("C")!.OffsetX);
        Assert.Equal(-10, vm.Project.Find("A")!.OffsetY);
        Assert.Equal(-10, vm.Project.Find("B")!.OffsetY);
        Assert.Contains("2 objects as a group", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Dragging_an_unselected_frame_keeps_the_selection_and_moves_only_that_frame()
    {
        var vm = ViewModel(
            Frame("A", width: 100, height: 50, offsetX: 0),
            Frame("B", width: 100, height: 50, offsetX: 200));

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.DragFrame("C_unused", 10, 0);

        Assert.Equal(0, vm.Project.Find("A")!.OffsetX);
        Assert.Equal(200, vm.Project.Find("B")!.OffsetX);
        Assert.Equal(["A", "B"], vm.SelectedNames);
    }

    [Fact]
    public void A_selected_child_anchored_to_a_selected_parent_is_not_moved_twice()
    {
        var vm = ViewModel(
            Frame("Parent", width: 200, height: 200, offsetX: 0, offsetY: 0),
            Frame("Child", parent: "Parent", relativeTo: "Parent", width: 50, height: 50, offsetX: 10, offsetY: 10));

        var before = LayoutResolver.Resolve(vm.Project);

        vm.OnCanvasSelectionRequested("Parent");
        vm.OnCanvasSelectionRequested("Child", additive: true);
        vm.DragFrame("Child", 30, 0);

        Assert.Equal(30, vm.Project.Find("Parent")!.OffsetX);
        Assert.Equal(10, vm.Project.Find("Child")!.OffsetX);

        // The child still ends up exactly one delta from where it started, which is what dragging
        // a group has to mean even when the child only follows its parent.
        var after = LayoutResolver.Resolve(vm.Project);
        Assert.Equal(before.Rects["Child"].Left + 30, after.Rects["Child"].Left);
        Assert.Equal(before.Rects["Parent"].Left + 30, after.Rects["Parent"].Left);
    }

    [Fact]
    public void Deleting_one_member_of_a_multi_selection_keeps_the_rest()
    {
        var vm = ViewModel(Frame("A"), Frame("B"), Frame("C"));

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.OnCanvasSelectionRequested("C", additive: true);
        vm.DeleteFrame();

        Assert.Equal(["A", "B"], vm.SelectedNames);
        Assert.Equal("B", vm.SelectedName);
    }

    [Fact]
    public void Adding_a_new_object_replaces_the_whole_selection()
    {
        var vm = ViewModel(Frame("A"), Frame("B"));

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.AddFrame();

        Assert.Single(vm.SelectedNames);
        Assert.Equal(vm.SelectedName, vm.SelectedNames[0]);
    }

    [Fact]
    public void Switching_design_state_drops_selected_objects_the_new_state_does_not_show()
    {
        var vm = ViewModel(Frame("A"), Frame("B"));

        vm.StateNameDraft = "Idle";
        vm.CreateDesignState();
        vm.StateNameDraft = "Highlight";
        vm.CreateDesignState();

        vm.OnCanvasSelectionRequested("A");
        SelectState(vm, vm.DesignStateOptions.Single(choice => choice.Name == "Idle"));
        vm.AssignSelectionToSelectedState();
        vm.OnCanvasSelectionRequested("B");
        SelectState(vm, vm.DesignStateOptions.Single(choice => choice.Name == "Highlight"));
        vm.AssignSelectionToSelectedState();

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        Assert.Equal(["A", "B"], vm.SelectedNames);

        vm.ActiveDesignState = vm.DesignStateOptions.Single(choice => choice.Name == "Idle");

        Assert.Equal(["A"], vm.SelectedNames);
        Assert.Equal("A", vm.SelectedName);
        Assert.Contains("left the selection", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void An_object_in_all_states_survives_a_state_switch_because_it_is_in_every_state()
    {
        var vm = ViewModel(Frame("A"), Frame("B"));

        vm.StateNameDraft = "Highlight";
        vm.CreateDesignState();

        // B has no membership, which means All States, not "no state".
        vm.OnCanvasSelectionRequested("B");
        SelectState(vm, vm.DesignStateOptions.Single(choice => choice.Name == "Highlight"));
        vm.AssignSelectionToSelectedState();

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.ActiveDesignState = vm.DesignStateOptions.Single(choice => choice.Name == "Highlight");

        Assert.Equal(["A", "B"], vm.SelectedNames);
    }

    [Fact]
    public void Canvas_reports_an_additive_click_for_ctrl_shift_and_meta_but_not_for_a_plain_click()
    {
        Assert.True(LayoutCanvas.IsAdditiveModifier(KeyModifiers.Control));
        Assert.True(LayoutCanvas.IsAdditiveModifier(KeyModifiers.Meta));
        Assert.True(LayoutCanvas.IsAdditiveModifier(KeyModifiers.Shift));
        Assert.False(LayoutCanvas.IsAdditiveModifier(KeyModifiers.None));
        Assert.False(LayoutCanvas.IsAdditiveModifier(KeyModifiers.Alt));
    }

    [Fact]
    public void Tree_nodes_report_their_selection_so_a_multi_selection_is_visible_in_the_tree()
    {
        var vm = ViewModel(Frame("A"), Frame("B"), Frame("C"));

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);

        var a = Assert.Single(vm.TreeRoots, node => node.Name == "A");
        var b = Assert.Single(vm.TreeRoots, node => node.Name == "B");
        var c = Assert.Single(vm.TreeRoots, node => node.Name == "C");

        Assert.True(a.IsSelected);
        Assert.False(a.IsPrimarySelection);
        Assert.True(b.IsSelected);
        Assert.True(b.IsPrimarySelection);
        Assert.False(c.ShowsSelectionBadge);
        Assert.NotEqual(a.SelectionBadge, b.SelectionBadge);
    }

    [Fact]
    public void Tree_selection_follows_the_primary_without_collapsing_the_multi_selection()
    {
        var vm = ViewModel(Frame("A"), Frame("B"), Frame("C"));

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.OnCanvasSelectionRequested("C", additive: true);

        // Every refresh re-publishes the tree node; that must not be mistaken for a user click.
        vm.Select("C");

        Assert.Equal(["C"], vm.SelectedNames);
        Assert.Equal("C", vm.SelectedTreeNode?.Name);
    }

    [Fact]
    public void The_selection_is_view_state_and_is_not_written_into_the_saved_document()
    {
        var vm = ViewModel(Frame("A"), Frame("B"));

        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.ArrangeSelection(SelectionArrangeCommand.AlignLeft);

        var path = Path.Combine(Path.GetTempPath(), $"frameforge-multiselect-{Guid.NewGuid():N}.fforge.json");
        vm.SaveToFile(path);
        try
        {
            var reloaded = new MainWindowViewModel(
                Path.Combine(Path.GetTempPath(), $"frameforge-settings-{Guid.NewGuid():N}.json"),
                stockTemplates: new PassThroughStockTemplates());
            reloaded.OpenFromFile(path);

            Assert.Empty(reloaded.SelectedNames);
            Assert.Equal(vm.Project.Find("A")!.OffsetX, reloaded.Project.Find("A")!.OffsetX);
            Assert.Equal(2, reloaded.Project.Frames.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void SelectState(MainWindowViewModel vm, DesignStateChoice choice) =>
        vm.SelectedAuthoredState = choice;

    private static FrameDef Frame(
        string name,
        string? parent = null,
        double width = 100,
        double height = 100,
        string? relativeTo = null,
        double offsetX = 0,
        double offsetY = 0,
        bool setAllPoints = false) => new()
        {
            Name = name,
            Parent = parent,
            Width = width,
            Height = height,
            Point = AnchorPoint.TOPLEFT,
            RelativeTo = relativeTo,
            RelativePoint = AnchorPoint.TOPLEFT,
            OffsetX = offsetX,
            OffsetY = offsetY,
            SetAllPoints = setAllPoints,
        };

    private static MainWindowViewModel ViewModel(params FrameDef[] frames)
    {
        var vm = new MainWindowViewModel(
            Path.Combine(Path.GetTempPath(), $"frameforge-multiselect-{Guid.NewGuid():N}.json"),
            stockTemplates: new PassThroughStockTemplates());
        vm.Load(ProjectFactory.Create("multi-selection", frames, new Screen(1024, 768)),
            null, "Loaded.");
        return vm;
    }

    /// <summary>
    /// A locked stock label and an editable custom label placed inside its box, which is the real
    /// "line my new element up with the Blizzard framework" case.
    /// </summary>
    private static MainWindowViewModel LockedReferenceViewModel()
    {
        var vm = ViewModel(
            new FrameDef
            {
                Name = "Stock", Width = 200, Height = 100, OffsetX = -300, OffsetY = 200,
            },
            new FrameDef
            {
                Name = "Custom", Width = 120, Height = 40, OffsetX = -278, OffsetY = 176,
            });
        vm.SetElementLocked("Stock", true);
        vm.IsDirty = false;
        return vm;
    }

    private static void Select(MainWindowViewModel vm, params string[] names)
    {
        foreach (var name in names)
            vm.ToggleSelection(name);
    }

    [Fact]
    public void LockedReferenceEnablesAlignForTwoSelectedObjects()
    {
        var vm = LockedReferenceViewModel();
        vm.ToggleSelection("Stock");
        vm.ToggleSelection("Custom");

        Assert.True(vm.IsMultiSelection);
        Assert.True(vm.CanAlignSelection);
    }

    [Fact]
    public void AlignTopWithLockedReferenceMovesOnlyTheEditableObject()
    {
        var vm = LockedReferenceViewModel();
        Select(vm, "Custom", "Stock");
        Assert.Equal("Stock", vm.SelectedName);

        var stockBefore = vm.Project.Find("Stock");
        vm.ArrangeSelection(SelectionArrangeCommand.AlignTop);
        var stockAfter = vm.Project.Find("Stock");

        Assert.Equal(stockBefore, stockAfter);
        Assert.Contains("1 object moved", vm.Status);
    }

    [Fact]
    public void AlignLeftWithLockedReferenceWorksRegardlessOfSelectionOrder()
    {
        var vm = LockedReferenceViewModel();
        Select(vm, "Stock", "Custom");
        vm.ArrangeSelection(SelectionArrangeCommand.AlignLeft);
        var after = vm.Layout.Rects;
        Assert.Equal(after["Stock"].Left, after["Custom"].Left, 6);
        Assert.Contains("1 object moved", vm.Status);

        var vm2 = LockedReferenceViewModel();
        Select(vm2, "Custom", "Stock");
        vm2.ArrangeSelection(SelectionArrangeCommand.AlignLeft);
        var after2 = vm2.Layout.Rects;
        Assert.Equal(after2["Stock"].Left, after2["Custom"].Left, 6);
    }

    [Fact]
    public void AllLockedSelectionDisablesAlignAndRefusesToChangeProject()
    {
        var vm = ViewModel(
            new FrameDef { Name = "L1", Width = 100, Height = 100 },
            new FrameDef { Name = "L2", Width = 100, Height = 100, OffsetX = 300 });
        vm.SetElementLocked("L1", true);
        vm.SetElementLocked("L2", true);
        vm.IsDirty = false;
        vm.ToggleSelection("L1");
        vm.ToggleSelection("L2");

        Assert.False(vm.CanAlignSelection);
        var before = ProjectCodec.Serialize(vm.Project);
        vm.ArrangeSelection(SelectionArrangeCommand.AlignTop);
        Assert.Equal(before, ProjectCodec.Serialize(vm.Project));
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void AllLockedSelectionDisablesDistributeAsWell()
    {
        var vm = ViewModel(
            new FrameDef { Name = "L1", Width = 100, Height = 100 },
            new FrameDef { Name = "L2", Width = 100, Height = 100, OffsetX = 150 },
            new FrameDef { Name = "L3", Width = 100, Height = 100, OffsetX = 300 });
        vm.SetElementLocked("L1", true);
        vm.SetElementLocked("L2", true);
        vm.SetElementLocked("L3", true);
        vm.IsDirty = false;
        Select(vm, "L1", "L2", "L3");

        Assert.False(vm.CanAlignSelection);
        Assert.False(vm.CanDistributeSelection);
        var before = ProjectCodec.Serialize(vm.Project);
        vm.ArrangeSelection(SelectionArrangeCommand.DistributeHorizontal);
        Assert.Equal(before, ProjectCodec.Serialize(vm.Project));
        Assert.False(vm.IsDirty);
    }

    private sealed class PassThroughStockTemplates : IStockTemplateResolver
    {
        public int Generation => 1;
        public IReadOnlyList<StockDefinitionDiagnostic> Diagnostics => [];
        public IReadOnlyList<AssetMaterializationResult> MaterializeRequired(WowClientValidation client) => [];
        public void Reload() { }
        public Project ApplyEffectiveGeometry(Project declaredProject) => declaredProject;
        public StockFontStyle? ResolveFont(string? name) => null;
        public StockButtonStyle? ResolveButton(string? name) => null;
        public StockExternalFrameStyle? ResolveExternalFrame(string? name) => null;
        public IReadOnlyList<string> Describe(FrameDef frame) => [];
        public IReadOnlyList<string> ExpandedChildNames(string instanceName, StockButtonStyle style) => [];
        public double MeasureText(StockFontStyle style, string text) => text.Length * 6;
    }

}
