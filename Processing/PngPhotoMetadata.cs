using System.Buffers.Binary;
using System.Text;

namespace UpscaleHelper.Processing;

internal static class PngPhotoMetadata
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly HashSet<string> Color = ["iCCP", "sRGB", "gAMA", "cHRM", "cICP"];
    private static readonly HashSet<string> Metadata = [.. Color, "eXIf", "iTXt", "tEXt", "zTXt", "pHYs"];
    private const int MaxMetadataBytes = 16 * 1024 * 1024;

    public static (int Width, int Height) Dimensions(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadDimensions(stream);
    }

    public static void Restore(string source, string result, CancellationToken token)
    {
        var (width, height) = Dimensions(result);
        var chunks = ReadMetadata(source, width, height, token);
        if (chunks.Count == 0) return;
        var replaced = chunks.Select(chunk => chunk.Type).ToHashSet(StringComparer.Ordinal);
        if (replaced.Overlaps(Color)) replaced.UnionWith(Color);
        string temporary = result + ".metadata";
        using (var input = File.OpenRead(result))
        using (var output = File.Create(temporary))
        {
            ReadSignature(input);
            output.Write(Signature);
            byte[] header = new byte[8];
            byte[] buffer = new byte[64 * 1024];
            bool inserted = false;
            string type;
            do
            {
                token.ThrowIfCancellationRequested();
                (type, int length) = ReadChunk(input, header);
                if (type == "IDAT" && !inserted)
                {
                    foreach (var chunk in chunks) WriteChunk(output, chunk);
                    inserted = true;
                }
                if (replaced.Contains(type)) input.Seek(length + 4L, SeekOrigin.Current);
                else
                {
                    output.Write(header);
                    CopyBytes(input, output, length + 4L, buffer, token);
                }
            } while (type != "IEND");
        }
        token.ThrowIfCancellationRequested();
        File.Move(temporary, result, overwrite: true);
    }

    private static List<(string Type, byte[] Data)> ReadMetadata(string path, int width, int height,
        CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        ReadSignature(stream);
        var chunks = new List<(string Type, byte[] Data)>();
        byte[] header = new byte[8];
        int remaining = MaxMetadataBytes;
        string type;
        do
        {
            token.ThrowIfCancellationRequested();
            (type, int length) = ReadChunk(stream, header);
            if (length > 0 && length <= remaining && Metadata.Contains(type))
            {
                byte[] data = new byte[length];
                stream.ReadExactly(data);
                stream.Seek(4, SeekOrigin.Current);
                remaining -= length;
                if (type != "eXIf" || NormalizeExif(data, width, height)) chunks.Add((type, data));
            }
            else stream.Seek(length + 4L, SeekOrigin.Current);
        } while (type != "IEND");
        if (chunks.Any(chunk => chunk.Type == "iCCP")) chunks.RemoveAll(chunk => chunk.Type == "sRGB");
        return chunks;
    }

    private static (int Width, int Height) ReadDimensions(Stream stream)
    {
        ReadSignature(stream);
        Span<byte> header = stackalloc byte[8];
        if (ReadChunk(stream, header) != ("IHDR", 13))
            throw new InvalidDataException(T("InvalidPngFrame"));
        Span<byte> size = stackalloc byte[8];
        stream.ReadExactly(size);
        int width = BinaryPrimitives.ReadInt32BigEndian(size);
        int height = BinaryPrimitives.ReadInt32BigEndian(size[4..]);
        if (width <= 0 || height <= 0) throw new InvalidDataException(T("InvalidPngFrame"));
        return (width, height);
    }

    private static void ReadSignature(Stream stream)
    {
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual(Signature)) throw new InvalidDataException(T("InvalidPngFrame"));
    }

    private static (string Type, int Length) ReadChunk(Stream stream, Span<byte> header)
    {
        stream.ReadExactly(header);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length + 4L > stream.Length - stream.Position)
            throw new InvalidDataException(T("InvalidPngFrame"));
        return (Encoding.ASCII.GetString(header[4..]), length);
    }

    private static void CopyBytes(Stream source, Stream target, long count, byte[] buffer, CancellationToken token)
    {
        while (count > 0)
        {
            token.ThrowIfCancellationRequested();
            int size = (int)Math.Min(buffer.Length, count);
            source.ReadExactly(buffer.AsSpan(0, size));
            target.Write(buffer, 0, size);
            count -= size;
        }
    }

    private static void WriteChunk(Stream output, (string Type, byte[] Data) chunk)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, chunk.Data.Length);
        output.Write(buffer);
        Encoding.ASCII.GetBytes(chunk.Type, buffer);
        output.Write(buffer);
        uint crc = UpdateCrc(uint.MaxValue, buffer);
        output.Write(chunk.Data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, ~UpdateCrc(crc, chunk.Data));
        output.Write(buffer);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return crc;
    }

    internal static bool NormalizeExif(byte[] data, int width, int height)
    {
        if (data.Length < 8) return false;
        bool little = data.AsSpan(0, 2).SequenceEqual("II"u8);
        if ((!little && !data.AsSpan(0, 2).SequenceEqual("MM"u8)) || ReadShort(2) != 42)
            return false;
        int main = (int)ReadLong(4);
        if (!ReadIfd(main, out int count)) return false;
        int photo = UpdateIfd(main, count);
        if (ReadIfd(photo, out int photoCount)) UpdateIfd(photo, photoCount);
        // The thumbnail IFD describes the old pixels and may carry a different orientation.
        data.AsSpan(main + 2 + 12 * count, 4).Clear();
        return true;

        bool ReadIfd(int offset, out int count)
        {
            count = 0;
            if (offset < 8 || offset > data.Length - 6) return false;
            count = ReadShort(offset);
            return 2L + 12L * count + 4 <= data.Length - offset;
        }

        int UpdateIfd(int offset, int count)
        {
            int photoOffset = 0;
            for (int index = 0; index < count; index++)
            {
                int entry = offset + 2 + 12 * index;
                ushort tag = ReadShort(entry);
                ushort format = ReadShort(entry + 2);
                if (ReadLong(entry + 4) != 1) continue;
                if (tag == 0x8769 && format is 4 or 13)
                {
                    photoOffset = (int)ReadLong(entry + 8);
                    continue;
                }
                int value = tag switch
                {
                    0x0112 => 1,
                    0x0100 or 0xa002 => width,
                    0x0101 or 0xa003 => height,
                    _ => -1
                };
                if (value < 0) continue;
                if (format == 3 && value <= ushort.MaxValue)
                {
                    if (little) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entry + 8), (ushort)value);
                    else BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(entry + 8), (ushort)value);
                }
                else if (format == 4)
                {
                    if (little) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entry + 8), (uint)value);
                    else BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entry + 8), (uint)value);
                }
            }
            return photoOffset;
        }

        ushort ReadShort(int offset) => little
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        uint ReadLong(int offset) => little
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
    }
}
