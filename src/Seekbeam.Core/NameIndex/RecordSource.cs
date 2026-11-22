using System.Runtime.InteropServices;

namespace Seekbeam.Core.NameIndex;

[Flags]
public enum EntryFlags : byte { None = 0, Directory = 1, Hidden = 2, System = 4, Reparse = 8, Root = 16 }

/// <summary>A change to the Name Index, produced after the catalog transaction committed.</summary>
public readonly record struct NameChange(int Id, int Parent, EntryFlags Flags, string? Name, long Rev, bool Deleted);

/// <summary>
/// Columnar, id-sorted block of name records (immutable data + mutable staleness bitset).
/// Implemented by memory-mapped <see cref="Segment"/> and in-memory <see cref="DeltaChunk"/>.
/// </summary>
public abstract class RecordSource
{
    protected long[] StaleBits = [];
    private int _staleCount;

    public abstract int Count { get; }
    public abstract ReadOnlySpan<int> Ids { get; }
    public abstract ReadOnlySpan<int> Parents { get; }
    public abstract ReadOnlySpan<byte> Flags { get; }
    /// <summary>Count+1 offsets into <see cref="Text"/>. Record i spans [Offsets[i], Offsets[i+1]-1) followed by '\n'.</summary>
    public abstract ReadOnlySpan<int> Offsets { get; }
    public abstract ReadOnlySpan<byte> Text { get; }
    /// <summary>Names of directory records only ('\n'-separated): tells a search cheaply whether a token can match a path.</summary>
    public abstract ReadOnlySpan<byte> DirText { get; }

    public int MaxId => Count == 0 ? 0 : Ids[Count - 1];
    public int MinId => Count == 0 ? 0 : Ids[0];
    public int StaleCount => Volatile.Read(ref _staleCount);
    public int LiveCount => Count - StaleCount;

    protected void InitStale(int count) => StaleBits = new long[(count + 63) >> 6];

    public bool IsStale(int i) => (Volatile.Read(ref StaleBits[i >> 6]) & (1L << (i & 63))) != 0;

    public bool MarkStale(int i)
    {
        long bit = 1L << (i & 63);
        long prev = Interlocked.Or(ref StaleBits[i >> 6], bit);
        if ((prev & bit) != 0) return false;
        Interlocked.Increment(ref _staleCount);
        return true;
    }

    /// <summary>Ordinal of <paramref name="id"/> or -1.</summary>
    public int Find(int id)
    {
        if (Count == 0 || id < MinId || id > MaxId) return -1;
        int i = Ids.BinarySearch(id);
        return i >= 0 ? i : -1;
    }

    /// <summary>Marks the live copy of <paramref name="id"/> stale; returns true if one was found.</summary>
    public bool MarkStaleById(int id)
    {
        int i = Find(id);
        return i >= 0 && MarkStale(i);
    }

    public ReadOnlySpan<byte> NameAt(int i)
    {
        var o = Offsets;
        return Text.Slice(o[i], o[i + 1] - o[i] - 1);
    }
}

/// <summary>Immutable in-memory block of recent records (the delta), sorted by id.</summary>
public sealed class DeltaChunk : RecordSource
{
    private readonly int[] _ids, _parents, _offsets;
    private readonly byte[] _flags, _text, _dirText;

    public DeltaChunk(int[] ids, int[] parents, byte[] flags, int[] offsets, byte[] text)
    {
        _ids = ids; _parents = parents; _flags = flags; _offsets = offsets; _text = text;
        var dirs = new List<byte>();
        for (int i = 0; i < ids.Length; i++)
            if ((flags[i] & (byte)EntryFlags.Directory) != 0) dirs.AddRange(text.AsSpan(offsets[i], offsets[i + 1] - offsets[i]));
        _dirText = [.. dirs];
        InitStale(ids.Length);
    }

    public override int Count => _ids.Length;
    public override ReadOnlySpan<int> Ids => _ids;
    public override ReadOnlySpan<int> Parents => _parents;
    public override ReadOnlySpan<byte> Flags => _flags;
    public override ReadOnlySpan<int> Offsets => _offsets;
    public override ReadOnlySpan<byte> Text => _text;
    public override ReadOnlySpan<byte> DirText => _dirText;
    public long MemoryBytes => _ids.Length * 13L + _offsets.Length * 4L + _text.Length + _dirText.Length + StaleBits.Length * 8L;

