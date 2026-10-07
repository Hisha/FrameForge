using System.Text.Json;
using System.Text.Json.Nodes;
using FrameForge.Core;
using FrameForge.Core.Models;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Templates;
using FrameForge.Desktop.ViewModels;
using Xunit;

namespace FrameForge.Desktop.Tests;

/// <summary>
/// ARCHITECTURE: the configured WoW client (and its managed cache) is THE authoritative source for
/// logical stock references, project-owned artwork resolves relative to the directory containing
/// the <c>.fforge.json</c>, and machine-local asset roots are retired from the normal design path.
/// Normal rendering auto-populates the cache from the configured client instead of requiring a
/// manual "Resolve Missing Assets" step, and no project may ever persist a machine path.
/// </summary>
public sealed class AssetResolutionArchitectureTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"frameforge-asset-architecture-{Guid.NewGuid():N}");

    public AssetResolutionArchitectureTests() => Directory.CreateDirectory(_temp);

    // ---------- Project-owned artwork: zero roots, project-relative, portable ----------

    [Fact]
    public void Project_artwork_beside_the_json_resolves_with_zero_asset_roots()
    {
        var projectDirectory = Path.Combine(_temp, "project");
        Directory.CreateDirectory(projectDirectory);
        WriteTga(Path.Combine(projectDirectory, "custom_bar.tga"));

        var vm = NewViewModel(Path.Combine(_temp, "s1.json"));
        vm.Load(StatusBarProject("custom_bar.tga"), Path.Combine(projectDirectory, "Hunt.fforge.json"), "Loaded.");

        Assert.Empty(vm.AssetRoots);
        var resolved = vm.Assets.Resolve("custom_bar.tga");
        Assert.True(resolved.CanRender, $"{resolved.Status}: {resolved.Diagnostic?.Message}");
        Assert.Equal(AssetSourceKind.ProjectRelative, resolved.SourceKind);
        Assert.Equal(TextureFileFormat.Tga, resolved.Format);
    }

    [Fact]
    public void Importing_into_a_project_directory_named_assets_never_produces_assets_assets()
    {
        // A legacy checkout may already live inside .../assets; importing must yield a clean result
        // beside the project (no assets/assets/foo.tga) with a portable reference of just the name.
        var projectDirectory = Path.Combine(_temp, "assets");
        Directory.CreateDirectory(projectDirectory);
        var source = Path.Combine(_temp, "outside", "custom_bar.tga");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        WriteTga(source);

        var vm = NewViewModel(Path.Combine(_temp, "s2.json"));
        vm.Load(StatusBarProject(), Path.Combine(projectDirectory, "Hunt.fforge.json"), "Loaded.");
        vm.OnCanvasSelectionRequested("HuntBar");
        Assert.True(vm.SetSelectedStatusBarTextureFromFile(source, importExternal: true));

        Assert.Equal("custom_bar.tga", vm.Project.Find("HuntBar")!.Visual!.StatusBar!.BarTexture);
        Assert.True(File.Exists(Path.Combine(projectDirectory, "custom_bar.tga")));
        Assert.False(File.Exists(Path.Combine(projectDirectory, "assets", "custom_bar.tga")));

        Assert.True(vm.SaveToFile(Path.Combine(projectDirectory, "Hunt.fforge.json")));
        var json = File.ReadAllText(Path.Combine(projectDirectory, "Hunt.fforge.json"));
        Assert.Contains("custom_bar.tga", json, StringComparison.Ordinal);
        Assert.DoesNotContain("assets/assets", json, StringComparison.Ordinal);
        Assert.True(vm.Assets.Resolve("custom_bar.tga").CanRender);
    }

    [Fact]
    public void Relocating_a_project_directory_keeps_project_artwork_resolvable()
    {
        var original = Path.Combine(_temp, "phase-a");
        Directory.CreateDirectory(original);
        WriteTga(Path.Combine(original, "custom_bar.tga"));
        var projectFile = Path.Combine(original, "Hunt.fforge.json");

        var vm = NewViewModel(Path.Combine(_temp, "s3.json"));
        vm.Load(StatusBarProject("custom_bar.tga"), projectFile, "Loaded.");
        Assert.True(vm.Assets.Resolve("custom_bar.tga").CanRender);
        Assert.True(vm.SaveToFile(projectFile));

        var relocated = Path.Combine(_temp, "phase-b");
        Directory.Move(original, relocated);
        Assert.False(File.Exists(projectFile));

        var reopened = NewViewModel(Path.Combine(_temp, "s4.json"));
        reopened.OpenFromFile(Path.Combine(relocated, "Hunt.fforge.json"));
        Assert.Equal("custom_bar.tga", reopened.Project.Find("HuntBar")!.Visual!.StatusBar!.BarTexture);
        var resolved = reopened.Assets.Resolve("custom_bar.tga");
        Assert.True(resolved.CanRender, $"{resolved.Status}: {resolved.Diagnostic?.Message}");
        Assert.Equal(AssetSourceKind.ProjectRelative, resolved.SourceKind);
    }

    [Fact]
    public void Legacy_roots_never_shadow_project_artwork()
    {
        var projectDirectory = Path.Combine(_temp, "project-dir");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "assets"));
        WriteTga(Path.Combine(projectDirectory, "assets", "custom_bar.tga"));

        var legacy = Path.Combine(_temp, "legacy-root");
        Directory.CreateDirectory(Path.Combine(legacy, "assets"));
        WriteTga(Path.Combine(legacy, "assets", "custom_bar.tga"), red: 9, green: 9, blue: 9);

        using var resolver = new TextureAssetResolver();
        resolver.Configure(Path.Combine(projectDirectory, "something.xml"), [legacy],
            projectPath: Path.Combine(projectDirectory, "Hunt.fforge.json"),
            designReferences: ["assets/custom_bar.tga"]);

        var resolved = resolver.Resolve("assets/custom_bar.tga");
        Assert.True(resolved.CanRender, $"{resolved.Status}: {resolved.Diagnostic?.Message}");
        Assert.Equal(AssetSourceKind.ProjectRelative, resolved.SourceKind);
        Assert.Equal(Path.GetFullPath(Path.Combine(projectDirectory, "assets", "custom_bar.tga")),
            Path.GetFullPath(resolved.PhysicalPath!));
    }

    // ---------- Logical stock sources: cache, then authoritative configured client ----------

    [Fact]
    public void Cached_stock_resolves_without_a_client_or_any_root()
    {
        var provider = Provider(out _);
        WriteTga(Path.Combine(provider.CacheRoot, "Interface", "TargetingFrame", "UI-StatusBar.tga"));

        var vm = NewViewModel(Path.Combine(_temp, "s5.json"), provider);
        vm.Load(StatusBarProject("Interface/TargetingFrame/UI-StatusBar"), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");

        Assert.Empty(vm.AssetRoots);
        var resolved = vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar");
        Assert.True(resolved.CanRender, $"{resolved.Status}: {resolved.Diagnostic?.Message}");
        Assert.Equal(AssetSourceKind.ConfiguredRoot, resolved.SourceKind);
        Assert.StartsWith(Path.GetFullPath(provider.CacheRoot), Path.GetFullPath(resolved.PhysicalPath!), StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_client_auto_materializes_a_never_before_resolved_reference()
    {
        var clientRoot = MakeClient("enUS");
        var archive = Path.Combine(clientRoot, "Data", "enUS", "locale-enUS.MPQ");
        var provider = Provider(out var reader);
        reader.Add(archive, @"Interface\TargetingFrame\UI-StatusBar.tga", Tga());
        var settingsPath = Path.Combine(_temp, "s6.json");
        new AssetSettingsStore(settingsPath).SaveConfiguration(new FrameForgeLocalSettings(Roots: [], WowClientPath: clientRoot));

        var vm = NewViewModel(settingsPath, provider);
        vm.Load(StatusBarProject("Interface/TargetingFrame/UI-StatusBar"), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");

        // A NORMAL rendering resolve - no manual "Resolve Missing Assets" press - must populate the
        // cache and go green. The reference stays logical and no machine path appears anywhere.
        var resolved = vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar");
        Assert.True(resolved.CanRender, $"{resolved.Status}: {resolved.Diagnostic?.Message}");
        Assert.StartsWith(Path.GetFullPath(provider.CacheRoot), Path.GetFullPath(resolved.PhysicalPath!), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(provider.CacheRoot, "Interface", "TargetingFrame", "UI-StatusBar.tga")));
        var firstReads = reader.ReadCount;

        // The second resolve must not extract again: the cache hit path serves it.
        vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar");
        Assert.Equal(firstReads, reader.ReadCount);
    }

    [Fact]
    public void Auto_materialization_applies_only_to_Interface_and_Fonts_logical_references()
    {
        var clientRoot = MakeClient("enUS");
        var archive = Path.Combine(clientRoot, "Data", "enUS", "locale-enUS.MPQ");
        var provider = Provider(out var reader);
        reader.Add(archive, @"assets\custom_bar.tga", Tga()); // would be a wrong place for a design asset
        var settingsPath = Path.Combine(_temp, "s7.json");
        new AssetSettingsStore(settingsPath).SaveConfiguration(new FrameForgeLocalSettings(Roots: [], WowClientPath: clientRoot));

        var projectDirectory = Path.Combine(_temp, "project");
        Directory.CreateDirectory(projectDirectory);
        WriteTga(Path.Combine(projectDirectory, "assets", "custom_bar.tga"));

        var vm = NewViewModel(settingsPath, provider);
        vm.Load(StatusBarProject("assets/custom_bar.tga"), Path.Combine(projectDirectory, "Hunt.fforge.json"), "Loaded.");

        var resolved = vm.Assets.Resolve("assets/custom_bar.tga");
        Assert.True(resolved.CanRender, $"{resolved.Status}: {resolved.Diagnostic?.Message}");
        Assert.Equal(AssetSourceKind.ProjectRelative, resolved.SourceKind);
        Assert.Equal(0, reader.ReadCount); // the client archive was never consulted for project art
    }

    [Fact]
    public void A_valid_client_missing_the_asset_reports_a_diagnostic_instead_of_crashing()
    {
        var clientRoot = MakeClient("enUS");
        var provider = Provider(out var reader); // empty archive index: the entry is absent
        var settingsPath = Path.Combine(_temp, "s8.json");
        new AssetSettingsStore(settingsPath).SaveConfiguration(new FrameForgeLocalSettings(Roots: [], WowClientPath: clientRoot));

        var vm = NewViewModel(settingsPath, provider);
        vm.Load(StatusBarProject("Interface/TargetingFrame/UI-StatusBar"), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");

        var resolved = vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar");
        Assert.False(resolved.CanRender);
        Assert.Equal(AssetResolutionStatus.Missing, resolved.Status);
        Assert.Contains("does not provide it", resolved.Diagnostic.Message, StringComparison.Ordinal);
        var readsAfterFirst = reader.ReadCount;

        var again = vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar");
        Assert.False(again.CanRender);
        Assert.Equal(readsAfterFirst, reader.ReadCount); // failed resolution is cached, no re-extraction
    }

    [Fact]
    public void An_invalid_client_is_a_clean_diagnostic_and_still_never_crashes()
    {
        var brokenClient = Path.Combine(_temp, "broken-client");
        Directory.CreateDirectory(Path.Combine(brokenClient, "Data"));
        var provider = Provider(out _);
        var settingsPath = Path.Combine(_temp, "s9.json");
        new AssetSettingsStore(settingsPath).SaveConfiguration(new FrameForgeLocalSettings(Roots: [], WowClientPath: brokenClient));

        var vm = NewViewModel(settingsPath, provider);
        vm.Load(StatusBarProject("Interface/TargetingFrame/UI-StatusBar"), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");

        var resolved = vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar");
        Assert.False(resolved.CanRender);
        Assert.Equal(AssetResolutionStatus.Missing, resolved.Status);
        Assert.Equal("Interface/TargetingFrame/UI-StatusBar", resolved.Reference);

        // Project-owned artwork still resolves alongside the invalid stock source.
        var projectDirectory = Path.Combine(_temp, "project");
        Directory.CreateDirectory(projectDirectory);
        WriteTga(Path.Combine(projectDirectory, "custom_bar.tga"));
        vm.Load(StatusBarProject("custom_bar.tga"), Path.Combine(projectDirectory, "Hunt.fforge.json"), "Loaded.");
        Assert.True(vm.Assets.Resolve("custom_bar.tga").CanRender);
    }

    [Fact]
    public void Unresolved_logical_references_stay_intact_and_reported()
    {
        var provider = Provider(out _); // empty cache, no machine roots, no client
        var vm = NewViewModel(Path.Combine(_temp, "s10.json"), provider);
        var projectFile = Path.Combine(_temp, "Hunt.fforge.json");
        vm.Load(StatusBarProject("Interface/TargetingFrame/UI-StatusBar"), projectFile, "Loaded.");

        var resolved = vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar");
        Assert.False(resolved.CanRender);
        Assert.Equal("Interface/TargetingFrame/UI-StatusBar", resolved.Reference);
        Assert.Contains("no WoW client is configured", resolved.Diagnostic.Message, StringComparison.Ordinal);

        Assert.True(vm.SaveToFile(projectFile));
        var json = File.ReadAllText(projectFile);
        Assert.Contains("Interface/TargetingFrame/UI-StatusBar", json, StringComparison.Ordinal);

        var reopened = NewViewModel(Path.Combine(_temp, "s11.json"), Provider(out _));
        reopened.OpenFromFile(projectFile);
        Assert.Equal("Interface/TargetingFrame/UI-StatusBar", reopened.Project.Find("HuntBar")!.Visual!.StatusBar!.BarTexture);
    }

    [Fact]
    public void Setting_a_client_later_refreshes_resolution_and_then_survives_being_cleared()
    {
        var clientRoot = MakeClient("enUS");
        var archive = Path.Combine(clientRoot, "Data", "enUS", "locale-enUS.MPQ");
        var provider = Provider(out var reader);
        reader.Add(archive, @"Interface\TargetingFrame\UI-StatusBar.tga", Tga());

        var vm = NewViewModel(Path.Combine(_temp, "s12.json"), provider);
        vm.Load(StatusBarProject("Interface/TargetingFrame/UI-StatusBar"), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");

        Assert.False(vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar").CanRender);

        vm.SetWoWClientPath(clientRoot);
        Assert.True(vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar").CanRender);
        Assert.True(File.Exists(Path.Combine(provider.CacheRoot, "Interface", "TargetingFrame", "UI-StatusBar.tga")));

        // Clearing the client selection keeps the already-populated cache usable.
        vm.ClearWoWClientPath();
        Assert.True(vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar").CanRender);
    }

    [Fact]
    public void Revalidating_a_client_recovers_once_the_install_becomes_valid()
    {
        var clientRoot = Path.Combine(_temp, "growing-client");
        Directory.CreateDirectory(Path.Combine(clientRoot, "Data"));
        var archive = Path.Combine(clientRoot, "Data", "enUS", "locale-enUS.MPQ");
        var provider = Provider(out var reader);

        var vm = NewViewModel(Path.Combine(_temp, "s13.json"), provider);
        vm.Load(StatusBarProject("Interface/TargetingFrame/UI-StatusBar"), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");
        vm.SetWoWClientPath(clientRoot);
        Assert.False(vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar").CanRender);
        Assert.False(string.IsNullOrEmpty(vm.WoWClientStatus));

        // The install appears on disk; Revalidate must refresh the resolver and go green.
        CompleteClient(clientRoot);
        reader.Add(archive, @"Interface\TargetingFrame\UI-StatusBar.tga", Tga());
        vm.RevalidateWoWClient();
        Assert.True(vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar").CanRender,
            vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar").Diagnostic.Message);
    }

    [Fact]
    public void Cache_takes_precedence_over_legacy_roots_for_stock_references_once_populated()
    {
        // A stock reference first resolves from a legacy manual root (cache and client absent);
        // once the configured client populates its cache, the authoritative cache wins.
        var clientRoot = MakeClient("enUS");
        var archive = Path.Combine(clientRoot, "Data", "enUS", "locale-enUS.MPQ");
        var provider = Provider(out var reader);
        reader.Add(archive, @"Interface\TargetingFrame\UI-StatusBar.tga", Tga());

        var legacy = Path.Combine(_temp, "legacy-root");
        Directory.CreateDirectory(Path.Combine(legacy, "Interface", "TargetingFrame"));
        WriteTga(Path.Combine(legacy, "Interface", "TargetingFrame", "UI-StatusBar.tga"), red: 30, green: 30, blue: 30);

        using var resolver = new TextureAssetResolver();
        resolver.Configure(sourcePath: null, [legacy]);
        var legacyResolved = resolver.Resolve("Interface/TargetingFrame/UI-StatusBar");
        Assert.True(legacyResolved.CanRender);
        Assert.StartsWith(Path.GetFullPath(legacy), Path.GetFullPath(legacyResolved.PhysicalPath!), StringComparison.Ordinal);

        // Configure a valid client: normal resolves now prefer the managed cache to the legacy root.
        resolver.ConfigureWowAssetSource(provider, provider.ValidateClient(clientRoot));
        var authoritative = resolver.Resolve("Interface/TargetingFrame/UI-StatusBar");
        Assert.True(authoritative.CanRender);
        Assert.StartsWith(Path.GetFullPath(provider.CacheRoot), Path.GetFullPath(authoritative.PhysicalPath!), StringComparison.Ordinal);
    }

    // ---------- Portability and serialization ----------

    [Fact]
    public void Saved_projects_stay_free_of_machine_paths_and_work_across_machines()
    {
        var clientRootA = MakeClient("enUS");
        var archive = Path.Combine(clientRootA, "Data", "enUS", "locale-enUS.MPQ");
        var provider = Provider(out var reader);
        reader.Add(archive, @"Interface\TargetingFrame\UI-StatusBar.tga", Tga());
        var settingsA = Path.Combine(_temp, "machine-a.json");
        new AssetSettingsStore(settingsA).SaveConfiguration(new FrameForgeLocalSettings(Roots: [], WowClientPath: clientRootA));

        var projectDirectory = Path.Combine(_temp, "shared-hunt");
        Directory.CreateDirectory(projectDirectory);
        WriteTga(Path.Combine(projectDirectory, "custom_bar.tga"));
        var projectFile = Path.Combine(projectDirectory, "Hunt.fforge.json");

        var machineA = NewViewModel(settingsA, provider);
        machineA.Load(StatusBarProject("custom_bar.tga", "Interface/TargetingFrame/UI-StatusBar"),
            projectFile, "Loaded.");
        Assert.True(machineA.Assets.Resolve("custom_bar.tga").CanRender);
        Assert.True(machineA.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar").CanRender);
        Assert.True(machineA.SaveToFile(projectFile));

        var json = File.ReadAllText(projectFile);
        Assert.DoesNotContain(clientRootA, json, StringComparison.Ordinal);
        Assert.DoesNotContain(provider.CacheRoot, json, StringComparison.Ordinal);
        Assert.Contains("custom_bar.tga", json, StringComparison.Ordinal);
        Assert.Contains("Interface/TargetingFrame/UI-StatusBar", json, StringComparison.Ordinal);

        // Machine B: same shared checkout, wholly different client install and no roots.
        var clientRootB = MakeClient("enUS");
        var providerB = Provider(out var readerB);
        readerB.Add(Path.Combine(clientRootB, "Data", "enUS", "locale-enUS.MPQ"), @"Interface\TargetingFrame\UI-StatusBar.tga", Tga());
        var settingsB = Path.Combine(_temp, "machine-b.json");
        new AssetSettingsStore(settingsB).SaveConfiguration(new FrameForgeLocalSettings(Roots: [], WowClientPath: clientRootB));

        var machineB = NewViewModel(settingsB, providerB);
        machineB.OpenFromFile(projectFile);
        Assert.Empty(machineB.AssetRoots);
        Assert.Equal("custom_bar.tga", machineB.Project.Find("HuntBar")!.Visual!.StatusBar!.BarTexture);
        Assert.Equal("Interface/TargetingFrame/UI-StatusBar", machineB.Project.Find("FillBar")!.Visual!.StatusBar!.BarTexture);
        Assert.True(machineB.Assets.Resolve("custom_bar.tga").CanRender);
        Assert.True(machineB.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar").CanRender);
    }

    [Fact]
    public void Legacy_project_with_top_level_assetRoots_metadata_loads_and_never_reexports_it()
    {
        var projectDirectory = Path.Combine(_temp, "legacy-project");
        Directory.CreateDirectory(projectDirectory);
        WriteTga(Path.Combine(projectDirectory, "custom_bar.tga"));
        var projectFile = Path.Combine(projectDirectory, "Hunt.fforge.json");

        var seed = NewViewModel(Path.Combine(_temp, "s14.json"));
        seed.Load(StatusBarProject("custom_bar.tga"), projectFile, "Loaded.");
        var baseJson = seed.SaveToFile(projectFile) ? File.ReadAllText(projectFile) : string.Empty;
        Assert.NotEmpty(baseJson);

        // Old projects carried machine-local settings under a top-level assetRoots key; the codec
        // must tolerate (not reject) that unknown node and it must not become project data.
        var node = JsonNode.Parse(baseJson)!;
        node["assetRoots"] = new JsonArray("/old/machine/first", "/old/machine/second");
        File.WriteAllText(projectFile, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var vm = NewViewModel(Path.Combine(_temp, "s15.json"));
        vm.OpenFromFile(projectFile);
        Assert.Empty(vm.AssetRoots);
        Assert.True(vm.Assets.Resolve("custom_bar.tga").CanRender);

        Assert.True(vm.SaveToFile(projectFile));
        var resaved = File.ReadAllText(projectFile);
        Assert.DoesNotContain("/old/machine", resaved, StringComparison.Ordinal);
        Assert.Null(JsonNode.Parse(resaved)!["assetRoots"]);
    }

    [Fact]
    public void Windows_and_linux_style_separators_in_logical_references_resolve()
    {
        var clientRoot = MakeClient("enUS");
        var provider = Provider(out var reader);
        reader.Add(Path.Combine(clientRoot, "Data", "enUS", "locale-enUS.MPQ"), @"Interface\TargetingFrame\UI-StatusBar.tga", Tga());
        var settingsPath = Path.Combine(_temp, "s16.json");
        new AssetSettingsStore(settingsPath).SaveConfiguration(new FrameForgeLocalSettings(Roots: [], WowClientPath: clientRoot));

        var vm = NewViewModel(settingsPath, provider);
        vm.Load(StatusBarProject(), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");
        vm.OnCanvasSelectionRequested("HuntBar");
        Assert.True(vm.SetSelectedStatusBarTextureFromWow(@"Interface\TargetingFrame\UI-StatusBar"));

        // Persisted as a portable forward-slash reference; resolve accepts either separator.
        Assert.Equal("Interface/TargetingFrame/UI-StatusBar", vm.Project.Find("HuntBar")!.Visual!.StatusBar!.BarTexture);
        Assert.True(vm.Assets.Resolve(@"Interface\TargetingFrame\UI-StatusBar").CanRender);
        Assert.True(vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar").CanRender);
    }

    [Fact]
    public void Malformed_or_rooted_references_are_rejected_before_any_client_read()
    {
        var clientRoot = MakeClient("enUS");
        var provider = Provider(out var reader);
        var settingsPath = Path.Combine(_temp, "s17.json");
        new AssetSettingsStore(settingsPath).SaveConfiguration(new FrameForgeLocalSettings(Roots: [], WowClientPath: clientRoot));

        var vm = NewViewModel(settingsPath, provider);
        vm.Load(StatusBarProject("Interface/TargetingFrame/UI-StatusBar"), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");

        foreach (var reference in new[] { "/absolute/path.tga", "C:\\Windows\\x.tga", "Interface/../escape.tga" })
        {
            var resolved = vm.Assets.Resolve(reference);
            Assert.Equal(AssetResolutionStatus.InvalidPath, resolved.Status);
            Assert.False(resolved.CanRender);
        }
        // An empty/whitespace reference is NoReference, not a path error.
        var empty = vm.Assets.Resolve("   ");
        Assert.Equal(AssetResolutionStatus.NoReference, empty.Status);
        Assert.False(empty.CanRender);
        // Doubled separators collapse into a normal logical reference (a real WoW path), which is
        // simply absent from the client - reported, never thrown.
        var doubled = vm.Assets.Resolve("Interface//doubled.tga");
        Assert.Equal(AssetResolutionStatus.Missing, doubled.Status);
        Assert.False(doubled.CanRender);

        // With an isolated resolver (free of the ViewModel's own stock bookkeeping), invalid
        // references provably never reach the client archives at all.
        var beforeInvalidReads = reader.ReadCount;
        using var isolated = new TextureAssetResolver();
        isolated.ConfigureWowAssetSource(provider, provider.ValidateClient(clientRoot));
        foreach (var reference in new[] { "/absolute/path.tga", "C:\\Windows\\x.tga", "Interface/../escape.tga" })
            Assert.Equal(AssetResolutionStatus.InvalidPath, isolated.Resolve(reference).Status);
        Assert.Equal(beforeInvalidReads, reader.ReadCount);
    }

    // ---------- DESIGN/UX invariants ----------

    [Fact]
    public void A_normal_design_flow_keeps_AssetRoots_empty_while_resolving_stock_art()
    {
        var clientRoot = MakeClient("enUS");
        var provider = Provider(out var reader);
        reader.Add(Path.Combine(clientRoot, "Data", "enUS", "locale-enUS.MPQ"), @"Interface\TargetingFrame\UI-StatusBar.tga", Tga());
        var settingsPath = Path.Combine(_temp, "s18.json");
        new AssetSettingsStore(settingsPath).SaveConfiguration(new FrameForgeLocalSettings(Roots: [], WowClientPath: clientRoot));

        var vm = NewViewModel(settingsPath, provider);
        vm.Load(StatusBarProject("Interface/TargetingFrame/UI-StatusBar"), Path.Combine(_temp, "Hunt.fforge.json"), "Loaded.");

        vm.OnCanvasSelectionRequested("HuntBar");
        var choices = vm.StatusBarStockTextureChoices();

        Assert.Empty(vm.AssetRoots);
        Assert.True(vm.Assets.Resolve("Interface/TargetingFrame/UI-StatusBar").CanRender);
        Assert.Contains(choices, choice => choice.InterfacePath == "Interface/TargetingFrame/UI-StatusBar");
        Assert.DoesNotContain(choices, choice => choice.InterfacePath.Contains(provider.CacheRoot, StringComparison.Ordinal));
    }

    [Fact]
    public void Fonts_namespaces_resolve_as_project_art_and_never_touch_client_archives()
    {
        var clientRoot = MakeClient("enUS");
        var provider = Provider(out var reader);
        var projectDirectory = Path.Combine(_temp, "font-project");
        Directory.CreateDirectory(Path.Combine(projectDirectory, "Fonts"));
        WriteTga(Path.Combine(projectDirectory, "Fonts", "ExtraFont.tga"));

        // Fonts/... texture references are DESIGN-owned (the Blizzard font itself flows through the
        // stock template resolver, not the texture pipeline): they resolve relative to the project
        // and never consult the client archives.
        using var resolver = new TextureAssetResolver();
        resolver.Configure(sourcePath: null, assetRoots: [], projectPath: Path.Combine(projectDirectory, "Hunt.fforge.json"),
            designReferences: ["Fonts/ExtraFont.tga"]);
        var resolved = resolver.Resolve("Fonts/ExtraFont.tga");
        Assert.True(resolved.CanRender, $"{resolved.Status}: {resolved.Diagnostic?.Message}");
        Assert.Equal(AssetSourceKind.ProjectRelative, resolved.SourceKind);
        Assert.Equal(0, reader.ReadCount);

        // An absent Fonts reference is a clean Missing, not a crash or an archive read.
        var absent = resolver.Resolve("Fonts/Font_Locale/Locale.ttf");
        Assert.False(absent.CanRender);
        Assert.Equal(AssetResolutionStatus.Missing, absent.Status);
        Assert.Equal(0, reader.ReadCount);
    }

    // ---------- Helpers ----------

    private static MainWindowViewModel NewViewModel(string settingsPath) =>
        new(settingsPath, stockTemplates: new PassThroughStockTemplates());

    private static MainWindowViewModel NewViewModel(string settingsPath, WoWClientAssetProvider wowAssets) =>
        new(settingsPath, wowAssets: wowAssets, stockTemplates: new PassThroughStockTemplates());

    private static WoWClientAssetProvider Provider(out FakeArchiveReader reader)
    {
        reader = new FakeArchiveReader();
        return new WoWClientAssetProvider(
            Path.Combine(Path.GetTempPath(), $"frameforge-asset-arch-provider-{Guid.NewGuid():N}"),
            reader, new FakeBuildReader(new WowClientBuild(3, 3, 5, 12340)));
    }

    private static Project StatusBarProject(string barTexture = "assets/custom_bar.tga", string? secondTexture = null)
    {
        var frames = new List<FrameDef>
        {
            new()
            {
                Name = "HuntBar",
                Kind = FrameKind.STATUSBAR,
                Width = 120,
                Height = 28,
                Visual = new FrameVisual { StatusBar = new StatusBarVisual(0, 100, 50, barTexture) },
            },
        };
        if (secondTexture is not null)
        {
            frames.Add(new FrameDef
            {
                Name = "FillBar",
                Kind = FrameKind.STATUSBAR,
                Width = 120,
                Height = 28,
                Visual = new FrameVisual { StatusBar = new StatusBarVisual(0, 100, 50, secondTexture) },
            });
        }
        return ProjectFactory.Create("asset-architecture", frames) with
        {
            Editor = new EditorMetadata
            {
                DesignObjects = frames.Select(frame =>
                    new DesignObjectMetadata { FrameName = frame.Name, DisplayName = frame.Name }).ToArray(),
                DesignOrder = frames.Select(frame => frame.Name).ToArray(),
            },
        };
    }

    private string MakeClient(string locale)
    {
        var root = Path.Combine(_temp, Guid.NewGuid().ToString("N"));
        CompleteClient(root, locale);
        return root;
    }

    private static void CompleteClient(string root, string locale = "enUS")
    {
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        Touch(Path.Combine(root, "Wow.exe"));
        Touch(Path.Combine(root, "Data", "common.MPQ"));
        Directory.CreateDirectory(Path.Combine(root, "Data", locale));
        Touch(Path.Combine(root, "Data", locale, $"locale-{locale}.MPQ"));
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
    }

    private static void WriteTga(string path, int red = 200, int green = 40, int blue = 40)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Tga(red, green, blue));
    }

    /// <summary>A 2x2 uncompressed 32-bit TGA with a consistent fill color.</summary>
    private static byte[] Tga(int red = 200, int green = 40, int blue = 40)
    {
        var width = 2;
        var height = 2;
        var bytes = new byte[18 + width * height * 4];
        bytes[2] = 2;
        bytes[12] = (byte)width;
        bytes[13] = (byte)(width >> 8);
        bytes[14] = (byte)height;
        bytes[15] = (byte)(height >> 8);
        bytes[16] = 32;
        bytes[17] = 0x08 | 0x20;
        for (var i = 0; i < width * height; i++)
        {
            bytes[18 + i * 4] = (byte)blue;
            bytes[19 + i * 4] = (byte)green;
            bytes[20 + i * 4] = (byte)red;
            bytes[21 + i * 4] = 255;
        }
        return bytes;
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