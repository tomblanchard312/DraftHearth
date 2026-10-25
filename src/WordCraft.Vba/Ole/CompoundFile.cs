using System.Buffers.Binary;
using System.Text;

namespace WordCraft.Vba.Ole;

public sealed class CompoundFileException(string message) : Exception(message);

/// <summary>A storage or stream inside a compound file.</summary>
public sealed class CfbEntry
{
    public required string Name { get; init; }
    public required bool IsStorage { get; init; }
    public List<CfbEntry> Children { get; } = [];
    internal uint StartSector { get; init; }
    internal long Size { get; init; }

    public CfbEntry? Child(string name) => Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public override string ToString() => IsStorage ? Name + "/" : Name;
}

/// <summary>
/// Reads OLE compound files ([MS-CFB]): the container of vbaProject.bin and of legacy .doc files.
/// The input is treated as hostile: every sector index, chain and size is checked.
/// </summary>
public sealed class CompoundFileReader
{
    const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF, MaxRegSect = 0xFFFFFFFA, NoStream = 0xFFFFFFFF;
    const long MaxStreamSize = 256L * 1024 * 1024;
    static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    readonly byte[] _data;
    readonly int _sectorSize;
    readonly int _miniSectorSize;
    readonly uint _miniCutoff;
    readonly uint[] _fat;
    readonly uint[] _miniFat;
    readonly byte[] _miniStream;

    public CfbEntry Root { get; }

    public static bool IsCompoundFile(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    public CompoundFileReader(byte[] data)
    {
        if (data.Length < 512 || !IsCompoundFile(data)) throw new CompoundFileException("Not a compound file.");
        _data = data;
        int sectorShift = U16(0x1E), miniShift = U16(0x20);
        if (sectorShift is not (9 or 12) || miniShift != 6) throw new CompoundFileException("Unsupported sector size.");
        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniShift;
        _miniCutoff = U32(0x38);

        // DIFAT: 109 entries in the header, then a chain of DIFAT sectors.
        var difat = new List<uint>();
        for (int i = 0; i < 109; i++) difat.Add(U32(0x4C + i * 4));
        uint next = U32(0x44);
        int perSector = _sectorSize / 4 - 1, guard = 0;
        while (next <= MaxRegSect && guard++ < 100_000)
        {
            int off = SectorOffset(next);
            for (int i = 0; i < perSector; i++) difat.Add(U32(off + i * 4));
            next = U32(off + perSector * 4);
        }
        var fat = new List<uint>();
        foreach (var s in difat.Where(s => s <= MaxRegSect))
        {
            int off = SectorOffset(s);
            for (int i = 0; i < _sectorSize / 4; i++) fat.Add(U32(off + i * 4));
        }
        _fat = fat.ToArray();

        var directory = ReadChain(U32(0x30), long.MaxValue);
        var miniFatBytes = U32(0x3C) <= MaxRegSect ? ReadChain(U32(0x3C), long.MaxValue) : [];
        _miniFat = new uint[miniFatBytes.Length / 4];
        for (int i = 0; i < _miniFat.Length; i++) _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4));

