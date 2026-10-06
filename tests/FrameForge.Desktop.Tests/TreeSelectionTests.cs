using FrameForge.Core;
using FrameForge.Core.Models;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using Xunit;

namespace FrameForge.Desktop.Tests;

/// <summary>
/// The left tree as the authoritative source of exact-object selection.
/// </summary>
/// <remarks>
/// The tree's selected <c>FrameTreeNode</c> is a projection of the view model's selection, but the
/// binding is TwoWay: a user click writes <c>SelectedTreeNode</c> and must be honoured as a real
/// selection request. These tests pin the cases that used to fail when that write was treated as a
/// no-op (the dead reference-equality guard) and the cases that make the tree authoritative -
/// plain clicks replace, additive clicks toggle, and repeating a request never grows or clears.
/// </remarks>
public sealed class TreeSelectionTests
{
    [Fact]
    public void A_tree_binding_write_selects_the_clicked_object()
    {
        var vm = ViewModel(Frame("A"), Frame("B"));
        vm.OnCanvasSelectionRequested("A");
        Assert.Equal("A", vm.SelectedName);

        vm.SelectedTreeNode = vm.TreeRoots.Single(node => node.Name == "B");

        Assert.Equal("B", vm.SelectedName);
        Assert.Equal(["B"], vm.SelectedNames);
        Assert.False(vm.IsMultiSelection);
        Assert.Equal("B", vm.SelectedTreeNode?.Name);
    }

    [Fact]
    public void Writing_null_to_the_tree_node_clears_the_selection()
    {
        var vm = ViewModel(Frame("A"), Frame("B"));
        vm.OnCanvasSelectionRequested("A");

        vm.SelectedTreeNode = null;

        Assert.Null(vm.SelectedName);
        Assert.Empty(vm.SelectedNames);
    }

    [Fact]
    public void Re_publishing_the_selected_node_keeps_the_selection_single_and_unchanged()
    {
        var vm = ViewModel(Frame("A"), Frame("B"));
        vm.OnCanvasSelectionRequested("A");

        // The tree rebuilds on every refresh, so this is a fresh node instance that re-publishes
        // an already-selected object. A real click on the already-selected row must not build a
        // growing or multi selection; it just re-affirms the one object.
        vm.SelectedTreeNode = vm.TreeRoots.Single(node => node.Name == "A");

        Assert.Equal(["A"], vm.SelectedNames);
        Assert.False(vm.IsMultiSelection);
    }

    [Fact]
    public void A_plain_select_collapses_a_multi_selection_onto_the_clicked_object()
    {
        var vm = ViewModel(Frame("A"), Frame("B"), Frame("C"));
        vm.OnCanvasSelectionRequested("A");
        vm.OnCanvasSelectionRequested("B", additive: true);
        vm.OnCanvasSelectionRequested("C", additive: true);

        Assert.True(vm.IsMultiSelection);
        Assert.Equal("C", vm.SelectedName);

        vm.Select("C");

        Assert.Equal(["C"], vm.SelectedNames);
        Assert.False(vm.IsMultiSelection);
        Assert.Equal("C", vm.SelectedName);
    }

    [Fact]
    public void Clicking_a_search_filtered_node_selects_that_object()
    {
        var vm = ViewModel(Frame("Alpha"), Frame("BetaBar"));
        vm.TreeSearch = "beta";

        var visible = Assert.Single(vm.TreeRoots);
        Assert.Equal("BetaBar", visible.Name);

        vm.SelectedTreeNode = visible;

        Assert.Equal("BetaBar", vm.SelectedName);
        Assert.False(vm.IsMultiSelection);
    }

    [Fact]
    public void An_additive_tree_toggle_adds_and_removes_without_losing_the_rest()
    {
        var vm = ViewModel(Frame("A"), Frame("B"), Frame("C"));
        vm.OnCanvasSelectionRequested("A");

        vm.ToggleSelection("B");
        vm.ToggleSelection("C");
        Assert.Equal(["A", "B", "C"], vm.SelectedNames);

        vm.ToggleSelection("B");
        Assert.Equal(["A", "C"], vm.SelectedNames);
        Assert.Equal("C", vm.SelectedName);
    }

    private static FrameDef Frame(string name) => new()
    {
        Name = name,
        Width = 100,
        Height = 60,
        Point = AnchorPoint.TOPLEFT,
        RelativePoint = AnchorPoint.TOPLEFT,
    };

    private static MainWindowViewModel ViewModel(params FrameDef[] frames)
    {
        var vm = new MainWindowViewModel(
            Path.Combine(Path.GetTempPath(), $"frameforge-treeselection-{Guid.NewGuid():N}.json"),
            stockTemplates: new PassThroughStockTemplates());
        vm.Load(ProjectFactory.Create("tree-selection", frames, new Screen(1024, 768)),
            null, "Loaded.");
        return vm;
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