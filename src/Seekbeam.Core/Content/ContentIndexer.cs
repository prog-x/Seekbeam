using System.Diagnostics;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Config;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.Resources;
using Seekbeam.Core.SourceAccess;

namespace Seekbeam.Core.Content;

public sealed class ContentIndexerStats
{
    public long QueueLength { get; init; }
    /// <summary>Items waiting for a delayed retry (locked files, transient errors).</summary>
    public long Deferred { get; init; }
    public long Indexed { get; init; }
    public long Failed { get; init; }
    public long Skipped { get; init; }
    public long BytesRead { get; init; }
    public double FilesPerSecond { get; init; }
    public string? CurrentFile { get; init; }
    public bool Busy { get; init; }
    public int HostRestarts { get; init; }
    /// <summary>Isolation of the extractor host, verified at its last start; null if no host has started yet (TD-17).</summary>
    public SandboxLevel? HostIsolation { get; init; }
    public long HostPrivateBytes { get; init; }
    public int Workers { get; init; }
}

/// <summary>
/// Drains content_queue on dedicated background-mode threads: re-checks eligibility, skips unchanged documents,
/// extracts out of process, writes content.db. Name indexing always has priority (gate), failures are isolated with
/// bounded retries and exponential backoff.
/// </summary>
public sealed class ContentIndexer : IDisposable
{
    private readonly CatalogDb _db;
    private readonly ContentDb _content;
    private readonly EntryStore _entries;
    private readonly IWorkGate _gate;
    private readonly Func<SeekbeamConfig> _config;
    private readonly Func<NameIndexer> _indexer;
    private readonly Func<IExtractionService> _serviceFactory;
    private readonly List<(Thread Thread, IExtractionService Service)> _workers = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<long> _inFlight = [];
    private long _indexed, _failed, _skipped, _bytes, _queueLength;
    private long _deferred;
    private volatile string? _current;
    private volatile bool _busy;
    private readonly Stopwatch _rateSw = Stopwatch.StartNew();
    private long _rateCount;
    private double _rate;
    private int _desiredWorkers = 1;

    /// <summary>Raised (on a worker thread) when the content queue becomes empty after work.</summary>
    public event Action? QueueDrained;

    public ContentIndexer(CatalogDb db, ContentDb content, EntryStore entries, IWorkGate gate, Func<SeekbeamConfig> config,
        Func<NameIndexer> indexer, Func<IExtractionService> serviceFactory)
    {
        _db = db; _content = content; _entries = entries; _gate = gate; _config = config; _indexer = indexer; _serviceFactory = serviceFactory;
    }

    public void Start(int workers = 1)
    {
        _desiredWorkers = Math.Clamp(workers, 1, 8);
        lock (_workers)
            while (_workers.Count < _desiredWorkers)
            {
                var svc = _serviceFactory();
                var t = new Thread(Run) { Name = "Seekbeam.ContentIndexer" + _workers.Count, IsBackground = true, Priority = ThreadPriority.Lowest };
                _workers.Add((t, svc));
                t.Start(svc);
            }
    }

    public void SetLimits(int memoryMB, int cpuPercent)
    {
        lock (_workers) foreach (var w in _workers) w.Service.SetLimits(memoryMB, Math.Max(1, cpuPercent / Math.Max(1, _workers.Count)));
    }

    public void Wake() { Interlocked.Exchange(ref _countTicks, 0); _wake.Set(); }

