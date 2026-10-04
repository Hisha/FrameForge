using System.Buffers.Binary;

namespace FrameForge.Desktop.Assets;

public enum BlpCompression { Jpeg = 0, Direct = 1, Dxt = 2, Argb = 3 }
public enum BlpDxtSubtype { Dxt1, Dxt3, Dxt5 }

public sealed record BlpHeader(
    string Magic,
    uint Type,
    byte Encoding,
    byte AlphaDepth,
    byte AlphaEncoding,
    bool HasMipmaps,
    int Width,
    int Height,
    IReadOnlyList<uint> MipOffsets,
    IReadOnlyList<uint> MipSizes)
{
    public int MipCount => MipOffsets.Zip(MipSizes).TakeWhile(pair => pair.First > 0 && pair.Second > 0).Count();

    public BlpDxtSubtype RequiredSubtype => (Encoding, AlphaDepth, AlphaEncoding) switch
    {
        (2, 0 or 1, 0) => BlpDxtSubtype.Dxt1,
        (2, 8, 1) => BlpDxtSubtype.Dxt3,
        (2, 8, 7) => BlpDxtSubtype.Dxt5,
        _ => throw new UnsupportedTextureEncodingException(
            $"Unsupported BLP2 encoding tuple: encoding={Encoding}, alphaDepth={AlphaDepth}, alphaEncoding={AlphaEncoding}."),
    };

    public string Description => RequiredSubtype switch
    {
        BlpDxtSubtype.Dxt1 => $"BLP2 DXT1 ({(AlphaDepth == 1 ? "1-bit alpha" : "opaque")}, base mip, {MipCount} mip levels)",
        BlpDxtSubtype.Dxt3 => $"BLP2 DXT3 (4-bit explicit alpha, base mip, {MipCount} mip levels)",
        BlpDxtSubtype.Dxt5 => $"BLP2 DXT5 (8-bit interpolated alpha, base mip, {MipCount} mip levels)",
        _ => "BLP2",
    };
}

/// <summary>Decodes only the BLP2 DXT1, DXT3, and DXT5 forms demonstrated by required stock art.</summary>
public sealed class BlpTextureDecoder : ITextureDecoder
{
    public const int HeaderSize = 148;
    public const int PaletteSize = 1024;
    public TextureFileFormat Format => TextureFileFormat.Blp;

    public DecodedImageData Decode(Stream stream)
    {
        var header = ReadHeader(stream);
        var subtype = header.RequiredSubtype;
        var blockBytes = subtype == BlpDxtSubtype.Dxt1 ? 8 : 16;
        var expected = checked(((header.Width + 3) / 4) * ((header.Height + 3) / 4) * blockBytes);
        var offset = header.MipOffsets[0];
        var size = header.MipSizes[0];
        if (size < expected)
            throw new InvalidDataException($"BLP2 base mip is truncated: expected {expected} bytes, header declares {size}.");
        if (offset > int.MaxValue || size > int.MaxValue)
            throw new InvalidDataException("BLP2 base mip offset or size is too large.");
        if (!stream.CanSeek)
            throw new InvalidDataException("BLP2 decoding requires a seekable stream.");
        if ((long)offset + size > stream.Length)
            throw new EndOfStreamException("BLP2 base mip extends beyond the physical file.");

        stream.Position = offset;
        var compressed = new byte[checked((int)size)];
        ReadExactly(stream, compressed);
        var pixels = subtype switch
        {
            BlpDxtSubtype.Dxt1 => DecodeDxt1(compressed, header.Width, header.Height, header.AlphaDepth == 1),
            BlpDxtSubtype.Dxt3 => DecodeDxt3(compressed, header.Width, header.Height),
            BlpDxtSubtype.Dxt5 => DecodeDxt5(compressed, header.Width, header.Height),
            _ => throw new UnsupportedTextureEncodingException($"Unsupported DXT subtype {subtype}."),
        };
        return new DecodedImageData(header.Width, header.Height, pixels, header.Description, TextureFileFormat.Blp);
    }

