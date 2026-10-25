namespace WordCraft.Vba.Ole;

/// <summary>The run-length compression of [MS-OVBA] 2.4.1, used for the dir stream and module source.</summary>
public static class OvbaCompression
{
    const int ChunkSize = 4096;
    const int MaxOutput = 64 * 1024 * 1024;

    public static byte[] Decompress(ReadOnlySpan<byte> container)
    {
        if (container.Length == 0 || container[0] != 0x01) throw new CompoundFileException("Not a compressed VBA container.");
        var output = new List<byte>(container.Length * 3);
        int pos = 1;
        while (pos + 2 <= container.Length)
        {
            int header = container[pos] | (container[pos + 1] << 8);
            int chunkEnd = Math.Min(container.Length, pos + (header & 0x0FFF) + 3);
            bool compressed = (header & 0x8000) != 0;
            pos += 2;
            int chunkStart = output.Count;
            if (!compressed)
            {
                for (int i = 0; i < ChunkSize && pos < container.Length; i++) output.Add(container[pos++]);
                continue;
            }
            while (pos < chunkEnd)
            {
                byte flags = container[pos++];
                for (int bit = 0; bit < 8 && pos < chunkEnd; bit++)
                {
                    if ((flags & (1 << bit)) == 0)
                    {
                        output.Add(container[pos++]);
                        continue;
                    }
                    if (pos + 2 > chunkEnd) throw new CompoundFileException("Truncated copy token.");
                    int token = container[pos] | (container[pos + 1] << 8);
                    pos += 2;
                    var (lengthMask, offsetMask, bitCount) = CopyTokenHelp(output.Count - chunkStart);
                    int length = (token & lengthMask) + 3;
                    int offset = ((token & offsetMask) >> (16 - bitCount)) + 1;
                    int from = output.Count - offset;
                    if (from < chunkStart) throw new CompoundFileException("Copy token reaches before its chunk.");
                    for (int i = 0; i < length; i++) output.Add(output[from + i]);
                    if (output.Count > MaxOutput) throw new CompoundFileException("Decompressed data too large.");
                }
            }
        }
        return output.ToArray();
    }

    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length) { 0x01 };
        for (int start = 0; start < data.Length; start += ChunkSize)
        {
            int end = Math.Min(data.Length, start + ChunkSize);
            var chunk = CompressChunk(data, start, end);
            if (chunk is null) // incompressible: a raw chunk holds 4096 bytes, padded with zeros ([MS-OVBA] 2.4.1.3.10)
            {
                output.Add(0xFF);
                output.Add(0x3F);
                for (int i = start; i < start + ChunkSize; i++) output.Add(i < end ? data[i] : (byte)0);
            }
            else output.AddRange(chunk);
        }
        return output.ToArray();
    }

    static byte[]? CompressChunk(ReadOnlySpan<byte> data, int start, int end)
    {
        var body = new List<byte>(ChunkSize);
        int pos = start;
        while (pos < end)
        {
            int flagIndex = body.Count;
            body.Add(0);
            byte flags = 0;
            for (int bit = 0; bit < 8 && pos < end; bit++)
            {
                var (offset, length) = Match(data, start, pos, end);
                if (length >= 3)
                {
                    var (_, _, bitCount) = CopyTokenHelp(pos - start);
                    int token = ((offset - 1) << (16 - bitCount)) | (length - 3);
                    body.Add((byte)token);
                    body.Add((byte)(token >> 8));
                    flags |= (byte)(1 << bit);
                    pos += length;
                }
                else body.Add(data[pos++]);
            }
            body[flagIndex] = flags;
        }
        if (body.Count > ChunkSize) return null;
        int header = (body.Count + 2 - 3) | 0x3000 | 0x8000;
        var chunk = new byte[body.Count + 2];
        chunk[0] = (byte)header;
        chunk[1] = (byte)(header >> 8);
        body.CopyTo(chunk, 2);
        return chunk;
    }

    /// <summary>The longest earlier match in the chunk, limited by how many bits the offset needs.</summary>
    static (int Offset, int Length) Match(ReadOnlySpan<byte> data, int chunkStart, int pos, int end)
    {
        var (lengthMask, _, _) = CopyTokenHelp(pos - chunkStart);
        int maxLength = lengthMask + 3;
        int bestLen = 0, bestOffset = 0;
        for (int candidate = pos - 1; candidate >= chunkStart; candidate--)
        {
            int len = 0;
            while (pos + len < end && len < maxLength && data[candidate + len] == data[pos + len]) len++;
            if (len > bestLen) { bestLen = len; bestOffset = pos - candidate; }
        }
        return (bestOffset, bestLen);
    }

    static (int LengthMask, int OffsetMask, int BitCount) CopyTokenHelp(int difference)
    {
        int bitCount = 4;
        while ((1 << bitCount) < difference) bitCount++;
        bitCount = Math.Min(bitCount, 12);
        int lengthMask = 0xFFFF >> bitCount;
        return (lengthMask, ~lengthMask & 0xFFFF, bitCount);
    }
}