    /// <summary>Builds a chunk from (id, parent, flags, normalized-name) tuples; input need not be sorted, last duplicate wins.</summary>
    public static DeltaChunk Build(List<(int Id, int Parent, byte Flags, byte[] Text)> items)
    {
        // Stable sort by id, keep last occurrence of each id.
        var idx = new int[items.Count];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        Array.Sort(idx, (a, b) => { int c = items[a].Id.CompareTo(items[b].Id); return c != 0 ? c : a.CompareTo(b); });
        var keep = new List<int>(idx.Length);
        for (int k = 0; k < idx.Length; k++)
        {
            if (k + 1 < idx.Length && items[idx[k + 1]].Id == items[idx[k]].Id) continue;
            keep.Add(idx[k]);
        }
        int n = keep.Count;
        var ids = new int[n]; var parents = new int[n]; var flags = new byte[n]; var offsets = new int[n + 1];
        long textLen = 0;
        foreach (int k in keep) textLen += items[k].Text.Length + 1;
        var text = new byte[textLen];
        int pos = 0;
        for (int j = 0; j < n; j++)
        {
            var it = items[keep[j]];
            ids[j] = it.Id; parents[j] = it.Parent; flags[j] = it.Flags; offsets[j] = pos;
            it.Text.CopyTo(text, pos); pos += it.Text.Length;
            text[pos++] = (byte)'\n';
        }
        offsets[n] = pos;
        return new DeltaChunk(ids, parents, flags, offsets, text);
    }
}

/// <summary>Memory-mapped immutable segment. Reference counted so that it is unmapped only after readers finish.</summary>
public sealed unsafe class Segment : RecordSource, IDisposable
{
    public const ulong Magic = 0x0200_0047_4553_5846; // "FXSEG" v2 little-endian (v2 adds the directory-name sidecar)
    public const int HeaderSize = 64;

    private readonly Storage.MappedFile _map;
    private readonly int _count;
    private readonly int* _ids, _parents, _offsets;
    private readonly byte* _flags, _text;
    private readonly int _textLength;
    private readonly byte* _dirText;
    private readonly int _dirTextLength;
    private int _refs = 1; // owned by the manager
    private bool _retired;
    private readonly Action<Segment>? _onFree;

    public string FileName { get; }
    public long MaxRev { get; }
    public int Level { get; }
    public long FileBytes => _map.Length;

    internal Segment(Storage.MappedFile map, string fileName, int level, Action<Segment>? onFree)
    {
        _map = map; FileName = fileName; Level = level; _onFree = onFree;
        byte* p = map.Pointer;
        if (map.Length < HeaderSize || *(ulong*)p != Magic) throw new InvalidDataException("Bad segment magic: " + fileName);
        _count = *(int*)(p + 8);
        long textLen = *(long*)(p + 16);
        MaxRev = *(long*)(p + 24);
        long dirTextLen = *(long*)(p + 40);
        long flagsPadded = (_count + 3) & ~3L;
        long expected = HeaderSize + _count * 8L + flagsPadded + (_count + 1) * 4L + textLen + dirTextLen;
        if (_count < 0 || textLen < 0 || textLen > int.MaxValue || dirTextLen < 0 || dirTextLen > int.MaxValue || expected != map.Length)
            throw new InvalidDataException($"Corrupt segment {fileName}: size {map.Length} != {expected}");
        _ids = (int*)(p + HeaderSize);
        _parents = _ids + _count;
        _flags = (byte*)(_parents + _count);
        _offsets = (int*)(_flags + flagsPadded);
        _text = (byte*)(_offsets + _count + 1);
        _textLength = (int)textLen;
        _dirText = _text + textLen;
        _dirTextLength = (int)dirTextLen;
        InitStale(_count);
    }

    public override int Count => _count;
    public override ReadOnlySpan<int> Ids => new(_ids, _count);
    public override ReadOnlySpan<int> Parents => new(_parents, _count);
    public override ReadOnlySpan<byte> Flags => new(_flags, _count);
    public override ReadOnlySpan<int> Offsets => new(_offsets, _count + 1);
    public override ReadOnlySpan<byte> Text => new(_text, _textLength);
    public override ReadOnlySpan<byte> DirText => new(_dirText, _dirTextLength);

    internal bool TryAddRef()
    {
        while (true)
        {
            int r = Volatile.Read(ref _refs);
            if (r <= 0) return false;
            if (Interlocked.CompareExchange(ref _refs, r + 1, r) == r) return true;
        }
    }

    internal void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0) Free();
    }

    /// <summary>Called by the manager when the segment is replaced; drops the manager's reference.</summary>
    internal void Retire()
    {
        if (_retired) return;
        _retired = true;
        Release();
    }

    private void Free()
    {
        _map.Dispose();
        _onFree?.Invoke(this);
    }

    public void Dispose() => Retire();
}

internal static class SpanExt
{
    public static ReadOnlySpan<byte> AsBytes(ReadOnlySpan<int> s) => MemoryMarshal.AsBytes(s);
}
