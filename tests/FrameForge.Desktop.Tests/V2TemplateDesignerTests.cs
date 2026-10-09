using FrameForge.Core.Semantics.V2;
using FrameForge.Core.Templates;
using FrameForge.Core.Models;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Preview;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class V2TemplateDesignerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"frameforge-v2-template-ui-{Guid.NewGuid():N}");

    public V2TemplateDesignerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task SelectorAssignsThroughSemanticEditorAndClearOverridesRevealsEffectiveDimensions()
    {
        var loader = new FakeLoader(Registry());
        var vm = ViewModel(loader);
        vm.NewV2Project();
        await vm.V2TemplateRegistryLoadingTask;
        vm.AddV2Control(UiNodeKind.Button);

        Assert.Equal(4, vm.V2TemplateOptions.Count);
        vm.V2TemplateDraft = vm.V2TemplateOptions.Single(item => item.Identity == "UIPanelButtonTemplate");
        vm.V2TextDraft = "Okay";
        vm.ApplyV2Inspector();

        Assert.Equal("UIPanelButtonTemplate", vm.SelectedV2Node!.BlizzardTemplate);
        Assert.Equal("Okay", vm.SelectedV2Node.AuthoredProperties.Button!.Text);
        Assert.Equal(120, vm.SelectedV2Node.AuthoredProperties.Frame!.Width);

        vm.ClearV2TemplateOverrides();

        Assert.Null(vm.SelectedV2Node!.AuthoredProperties.Frame!.Width);
        Assert.Null(vm.SelectedV2Node.AuthoredProperties.Frame.Height);
        Assert.Equal(88, vm.Project.Find(vm.SelectedV2Node.Id.Value)!.Width);
        Assert.Contains("TemplateDeclared", vm.V2DimensionSummary);

        var export = vm.ExportV2(Path.Combine(_directory, "export"));
        Assert.NotNull(export);
        Assert.True(export.Success, export.Summary);
        Assert.Contains("inherits=\"UIPanelButtonTemplate\"", export.Plan.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewStateIsPresentationOnlyAndRegistryLoadsOncePerConfiguration()
    {
        var loader = new FakeLoader(Registry());
        var vm = ViewModel(loader);
        vm.NewV2Project();
        await vm.V2TemplateRegistryLoadingTask;
        vm.AddV2Control(UiNodeKind.Button);
        var before = vm.V2Document;

        vm.V2PreviewStateDraft = vm.V2PreviewStates.Single(item => item.State == PreviewButtonState.Pushed);

        Assert.Same(before, vm.V2Document);
        Assert.Equal(PreviewButtonState.Pushed, vm.V2PreviewButtonState);
        Assert.Equal(1, loader.LoadCount);
        Assert.Same(vm.V2TemplateRegistry, vm.V2TemplateRegistry);
    }

    [Fact]
    public async Task NonButtonSelectionOffersOnlyNone()
    {
        var vm = ViewModel(new FakeLoader(Registry()));
        vm.NewV2Project();
        await vm.V2TemplateRegistryLoadingTask;
        vm.AddV2Control(UiNodeKind.Frame);

        Assert.Single(vm.V2TemplateOptions);
        Assert.Null(vm.V2TemplateOptions[0].Identity);
    }

    private MainWindowViewModel ViewModel(FakeLoader loader)
    {
        var settings = Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json");
        new AssetSettingsStore(settings).SaveConfiguration(new FrameForgeLocalSettings([], "synthetic-client"));
        return new MainWindowViewModel(settings, new ValidProvider(_directory),
            new PassThroughStockTemplates(), v2TemplateLoader: loader);
    }

    private static BlizzardTemplateRegistry Registry() => BlizzardTemplateParser.Parse(
    [
        new BlizzardTemplateXmlSource("Interface\\FrameXML\\Synthetic.xml", """
            <Ui>
              <Font name="GameFontNormal" font="Fonts\FRIZQT__.TTF" virtual="true"><FontHeight><AbsValue val="12"/></FontHeight></Font>
              <Button name="UIPanelButtonTemplate" virtual="true"><Size x="88" y="24"/><NormalFont style="GameFontNormal"/><NormalTexture file="Interface\Buttons\Up"/><PushedTexture file="Interface\Buttons\Down"/><DisabledTexture file="Interface\Buttons\Disabled"/><HighlightTexture file="Interface\Buttons\Highlight"/></Button>
              <Button name="GameMenuButtonTemplate" inherits="UIPanelButtonTemplate" virtual="true"><Size x="144" y="36"/></Button>
              <Button name="CharacterFrameTabButtonTemplate" inherits="UIPanelButtonTemplate" virtual="true"><Size x="10" y="32"/><Scripts><OnShow>PanelTemplates_TabResize(self, 0)</OnShow></Scripts></Button>
            </Ui>
            """, "synthetic.MPQ", "3.3.5a / 12340", "enUS"),
    ],
    [
        new BlizzardAssetDependency("Interface\\Buttons\\Up", true),
        new BlizzardAssetDependency("Interface\\Buttons\\Down", true),
        new BlizzardAssetDependency("Interface\\Buttons\\Disabled", true),
        new BlizzardAssetDependency("Interface\\Buttons\\Highlight", true),
        new BlizzardAssetDependency("Fonts\\FRIZQT__.TTF", true),
    ]);

    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class FakeLoader(BlizzardTemplateRegistry registry) : IBuild12340TemplateRegistryLoader
    {
        public int LoadCount { get; private set; }
        public BlizzardTemplateRegistry Load(WowClientValidation client)
        {
            Thread.Sleep(50); // Exercise the real off-UI-thread lifecycle instead of completing inline.
            LoadCount++;
            return registry;
        }
    }

    private sealed class ValidProvider(string cacheRoot) : IWoWClientAssetProvider
    {
        public string CacheRoot => cacheRoot;
        public WowClientValidation ValidateClient(string? clientPath) => new(WowClientValidationStatus.Valid,
            clientPath, new WowClientBuild(3, 3, 5, 12340), "enUS", [], "valid");
        public AssetMaterializationResult Materialize(string reference, WowClientValidation client) =>
            new(false, reference, null, null, false, "not used");
        public StockAssetProvenance? GetProvenance(string physicalPath) => null;
        public void ClearCache() { }
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
        public double MeasureText(StockFontStyle style, string text) => text.Length * style.Size;
    }
}
