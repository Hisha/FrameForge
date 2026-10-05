namespace FrameForge.Desktop.Assets;

using SkiaSharp;

/// <summary>A physical texture decoder; path resolution and rendering are separate concerns.</summary>
public interface ITextureDecoder
{
    TextureFileFormat Format { get; }
    DecodedImageData Decode(Stream stream);
}

public sealed class UnsupportedTextureEncodingException(string message) : NotSupportedException(message);

/// <summary>Identifies physical bytes and dispatches to the smallest supported decoder.</summary>
public sealed class TextureDecoderRegistry
{
    private readonly IReadOnlyDictionary<TextureFileFormat, ITextureDecoder> _decoders;

    public TextureDecoderRegistry(IEnumerable<ITextureDecoder>? decoders = null)
    {
        var available = decoders?.ToArray() ?? [new TgaTextureDecoder(), new BlpTextureDecoder(), new PngTextureDecoder()];
        _decoders = available.ToDictionary(decoder => decoder.Format);
    }

    public TextureFileFormat Identify(Stream stream)
    {
        Span<byte> header = stackalloc byte[18];
        var start = stream.CanSeek ? stream.Position : 0;
        var length = stream.Read(header);
        if (stream.CanSeek)
            stream.Position = start;

        if (length >= 4 && header[..4].SequenceEqual("BLP2"u8))
            return TextureFileFormat.Blp;
        if (length >= 4 && header[..4].SequenceEqual("BLP1"u8))
            return TextureFileFormat.Blp;
        if (length >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return TextureFileFormat.Png;
        if (length >= 18 && header[1] == 0 && header[2] is 2 or 10)
            return TextureFileFormat.Tga;
        return TextureFileFormat.Unknown;
    }

    public DecodedImageData Decode(Stream stream, TextureFileFormat format)
    {
        if (!_decoders.TryGetValue(format, out var decoder))
            throw new UnsupportedTextureEncodingException($"{format} decoding is not supported.");
        if (stream.CanSeek)
            stream.Position = 0;
        return decoder.Decode(stream);
    }
}

public sealed class TgaTextureDecoder : ITextureDecoder
{
    public TextureFileFormat Format => TextureFileFormat.Tga;
    public DecodedImageData Decode(Stream stream) => TgaDecoder.Decode(stream);
}

/// <summary>Decodes ordinary PNG design-source artwork. PNG is not claimed as a WoW export format.</summary>
public sealed class PngTextureDecoder : ITextureDecoder
{
    public TextureFileFormat Format => TextureFileFormat.Png;

    public DecodedImageData Decode(Stream stream)
    {
        using var bitmap = SKBitmap.Decode(stream) ?? throw new InvalidDataException("PNG data could not be decoded.");
        if (bitmap.Width <= 0 || bitmap.Height <= 0)
            throw new InvalidDataException("PNG dimensions must be non-zero.");

        var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            var offset = (y * bitmap.Width + x) * 4;
            pixels[offset] = color.Blue;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Red;
            pixels[offset + 3] = color.Alpha;
        }
        return new DecodedImageData(bitmap.Width, bitmap.Height, pixels, "PNG design-source image", TextureFileFormat.Png);
    }
}
