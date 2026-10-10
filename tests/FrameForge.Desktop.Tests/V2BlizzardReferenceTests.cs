using Avalonia.Controls;
using Avalonia.Headless;
using System.Security.Cryptography;
using FrameForge.Core.Export;
using FrameForge.Core.Models;
using FrameForge.Core.Semantics.V2;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Controls;
using FrameForge.Desktop.Rendering;
using FrameForge.Desktop.Services;
using FrameForge.Desktop.ViewModels;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class V2BlizzardReferenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"frameforge-v2-lfd-{Guid.NewGuid():N}");

    public V2BlizzardReferenceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void DirectStarterCreatesStableProtectedV2ReferenceAndRoundTripsMetadata()
    {
        var path = WriteXml();

        var created = V2DungeonFinderStarter.Create(path);

        Assert.True(created.Success, string.Join(Environment.NewLine, created.Errors));
        var document = created.Document!;
        var reference = Assert.Single(document.Editor!.ReferenceCompositions);
        Assert.Equal(V2DungeonFinderStarter.SourceIdentity, reference.SourceIdentity);
        Assert.True(document.Editor.Groups.Single(group => group.Id == reference.LockGroupId).Locked);
        Assert.All(reference.Members, id => Assert.True(document.Nodes.Single(node => node.Id == id).Editor!.ReferenceOnly));
        Assert.Contains(document.Nodes, node => node.RuntimeName == "LFDParentFrameBackground" &&
                                               node.AuthoredProperties.Texture?.TextureReference == @"Interface\LFGFrame\DungeonFinder.tga");
        var stockButton = document.Nodes.Single(node => node.RuntimeName == "LFDParentFrameJoinButton");
        Assert.Equal("UIPanelButtonTemplate", stockButton.Editor!.ReferenceTemplate);
        Assert.Null(stockButton.BlizzardTemplate);
        Assert.Equal("UIPanelButtonTemplate",
            UiTemplateEffectiveProperties.Resolve(stockButton, V2TemplateDesignerTests.Registry()).TemplateIdentity);
        var automaticText = document.Nodes.Single(node => node.RuntimeName == "LFDParentFrameAutomaticText");
        Assert.Equal(280, automaticText.AuthoredProperties.Region!.Width);
        Assert.Null(automaticText.AuthoredProperties.Region.Height);
        Assert.True(automaticText.Editor!.ReferenceAutoHeight);
        Assert.Equal("GameFontNormal", automaticText.AuthoredProperties.FontString!.FontReference);
        var diagnostics = UiDocumentValidator.Validate(document);
        Assert.DoesNotContain(diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.Contains(diagnostics, item => item.Code == "FFV2-REF-GEOMETRY-001" && item.NodeId == automaticText.Id);

        var roundTrip = UiDocumentCodec.Parse(UiDocumentCodec.Serialize(document));
        Assert.True(roundTrip.Ok, string.Join(Environment.NewLine, roundTrip.Errors));
        Assert.Equal(reference.SourceIdentity, Assert.Single(roundTrip.Document!.Editor!.ReferenceCompositions).SourceIdentity);
        var preview = Assert.Single(roundTrip.Document.Editor.PreviewStates);
        Assert.Equal("Dungeon Finder open", preview.Name);
        var stockRoot = document.Nodes.Single(node => node.RuntimeName == "LFDParentFrame");
        Assert.False(stockRoot.AuthoredProperties.Frame!.Visible);
        Assert.True(Assert.Single(preview.Overrides).Visible);
        Assert.Equal(stockRoot.Id, preview.Overrides[0].NodeId);
    }

    [Fact]
    public void ReferenceVisibilityPersistsHonorsHiddenAncestorsAndNeverChangesExport()
    {
        var document = V2DungeonFinderStarter.Create(WriteXml()).Document!;
        var reference = Assert.Single(document.Editor!.ReferenceCompositions);
        var root = document.Nodes.Single(node => node.Id == reference.RootNodeId);
        var texture = document.Nodes.Single(node => node.Kind == UiNodeKind.Texture);
        var editor = new UiDocumentEditor();
        var before = V2FrameXmlExporter.Build(document, null);

        var hidden = editor.SetReferenceVisibility(document, root.Id, visible: false, includeDescendants: true);
        Assert.True(hidden.Success, hidden.ErrorText);
        Assert.Contains(root.Id, hidden.Document.Editor!.HiddenReferenceNodes);
        Assert.Contains(texture.Id, hidden.Document.Editor.HiddenReferenceNodes);

        var childShown = editor.SetReferenceVisibility(hidden.Document, texture.Id, visible: true);
        Assert.True(childShown.Success, childShown.ErrorText);
        Assert.DoesNotContain(texture.Id, childShown.Document.Editor!.HiddenReferenceNodes);
        var state = Assert.Single(childShown.Document.Editor.PreviewStates);
        var presentation = UiPreviewPresentation.Apply(childShown.Document, state);
        var rootDefinition = childShown.Document.CompositionRoots.Single();
        var layout = UiPreviewPresentation.ApplyVisibility(
            UiLayoutResolver.Resolve(presentation, UiPreviewHost.FromDesignRoot(rootDefinition)), state);
        Assert.False(layout.Elements[texture.Id].EffectiveVisible);

        var parsed = UiDocumentCodec.Parse(UiDocumentCodec.Serialize(childShown.Document));
        Assert.True(parsed.Ok, parsed.ErrorText);
        Assert.Contains(root.Id, parsed.Document!.Editor!.HiddenReferenceNodes);
        var after = V2FrameXmlExporter.Build(parsed.Document, null);
        Assert.Equal(before.Xml, after.Xml);
        Assert.True(after.IsValid, string.Join(" ", after.Diagnostics.Select(item => item.Message)));

        var restored = editor.RestoreReferenceComposition(parsed.Document, reference.Id);
        Assert.True(restored.Success, restored.ErrorText);
        Assert.Empty(restored.Document.Editor!.HiddenReferenceNodes);
        Assert.All(reference.Members, id => Assert.True(restored.Document.Nodes.Single(node => node.Id == id).Editor!.ReferenceOnly));
    }

    [Fact]
    public async Task ExplicitBuild12340DungeonFinderSourceImportsWhenRequested()
    {
        var xml = Environment.GetEnvironmentVariable("FRAMEFORGE_LFD_XML");
        var clientRoot = Environment.GetEnvironmentVariable("FRAMEFORGE_WOW_CLIENT");
        var cacheRoot = Environment.GetEnvironmentVariable("FRAMEFORGE_WOW_CACHE_ROOT");
        if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(clientRoot) || string.IsNullOrWhiteSpace(cacheRoot))
            return;

        using var stream = File.OpenRead(xml);
        Assert.Equal("cbb539a5a58b522b2b7bdacaf70ddbdfdf3332ad4b8c590d226460b70e209ee4",
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        var provider = new WoWClientAssetProvider(cacheRoot);
        var client = provider.ValidateClient(clientRoot);
        Assert.True(client.IsValid, client.Message);
        var templateSources = V2DungeonFinderStarter.TemplateSourcePaths.Select(logicalPath =>
        {
            var materialized = provider.Materialize(logicalPath, client);
            Assert.True(materialized.Success, $"{logicalPath}: {materialized.Message}");
            return new V2DungeonFinderTemplateSource(logicalPath, materialized.CachePath!);
        }).ToArray();
        var created = V2DungeonFinderStarter.Create(xml, templateSources);
        Assert.True(created.Success, string.Join(Environment.NewLine, created.Errors));
        var document = created.Document!;
        var reference = Assert.Single(document.Editor!.ReferenceCompositions);
        Assert.True(document.Editor.Groups.Single(group => group.Id == reference.LockGroupId).Locked);
        Assert.Equal(218, reference.Members.Count); // wrapper + direct source and inherited static regions.
        Assert.Equal(78, document.Nodes.Count(node => node.Kind == UiNodeKind.Texture));
        Assert.Equal(55, document.Nodes.Count(node => node.Kind == UiNodeKind.FontString));
        Assert.Contains(document.Nodes, node => node.RuntimeName == "LFDQueueFrameBackground" &&
                                               node.AuthoredProperties.Texture?.TextureReference == @"Interface\LFGFrame\UI-LFG-BACKGROUND-QUESTPAPER");
        var tank = document.Nodes.Single(node => node.RuntimeName == "LFDQueueFrameRoleButtonTank");
        Assert.Equal((48d, 48d), (tank.AuthoredProperties.Frame!.Width, tank.AuthoredProperties.Frame.Height));
        var roleBackground = document.Nodes.Single(node => node.RuntimeName == "LFDQueueFrameRoleButtonTankBackground");
        Assert.Equal(@"Interface\LFGFrame\UI-LFG-ICONS-ROLEBACKGROUNDS",
            roleBackground.AuthoredProperties.Texture!.TextureReference);
        Assert.Equal((80d, 80d),
            (roleBackground.AuthoredProperties.Region!.Width, roleBackground.AuthoredProperties.Region.Height));
        Assert.Equal(@"Interface\FrameXML\LFGFrame.xml", roleBackground.Editor!.ReferenceSource);
        Assert.Equal("LFGRoleButtonWithBackgroundTemplate", roleBackground.Editor.ReferenceTemplate);
        var healer = document.Nodes.Single(node => node.RuntimeName == "LFDQueueFrameRoleButtonHealer");
        Assert.Equal(tank.Id, Assert.Single(healer.Anchors).Target.NodeId);
        var dropdownLeft = document.Nodes.Single(node => node.RuntimeName == "LFDQueueFrameTypeDropDownLeft");
        Assert.Equal(new UiTexCoords(0, 0.1953125, 0, 1), dropdownLeft.AuthoredProperties.Texture!.TexCoords);
        var automatic = document.Nodes.Where(node => node.Editor is { ReferenceAutoWidth: true } or { ReferenceAutoHeight: true }).ToArray();
        Assert.True(automatic.Length >= 14);
        Assert.Contains(automatic, node => node.RuntimeName == "LFDQueueFrameRandomScrollFrameChildFrameDescription" &&
                                          node.Kind == UiNodeKind.FontString &&
                                          node.AuthoredProperties.Region!.Height is null);
        var semantic = UiDocumentValidator.Validate(document);
        Assert.DoesNotContain(semantic, item => item.Code == "FFV2-PROP-019");
        var previewState = Assert.Single(document.Editor.PreviewStates);
        var presentation = UiPreviewPresentation.Apply(document, previewState);
        var layout = UiPreviewPresentation.ApplyVisibility(
            UiLayoutResolver.Resolve(presentation, UiPreviewHost.FromDesignRoot(document.CompositionRoots.Single())),
            previewState);
        Assert.Equal(175, layout.Elements.Values.Count(item => item.Rect is not null));
        Assert.Equal(44, layout.Elements.Values.Count(item => item.Rect is null));
        Assert.Equal(67, layout.Elements.Values.Count(item => item.Node?.Kind == UiNodeKind.Texture && item.Rect is not null));
        Assert.Equal(25, layout.Elements.Values.Count(item => item.Node?.Kind == UiNodeKind.Texture &&
                                                               item.Rect is not null && item.EffectiveVisible));
        var hiddenRoleCover = document.Nodes.First(node => node.Kind == UiNodeKind.Texture &&
            node.AuthoredProperties.Region?.Visible == false &&
            node.AuthoredProperties.Texture?.TextureReference == @"Interface\LFGFrame\UI-LFG-ICON-ROLES");
        Assert.False(layout.Elements[hiddenRoleCover.Id].OwnVisible);
        Assert.False(layout.Elements[hiddenRoleCover.Id].EffectiveVisible);
        var background = document.Nodes.Single(node => node.RuntimeName == "LFDQueueFrameBackground");
        var frameArt = document.Nodes.Single(node => node.RuntimeName == "LFDQueueFrameLayout");
        Assert.Equal(21, background.Anchors.Single().OffsetX); // nested Offset/AbsDimension from LFDFrame.xml
        Assert.Equal(-64, background.Anchors.Single().OffsetY);
        Assert.True(Array.IndexOf(layout.PaintOrder.ToArray(), frameArt.Id) <
                    Array.IndexOf(layout.PaintOrder.ToArray(), background.Id));
        var findMiddle = document.Nodes.Single(node => node.RuntimeName == "LFDQueueFrameFindGroupButtonMiddle");
        Assert.Equal(111, layout.Elements[findMiddle.Id].Rect!.Value.Width); // opposing native anchors override template seed width
        var textureReferences = document.Nodes.Select(node => node.AuthoredProperties.Texture?.TextureReference)
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.NotEmpty(textureReferences);
        Assert.All(textureReferences, referencePath =>
        {
            var materialized = provider.Materialize(referencePath, client);
            Assert.True(materialized.Success, $"{referencePath}: {materialized.Message}");
        });
        using (var textures = new TextureAssetResolver())
        {
            textures.Configure(xml, [cacheRoot]);
            textures.ConfigureWowAssetSource(provider, client);
            var resolved = textures.Resolve(@"Interface\LFGFrame\UI-LFG-BACKGROUND-QUESTPAPER");
            Assert.True(resolved.CanRender, resolved.Diagnostic.Message);

            using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
            var render = await session.Dispatch(() =>
            {
                var canvas = new LayoutCanvas
                {
                    V2Layout = layout, AssetResolver = textures, Mode = CanvasViewMode.PREVIEW,
                };
                var window = new Window { Content = canvas, Width = 1024, Height = 768, ShowActivated = false };
                try
                {
                    window.Show();
                    window.CaptureRenderedFrame();
                    canvas.FitToContent();
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    window.CaptureRenderedFrame();
                    return Assert.IsType<CanvasRenderTrace>(canvas.LastRenderTrace);
                }
                finally { window.Close(); }
            }, CancellationToken.None);
            Assert.True(render.VisualContentExecuted);
            Assert.Equal(25, render.VisualAttemptsByKind.GetValueOrDefault(FrameForge.Core.Models.FrameKind.TEXTURE));
            Assert.True(render.VisualPaintOperations >= 25);
        }

        var editor = new UiDocumentEditor();
        var overlay = editor.CreateControl(document, UiNodeKind.Button,
            OwnerReference.Root(document.CompositionRoots[0].Id), "Actual source overlay", "ActualSourceOverlay");
        Assert.True(overlay.Success, overlay.ErrorText);
        var export = V2FrameXmlExporter.Build(overlay.Document, null);
        Assert.True(export.IsValid, string.Join(Environment.NewLine, export.Diagnostics.Select(item => item.Message)));
        Assert.Contains("ActualSourceOverlay", export.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain("LFDParentFrame", export.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain("UI-LFG-BACKGROUND-QUESTPAPER", export.Xml, StringComparison.Ordinal);

        var settings = Path.Combine(_directory, "actual-client-settings.json");
        new AssetSettingsStore(settings).SaveConfiguration(new FrameForgeLocalSettings([], clientRoot));
        var viewModel = new MainWindowViewModel(settings, provider,
            new V2TemplateDesignerTests.PassThroughStockTemplates());
        Assert.True(viewModel.NewV2DungeonFinderProject(), viewModel.Status);
        await viewModel.V2TemplateRegistryLoadingTask;
        Assert.True(viewModel.IsV2Project);
        Assert.Equal("DungeonFinderDesignRoot", viewModel.V2Document!.CompositionRoots.Single().RuntimeName);
        Assert.Equal("Dungeon Finder open", viewModel.SelectedV2PresentationState!.Label);
        Assert.True(viewModel.V2Layout!.Elements[
            viewModel.V2Document.Nodes.Single(node => node.RuntimeName == "LFDQueueFrameBackground").Id].EffectiveVisible);
        Assert.DoesNotContain(viewModel.V2Diagnostics, item => item.Code == "FFV2-PROP-019");
    }

    [Fact]
    public void ReferenceLockUnlockAndRestoreAreUndoableSemanticOperations()
    {
        var document = V2DungeonFinderStarter.Create(WriteXml()).Document!;
        var reference = document.Editor!.ReferenceCompositions.Single();
        var editor = new UiDocumentEditor();
        Assert.False(editor.MoveSelection(document, [reference.RootNodeId], 10, 0).Success);

        var unlocked = editor.SetReferenceLocked(document, reference.Id, false);
        Assert.True(unlocked.Success, unlocked.ErrorText);
        var relocked = editor.SetReferenceLocked(unlocked.Document, reference.Id, true);
        Assert.True(relocked.Success, relocked.ErrorText);
        Assert.True(relocked.Document.Editor!.Groups.Single(group => group.Id == reference.LockGroupId).Locked);
        var moved = editor.MoveSelection(unlocked.Document, [reference.RootNodeId], 10, 0);
        Assert.True(moved.Success, moved.ErrorText);
        Assert.NotEqual(document.Nodes.Single(node => node.Id == reference.RootNodeId).Anchors,
            moved.Document.Nodes.Single(node => node.Id == reference.RootNodeId).Anchors);

        var restored = editor.RestoreReferenceComposition(moved.Document, reference.Id);
        Assert.True(restored.Success, restored.ErrorText);
        Assert.Equal(document.Nodes.Single(node => node.Id == reference.RootNodeId).Anchors,
            restored.Document.Nodes.Single(node => node.Id == reference.RootNodeId).Anchors);

        var session = new SemanticEditingSession(document);
        Assert.True(session.Execute("Unlock", (gateway, current) => gateway.SetReferenceLocked(current, reference.Id, false)).Success);
        Assert.True(session.Undo().Success);
        Assert.True(session.Document.Editor!.Groups.Single(group => group.Id == reference.LockGroupId).Locked);
    }

    [Fact]
    public void ExportExcludesReferenceAndIncludesOnlyAuthoredOverlay()
    {
        var document = V2DungeonFinderStarter.Create(WriteXml()).Document!;
        var editor = new UiDocumentEditor();
        var created = editor.CreateControl(document, UiNodeKind.Button,
            OwnerReference.Root(document.CompositionRoots[0].Id), "Authored Overlay", "FrameForgeOverlayButton");
        Assert.True(created.Success, created.ErrorText);

        var plan = V2FrameXmlExporter.Build(created.Document, null);

        Assert.True(plan.IsValid, string.Join(Environment.NewLine, plan.Diagnostics.Select(item => item.Message)));
        Assert.Contains("FrameForgeOverlayButton", plan.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain("LFDParentFrame", plan.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain("DungeonFinder.tga", plan.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain("referenceCompositions", plan.Manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationStateChangesOnlyTransientValuesAndNeverGeometryOrSerialization()
    {
        var document = V2DungeonFinderStarter.Create(WriteXml()).Document!;
        var texture = document.Nodes.Single(node => node.Kind == UiNodeKind.Texture);
        var state = new SemanticPreviewState
        {
            Id = SemanticId.New(), Name = "Hidden reference",
            Overrides = [new SemanticPreviewOverride { NodeId = texture.Id, Visible = false, TextureReference = "PreviewOnly" }],
        };
        var before = UiDocumentCodec.Serialize(document);
        var normal = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(document.CompositionRoots[0]));

        var presentation = UiPreviewPresentation.Apply(document, state);
        var preview = UiPreviewPresentation.ApplyVisibility(
            UiLayoutResolver.Resolve(presentation, UiPreviewHost.FromDesignRoot(document.CompositionRoots[0])), state);

        Assert.Equal(before, UiDocumentCodec.Serialize(document));
        Assert.Equal(normal.Elements[texture.Id].Rect, preview.Elements[texture.Id].Rect);
        Assert.False(preview.Elements[texture.Id].EffectiveVisible);
        Assert.Equal("PreviewOnly", presentation.Nodes.Single(node => node.Id == texture.Id).AuthoredProperties.Texture!.TextureReference);
    }

    [Fact]
    public void ViewModelStarterUsesValidatedAssetsAddsOverlayOutsideReferenceAndPreviewBlocksEditing()
    {
        var settings = Path.Combine(_directory, "settings.json");
        new AssetSettingsStore(settings).SaveConfiguration(new FrameForgeLocalSettings([], "synthetic-client"));
        var provider = new StarterProvider(_directory);
        var vm = new MainWindowViewModel(settings, provider,
            new V2TemplateDesignerTests.PassThroughStockTemplates(),
            v2TemplateLoader: new V2TemplateDesignerTests.FakeLoader(V2TemplateDesignerTests.Registry()));

        Assert.True(vm.NewV2DungeonFinderProject(), vm.Status);
        Assert.True(vm.IsV2Project);
        Assert.True(vm.IsV2DesignMode);
        var reference = vm.V2Document!.Editor!.ReferenceCompositions.Single();
        Assert.Equal(reference.RootNodeId, vm.SelectedV2Node!.Id);
        vm.AddV2Control(UiNodeKind.Button);
        var overlay = vm.SelectedV2Node!;
        Assert.Equal(OwnerReference.Root(vm.V2Document.CompositionRoots[0].Id), overlay.Owner);
        var before = overlay.Anchors[0];

        vm.SetV2DesignMode(false);
        vm.MoveV2SelectionBy(25, -10);

        Assert.True(vm.IsV2PreviewMode);
        Assert.Equal(before, vm.V2Document.Nodes.Single(node => node.Id == overlay.Id).Anchors[0]);
        Assert.Contains("read-only", vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(V2DungeonFinderStarter.SourcePath, provider.Materialized, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"Interface\LFGFrame\DungeonFinder.tga", provider.Materialized, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewCanvasHidesSelectionChromeAndResizeHandles()
    {
        var document = V2DungeonFinderStarter.Create(WriteXml()).Document!;
        var selected = document.Editor!.ReferenceCompositions.Single().RootNodeId;
        var layout = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(document.CompositionRoots[0]));
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

        var result = await session.Dispatch(() =>
        {
            var canvas = new LayoutCanvas
            {
                V2Layout = layout, Mode = CanvasViewMode.PREVIEW,
                SelectedName = selected.Value, SelectedNames = [selected.Value], ShowGrid = true,
            };
            var window = new Window { Content = canvas, Width = 640, Height = 480, ShowActivated = false };
            try
            {
                window.Show();
                window.CaptureRenderedFrame();
                return (canvas.GetV2ResizeHandles().Count, canvas.LastRenderTrace!.ActiveLayers);
            }
            finally { window.Close(); }
        }, CancellationToken.None);

        Assert.Equal(0, result.Count);
        Assert.DoesNotContain("selection-overlay", result.ActiveLayers);
        Assert.DoesNotContain("debug-overlay", result.ActiveLayers);
    }

    [Fact]
    public void MissingOrInvalidClientSourcesReturnActionableDiagnosticsAndKeepCurrentProject()
    {
        var missingNode = Path.Combine(_directory, "missing-node.xml");
        File.WriteAllText(missingNode, "<Ui><Frame name=\"Other\"/></Ui>");
        var parsed = V2DungeonFinderStarter.Create(missingNode);
        Assert.False(parsed.Success);
        Assert.Contains("LFDParentFrame", parsed.Errors.Single(), StringComparison.Ordinal);

        var invalidDimension = Path.Combine(_directory, "invalid-dimension.xml");
        File.WriteAllText(invalidDimension, """
            <Ui><Frame name="LFDParentFrame"><Size x="355" y="440"/><Layers><Layer>
              <Texture name="$parentBroken" file="Interface\Broken"><Size x="-1" y="20"/></Texture>
            </Layer></Layers></Frame></Ui>
            """);
        var invalidGeometry = V2DungeonFinderStarter.Create(invalidDimension);
        Assert.False(invalidGeometry.Success);
        Assert.Contains(invalidGeometry.Errors,
            error => error.Contains("FFV2-PROP-019", StringComparison.Ordinal) &&
                     error.Contains("LFDParentFrameBroken", StringComparison.Ordinal));

        var settings = Path.Combine(_directory, "invalid-settings.json");
        var vm = new MainWindowViewModel(settings, new InvalidProvider(_directory),
            new V2TemplateDesignerTests.PassThroughStockTemplates());
        vm.NewV2Project();
        var before = vm.V2Document;
        Assert.False(vm.NewV2DungeonFinderProject());
        Assert.Same(before, vm.V2Document);
        Assert.Contains("configured", vm.Status, StringComparison.OrdinalIgnoreCase);

        var validSettings = Path.Combine(_directory, "missing-asset-settings.json");
        new AssetSettingsStore(validSettings).SaveConfiguration(new FrameForgeLocalSettings([], "synthetic-client"));
        var missingAssetVm = new MainWindowViewModel(validSettings, new StarterProvider(_directory, failArtwork: true),
            new V2TemplateDesignerTests.PassThroughStockTemplates(),
            v2TemplateLoader: new V2TemplateDesignerTests.FakeLoader(V2TemplateDesignerTests.Registry()));
        missingAssetVm.NewV2Project();
        var blank = missingAssetVm.V2Document;
        Assert.False(missingAssetVm.NewV2DungeonFinderProject());
        Assert.Same(blank, missingAssetVm.V2Document);
        Assert.Contains("artwork is incomplete", missingAssetVm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanvasLabelsPreferDisplayLabelThenRuntimeNameWithoutChangingSemanticIdentity()
    {
        var id = SemanticId.New();
        var friendly = new ResolvedUiElement { Id = id, DisplayLabel = "Join Queue", RuntimeName = "LFDJoinButton" };
        var runtimeFallback = friendly with { DisplayLabel = " " };

        var first = new DrawableFrame(id.Value, null, null, null, true, false, false, true, V2Element: friendly);
        var second = first with { V2Element = runtimeFallback };

        Assert.Equal("Join Queue", first.DisplayName);
        Assert.Equal("LFDJoinButton", second.DisplayName);
        Assert.Equal(id.Value, first.Name);
    }

    private string WriteXml()
    {
        var path = Path.Combine(_directory, $"LFDFrame-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, StarterProvider.Xml);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class StarterProvider(string cacheRoot, bool failArtwork = false) : IWoWClientAssetProvider
    {
        public const string Xml = """
            <Ui xmlns="http://www.blizzard.com/wow/ui/">
              <Frame name="LFDParentFrame" parent="UIParent" frameStrata="DIALOG" hidden="true">
                <Size x="355" y="440"/><Anchors><Anchor point="CENTER" relativeTo="UIParent"/></Anchors>
                <Layers><Layer level="BACKGROUND">
                  <Texture name="$parentBackground" file="Interface\LFGFrame\DungeonFinder.tga" setAllPoints="true">
                    <TexCoords left="0.1" right="0.9" top="0.2" bottom="0.8"/>
                  </Texture>
                </Layer><Layer level="ARTWORK">
                  <FontString name="$parentAutomaticText" inherits="GameFontNormal" text="AUTO_HEIGHT"><Size><AbsDimension x="280" y="0"/></Size><Anchors><Anchor point="TOP"/></Anchors></FontString>
                </Layer></Layers>
                <Frames><Button name="$parentJoinButton" inherits="UIPanelButtonTemplate" text="Join"><Size x="120" y="24"/><Anchors><Anchor point="BOTTOM" x="0" y="20"/></Anchors></Button></Frames>
              </Frame>
            </Ui>
            """;

        public List<string> Materialized { get; } = [];
        public string CacheRoot { get; } = cacheRoot;
        public WowClientValidation ValidateClient(string? clientPath) => new(WowClientValidationStatus.Valid,
            clientPath, new WowClientBuild(3, 3, 5, 12340), "enUS", [], "valid");
        public AssetMaterializationResult Materialize(string reference, WowClientValidation client)
        {
            Materialized.Add(reference);
            var path = Path.Combine([CacheRoot, .. reference.Replace('\\', '/').Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (string.Equals(reference, V2DungeonFinderStarter.SourcePath, StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(path, Xml);
            else if (V2DungeonFinderStarter.TemplateSourcePaths.Contains(reference, StringComparer.OrdinalIgnoreCase))
                File.WriteAllText(path, "<Ui xmlns=\"http://www.blizzard.com/wow/ui/\" />");
            else if (failArtwork)
                return new AssetMaterializationResult(false, reference, null, null, false, "synthetic missing artwork");
            else
                File.WriteAllBytes(path, TinyTga());
            return new AssetMaterializationResult(true, reference, path,
                new StockAssetProvenance(reference, "synthetic.MPQ", client.ClientPath!, client.Build!.ToString(),
                    client.Locale!, new FileInfo(path).Length, "synthetic", DateTimeOffset.UtcNow, path), false, "materialized");
        }
        public StockAssetProvenance? GetProvenance(string physicalPath) => null;
        public void ClearCache() { }

        private static byte[] TinyTga() =>
        [
            0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 0, 1, 0, 32, 0x28,
            0, 0, 255, 255,
        ];
    }

    private sealed class InvalidProvider(string cacheRoot) : IWoWClientAssetProvider
    {
        public string CacheRoot { get; } = cacheRoot;
        public WowClientValidation ValidateClient(string? clientPath) =>
            new(WowClientValidationStatus.NotConfigured, null, null, null, [], "not configured");
        public AssetMaterializationResult Materialize(string reference, WowClientValidation client) =>
            new(false, reference, null, null, false, "unavailable");
        public StockAssetProvenance? GetProvenance(string physicalPath) => null;
        public void ClearCache() { }
    }
}
