using FrameForge.Core.Geometry;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Inspection;
using FrameForge.Desktop.Preview;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using SkiaSharp;
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
        vm.Editor.CommitBufferedField("offsetX");
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
        vm.Select(root.Name);
        Assert.True(vm.SelectedElementLocked);
        Assert.Equal("Unlock for Editing", vm.StockFrameworkAction);
        vm.SetConceptualStockExpanded(true);
        Assert.True(vm.ConceptualStockFramework!.Expanded);
        Assert.True(vm.ConceptualStockFramework.Locked);
        vm.SelectedElementLocked = false;
        Assert.False(vm.Project.Editor.IsLocked(piece.Name));
        Assert.False(vm.SelectedElementLocked);
        Assert.Equal("🔓 Blizzard Dungeon Finder Frame", vm.TreeRoots[0].DisplayName);
        Assert.Equal("Lock Blizzard Dungeon Finder Frame", vm.StockFrameworkAction);
        vm.SelectedElementLocked = true;
        Assert.True(vm.Project.Editor.IsLocked(piece.Name));
        Assert.True(vm.SelectedElementLocked);
        Assert.Equal("Unlock for Editing", vm.StockFrameworkAction);
        Assert.True(vm.ConceptualStockFramework.Expanded);
    }

    [Fact]
    public void Text_name_and_visible_override_are_distinct_continuously_editable_and_preserve_source()
    {
        const string frameName = "LFDHeaderText";
        var source = new FrameDef
        {
            Name = frameName,
            Kind = FrameKind.FONTSTRING,
            Width = 240,
            Height = 24,
            Visual = new FrameVisual { Text = new TextVisual("LOOKING FOR DUNGEON", "CENTER", "MIDDLE", "GameFontNormalLarge") },
        };
        var vm = ViewModel();
        vm.Load(new Project { Frames = [source] }, null, "test");
        vm.Select(frameName);

        vm.DesignNameDraft = "Title Text";
        foreach (var typed in new[] { "N", "NA", "NAT", "NATI", "NATIV", "NATIVE", "NATIVE ", "NATIVE HUNTS" })
            vm.ChangeSelectedDesignText(typed);

        Assert.Equal("Title Text", vm.Project.Editor.DisplayNameFor(vm.Project.Find(frameName)!));
        Assert.Equal("LOOKING FOR DUNGEON", vm.Project.Find(frameName)!.Visual!.Text!.Text);
        Assert.Equal("NATIVE HUNTS", vm.Project.Editor.DesignObjectFor(frameName)!.TextOverride);
        Assert.Equal("NATIVE HUNTS", vm.PresentationProject.Find(frameName)!.Visual!.Text!.Text);
        Assert.Equal("LOOKING FOR DUNGEON", vm.SelectedSourceText);
        Assert.Equal("NATIVE HUNTS", vm.SelectedDesignTextOverride);

        var json = ProjectCodec.Serialize(vm.Project);
        Assert.Contains("\"textOverride\": \"NATIVE HUNTS\"", json);
        var parsed = ProjectCodec.Parse(json);
        Assert.True(parsed.Ok, parsed.ErrorText);
        var reopened = ViewModel();
        reopened.Load(parsed.Project!, null, "reopened");
        reopened.Select(frameName);
        Assert.Equal("NATIVE HUNTS", reopened.DesignTextDraft);
        Assert.Equal("NATIVE HUNTS", reopened.PresentationProject.Find(frameName)!.Visual!.Text!.Text);
        Assert.Equal("LOOKING FOR DUNGEON", reopened.Project.Find(frameName)!.Visual!.Text!.Text);
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

    [Theory]
    [InlineData(FrameKind.FRAME)]
    [InlineData(FrameKind.FONTSTRING)]
    [InlineData(FrameKind.TEXTURE)]
    public void Deleting_the_final_custom_object_clears_selection_and_inspector_and_allows_add_again(FrameKind kind)
    {
        var vm = ViewModel();
        vm.NewProject();
        AddDesignObject(vm, kind);
        var deleted = vm.SelectedName!;
        Assert.True(vm.Editor.HasSelection);

        vm.DeleteFrame();

        Assert.Null(vm.Project.Find(deleted));
        Assert.Null(vm.SelectedName);
        Assert.Null(vm.SelectedTreeNode);
        Assert.False(vm.Editor.HasSelection);
        Assert.Null(vm.Editor.Frame);
        Assert.Null(vm.Editor.Resolved);
        Assert.Equal("No frame selected.", vm.Editor.ResolvedSummary);
        Assert.Empty(vm.Editor.ExtraAnchors);
        Assert.Empty(vm.Editor.VisualLines);
        Assert.Empty(vm.VisualComposition);
        Assert.Empty(vm.CompositionSizeSource);
        Assert.Empty(vm.CompositionAppearanceSource);
        Assert.Empty(vm.ResizeGuidance);
        Assert.DoesNotContain(deleted, vm.CanvasSelectionNames);

        AddDesignObject(vm, kind);
        Assert.NotNull(vm.SelectedFrame);
        Assert.True(vm.Editor.HasSelection);

        var path = Path.Combine(Path.GetTempPath(), $"frameforge-delete-{Guid.NewGuid():N}.frameforge.json");
        try
        {
            Assert.True(vm.SaveToFile(path));
            var reopened = ProjectCodec.Parse(File.ReadAllText(path));
            Assert.True(reopened.Ok, reopened.ErrorText);
            Assert.Single(reopened.Project!.Frames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(WorkspaceExperience.Design)]
    [InlineData(WorkspaceExperience.Inspect)]
    public void Deletion_is_safe_in_both_workspaces(WorkspaceExperience workspace)
    {
        var vm = ViewModel();
        vm.NewProject();
        vm.AddDesignFrame();
        vm.SetWorkspace(workspace);

        vm.DeleteFrame();

        Assert.Null(vm.SelectedName);
        Assert.False(vm.Editor.HasSelection);
        Assert.Empty(vm.VisualComposition);
    }

    [Fact]
    public void Delete_cleans_state_and_group_metadata_without_touching_locked_stock()
    {
        const string stock = "LFDParentFrame";
        const string custom = "DesignObject";
        var vm = ViewModel();
        vm.Load(new Project
        {
            Frames =
            [
                new FrameDef { Name = stock, Width = 355, Height = 440 },
                new FrameDef { Name = custom, Width = 100, Height = 40 },
            ],
            Editor = new EditorMetadata
            {
                Groups =
                [
                    new EditorGroup { Name = "Blizzard", Members = [stock], Locked = true, Concept = "stock-framework" },
                    new EditorGroup { Name = "Custom", Members = [custom] },
                ],
                DesignStates = [new DesignState { Id = "active", Name = "Active" }],
                DesignObjects = [new DesignObjectMetadata { FrameName = custom, StateIds = ["active"] }],
            },
        }, null, "test");
        vm.Select(custom);

        vm.DeleteFrame();

        Assert.NotNull(vm.Project.Find(stock));
        Assert.True(vm.Project.Editor.IsLocked(stock));
        Assert.DoesNotContain(vm.Project.Editor.DesignObjects, item => item.FrameName == custom);
        Assert.DoesNotContain(vm.Project.Editor.Groups.SelectMany(group => group.Members), item => item == custom);
        Assert.Single(vm.Project.Editor.DesignStates);

        vm.NewObjectName = "Hunt Record";
        vm.AddDesignFrame();
        Assert.Equal("Hunt Record", vm.Project.Editor.DisplayNameFor(vm.SelectedFrame!));
        Assert.NotNull(vm.Project.Find(stock));
        Assert.True(vm.Project.Editor.IsLocked(stock));
    }

    [Fact]
    public void Locked_element_and_locked_stock_member_cannot_be_deleted_until_unlocked()
    {
        const string stock = "LFDParentFrame";
        const string custom = "Custom";
        var vm = ViewModel();
        vm.Load(new Project
        {
            Frames =
            [
                new FrameDef { Name = stock, Width = 355, Height = 440 },
                new FrameDef { Name = custom, Width = 100, Height = 40 },
            ],
            Editor = new EditorMetadata
            {
                Groups = [new EditorGroup { Name = "Blizzard", Members = [stock], Locked = true, Concept = "stock-framework" }],
                LockedElements = [custom],
            },
        }, null, "test");

        vm.Select(custom);
        vm.DeleteFrame();
        Assert.NotNull(vm.Project.Find(custom));
        vm.SetElementLocked(custom, false);
        vm.DeleteFrame();
        Assert.Null(vm.Project.Find(custom));

        vm.Select(stock);
        vm.DeleteFrame();
        Assert.NotNull(vm.Project.Find(stock));
        vm.SetConceptualStockLocked(false);
        vm.DeleteFrame();
        Assert.Null(vm.Project.Find(stock));
    }

    [Fact]
    public void Design_image_browse_requires_saved_project_and_rejects_external_or_traversal_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"frameforge-design-assets-{Guid.NewGuid():N}");
        var projectDirectory = Path.Combine(root, "project");
        var external = Path.Combine(root, "outside.png");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllBytes(external, Png(1, 1));
        try
        {
            var vm = ViewModel();
            vm.NewProject();
            Assert.False(vm.SetNewDesignImageFromFile(external));
            Assert.Contains("Save", vm.Status);

            Assert.True(vm.SaveToFile(Path.Combine(projectDirectory, "Design.fforge.json")));
            Assert.False(vm.SetNewDesignImageFromFile(external));
            Assert.Contains("outside the FrameForge project", vm.Status);

            vm.NewImageAsset = "../outside.png";
            vm.AddDesignImage();
            Assert.Empty(vm.Project.Frames);
            Assert.Contains("traversal", vm.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Project_png_round_trips_after_relocation_and_custom_image_stays_editable_over_locked_stock()
    {
        const string stock = "LFDParentFrame";
        var root = Path.Combine(Path.GetTempPath(), $"frameforge-portable-assets-{Guid.NewGuid():N}");
        var original = Path.Combine(root, "original");
        var relocated = Path.Combine(root, "relocated");
        var assets = Path.Combine(original, "assets");
        Directory.CreateDirectory(assets);
        var imagePath = Path.Combine(assets, "hunt_divider.png");
        File.WriteAllBytes(imagePath, Png(7, 3));
        try
        {
            var vm = ViewModel();
            vm.Load(new Project
            {
                Frames = [new FrameDef { Name = stock, Width = 355, Height = 440 }],
                Editor = new EditorMetadata
                {
                    Groups = [new EditorGroup
                    {
                        Name = "Blizzard Dungeon Finder Frame", Members = [stock], Locked = true,
                        Concept = "stock-framework",
                    }],
                },
            }, null, "test");
            var projectPath = Path.Combine(original, "NativeHuntsRedesign.fforge.json");
            Assert.True(vm.SaveToFile(projectPath));
            Assert.True(vm.SetNewDesignImageFromFile(imagePath));
            Assert.Equal("assets/hunt_divider.png", vm.NewImageAsset);
            vm.NewObjectName = "Divider";
            vm.AddDesignImage();
            var divider = vm.SelectedName!;
            Assert.True(vm.ChangeSelectedDesignImageFromFile(imagePath));

            Assert.Equal("assets/hunt_divider.png", vm.Project.Editor.DesignObjectFor(divider)!.DesignAsset);
            Assert.Equal(AssetSourceKind.ProjectRelative, vm.Assets.Resolve("assets/hunt_divider.png").SourceKind);
            Assert.Equal((7, 3), (vm.Assets.Resolve("assets/hunt_divider.png").Width, vm.Assets.Resolve("assets/hunt_divider.png").Height));
            Assert.Equal("PNG", vm.SelectedDesignAssetFormat);
            Assert.Equal("Project-owned", vm.SelectedDesignAssetOwnership);
            Assert.Contains("future export", vm.SelectedWowExportReference);
            var stockBefore = vm.Project.Find(stock)!;
            vm.DragFrame(divider, 12, -8);
            vm.Editor.Width = "96";
            vm.Editor.CommitBufferedField("width");
            Assert.Equal(12, vm.Project.Find(divider)!.OffsetX);
            Assert.Equal(96, vm.Project.Find(divider)!.Width);
            Assert.Equal(stockBefore, vm.Project.Find(stock));
            Assert.True(vm.Project.Editor.IsLocked(stock));
            Assert.True(vm.SaveToFile(projectPath));
            Assert.Contains("\"designAsset\": \"assets/hunt_divider.png\"", File.ReadAllText(projectPath));

            Directory.Move(original, relocated);
            var relocatedProject = Path.Combine(relocated, "NativeHuntsRedesign.fforge.json");
            var reopened = ViewModel();
            reopened.OpenFromFile(relocatedProject);
            reopened.Select(divider);
            var resolved = reopened.Assets.Resolve("assets/hunt_divider.png");
            Assert.True(resolved.CanRender, resolved.Diagnostic.Message);
            Assert.StartsWith(relocated, resolved.PhysicalPath, StringComparison.Ordinal);
            Assert.Equal(7, resolved.Width);
            Assert.Equal("assets/hunt_divider.png", reopened.SelectedDesignAssetReference);
            Assert.Equal(96, reopened.Project.Find(divider)!.Width);
            Assert.True(reopened.Project.Editor.IsLocked(stock));

            reopened.DeleteFrame();
            Assert.Null(reopened.Project.Find(divider));
            Assert.NotNull(reopened.Project.Find(stock));
            Assert.True(reopened.SetNewDesignImageFromFile(Path.Combine(relocated, "assets", "hunt_divider.png")));
            reopened.NewObjectName = "Divider";
            reopened.AddDesignImage();
            Assert.True(reopened.Assets.Resolve("assets/hunt_divider.png").CanRender);
            Assert.True(reopened.Project.Editor.IsLocked(stock));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void External_browse_imports_a_copy_collision_safely_and_never_changes_source()
    {
        var root = Path.Combine(Path.GetTempPath(), $"frameforge-import-assets-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(root, "source");
        var projectDirectory = Path.Combine(root, "project");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(Path.Combine(projectDirectory, "assets"));
        var external = Path.Combine(sourceDirectory, "hunt_divider.png");
        var originalBytes = Png(7, 3);
        File.WriteAllBytes(external, originalBytes);
        File.WriteAllBytes(Path.Combine(projectDirectory, "assets", "hunt_divider.png"), Png(1, 1));
        try
        {
            var vm = ViewModel();
            vm.NewProject();
            Assert.True(vm.SaveToFile(Path.Combine(projectDirectory, "Native-Hunts.fforge.json")));
            Assert.True(vm.TryAssessDesignAssetSelection(external, out var requiresImport));
            Assert.True(requiresImport);
            Assert.False(vm.SetNewDesignImageFromFile(external));
            Assert.Contains("outside", vm.Status);
            Assert.True(vm.SetNewDesignImageFromFile(external, importExternal: true));
            Assert.Equal("assets/hunt_divider-2.png", vm.NewImageAsset);
            Assert.Equal(originalBytes, File.ReadAllBytes(external));
            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(projectDirectory, vm.NewImageAsset.Replace('/', Path.DirectorySeparatorChar))));

            vm.NewObjectName = "Divider";
            vm.AddDesignImage();
            var divider = vm.SelectedName!;
            var rendered = vm.Assets.Resolve(vm.NewImageAsset.Length == 0
                ? vm.Project.Editor.DesignObjectFor(divider)!.DesignAsset
                : vm.NewImageAsset);
            Assert.True(rendered.CanRender, rendered.Diagnostic.Message);
            Assert.Equal((7, 3), (rendered.Width, rendered.Height));
            Assert.Contains(rendered.Texture!.Image.Bgra.Chunk(4), pixel => pixel[2] > 100 && pixel[3] > 0);
            Assert.True(vm.SaveToFile(vm.ProjectPath));

            var reopened = ViewModel();
            reopened.OpenFromFile(vm.ProjectPath);
            reopened.Select(divider);
            Assert.True(reopened.Assets.Resolve("assets/hunt_divider-2.png").CanRender);
            Assert.Equal("assets/hunt_divider-2.png", reopened.SelectedDesignAssetReference);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Browse_of_an_already_project_owned_image_uses_portable_reference_without_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"frameforge-owned-asset-{Guid.NewGuid():N}");
        var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(assets);
        var image = Path.Combine(assets, "hunt_divider.png");
        File.WriteAllBytes(image, Png(4, 2));
        try
        {
            var vm = ViewModel();
            vm.NewProject();
            Assert.True(vm.SaveToFile(Path.Combine(root, "Native-Hunts.fforge.json")));
            Assert.True(vm.TryAssessDesignAssetSelection(image, out var requiresImport));
            Assert.False(requiresImport);
            Assert.True(vm.SetNewDesignImageFromFile(image));
            Assert.Equal("assets/hunt_divider.png", vm.NewImageAsset);
            Assert.Single(Directory.GetFiles(assets));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_design_asset_reports_a_useful_diagnostic_without_crashing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"frameforge-missing-asset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var vm = ViewModel();
            vm.NewProject();
            Assert.True(vm.SaveToFile(Path.Combine(root, "Missing.fforge.json")));
            vm.NewImageAsset = "assets/missing.png";
            vm.AddDesignImage();

            var result = vm.Assets.Resolve("assets/missing.png");
            Assert.Equal(AssetResolutionStatus.Missing, result.Status);
            Assert.Contains("was not found", result.Diagnostic.Message);
            Assert.Contains("was not found", vm.SelectedDesignAssetDiagnostic);
            Assert.True(vm.Editor.HasSelection);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Geometry_text_buffers_accept_multi_character_intermediate_states_and_commit_once()
    {
        var vm = ViewModel();
        vm.NewProject();
        vm.NewObjectName = "Divider";
        vm.AddDesignImage();
        var divider = vm.SelectedName!;
        var initial = vm.Project.Find(divider)!;

        foreach (var text in new[] { "", "3", "30", "300" })
        {
            vm.Editor.Width = text;
            Assert.Equal(initial.Width, vm.Project.Find(divider)!.Width);
            Assert.Equal(divider, vm.SelectedName);
        }
        Assert.True(vm.Editor.CommitBufferedField("width")); // Enter and LostFocus use this same boundary.
        Assert.Equal(300, vm.Project.Find(divider)!.Width);
        Assert.Equal(300, vm.Layout.Rects[divider].Width);

        foreach (var text in new[] { "", "2", "24" })
            vm.Editor.Height = text;
        Assert.True(vm.Editor.CommitBufferedField("height"));

        foreach (var text in new[] { "", "-", ".", "-.", "-1", "-12", "-12.5" })
        {
            vm.Editor.OffsetX = text;
            Assert.Equal(0, vm.Project.Find(divider)!.OffsetX);
        }
        Assert.True(vm.Editor.CommitBufferedField("offsetX"));

        foreach (var text in new[] { "", "4", "42" })
            vm.Editor.OffsetY = text;
        Assert.True(vm.Editor.CommitBufferedField("offsetY"));
        Assert.Equal((300d, 24d, -12.5, 42d),
            (vm.Project.Find(divider)!.Width, vm.Project.Find(divider)!.Height,
             vm.Project.Find(divider)!.OffsetX, vm.Project.Find(divider)!.OffsetY));
        Assert.Equal(divider, vm.SelectedName);
        Assert.True(vm.Editor.HasSelection);

        vm.Editor.Width = "not-a-number";
        Assert.False(vm.Editor.CommitBufferedField("width"));
        Assert.Equal(300, vm.Project.Find(divider)!.Width);
        Assert.Contains("must be a number", vm.Editor.ValidationMessage);
        Assert.Equal("not-a-number", vm.Editor.Width);
        vm.Editor.CancelBufferedField("width");
        Assert.Equal("300", vm.Editor.Width);
        Assert.Empty(vm.Editor.ValidationMessage);

        var json = ProjectCodec.Serialize(vm.Project);
        var reopened = ProjectCodec.Parse(json);
        Assert.True(reopened.Ok, reopened.ErrorText);
        Assert.Equal((300d, 24d, -12.5, 42d),
            (reopened.Project!.Find(divider)!.Width, reopened.Project.Find(divider)!.Height,
             reopened.Project.Find(divider)!.OffsetX, reopened.Project.Find(divider)!.OffsetY));
    }

    [Fact]
    public void Design_draw_order_is_deterministic_protected_and_persistent()
    {
        var frames = new[]
        {
            new FrameDef { Name = "Stock", Width = 100, Height = 100 },
            new FrameDef { Name = "Panel", Width = 100, Height = 100 },
            new FrameDef { Name = "Divider", Width = 100, Height = 100 },
            new FrameDef { Name = "Paw", Width = 100, Height = 100 },
        };
        var vm = ViewModel();
        vm.Load(new Project
        {
            Frames = frames,
            Editor = new EditorMetadata
            {
                Groups = [new EditorGroup { Name = "Blizzard", Members = ["Stock"], Locked = true, Concept = "stock-framework" }],
                DesignObjects =
                [
                    new DesignObjectMetadata { FrameName = "Panel", DisplayName = "Hunt Panel" },
                    new DesignObjectMetadata { FrameName = "Divider" },
                    new DesignObjectMetadata { FrameName = "Paw", DisplayName = "Paw Emblem" },
                ],
            },
        }, null, "test");

        Assert.Equal(["Stock", "Panel", "Divider", "Paw"], vm.Layout.PaintOrder);
        Assert.True(vm.Project.Editor.IsLocked("Stock"));
        var stockBefore = vm.Project.Find("Stock");
        var parentsBefore = vm.Project.Frames.Select(frame => frame.Parent).ToArray();

        vm.Select("Divider");
        vm.BringSelectedForward();
        Assert.Equal(["Panel", "Paw", "Divider"], vm.Project.Editor.DesignOrder);
        vm.SendSelectedBackward();
        Assert.Equal(["Panel", "Divider", "Paw"], vm.Project.Editor.DesignOrder);
        vm.BringSelectedToFront();
        Assert.Equal(["Panel", "Paw", "Divider"], vm.Project.Editor.DesignOrder);
        vm.SendSelectedToBack();
        Assert.Equal(["Divider", "Panel", "Paw"], vm.Project.Editor.DesignOrder);
        Assert.Equal(["Stock", "Divider", "Panel", "Paw"], vm.Layout.PaintOrder);
        Assert.Equal(stockBefore, vm.Project.Find("Stock"));
        Assert.Equal(parentsBefore, vm.Project.Frames.Select(frame => frame.Parent));
        Assert.True(vm.Project.Editor.IsLocked("Stock"));

        var reopened = ProjectCodec.Parse(ProjectCodec.Serialize(vm.Project));
        Assert.True(reopened.Ok, reopened.ErrorText);
        Assert.Equal(["Divider", "Panel", "Paw"], reopened.Project!.Editor.DesignOrder);
        Assert.Equal(["Stock", "Divider", "Panel", "Paw"], LayoutResolver.Resolve(reopened.Project).PaintOrder);

        vm.NewObjectName = "Title Text";
        vm.AddDesignText();
        Assert.Equal(vm.SelectedName, vm.Project.Editor.DesignOrder[^1]);
        Assert.Equal(vm.SelectedName, vm.Layout.PaintOrder[^1]);
        Assert.Equal("Stock", vm.Layout.PaintOrder[0]);
    }

    [Fact]
    public void Design_has_one_editable_name_and_legacy_identity_is_inspect_only()
    {
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "FrameForge.Desktop", "Views", "MainWindow.axaml"));
        var xaml = File.ReadAllText(source);
        Assert.Equal(1, xaml.Split("Text=\"{Binding DesignNameDraft, Mode=TwoWay}\"").Length - 1);
        Assert.Contains("Text=\"{Binding Editor.Name, Mode=TwoWay}\"", xaml);
        Assert.Contains("<Grid ColumnDefinitions=\"96,*\" IsVisible=\"{Binding IsInspectWorkspace}\">", xaml);
    }

    [Fact]
    public void WoW_text_presets_overrides_copy_paste_and_persistence_preserve_non_style_authoring()
    {
        var vm = ViewModel();
        vm.Load(new Project
        {
            Frames =
            [
                new FrameDef
                {
                    Name = "StockHeader", Kind = FrameKind.FONTSTRING, Width = 240, Height = 24,
                    Visual = new FrameVisual { Text = new TextVisual("Player vs. Environment", FontTemplate: "GameFontNormal") },
                },
                new FrameDef
                {
                    Name = "NativeHunt", Kind = FrameKind.FONTSTRING, Width = 180, Height = 24, OffsetX = 7, OffsetY = 9,
                    Visual = new FrameVisual { Text = new TextVisual("Native Hunt", "LEFT", "MIDDLE", "GameFontHighlight") },
                },
            ],
            Editor = new EditorMetadata
            {
                Groups = [new EditorGroup { Name = "Blizzard", Members = ["StockHeader"], Locked = true, Concept = "stock-framework" }],
                DesignStates = [new DesignState { Id = "active", Name = "Active" }],
                DesignObjects = [new DesignObjectMetadata { FrameName = "NativeHunt", DisplayName = "Header Text", StateIds = ["active"] }],
            },
        }, null, "test");

        vm.Select("StockHeader");
        Assert.Equal("GameFontNormal", vm.SelectedTextBaseStyle);
        Assert.Equal("12 px", vm.SelectedTextEffectiveSize);
        Assert.Equal("#FFFFD100", vm.SelectedTextEffectiveColor);
        Assert.Contains("Friz Quadrata", vm.SelectedTextFont);
        vm.CopySelectedTextStyle();

        vm.Select("NativeHunt");
        var before = vm.Project.Find("NativeHunt")!;
        var beforeMetadata = vm.Project.Editor.DesignObjectFor("NativeHunt")!;
        vm.PasteSelectedTextStyle();
        var pasted = vm.Project.Editor.DesignObjectFor("NativeHunt")!;
        Assert.Equal("GameFontNormal", pasted.TextStyle!.BaseStyle);
        Assert.Equal(before, vm.Project.Find("NativeHunt"));
        Assert.Equal(beforeMetadata.DisplayName, pasted.DisplayName);
        Assert.Equal(beforeMetadata.StateIds, pasted.StateIds);
        Assert.Equal("Native Hunt", vm.Project.Find("NativeHunt")!.Visual!.Text!.Text);

        vm.SelectedTextStyle = vm.TextStyleOptions.Single(item => item.Name == "GameFontNormalLarge");
        Assert.Equal("16 px", vm.SelectedTextEffectiveSize);
        var committedBefore = vm.Project.Editor.DesignObjectFor("NativeHunt")!.TextStyle!.Size;
        foreach (var text in new[] { "", "1", "16" })
        {
            vm.TextStyleSizeDraft = text;
            Assert.Equal(committedBefore, vm.Project.Editor.DesignObjectFor("NativeHunt")!.TextStyle!.Size);
        }
        Assert.True(vm.CommitTextStyleSize());
        vm.TextStyleColorDraft = "#804080FF";
        Assert.True(vm.CommitTextStyleColor());
        vm.SelectedTextOutline = "Thick";
        vm.SelectedTextShadow = "Off";
        vm.SelectedTextAlignment = "Right";
        Assert.Equal("16 px", vm.SelectedTextEffectiveSize);
        Assert.Equal("#804080FF", vm.SelectedTextEffectiveColor);
        Assert.Equal("THICK", vm.SelectedTextEffectiveOutline);
        Assert.Equal("None", vm.SelectedTextEffectiveShadow);
        Assert.Equal("RIGHT", vm.SelectedTextEffectiveAlignment);
        Assert.Contains("color", vm.SelectedTextOverrides);

        var parsed = ProjectCodec.Parse(ProjectCodec.Serialize(vm.Project));
        Assert.True(parsed.Ok, parsed.ErrorText);
        Assert.Equal(vm.Project.Editor.DesignObjectFor("NativeHunt")!.TextStyle,
            parsed.Project!.Editor.DesignObjectFor("NativeHunt")!.TextStyle);

        vm.ResetTextStyleOverrides();
        Assert.False(vm.Project.Editor.DesignObjectFor("NativeHunt")!.TextStyle!.HasOverrides);
        Assert.Equal("16 px", vm.SelectedTextEffectiveSize);
        Assert.Equal("#FFFFD100", vm.SelectedTextEffectiveColor);
    }

    private static void AddDesignObject(MainWindowViewModel vm, FrameKind kind)
    {
        switch (kind)
        {
            case FrameKind.FRAME:
                // Exercise the toolbar's exact New Blank -> Add Frame path.
                vm.AddFrame();
                break;
            case FrameKind.FONTSTRING:
                vm.AddDesignText();
                break;
            case FrameKind.TEXTURE:
                vm.AddDesignImage();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Crimson);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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
        public StockFontStyle? ResolveFont(string? name)
        {
            var (size, color) = name switch
            {
                "GameFontNormal" => (12d, new ColorRgba(1, 0.82, 0, 1)),
                "GameFontHighlight" => (12d, ColorRgba.White),
                "GameFontNormalSmall" => (10d, new ColorRgba(1, 0.82, 0, 1)),
                "GameFontHighlightSmall" => (10d, ColorRgba.White),
                "GameFontNormalLarge" => (16d, new ColorRgba(1, 0.82, 0, 1)),
                "GameFontHighlightLarge" => (16d, ColorRgba.White),
                _ => (0d, default),
            };
            return size == 0 ? null : new StockFontStyle(name!, @"Fonts\FRIZQT__.TTF", null,
                "Friz Quadrata TT", size, color, "CENTER", "MIDDLE", null, 1, -1,
                new ColorRgba(0, 0, 0, 1),
                [new StockPropertyProvenance("font style", name!, @"Interface\FrameXML\FontStyles.xml")]);
        }
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
