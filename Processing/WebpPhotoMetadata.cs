using System.Buffers.Binary;

namespace UpscaleHelper.Processing;

internal static class WebpPhotoMetadata
{
    public static async Task WriteAsync(string encoded, string target, int width, int height,
        byte[]? icc, byte[]? exif, CancellationToken token)
    {
        await using var input = File.OpenRead(encoded);
        byte[] header = new byte[12];
        input.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8).SequenceEqual("WEBP"u8))
            throw new InvalidDataException(T("CompressionInvalidWebp"));

        byte[] chunk = new byte[8];
        input.ReadExactly(chunk);
        var extended = new byte[10];
        if (chunk.AsSpan(0, 4).SequenceEqual("VP8X"u8))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4)) != 10)
                throw new InvalidDataException(T("CompressionInvalidWebp"));
            input.ReadExactly(extended);
        }
        else
        {
            if (chunk.AsSpan(0, 4).SequenceEqual("VP8L"u8))
            {
                Span<byte> losslessHeader = stackalloc byte[5];
                input.ReadExactly(losslessHeader);
                if ((losslessHeader[4] & 0x10) != 0) extended[0] = 0x10;
            }
            input.Position = 12;
        }

        if (icc is not null) extended[0] |= 0x20;
        if (exif is not null) extended[0] |= 0x08;
        WriteSize(extended.AsSpan(4), width);
        WriteSize(extended.AsSpan(7), height);

        await using var output = File.Create(target);
        output.Write(header);
        WriteChunk(output, "VP8X"u8, extended);
        if (icc is not null) WriteChunk(output, "ICCP"u8, icc);
        await input.CopyToAsync(output, token);
        if (exif is not null) WriteChunk(output, "EXIF"u8, exif);
        output.Position = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, checked((uint)(output.Length - 8)));
        output.Write(chunk.AsSpan(0, 4));
    }

    private static void WriteSize(Span<byte> target, int value)
    {
        target[0] = (byte)(value - 1);
        target[1] = (byte)((value - 1) >> 8);
        target[2] = (byte)((value - 1) >> 16);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        output.Write(type);
        output.Write(size);
        output.Write(data);
        if (data.Length % 2 != 0) output.WriteByte(0);
    }
}
