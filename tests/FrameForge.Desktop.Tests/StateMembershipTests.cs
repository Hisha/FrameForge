using FrameForge.Core;
using FrameForge.Core.Geometry;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using Xunit;

namespace FrameForge.Desktop.Tests;

/// <summary>
/// DESIGN state membership through the "Choose States..." chooser: two semantic modes, bulk
/// assignment across a multi-selection, locks, visibility, dirty-state and persistence.
/// </summary>
/// <remarks>
/// The representative project mirrors the manual Native Hunts case: three lower panels that belong
/// to the active-hunt states but not Idle, plus a header that is All States. The state names come
/// from the project, never from the code under test, so nothing here hardcodes a particular
/// addon's vocabulary.
/// </remarks>
public sealed class StateMembershipTests
{
    private static readonly string[] Panels = ["Hunt_Panel_Top", "Hunt_Panel_Middle", "Hunt_Panel_Bottom"];

    [Fact]
    public void Chooser_lists_the_projects_current_authored_states_and_picks_up_new_ones()
    {
        var vm = HuntProject();
        vm.Select("Hunt_Panel_Top");

        var chooser = vm.CreateStateMembershipChooser();
        Assert.NotNull(chooser);
        Assert.Equal(["All States", "Idle", "Tracking", "Located"],
            chooser.Options.Select(option => option.Name).ToArray());

        vm.StateNameDraft = "Complete";
        vm.CreateDesignState();

        var later = vm.CreateStateMembershipChooser()!;
        Assert.Equal(["All States", "Idle", "Tracking", "Located", "Complete"],
            later.Options.Select(option => option.Name).ToArray());
    }

    [Fact]
    public void One_explicit_state_is_applied_exactly_to_the_selected_object()
    {
        var vm = HuntProject();
        vm.Select("Hunt_Panel_Top");
        var chooser = vm.CreateStateMembershipChooser()!;
        Row(chooser, "Tracking").IsChecked = true;

        Assert.True(chooser.CanApply);
        Assert.True(vm.ApplyStateMembership(chooser));

        Assert.Equal(["tracking"], vm.Project.Editor.DesignObjectFor("Hunt_Panel_Top")!.StateIds);
        Assert.Equal("Tracking", vm.SelectedStateMembership);
    }

    [Fact]
    public void Several_explicit_states_are_applied_as_one_exact_membership()
    {
        var vm = HuntProject();
        vm.Select("Hunt_Panel_Top");
        var chooser = vm.CreateStateMembershipChooser()!;
        Row(chooser, "Tracking").IsChecked = true;
        Row(chooser, "Located").IsChecked = true;

        Assert.True(vm.ApplyStateMembership(chooser));

        var ids = vm.Project.Editor.DesignObjectFor("Hunt_Panel_Top")!.StateIds;
        Assert.Equal(2, ids.Count);
        Assert.Contains("tracking", ids);
        Assert.Contains("located", ids);
        Assert.DoesNotContain("idle", ids);
        Assert.Equal("Tracking, Located", vm.SelectedStateMembership);
    }

    [Fact]
    public void All_States_and_explicit_membership_are_mutually_exclusive_and_never_both_persisted()
    {
        var vm = HuntProject();
        vm.Select("Hunt_Panel_Top");
        var chooser = vm.CreateStateMembershipChooser()!;

        // An object without membership starts as All States, which clears and disables the rows.
        Assert.True(chooser.IsAllStates);
        Assert.All(chooser.Options.Skip(1), option =>
        {
            Assert.False(option.IsChecked);
            Assert.False(option.IsEnabled);
        });

        // Choosing a state turns All States off...
        chooser.Options[0].IsChecked = false;
        Assert.All(chooser.Options.Skip(1), option => Assert.True(option.IsEnabled));
        Row(chooser, "Tracking").IsChecked = true;
        Assert.False(chooser.IsAllStates);
        Assert.True(vm.ApplyStateMembership(chooser));
        Assert.Equal(["tracking"], vm.Project.Editor.DesignObjectFor("Hunt_Panel_Top")!.StateIds);

        // ...and going back to All States clears the explicit rows before anything is written.
        var again = vm.CreateStateMembershipChooser()!;
        Assert.False(again.IsAllStates);
        Assert.True(Row(again, "Tracking").IsChecked);
        again.Options[0].IsChecked = true;
        Assert.True(again.IsAllStates);
        Assert.All(again.Options.Skip(1), option => Assert.False(option.IsChecked));
        Assert.True(again.TryGetResult(out var allStates, out var stateIds));
        Assert.True(allStates);
        Assert.Empty(stateIds);

        Assert.True(vm.ApplyStateMembership(again));
        Assert.Empty(vm.Project.Editor.DesignObjectFor("Hunt_Panel_Top")!.StateIds);
        Assert.Equal("All States", vm.SelectedStateMembership);
    }

