using FrameForge.Core.Geometry;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Preview;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class PreviewStateTests
{
    private readonly PreviewStateRegistry _registry = new();
    private readonly Project _native = FrameXmlImporter.ImportFile(FixturePath()).Project!;

    [Fact]
    public void Xml_defaults_has_no_overrides_and_returns_source_unchanged()
    {
        var set = _registry.Resolve(_native, PreviewStateRegistry.XmlDefaultsId);
        Assert.Empty(set.Overrides);
        Assert.Same(_native, _registry.Apply(_native, set));
    }

    [Fact]
    public void Native_hunts_state_lookup_is_exact_and_non_native_projects_are_safe()
    {
        Assert.Equal(new[] { "XML Defaults", "Idle", "Standard Hunt", "Elite Hunt", "Hunt Complete" },
            _registry.StatesFor(_native).Select(state => state.Label));
        Assert.Single(_registry.StatesFor(new Project()));
        var unknown = _registry.Resolve(new Project(), "standard-hunt");
        Assert.True(unknown.State.IsXmlDefaults);
        Assert.Contains(unknown.Diagnostics, item => item.Code == "unknown-preview-state");
        Assert.Equal("idle", _registry.DefaultStateFor(_native).Id);
        Assert.True(_registry.DefaultStateFor(new Project()).IsXmlDefaults);
    }

    [Fact]
    public void Idle_applies_real_visibility_and_text_with_sample_record_values()
    {
        var applied = Apply("idle", out var set);
        Assert.True(applied.Find("NativeHuntsFrame")!.Visible);
        Assert.True(applied.Find("NativeHuntsFrameContentPanelIdle")!.Visible);
        Assert.False(applied.Find("NativeHuntsFrameContentPanelIdentity")!.Visible);
        Assert.False(applied.Find("NativeHuntsFrameContentPanelHuntState")!.Visible);
        Assert.True(applied.Find("NativeHuntsFrameContentPanelRecord")!.Visible);
        Assert.Equal("NO ACTIVE HUNT", Text(applied, "NativeHuntsFrameContentPanelIdleState"));
        Assert.Equal("Speak with a Huntmaster\nto begin a Hunt.",
            Text(applied, "NativeHuntsFrameContentPanelIdleDescription"));
        Assert.Equal(("12", "2", "Available", "3"), Record(applied));
        Assert.True(set.Find("NativeHuntsFrameContentPanelRecordStandard")!.TextIsSample);
    }

    [Fact]
    public void Standard_and_elite_select_the_real_tier_icons_and_tracking_presentation()
    {
        var standard = Apply("standard-hunt", out _);
        var elite = Apply("elite-hunt", out _);
        Assert.Equal(@"Interface\NativeHunts\hunt_icon_standard.tga",
            standard.Find("NativeHuntsFrameContentPanelIdentityIcon")!.Visual!.Texture!.File);
        Assert.Equal(@"Interface\NativeHunts\hunt_icon_elite.tga",
            elite.Find("NativeHuntsFrameContentPanelIdentityIcon")!.Visual!.Texture!.File);
        Assert.Equal("STANDARD HUNT", Text(standard, "NativeHuntsFrameContentPanelIdentityTier"));
        Assert.Equal("ELITE HUNT", Text(elite, "NativeHuntsFrameContentPanelIdentityTier"));
        Assert.Equal("HUNT PROGRESS", Text(standard, "NativeHuntsFrameContentPanelHuntStateHeader"));
        Assert.Equal(60, standard.Find("NativeHuntsFrameContentPanelHuntStateProgress")!.Visual!.StatusBar!.DefaultValue);
        Assert.Equal(65, elite.Find("NativeHuntsFrameContentPanelHuntStateProgress")!.Visual!.StatusBar!.DefaultValue);
        Assert.Equal(-34, standard.Find("NativeHuntsFrameContentPanelHuntStatePrimary")!.OffsetY);
        Assert.Equal(-78, standard.Find("NativeHuntsFrameContentPanelHuntStateSecondary")!.OffsetY);
    }

    [Fact]
    public void Complete_uses_ready_to_turn_in_state_and_hides_progress()
    {
        var applied = Apply("hunt-complete", out _);
        Assert.Equal("HUNT COMPLETE", Text(applied, "NativeHuntsFrameContentPanelHuntStateHeader"));
        Assert.Equal("READY TO TURN IN", Text(applied, "NativeHuntsFrameContentPanelHuntStatePrimary"));
        Assert.True(applied.Find("NativeHuntsFrameContentPanelHuntStateReadyIcon")!.Visible);
        Assert.False(applied.Find("NativeHuntsFrameContentPanelHuntStateProgress")!.Visible);
        Assert.Equal(0, applied.Find("NativeHuntsFrameContentPanelHuntStateProgress")!.Visual!.StatusBar!.DefaultValue);
    }

    [Fact]
    public void Non_default_states_select_the_hunts_tab_without_changing_source_buttons()
    {
        var set = _registry.Resolve(_native, "standard-hunt");
        Assert.Equal(PreviewButtonState.Normal, set.Find("LFDParentFrameTab1")!.ButtonState);
        Assert.Equal(PreviewButtonState.Selected, set.Find("LFDParentFrameTab2")!.ButtonState);
        Assert.Null(_native.Find("LFDParentFrameTab2")!.Visual?.StatusBar);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(60, 0.6)]
    [InlineData(100, 1)]
    public void Status_bar_override_handles_zero_partial_and_full(double value, double fraction)
    {
        var frame = new FrameDef
        {
            Name = "Progress", Kind = FrameKind.STATUSBAR,
            Visual = new FrameVisual { StatusBar = new StatusBarVisual(0, 100, 0) },
        };
        var source = new Project { Frames = [frame] };
        var definition = new PreviewStateDefinition("test", "Test", "test",
            new Dictionary<string, PreviewFrameOverride>
            {
                ["Progress"] = new("Progress", StatusBarValue: value),
            });
        var set = new PreviewOverrideSet(definition, definition.Overrides, []);
        var applied = _registry.Apply(source, set);
        Assert.Equal(fraction, applied.Find("Progress")!.Visual!.StatusBar!.DefaultFraction);
        Assert.Equal(0, source.Find("Progress")!.Visual!.StatusBar!.DefaultValue);
    }

    [Fact]
    public void Switching_is_deterministic_and_never_bakes_into_project_or_json()
    {
        var before = ProjectCodec.Serialize(_native);
        var standard1 = ProjectCodec.Serialize(Apply("standard-hunt", out _));
        _ = Apply("idle", out _);
        var standard2 = ProjectCodec.Serialize(Apply("standard-hunt", out _));
        Assert.Equal(standard1, standard2);
        Assert.Equal(before, ProjectCodec.Serialize(_native));

        var reopened = ProjectCodec.Parse(before);
        Assert.True(reopened.Ok);
        Assert.Null(reopened.Project!.Find("NativeHuntsFrameContentPanelIdentityIcon")!.Visual?.Texture?.File);
        Assert.DoesNotContain("standard-hunt", before, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_and_hybrid_use_the_same_effective_state_and_selection_persists()
    {
        var settings = Path.Combine(Path.GetTempPath(), $"frameforge-preview-{Guid.NewGuid():N}.json");
        var vm = new MainWindowViewModel(settings, stockTemplates: new PassThroughStockTemplates());
        vm.Load(_native, null, "test");
        Assert.Equal("idle", vm.SelectedPreviewState!.Id);
        Assert.False(vm.Project.Find("NativeHuntsFrame")!.Visible);
        Assert.True(vm.PresentationProject.Find("NativeHuntsFrame")!.Visible);
        Assert.True(vm.PresentationProject.Find("NativeHuntsFrameContentPanelRecord")!.Visible);
        vm.SelectedPreviewState = vm.PreviewStateOptions.Single(state => state.Id == "standard-hunt");
        vm.SetViewMode(CanvasViewMode.PREVIEW);
        var preview = ProjectCodec.Serialize(vm.PresentationProject);
        vm.SetViewMode(CanvasViewMode.HYBRID);
        Assert.Equal(preview, ProjectCodec.Serialize(vm.PresentationProject));
        vm.SetViewMode(CanvasViewMode.DEBUG);
        Assert.Equal(_native.Frames.Count, vm.Layout.Frames.Count);
        Assert.NotEmpty(vm.Layout.Rects);
        Assert.True(vm.IsDirty);
        Assert.Equal("standard-hunt", vm.Project.Editor.PreviewStateId);

        var reopened = ProjectCodec.Parse(ProjectCodec.Serialize(vm.Project)).Project!;
        var reopenedVm = new MainWindowViewModel(settings, stockTemplates: new PassThroughStockTemplates());
        reopenedVm.Load(reopened, "preview.fforge.json", "reopened");
        Assert.Equal("standard-hunt", reopenedVm.SelectedPreviewState!.Id);
        Assert.False(reopenedVm.Project.Find("NativeHuntsFrame")!.Visible);
    }

    [Fact]
    public void Missing_targets_are_diagnostics_not_exceptions()
    {
        var partial = new Project
        {
            Frames =
            [
                new FrameDef { Name = "NativeHuntsFrame" },
                new FrameDef { Name = "NativeHuntsFrameContentPanelIdentityIcon", Kind = FrameKind.TEXTURE },
                new FrameDef { Name = "LFDParentFrameTab2", Kind = FrameKind.BUTTON },
            ],
        };
        var set = _registry.Resolve(partial, "idle");
        Assert.NotEmpty(set.Diagnostics);
        Assert.All(set.Diagnostics, item => Assert.Equal("missing-preview-target", item.Code));
        var applied = _registry.Apply(partial, set);
        Assert.Equal(partial.Frames.Count, applied.Frames.Count);
    }

    [Fact]
    public void Inspector_description_distinguishes_behavior_sample_and_source()
    {
        var set = _registry.Resolve(_native, "standard-hunt");
        var frame = _native.Find("NativeHuntsFrameContentPanelIdentityPrey")!;
        var description = set.Describe(frame);
        Assert.Contains(description, line => line.Contains("design-time only", StringComparison.Ordinal));
        Assert.Contains(description, line => line.Contains("sample content", StringComparison.Ordinal));
        Assert.Contains(description, line => line.Contains("runtime/unset", StringComparison.Ordinal));
    }

    private Project Apply(string id, out PreviewOverrideSet set)
    {
        set = _registry.Resolve(_native, id);
        Assert.Empty(set.Diagnostics);
        return _registry.Apply(_native, set);
    }

    private static string? Text(Project project, string name) => project.Find(name)?.Visual?.Text?.Text;
    private static (string?, string?, string?, string?) Record(Project project) =>
        (Text(project, "NativeHuntsFrameContentPanelRecordStandard"),
            Text(project, "NativeHuntsFrameContentPanelRecordElite"),
            Text(project, "NativeHuntsFrameContentPanelRecordAvailability"),
            Text(project, "NativeHuntsFrameContentPanelRecordSeals"));

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
        public double MeasureText(StockFontStyle style, string text) => 0;
    }
}