    public static BlpHeader ReadHeader(Stream stream)
    {
        if (stream.CanSeek)
            stream.Position = 0;
        Span<byte> bytes = stackalloc byte[HeaderSize];
        ReadExactly(stream, bytes);
        var magic = System.Text.Encoding.ASCII.GetString(bytes[..4]);
        if (magic != "BLP2")
            throw magic == "BLP1"
                ? new UnsupportedTextureEncodingException("BLP1 is not supported; Phase 4B implements only demonstrated BLP2 DXT forms.")
                : new InvalidDataException($"Invalid BLP signature '{magic}'.");

        var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]);
        var width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]));
        var height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..20]));
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("BLP2 dimensions must be non-zero.");

        var offsets = new uint[16];
        var sizes = new uint[16];
        for (var i = 0; i < 16; i++)
        {
            offsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(20 + i * 4)..(24 + i * 4)]);
            sizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(84 + i * 4)..(88 + i * 4)]);
        }
        if (offsets[0] == 0 || sizes[0] == 0)
            throw new InvalidDataException("BLP2 has no base mip.");

        return new BlpHeader(magic, type, bytes[8], bytes[9], bytes[10], bytes[11] != 0,
            width, height, offsets, sizes);
    }

    public static byte[] DecodeDxt1(ReadOnlySpan<byte> data, int width, int height, bool oneBitAlpha)
    {
        var output = new byte[checked(width * height * 4)];
        DecodeBlocks(data, width, height, 8, (block, colors, alpha) =>
        {
            BuildColorPalette(block, colors, forceFourColors: !oneBitAlpha);
            var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..8]);
            for (var pixel = 0; pixel < 16; pixel++)
            {
                var index = (int)((indices >> (pixel * 2)) & 3);
                alpha[pixel] = oneBitAlpha && colors[index * 4 + 3] == 0 ? (byte)0 : (byte)255;
                colors[index * 4 + 3] = alpha[pixel];
            }
        }, output);
        return output;
    }

    public static byte[] DecodeDxt5(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[checked(width * height * 4)];
        DecodeBlocks(data, width, height, 16, (block, colors, alpha) =>
        {
            BuildAlphaPalette(block, alpha);
            BuildColorPalette(block[8..], colors, forceFourColors: true);
            var colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(block[12..16]);
            for (var pixel = 0; pixel < 16; pixel++)
            {
                var index = (int)((colorIndices >> (pixel * 2)) & 3);
                colors[index * 4 + 3] = alpha[pixel];
            }
        }, output);
        return output;
    }

    public static byte[] DecodeDxt3(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[checked(width * height * 4)];
        DecodeBlocks(data, width, height, 16, (block, colors, alpha) =>
        {
            for (var pixel = 0; pixel < 16; pixel++)
            {
                var packed = block[pixel / 2];
                var nibble = pixel % 2 == 0 ? packed & 0x0f : packed >> 4;
                alpha[pixel] = (byte)(nibble * 17);
            }
            BuildColorPalette(block[8..], colors, forceFourColors: true);
        }, output);
        return output;
    }

    private delegate void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> colors, Span<byte> alpha);

    private static void DecodeBlocks(ReadOnlySpan<byte> data, int width, int height, int blockBytes,
        DecodeBlock decode, byte[] output)
    {
        var blockColumns = (width + 3) / 4;
        var blockRows = (height + 3) / 4;
        var required = checked(blockColumns * blockRows * blockBytes);
        if (data.Length < required)
            throw new EndOfStreamException($"DXT data is truncated: expected {required} bytes, got {data.Length}.");

        Span<byte> colors = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[16];
        for (var blockY = 0; blockY < blockRows; blockY++)
        for (var blockX = 0; blockX < blockColumns; blockX++)
        {
            colors.Clear();
            alpha.Clear();
            var block = data.Slice((blockY * blockColumns + blockX) * blockBytes, blockBytes);
            decode(block, colors, alpha);
            var colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(block[(blockBytes - 4)..]);
            for (var py = 0; py < 4; py++)
            for (var px = 0; px < 4; px++)
            {
                var x = blockX * 4 + px;
                var y = blockY * 4 + py;
                if (x >= width || y >= height)
                    continue;
                var pixel = py * 4 + px;
                var color = (int)((colorIndices >> (pixel * 2)) & 3);
                var target = (y * width + x) * 4;
                output[target] = colors[color * 4];
                output[target + 1] = colors[color * 4 + 1];
                output[target + 2] = colors[color * 4 + 2];
                output[target + 3] = alpha[pixel];
            }
        }
    }

    private static void BuildColorPalette(ReadOnlySpan<byte> block, Span<byte> colors, bool forceFourColors)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(block[..2]);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..4]);
        WriteRgb565(c0, colors[..4]);
        WriteRgb565(c1, colors[4..8]);
        colors[3] = colors[7] = 255;
        if (c0 > c1 || forceFourColors)
        {
            Mix(colors[..4], colors[4..8], 2, 1, 3, colors[8..12]);
            Mix(colors[..4], colors[4..8], 1, 2, 3, colors[12..16]);
            colors[11] = colors[15] = 255;
        }
        else
        {
            Mix(colors[..4], colors[4..8], 1, 1, 2, colors[8..12]);
            colors[11] = 255;
            colors[12] = colors[13] = colors[14] = colors[15] = 0;
        }
    }

    private static void BuildAlphaPalette(ReadOnlySpan<byte> block, Span<byte> output)
    {
        Span<byte> palette = stackalloc byte[8];
        palette[0] = block[0];
        palette[1] = block[1];
        if (palette[0] > palette[1])
        {
            for (var i = 1; i <= 6; i++)
                palette[i + 1] = (byte)(((7 - i) * palette[0] + i * palette[1]) / 7);
        }
        else
        {
            for (var i = 1; i <= 4; i++)
                palette[i + 1] = (byte)(((5 - i) * palette[0] + i * palette[1]) / 5);
            palette[6] = 0;
            palette[7] = 255;
        }

        ulong indices = 0;
        for (var i = 0; i < 6; i++)
            indices |= (ulong)block[2 + i] << (8 * i);
        for (var pixel = 0; pixel < 16; pixel++)
            output[pixel] = palette[(int)((indices >> (pixel * 3)) & 7)];
    }

    private static void WriteRgb565(ushort value, Span<byte> bgra)
    {
        var r = (value >> 11) & 31;
        var g = (value >> 5) & 63;
        var b = value & 31;
        bgra[0] = (byte)((b * 255 + 15) / 31);
        bgra[1] = (byte)((g * 255 + 31) / 63);
        bgra[2] = (byte)((r * 255 + 15) / 31);
    }

    private static void Mix(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second,
        int firstWeight, int secondWeight, int divisor, Span<byte> output)
    {
        for (var channel = 0; channel < 3; channel++)
            output[channel] = (byte)((firstWeight * first[channel] + secondWeight * second[channel]) / divisor);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0)
                throw new EndOfStreamException("The BLP file ended before the requested data was complete.");
            read += count;
        }
    }
}