    [Fact]
    public void An_explicit_membership_does_not_follow_states_created_afterwards()
    {
        var vm = HuntProject();
        vm.Select("Hunt_Panel_Top");
        var chooser = vm.CreateStateMembershipChooser()!;
        Row(chooser, "Tracking").IsChecked = true;
        Assert.True(vm.ApplyStateMembership(chooser));

        vm.StateNameDraft = "Complete";
        vm.CreateDesignState();

        Assert.Equal(["tracking"], vm.Project.Editor.DesignObjectFor("Hunt_Panel_Top")!.StateIds);
        vm.ActiveDesignState = vm.DesignStateOptions.Single(option => option.Name == "Complete");
        Assert.False(vm.Layout.Frames["Hunt_Panel_Top"].EffectiveVisible);
    }

    [Fact]
    public void An_all_states_object_appears_in_states_created_afterwards()
    {
        var vm = HuntProject();
        vm.Select("Header");
        Assert.Equal("All States", vm.SelectedStateMembership);

        vm.StateNameDraft = "Complete";
        vm.CreateDesignState();
        vm.ActiveDesignState = vm.DesignStateOptions.Single(option => option.Name == "Complete");

        Assert.True(vm.Layout.Frames["Header"].EffectiveVisible);
    }

    [Fact]
    public void A_tracking_and_located_object_is_absent_while_Idle_is_the_active_state()
    {
        var vm = PanelsInTrackingAndLocated();

        vm.ActiveDesignState = vm.DesignStateOptions.Single(option => option.Name == "Idle");

        foreach (var panel in Panels)
            Assert.False(vm.Layout.Frames[panel].EffectiveVisible);
        Assert.True(vm.Layout.Frames["Header"].EffectiveVisible);
    }

    [Fact]
    public void A_tracking_and_located_object_is_visible_while_Tracking_is_the_active_state()
    {
        var vm = PanelsInTrackingAndLocated();

        vm.ActiveDesignState = vm.DesignStateOptions.Single(option => option.Name == "Tracking");

        foreach (var panel in Panels)
            Assert.True(vm.Layout.Frames[panel].EffectiveVisible);
        Assert.True(vm.Layout.Frames["Header"].EffectiveVisible);
    }

    [Fact]
    public void A_tracking_and_located_object_is_visible_while_Located_is_the_active_state()
    {
        var vm = PanelsInTrackingAndLocated();

        vm.ActiveDesignState = vm.DesignStateOptions.Single(option => option.Name == "Located");

        foreach (var panel in Panels)
            Assert.True(vm.Layout.Frames[panel].EffectiveVisible);
        Assert.True(vm.Layout.Frames["Header"].EffectiveVisible);
    }

