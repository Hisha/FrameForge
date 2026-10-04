using FrameForge.Core.Geometry;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Inspection;
using FrameForge.Desktop.Preview;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class CompositionUsabilityTests
{
    private readonly Project _native = FrameXmlImporter.ImportFile(FixturePath()).Project!;

    [Fact]
    public void Origin_classification_distinguishes_project_stock_runtime_and_standin()
    {
        using var assets = new TextureAssetResolver();
        var stock = new FocusedStockTemplates();
        var classifier = new ElementOriginClassifier(assets, stock, "/managed");
        var defaults = new PreviewStateRegistry().Resolve(_native, PreviewStateRegistry.XmlDefaultsId);
        Assert.Equal(ElementOrigin.ProjectSource, classifier.Classify(_native.Find("NativeHuntsFrameContentPanelRecord")!, defaults));
        Assert.Equal(ElementOrigin.BlizzardStock, classifier.Classify(_native.Find("LFDParentFrame")!, defaults));
        Assert.Equal(ElementOrigin.StandIn, classifier.Classify(new FrameDef { Name = "Unknown", Placeholder = true }, defaults));
        var active = new PreviewStateRegistry().Resolve(_native, "standard-hunt");
        Assert.Equal(ElementOrigin.RuntimeDesignTime,
            classifier.Classify(_native.Find("NativeHuntsFrameContentPanelIdentityIcon")!, active));
    }

    [Fact]
    public void PanelRecord_composition_reports_direct_art_text_geometry_and_safe_resize_guidance()
    {
        using var assets = new TextureAssetResolver();
        var stock = new FocusedStockTemplates();
        var classifier = new ElementOriginClassifier(assets, stock, "/managed");
        var inspector = new VisualCompositionInspector(assets, stock, classifier, "/managed");
        var preview = new PreviewStateRegistry().Resolve(_native, PreviewStateRegistry.XmlDefaultsId);
        var result = inspector.Inspect(_native, _native, LayoutResolver.Resolve(_native),
            "NativeHuntsFrameContentPanelRecord", preview);
        Assert.Equal(11, result.Components.Count);
        Assert.Contains(result.Components, item => item.AssetPath.Contains("hunt_panel_record.tga", StringComparison.Ordinal));
        Assert.Contains(result.Components, item => item.AssetPath.Contains("hunt_icon_seal.tga", StringComparison.Ordinal));
        Assert.Contains("NativeHuntsFrame.xml", result.SizeSource);
        Assert.Contains("cropped/atlas", result.ResizeGuidance);
        Assert.DoesNotContain("may stretch fixed artwork", result.ResizeGuidance);

        var tab = inspector.Inspect(_native, _native, LayoutResolver.Resolve(_native), "LFDParentFrameTab2", preview);
        var template = Assert.Single(tab.Components);
        Assert.True(template.IsTemplateDerived);
        Assert.Contains("InactiveTab", template.AssetPath);
        Assert.Contains("selected-state atlas slices", template.Details);
    }

    [Fact]
    public void Groups_create_rename_membership_lock_and_delete_without_deleting_elements()
    {
        var vm = ViewModel();
        vm.Load(_native, null, "test");
        vm.GroupNameDraft = "Blizzard Chrome";
        vm.CreateGroup();
        vm.Select("LFDParentFrame");
        vm.AddSelectionToGroup();
        vm.SelectedGroupLocked = true;
        Assert.True(vm.Project.Editor.IsLocked("LFDParentFrame"));
        Assert.Contains("Blizzard Chrome", vm.SelectedGroupMembership);

        var before = vm.Project.Find("LFDParentFrame")!;
        vm.DragFrame(before.Name, 20, -10);
        Assert.Equal(before, vm.Project.Find(before.Name));
        vm.Editor.OffsetX = "999";
        Assert.Equal(before.OffsetX, vm.Project.Find(before.Name)!.OffsetX);

        vm.GroupNameDraft = "Stock Shell";
        vm.RenameSelectedGroup();
        Assert.Equal("Stock Shell", Assert.Single(vm.Project.Editor.Groups).Name);
        vm.DeleteSelectedGroup();
        Assert.Empty(vm.Project.Editor.Groups);
        Assert.NotNull(vm.Project.Find("LFDParentFrame"));
        Assert.False(vm.Project.Editor.IsLocked("LFDParentFrame"));
    }

    [Fact]
    public void Group_lock_persists_and_preview_state_remains_non_destructive()
    {
        var vm = ViewModel();
        vm.Load(_native, null, "test");
        vm.GroupNameDraft = "Blizzard Chrome";
        vm.CreateGroup();
        vm.Select("LFDParentFrame");
        vm.AddSelectionToGroup();
        vm.SelectedGroupLocked = true;
        vm.SelectedPreviewState = vm.PreviewStateOptions.Single(state => state.Id == "standard-hunt");
        Assert.Equal(60, vm.PresentationProject.Find("NativeHuntsFrameContentPanelHuntStateProgress")!
            .Visual!.StatusBar!.DefaultValue);
        Assert.True(vm.Project.Editor.IsLocked("LFDParentFrame"));
        Assert.Null(vm.Project.Find("NativeHuntsFrameContentPanelIdentityIcon")!.Visual?.Texture?.File);

        var reopened = ProjectCodec.Parse(ProjectCodec.Serialize(vm.Project));
        Assert.True(reopened.Ok, reopened.ErrorText);
        Assert.True(reopened.Project!.Editor.IsLocked("LFDParentFrame"));
    }

    [Fact]
    public void Origin_filters_are_presentation_only_and_do_not_hide_custom_descendants_by_ancestry()
    {
        var vm = ViewModel();
        vm.Load(_native, null, "test");
        var sourceVisibility = vm.Project.Frames.Select(frame => (frame.Name, frame.Visible)).ToArray();
        vm.SetOriginVisible(OriginVisibility.BlizzardStock, false);
        Assert.Contains("LFDParentFrame", vm.HiddenByOrigin);
        Assert.DoesNotContain("NativeHuntsFrameContentPanelRecord", vm.HiddenByOrigin);
        Assert.Equal(sourceVisibility, vm.Project.Frames.Select(frame => (frame.Name, frame.Visible)).ToArray());
    }

    [Fact]
    public void Selection_priority_prefers_editable_content_but_keeps_every_candidate()
    {
        var ordered = SelectionPriority.Order(["LockedStock", "Project", "OtherStock"],
            new HashSet<string>(["Project"], StringComparer.Ordinal));
        Assert.Equal(["Project", "LockedStock", "OtherStock"], ordered);
    }

    [Fact]
    public void Design_and_inspect_share_the_model_but_change_tree_identity_and_diagnostics_presentation()
    {
        var vm = ViewModel();
        vm.Load(_native, null, "test");
        Assert.True(vm.IsDesignWorkspace);
        var texture = _native.Frames.First(frame => frame.Anonymous && frame.Kind == FrameKind.TEXTURE
            && frame.Visual?.Texture?.File?.Contains("hunt_panel_record", StringComparison.OrdinalIgnoreCase) == true);
        vm.Select(texture.Name);
        Assert.Equal("Hunt Panel Record", vm.DesignNameDraft);
        Assert.NotEqual(vm.DesignNameDraft, texture.Name);
        vm.SetWorkspace(WorkspaceExperience.Inspect);
        Assert.True(vm.IsInspectWorkspace);
        Assert.Equal(texture.Name, vm.SelectedName);
        Assert.Contains("NativeHuntsFrame.xml", vm.SelectedSourceFile);
        Assert.True(vm.HasVisualComposition);
    }

    [Fact]
    public void Conceptual_stock_expands_without_unlock_and_unlocks_and_relocks_explicitly()
    {
        var root = new FrameDef { Name = "LFDParentFrame", Width = 355, Height = 440 };
        var piece = new FrameDef { Name = "Texture#23", Parent = root.Name, Kind = FrameKind.TEXTURE };
        var project = new Project
        {
            Frames = [root, piece],
            Editor = new EditorMetadata
            {
                Groups =
                [
                    new EditorGroup
                    {
                        Name = "Blizzard Dungeon Finder Frame", Members = [root.Name, piece.Name], Locked = true,
                        Concept = "stock-framework", StockIdentity = "wow-3.3.5a-12340:LFDParentFrame",
                    },
                ],
            },
        };
        var vm = ViewModel();
        vm.Load(project, null, "test");
        Assert.Single(vm.TreeRoots);
        Assert.Equal("🔒 Blizzard Dungeon Finder Frame", vm.TreeRoots[0].DisplayName);
        Assert.True(vm.Project.Editor.IsLocked(piece.Name));
        vm.SetConceptualStockExpanded(true);
        Assert.True(vm.ConceptualStockFramework!.Expanded);
        Assert.True(vm.ConceptualStockFramework.Locked);
        vm.SetConceptualStockLocked(false);
        Assert.False(vm.Project.Editor.IsLocked(piece.Name));
        vm.SetConceptualStockLocked(true);
        Assert.True(vm.Project.Editor.IsLocked(piece.Name));
    }

    [Fact]
    public void Custom_design_objects_edit_over_locked_stock_and_names_persist()
    {
        var vm = ViewModel();
        vm.Load(new Project
        {
            Frames = [new FrameDef { Name = "LFDParentFrame", Width = 355, Height = 440 }],
            Editor = new EditorMetadata
            {
                Groups = [new EditorGroup { Name = "Blizzard Dungeon Finder Frame", Members = ["LFDParentFrame"], Locked = true, Concept = "stock-framework" }],
            },
        }, null, "test");
        vm.NewObjectName = "Hunt Record";
        vm.AddDesignFrame();
        var custom = vm.SelectedName!;
        Assert.Equal("Hunt Record", vm.Project.Editor.DisplayNameFor(vm.Project.Find(custom)!));
        var before = vm.Project.Find(custom)!.OffsetX;
        vm.DragFrame(custom, 15, 0);
        Assert.Equal(before + 15, vm.Project.Find(custom)!.OffsetX);
        Assert.True(vm.Project.Editor.IsLocked("LFDParentFrame"));
        var reopened = ProjectCodec.Parse(ProjectCodec.Serialize(vm.Project));
        Assert.True(reopened.Ok, reopened.ErrorText);
        Assert.Equal("Hunt Record", reopened.Project!.Editor.DisplayNameFor(reopened.Project.Find(custom)!));
    }

    [Fact]
    public void Authored_states_create_rename_assign_switch_delete_and_keep_preview_states()
    {
        var vm = ViewModel();
        vm.Load(_native, null, "test");
        Assert.Equal(5, vm.PreviewStateOptions.Count);
        vm.NewObjectName = "Temporary Standard Object";
        vm.AddDesignFrame();
        var frame = vm.SelectedName!;
        vm.StateNameDraft = "Standard Hunt";
        vm.CreateDesignState();
        vm.AssignSelectionToSelectedState();
        Assert.Equal("Standard Hunt", vm.SelectedStateMembership);
        vm.StateNameDraft = "Idle";
        vm.CreateDesignState();
        vm.ActiveDesignState = DesignStateChoice.All;
        Assert.True(vm.PresentationProject.Find(frame)!.Visible);
        vm.ActiveDesignState = vm.DesignStateOptions.Single(item => item.Id == "standard-hunt");
        Assert.True(vm.PresentationProject.Find(frame)!.Visible);
        vm.ActiveDesignState = vm.DesignStateOptions.Single(item => item.Id == "idle");
        Assert.False(vm.PresentationProject.Find(frame)!.Visible);
        vm.SelectedAuthoredState = vm.AuthoredStateOptions.Single(item => item.Id == "standard-hunt");
        vm.StateNameDraft = "Standard Encounter";
        vm.RenameSelectedDesignState();
        Assert.Contains(vm.Project.Editor.DesignStates, state => state.Name == "Standard Encounter");
        vm.DeleteSelectedDesignState();
        Assert.Single(vm.Project.Editor.DesignStates);
        Assert.Equal("All States", vm.SelectedStateMembership);
        Assert.Equal(5, vm.PreviewStateOptions.Count);
    }

    [Fact]
    public void Lfd_new_project_explains_the_validated_client_requirement()
    {
        var vm = ViewModel();
        Assert.False(vm.NewDungeonFinderProject());
        Assert.Contains("WoW 3.3.5a build 12340", vm.Status);
        Assert.DoesNotContain("substitute", ProjectCodec.Serialize(vm.Project), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Design_image_creation_rejects_absolute_or_traversal_paths()
    {
        var vm = ViewModel();
        vm.NewObjectName = "Unsafe";
        vm.NewImageAsset = "../outside.tga";
        var count = vm.Project.Frames.Count;
        vm.AddDesignImage();
        Assert.Equal(count, vm.Project.Frames.Count);
        Assert.Contains("not added", vm.Status);
    }

    private static MainWindowViewModel ViewModel() => new(
        Path.Combine(Path.GetTempPath(), $"frameforge-usability-{Guid.NewGuid():N}.json"),
        stockTemplates: new FocusedStockTemplates());

    private static string FixturePath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "FrameForge.Core.Tests", "Fixtures", "NativeHuntsFrame.xml"));

    private sealed class FocusedStockTemplates : IStockTemplateResolver
    {
        public int Generation => 1;
        public IReadOnlyList<StockDefinitionDiagnostic> Diagnostics => [];
        public IReadOnlyList<AssetMaterializationResult> MaterializeRequired(WowClientValidation client) => [];
        public void Reload() { }
        public Project ApplyEffectiveGeometry(Project declaredProject) => declaredProject;
        public StockFontStyle? ResolveFont(string? name) => null;
        public StockButtonStyle? ResolveButton(string? name) => name == StockTemplateResolver.TabTemplate
            ? new(name, 32, 32, "Normal", "Selected", 0, 0,
                [new("Left", @"Interface\Tabs\InactiveTab", 16, 32, 0, 0, TexCoords.Full, "templates.xml")],
                [new("LeftDisabled", @"Interface\Tabs\ActiveTab", 16, 32, 0, 0, TexCoords.Full, "templates.xml")],
                null, [], StockDefinitionStatus.FullyResolved)
            : null;
        public StockExternalFrameStyle? ResolveExternalFrame(string? name) => name == StockTemplateResolver.LfdParentFrame
            ? new(name, 355, 500, "LFDFrame.xml", StockDefinitionStatus.PartiallyResolved, "test")
            : null;
        public IReadOnlyList<string> Describe(FrameDef frame) => [];
        public IReadOnlyList<string> ExpandedChildNames(string instanceName, StockButtonStyle style) => [];
        public double MeasureText(StockFontStyle style, string text) => 0;
    }
}
