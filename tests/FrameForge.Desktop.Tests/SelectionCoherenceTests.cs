using System.Collections.Specialized;
using FrameForge.Core;
using FrameForge.Core.Models;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using Xunit;

namespace FrameForge.Desktop.Tests;

/// <summary>
/// Selection coherence: whoever is selected, every consumer must be identical, and a selection can
/// never be silently dropped while its inspector stays populated - the exact desynchronization that
/// produced a DESIGN panel showing only STATE MEMBERSHIP while the status line described a real
/// frame.
/// </summary>
/// <remarks>
/// The regression scenario is the live TreeView clearing its selected row whenever the tree's
/// <c>ItemsSource</c> is rebuilt (<c>TreeRoots.Clear()</c>). That write arrives through the TwoWay
/// binding while the tree projection is being re-published and must be ignored; before the selection
/// depth guard existed it ran <c>Select(null)</c> re-entrantly and cleared the model selection while
/// <c>Editor.Refresh(project, selection)</c> kept the inspector describing the old frame.
/// </remarks>
public sealed class SelectionCoherenceTests
{
    [Fact]
    public void A_tree_feedback_write_during_rebuild_leaves_the_selection_fully_intact()
    {
        var vm = ViewModel(CoherenceProject());
        vm.OnCanvasSelectionRequested("HuntBar");
        vm.TreeRoots.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                vm.SelectedTreeNode = null;
        };

        // The status bar authoring path re-runs the full RelaidOut publish while the simulated
        // TreeView is about to clear its rows. The new texture differs from the authored one so the
        // rebuild genuinely happens (an unchanged value short-circuits before RelaidOut).
        Assert.True(vm.SetSelectedStatusBarTextureFromWow("Interface/FriendsFrame/BG-StatusBar"));

