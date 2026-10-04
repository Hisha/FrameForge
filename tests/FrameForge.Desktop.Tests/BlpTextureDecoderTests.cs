using System.Buffers.Binary;
using System.Security.Cryptography;
using FrameForge.Desktop.Assets;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class BlpTextureDecoderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"frameforge-blp-{Guid.NewGuid():N}");

    public BlpTextureDecoderTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void Parses_demonstrated_Blp2_Dxt1_header_and_decodes_rgb565()
    {
        var fixture = Blp2(4, 4, alphaDepth: 0, alphaEncoding: 0,
            Dxt1Block(0xf800, 0x07e0, 0xe4));
        var decoder = new BlpTextureDecoder();
        var header = BlpTextureDecoder.ReadHeader(new MemoryStream(fixture));
        var image = decoder.Decode(new MemoryStream(fixture));

        Assert.Equal("BLP2", header.Magic);
        Assert.Equal(2, header.Encoding);
        Assert.Equal(BlpDxtSubtype.Dxt1, header.RequiredSubtype);
        Assert.Equal((4, 4, 1), (header.Width, header.Height, header.MipCount));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, image.Bgra[..4]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, image.Bgra[4..8]);
    }

    [Fact]
    public void Dxt1_one_bit_alpha_decodes_transparent_index()
    {
        var block = Dxt1Block(0x0000, 0xffff, 0xff);
        var image = new BlpTextureDecoder().Decode(new MemoryStream(Blp2(4, 4, 1, 0, block)));
        Assert.All(Enumerable.Range(0, 16), pixel => Assert.Equal(0, image.Bgra[pixel * 4 + 3]));
    }

    [Fact]
    public void Dxt5_decodes_interpolated_alpha_indices()
    {
        var block = new byte[16];
        block[0] = 255;
        block[1] = 0;
        block[2] = 8; // pixel 0 index 0, pixel 1 index 1
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8, 2), 0xf800);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(10, 2), 0x07e0);
        var image = new BlpTextureDecoder().Decode(new MemoryStream(Blp2(4, 4, 8, 7, block)));
        Assert.Equal(255, image.Bgra[3]);
        Assert.Equal(0, image.Bgra[7]);
        Assert.Contains("DXT5", image.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_malformed_truncated_and_unsupported_Blp()
    {
        var malformed = Blp2(4, 4, 0, 0, Dxt1Block(0xf800, 0x07e0, 0));
        malformed[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => new BlpTextureDecoder().Decode(new MemoryStream(malformed)));

        var truncated = Blp2(4, 4, 0, 0, Dxt1Block(0xf800, 0x07e0, 0));
        Array.Resize(ref truncated, truncated.Length - 1);
        Assert.Throws<EndOfStreamException>(() => new BlpTextureDecoder().Decode(new MemoryStream(truncated)));

        var unsupported = Blp2(4, 4, 4, 1, new byte[16]);
        Assert.Throws<UnsupportedTextureEncodingException>(() =>
            new BlpTextureDecoder().Decode(new MemoryStream(unsupported)));
    }

    [Fact]
    public void Registry_dispatches_Blp_and_resolver_reuses_one_decoded_atlas_for_multiple_crops()
    {
        var root = Path.Combine(_temp, "root");
        var directory = Path.Combine(root, "Interface", "LFGFrame");
        Directory.CreateDirectory(directory);
        var red = Dxt1Block(0xf800, 0x07e0, 0);
        var blue = Dxt1Block(0x001f, 0x07e0, 0);
        File.WriteAllBytes(Path.Combine(directory, "atlas.blp"), Blp2(8, 4, 0, 0, [.. red, .. blue]));
        using var resolver = new TextureAssetResolver();
        resolver.Configure(null, [root]);

        var first = resolver.Resolve(@"Interface\LFGFrame\atlas");
        var second = resolver.Resolve(@"Interface\LFGFrame\atlas");

        Assert.Equal(TextureFileFormat.Blp, first.Format);
        Assert.Same(first.Texture, second.Texture);
        Assert.Equal(1, resolver.DecodeCount);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, first.Texture!.Image.Bgra[..4]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, first.Texture.Image.Bgra[(4 * 4)..(4 * 4 + 4)]);
        Assert.NotEqual(
            TextureSourceRect.Map(new(0, .5, 0, 1), 8, 4),
            TextureSourceRect.Map(new(.5, 1, 0, 1), 8, 4));

        resolver.Refresh();
        resolver.Resolve(@"Interface\LFGFrame\atlas");
        Assert.Equal(2, resolver.DecodeCount);
    }

    [Fact]
    public void Explicit_real_Blp_headers_and_hashes_when_requested()
    {
        var root = Environment.GetEnvironmentVariable("FRAMEFORGE_NATIVE_HUNTS_ASSET_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            return;

        Check("Interface/LFGFrame/UI-LFG-FRAME.blp", 512, 512, 8, 7, 10,
            "817264ed7787223d362cfafd504ce103d8773a6e7f8c4fb6cd338a183f236047", BlpDxtSubtype.Dxt5);
        Check("Interface/LFGFrame/UI-LFG-BACKGROUND-QUESTPAPER.blp", 512, 256, 1, 0, 10,
            "ad4e620f902004702c50b4860d0147831631b57b97c6487fea34a9d77219975d", BlpDxtSubtype.Dxt1);
        Check("Interface/TARGETINGFRAME/UI-StatusBar.blp", 64, 8, 0, 0, 7,
            "233168bdc2faf17ba297c7a0a310560c55f1ce7fca5b91aac558e56217db945d", BlpDxtSubtype.Dxt1);

        void Check(string relative, int width, int height, byte alphaDepth, byte alphaEncoding,
            int mips, string hash, BlpDxtSubtype subtype)
        {
            var path = Path.Combine(root, relative);
            using var stream = File.OpenRead(path);
            var header = BlpTextureDecoder.ReadHeader(stream);
            Assert.Equal((width, height, alphaDepth, alphaEncoding, mips, subtype),
                (header.Width, header.Height, header.AlphaDepth, header.AlphaEncoding, header.MipCount, header.RequiredSubtype));
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
            stream.Position = 0;
            var image = new BlpTextureDecoder().Decode(stream);
            Assert.Equal((width, height), (image.Width, image.Height));
        }
    }

    private static byte[] Blp2(int width, int height, byte alphaDepth, byte alphaEncoding, byte[] mip, byte encoding = 2)
    {
        var bytes = new byte[BlpTextureDecoder.HeaderSize + BlpTextureDecoder.PaletteSize + mip.Length];
        "BLP2"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 1);
        bytes[8] = encoding;
        bytes[9] = alphaDepth;
        bytes[10] = alphaEncoding;
        bytes[11] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4),
            BlpTextureDecoder.HeaderSize + BlpTextureDecoder.PaletteSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84, 4), (uint)mip.Length);
        mip.CopyTo(bytes, BlpTextureDecoder.HeaderSize + BlpTextureDecoder.PaletteSize);
        return bytes;
    }

    private static byte[] Dxt1Block(ushort color0, ushort color1, byte repeatingIndices)
    {
        var block = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0, 2), color0);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2, 2), color1);
        block[4] = block[5] = block[6] = block[7] = repeatingIndices;
        return block;
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }
}