        var entries = ParseDirectory(directory);
        if (entries.Count == 0 || entries[0].Type != 5) throw new CompoundFileException("Missing root entry.");
        _miniStream = entries[0].Start <= MaxRegSect ? ReadChain(entries[0].Start, entries[0].Size) : [];
        Root = Build(entries, 0, 0);
    }

    public static CompoundFileReader Open(string path) => new(File.ReadAllBytes(path));

    /// <summary>Finds an entry by a path such as "VBA/dir" (case-insensitive, as compound files are).</summary>
    public CfbEntry? Find(string path)
    {
        var e = Root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            e = e?.Child(part);
            if (e is null) return null;
        }
        return e;
    }

    public byte[] Read(CfbEntry stream)
    {
        if (stream.IsStorage) throw new CompoundFileException($"{stream.Name} is a storage, not a stream.");
        if (stream.Size > MaxStreamSize) throw new CompoundFileException($"Stream {stream.Name} is too large.");
        if (stream.Size == 0) return [];
        return stream.Size < _miniCutoff ? ReadMiniChain(stream.StartSector, stream.Size) : ReadChain(stream.StartSector, stream.Size);
    }

    public byte[] Read(string path) => Read(Find(path) ?? throw new CompoundFileException($"No stream {path}."));

    readonly record struct RawEntry(string Name, byte Type, uint Left, uint Right, uint Child, uint Start, long Size);

    List<RawEntry> ParseDirectory(byte[] dir)
    {
        var list = new List<RawEntry>();
        bool v3 = _sectorSize == 512;
        for (int off = 0; off + 128 <= dir.Length; off += 128)
        {
            var e = dir.AsSpan(off, 128);
            int nameLen = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(e[64..]), 0, 64);
            string name = Encoding.Unicode.GetString(e[..Math.Max(0, nameLen - 2)]);
            long size = v3 ? BinaryPrimitives.ReadUInt32LittleEndian(e[120..]) : (long)BinaryPrimitives.ReadUInt64LittleEndian(e[120..]);
            list.Add(new RawEntry(name, e[66],
                BinaryPrimitives.ReadUInt32LittleEndian(e[68..]), BinaryPrimitives.ReadUInt32LittleEndian(e[72..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[76..]), BinaryPrimitives.ReadUInt32LittleEndian(e[116..]), size));
        }
        return list;
    }

    CfbEntry Build(List<RawEntry> entries, int index, int depth)
    {
        if (depth > 64) throw new CompoundFileException("Directory too deep.");
        var raw = entries[index];
        var entry = new CfbEntry { Name = raw.Name, IsStorage = raw.Type is 1 or 5, StartSector = raw.Start, Size = raw.Size };
        if (!entry.IsStorage || raw.Child == NoStream) return entry;
        // The children of a storage form a tree through left/right sibling links.
        var seen = new HashSet<uint>();
        var stack = new Stack<uint>();
        stack.Push(raw.Child);
        while (stack.Count > 0)
        {
            uint i = stack.Pop();
            if (i == NoStream || i >= entries.Count || !seen.Add(i)) continue;
            var c = entries[(int)i];
            if (c.Type is 1 or 2) entry.Children.Add(Build(entries, (int)i, depth + 1));
            stack.Push(c.Left);
            stack.Push(c.Right);
        }
        return entry;
    }

    byte[] ReadChain(uint start, long size)
    {
        using var ms = new MemoryStream();
        uint s = start;
        long guard = 0;
        while (s <= MaxRegSect)
        {
            if (++guard > _fat.Length + 1) throw new CompoundFileException("Sector chain loops.");
            int off = SectorOffset(s);
            ms.Write(_data, off, Math.Min(_sectorSize, _data.Length - off));
            if (ms.Length >= size) break;
            if (s >= _fat.Length) throw new CompoundFileException("Sector outside the FAT.");
            s = _fat[s];
        }
        if (s != EndOfChain && s > MaxRegSect && s != FreeSect && ms.Length < size) throw new CompoundFileException("Broken sector chain.");
        var bytes = ms.ToArray();
        return size < bytes.Length ? bytes[..(int)size] : bytes;
    }

    byte[] ReadMiniChain(uint start, long size)
    {
        var buf = new byte[size];
        long pos = 0, guard = 0;
        uint s = start;
        while (pos < size && s <= MaxRegSect)
        {
            if (++guard > _miniFat.Length + 1) throw new CompoundFileException("Mini sector chain loops.");
            long off = (long)s * _miniSectorSize;
            if (off >= _miniStream.Length) throw new CompoundFileException("Mini sector outside the mini stream.");
            int n = (int)Math.Min(Math.Min(_miniSectorSize, size - pos), _miniStream.Length - off);
            Array.Copy(_miniStream, off, buf, pos, n);
            pos += n;
            if (s >= _miniFat.Length) break;
            s = _miniFat[s];
        }
        if (pos < size) throw new CompoundFileException("Truncated mini stream.");
        return buf;
    }

    int SectorOffset(uint sector)
    {
        long off = ((long)sector + 1) * _sectorSize;
        if (off + _sectorSize > _data.Length && off >= _data.Length) throw new CompoundFileException($"Sector {sector} is outside the file.");
        return (int)off;
    }

    ushort U16(int off) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(off));
    uint U32(int off) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off));
}

