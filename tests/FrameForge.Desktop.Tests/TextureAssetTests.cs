using FrameForge.Core.Models;
using FrameForge.Desktop.Assets;
using FrameForge.Core.Import;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class TextureAssetTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"frameforge-assets-{Guid.NewGuid():N}");

    public TextureAssetTests() => Directory.CreateDirectory(_temp);

    [Theory]
    [InlineData(@"Interface\NativeHunts\panel.tga")]
    [InlineData("Interface/NativeHunts/panel.tga")]
    public void Wow_paths_normalize_both_separator_styles(string path)
    {
        Assert.True(TextureAssetResolver.TryNormalize(path, out var segments, out _));
        Assert.Equal(new[] { "Interface", "NativeHunts", "panel.tga" }, segments);
    }

    [Theory]
    [InlineData("../secret.tga")]
    [InlineData("/absolute/file.tga")]
    [InlineData("C:\\file.tga")]
    public void Unsafe_paths_are_rejected(string path)
    {
        Assert.False(TextureAssetResolver.TryNormalize(path, out _, out _));
    }

    [Fact]
    public void Source_relative_resolution_supports_Interface_and_extension_omission()
    {
        var xml = MakeSourceTree("Panel.TGA", Tga(2, 1, topOrigin: true,
            (10, 20, 30, 255), (40, 50, 60, 128)));
        using var resolver = new TextureAssetResolver();
        resolver.Configure(xml, []);

        var asset = resolver.Resolve(@"Interface\NativeHunts\Panel");

        Assert.Equal(AssetResolutionStatus.Resolved, asset.Status);
        Assert.Equal(AssetSourceKind.SourceRelative, asset.SourceKind);
        Assert.Equal(TextureFileFormat.Tga, asset.Format);
        Assert.Equal(2, asset.Width);
        Assert.Equal(1, asset.Height);
        Assert.Equal(128, asset.Texture!.Image.Bgra[7]);
    }

    [Fact]
    public void Configured_Interface_root_is_accepted_and_root_changes_invalidate_cache()
    {
        var interfaceRoot = Path.Combine(_temp, "extracted", "Interface");
        Directory.CreateDirectory(Path.Combine(interfaceRoot, "NativeHunts"));
        File.WriteAllBytes(Path.Combine(interfaceRoot, "NativeHunts", "asset.tga"),
            Tga(1, 1, true, (1, 2, 3, 255)));
        using var resolver = new TextureAssetResolver();
        resolver.Configure(null, [interfaceRoot]);

        Assert.Equal(AssetResolutionStatus.Resolved, resolver.Resolve(@"Interface\NativeHunts\asset.tga").Status);
        Assert.Equal(1, resolver.DecodeCount);
        Assert.Equal(AssetResolutionStatus.Resolved, resolver.Resolve(@"Interface\NativeHunts\asset.tga").Status);
        Assert.True(resolver.CacheHitCount > 0);

        resolver.Configure(null, []);
        Assert.Equal(AssetResolutionStatus.Missing, resolver.Resolve(@"Interface\NativeHunts\asset.tga").Status);
    }

    [Fact]
    public void Case_insensitive_fallback_diagnoses_ambiguity_but_exact_case_wins()
    {
        var xml = MakeSourceTree("panel.tga", Tga(1, 1, true, (1, 2, 3, 255)));
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(xml))!, "NativeHunts");
        File.WriteAllBytes(Path.Combine(directory, "Panel.tga"), Tga(1, 1, true, (4, 5, 6, 255)));
        using var resolver = new TextureAssetResolver();
        resolver.Configure(xml, []);

        Assert.Equal(AssetResolutionStatus.Resolved, resolver.Resolve(@"Interface\NativeHunts\panel.tga").Status);
        Assert.Equal(AssetResolutionStatus.Ambiguous, resolver.Resolve(@"Interface\NativeHunts\PANEL.TGA").Status);
    }

    [Fact]
    public void Missing_unsupported_and_invalid_are_distinct()
    {
        var xml = MakeSourceTree("stock.blp", "BLP2"u8.ToArray());
        using var resolver = new TextureAssetResolver();
        resolver.Configure(xml, []);
        Assert.Equal(AssetResolutionStatus.UnsupportedFormat,
            resolver.Resolve(@"Interface\NativeHunts\stock.blp").Status);
        Assert.Equal(AssetResolutionStatus.Missing,
            resolver.Resolve(@"Interface\NativeHunts\absent.tga").Status);
        Assert.Equal(AssetResolutionStatus.InvalidPath, resolver.Resolve("../escape.tga").Status);
    }

    [Fact]
    public void Decoder_normalizes_bottom_origin_and_preserves_alpha()
    {
        using var stream = new MemoryStream(Tga(1, 2, topOrigin: false,
            (1, 2, 3, 64), (4, 5, 6, 200)));
        var image = TgaDecoder.Decode(stream);
        Assert.Equal(new byte[] { 4, 5, 6, 200, 1, 2, 3, 64 }, image.Bgra);
    }

    [Fact]
    public void Decoder_supports_24_bit_and_right_origin()
    {
        var bytes = new byte[18 + 6];
        bytes[2] = 2;
        bytes[12] = 2;
        bytes[14] = 1;
        bytes[16] = 24;
        bytes[17] = 0x30;
        bytes[18] = 1; bytes[19] = 2; bytes[20] = 3;
        bytes[21] = 4; bytes[22] = 5; bytes[23] = 6;

        var image = TgaDecoder.Decode(new MemoryStream(bytes));

        Assert.Equal(new byte[] { 4, 5, 6, 255, 1, 2, 3, 255 }, image.Bgra);
    }

    [Fact]
    public void Unsupported_compressed_Tga_is_not_silently_decoded()
    {
        var bytes = Tga(1, 1, true, (1, 2, 3, 255));
        bytes[2] = 10;
        Assert.Throws<NotSupportedException>(() => TgaDecoder.Decode(new MemoryStream(bytes)));
    }

    [Fact]
    public void Texcoords_map_pixels_and_reject_reversed_ranges()
    {
        Assert.Equal(new Avalonia.Rect(64, 16, 128, 48),
            TextureSourceRect.Map(new TexCoords(.25, .75, .25, 1), 256, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TextureSourceRect.Map(new TexCoords(.8, .2, 0, 1), 10, 10));
    }

    [Fact]
    public void Tint_and_alpha_are_per_render_transform_not_source_mutation()
    {
        var source = new DecodedImageData(1, 1, [100, 150, 200, 128]);
        var transformed = DecodedTexture.TransformToPremultiplied(source, new ColorRgba(.5, 1, .25));
        Assert.Equal(new byte[] { 13, 75, 50, 128 }, transformed);
        Assert.Equal(new byte[] { 100, 150, 200, 128 }, source.Bgra);
    }

    [Fact]
    public void Explicit_real_checkout_acceptance_when_requested()
    {
        var xml = Environment.GetEnvironmentVariable("FRAMEFORGE_NATIVE_HUNTS_XML");
        if (string.IsNullOrWhiteSpace(xml))
            return; // The normal portable suite intentionally has no sibling-checkout dependency.

        var import = FrameXmlImporter.ImportFile(xml);
        Assert.True(import.Ok);
        using var resolver = new TextureAssetResolver();
        resolver.Configure(xml, []);
        var textures = import.Project!.Frames.Where(frame => frame.Kind == FrameKind.TEXTURE).ToArray();
        var declared = textures.Where(frame => frame.Visual?.Texture?.File is { Length: > 0 }).ToArray();
        var results = declared.Select(frame => resolver.Resolve(frame.Visual!.Texture!.File)).ToArray();

        Assert.Equal(21, textures.Length);
        Assert.Equal(19, declared.Length);
        Assert.Equal(8, results.Count(result => result.Status == AssetResolutionStatus.Resolved));
        Assert.Equal(11, results.Count(result => result.Status == AssetResolutionStatus.Missing));
        Assert.Equal(8, results.Count(result => result.Format == TextureFileFormat.Tga && result.CanRender));
        Assert.Equal(0, results.Count(result => result.Status == AssetResolutionStatus.DecodeFailed));

        Console.WriteLine("NATIVE_ASSET_ACCEPTANCE " +
            $"textures={textures.Length} declared={declared.Length} resolved={results.Count(r => r.CanRender)} " +
            $"unresolved={results.Count(r => !r.CanRender)} renderedTga={results.Count(r => r.CanRender && r.Format == TextureFileFormat.Tga)} " +
            $"stockBlpExpected={results.Count(r => r.Status == AssetResolutionStatus.Missing)} " +
            $"inherited={textures.Length - declared.Length} decodeFailures={results.Count(r => r.Status == AssetResolutionStatus.DecodeFailed)}");
    }

    [Fact]
    public void Asset_roots_persist_in_machine_local_settings_not_project_data()
    {
        var settingsPath = Path.Combine(_temp, "settings", "settings.json");
        var store = new AssetSettingsStore(settingsPath);
        var roots = new[] { Path.Combine(_temp, "one"), Path.Combine(_temp, "two") };
        store.Save(roots);

        Assert.Equal(roots, new AssetSettingsStore(settingsPath).Load());
        Assert.Contains("assetRoots", File.ReadAllText(settingsPath), StringComparison.Ordinal);
    }

    private string MakeSourceTree(string fileName, byte[] bytes)
    {
        var xmlDirectory = Path.Combine(_temp, Guid.NewGuid().ToString("N"), "content", "client", "Interface", "FrameXML");
        var assetDirectory = Path.Combine(Path.GetDirectoryName(xmlDirectory)!, "NativeHunts");
        Directory.CreateDirectory(xmlDirectory);
        Directory.CreateDirectory(assetDirectory);
        var xml = Path.Combine(xmlDirectory, "Layout.xml");
        File.WriteAllText(xml, "<Ui />");
        File.WriteAllBytes(Path.Combine(assetDirectory, fileName), bytes);
        return xml;
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

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }
}
