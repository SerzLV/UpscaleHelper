using System.Buffers.Binary;

namespace UpscaleHelper.Processing;

internal sealed class PngFrameReader(Stream input)
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly byte[] header = new byte[8];
    private readonly byte[] buffer = new byte[64 * 1024];

    public async Task<bool> ReadAsync(string path, CancellationToken token)
    {
        int count = await input.ReadAtLeastAsync(header, header.Length, false, token);
        if (count == 0) return false;
        if (count != header.Length || !header.AsSpan().SequenceEqual(Signature))
            throw new InvalidDataException(T("InvalidPngFrame"));

        await using var output = File.Create(path);
        await output.WriteAsync(header, token);
        bool lastChunk;
        do
        {
            await input.ReadExactlyAsync(header, token);
            long remaining = BinaryPrimitives.ReadUInt32BigEndian(header) + 4L;
            lastChunk = header.AsSpan(4).SequenceEqual("IEND"u8);
            await output.WriteAsync(header, token);
            while (remaining > 0)
            {
                int length = (int)Math.Min(remaining, buffer.Length);
                var block = buffer.AsMemory(0, length);
                await input.ReadExactlyAsync(block, token);
                await output.WriteAsync(block, token);
                remaining -= length;
            }
        } while (!lastChunk);
        return true;
    }
}
