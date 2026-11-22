using System.Buffers.Binary;

namespace Seekbeam.Extraction.Formats;

/// <summary>
/// Minimal read-only Compound File Binary reader (MS-CFB): header, DIFAT (incl. extra DIFAT sectors), FAT, mini FAT,
/// mini stream and the root storage's children. Every sector index and chain is validated; chains are capped by the
/// number of sectors in the file, so corrupt files (loops, out-of-range indices) fail fast instead of hanging.
/// </summary>
internal sealed class CfbReader
{
    public static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const uint MaxRegSect = 0xFFFFFFFA;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSect = 0xFFFFFFFF;
    private const uint NoStream = 0xFFFFFFFF;

    public sealed record Entry(string Name, int Type, uint StartSector, long Size);

    private readonly Stream _s;
    private readonly long _base;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly int _sectorCount;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly List<uint> _miniStreamChain;
    private readonly long _maxStreamBytes;
    private readonly Dictionary<string, Entry> _root = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Entry> RootEntries => _root;

    public static bool HasSignature(Stream s, long position)
    {
        if (!s.CanSeek) return false;
        Span<byte> head = stackalloc byte[8];
        s.Position = position;
        int n = 0;
        while (n < 8) { int r = s.Read(head[n..]); if (r <= 0) break; n += r; }
        s.Position = position;
        return n == 8 && head.SequenceEqual(Signature);
    }

    /// <param name="s">Seekable stream positioned at the start of the compound file.</param>
    /// <param name="maxStreamBytes">Upper bound for any stream returned by <see cref="ReadStream"/>.</param>
    public CfbReader(Stream s, long maxStreamBytes)
    {
        if (!s.CanSeek) throw new NotSupportedException("CFB requires a seekable stream");
        _s = s;
        _base = s.Position;
        _maxStreamBytes = maxStreamBytes;
        long length = s.Length - _base;

        byte[] h = new byte[512];
        ReadAt(0, h);
        if (!h.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("not a compound file");
        int sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(0x1E));
        int miniShift = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(0x20));
        if (sectorShift is not (9 or 12) || miniShift != 6) throw new InvalidDataException("bad sector shift");
        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniShift;
        uint numFat = U32(h, 0x2C);
        uint firstDir = U32(h, 0x30);
        _miniCutoff = U32(h, 0x38);
        if (_miniCutoff != 4096) _miniCutoff = 4096;
        uint firstMiniFat = U32(h, 0x3C);
        uint numMiniFat = U32(h, 0x40);
        uint firstDifat = U32(h, 0x44);
        uint numDifat = U32(h, 0x48);

        // Sector n starts at (n + 1) * sectorSize; a truncated last sector is still counted (reads are bounds-checked).
        long sectors = (length - _sectorSize + _sectorSize - 1) / _sectorSize;
        if (sectors <= 0) throw new InvalidDataException("empty compound file");
        _sectorCount = (int)Math.Min(sectors, int.MaxValue / 2);
        if (numFat > (uint)_sectorCount) throw new InvalidDataException("FAT sector count exceeds file size");

        // DIFAT: 109 entries in the header, then a chain of DIFAT sectors (each: n-1 entries + next sector index).
        var fatSectors = new List<uint>((int)numFat);
        for (int i = 0; i < 109 && fatSectors.Count < numFat; i++)
        {
            uint sid = U32(h, 0x4C + i * 4);
            if (sid > MaxRegSect) break;
            fatSectors.Add(sid);
        }
        int perSector = _sectorSize / 4;
        byte[] sec = new byte[_sectorSize];
        uint difat = firstDifat;
        for (uint d = 0; d < numDifat && difat <= MaxRegSect && fatSectors.Count < numFat; d++)
        {
            if (d > (uint)_sectorCount) throw new InvalidDataException("DIFAT loop");
            ReadSector(difat, sec);
            for (int i = 0; i < perSector - 1 && fatSectors.Count < numFat; i++)
            {
                uint sid = U32(sec, i * 4);
                if (sid <= MaxRegSect) fatSectors.Add(sid);
            }
            difat = U32(sec, (perSector - 1) * 4);
        }

        // FAT entries beyond the number of sectors in the file are irrelevant (any reference to them is invalid).
        _fat = new uint[Math.Min((long)fatSectors.Count * perSector, _sectorCount)];
        int pos = 0;
        foreach (uint fs in fatSectors)
        {
            if (pos >= _fat.Length) break;
            ReadSector(fs, sec);
            for (int i = 0; i < perSector && pos < _fat.Length; i++) _fat[pos++] = U32(sec, i * 4);
        }

        // Mini FAT.
        var miniFatSectors = Chain(firstMiniFat, long.MaxValue);
        if (miniFatSectors.Count > numMiniFat && numMiniFat > 0) miniFatSectors.RemoveRange((int)numMiniFat, miniFatSectors.Count - (int)numMiniFat);
        _miniFat = new uint[miniFatSectors.Count * perSector];
        pos = 0;
        foreach (uint ms in miniFatSectors)
        {
            ReadSector(ms, sec);
            for (int i = 0; i < perSector; i++) _miniFat[pos++] = U32(sec, i * 4);
        }

