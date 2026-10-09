using FrameForge.Core.Models;
using FrameForge.Core.Geometry;
using FrameForge.Core.Semantics.V2;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using System.Xml.Linq;
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
        Assert.True(vm.Layout.Frames.ContainsKey(root.Id.Value));
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
        var before = vm.Layout.Frames[child.Id.Value].Rect;

        vm.V2OwnerDraft = vm.V2OwnerOptions.Single(option => option.Owner == OwnerReference.Node(second.Id));
        vm.ApplyV2Inspector();

        Assert.Equal(before, vm.Layout.Frames[child.Id.Value].Rect);
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

        var originalChildRect = vm.Layout.Frames[textId.Value].Rect!.Value;
        var viewport = Viewport.Identity;
        var origin = new CanvasOrigin(0, 0);
        var canvasPoint = viewport.ModelToCanvas(
            new ModelPoint((originalChildRect.Left + originalChildRect.Right) / 2,
                (originalChildRect.Top + originalChildRect.Bottom) / 2), origin);
        var hitCandidates = HitTester.CandidatesAt(vm.Project, vm.Layout, viewport, origin,
            VisibilityFilter.ALL, canvasPoint.X, canvasPoint.Y);
        Assert.True(hitCandidates[0] == textId.Value,
            $"Expected FontString first. Candidates: {string.Join(", ", hitCandidates)}; " +
            $"frame={frameId.Value}, status={statusId.Value}, text={textId.Value}");
        Assert.Contains(statusId.Value, hitCandidates);

        var originalChildOffset = vm.SelectedV2Node!.Anchors[0];
        vm.DragFrame(textId.Value, 13, -7);
        var independentlyMoved = vm.Layout.Frames[textId.Value].Rect!.Value;
        Assert.Equal(originalChildRect.Left + 13, independentlyMoved.Left);
        Assert.Equal(originalChildRect.Top - 7, independentlyMoved.Top);
        Assert.Equal(OwnerReference.Node(statusId), vm.SelectedV2Node!.Owner);
        Assert.NotEqual(originalChildOffset.OffsetX, vm.SelectedV2Node.Anchors[0].OffsetX);

        var childAnchorBeforeParentMove = vm.SelectedV2Node.Anchors[0];
        vm.Select(statusId.Value);
        var statusBefore = vm.Layout.Frames[statusId.Value].Rect!.Value;
        var childBefore = vm.Layout.Frames[textId.Value].Rect!.Value;
        vm.DragFrame(statusId.Value, 20, 11);
        var statusAfter = vm.Layout.Frames[statusId.Value].Rect!.Value;
        var childAfter = vm.Layout.Frames[textId.Value].Rect!.Value;
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