/// <summary>
/// Writes version 3 compound files (512-byte sectors, 64-byte mini sectors). Small streams go to
/// the mini stream as [MS-CFB] requires. Used to build vbaProject.bin.
/// </summary>
public sealed class CompoundFileWriter
{
    const int SectorSize = 512, MiniSectorSize = 64, MiniCutoff = 4096;
    const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF, FatSect = 0xFFFFFFFD, DifSect = 0xFFFFFFFC, NoStream = 0xFFFFFFFF;

    sealed class Node(string name, byte[]? data)
    {
        public string Name { get; } = name;
        public byte[]? Data { get; } = data; // null for a storage
        public List<Node> Children { get; } = [];
        public int Id;
        public uint Start = EndOfChain;
        public uint Left = NoStream, Right = NoStream, Child = NoStream;
        public byte Color = 1; // black
    }

    /// <summary>
    /// Siblings must form a red-black tree ordered by name length, then upper-cased name. A tree
    /// built from the sorted list's midpoints has leaves on its last two levels; colouring the
    /// deepest level red and the rest black satisfies every red-black rule.
    /// </summary>
    static void LinkChildren(Node parent)
    {
        var sorted = parent.Children
            .OrderBy(c => c.Name.Length)
            .ThenBy(c => c.Name.ToUpperInvariant(), StringComparer.Ordinal)
            .ToList();
        if (sorted.Count == 0) return;
        int maxDepth = 0;
        var depth = new Dictionary<Node, int>();
        Node? Build(int lo, int hi, int d)
        {
            if (lo > hi) return null;
            int mid = (lo + hi + 1) / 2;
            var n = sorted[mid];
            depth[n] = d;
            maxDepth = Math.Max(maxDepth, d);
            n.Left = Build(lo, mid - 1, d + 1)?.Id is int l ? (uint)l : NoStream;
            n.Right = Build(mid + 1, hi, d + 1)?.Id is int r ? (uint)r : NoStream;
            return n;
        }
        var root = Build(0, sorted.Count - 1, 0)!;
        foreach (var n in sorted) n.Color = maxDepth > 0 && depth[n] == maxDepth ? (byte)0 : (byte)1;
        parent.Child = (uint)root.Id;
    }

    readonly Node _root = new("Root Entry", null);

    /// <summary>Adds a stream at a path like "VBA/dir"; storages along the way are created.</summary>
    public void AddStream(string path, byte[] data)
    {
        var parts = path.Split('/');
        var node = _root;
        foreach (var storage in parts[..^1])
        {
            var next = node.Children.FirstOrDefault(c => c.Data is null && c.Name.Equals(storage, StringComparison.OrdinalIgnoreCase));
            if (next is null) { next = new Node(storage, null); node.Children.Add(next); }
            node = next;
        }
        node.Children.Add(new Node(parts[^1], data));
    }

