using System.Security.Cryptography;
using FrameForge.Core.Import;
using FrameForge.Core.Models;
using FrameForge.Desktop.Assets;
using FrameForge.Desktop.Templates;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class WoWClientAssetProviderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"frameforge-wow-client-{Guid.NewGuid():N}");

    public WoWClientAssetProviderTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void Validation_requires_build_12340_and_one_unambiguous_locale()
    {
        var client = MakeClient("enUS");
        var valid = Provider(build: new(3, 3, 5, 12340)).ValidateClient(client);
        Assert.True(valid.IsValid);
        Assert.Equal("enUS", valid.Locale);
        Assert.Equal(12340, valid.Build!.Build);

        var unsupported = Provider(build: new(3, 3, 5, 12341)).ValidateClient(client);
        Assert.Equal(WowClientValidationStatus.UnsupportedBuild, unsupported.Status);

        MakeLocale(client, "deDE");
        var ambiguous = Provider(build: new(3, 3, 5, 12340)).ValidateClient(client);
        Assert.Equal(WowClientValidationStatus.AmbiguousLocale, ambiguous.Status);
        Assert.Contains("deDE", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("enUS", ambiguous.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_rejects_arbitrary_directory_and_missing_client_is_graceful()
    {
        var provider = Provider(build: new(3, 3, 5, 12340));
        Assert.Equal(WowClientValidationStatus.NotConfigured, provider.ValidateClient(null).Status);
        Assert.Equal(WowClientValidationStatus.Invalid, provider.ValidateClient(_temp).Status);
    }

    [Fact]
    public void Portable_build_reader_parses_the_cross_platform_PE_string_resource()
    {
        var path = Path.Combine(_temp, "Wow.exe");
        File.WriteAllBytes(path, [.. new byte[24], .. System.Text.Encoding.Unicode.GetBytes("FileVersion\0\0"),
            .. System.Text.Encoding.Unicode.GetBytes("3, 3, 5, 12340\0")]);
        var reader = new PortableWowBuildReader();
        Assert.True(reader.TryRead(path, out var build, out _));
        Assert.Equal(new WowClientBuild(3, 3, 5, 12340), build);
    }

    [Fact]
    public void Archive_precedence_is_locale_patches_then_global_patches_then_bases()
    {
        var client = MakeClient("enUS", "patch.MPQ", "patch-2.MPQ", "patch-3.MPQ");
        Touch(Path.Combine(client, "Data", "enUS", "patch-enUS.MPQ"));
        Touch(Path.Combine(client, "Data", "enUS", "patch-enUS-2.MPQ"));
        Touch(Path.Combine(client, "Data", "enUS", "patch-enUS-3.MPQ"));
        var archives = WoWClientAssetProvider.DiscoverArchives(client, "enUS");
        var descending = archives.OrderByDescending(archive => archive.Priority).Select(archive => archive.RelativePath).ToArray();

        Assert.Equal("Data/enUS/patch-enUS-3.MPQ", descending[0]);
        Assert.Equal("Data/enUS/patch-enUS-2.MPQ", descending[1]);
        Assert.True(Array.IndexOf(descending, "Data/patch-3.MPQ") < Array.IndexOf(descending, "Data/enUS/locale-enUS.MPQ"));
    }

    [Fact]
    public void Materialization_uses_effective_precedence_extension_omission_and_case_insensitive_lookup()
    {
        var client = MakeClient("enUS", "patch-3.MPQ");
        Touch(Path.Combine(client, "Data", "enUS", "patch-enUS-2.MPQ"));
        Touch(Path.Combine(client, "Data", "enUS", "patch-enUS-3.MPQ"));
        var fake = new FakeArchiveReader();
        fake.Add(Path.Combine(client, "Data", "patch-3.MPQ"), @"Interface\LFGFrame\Atlas.blp", [3]);
        fake.Add(Path.Combine(client, "Data", "enUS", "patch-enUS-2.MPQ"), @"INTERFACE\LFGFRAME\ATLAS.BLP", [2]);
        fake.Add(Path.Combine(client, "Data", "enUS", "patch-enUS-3.MPQ"), @"Interface\LFGFrame\Atlas.blp", [1]);
        var provider = Provider(fake, new(3, 3, 5, 12340));
        var validation = provider.ValidateClient(client);

        var result = provider.Materialize(@"Interface\LFGFrame\Atlas", validation);

        Assert.True(result.Success);
        Assert.False(result.CacheHit);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(result.CachePath!));
        Assert.Equal("Data/enUS/patch-enUS-3.MPQ", result.Provenance!.ArchivePath);
    }

    [Theory]
    [InlineData("../outside.blp")]
    [InlineData("/absolute.blp")]
    [InlineData("C:\\outside.blp")]
    [InlineData("Interface/../outside.blp")]
    [InlineData("Interface//outside.blp")]
    public void Unsafe_archive_paths_are_rejected(string reference)
    {
        Assert.False(WoWClientAssetProvider.TryNormalizeInterfacePath(reference, out _, out _));
    }

    [Fact]
    public void Focused_client_resources_allow_safe_fonts_but_reject_other_roots()
    {
        Assert.True(WoWClientAssetProvider.TryNormalizeClientResourcePath(
            @"Fonts\FRIZQT__.TTF", out var font, out _));
        Assert.Equal(new[] { "Fonts", "FRIZQT__.TTF" }, font);
        Assert.False(WoWClientAssetProvider.TryNormalizeClientResourcePath(
            @"WDB\enUS\creaturecache.wdb", out _, out _));
    }

    [Fact]
    public void Materialization_refuses_a_symlink_escape_from_the_owned_cache()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var client = MakeClient("enUS");
        var archive = Path.Combine(client, "Data", "enUS", "locale-enUS.MPQ");
        var fake = new FakeArchiveReader();
        fake.Add(archive, @"Interface\Linked\asset.blp", [1]);
        var provider = Provider(fake, new(3, 3, 5, 12340));
        Directory.CreateDirectory(Path.Combine(provider.CacheRoot, "Interface"));
        Directory.CreateSymbolicLink(Path.Combine(provider.CacheRoot, "Interface", "Linked"), _temp);

        Assert.Throws<IOException>(() =>
            provider.Materialize(@"Interface\Linked\asset", provider.ValidateClient(client)));
        Assert.False(File.Exists(Path.Combine(_temp, "asset.blp")));
    }

    [Fact]
    public void Materialization_is_atomic_records_hash_and_cache_hit_avoids_archive_read()
    {
        var client = MakeClient("enUS");
        var archive = Path.Combine(client, "Data", "enUS", "locale-enUS.MPQ");
        var fake = new FakeArchiveReader();
        fake.Add(archive, @"Interface\TargetingFrame\UI-StatusBar.blp", [7, 8, 9]);
        var provider = Provider(fake, new(3, 3, 5, 12340));
        var validation = provider.ValidateClient(client);

        var first = provider.Materialize(@"Interface\TargetingFrame\UI-StatusBar", validation);
        var reads = fake.ReadCount;
        var second = provider.Materialize(@"Interface\TargetingFrame\UI-StatusBar", validation);

        Assert.True(first.Success);
        Assert.True(second.CacheHit);
        Assert.Equal(reads, fake.ReadCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 7, 8, 9 })).ToLowerInvariant(), first.Provenance!.Sha256);
        Assert.Equal(first.Provenance.Sha256, provider.GetProvenance(first.CachePath!)!.Sha256);
        Assert.Empty(Directory.EnumerateFiles(provider.CacheRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Clearing_owned_cache_allows_a_clean_rebuild()
    {
        var client = MakeClient("enUS");
        var archive = Path.Combine(client, "Data", "enUS", "locale-enUS.MPQ");
        var fake = new FakeArchiveReader();
        fake.Add(archive, @"Interface\X\asset.blp", [1]);
        var provider = Provider(fake, new(3, 3, 5, 12340));
        var validation = provider.ValidateClient(client);
        provider.Materialize(@"Interface\X\asset", validation);

        provider.ClearCache();
        fake.Add(archive, @"Interface\X\asset.blp", [2]);
        var rebuilt = provider.Materialize(@"Interface\X\asset", validation);

        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(rebuilt.CachePath!));
        Assert.False(rebuilt.CacheHit);
    }

    [Fact]
    public void Missing_archive_entry_is_reported_without_writing_a_file()
    {
        var client = MakeClient("enUS");
        var provider = Provider(new FakeArchiveReader(), new(3, 3, 5, 12340));
        var result = provider.Materialize(@"Interface\Missing\asset", provider.ValidateClient(client));
        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(provider.CacheRoot, "Interface")));
    }

    [Fact]
    public void Texture_discovery_merges_partial_archive_listfiles_and_cache_without_extracting_assets()
    {
        var client = MakeClient("enUS");
        var archive = Path.Combine(client, "Data", "enUS", "locale-enUS.MPQ");
        var fake = new FakeArchiveReader();
        fake.Add(archive, "(listfile)", System.Text.Encoding.UTF8.GetBytes(
            "Interface\\LFGFrame\\UI-LFG-FRAME.blp\r\nInterface\\Icons\\INV_Test.tga\r\nDBFilesClient\\Map.dbc\r\n"));
        var provider = Provider(fake, new(3, 3, 5, 12340));
        var cached = Path.Combine(provider.CacheRoot, "Interface", "Cached", "Known.blp");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.WriteAllBytes(cached, [1]);

        var catalog = provider.DiscoverTextures(provider.ValidateClient(client));

        Assert.Contains(@"Interface\LFGFrame\UI-LFG-FRAME.blp", catalog.Paths);
        Assert.Contains(@"Interface\Icons\INV_Test.tga", catalog.Paths);
        Assert.Contains(@"Interface\Cached\Known.blp", catalog.Paths);
        Assert.DoesNotContain(catalog.Paths, path => path.Contains("Map.dbc", StringComparison.Ordinal));
        Assert.False(catalog.IsComplete);
        Assert.Contains("partial", catalog.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, catalog.ArchivesWithListFiles);
    }

    [Fact]
    public void Explicit_real_client_texture_catalog_when_requested()
    {
        var clientRoot = Environment.GetEnvironmentVariable("FRAMEFORGE_WOW_CLIENT");
        var cacheRoot = Environment.GetEnvironmentVariable("FRAMEFORGE_WOW_CACHE_ROOT");
        if (string.IsNullOrWhiteSpace(clientRoot) || string.IsNullOrWhiteSpace(cacheRoot)) return;
        var provider = new WoWClientAssetProvider(cacheRoot);
        var validation = provider.ValidateClient(clientRoot);
        Assert.True(validation.IsValid, validation.Message);

        var catalog = provider.DiscoverTextures(validation);

        Assert.NotEmpty(catalog.Paths);
        Assert.Contains(catalog.Paths, path =>
            path.Contains("LFGFrame", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("partial", catalog.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolver_precedence_remains_source_then_manual_then_managed_cache()
    {
        var sourceRoot = Path.Combine(_temp, "addon", "Interface");
        var manualRoot = Path.Combine(_temp, "manual");
        var managedRoot = Path.Combine(_temp, "cache");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "Test"));
        Directory.CreateDirectory(Path.Combine(manualRoot, "Interface", "Test"));
        Directory.CreateDirectory(Path.Combine(managedRoot, "Interface", "Test"));
        var xml = Path.Combine(sourceRoot, "layout.xml");
        File.WriteAllText(xml, "<Ui />");
        File.WriteAllBytes(Path.Combine(sourceRoot, "Test", "asset.tga"), Tga(1));
        File.WriteAllBytes(Path.Combine(manualRoot, "Interface", "Test", "asset.tga"), Tga(2));
        File.WriteAllBytes(Path.Combine(managedRoot, "Interface", "Test", "asset.tga"), Tga(3));
        using var resolver = new TextureAssetResolver();

        resolver.Configure(xml, [manualRoot, managedRoot]);
        Assert.Equal(1, resolver.Resolve(@"Interface\Test\asset.tga").Texture!.Image.Bgra[0]);
        File.Delete(Path.Combine(sourceRoot, "Test", "asset.tga"));
        resolver.Refresh();
        Assert.Equal(2, resolver.Resolve(@"Interface\Test\asset.tga").Texture!.Image.Bgra[0]);
    }

    [Fact]
    public void Client_path_persists_only_in_machine_local_settings()
    {
        var settingsPath = Path.Combine(_temp, "settings", "settings.json");
        var store = new AssetSettingsStore(settingsPath);
        store.SaveConfiguration(new FrameForgeLocalSettings(["manual"], "/clients/wow"));
        var loaded = store.LoadConfiguration();
        Assert.Equal("/clients/wow", loaded.WowClientPath);
        Assert.Contains("wowClientPath", File.ReadAllText(settingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_real_client_acceptance_when_requested()
    {
        var clientRoot = Environment.GetEnvironmentVariable("FRAMEFORGE_WOW_CLIENT");
        var cacheRoot = Environment.GetEnvironmentVariable("FRAMEFORGE_WOW_CACHE_ROOT");
        var xml = Environment.GetEnvironmentVariable("FRAMEFORGE_NATIVE_HUNTS_XML");
        if (string.IsNullOrWhiteSpace(clientRoot) || string.IsNullOrWhiteSpace(cacheRoot) || string.IsNullOrWhiteSpace(xml))
            return;

        var provider = new WoWClientAssetProvider(cacheRoot);
        provider.ClearCache();
        var validation = provider.ValidateClient(clientRoot);
        Assert.True(validation.IsValid, validation.Message);
        Assert.Equal("enUS", validation.Locale);
        using var stock = new StockTemplateResolver(provider);
        var stockResults = stock.MaterializeRequired(validation);
        Assert.Equal(11, stockResults.Count);
        Assert.All(stockResults, result => Assert.True(result.Success, result.Message));
        Assert.Equal(StockDefinitionStatus.FullyResolved,
            stock.ResolveButton(StockTemplateResolver.TabTemplate)!.Status);
        Assert.Equal(10, stock.ResolveFont("GameFontNormalSmall")!.Size);
        Assert.Equal(16, stock.ResolveFont("GameFontHighlightLarge")!.Size);
        Assert.Equal((355d, 440d),
            (stock.ResolveExternalFrame(StockTemplateResolver.LfdParentFrame)!.Width,
                stock.ResolveExternalFrame(StockTemplateResolver.LfdParentFrame)!.Height));

        using (var stockTextures = new TextureAssetResolver())
        {
            stockTextures.Configure(xml, [provider.CacheRoot]);
            var tab = stockTextures.Resolve(@"Interface\PaperDollInfoFrame\UI-Character-InactiveTab");
            Assert.True(tab.CanRender, tab.Diagnostic.Message);
            Assert.Contains("DXT3", tab.Texture!.Image.Description, StringComparison.Ordinal);
        }
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"Interface\LFGFrame\UI-LFG-FRAME"] = "817264ed7787223d362cfafd504ce103d8773a6e7f8c4fb6cd338a183f236047",
            [@"Interface\LFGFrame\UI-LFG-BACKGROUND-QUESTPAPER"] = "ad4e620f902004702c50b4860d0147831631b57b97c6487fea34a9d77219975d",
            [@"Interface\TargetingFrame\UI-StatusBar"] = "233168bdc2faf17ba297c7a0a310560c55f1ce7fca5b91aac558e56217db945d",
        };
        foreach (var item in expected)
        {
            var result = provider.Materialize(item.Key, validation);
            Assert.True(result.Success, result.Message);
            Assert.Equal(item.Value, result.Provenance!.Sha256);
        }

        var import = FrameXmlImporter.ImportFile(xml);
        using var resolver = new TextureAssetResolver();
        resolver.Configure(xml, [provider.CacheRoot]);
        var direct = import.Project!.Frames.Where(frame => frame.Kind == FrameKind.TEXTURE)
            .Select(frame => frame.Visual?.Texture?.File).Where(path => path is { Length: > 0 }).Cast<string>().ToArray();
        Assert.Equal(19, direct.Length);
        Assert.Equal(19, direct.Count(reference => resolver.Resolve(reference).CanRender));
    }

    private WoWClientAssetProvider Provider(IWoWArchiveReader? archives = null, WowClientBuild? build = null) =>
        new(Path.Combine(_temp, "managed", "wow-3.3.5a-12340"), archives ?? new FakeArchiveReader(),
            new FakeBuildReader(build ?? new WowClientBuild(3, 3, 5, 12340)));

    private WoWClientAssetProvider Provider(WowClientBuild build) => Provider(null, build);

    private string MakeClient(string locale, params string[] globalPatches)
    {
        var root = Path.Combine(_temp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        Touch(Path.Combine(root, "Wow.exe"));
        Touch(Path.Combine(root, "Data", "common.MPQ"));
        foreach (var patch in globalPatches)
            Touch(Path.Combine(root, "Data", patch));
        MakeLocale(root, locale);
        return root;
    }

    private static void MakeLocale(string root, string locale)
    {
        var directory = Path.Combine(root, "Data", locale);
        Directory.CreateDirectory(directory);
        Touch(Path.Combine(directory, $"locale-{locale}.MPQ"));
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
    }

    private static byte[] Tga(byte blue)
    {
        var bytes = new byte[22];
        bytes[2] = 2; bytes[12] = 1; bytes[14] = 1; bytes[16] = 32; bytes[17] = 0x28;
        bytes[18] = blue; bytes[21] = 255;
        return bytes;
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
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