        // Directory.
        var dirSectors = Chain(firstDir, long.MaxValue);
        if (dirSectors.Count == 0) throw new InvalidDataException("no directory");
        int entriesPerSector = _sectorSize / 128;
        var raw = new byte[dirSectors.Count * _sectorSize];
        for (int i = 0; i < dirSectors.Count; i++) ReadSector(dirSectors[i], raw.AsSpan(i * _sectorSize, _sectorSize));
        int count = dirSectors.Count * entriesPerSector;

        Entry Parse(int i, out uint left, out uint right, out uint child)
        {
            var e = raw.AsSpan(i * 128, 128);
            int nameLen = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(e[0x40..]), 0, 64);
            int chars = Math.Max(0, nameLen / 2 - 1); // length includes the terminating NUL
            string name = new(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(e[..(chars * 2)]));
            int type = e[0x42];
            left = U32(e, 0x44); right = U32(e, 0x48); child = U32(e, 0x4C);
            uint start = U32(e, 0x74);
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[0x78..]);
            if (_sectorSize == 512) size &= 0xFFFFFFFF; // v3: the high 32 bits are undefined
            return new Entry(name, type, start, size);
        }

        var root = Parse(0, out _, out _, out uint rootChild);
        if (root.Type != 5) throw new InvalidDataException("bad root entry");
        _miniStreamChain = root.Size > 0 ? Chain(root.StartSector, root.Size) : [];

        // Flatten the red-black tree of the root's children (iterative, each entry visited once).
        var visited = new bool[count];
        var stack = new Stack<uint>();
        if (rootChild != NoStream) stack.Push(rootChild);
        while (stack.Count > 0)
        {
            uint id = stack.Pop();
            if (id >= count || visited[id]) continue;
            visited[id] = true;
            var en = Parse((int)id, out uint l, out uint r, out _);
            if (en.Type is 1 or 2) _root.TryAdd(en.Name, en);
            if (l != NoStream) stack.Push(l);
            if (r != NoStream) stack.Push(r);
        }
    }

    public bool HasStream(string name) => _root.TryGetValue(name, out var e) && e.Type == 2;

    /// <summary>Reads a root-level stream, or null when absent.</summary>
    /// <exception cref="ExtractionException">TooLarge when the stream exceeds the configured bound.</exception>
    public byte[]? ReadStream(string name)
    {
        if (!_root.TryGetValue(name, out var e) || e.Type != 2) return null;
        if (e.Size > _maxStreamBytes) throw new ExtractionException(ExtractionStatus.TooLarge, $"stream {name} too large");
        if (e.Size > (long)_sectorCount * _sectorSize) throw new InvalidDataException($"stream {name} larger than the file");
        var data = new byte[e.Size];
        if (e.Size == 0) return data;

        if (e.Size < _miniCutoff)
        {
            // Mini stream: 64-byte sectors chained through the mini FAT, stored inside the root entry's stream.
            uint sid = e.StartSector;
            long done = 0;
            int steps = 0;
            while (done < e.Size)
            {
                if (sid > MaxRegSect || sid >= _miniFat.Length || ++steps > _miniFat.Length) throw new InvalidDataException("bad mini FAT chain");
                long off = (long)sid * _miniSectorSize;
                int bigIndex = (int)(off / _sectorSize);
                if (bigIndex >= _miniStreamChain.Count) throw new InvalidDataException("mini sector outside mini stream");
                int n = (int)Math.Min(_miniSectorSize, e.Size - done);
                ReadAt(((long)_miniStreamChain[bigIndex] + 1) * _sectorSize + off % _sectorSize, data.AsSpan((int)done, n));
                done += n;
                sid = _miniFat[sid];
            }
        }
        else
        {
            var chain = Chain(e.StartSector, e.Size);
            long done = 0;
            foreach (uint sid in chain)
            {
                int n = (int)Math.Min(_sectorSize, e.Size - done);
                ReadAt(((long)sid + 1) * _sectorSize, data.AsSpan((int)done, n));
                done += n;
            }
            if (done < e.Size) throw new InvalidDataException("stream chain shorter than its size");
        }
        return data;
    }

    /// <summary>Follows a FAT chain; stops after enough sectors for <paramref name="size"/> bytes. Loops and bad indices throw.</summary>
    private List<uint> Chain(uint start, long size)
    {
        var list = new List<uint>();
        long needed = size == long.MaxValue ? long.MaxValue : (size + _sectorSize - 1) / _sectorSize;
        uint sid = start;
        while (sid <= MaxRegSect && list.Count < needed)
        {
            if (sid >= _fat.Length || list.Count >= _fat.Length) throw new InvalidDataException("bad FAT chain");
            list.Add(sid);
            sid = _fat[sid];
        }
        return list; // a chain ending early is detected by the caller (size check)
    }

    private void ReadSector(uint sid, Span<byte> buffer)
    {
        if (sid >= (uint)_sectorCount) throw new InvalidDataException("sector index out of range");
        ReadAt(((long)sid + 1) * _sectorSize, buffer[.._sectorSize]);
    }

    /// <summary>Reads exactly; bytes past the end of a truncated file read as zero.</summary>
    private void ReadAt(long offset, Span<byte> buffer)
    {
        _s.Position = _base + offset;
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _s.Read(buffer[total..]);
            if (n <= 0) break;
            total += n;
        }
        if (total == 0 && buffer.Length > 0 && offset >= _s.Length - _base) throw new InvalidDataException("read past end of file");
        buffer[total..].Clear();
    }

    private static uint U32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b[off..]);
}
