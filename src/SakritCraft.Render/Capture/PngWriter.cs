using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace SakritCraft.Render.Capture;

/// <summary>
/// Minimal PNG encoder for screenshots.
/// <para>
/// Written rather than taken from a library because the only alternatives on this platform
/// pull in a Windows-only imaging stack, and the format needed here is one chunk type, one
/// filter mode and a deflate stream the runtime already provides.
/// </para>
/// </summary>
public static class PngWriter
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>
    /// Writes 8-bit RGBA pixels, top row first, as a PNG.
    /// </summary>
    public static void WriteRgba(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("Pixel buffer is smaller than the stated image size.", nameof(rgba));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        file.Write(Signature);

        // IHDR: 8 bits per channel, truecolour with alpha, no interlacing.
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), height);
        header[8] = 8;    // bit depth
        header[9] = 6;    // colour type: RGBA
        header[10] = 0;   // deflate
        header[11] = 0;   // adaptive filtering
        header[12] = 0;   // no interlace
        WriteChunk(file, "IHDR", header);

        // Scanlines, each prefixed with its filter byte. Filter 0 (none) keeps this simple; the
        // deflate pass still compresses a screenshot to a fraction of its raw size.
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0;
            rgba.Slice(y * stride, stride).CopyTo(raw.AsSpan(y * (stride + 1) + 1, stride));
        }

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }
        WriteChunk(file, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));

        WriteChunk(file, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        stream.Write(typeBytes);
        stream.Write(data);

        // The CRC covers the type and the data, but not the length.
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc.GetCurrentHashAsUInt32());
        stream.Write(checksum);
    }
}
