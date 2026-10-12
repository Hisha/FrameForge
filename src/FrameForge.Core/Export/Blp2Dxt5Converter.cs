using System.Buffers.Binary;
using System.Runtime.InteropServices;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using SkiaSharp;

namespace FrameForge.Core.Export;

internal readonly record struct PngDimensions(int Width, int Height);

internal readonly record struct Blp2TextureLayout(
    int SourceWidth,
    int SourceHeight,
    int PreparedWidth,
    int PreparedHeight,
    int Width,
    int Height,
    int MipCount)
{
    public bool Resized => SourceWidth != PreparedWidth || SourceHeight != PreparedHeight;
    public bool PaddingApplied => PreparedWidth != Width || PreparedHeight != Height;
}

internal readonly record struct Blp2Verification(
    int MipCount,
    double PremultipliedColorMeanAbsoluteError,
    double AlphaMeanAbsoluteError);

/// <summary>
/// Prepares PNG artwork and writes the BLP2/DXT5 form consumed by WoW 3.3.5a. BC3 block
/// compression is delegated to BCnEncoder.Net; the BLP container and independent decoder are
/// deliberately local so malformed offsets, lengths, or encoder output fail before publication.
/// </summary>
internal static class Blp2Dxt5Converter
{
    internal const int HeaderSize = 148;
    internal const int PaletteSize = 1024;
    internal const int PayloadOffset = HeaderSize + PaletteSize;
    private const int MaxMipmaps = 16;
    private const int BlockBytes = 16;

