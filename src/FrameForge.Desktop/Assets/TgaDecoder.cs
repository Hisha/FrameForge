using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FrameForge.Core.Models;

namespace FrameForge.Desktop.Assets;

/// <summary>A decoded, top-left-origin image in straight BGRA byte order.</summary>
public sealed record DecodedImageData(
    int Width,
    int Height,
    byte[] Bgra,
    string Description = "TGA type 2 true-color",
    TextureFileFormat Format = TextureFileFormat.Tga);

/// <summary>Decodes the uncompressed true-colour TGA variant used by Native Hunts.</summary>
public static class TgaDecoder
{
    public static DecodedImageData Decode(Stream stream)
    {
        Span<byte> header = stackalloc byte[18];
        ReadExactly(stream, header);

        var idLength = header[0];
        var colorMapType = header[1];
        var imageType = header[2];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(header[12..14]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(header[14..16]);
        var bits = header[16];
        var descriptor = header[17];

        if (colorMapType != 0)
            throw new NotSupportedException("Color-mapped TGA files are not supported.");
        if (imageType != 2)
            throw new NotSupportedException($"TGA image type {imageType} is not supported; expected uncompressed true-colour type 2.");
        if (bits is not (24 or 32))
            throw new NotSupportedException($"{bits}-bit TGA files are not supported; expected 24- or 32-bit true colour.");
        if (width == 0 || height == 0)
            throw new InvalidDataException("TGA dimensions must be non-zero.");

        if (idLength > 0)
        {
            var skipped = new byte[idLength];
            ReadExactly(stream, skipped);
        }

        var bytesPerPixel = bits / 8;
        var source = new byte[checked(width * height * bytesPerPixel)];
        ReadExactly(stream, source);
        var target = new byte[checked(width * height * 4)];
        var topOrigin = (descriptor & 0x20) != 0;
        var rightOrigin = (descriptor & 0x10) != 0;

        for (var sourceY = 0; sourceY < height; sourceY++)
        {
            var y = topOrigin ? sourceY : height - 1 - sourceY;
            for (var sourceX = 0; sourceX < width; sourceX++)
            {
                var x = rightOrigin ? width - 1 - sourceX : sourceX;
                var input = (sourceY * width + sourceX) * bytesPerPixel;
                var output = (y * width + x) * 4;
                target[output] = source[input];
                target[output + 1] = source[input + 1];
                target[output + 2] = source[input + 2];
                target[output + 3] = bytesPerPixel == 4 ? source[input + 3] : (byte)255;
            }
        }

        return new DecodedImageData(width, height, target);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0)
                throw new EndOfStreamException("The TGA file ended before its declared pixel data was complete.");
            read += count;
        }
    }
}

/// <summary>Creates Avalonia bitmaps without changing the cached straight-alpha source pixels.</summary>
public sealed class DecodedTexture : IDisposable
{
    private readonly Dictionary<(byte R, byte G, byte B), Bitmap> _variants = [];
    private Bitmap? _bitmap;

    public DecodedTexture(DecodedImageData image)
    {
        Image = image;
    }

    public DecodedImageData Image { get; }
    public Bitmap Bitmap => _bitmap ??= CreateBitmap(Image, ColorRgba.White);

    public Bitmap BitmapFor(ColorRgba? tint)
    {
        if (tint is null || (tint.Value.R == 1 && tint.Value.G == 1 && tint.Value.B == 1))
            return Bitmap;

        var key = ((byte)Math.Round(Math.Clamp(tint.Value.R, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(tint.Value.G, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(tint.Value.B, 0, 1) * 255));
        if (!_variants.TryGetValue(key, out var bitmap))
        {
            bitmap = CreateBitmap(Image, new ColorRgba(key.Item1 / 255d, key.Item2 / 255d, key.Item3 / 255d));
            _variants[key] = bitmap;
        }

        return bitmap;
    }

    public static byte[] TransformToPremultiplied(DecodedImageData image, ColorRgba tint)
    {
        var output = new byte[image.Bgra.Length];
        for (var i = 0; i < image.Bgra.Length; i += 4)
        {
            var alpha = image.Bgra[i + 3];
            output[i] = Multiply(image.Bgra[i], tint.B, alpha);
            output[i + 1] = Multiply(image.Bgra[i + 1], tint.G, alpha);
            output[i + 2] = Multiply(image.Bgra[i + 2], tint.R, alpha);
            output[i + 3] = alpha;
        }
        return output;
    }

    private static byte Multiply(byte channel, double tint, byte alpha) =>
        (byte)Math.Round(channel * Math.Clamp(tint, 0, 1) * alpha / 255d);

    private static Bitmap CreateBitmap(DecodedImageData image, ColorRgba tint)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        var pixels = TransformToPremultiplied(image, tint);
        using var buffer = bitmap.Lock();
        for (var row = 0; row < image.Height; row++)
            Marshal.Copy(pixels, row * image.Width * 4, buffer.Address + row * buffer.RowBytes, image.Width * 4);
        return bitmap;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        foreach (var bitmap in _variants.Values)
            bitmap.Dispose();
        _variants.Clear();
    }
}
