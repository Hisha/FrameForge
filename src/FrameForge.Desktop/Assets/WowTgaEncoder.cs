using System.Buffers.Binary;

namespace FrameForge.Desktop.Assets;

/// <summary>Writes a deterministic, uncompressed 32-bit top-origin TGA accepted by the v2 exporter.</summary>
public static class WowTgaEncoder
{
    public static void Write(Stream destination, DecodedImageData image)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width is <= 0 or > ushort.MaxValue || image.Height is <= 0 or > ushort.MaxValue)
            throw new InvalidDataException("TGA dimensions must be from 1 through 65535 pixels.");
        if (image.Bgra.Length != checked(image.Width * image.Height * 4))
            throw new InvalidDataException("Decoded BGRA data does not match its dimensions.");
        Span<byte> header = stackalloc byte[18];
        header[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..14], (ushort)image.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..16], (ushort)image.Height);
        header[16] = 32;
        header[17] = 0x28; // top origin, eight alpha bits
        destination.Write(header);
        destination.Write(image.Bgra);
    }
}
