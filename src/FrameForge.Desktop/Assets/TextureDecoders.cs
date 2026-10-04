namespace FrameForge.Desktop.Assets;

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
        var available = decoders?.ToArray() ?? [new TgaTextureDecoder(), new BlpTextureDecoder()];
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