        AssertCoherent(vm, "HuntBar");
        Assert.Equal("HuntBar", vm.SelectedTreeNode?.Name);
        Assert.True(vm.Editor.HasSelection);
        Assert.True(vm.ShowsSingleObjectEditors);
        Assert.True(vm.IsDesignStatusBarSelected);
        Assert.Equal("Interface/FriendsFrame/BG-StatusBar", vm.StatusBarTextureDraft);
        Assert.Contains("HuntBar", vm.CanvasSelectionNames);
    }

    [Fact]
    public void A_rebuild_under_a_pending_feedback_write_also_keeps_a_tree_selected_stock_frame()
    {
        var vm = ViewModel(CoherenceProject());
        vm.SelectedTreeNode = vm.TreeRoots.Single(node => node.Name == "StockBg");
        AssertCoherent(vm, "StockBg");
        vm.TreeRoots.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
                vm.SelectedTreeNode = null;
        };

        // Workspace switches re-publish the selection under the depth guard.
        vm.SetWorkspace(WorkspaceExperience.Inspect);
        AssertCoherent(vm, "StockBg");
        vm.SetWorkspace(WorkspaceExperience.Design);
        AssertCoherent(vm, "StockBg");

        Assert.Equal("StockBg", vm.SelectedTreeNode?.Name);
        Assert.True(vm.Editor.HasSelection);
        Assert.True(vm.IsSelectionLocked);
    }

    [Fact]
    public void Status_bar_to_stock_to_text_selections_are_coherent_at_every_step()
    {
        var vm = ViewModel(CoherenceProject());

        vm.OnCanvasSelectionRequested("HuntBar");
        AssertCoherent(vm, "HuntBar");
        Assert.True(vm.IsDesignStatusBarSelected);
        Assert.True(vm.Editor.HasSelection);

        vm.SelectedTreeNode = vm.TreeRoots.Single(node => node.Name == "StockBg");
        AssertCoherent(vm, "StockBg");
        Assert.True(vm.IsStockFrameworkSelected);
        Assert.True(vm.IsSelectionLocked);
        Assert.True(vm.Editor.HasSelection);
        Assert.True(vm.ShowsSingleObjectEditors);

        vm.OnCanvasSelectionRequested("Title");
        AssertCoherent(vm, "Title");
        Assert.Equal(FrameKind.FONTSTRING, vm.SelectedFrame!.Kind);
        Assert.True(vm.Editor.HasSelection);

        vm.OnCanvasSelectionRequested("HuntBar");
        AssertCoherent(vm, "HuntBar");
        Assert.True(vm.CanEditDesignStatusBar);
        Assert.NotEmpty(vm.StatusBarTextureDraft);
    }

    [Fact]
    public void A_click_on_the_already_selected_object_keeps_the_inspector_populated()
    {
        var vm = ViewModel(CoherenceProject());
        vm.OnCanvasSelectionRequested("HuntBar");

        vm.OnCanvasSelectionRequested("HuntBar");
        vm.Select("HuntBar");
        vm.SelectedTreeNode = vm.TreeRoots.Single(node => node.Name == "HuntBar");

        AssertCoherent(vm, "HuntBar");
        Assert.Equal(["HuntBar"], vm.SelectedNames);
        Assert.False(vm.IsMultiSelection);
        Assert.True(vm.CanEditDesignStatusBar);
        Assert.NotEmpty(vm.StatusBarValueDraft);
    }

    [Fact]
    public void An_empty_canvas_click_clears_to_an_explicit_no_selection_and_one_click_recovers()
    {
        var vm = ViewModel(CoherenceProject());
        vm.OnCanvasSelectionRequested("HuntBar");
        AssertCoherent(vm, "HuntBar");

        vm.Select(null);

        AssertCoherent(vm);
        Assert.Null(vm.SelectedTreeNode);
        Assert.Empty(vm.SelectedNames);
        Assert.False(vm.Editor.HasSelection);
        Assert.False(vm.ShowsSingleObjectEditors);
        Assert.False(vm.IsDesignStatusBarSelected);

        vm.OnCanvasSelectionRequested("HuntBar");
        AssertCoherent(vm, "HuntBar");
        Assert.True(vm.CanEditDesignStatusBar);
    }

    [Fact]
    public void Overlapping_objects_keep_the_exact_tree_selection_authoritative()
    {
        var vm = ViewModel(CoherenceProject());

        vm.OnCanvasSelectionRequested("StockBg");
        AssertCoherent(vm, "StockBg");

        // A later plain click on the overlapping custom icon replaces the exact object.
        vm.OnCanvasSelectionRequested("Icon");
        AssertCoherent(vm, "Icon");
        Assert.Equal(["Icon"], vm.SelectedNames);

        // The tree row that represents the primary is marked, and re-clicking the lower
        // (stock) object commits that exact row again.
        vm.SelectedTreeNode = vm.TreeRoots.Single(node => node.Name == "StockBg");
        AssertCoherent(vm, "StockBg");
        Assert.True(vm.Layout.Frames.ContainsKey("StockBg"));
        Assert.True(vm.TreeRoots.Single(node => node.Name == "StockBg").IsPrimarySelection);
    }

    [Fact]
    public void A_locked_stock_selection_is_selectable_for_reference_but_not_editable()
    {
        var vm = ViewModel(CoherenceProject());
        vm.SelectedTreeNode = vm.TreeRoots.Single(node => node.Name == "StockBg");
        AssertCoherent(vm, "StockBg");

        Assert.True(vm.IsSelectionLocked);
        Assert.True(vm.IsStockFrameworkSelected);
        Assert.False(vm.CanEditSelection);
        Assert.False(vm.CanEditDesignStatusBar);
        Assert.Contains("StockBg", vm.DesignLockedSummary, StringComparison.Ordinal);

        var before = vm.Project.Editor.DesignObjectFor("StockBg");
        vm.DesignNameDraft = "Mutated label";
        Assert.Equal(before, vm.Project.Editor.DesignObjectFor("StockBg"));
        Assert.False(vm.Project.Editor.DesignObjectFor("StockBg")?.DisplayName is "Mutated label");
    }

    [Fact]
    public void A_locked_status_bar_refuses_a_bar_texture_change_with_a_diagnostic()
    {
        var vm = ViewModel(LockedStockBarProject());
        vm.SelectedTreeNode = vm.TreeRoots.Single(node => node.Name == "StockBar");
        AssertCoherent(vm, "StockBar");
        Assert.True(vm.IsSelectionLocked);

        Assert.False(vm.SetSelectedStatusBarTextureFromWow("Interface/TargetingFrame/UI-StatusBar"));
        Assert.Contains("locked", vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Null(vm.Project.Find("StockBar")!.Visual?.StatusBar?.BarTexture);
    }

    private static void AssertCoherent(MainWindowViewModel vm, string? expectedPrimary = null)
    {
        var name = vm.SelectedName;
        Assert.Equal(vm.SelectedNames.Count > 0 ? vm.SelectedNames[^1] : null, name);
        if (expectedPrimary is not null)
            Assert.Equal(expectedPrimary, name);
        Assert.Equal(name is not null, vm.Editor.HasSelection);
        Assert.Equal(name, vm.Editor.Frame?.Name);
        Assert.Equal(name, vm.SelectedTreeNode?.Name);
        Assert.Equal(vm.SelectedNames.Count == 1, vm.ShowsSingleObjectEditors);
        Assert.Equal(vm.SelectedNames.Count > 1, vm.IsMultiSelection);
        Assert.Equal(name is not null, vm.CanvasSelectionNames.Contains(name ?? string.Empty));
    }

    private static Project CoherenceProject() => ProjectFactory.Create("coherence", new[]
    {
        StatusBar("HuntBar", "Interface/TargetingFrame/UI-StatusBar"),
        FontString("Title", "Native Hunt"),
        Texture("Icon"),
        Texture("StockBg"),
    }, new Screen(1024, 768)) with
    {
        Editor = new EditorMetadata
        {
            Groups =
            [
                new EditorGroup
                {
                    Name = "Blizzard Dungeon Finder Frame", Members = ["StockBg"], Locked = true,
                    Concept = "stock-framework",
                },
            ],
            DesignObjects =
            [
                new DesignObjectMetadata { FrameName = "HuntBar", DisplayName = "Hunt Mana Bar" },
                new DesignObjectMetadata { FrameName = "Title", DisplayName = "Hunt Title" },
                new DesignObjectMetadata { FrameName = "Icon", DisplayName = "Hunt Icon" },
            ],
            DesignOrder = ["HuntBar", "Title", "Icon"],
        },
    };

    private static Project LockedStockBarProject() => ProjectFactory.Create("locked-stock-bar", new[]
    {
        StatusBar("HuntBar", "Interface/TargetingFrame/UI-StatusBar"),
        StatusBar("StockBar", null),
    }, new Screen(1024, 768)) with
    {
        Editor = new EditorMetadata
        {
            Groups =
            [
                new EditorGroup
                {
                    Name = "Blizzard Status Bar", Members = ["StockBar"], Locked = true,
                    Concept = "stock-framework",
                },
            ],
            DesignObjects = [new DesignObjectMetadata { FrameName = "HuntBar", DisplayName = "Hunt Bar" }],
            DesignOrder = ["HuntBar"],
        },
    };

    private static FrameDef StatusBar(string name, string? texture, double defaultValue = 50) => new()
    {
        Name = name,
        Kind = FrameKind.STATUSBAR,
        Width = 120,
        Height = 28,
        Visual = new FrameVisual { StatusBar = new StatusBarVisual(0, 100, defaultValue, texture) },
    };

    private static FrameDef FontString(string name, string text) => new()
    {
        Name = name,
        Kind = FrameKind.FONTSTRING,
        Width = 120,
        Height = 28,
        Visual = new FrameVisual { Text = new TextVisual(text, "CENTER", "MIDDLE") },
    };

    private static FrameDef Texture(string name) => new()
    {
        Name = name,
        Kind = FrameKind.TEXTURE,
        Width = 120,
        Height = 80,
        Visual = new FrameVisual { Texture = new TextureVisual(null, Color: new ColorRgba(0, 0.5f, 1, 1)) },
    };

    private static MainWindowViewModel ViewModel(Project project)
    {
        var vm = new MainWindowViewModel(
            Path.Combine(Path.GetTempPath(), $"frameforge-coherence-{Guid.NewGuid():N}.json"),
            stockTemplates: new PassThroughStockTemplates());
        vm.Load(project, null, "Loaded.");
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