    public static PngDimensions ReadPngDimensions(Stream source)
    {
        using var codec = CreateCodec(source);
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0)
            throw new InvalidDataException("PNG dimensions must be positive.");
        return new(codec.Info.Width, codec.Info.Height);
    }

    public static Blp2TextureLayout CreateLayout(int sourceWidth, int sourceHeight,
        double requiredWidth, double requiredHeight, bool preserveSource)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new InvalidDataException("PNG dimensions must be positive.");
        if (!double.IsFinite(requiredWidth) || !double.IsFinite(requiredHeight)
            || requiredWidth <= 0 || requiredHeight <= 0)
            throw new InvalidDataException("Required texture dimensions must be finite and positive.");

        var scale = preserveSource
            ? 1d
            : Math.Min(1d, Math.Max(requiredWidth / sourceWidth, requiredHeight / sourceHeight));
        var preparedWidth = Math.Max(1, (int)Math.Ceiling(sourceWidth * scale));
        var preparedHeight = Math.Max(1, (int)Math.Ceiling(sourceHeight * scale));
        var width = NextPowerOfTwo(preparedWidth);
        var height = NextPowerOfTwo(preparedHeight);
        var mipCount = 1;
        for (int w = width, h = height; w > 1 || h > 1; mipCount++)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        if (mipCount > MaxMipmaps)
            throw new InvalidDataException($"Prepared texture {width}x{height} needs {mipCount} mip levels; BLP2 supports at most {MaxMipmaps}.");
        return new(sourceWidth, sourceHeight, preparedWidth, preparedHeight, width, height, mipCount);
    }

    public static Blp2Verification Convert(Stream source, Stream destination, Blp2TextureLayout expected)
    {
        var sourcePixels = DecodePng(source, out var sourceWidth, out var sourceHeight);
        if (sourceWidth != expected.SourceWidth || sourceHeight != expected.SourceHeight)
            throw new InvalidDataException("PNG dimensions changed while the export was being written.");

        var prepared = ResizePremultipliedArea(sourcePixels, sourceWidth, sourceHeight,
            expected.PreparedWidth, expected.PreparedHeight);
        var padded = new byte[checked(expected.Width * expected.Height * 4)];
        for (var y = 0; y < expected.PreparedHeight; y++)
            prepared.AsSpan(y * expected.PreparedWidth * 4, expected.PreparedWidth * 4)
                .CopyTo(padded.AsSpan(y * expected.Width * 4));

        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = true;
        encoder.OutputOptions.Quality = CompressionQuality.BestQuality;
        encoder.OutputOptions.Format = CompressionFormat.Bc3;
        var mipmaps = encoder.EncodeToRawBytes(padded, expected.Width, expected.Height, PixelFormat.Rgba32);
        if (mipmaps.Length != expected.MipCount)
            throw new InvalidDataException($"BC3 encoder returned {mipmaps.Length} mip levels; expected {expected.MipCount}.");

        using var encoded = new MemoryStream();
        WriteContainer(encoded, expected, mipmaps);
        encoded.Position = 0;
        var verification = VerifyAndDecode(encoded, expected, out var decodedBase);
        ComparePreparedPixels(prepared, decodedBase, expected, out var colorError, out var alphaError);
        VerifyTransparentPadding(decodedBase, expected);
        if (colorError > 64 || alphaError > 32)
            throw new InvalidDataException(
                $"Independent DXT5 decode exceeded export quality limits (premultiplied RGB MAE {colorError:0.###}, alpha MAE {alphaError:0.###}).");

        encoded.Position = 0;
        encoded.CopyTo(destination);
        return verification with
        {
            PremultipliedColorMeanAbsoluteError = colorError,
            AlphaMeanAbsoluteError = alphaError,
        };
    }

    public static Blp2Verification Verify(Stream stream, Blp2TextureLayout expected)
    {
        stream.Position = 0;
        return VerifyAndDecode(stream, expected, out _);
    }

    private static void WriteContainer(Stream destination, Blp2TextureLayout layout, IReadOnlyList<byte[]> mipmaps)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        "BLP2"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], 1);
        header[8] = 2; // DXT
        header[9] = 8; // full alpha
        header[10] = 7; // DXT5 / BC3
        header[11] = 1; // mipmaps present
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], checked((uint)layout.Width));
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], checked((uint)layout.Height));
        var offset = PayloadOffset;
        for (var index = 0; index < mipmaps.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[(20 + index * 4)..], checked((uint)offset));
            BinaryPrimitives.WriteUInt32LittleEndian(header[(84 + index * 4)..], checked((uint)mipmaps[index].Length));
            offset = checked(offset + mipmaps[index].Length);
        }
        destination.Write(header);
        destination.Write(new byte[PaletteSize]);
        foreach (var mipmap in mipmaps)
            destination.Write(mipmap);
    }

    private static Blp2Verification VerifyAndDecode(Stream stream, Blp2TextureLayout expected,
        out byte[] decodedBase)
    {
        if (!stream.CanSeek)
            throw new InvalidDataException("BLP2 verification requires a seekable stream.");
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("BLP2"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) != 1
            || header[8] != 2 || header[9] != 8 || header[10] != 7 || header[11] != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]) != expected.Width
            || BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]) != expected.Height)
            throw new InvalidDataException("Generated BLP header is not BLP2 type 1, DXT5, eight-bit alpha with mipmaps.");

        var nextOffset = PayloadOffset;
        decodedBase = [];
        for (var index = 0; index < expected.MipCount; index++)
        {
            var width = Math.Max(1, expected.Width >> index);
            var height = Math.Max(1, expected.Height >> index);
            var requiredLength = checked(((width + 3) / 4) * ((height + 3) / 4) * BlockBytes);
            var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[(20 + index * 4)..]));
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[(84 + index * 4)..]));
            if (offset != nextOffset || length != requiredLength || (long)offset + length > stream.Length)
                throw new InvalidDataException($"Generated BLP mip {index} has invalid offset, length, or file bounds.");
            if (index == 0)
            {
                stream.Position = offset;
                var compressed = new byte[length];
                stream.ReadExactly(compressed);
                decodedBase = DecodeDxt5(compressed, width, height);
            }
            nextOffset = checked(offset + length);
        }
        for (var index = expected.MipCount; index < MaxMipmaps; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[(20 + index * 4)..]) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(header[(84 + index * 4)..]) != 0)
                throw new InvalidDataException("Generated BLP contains non-zero entries after the mip chain.");
        }
        if (nextOffset != stream.Length)
            throw new InvalidDataException("Generated BLP has trailing or missing payload bytes.");
        return new(expected.MipCount, 0, 0);
    }

    private static byte[] DecodePng(Stream source, out int width, out int height)
    {
        using var codec = CreateCodec(source);
        width = codec.Info.Width;
        height = codec.Info.Height;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
            throw new InvalidDataException($"PNG pixel data could not be decoded completely ({result}).");
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
            Marshal.Copy(bitmap.GetPixels() + y * bitmap.RowBytes, pixels, y * width * 4, width * 4);
        return pixels;
    }

    private static SKCodec CreateCodec(Stream source)
    {
        if (source.CanSeek)
            source.Position = 0;
        var codec = SKCodec.Create(source) ?? throw new InvalidDataException("PNG data could not be decoded.");
        if (codec.EncodedFormat != SKEncodedImageFormat.Png)
        {
            codec.Dispose();
            throw new InvalidDataException("The project artwork has a .png extension but is not PNG data.");
        }
        return codec;
    }

    private static byte[] ResizePremultipliedArea(byte[] source, int sourceWidth, int sourceHeight,
        int width, int height)
    {
        if (sourceWidth == width && sourceHeight == height)
            return [.. source];
        var destination = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var top = (double)y * sourceHeight / height;
            var bottom = (double)(y + 1) * sourceHeight / height;
            for (var x = 0; x < width; x++)
            {
                var left = (double)x * sourceWidth / width;
                var right = (double)(x + 1) * sourceWidth / width;
                double alphaTotal = 0;
                double red = 0;
                double green = 0;
                double blue = 0;
                double totalWeight = 0;
                var lastSourceY = Math.Min(sourceHeight, (int)Math.Ceiling(bottom));
                var lastSourceX = Math.Min(sourceWidth, (int)Math.Ceiling(right));
                for (var sourceY = (int)Math.Floor(top); sourceY < lastSourceY; sourceY++)
                {
                    var yWeight = Math.Min(bottom, sourceY + 1) - Math.Max(top, sourceY);
                    for (var sourceX = (int)Math.Floor(left); sourceX < lastSourceX; sourceX++)
                    {
                        var xWeight = Math.Min(right, sourceX + 1) - Math.Max(left, sourceX);
                        var weight = xWeight * yWeight;
                        var offset = (sourceY * sourceWidth + sourceX) * 4;
                        var sourceAlpha = source[offset + 3];
                        red += source[offset] * sourceAlpha / 255d * weight;
                        green += source[offset + 1] * sourceAlpha / 255d * weight;
                        blue += source[offset + 2] * sourceAlpha / 255d * weight;
                        alphaTotal += sourceAlpha * weight;
                        totalWeight += weight;
                    }
                }
                var alpha = Math.Clamp(alphaTotal / totalWeight, 0, 255);
                var target = (y * width + x) * 4;
                destination[target + 3] = (byte)Math.Round(alpha);
                destination[target] = Unpremultiply(red / totalWeight, alpha);
                destination[target + 1] = Unpremultiply(green / totalWeight, alpha);
                destination[target + 2] = Unpremultiply(blue / totalWeight, alpha);
            }
        }
        return destination;

        static byte Unpremultiply(double value, double alpha) => alpha <= 0
            ? (byte)0
            : (byte)Math.Clamp(Math.Round(value * 255 / alpha), 0, 255);
    }

    private static byte[] DecodeDxt5(ReadOnlySpan<byte> data, int width, int height)
    {
        var output = new byte[checked(width * height * 4)];
        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var alphaBuffer = new byte[8];
        var colorBuffer = new byte[16];
        if (data.Length != checked(blocksWide * blocksHigh * BlockBytes))
            throw new InvalidDataException("DXT5 payload length does not match its dimensions.");
        for (var blockY = 0; blockY < blocksHigh; blockY++)
            for (var blockX = 0; blockX < blocksWide; blockX++)
            {
                var block = data.Slice((blockY * blocksWide + blockX) * BlockBytes, BlockBytes);
                var alpha = alphaBuffer.AsSpan();
                alpha.Clear();
                alpha[0] = block[0]; alpha[1] = block[1];
                if (alpha[0] > alpha[1])
                    for (var i = 1; i <= 6; i++) alpha[i + 1] = (byte)(((7 - i) * alpha[0] + i * alpha[1]) / 7);
                else
                {
                    for (var i = 1; i <= 4; i++) alpha[i + 1] = (byte)(((5 - i) * alpha[0] + i * alpha[1]) / 5);
                    alpha[6] = 0; alpha[7] = 255;
                }
                ulong alphaIndices = 0;
                for (var i = 0; i < 6; i++) alphaIndices |= (ulong)block[2 + i] << (i * 8);

                var colors = colorBuffer.AsSpan();
                colors.Clear();
                Write565(BinaryPrimitives.ReadUInt16LittleEndian(block[8..10]), colors[..4]);
                Write565(BinaryPrimitives.ReadUInt16LittleEndian(block[10..12]), colors[4..8]);
                for (var channel = 0; channel < 3; channel++)
                {
                    colors[8 + channel] = (byte)((2 * colors[channel] + colors[4 + channel]) / 3);
                    colors[12 + channel] = (byte)((colors[channel] + 2 * colors[4 + channel]) / 3);
                }
                var colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(block[12..16]);
                for (var py = 0; py < 4; py++)
                    for (var px = 0; px < 4; px++)
                    {
                        var x = blockX * 4 + px;
                        var y = blockY * 4 + py;
                        if (x >= width || y >= height) continue;
                        var pixel = py * 4 + px;
                        var color = (int)((colorIndices >> (pixel * 2)) & 3);
                        var target = (y * width + x) * 4;
                        colors.Slice(color * 4, 3).CopyTo(output.AsSpan(target, 3));
                        output[target + 3] = alpha[(int)((alphaIndices >> (pixel * 3)) & 7)];
                    }
            }
        return output;
    }

    private static void Write565(ushort value, Span<byte> rgba)
    {
        rgba[0] = (byte)((((value >> 11) & 31) * 255 + 15) / 31);
        rgba[1] = (byte)((((value >> 5) & 63) * 255 + 31) / 63);
        rgba[2] = (byte)(((value & 31) * 255 + 15) / 31);
        rgba[3] = 255;
    }

    private static void ComparePreparedPixels(ReadOnlySpan<byte> source, ReadOnlySpan<byte> decoded,
        Blp2TextureLayout layout, out double colorError, out double alphaError)
    {
        double colorTotal = 0;
        double alphaTotal = 0;
        var count = checked(layout.PreparedWidth * layout.PreparedHeight);
        for (var y = 0; y < layout.PreparedHeight; y++)
            for (var x = 0; x < layout.PreparedWidth; x++)
            {
                var sourceOffset = (y * layout.PreparedWidth + x) * 4;
                var decodedOffset = (y * layout.Width + x) * 4;
                var sourceAlpha = source[sourceOffset + 3];
                var decodedAlpha = decoded[decodedOffset + 3];
                alphaTotal += Math.Abs(sourceAlpha - decodedAlpha);
                for (var channel = 0; channel < 3; channel++)
                {
                    var sourcePremultiplied = source[sourceOffset + channel] * sourceAlpha / 255d;
                    var decodedPremultiplied = decoded[decodedOffset + channel] * decodedAlpha / 255d;
                    colorTotal += Math.Abs(sourcePremultiplied - decodedPremultiplied);
                }
            }
        colorError = colorTotal / (count * 3);
        alphaError = alphaTotal / count;
    }

    private static void VerifyTransparentPadding(ReadOnlySpan<byte> decoded, Blp2TextureLayout layout)
    {
        for (var y = 0; y < layout.Height; y++)
            for (var x = 0; x < layout.Width; x++)
            {
                if (x < layout.PreparedWidth && y < layout.PreparedHeight)
                    continue;
                if (decoded[(y * layout.Width + x) * 4 + 3] > 8)
                    throw new InvalidDataException("Independent DXT5 decode found non-transparent power-of-two padding.");
            }
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
            result = checked(result * 2);
        return result;
    }
}
