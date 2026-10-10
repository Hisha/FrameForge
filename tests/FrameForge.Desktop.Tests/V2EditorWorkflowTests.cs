using FrameForge.Core.Models;
using FrameForge.Core.Geometry;
using FrameForge.Core.Semantics.V2;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using System.Xml.Linq;
using FrameForge.Core.Export;
using SkiaSharp;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class V2EditorWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"frameforge-v2-editor-{Guid.NewGuid():N}");

    public V2EditorWorkflowTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void NewV2WorkflowOpensRootDirectlyInExistingTreeAndCanvasProjection()
    {
        var vm = ViewModel();

        vm.NewV2Project();

        Assert.True(vm.IsV2Project);
        Assert.False(vm.IsV1Project);
        var root = Assert.Single(vm.V2Document!.CompositionRoots);
        Assert.Equal(root.Id.Value, vm.SelectedName);
        Assert.Equal(root.Id.Value, Assert.Single(vm.TreeRoots).Name);
        Assert.True(vm.V2Layout!.Elements.ContainsKey(root.Id));
        Assert.Empty(vm.V2Diagnostics);
    }

    [Fact]
    public void AddCommandsCreateAllKindsUnderSelectedContainerOrRoot()
    {
        var vm = ViewModel();
        vm.NewV2Project();
        vm.AddV2Control(UiNodeKind.Frame);
        var frame = vm.SelectedV2Node!;
        vm.AddV2Control(UiNodeKind.Texture);
        vm.Select(vm.V2Document!.CompositionRoots[0].Id.Value);
        vm.AddV2Control(UiNodeKind.FontString);
        vm.AddV2Control(UiNodeKind.Button);
        vm.AddV2Control(UiNodeKind.StatusBar);

        Assert.Equal(5, vm.V2Document!.Nodes.Count);
        Assert.Equal(OwnerReference.Node(frame.Id), vm.V2Document.Nodes.Single(node => node.Kind == UiNodeKind.Texture).Owner);
        Assert.All(vm.V2Document.Nodes, node => Assert.Equal(AnchorTargetKind.Parent, node.Anchors[0].Target.Kind));
    }

    [Fact]
    public void CanvasDragAndInspectorResizeUpdateSemanticStateNotProjectionOnly()
    {
        var vm = ViewModel();
        vm.NewV2Project();
        vm.AddV2Control(UiNodeKind.Button);
        var id = vm.SelectedV2Node!.Id;

        vm.DragFrame(id.Value, 20, -12);
        vm.EndDrag();
        Assert.Equal((20d, -12d), (vm.SelectedV2Node!.Anchors[0].OffsetX, vm.SelectedV2Node.Anchors[0].OffsetY));

        vm.V2WidthDraft = "240";
        vm.V2HeightDraft = "44";
        vm.V2DisplayLabelDraft = "Submit";
        vm.V2RuntimeNameDraft = "QuestSubmitButton";
        vm.ApplyV2Inspector();

        var edited = vm.V2Document!.Nodes.Single(node => node.Id == id);
        Assert.Equal(id, edited.Id);
        Assert.Equal((240d, 44d), (edited.AuthoredProperties.Frame!.Width, edited.AuthoredProperties.Frame.Height));
        Assert.Equal("Submit", edited.DisplayLabel);
        Assert.Equal("QuestSubmitButton", edited.RuntimeName);
        Assert.Equal(AnchorTargetKind.Parent, edited.Anchors[0].Target.Kind);
    }

    [Fact]
    public void TextureCoordinatesUpdatePreviewExportAndUndoRedo()
    {
        var vm = ViewModel();
        vm.NewV2Project();
        vm.AddV2Control(UiNodeKind.Texture);
        vm.V2TextureDraft = @"Interface\Buttons\WHITE8X8";
        vm.V2UseTexCoordsDraft = true;
        vm.V2TexCoordLeftDraft = "0.125";
        vm.V2TexCoordRightDraft = "0.875";
        vm.V2TexCoordTopDraft = "0.25";
        vm.V2TexCoordBottomDraft = "0.75";
        vm.ApplyV2Inspector();

        var coords = vm.SelectedV2Node!.AuthoredProperties.Texture!.TexCoords!;
        Assert.Equal(new UiTexCoords(.125, .875, .25, .75), coords);
        var plan = V2FrameXmlExporter.Build(vm.V2Document!, null);
        Assert.True(plan.IsValid, string.Join(" ", plan.Diagnostics.Select(item => item.Message)));
        Assert.Contains("left=\"0.125\"", plan.Xml, StringComparison.Ordinal);

        vm.UndoV2();
        Assert.Null(vm.SelectedV2Node!.AuthoredProperties.Texture!.TexCoords);
        vm.RedoV2();
        Assert.Equal(coords, vm.SelectedV2Node!.AuthoredProperties.Texture!.TexCoords);

        vm.V2TexCoordLeftDraft = "0.9";
        vm.V2TexCoordRightDraft = "0.1";
        vm.ApplyV2Inspector();
        Assert.Equal(coords, vm.SelectedV2Node!.AuthoredProperties.Texture!.TexCoords);
        Assert.Contains("ordered", vm.V2InspectorValidation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportedPngIsPreparedAsPortableTgaAndRoundTripsWithoutPngRuntimeLeakage()
    {
        var projectPath = Path.Combine(_directory, "custom-art.fforge.json");
        var sourcePath = Path.Combine(_directory, "outside.png");
        using (var bitmap = new SKBitmap(2, 2))
        {
            bitmap.Erase(SKColors.CornflowerBlue);
            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(sourcePath, png.ToArray());
        }
        var vm = ViewModel();
        vm.NewV2Project();
        Assert.True(vm.SaveV2ToFile(projectPath), vm.Status);
        vm.AddV2Control(UiNodeKind.Texture);

        Assert.True(vm.ImportSelectedV2Texture(sourcePath, importExternal: true), vm.Status);
        var runtimeReference = vm.SelectedV2Node!.AuthoredProperties.Texture!.TextureReference!;
        Assert.EndsWith(".tga", runtimeReference, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".png", runtimeReference, StringComparison.OrdinalIgnoreCase);
        var asset = Assert.Single(vm.V2Document!.Editor!.ProjectAssets);
        Assert.EndsWith(".png", asset.SourceReference, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("prepared-as-uncompressed-32-bit-tga", asset.ConversionStatus);
        Assert.StartsWith(@"Interface\FrameForge\Artwork\", asset.IntendedClientPath, StringComparison.Ordinal);
        Assert.True(vm.Assets.Resolve(runtimeReference).CanRender);
        var choices = await vm.V2TextureChoicesAsync();
        Assert.Contains(choices, choice => choice.InterfacePath == asset.PreparedReference && choice.Category == "Project artwork");
        vm.SelectV2(vm.V2Document.CompositionRoots.Single().Id, false);
        vm.AddV2Control(UiNodeKind.Texture);
        Assert.True(vm.AssignSelectedV2Texture(asset.PreparedReference), vm.Status);
        Assert.Equal(asset.PreparedReference, vm.SelectedV2Node!.AuthoredProperties.Texture!.TextureReference);
        Assert.True(vm.SaveV2ToFile(projectPath), vm.Status);

        var reopened = ViewModel();
        reopened.OpenFromFile(projectPath);
        Assert.All(reopened.V2Document!.Nodes, node =>
            Assert.Equal(runtimeReference, node.AuthoredProperties.Texture!.TextureReference));
        Assert.Single(reopened.V2Document.Editor!.ProjectAssets);
        var export = V2FrameXmlExporter.Build(reopened.V2Document, projectPath);
        Assert.True(export.IsValid, string.Join(" ", export.Diagnostics.Select(item => item.Message)));
        Assert.DoesNotContain(".png", export.Xml, StringComparison.OrdinalIgnoreCase);
        Assert.Single(export.Assets);
    }

    [Fact]
    public void ParentPickerReparentsAndPreservesCanvasRectangle()
    {
        var vm = ViewModel();
        vm.NewV2Project();
        vm.AddV2Control(UiNodeKind.Frame);
        var first = vm.SelectedV2Node!;
        vm.Select(vm.V2Document!.CompositionRoots[0].Id.Value);
        vm.AddV2Control(UiNodeKind.Frame);
        var second = vm.SelectedV2Node!;
        vm.Select(first.Id.Value);
        vm.AddV2Control(UiNodeKind.Button);
        var child = vm.SelectedV2Node!;
        var before = vm.V2Layout!.Elements[child.Id].Rect;

        vm.V2OwnerDraft = vm.V2OwnerOptions.Single(option => option.Owner == OwnerReference.Node(second.Id));
        vm.ApplyV2Inspector();

        Assert.Equal(before, vm.V2Layout!.Elements[child.Id].Rect);
        Assert.Equal(OwnerReference.Node(second.Id), vm.SelectedV2Node!.Owner);
        Assert.Equal(AnchorTargetKind.Parent, vm.SelectedV2Node.Anchors[0].Target.Kind);
    }

    [Fact]
    public void SaveOpenRoundTripKeepsSemanticDocumentAndV1RemainsSeparate()
    {
        var path = Path.Combine(_directory, "workflow.fforge.json");
        var vm = ViewModel();
        vm.NewV2Project();
        vm.AddV2Control(UiNodeKind.StatusBar);
        var id = vm.SelectedV2Node!.Id;
        vm.V2StatusMinimumDraft = "0";
        vm.V2StatusMaximumDraft = "250";
        vm.V2StatusValueDraft = "125";
        vm.ApplyV2Inspector();
        Assert.True(vm.SaveToFile(path), vm.Status);

        var reopened = ViewModel();
        reopened.OpenFromFile(path);

        Assert.True(reopened.IsV2Project);
        Assert.Equal(id, Assert.Single(reopened.V2Document!.Nodes).Id);
        Assert.Equal(250, reopened.V2Document.Nodes[0].AuthoredProperties.StatusBar!.Maximum);
        reopened.NewProject();
        Assert.True(reopened.IsV1Project);
        Assert.Null(reopened.V2Document);
    }

    [Fact]
    public async Task FrameStatusBarFontStringButtonWorkflowPreservesChildEditingAndRoundTripsDeterministically()
    {
        var path = Path.Combine(_directory, "acceptance.fforge.json");
        var vm = TemplateViewModel();
        vm.NewV2Project();
        await vm.V2TemplateRegistryLoadingTask;

        vm.AddV2Control(UiNodeKind.Frame);
        var frameId = vm.SelectedV2Node!.Id;
        vm.V2WidthDraft = "500";
        vm.V2HeightDraft = "300";
        vm.ApplyV2Inspector();

        vm.AddV2Control(UiNodeKind.StatusBar);
        var statusId = vm.SelectedV2Node!.Id;
        vm.V2WidthDraft = "250";
        vm.V2HeightDraft = "24";
        vm.V2StatusMinimumDraft = "0";
        vm.V2StatusMaximumDraft = "100";
        vm.V2StatusValueDraft = "65";
        vm.V2StatusFillColorDraft = "0,1,0,1";
        vm.V2StatusBackgroundColorDraft = "0.1,0.1,0.1,1";
        vm.ApplyV2Inspector();

        vm.AddV2Control(UiNodeKind.FontString);
        var textId = vm.SelectedV2Node!.Id;
        vm.V2TextDraft = "65 / 100";
        vm.V2FontSizeDraft = "14";
        vm.V2TintDraft = "1,1,1,1";
        vm.V2JustifyHDraft = "CENTER";
        vm.V2JustifyVDraft = "MIDDLE";
        vm.ApplyV2Inspector();

        var originalChildRect = vm.V2Layout!.Elements[textId].Rect!.Value;
        var viewport = Viewport.Identity;
        var origin = new CanvasOrigin(0, 0);
        var canvasPoint = viewport.ModelToCanvas(
            new ModelPoint((originalChildRect.Left + originalChildRect.Right) / 2,
                (originalChildRect.Top + originalChildRect.Bottom) / 2), origin);
        var hitCandidates = UiLayoutHitTester.CandidatesAt(vm.V2Layout!, viewport, origin,
            VisibilityFilter.ALL, canvasPoint.X, canvasPoint.Y);
        Assert.True(hitCandidates[0] == textId,
            $"Expected FontString first. Candidates: {string.Join(", ", hitCandidates)}; " +
            $"frame={frameId.Value}, status={statusId.Value}, text={textId.Value}");
        Assert.Contains(statusId, hitCandidates);

        var originalChildOffset = vm.SelectedV2Node!.Anchors[0];
        vm.DragFrame(textId.Value, 13, -7);
        vm.EndDrag();
        var independentlyMoved = vm.V2Layout!.Elements[textId].Rect!.Value;
        Assert.Equal(originalChildRect.Left + 13, independentlyMoved.Left);
        Assert.Equal(originalChildRect.Top - 7, independentlyMoved.Top);
        Assert.Equal(OwnerReference.Node(statusId), vm.SelectedV2Node!.Owner);
        Assert.NotEqual(originalChildOffset.OffsetX, vm.SelectedV2Node.Anchors[0].OffsetX);

        var childAnchorBeforeParentMove = vm.SelectedV2Node.Anchors[0];
        vm.Select(statusId.Value);
        var statusBefore = vm.V2Layout!.Elements[statusId].Rect!.Value;
        var childBefore = vm.V2Layout!.Elements[textId].Rect!.Value;
        vm.DragFrame(statusId.Value, 20, 11);
        vm.EndDrag();
        var statusAfter = vm.V2Layout!.Elements[statusId].Rect!.Value;
        var childAfter = vm.V2Layout!.Elements[textId].Rect!.Value;
        Assert.Equal(statusBefore.Left + 20, statusAfter.Left);
        Assert.Equal(statusBefore.Top + 11, statusAfter.Top);
        Assert.Equal(childBefore.Left + 20, childAfter.Left);
        Assert.Equal(childBefore.Top + 11, childAfter.Top);
        Assert.Equal(childAnchorBeforeParentMove,
            vm.V2Document!.Nodes.Single(node => node.Id == textId).Anchors[0]);

        vm.Select(frameId.Value);
        vm.AddV2Control(UiNodeKind.Button);
        var buttonId = vm.SelectedV2Node!.Id;
        vm.V2TemplateDraft = vm.V2TemplateOptions.Single(option => option.Identity == "UIPanelButtonTemplate");
        vm.V2TextDraft = "Test";
        vm.ApplyV2Inspector();
        Assert.Equal("UIPanelButtonTemplate", vm.SelectedV2Node!.BlizzardTemplate);
        Assert.Equal("Test", vm.SelectedV2Node.AuthoredProperties.Button!.Text);

        Assert.True(vm.SaveV2ToFile(path), vm.Status);
        var first = vm.ExportV2(Path.Combine(_directory, "export-one"));
        Assert.NotNull(first);
        Assert.True(first.Success, first.Summary);
        XNamespace ui = "http://www.blizzard.com/wow/ui/";
        var exportedStatus = XDocument.Parse(first.Plan.Xml).Descendants(ui + "StatusBar").Single();
        Assert.Single(exportedStatus.Descendants(ui + "FontString"));
        Assert.Equal("65 / 100", (string?)exportedStatus.Descendants(ui + "FontString").Single().Attribute("text"));

        var reopened = TemplateViewModel();
        reopened.NewV2Project();
        await reopened.V2TemplateRegistryLoadingTask;
        reopened.OpenFromFile(path);
        var second = reopened.ExportV2(Path.Combine(_directory, "export-two"));
        Assert.NotNull(second);
        Assert.True(second.Success, second.Summary);
        Assert.Equal(first.Plan.Xml, second.Plan.Xml);
        Assert.Equal(first.Plan.Manifest, second.Plan.Manifest);

        var status = reopened.V2Document!.Nodes.Single(node => node.Id == statusId);
        var text = reopened.V2Document.Nodes.Single(node => node.Id == textId);
        Assert.Equal(65, status.AuthoredProperties.StatusBar!.Value);
        Assert.Equal(new UiColor(0, 1, 0, 1), status.AuthoredProperties.StatusBar.FillColor);
        Assert.Equal("65 / 100", text.AuthoredProperties.FontString!.Text);
        Assert.Equal(14, text.AuthoredProperties.FontString.FontSize);
        Assert.Equal(OwnerReference.Node(statusId), text.Owner);
        Assert.Equal(OwnerReference.Node(frameId), status.Owner);
        var button = reopened.V2Document.Nodes.Single(node => node.Id == buttonId);
        Assert.Equal("UIPanelButtonTemplate", button.BlizzardTemplate);
        Assert.Equal("Test", button.AuthoredProperties.Button!.Text);
    }

    [Fact]
    public void InvalidInspectorEditIsRejectedWithoutChangingDocument()
    {
        var vm = ViewModel();
        vm.NewV2Project();
        vm.AddV2Control(UiNodeKind.Frame);
        var before = vm.V2Document;

        vm.V2WidthDraft = "-1";
        vm.ApplyV2Inspector();

        Assert.Same(before, vm.V2Document);
        Assert.Contains("positive", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SemanticSelectionSurvivesRenameAndSupportsPrimaryMultiSelection()
    {
        var vm = ViewModel();
        vm.NewV2Project();
        vm.AddV2Control(UiNodeKind.Frame);
        var first = vm.SelectedV2Node!.Id;
        vm.Select(vm.V2Document!.CompositionRoots[0].Id.Value);
        vm.AddV2Control(UiNodeKind.Button);
        var second = vm.SelectedV2Node!.Id;

        vm.Select(first.Value);
        vm.ToggleSelection(second.Value);
        vm.V2DisplayLabelDraft = "Renamed primary";
        vm.V2RuntimeNameDraft = "RenamedRuntime";
        vm.ApplyV2Inspector();

        Assert.Equal(new[] { first, second }, vm.V2Selection.OrderedIds);
        Assert.Equal(second, vm.V2Selection.PrimaryId);
        Assert.Equal("Renamed primary", vm.SelectedV2Node!.DisplayLabel);
    }

    [Fact]
    public void V2TreeSearchFilterRevealAndInspectorStayNative()
    {
        var vm = ViewModel();
        vm.NewV2Project();
        vm.AddV2Control(UiNodeKind.Frame);
        var parent = vm.SelectedV2Node!.Id;
        vm.AddV2Control(UiNodeKind.Texture);
        var texture = vm.SelectedV2Node!.Id;

        vm.TreeSearch = "texture 1";
        Assert.True(vm.IsTreeFiltering);
        Assert.Equal(texture.Value, Assert.Single(Assert.Single(vm.TreeRoots).Children).Children.Single().Name);
        vm.TreeFilter = TreeFilter.STRUCTURE;
        Assert.Empty(Assert.Single(vm.TreeRoots).Children);
        vm.TreeFilter = TreeFilter.ALL;
        vm.TreeSearch = string.Empty;
        vm.Select(texture.Value);

        Assert.True(Assert.Single(vm.TreeRoots).Children.Single(node => node.Name == parent.Value).IsExpanded);
        Assert.DoesNotContain(texture.Value, vm.Layout.Frames.Keys);
        Assert.NotNull(vm.V2Layout!.Elements[texture].Rect);
        Assert.Contains("Authored:", vm.V2DimensionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void DragIsOneUndoEntryRedoInvalidatesAndLocksBlockCommands()
    {
        var vm = ViewModel();
        vm.NewV2Project();
        vm.AddV2Control(UiNodeKind.Button);
        var id = vm.SelectedV2Node!.Id;
        var original = vm.SelectedV2Node.Anchors[0];
        vm.DragFrame(id.Value, 1, 0);
        vm.DragFrame(id.Value, 2, -3);
        vm.EndDrag();
        var moved = vm.SelectedV2Node!.Anchors[0];

        vm.UndoV2();
        Assert.Equal(original, vm.SelectedV2Node!.Anchors[0]);
        vm.RedoV2();
        Assert.Equal(moved, vm.SelectedV2Node!.Anchors[0]);
        vm.UndoV2();
        vm.MoveV2SelectionBy(5, 0);
        Assert.False(vm.CanRedoV2);

        vm.ToggleV2Lock();
        var locked = vm.SelectedV2Node!.Anchors[0];
        vm.MoveV2SelectionBy(5, 0);
        Assert.Equal(locked, vm.SelectedV2Node!.Anchors[0]);
        Assert.Contains("LOCKED", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultiSelectionSnapResizeKeyboardAndEditorGroupsUseSemanticTransactions()
    {
        var vm = ViewModel();
        vm.NewV2Project();
        var root = vm.V2Document!.CompositionRoots[0].Id;
        vm.AddV2Control(UiNodeKind.Frame);
        var first = vm.SelectedV2Node!.Id;
        vm.SelectV2(root, false);
        vm.AddV2Control(UiNodeKind.Frame);
        var second = vm.SelectedV2Node!.Id;
        vm.SelectV2(first, false);
        vm.SelectV2(second, true);
        vm.V2SnapEnabled = true;
        vm.V2GridSize = 8;

        vm.ApplyV2DragDelta(second, 5, -5);
        vm.CompleteV2Gesture();

        Assert.All(vm.V2Document.Nodes, node =>
            Assert.Equal((8d, -8d), (node.Anchors[0].OffsetX, node.Anchors[0].OffsetY)));
        vm.NudgeV2Selection(1, 0);
        vm.NudgeV2Selection(1, 0);
        vm.CompleteV2Nudge();
        Assert.All(vm.V2Document.Nodes, node => Assert.Equal(10, node.Anchors[0].OffsetX));
        vm.NudgeV2Selection(10, 0);
        vm.CompleteV2Nudge();
        Assert.All(vm.V2Document.Nodes, node => Assert.Equal(20, node.Anchors[0].OffsetX));
        vm.V2SnapEnabled = false;
        vm.ApplyV2DragDelta(second, 3, 0);
        vm.CompleteV2Gesture();
        Assert.All(vm.V2Document.Nodes, node => Assert.Equal(23, node.Anchors[0].OffsetX));

        vm.V2GroupNameDraft = "Pair";
        vm.CreateV2Group();
        Assert.Equal(2, vm.V2Document.Editor!.Groups.Single().Members.Count);
        Assert.Equal("Pair", vm.V2Groups.Single().Name);
        vm.ToggleSelectedV2GroupLock();
        var locked = vm.V2Document.Nodes.Select(node => node.Anchors[0]).ToArray();
        vm.MoveV2SelectionBy(10, 0);
        Assert.Equal(locked, vm.V2Document.Nodes.Select(node => node.Anchors[0]).ToArray());

        vm.ToggleSelectedV2GroupLock();
        vm.SelectV2(first, false);
        var oldWidth = vm.SelectedV2Node!.AuthoredProperties.Frame!.Width;
        vm.ApplyV2ResizeDelta(first, V2ResizeHandle.Right, 8, 0);
        vm.CompleteV2Gesture();
        Assert.Equal(oldWidth + 8, vm.SelectedV2Node!.AuthoredProperties.Frame!.Width);
        vm.UndoV2();
        Assert.Equal(oldWidth, vm.SelectedV2Node!.AuthoredProperties.Frame!.Width);
    }

    [Fact]
    public void OpeningSemanticallyInvalidV2IsRejectedWithDiagnosticAndKeepsCurrentProject()
    {
        var path = Path.Combine(_directory, "invalid.fforge.json");
        var invalid = UiDocumentFactory.Create("Root", "Host") with { CompositionRoots = [] };
        File.WriteAllText(path, UiDocumentCodec.Serialize(invalid));
        var vm = ViewModel();
        vm.NewProject();
        var before = vm.Project;

        vm.OpenFromFile(path);

        Assert.True(vm.IsV1Project);
        Assert.Same(before, vm.Project);
        Assert.Contains("FFV2-ROOT-001", vm.Status, StringComparison.Ordinal);
    }

    private MainWindowViewModel ViewModel() => new(
        Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json"),
        stockTemplates: new PassThroughStockTemplates());

    private MainWindowViewModel TemplateViewModel()
    {
        var settings = Path.Combine(_directory, $"settings-template-{Guid.NewGuid():N}.json");
        new AssetSettingsStore(settings).SaveConfiguration(new FrameForgeLocalSettings([], "synthetic-client"));
        return new MainWindowViewModel(settings,
            new V2TemplateDesignerTests.ValidProvider(_directory),
            new V2TemplateDesignerTests.PassThroughStockTemplates(),
            v2TemplateLoader: new V2TemplateDesignerTests.FakeLoader(V2TemplateDesignerTests.Registry()));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

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
        public double MeasureText(StockFontStyle style, string text) => text.Length * style.Size;
    }
}