    [Fact]
    public void Save_and_reopen_preserves_the_exact_explicit_membership()
    {
        var vm = PanelsInTrackingAndLocated();
        var path = Path.Combine(Path.GetTempPath(), $"frameforge-membership-{Guid.NewGuid():N}.fforge.json");
        try
        {
            Assert.True(vm.SaveToFile(path));
            var reopened = new MainWindowViewModel(SettingsPath(), stockTemplates: new PassThroughStockTemplates());
            reopened.OpenFromFile(path);

            foreach (var panel in Panels)
            {
                Assert.Equal(["tracking", "located"], reopened.Project.Editor.DesignObjectFor(panel)!.StateIds);
                Assert.DoesNotContain("idle", reopened.Project.Editor.DesignObjectFor(panel)!.StateIds);
            }

            reopened.Select("Hunt_Panel_Top");
            Assert.Equal("Tracking, Located", reopened.SelectedStateMembership);

            reopened.ActiveDesignState = reopened.DesignStateOptions.Single(option => option.Name == "Idle");
            Assert.False(reopened.Layout.Frames["Hunt_Panel_Top"].EffectiveVisible);
            reopened.ActiveDesignState = reopened.DesignStateOptions.Single(option => option.Name == "Tracking");
            Assert.True(reopened.Layout.Frames["Hunt_Panel_Top"].EffectiveVisible);
            reopened.ActiveDesignState = reopened.DesignStateOptions.Single(option => option.Name == "Located");
            Assert.True(reopened.Layout.Frames["Hunt_Panel_Top"].EffectiveVisible);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Cancelling_the_chooser_changes_nothing_and_leaves_the_project_clean()
    {
        var vm = HuntProject();
        vm.Select("Hunt_Panel_Top");
        var before = ProjectCodec.Serialize(vm.Project);

        var chooser = vm.CreateStateMembershipChooser()!;
        Row(chooser, "Tracking").IsChecked = true;

        // Cancel discards the chooser without calling ApplyStateMembership.
        Assert.False(vm.IsDirty);
        Assert.Equal(before, ProjectCodec.Serialize(vm.Project));
        Assert.Equal("All States", vm.SelectedStateMembership);

        // A chooser opened afterwards sees the untouched membership, not the abandoned draft.
        var fresh = vm.CreateStateMembershipChooser()!;
        Assert.True(fresh.IsAllStates);
        Assert.False(Row(fresh, "Tracking").IsChecked);
    }

    [Fact]
    public void A_changed_apply_marks_the_project_dirty()
    {
        var vm = HuntProject();
        Assert.False(vm.IsDirty);
        vm.Select("Hunt_Panel_Top");
        var chooser = vm.CreateStateMembershipChooser()!;
        Row(chooser, "Tracking").IsChecked = true;

        Assert.True(vm.ApplyStateMembership(chooser));
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void Applying_the_membership_an_object_already_has_is_a_no_op()
    {
        var vm = HuntProject();
        vm.Select("Hunt_Panel_Top");
        var first = vm.CreateStateMembershipChooser()!;
        Row(first, "Tracking").IsChecked = true;
        Assert.True(vm.ApplyStateMembership(first));
        vm.IsDirty = false;

        var project = vm.Project;
        var again = vm.CreateStateMembershipChooser()!;
        Assert.True(Row(again, "Tracking").IsChecked);

        Assert.False(vm.ApplyStateMembership(again));
        Assert.Same(project, vm.Project);
        Assert.False(vm.IsDirty);
        Assert.Contains("already", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_membership_cannot_be_applied()
    {
        var vm = HuntProject();
        vm.Select("Hunt_Panel_Top");
        var chooser = vm.CreateStateMembershipChooser()!;

        chooser.Options[0].IsChecked = false;

        Assert.False(chooser.CanApply);
        Assert.False(chooser.TryGetResult(out _, out _));
        Assert.False(vm.ApplyStateMembership(chooser));
        Assert.False(vm.IsDirty);
        Assert.Contains("Choose All States", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void One_apply_assigns_the_same_membership_to_every_selected_object()
    {
        var vm = HuntProject();
        vm.Select(Panels[0]);
        foreach (var panel in Panels.Skip(1))
            vm.ToggleSelection(panel);

        var chooser = vm.CreateStateMembershipChooser()!;
        Assert.Equal(3, chooser.TargetNames.Count);
        Row(chooser, "Tracking").IsChecked = true;
        Row(chooser, "Located").IsChecked = true;
        Assert.True(vm.ApplyStateMembership(chooser));

        foreach (var panel in Panels)
            Assert.Equal(["tracking", "located"], vm.Project.Editor.DesignObjectFor(panel)!.StateIds);

        // Bulk assignment touches only the selection: the header keeps no membership record.
        Assert.Null(vm.Project.Editor.DesignObjectFor("Header"));
        Assert.Equal("Tracking, Located", vm.SelectedStateMembership);
    }

    [Fact]
    public void Different_memberships_in_one_selection_report_Mixed_and_refuse_to_apply()
    {
        var vm = HuntProject();
        AssignSingle(vm, "Hunt_Panel_Top", "Tracking");
        AssignSingle(vm, "Hunt_Panel_Middle", "Located");
        vm.IsDirty = false;

        vm.Select("Hunt_Panel_Top");
        vm.ToggleSelection("Hunt_Panel_Middle");

        Assert.Equal("Mixed", vm.SelectedStateMembership);
        var chooser = vm.CreateStateMembershipChooser()!;
        Assert.True(chooser.IsMixed);
        Assert.False(chooser.CanApply);
        Assert.All(chooser.Options, option => Assert.False(option.IsChecked));
        Assert.False(string.IsNullOrEmpty(chooser.ValidationMessage));
        Assert.False(vm.ApplyStateMembership(chooser));
        Assert.False(vm.IsDirty);
        Assert.Equal(["tracking"], vm.Project.Editor.DesignObjectFor("Hunt_Panel_Top")!.StateIds);
        Assert.Equal(["located"], vm.Project.Editor.DesignObjectFor("Hunt_Panel_Middle")!.StateIds);
    }

    [Fact]
    public void A_locked_object_is_never_mutated_by_the_chooser()
    {
        var vm = HuntProject();
        vm.Select("Hunt_Panel_Top");
        vm.SetElementLocked("Hunt_Panel_Top", true);
        vm.IsDirty = false;

        var chooser = vm.CreateStateMembershipChooser()!;
        Assert.Empty(chooser.EditableTargetNames);
        Assert.False(chooser.CanApply);
        Row(chooser, "Tracking").IsChecked = true;

        Assert.False(vm.ApplyStateMembership(chooser));
        Assert.False(vm.IsDirty);
        Assert.True(vm.Project.Editor.DesignObjectFor("Hunt_Panel_Top")?.StateIds.Count is null or 0);
        Assert.Contains("locked", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_locked_plus_editable_selection_changes_only_the_editable_objects()
    {
        var vm = HuntProject();
        vm.SetElementLocked("Hunt_Panel_Middle", true);
        vm.IsDirty = false;
        vm.Select("Hunt_Panel_Top");
        vm.ToggleSelection("Hunt_Panel_Middle");

        var chooser = vm.CreateStateMembershipChooser()!;
        Assert.Single(chooser.EditableTargetNames);
        Assert.Equal(1, chooser.LockedTargetCount);
        Assert.Contains("locked", chooser.LockNote, StringComparison.OrdinalIgnoreCase);
        Row(chooser, "Tracking").IsChecked = true;

        Assert.True(vm.ApplyStateMembership(chooser));
        Assert.Equal(["tracking"], vm.Project.Editor.DesignObjectFor("Hunt_Panel_Top")!.StateIds);
        Assert.True(vm.Project.Editor.DesignObjectFor("Hunt_Panel_Middle")?.StateIds.Count is null or 0);
        Assert.Contains("locked", vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void Authored_membership_leaves_the_imported_preview_states_alone()
    {
        var vm = new MainWindowViewModel(SettingsPath(), stockTemplates: new PassThroughStockTemplates());
        vm.Load(FrameXmlImporter.ImportFile(FixturePath()).Project!, null, "test");
        var labels = vm.PreviewStateOptions.Select(state => state.Label).ToArray();
        var selectedPreview = vm.SelectedPreviewState?.Id;
        Assert.Equal(5, labels.Length);

        vm.NewObjectName = "Panels";
        vm.AddDesignFrame();
        var added = vm.SelectedName!;
        vm.StateNameDraft = "Tracking";
        vm.CreateDesignState();
        var chooser = vm.CreateStateMembershipChooser()!;
        Row(chooser, "Tracking").IsChecked = true;
        Assert.True(vm.ApplyStateMembership(chooser));

        Assert.Equal(labels, vm.PreviewStateOptions.Select(state => state.Label).ToArray());
        Assert.Equal(selectedPreview, vm.SelectedPreviewState?.Id);
        Assert.Equal(["tracking"], vm.Project.Editor.DesignObjectFor(added)!.StateIds);
    }

    [Fact]
    public void A_project_without_authored_states_reads_as_All_States_and_stays_compatible()
    {
        var vm = new MainWindowViewModel(SettingsPath(), stockTemplates: new PassThroughStockTemplates());
        vm.Load(ProjectFactory.Create("legacy", [Frame("A"), Frame("B")]), null, "test");
        vm.Select("A");

        Assert.Equal("All States", vm.SelectedStateMembership);
        var chooser = vm.CreateStateMembershipChooser()!;
        var only = Assert.Single(chooser.Options);
        Assert.Equal("All States", only.Name);
        Assert.True(only.IsChecked);
        Assert.True(chooser.CanApply);

        // Already All States, so applying it is a no-op rather than a rewrite.
        Assert.False(vm.ApplyStateMembership(chooser));
        Assert.False(vm.IsDirty);

        vm.StateNameDraft = "Idle";
        vm.CreateDesignState();
        vm.ActiveDesignState = vm.DesignStateOptions.Single(option => option.Name == "Idle");
        Assert.True(vm.Layout.Frames["A"].EffectiveVisible);
        Assert.True(vm.Layout.Frames["B"].EffectiveVisible);
    }

    /// <summary>Applies one explicit state to one object, the way a single-object edit would.</summary>
    private static void AssignSingle(MainWindowViewModel vm, string name, string stateName)
    {
        vm.Select(name);
        var chooser = vm.CreateStateMembershipChooser()!;
        Row(chooser, stateName).IsChecked = true;
        Assert.True(vm.ApplyStateMembership(chooser));
    }

    /// <summary>The Native Hunts case: three lower panels in Tracking and Located, header in all.</summary>
    private static MainWindowViewModel PanelsInTrackingAndLocated()
    {
        var vm = HuntProject();
        vm.Select(Panels[0]);
        foreach (var panel in Panels.Skip(1))
            vm.ToggleSelection(panel);

        var chooser = vm.CreateStateMembershipChooser()!;
        Row(chooser, "Tracking").IsChecked = true;
        Row(chooser, "Located").IsChecked = true;
        Assert.True(vm.ApplyStateMembership(chooser));
        vm.IsDirty = false;
        return vm;
    }

    /// <summary>Three lower panels, a header, and the authored states Idle / Tracking / Located.</summary>
    private static MainWindowViewModel HuntProject()
    {
        var vm = ViewModel(
            Frame("Hunt_Panel_Top", offsetX: 0, offsetY: 200),
            Frame("Hunt_Panel_Middle", offsetX: 0, offsetY: 0),
            Frame("Hunt_Panel_Bottom", offsetX: 0, offsetY: -200),
            Frame("Header", offsetX: 0, offsetY: 320));
        foreach (var state in new[] { "Idle", "Tracking", "Located" })
        {
            vm.StateNameDraft = state;
            vm.CreateDesignState();
        }
        vm.IsDirty = false;
        return vm;
    }

    private static StateMembershipOption Row(StateMembershipChooser chooser, string name) =>
        chooser.Options.Single(option => option.Name == name);

    private static FrameDef Frame(
        string name,
        string? parent = null,
        double width = 240,
        double height = 60,
        string? relativeTo = null,
        double offsetX = 0,
        double offsetY = 0) => new()
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
        };

    private static MainWindowViewModel ViewModel(params FrameDef[] frames)
    {
        var vm = new MainWindowViewModel(SettingsPath(), stockTemplates: new PassThroughStockTemplates());
        vm.Load(ProjectFactory.Create("state-membership", frames, new Screen(1024, 768)), null, "Loaded.");
        return vm;
    }

    private static string SettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"frameforge-membership-{Guid.NewGuid():N}.json");

    private static string FixturePath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "FrameForge.Core.Tests", "Fixtures", "NativeHuntsFrame.xml"));

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
