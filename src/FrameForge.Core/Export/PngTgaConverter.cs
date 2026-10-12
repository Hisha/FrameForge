using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace FrameForge.Core.Export;

/// <summary>Converts PNG design artwork to the conservative TGA variant accepted by WoW 3.3.5a.</summary>
internal static class PngTgaConverter
{
    private const byte UncompressedTrueColor = 2;
    private const byte BitsPerPixel = 32;
    private const byte TopLeftWithEightAlphaBits = 0x28;

    public static (int Width, int Height) Validate(Stream source)
    {
        using var codec = CreateCodec(source);
        ValidateDimensions(codec.Info.Width, codec.Info.Height);
        return (codec.Info.Width, codec.Info.Height);
    }

    public static (int Width, int Height) Convert(Stream source, Stream destination)
    {
        using var codec = CreateCodec(source);
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        ValidateDimensions(width, height);

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
            throw new InvalidDataException($"PNG pixel data could not be decoded completely ({result}).");

        Span<byte> header = stackalloc byte[18];
        header.Clear();
        header[2] = UncompressedTrueColor;
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..14], checked((ushort)width));
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..16], checked((ushort)height));
        header[16] = BitsPerPixel;
        header[17] = TopLeftWithEightAlphaBits;
        destination.Write(header);

        var row = new byte[checked(width * 4)];
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(bitmap.GetPixels() + y * bitmap.RowBytes, row, 0, row.Length);
            destination.Write(row);
        }

        return (width, height);
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

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("PNG dimensions must be positive.");
        if (width > ushort.MaxValue || height > ushort.MaxValue)
            throw new InvalidDataException($"PNG dimensions {width}x{height} exceed the TGA 16-bit dimension fields.");
        if (!IsPowerOfTwo(width) || !IsPowerOfTwo(height))
            throw new InvalidDataException($"PNG dimensions {width}x{height} are not powers of two as required by WoW 3.3.5a UI textures.");
    }

    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;
}
