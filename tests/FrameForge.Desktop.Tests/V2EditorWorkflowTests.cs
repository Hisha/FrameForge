using FrameForge.Core.Models;
using FrameForge.Core.Semantics.V2;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
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
