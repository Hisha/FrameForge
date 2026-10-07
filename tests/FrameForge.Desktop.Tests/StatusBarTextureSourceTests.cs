using FrameForge.Core;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using Xunit;

namespace FrameForge.Desktop.Tests;

/// <summary>
/// The STATUS BAR BAR TEXTURE has two portable sources: project-owned artwork (persisted
/// <c>assets/...</c>, committed with the project) and stock WoW client art (a logical
/// <c>Interface/...</c> reference resolved against the configured build-12340 client / managed
/// cache). Neither may ever persist a machine path, and a missing or unvalidated client must be a
/// graceful diagnostic, never a crash or a silently-persisted fallback.
/// </summary>
public sealed class StatusBarTextureSourceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"frameforge-bar-source-{Guid.NewGuid():N}");

    public StatusBarTextureSourceTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void A_project_image_imported_as_a_bar_texture_is_portable_and_round_trips()
    {
        var projectDirectory = Path.Combine(_temp, "project");
        Directory.CreateDirectory(projectDirectory);
        var projectFile = Path.Combine(projectDirectory, "Hunt.fforge.json");
        var sourceDirectory = Path.Combine(_temp, "outside");
        Directory.CreateDirectory(sourceDirectory);
        var sourceTga = Path.Combine(sourceDirectory, "custom_bar.tga");
        File.WriteAllBytes(sourceTga, Tga(1, 1, true, (10, 20, 30, 255)));

        var vm = NewViewModel(Path.Combine(_temp, "settings.json"));
        vm.Load(StatusBarProject(), projectFile, "Loaded.");
        vm.OnCanvasSelectionRequested("HuntBar");

        Assert.True(vm.SetSelectedStatusBarTextureFromFile(sourceTga, importExternal: true));

        Assert.Equal("assets/custom_bar.tga", vm.Project.Find("HuntBar")!.Visual!.StatusBar!.BarTexture);
        var resolved = vm.Assets.Resolve("assets/custom_bar.tga");
        Assert.True(resolved.CanRender);
        Assert.Equal(AssetSourceKind.ProjectRelative, resolved.SourceKind);
        Assert.Equal(TextureFileFormat.Tga, resolved.Format);
        Assert.True(File.Exists(Path.Combine(projectDirectory, "assets", "custom_bar.tga")));

        Assert.True(vm.SaveToFile(projectFile));
        var json = File.ReadAllText(projectFile);
        Assert.Contains("assets/custom_bar.tga", json, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceDirectory, json, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceTga, json, StringComparison.Ordinal);

        var reloaded = NewViewModel(Path.Combine(_temp, "settings-2.json"));
        reloaded.OpenFromFile(projectFile);
        Assert.Equal("assets/custom_bar.tga", reloaded.Project.Find("HuntBar")!.Visual!.StatusBar!.BarTexture);
        Assert.True(reloaded.Assets.Resolve("assets/custom_bar.tga").CanRender);
    }

    [Fact]
    public void A_wow_client_reference_is_persisted_logical_and_resolves_from_the_owned_cache()
    {
        var clientRoot = MakeClient("enUS");
        var archive = Path.Combine(clientRoot, "Data", "enUS", "locale-enUS.MPQ");
        var provider = Provider(out var reader);
        reader.Add(archive, @"Interface\TargetingFrame\UI-StatusBar.tga", Tga(4, 1, true, (1, 2, 3, 255)));
        var settingsPath = Path.Combine(_temp, "wow-settings.json");
        new AssetSettingsStore(settingsPath).SaveConfiguration(new FrameForgeLocalSettings(Roots: [], WowClientPath: clientRoot));

        var vm = NewViewModel(settingsPath, provider);
        var projectFile = Path.Combine(_temp, "Hunt.fforge.json");
        vm.Load(StatusBarProject(), projectFile, "Loaded.");
        vm.OnCanvasSelectionRequested("HuntBar");

        Assert.True(vm.SetSelectedStatusBarTextureFromWow(@"Interface\TargetingFrame\UI-StatusBar"));

        var reference = vm.Project.Find("HuntBar")!.Visual!.StatusBar!.BarTexture;
        Assert.Equal("Interface/TargetingFrame/UI-StatusBar", reference);
        Assert.NotNull(reference);

        var validation = provider.ValidateClient(clientRoot);
        var materialized = provider.Materialize(reference, validation);
        Assert.True(materialized.Success, materialized.Message);
        vm.Assets.Refresh();
        var resolved = vm.Assets.Resolve(reference);
        Assert.True(resolved.CanRender, $"{resolved.Status}: {resolved.Diagnostic?.Message}");
        Assert.Contains(provider.CacheRoot, resolved.PhysicalPath!, StringComparison.Ordinal);

        Assert.True(vm.SaveToFile(projectFile));
        var json = File.ReadAllText(projectFile);
        Assert.Contains("Interface/TargetingFrame/UI-StatusBar", json, StringComparison.Ordinal);
        Assert.DoesNotContain(clientRoot, json, StringComparison.Ordinal);
        Assert.DoesNotContain(provider.CacheRoot, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/absolute/path.tga")]
    [InlineData("C:\\Windows\\x.tga")]
    [InlineData("Interface/../escape.tga")]
    [InlineData("Interface//doubled.tga")]
    [InlineData("   ")]
    public void Malformed_or_absolute_references_are_rejected_and_nothing_is_persisted(string badReference)
    {
        var vm = NewViewModel(Path.Combine(_temp, "no-client-settings.json"));
        vm.Load(StatusBarProject(), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");
        vm.OnCanvasSelectionRequested("HuntBar");

        Assert.False(vm.SetSelectedStatusBarTextureFromWow(badReference));
        Assert.NotEmpty(vm.Status);
        Assert.Null(vm.Project.Find("HuntBar")!.Visual!.StatusBar!.BarTexture);
    }

    [Fact]
    public void Without_a_client_the_logical_reference_is_saved_gracefully_and_reported_unresolved()
    {
        // An injected empty provider isolates this from any real machine cache; the reference must
        // stay unresolved (reportable as a missing stock asset), never silently rendered.
        var vm = NewViewModel(Path.Combine(_temp, "no-client-settings.json"), Provider(out _));
        vm.Load(StatusBarProject(), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");
        vm.OnCanvasSelectionRequested("HuntBar");

        Assert.True(vm.SetSelectedStatusBarTextureFromWow("Interface/TargetingFrame/UI-StatusBar"));

        Assert.Equal("Interface/TargetingFrame/UI-StatusBar", vm.Project.Find("HuntBar")!.Visual!.StatusBar!.BarTexture);
        Assert.False(vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar").CanRender);
        Assert.Equal("Interface/TargetingFrame/UI-StatusBar", vm.StatusBarTextureDraft);
    }

    [Fact]
    public void Stock_choices_blend_curated_project_and_already_extracted_references_but_never_machine_paths()
    {
        var clientRoot = MakeClient("enUS");
        var archive = Path.Combine(clientRoot, "Data", "enUS", "locale-enUS.MPQ");
        var provider = Provider(out var reader);
        reader.Add(archive, @"Interface\FriendsFrame\BG-StatusBar.tga", Tga(1, 1, true, (5, 5, 5, 255)));
        var settingsPath = Path.Combine(_temp, "wow-settings.json");
        new AssetSettingsStore(settingsPath).SaveConfiguration(new FrameForgeLocalSettings(Roots: [], WowClientPath: clientRoot));

        var vm = NewViewModel(settingsPath, provider);
        vm.Load(StatusBarProject("Interface/LFGFrame/Atlas"), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");

        var validation = provider.ValidateClient(clientRoot);
        Assert.True(provider.Materialize(@"Interface\FriendsFrame\BG-StatusBar", validation).Success);

        var choices = vm.StatusBarStockTextureChoices();

        Assert.Contains(choices, entry => entry.InterfacePath == "Interface/TargetingFrame/UI-StatusBar");
        Assert.Contains(choices, entry => entry.InterfacePath == "Interface/LFGFrame/Atlas");
        Assert.Contains(choices, entry => entry.InterfacePath == "Interface/FriendsFrame/BG-StatusBar.tga"
            && entry.Category == "Already extracted");
        Assert.All(choices, entry =>
        {
            Assert.True(WoWClientAssetProvider.TryNormalizeInterfacePath(entry.InterfacePath, out _, out _),
                $"Expected a logical Interface reference: {entry.InterfacePath}");
        });
        Assert.DoesNotContain(choices, entry => entry.InterfacePath.Contains(clientRoot, StringComparison.Ordinal));
        Assert.DoesNotContain(choices, entry => entry.InterfacePath.Contains(provider.CacheRoot, StringComparison.Ordinal));
    }

    private static MainWindowViewModel NewViewModel(string settingsPath) =>
        new(settingsPath, stockTemplates: new PassThroughStockTemplates());

    private static MainWindowViewModel NewViewModel(string settingsPath, WoWClientAssetProvider wowAssets) =>
        new(settingsPath, wowAssets: wowAssets, stockTemplates: new PassThroughStockTemplates());

    private static WoWClientAssetProvider Provider(out FakeArchiveReader reader)
    {
        reader = new FakeArchiveReader();
        return new WoWClientAssetProvider(
            Path.Combine(Path.GetTempPath(), $"frameforge-bar-provider-{Guid.NewGuid():N}"),
            reader, new FakeBuildReader(new WowClientBuild(3, 3, 5, 12340)));
    }

    private static Project StatusBarProject(string? barTexture = null) =>
        ProjectFactory.Create("bar-source", new[]
        {
            new FrameDef
            {
                Name = "HuntBar",
                Kind = FrameKind.STATUSBAR,
                Width = 120,
                Height = 28,
                Visual = new FrameVisual { StatusBar = new StatusBarVisual(0, 100, 50, barTexture) },
            },
        }) with
        {
            Editor = new EditorMetadata
            {
                DesignObjects = [new DesignObjectMetadata { FrameName = "HuntBar", DisplayName = "Hunt Bar" }],
                DesignOrder = ["HuntBar"],
            },
        };

    private string MakeClient(string locale)
    {
        var root = Path.Combine(_temp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data", locale));
        Touch(Path.Combine(root, "Wow.exe"));
        Touch(Path.Combine(root, "Data", "common.MPQ"));
        Touch(Path.Combine(root, "Data", locale, $"locale-{locale}.MPQ"));
        return root;
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
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

    private static byte[] Tga(int width, int height, bool topOrigin, params (byte B, byte G, byte R, byte A)[] pixels)
    {
        var bytes = new byte[18 + width * height * 4];
        bytes[2] = 2;
        bytes[12] = (byte)width;
        bytes[13] = (byte)(width >> 8);
        bytes[14] = (byte)height;
        bytes[15] = (byte)(height >> 8);
        bytes[16] = 32;
        bytes[17] = (byte)(8 | (topOrigin ? 0x20 : 0));
        for (var i = 0; i < pixels.Length; i++)
        {
            bytes[18 + i * 4] = pixels[i].B;
            bytes[19 + i * 4] = pixels[i].G;
            bytes[20 + i * 4] = pixels[i].R;
            bytes[21 + i * 4] = pixels[i].A;
        }
        return bytes;
    }

    private sealed class FakeBuildReader(WowClientBuild build) : IWoWClientBuildReader
    {
        public bool TryRead(string executablePath, out WowClientBuild? result, out string diagnostic)
        {
            result = build;
            diagnostic = $"Detected {build}.";
            return true;
        }
    }

    private sealed class FakeArchiveReader : IWoWArchiveReader
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        public int ReadCount { get; private set; }
        public void Add(string archive, string entry, byte[] bytes) => _files[$"{archive}|{entry}"] = bytes;
        public bool TryRead(string archivePath, string entryPath, out byte[]? bytes)
        {
            ReadCount++;
            if (_files.TryGetValue($"{archivePath}|{entryPath}", out var found))
            {
                bytes = [.. found];
                return true;
            }
            bytes = null;
            return false;
        }
    }
}