    public byte[] ToArray()
    {
        var all = new List<Node>();
        void Number(Node n) { n.Id = all.Count; all.Add(n); foreach (var c in n.Children) Number(c); }
        Number(_root);
        foreach (var n in all.Where(n => n.Data is null)) LinkChildren(n);

        // Mini stream: streams under the cutoff, each padded to 64 bytes.
        var mini = new MemoryStream();
        var miniFat = new List<uint>();
        foreach (var n in all.Where(n => n.Data is { Length: > 0 and < MiniCutoff }))
        {
            n.Start = (uint)miniFat.Count;
            int count = (n.Data!.Length + MiniSectorSize - 1) / MiniSectorSize;
            for (int i = 0; i < count; i++) miniFat.Add(i == count - 1 ? EndOfChain : (uint)(miniFat.Count + 1));
            mini.Write(n.Data);
            mini.Write(new byte[count * MiniSectorSize - n.Data.Length]);
        }

        var sectors = new List<byte[]>();
        var fat = new List<uint>();
        uint Place(byte[] bytes)
        {
            if (bytes.Length == 0) return EndOfChain;
            uint first = (uint)sectors.Count;
            int count = (bytes.Length + SectorSize - 1) / SectorSize;
            for (int i = 0; i < count; i++)
            {
                var s = new byte[SectorSize];
                Array.Copy(bytes, i * SectorSize, s, 0, Math.Min(SectorSize, bytes.Length - i * SectorSize));
                sectors.Add(s);
                fat.Add(i == count - 1 ? EndOfChain : (uint)sectors.Count);
            }
            return first;
        }

        foreach (var n in all.Where(n => n.Data is { Length: >= MiniCutoff })) n.Start = Place(n.Data!);
        _root.Start = Place(mini.ToArray());
        var miniFatBytes = new byte[miniFat.Count * 4];
        for (int i = 0; i < miniFat.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(i * 4), miniFat[i]);
        uint miniFatStart = Place(miniFatBytes);
        int miniFatSectors = (miniFatBytes.Length + SectorSize - 1) / SectorSize;

        var dir = new byte[((all.Count + 3) / 4) * 4 * 128];
        foreach (var n in all) WriteEntry(dir.AsSpan(n.Id * 128, 128), n, (int)mini.Length);
        for (int i = all.Count; i < dir.Length / 128; i++) EmptyEntry(dir.AsSpan(i * 128, 128));
        uint dirStart = Place(dir);

        // FAT sectors describe every sector including themselves (128 entries each; ≤109 needs no DIFAT chain).
        int fatSectors = 1;
        while ((sectors.Count + fatSectors + 127) / 128 > fatSectors) fatSectors++;
        if (fatSectors > 109) throw new CompoundFileException("Project too large for this writer.");
        uint fatStart = (uint)sectors.Count;
        for (int i = 0; i < fatSectors; i++) fat.Add(FatSect);
        var fatBytes = new byte[fatSectors * SectorSize];
        for (int i = 0; i < fatBytes.Length / 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(fatBytes.AsSpan(i * 4), i < fat.Count ? fat[i] : FreeSect);
        for (int i = 0; i < fatSectors; i++) sectors.Add(fatBytes[(i * SectorSize)..((i + 1) * SectorSize)]);

        var header = new byte[SectorSize];
        byte[] sig = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        sig.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x18), 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1A), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1C), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x20), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x2C), (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x30), dirStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38), MiniCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x3C), miniFatSectors == 0 ? EndOfChain : miniFatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x40), (uint)miniFatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x44), EndOfChain);
        for (int i = 0; i < 109; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C + i * 4), i < fatSectors ? fatStart + (uint)i : FreeSect);

        var output = new MemoryStream();
        output.Write(header);
        foreach (var s in sectors) output.Write(s);
        return output.ToArray();
    }

    static void WriteEntry(Span<byte> e, Node n, int miniLength)
    {
        var name = Encoding.Unicode.GetBytes(n.Name);
        if (name.Length > 62) throw new CompoundFileException($"Name too long: {n.Name}");
        name.CopyTo(e);
        BinaryPrimitives.WriteUInt16LittleEndian(e[64..], (ushort)(name.Length + 2));
        e[66] = n.Data is null ? (n.Id == 0 ? (byte)5 : (byte)1) : (byte)2;
        e[67] = n.Color;
        BinaryPrimitives.WriteUInt32LittleEndian(e[68..], n.Left);
        BinaryPrimitives.WriteUInt32LittleEndian(e[72..], n.Right);
        BinaryPrimitives.WriteUInt32LittleEndian(e[76..], n.Child);
        BinaryPrimitives.WriteUInt32LittleEndian(e[116..], n.Start);
        uint size = n.Data is null ? (n.Id == 0 ? (uint)miniLength : 0) : (uint)n.Data.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(e[120..], size);
    }

    static void EmptyEntry(Span<byte> e)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(e[68..], NoStream);
        BinaryPrimitives.WriteUInt32LittleEndian(e[72..], NoStream);
        BinaryPrimitives.WriteUInt32LittleEndian(e[76..], NoStream);
    }
}
