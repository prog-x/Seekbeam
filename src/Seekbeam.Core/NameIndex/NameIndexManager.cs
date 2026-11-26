using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Storage;
using Seekbeam.Core.Text;

namespace Seekbeam.Core.NameIndex;

/// <summary>Immutable view used by one search. Segments are reference-counted for the snapshot's lifetime.</summary>
public sealed class NameIndexSnapshot : IDisposable
{
    public IReadOnlyList<Segment> Segments { get; }
    public IReadOnlyList<DeltaChunk> Chunks { get; }
    private int _disposed;

    internal NameIndexSnapshot(IReadOnlyList<Segment> segments, IReadOnlyList<DeltaChunk> chunks) { Segments = segments; Chunks = chunks; }

    /// <summary>All sources, newest first (delta chunks, then segments from newest to oldest).</summary>
    public IEnumerable<RecordSource> NewestFirst()
    {
        for (int i = Chunks.Count - 1; i >= 0; i--) yield return Chunks[i];
        for (int i = Segments.Count - 1; i >= 0; i--) yield return Segments[i];
    }

    /// <summary>Finds the live copy of a record by id.</summary>
    public bool TryFind(int id, out RecordSource source, out int ordinal)
    {
        for (int i = Chunks.Count - 1; i >= 0; i--)
        {
            int o = Chunks[i].Find(id);
            if (o >= 0 && !Chunks[i].IsStale(o)) { source = Chunks[i]; ordinal = o; return true; }
        }
        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            int o = Segments[i].Find(id);
            if (o >= 0 && !Segments[i].IsStale(o)) { source = Segments[i]; ordinal = o; return true; }
        }
        source = null!; ordinal = -1;
        return false;
    }

    public long LiveCount
    {
        get
        {
            long n = 0;
            foreach (var s in Segments) n += s.LiveCount;
            foreach (var c in Chunks) n += c.LiveCount;
            return n;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var s in Segments) s.Release();
    }
}

internal sealed class NameIndexManifest
{
    public int Version { get; set; } = 1;
    public long CoveredRev { get; set; }
    public int NextSegmentSeq { get; set; } = 1;
    public List<ManifestSegment> Segments { get; set; } = [];
}

internal sealed class ManifestSegment
{
    public string File { get; set; } = "";
    public int Level { get; set; }
}

