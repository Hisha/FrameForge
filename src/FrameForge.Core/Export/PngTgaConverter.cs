using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace FrameForge.Core.Export;

internal readonly record struct PngTgaLayout(
    int SourceWidth,
    int SourceHeight,
    int Width,
    int Height)
{
    public bool PaddingApplied => SourceWidth != Width || SourceHeight != Height;
}

/// <summary>Converts PNG design artwork to the conservative TGA variant accepted by WoW 3.3.5a.</summary>
internal static class PngTgaConverter
{
    private const byte UncompressedTrueColor = 2;
    private const byte BitsPerPixel = 32;
    private const byte TopLeftWithEightAlphaBits = 0x28;
    private const int LargestPowerOfTwoTgaDimension = 32768;

    public static PngTgaLayout Validate(Stream source)
    {
        using var codec = CreateCodec(source);
        return CreateLayout(codec.Info.Width, codec.Info.Height);
    }

    public static PngTgaLayout Convert(Stream source, Stream destination)
    {
        using var codec = CreateCodec(source);
        var layout = CreateLayout(codec.Info.Width, codec.Info.Height);

        var info = new SKImageInfo(layout.SourceWidth, layout.SourceHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
            throw new InvalidDataException($"PNG pixel data could not be decoded completely ({result}).");

        Span<byte> header = stackalloc byte[18];
        header.Clear();
        header[2] = UncompressedTrueColor;
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..14], checked((ushort)layout.Width));
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..16], checked((ushort)layout.Height));
        header[16] = BitsPerPixel;
        header[17] = TopLeftWithEightAlphaBits;
        destination.Write(header);

        var row = new byte[checked(layout.Width * 4)];
        var sourceRowLength = checked(layout.SourceWidth * 4);
        for (var y = 0; y < layout.SourceHeight; y++)
        {
            Marshal.Copy(bitmap.GetPixels() + y * bitmap.RowBytes, row, 0, sourceRowLength);
            destination.Write(row);
        }
        if (layout.SourceHeight < layout.Height)
            Array.Clear(row);
        for (var y = layout.SourceHeight; y < layout.Height; y++)
            destination.Write(row);

        return layout;
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

    private static PngTgaLayout CreateLayout(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("PNG dimensions must be positive.");
        if (width > LargestPowerOfTwoTgaDimension || height > LargestPowerOfTwoTgaDimension)
            throw new InvalidDataException(
                $"PNG dimensions {width}x{height} cannot be padded to power-of-two dimensions within the TGA 16-bit dimension fields.");
        var paddedWidth = NextPowerOfTwo(width);
        var paddedHeight = NextPowerOfTwo(height);
        return new(width, height, paddedWidth, paddedHeight);
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
            result = checked(result * 2);
        return result;
    }
}