    /// <summary>Exact queue length (read from the persistent queue, not a cached value).</summary>
    private long _countTicks;
    private long CountQueue()
    {
        // COUNT(*) is O(n) on large queues: cached for 2 s, never on the hot path.
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _countTicks) < 2000) return Interlocked.Read(ref _queueLength);
        Interlocked.Exchange(ref _countTicks, now);
        var con = _db.RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), IFNULL(SUM(not_before > $now),0) FROM content_queue";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            using var r = cmd.ExecuteReader();
            r.Read();
            Interlocked.Exchange(ref _deferred, r.GetInt64(1));
            long n = r.GetInt64(0) - r.GetInt64(1); // due items only; deferred retries are reported separately
            Interlocked.Exchange(ref _queueLength, n);
            return n;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return Interlocked.Read(ref _queueLength); }
        finally { _db.ReturnReader(con); }
    }

    public ContentIndexerStats Stats()
    {
        int restarts = 0; long hostBytes = 0;
        lock (_workers)
            foreach (var w in _workers)
                if (w.Service is ExtractorClient c) { restarts += c.Restarts; hostBytes += c.HostPrivateBytes; }
        return new ContentIndexerStats
        {
            QueueLength = CountQueue(), Deferred = Interlocked.Read(ref _deferred), Indexed = Interlocked.Read(ref _indexed), Failed = Interlocked.Read(ref _failed),
            Skipped = Interlocked.Read(ref _skipped), BytesRead = Interlocked.Read(ref _bytes), FilesPerSecond = _rate,
            CurrentFile = _current, Busy = _busy, HostRestarts = restarts, HostPrivateBytes = hostBytes, Workers = _workers.Count,
            HostIsolation = ExtractorClient.AnyHostStarted ? ExtractorClient.LastLevel : null,
        };
    }

    private readonly record struct Item(long EntryId, ContentOp Op, int Attempts);

    private List<Item> Dequeue(int max)
    {
        var res = new List<Item>();
        var con = _db.RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT entry_id, op, attempts FROM content_queue WHERE not_before <= $now ORDER BY priority, seq LIMIT $n";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("$n", max + 16);
            using (var r = cmd.ExecuteReader())
                while (r.Read() && res.Count < max)
                {
                    long id = r.GetInt64(0);
                    lock (_inFlight) if (!_inFlight.Add(id)) continue;
                    res.Add(new Item(id, (ContentOp)r.GetInt32(1), r.GetInt32(2)));
                }
        }
        finally { _db.ReturnReader(con); }
        return res;
    }

    private void Run(object? state)
    {
        var svc = (IExtractionService)state!;
        ThreadPriorities.EnterBackgroundMode();
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _gate.WaitTurn(WorkClass.Content, ct);
                // Name indexing first: content waits while the name queue is busy (CLAUDE.md §4.3).
                if (_indexer().Stats() is { Busy: true, QueueLength: > 0 } && Interlocked.Read(ref _queueLength) < 1_000_000)
                {
                    WaitHandle.WaitAny([_wake, ct.WaitHandle], TimeSpan.FromSeconds(2)); // wakes on cancellation too (several workers share _wake)
                    continue;
                }
                var items = Dequeue(_gate.BatchSize(WorkClass.Content));
                if (items.Count == 0)
                {
                    bool nothingInFlight; lock (_inFlight) nothingInFlight = _inFlight.Count == 0;
                    if (_busy && nothingInFlight && CountQueue() == 0) { Log.Info("Content", $"Content queue drained (indexed {Interlocked.Read(ref _indexed)}, failed {Interlocked.Read(ref _failed)}, skipped {Interlocked.Read(ref _skipped)})"); QueueDrained?.Invoke(); }
                    if (nothingInFlight) { _busy = false; _current = null; }
                    WaitHandle.WaitAny([_wake, ct.WaitHandle], TimeSpan.FromSeconds(nothingInFlight ? 15 : 1));
                    continue;
                }
                _busy = true;
                foreach (var it in items)
                {
                    try { Process(svc, it, ct); }
                    finally { lock (_inFlight) _inFlight.Remove(it.EntryId); }
                    _gate.WaitTurn(WorkClass.Content, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Content", "Worker error", ex);
                Thread.Sleep(2000);
            }
        }
    }

    private void Process(IExtractionService svc, Item it, CancellationToken ct)
    {
        if (it.Op == ContentOp.Delete)
        {
            _content.Delete(it.EntryId);
            Remove(it.EntryId);
            return;
        }
        var cfg = _config();
        var info = _entries.Get(it.EntryId);
        string? path = info == null ? null : _entries.GetPath(it.EntryId);
        var indexer = _indexer();
        var root = info == null ? null : indexer.Scope.ById(info.RootId);
        if (info == null || path == null || root == null || info.IsDirectory) { _content.Delete(it.EntryId); Remove(it.EntryId); return; }
        if (!SourceFileAccess.TryGetMetadata(path, out var md)) { Remove(it.EntryId); return; } // vanished: the scanner will delete it
        if (!indexer.Content.IsEligible(root, path, md.Size, md.Attributes))
        {
            _content.Delete(it.EntryId);
            Interlocked.Increment(ref _skipped);
            Remove(it.EntryId);
            return;
        }
        if (md.Size == 0)
        {
            // Empty files (placeholders) have no content: no extraction, no error.
            _content.Upsert(it.EntryId, md.LastWriteTimeUtc, 0, "none", 0, ContentStatus.Empty, "", null);
            Interlocked.Increment(ref _skipped);
            Remove(it.EntryId);
            return;
        }
        var existing = _content.GetInfo(it.EntryId);
        if (existing != null && existing.MTime == md.LastWriteTimeUtc && existing.Size == md.Size && existing.Status != ContentStatus.Failed)
        {
            Interlocked.Increment(ref _skipped);
            Remove(it.EntryId);
            return;
        }
        _current = path;
        var req = new ExtractRequest
        {
            Path = path, MaxChars = cfg.Content.MaxCharsPerDocument, MaxFileBytes = cfg.Content.MaxFileSizeMB * 1024L * 1024L,
            ArchivesEnabled = cfg.Content.Archives.Enabled, ArchiveMaxDepth = cfg.Content.Archives.MaxDepth, ArchiveMaxEntries = cfg.Content.Archives.MaxEntries,
            ArchiveMaxEntryBytes = cfg.Content.Archives.MaxEntrySizeMB * 1024L * 1024L, ArchiveMaxTotalBytes = cfg.Content.Archives.MaxTotalSizeMB * 1024L * 1024L,
            ArchiveMaxRatio = cfg.Content.Archives.MaxCompressionRatio, DisabledExtractors = cfg.Content.DisabledExtractors, ExtraTextExtensions = cfg.Content.ExtraTextExtensions,
        };
        var reply = svc.Extract(req, TimeSpan.FromSeconds(cfg.Content.TimeoutSeconds), ct);
        Interlocked.Add(ref _bytes, md.Size);
        _gate.Report(WorkClass.Content, 1, md.Size);
        UpdateRate();
        switch (reply.Status)
        {
            case ExtractStatus.Ok or ExtractStatus.Empty:
                string text = reply.Text;
                if (reply.Metadata.Count > 0) text = string.Join(" ", reply.Metadata.Values) + "\n" + text;
                _content.Upsert(it.EntryId, md.LastWriteTimeUtc, md.Size, reply.Extractor, reply.Version,
                    text.Length == 0 ? ContentStatus.Empty : ContentStatus.Indexed, text, reply.Truncated ? "truncated" : null);
                Interlocked.Increment(ref _indexed);
                Remove(it.EntryId);
                break;
            case ExtractStatus.Error when it.Attempts < 3 && reply.Error?.Contains("error 32", StringComparison.Ordinal) == true:
                // Sharing violation (file locked by another app): retry later with backoff.
                Retry(it, TimeSpan.FromMinutes(1 << (2 * it.Attempts)));
                break;
            case ExtractStatus.Timeout or ExtractStatus.HostCrashed when it.Attempts < 1:
                Retry(it, TimeSpan.FromMinutes(10));
                break;
            default:
                var status = reply.Status switch
                {
                    ExtractStatus.Encrypted => ContentStatus.Encrypted,
                    ExtractStatus.TooLarge => ContentStatus.TooLarge,
                    ExtractStatus.Unsupported or ExtractStatus.Skipped => ContentStatus.Skipped,
                    _ => ContentStatus.Failed,
                };
                // Keep any partial text recovered from a malformed document.
                _content.Upsert(it.EntryId, md.LastWriteTimeUtc, md.Size, reply.Extractor, reply.Version, status, reply.Text, reply.Error ?? reply.Status.ToString());
                if (status == ContentStatus.Failed)
                {
                    Interlocked.Increment(ref _failed);
                    _db.RecordError("Content", path, $"{reply.Status}: {reply.Error}");
                }
                else Interlocked.Increment(ref _skipped);
                Remove(it.EntryId);
                break;
        }
    }

    private void UpdateRate()
    {
        _rateCount++;
        double s = _rateSw.Elapsed.TotalSeconds;
        if (s >= 5) { _rate = _rateCount / s; _rateCount = 0; _rateSw.Restart(); }
    }

    private void Remove(long id)
    {
        lock (_db.WriteLock)
        {
            using var cmd = _db.Writer.CreateCommand();
            cmd.CommandText = "DELETE FROM content_queue WHERE entry_id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    private void Retry(Item it, TimeSpan delay)
    {
        lock (_db.WriteLock)
        {
            using var cmd = _db.Writer.CreateCommand();
            cmd.CommandText = "UPDATE content_queue SET attempts=attempts+1, not_before=$nb WHERE entry_id=$id";
            cmd.Parameters.AddWithValue("$nb", DateTime.UtcNow.Add(delay).Ticks);
            cmd.Parameters.AddWithValue("$id", it.EntryId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Queues every file of the catalog for content (re)indexing (eligibility re-checked by the worker).</summary>
    public void ReindexAll(bool clearExisting)
    {
        if (clearExisting) _content.DeleteAll();
        lock (_db.WriteLock)
            CatalogDb.Exec(_db.Writer, "INSERT OR IGNORE INTO content_queue(entry_id,priority,op,seq,attempts,not_before) " +
                                       "SELECT id, 4, 0, id, 0, 0 FROM entries WHERE kind=0;");
        _wake.Set();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        lock (_workers)
        {
            foreach (var w in _workers) { w.Thread.Join(TimeSpan.FromSeconds(5)); w.Service.Dispose(); }
            _workers.Clear();
        }
    }
}