[JsonSerializable(typeof(NameIndexManifest))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ManifestJsonContext : JsonSerializerContext;

/// <summary>
/// Name Index: LSM of immutable memory-mapped segments (L0 base + optional L1) plus a bounded in-memory delta.
/// Derived entirely from the catalog (entries.name_rev + name_tombstones), so it is crash-consistent by construction.
/// Writer methods (<see cref="Apply"/>, <see cref="MaybeMerge"/>) are serialised; readers use lock-free snapshots.
/// </summary>
public sealed class NameIndexManager : IDisposable
{
    private const string ManifestRel = "index\\manifest.json";
    public int DeltaFlushThreshold { get; set; } = 131_072;
    public int MaxDeltaChunks { get; set; } = 16;

    private readonly SeekbeamStorage _storage;
    private readonly CatalogDb _catalog;
    private readonly object _writeLock = new();
    private readonly object _snapLock = new();
    private NameIndexManifest _manifest = new();
    private Segment? _base, _l1;
    private List<DeltaChunk> _chunks = [];
    private volatile NameIndexSnapshot? _published;
    private long _appliedRev;
    private int _deltaCount;
    private long _pendingTombstones;
    private bool _disposed;

    public long MergeCount { get; private set; }
    public TimeSpan LastMergeDuration { get; private set; }
    public bool Loaded { get; private set; }

    public NameIndexManager(SeekbeamStorage storage, CatalogDb catalog)
    {
        _storage = storage;
        _catalog = catalog;
        Publish();
    }

    // ------------------------------------------------------------------ readers

    public NameIndexSnapshot Acquire()
    {
        lock (_snapLock)
        {
            var p = _published!;
            var segs = p.Segments;
            foreach (var s in segs)
                if (!s.TryAddRef()) throw new InvalidOperationException("Segment freed while published");
            return new NameIndexSnapshot(segs, p.Chunks);
        }
    }

    public long LiveCount { get { using var s = Acquire(); return s.LiveCount; } }
    public int DeltaCount => Volatile.Read(ref _deltaCount);
    public long SegmentBytes => (_base?.FileBytes ?? 0) + (_l1?.FileBytes ?? 0);
    public int SegmentCount => (_base != null ? 1 : 0) + (_l1 != null ? 1 : 0);

    public long DeltaMemoryBytes
    {
        get { long b = 0; foreach (var c in _chunks.ToArray()) b += c.MemoryBytes; return b; }
    }

    private void Publish()
    {
        var segs = new List<Segment>(2);
        if (_base != null) segs.Add(_base);
        if (_l1 != null) segs.Add(_l1);
        lock (_snapLock) _published = new NameIndexSnapshot(segs, _chunks.ToArray());
    }

    // ------------------------------------------------------------------ load / recovery

    /// <summary>Loads segments, re-derives staleness and the delta from the catalog. Safe after any crash.</summary>
    public void Load(CancellationToken ct = default)
    {
        lock (_writeLock)
        {
            var sw = Stopwatch.StartNew();
            _manifest = ReadManifest();
            var segs = new List<Segment>();
            bool corrupt = false;
            foreach (var ms in _manifest.Segments.OrderBy(m => m.Level == 0 ? 0 : 1))
            {
                try { segs.Add(OpenSegment(ms.File, ms.Level)); }
                catch (Exception ex) { Log.Error("NameIndex", "Cannot open segment " + ms.File + "; rebuilding from catalog", ex); corrupt = true; }
            }
            if (corrupt)
            {
                foreach (var s in segs) s.Retire();
                segs.Clear();
                _manifest = new NameIndexManifest { NextSegmentSeq = _manifest.NextSegmentSeq };
            }
            _base = segs.FirstOrDefault(s => s.Level == 0);
            _l1 = segs.FirstOrDefault(s => s.Level == 1);
            if (_base == null && _l1 != null) { _base = _l1; _l1 = null; }
            DeleteOrphanFiles();

            // Newer segment shadows older copies.
            if (_base != null && _l1 != null)
            {
                var ids = _l1.Ids;
                for (int i = 0; i < ids.Length; i++) _base.MarkStaleById(ids[i]);
            }
            // Tombstones (deletions not yet compacted away).
            var con = _catalog.RentReader();
            try
            {
                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = "SELECT id FROM name_tombstones";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        int id = (int)r.GetInt64(0);
                        _base?.MarkStaleById(id);
                        _l1?.MarkStaleById(id);
                        _pendingTombstones++;
                    }
                }
                Publish();
                // Delta = every entry changed after the covered revision (streamed, flushed as it grows).
                _appliedRev = _manifest.CoveredRev;
                using var cmd2 = con.CreateCommand();
                cmd2.CommandText = "SELECT id, parent_id, kind, attrs, name, name_rev, xflags FROM entries WHERE name_rev > $r ORDER BY name_rev";
                cmd2.Parameters.AddWithValue("$r", _manifest.CoveredRev);
                using var rd = cmd2.ExecuteReader();
                var batch = new List<NameChange>(4096);
                while (rd.Read())
                {
                    long rev = rd.GetInt64(5);
                    batch.Add(new NameChange((int)rd.GetInt64(0), (int)rd.GetInt64(1),
                        ToFlags(rd.GetInt32(2), rd.GetInt64(3), rd.GetInt64(1) == 0, rd.GetInt64(6)), rd.GetString(4), rev, false));
                    if (batch.Count == 4096) { ApplyLocked(batch); batch.Clear(); ct.ThrowIfCancellationRequested(); }
                }
                if (batch.Count > 0) ApplyLocked(batch);
            }
            finally { _catalog.ReturnReader(con); }
            _appliedRev = Math.Max(_appliedRev, _catalog.CurrentRev);
            Publish();
            Loaded = true;
            Log.Info("NameIndex", $"Loaded in {sw.ElapsedMilliseconds} ms: base={_base?.Count ?? 0} l1={_l1?.Count ?? 0} delta={_deltaCount} tombstones={_pendingTombstones}");
        }
    }

    /// <summary>Bits of entries.xflags that are copied to the record flags.</summary>
    public const long XFlagsMask = (long)(EntryFlags.Outside | EntryFlags.Lowered);

    public static EntryFlags ToFlags(int kind, long attrs, bool isRoot, long xflags = 0)
    {
        var f = (EntryFlags)(byte)(xflags & XFlagsMask);
        if (kind == 1) f |= EntryFlags.Directory;
        var a = (FileAttributes)attrs;
        if ((a & FileAttributes.Hidden) != 0) f |= EntryFlags.Hidden;
        if ((a & FileAttributes.System) != 0) f |= EntryFlags.System;
        if ((a & FileAttributes.ReparsePoint) != 0) f |= EntryFlags.Reparse;
        if (isRoot) f |= EntryFlags.Root;
        return f;
    }

    private Segment OpenSegment(string file, int level)
    {
        var map = _storage.MapReadOnly(Path.Combine("index", file));
        try { return new Segment(map, file, level, OnSegmentFreed); }
        catch { map.Dispose(); throw; }
    }

    private void OnSegmentFreed(Segment s)
    {
        if (_manifest.Segments.Any(m => m.File == s.FileName)) return; // still referenced by manifest (shutdown)
        if (!_storage.TryDelete(Path.Combine("index", s.FileName)))
            Log.Debug("NameIndex", "Deferred delete of " + s.FileName);
    }

    private void DeleteOrphanFiles()
    {
        var keep = new HashSet<string>(_manifest.Segments.Select(m => m.File), StringComparer.OrdinalIgnoreCase);
        foreach (var f in _storage.ListFiles("index", "names-*"))
            if (!keep.Contains(f)) _storage.TryDelete(Path.Combine("index", f));
    }

    private NameIndexManifest ReadManifest()
    {
        try
        {
            string? json = _storage.ReadAllText(ManifestRel);
            if (json != null) return JsonSerializer.Deserialize(json, ManifestJsonContext.Default.NameIndexManifest) ?? new();
        }
        catch (Exception ex) { Log.Error("NameIndex", "Manifest unreadable; full rebuild from catalog", ex); }
        return new NameIndexManifest();
    }

    private void WriteManifest() =>
        _storage.WriteAllTextAtomic(ManifestRel, JsonSerializer.Serialize(_manifest, ManifestJsonContext.Default.NameIndexManifest));

    // ------------------------------------------------------------------ writer

    /// <summary>Applies committed catalog changes. Must be called in revision order after the transaction commits.</summary>
    public void Apply(IReadOnlyList<NameChange> changes)
    {
        if (changes.Count == 0) return;
        lock (_writeLock)
        {
            ApplyLocked(changes);
            Publish();
            MaybeMergeLocked(force: false);
        }
    }

    private void ApplyLocked(IReadOnlyList<NameChange> changes)
    {
        var items = new List<(int, int, byte, byte[])>(changes.Count);
        var chunks = _chunks;
        foreach (var c in changes)
        {
            // Invalidate older copies everywhere (cheap skip when the id is newer than a source's max id).
            if (_base != null && c.Id <= _base.MaxId) _base.MarkStaleById(c.Id);
            if (_l1 != null && c.Id <= _l1.MaxId) _l1.MarkStaleById(c.Id);
            for (int i = 0; i < chunks.Count; i++)
                if (c.Id <= chunks[i].MaxId && chunks[i].MarkStaleById(c.Id)) _deltaCount--;
            if (c.Deleted) _pendingTombstones++;
            else items.Add((c.Id, c.Parent, (byte)c.Flags, Normalizer.NormalizeToBytes(c.Name ?? "")));
            if (c.Rev > _appliedRev) _appliedRev = c.Rev;
        }
        if (items.Count > 0)
        {
            var chunk = DeltaChunk.Build(items);
            _chunks = [.. chunks, chunk];
            _deltaCount += chunk.Count;
        }
        if (_chunks.Count > MaxDeltaChunks) CompactDelta();
        if (_deltaCount >= DeltaFlushThreshold) { Publish(); FlushLocked(full: false); }
    }

    private void CompactDelta()
    {
        // Merge all delta chunks into one (in memory).
        var items = new List<(int, int, byte, byte[])>(_deltaCount);
        foreach (var ch in _chunks)
            for (int i = 0; i < ch.Count; i++)
                if (!ch.IsStale(i)) items.Add((ch.Ids[i], ch.Parents[i], ch.Flags[i], ch.NameAt(i).ToArray()));
        var merged = DeltaChunk.Build(items);
        _chunks = [merged];
        _deltaCount = merged.Count;
    }

    /// <summary>Flushes/merges when thresholds are reached (or always when <paramref name="force"/>).</summary>
    public void MaybeMerge(bool force = false)
    {
        lock (_writeLock) MaybeMergeLocked(force);
    }

    private void MaybeMergeLocked(bool force)
    {
        if (force && (_deltaCount > 0 || (_l1 != null)))
        {
            FlushLocked(full: true);
            return;
        }
        long baseCount = _base?.Count ?? 0;
        if (_l1 != null && (_l1.Count >= Math.Max(baseCount / 3, 65_536) || _l1.StaleCount > _l1.Count / 2))
            FlushLocked(full: true);
        else if (baseCount > 0 && _pendingTombstones > Math.Max(baseCount / 8, 50_000))
            FlushLocked(full: true);
    }

    /// <summary>Writes the delta (and L1, or everything when <paramref name="full"/>) into a new segment.</summary>
    private void FlushLocked(bool full)
    {
        var sw = Stopwatch.StartNew();
        if (_base == null) full = true;
        var sources = new List<RecordSource>();
        if (full && _base != null) sources.Add(_base);
        if (_l1 != null) sources.Add(_l1);
        sources.AddRange(_chunks);
        if (sources.Count == 0) return;

        string file = $"names-{_manifest.NextSegmentSeq:D6}.seg";
        _manifest.NextSegmentSeq++;
        long coveredRev = _appliedRev;
        int written = SegmentWriter.Write(_storage, Path.Combine("index", file), sources, coveredRev);
        var seg = OpenSegment(file, full ? 0 : 1);

        Segment? oldBase = _base, oldL1 = _l1;
        if (full) { _base = seg; _l1 = null; }
        else _l1 = seg;
        _chunks = [];
        _deltaCount = 0;
        _manifest.CoveredRev = coveredRev;
        _manifest.Segments = [];
        if (_base != null) _manifest.Segments.Add(new ManifestSegment { File = _base.FileName, Level = 0 });
        if (_l1 != null) _manifest.Segments.Add(new ManifestSegment { File = _l1.FileName, Level = 1 });
        WriteManifest();
        Publish();
        if (full) { oldBase?.Retire(); }
        oldL1?.Retire();
        if (full) PurgeTombstones(coveredRev);
        MergeCount++;
        LastMergeDuration = sw.Elapsed;
        Log.Info("NameIndex", $"{(full ? "Full merge" : "Delta flush")}: {written:N0} records -> {file} in {sw.ElapsedMilliseconds} ms");
    }

    private void PurgeTombstones(long coveredRev)
    {
        lock (_catalog.WriteLock)
        {
            using var cmd = _catalog.Writer.CreateCommand();
            cmd.CommandText = "DELETE FROM name_tombstones WHERE rev <= $r";
            cmd.Parameters.AddWithValue("$r", coveredRev);
            cmd.ExecuteNonQuery();
            _pendingTombstones = CatalogDb.Scalar<long>(_catalog.Writer, "SELECT COUNT(*) FROM name_tombstones");
        }
    }

    /// <summary>Drops everything and rebuilds from the catalog (used after "reindex names").</summary>
    public void Reset()
    {
        lock (_writeLock)
        {
            var b = _base; var l = _l1;
            _base = null; _l1 = null; _chunks = []; _deltaCount = 0; _pendingTombstones = 0;
            _manifest = new NameIndexManifest { NextSegmentSeq = _manifest.NextSegmentSeq };
            WriteManifest();
            Publish();
            b?.Retire(); l?.Retire();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_writeLock)
        {
            try { if (_deltaCount > 0) FlushLocked(full: false); }
            catch (Exception ex) { Log.Warn("NameIndex", "Flush at shutdown failed (will be rebuilt from catalog): " + ex.Message); }
            lock (_snapLock) _published = new NameIndexSnapshot([], []);
            _base?.Retire(); _l1?.Retire();
        }
    }
}
