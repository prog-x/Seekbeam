using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Config;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.NameIndex;
using Seekbeam.Core.Resources;
using Seekbeam.Core.SourceAccess;
using Seekbeam.Core.Storage;

namespace Seekbeam.Core.Indexing;

/// <summary>Commands executed on the indexer thread (all catalog structure writes are serialised there).</summary>
public abstract record IndexCommand;
public sealed record RescanPathCommand(string Path, QueuePriority Priority, ScanMode Mode) : IndexCommand;
public sealed record RenameCommand(string OldPath, string NewPath) : IndexCommand;
public sealed record SyncRootsCommand(SeekbeamConfig Config) : IndexCommand;
public sealed record ReconcileCommand(long? RootId, QueuePriority Priority) : IndexCommand;
public sealed record DiscoverCommand(string FilePath) : IndexCommand;
public sealed record ReindexAllCommand : IndexCommand;

public sealed class NameIndexerStats
{
    public long QueueLength { get; init; }
    public long Deferred { get; init; }
    public long DirectoriesScanned { get; init; }
    public long Added { get; init; }
    public long Updated { get; init; }
    public long Deleted { get; init; }
    public long Errors { get; init; }
    public long ContentQueued { get; init; }
    public double EntriesPerSecond { get; init; }
    public string? CurrentDirectory { get; init; }
    public bool Busy { get; init; }
    public bool Paused { get; init; }
}

/// <summary>
/// Name indexing coordinator: one dedicated low-priority thread draining the persistent name_queue (breadth-first by
/// priority), applying per-directory diffs in short transactions and publishing changes to the Name Index.
/// </summary>
public sealed class NameIndexer : IDisposable
{
    private readonly CatalogDb _db;
    private readonly CatalogWriter _writer;
    private readonly NameIndexManager _index;
    private readonly EntryStore _entries;
    private readonly SeekbeamStorage _storage;
    private readonly IWorkGate _gate;
    private readonly ConcurrentQueue<IndexCommand> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly DirectoryScanner _scanner;
    private Thread? _thread;
    private volatile ScopeRules _scope;
    private volatile ContentEligibility _content;
    private volatile string? _current;
    private volatile bool _busy;
    /// <summary>Commands posted and not yet executed: work exists even before the queue has been filled.</summary>
    private long _pendingCommands;
    private long _queueLength;
    private long _deferred;
    private readonly Stopwatch _rateWindow = Stopwatch.StartNew();
    private long _rateEntries;
    private double _rate;
    private readonly Func<IEnumerable<string>> _supportedExtensions;
    private int _discoveryThisHour;
    private DateTime _discoveryHourStart = DateTime.UtcNow;
    private int _discoveryBudget = 2000;

    public event Action? ContentQueued;
    public event Action? QueueDrained;
    /// <summary>Raised after roots changed (watchers must be refreshed).</summary>
    public event Action<ScopeRules>? ScopeChanged;

    public NameIndexer(CatalogDb db, CatalogWriter writer, NameIndexManager index, EntryStore entries, SeekbeamStorage storage,
        IWorkGate gate, Func<IEnumerable<string>> supportedExtensions)
    {
        _db = db; _writer = writer; _index = index; _entries = entries; _storage = storage; _gate = gate;
        _supportedExtensions = supportedExtensions;
        _scanner = new DirectoryScanner(writer);
        _scope = new ScopeRules([], [], storage.DataRoot);
        _content = ContentEligibility.None;
    }

    public ScopeRules Scope => _scope;
    public ContentEligibility Content => _content;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { Name = "Seekbeam.NameIndexer", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Post(IndexCommand cmd) { Interlocked.Increment(ref _pendingCommands); _commands.Enqueue(cmd); Interlocked.Exchange(ref _countTicks, 0); _wake.Set(); }
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
            cmd.CommandText = "SELECT COUNT(*), IFNULL(SUM(not_before > $now),0) FROM name_queue";
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

    public NameIndexerStats Stats()
    {
        var c = _scanner.Counters;
        return new NameIndexerStats
        {
            QueueLength = CountQueue(), Deferred = Interlocked.Read(ref _deferred), DirectoriesScanned = c.DirectoriesScanned, Added = c.Added,
            Updated = c.Updated, Deleted = c.Deleted, Errors = c.Errors, ContentQueued = c.ContentQueued,
            EntriesPerSecond = _rate, CurrentDirectory = _current, Busy = _busy || Interlocked.Read(ref _pendingCommands) > 0,
        };
    }

    // ------------------------------------------------------------------ worker

    private void Run()
    {
        ThreadPriorities.EnterBackgroundMode();
        var ct = _cts.Token;
        var disk = new List<DiskChild>(1024);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (_commands.TryDequeue(out var cmd))
                {
                    // Busy from the moment a command is posted until the queue it fills has drained (see Stats().Busy).
                    _busy = true;
                    try { Execute(cmd); }
                    finally { Interlocked.Decrement(ref _pendingCommands); }
                }
                _gate.WaitTurn(WorkClass.Names, ct);
                int processed = ProcessQueueBatch(disk, ct);
                if (processed == 0)
                {
                    if (_busy)
                    {
                        _busy = false;
                        _current = null;
                        OnDrained();
                    }
                    WaitHandle.WaitAny([_wake, ct.WaitHandle], TimeSpan.FromSeconds(10));
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("NameIndexer", "Worker error", ex);
                _db.RecordError("NameIndexer", null, ex.Message);
                Thread.Sleep(1000);
            }
        }
    }

    private readonly record struct QueueItem(long DirId, QueuePriority Priority, ScanMode Mode, int Attempts);

    private List<QueueItem> Dequeue(int max)
    {
        var items = new List<QueueItem>(max);
        var con = _db.RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT dir_id, priority, mode, attempts FROM name_queue WHERE not_before <= $now ORDER BY priority, seq LIMIT $n";
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("$n", max);
            using (var r = cmd.ExecuteReader())
                while (r.Read()) items.Add(new QueueItem(r.GetInt64(0), (QueuePriority)r.GetInt32(1), (ScanMode)r.GetInt32(2), r.GetInt32(3)));
            if (items.Count > 0 || Interlocked.Read(ref _queueLength) != 0)
            {
                cmd.CommandText = "SELECT COUNT(*) FROM name_queue";
                Interlocked.Exchange(ref _queueLength, (long)cmd.ExecuteScalar()!);
            }
        }
        finally { _db.ReturnReader(con); }
        return items;
    }

    private int ProcessQueueBatch(List<DiskChild> disk, CancellationToken ct)
    {
        var items = Dequeue(_gate.BatchSize(WorkClass.Names));
        if (items.Count == 0) return 0;
        _busy = true;
        var scope = _scope;
        var content = _content;
        int processed = 0;
        int i = 0;
        var reader = _db.RentReader();
        try
        {
        while (i < items.Count)
        {
            // One transaction groups several small directories (≤ ~2000 row operations or ~150 ms).
            var sw = Stopwatch.StartNew();
            using var b = _writer.Begin();
            long contentBefore = _scanner.Counters.ContentQueued;
            while (i < items.Count && b.Operations < 2000 && sw.ElapsedMilliseconds < 150)
            {
                if (!_commands.IsEmpty) break; // commands (events) jump ahead
                var it = items[i++];
                ScanItem(b, reader, it, scope, content, disk, ct);
                processed++;
            }
            var changes = b.Commit();
            Interlocked.Exchange(ref _countTicks, 0);
            _index.Apply(changes);
            long n = changes.Count;
            _gate.Report(WorkClass.Names, (int)Math.Max(1, n), 0);
            UpdateRate(n);
            if (_scanner.Counters.ContentQueued != contentBefore) ContentQueued?.Invoke();
            if (!_commands.IsEmpty) break;
            ct.ThrowIfCancellationRequested();
        }
        }
        finally { _db.ReturnReader(reader); }
        return processed;
    }

    private void ScanItem(CatalogWriter.Batch b, Microsoft.Data.Sqlite.SqliteConnection reader, QueueItem it, ScopeRules scope, ContentEligibility content, List<DiskChild> disk, CancellationToken ct)
    {
        var dir = EntryStore.Get(reader, it.DirId);
        if (dir == null || dir.Kind != EntryKind.Directory) { DeleteQueueItem(b, it.DirId); return; }
        var root = scope.ById(dir.RootId);
        string? path = root == null ? null : _entries.DirectoryPath(reader, dir.Id);
        if (root == null || path == null) { DeleteQueueItem(b, it.DirId); return; }
        _current = path;
        var status = _scanner.ReadDisk(path, disk, out int err, ct);
        switch (status)
        {
            case DirectoryReadStatus.Ok:
                // A location reported unavailable is back (reconnected drive or share).
                if (dir.ParentId == 0) Exec(b, "UPDATE roots SET state=0, last_error=NULL WHERE id=$id AND state=1", ("$id", dir.RootId));
                _scanner.ApplyDiff(b, dir, path, root, scope, content, disk, it.Mode,
                    it.Priority == QueuePriority.Reconcile ? QueuePriority.Reconcile : it.Priority == QueuePriority.Event ? QueuePriority.Event : it.Priority);
                DeleteQueueItem(b, it.DirId);
                break;
            case DirectoryReadStatus.NotFound when dir.ParentId == 0:
                // Root temporarily unavailable (unplugged drive, offline share): keep its index, retry later.
                Log.Warn("NameIndexer", "Root unavailable: " + Log.P(path));
                SetRootState(b, dir.RootId, 1, "unavailable");
                Retry(b, it, TimeSpan.FromMinutes(5));
                break;
            case DirectoryReadStatus.NotFound or DirectoryReadStatus.NotADirectory:
                _scanner.Counters.Deleted += _writer.Delete(b, dir.Id, EntryKind.Directory);
                DeleteQueueItem(b, it.DirId);
                break;
            case DirectoryReadStatus.AccessDenied:
                _scanner.Counters.Errors++;
                _db.RecordError("Scan", path, "Access denied");
                _writer.MarkScanned(b, dir.Id, 0);
                DeleteQueueItem(b, it.DirId); // permissions are respected; never bypassed
                break;
            default:
                _scanner.Counters.Errors++;
                _db.RecordError("Scan", path, "Read error " + err);
                if (it.Attempts < 3) Retry(b, it, TimeSpan.FromSeconds(30 << it.Attempts));
                else DeleteQueueItem(b, it.DirId);
                break;
        }
    }

    private static void DeleteQueueItem(CatalogWriter.Batch b, long dirId) => Exec(b, "DELETE FROM name_queue WHERE dir_id=$id", ("$id", dirId));

    private static void Retry(CatalogWriter.Batch b, QueueItem it, TimeSpan delay) =>
        Exec(b, "UPDATE name_queue SET attempts=attempts+1, not_before=$nb WHERE dir_id=$id", ("$nb", DateTime.UtcNow.Add(delay).Ticks), ("$id", it.DirId));

    private void SetRootState(CatalogWriter.Batch b, long rootId, int state, string? error) =>
        Exec(b, "UPDATE roots SET state=$s, last_error=$e WHERE id=$id", ("$s", state), ("$e", (object?)error ?? DBNull.Value), ("$id", rootId));

    private static void Exec(CatalogWriter.Batch b, string sql, params (string, object)[] ps)
    {
        using var cmd = b.Tx.Connection!.CreateCommand();
        cmd.Transaction = b.Tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    private void UpdateRate(long n)
    {
        _rateEntries += n;
        double s = _rateWindow.Elapsed.TotalSeconds;
        if (s >= 2) { _rate = _rateEntries / s; _rateEntries = 0; _rateWindow.Restart(); }
    }

    private void OnDrained()
    {
        long now = DateTime.UtcNow.Ticks;
        lock (_db.WriteLock)
        {
            using var cmd = _db.Writer.CreateCommand();
            cmd.CommandText = "UPDATE roots SET last_full_scan = CASE WHEN last_full_scan=0 THEN $t ELSE last_full_scan END, last_reconcile=$t, state=CASE WHEN state=1 THEN state ELSE 0 END";
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }
        Log.Info("NameIndexer", $"Name queue drained ({_scanner.Counters.DirectoriesScanned} directories scanned, {_scanner.Counters.Added} added, {_scanner.Counters.Deleted} deleted, {_scanner.Counters.Errors} errors)");
        _index.MaybeMerge();
        _rate = 0;
        QueueDrained?.Invoke();
    }

    // ------------------------------------------------------------------ commands

    private void Execute(IndexCommand cmd)
    {
        switch (cmd)
        {
            case SyncRootsCommand s: SyncRoots(s.Config); break;
            case RescanPathCommand r: EnqueuePath(r.Path, r.Priority, r.Mode); break;
            case RenameCommand rn: ApplyRename(rn.OldPath, rn.NewPath); break;
            case ReconcileCommand rc: EnqueueRoots(rc.RootId, rc.Priority); break;
            case DiscoverCommand d: Discover(d.FilePath); break;
            case ReindexAllCommand: ReindexAll(); break;
        }
    }

    private IReadOnlyList<(long RootId, string RootPath, long RootEntryId)> RootTuples() =>
        _scope.Roots.Select(r => (r.RootId, r.Path, r.RootEntryId)).ToList();

    private void EnqueuePath(string path, QueuePriority priority, ScanMode mode)
    {
        var roots = RootTuples();
        string? cur = Path.TrimEndingDirectorySeparator(path);
        long? id = EntryStore.Resolve(_db.Writer, cur, roots);
        // Unknown directory (e.g. just created): rescan the nearest known ancestor; new directories are then scanned recursively.
        while (id == null && cur != null && _scope.FindRoot(cur) != null)
        {
            cur = Path.GetDirectoryName(cur);
            if (cur != null) id = EntryStore.Resolve(_db.Writer, cur, roots);
        }
        if (id == null) return;
        using var b = _writer.Begin();
        _writer.EnqueueDirectory(b, id.Value, priority, mode);
        b.Commit();
    }

    /// <summary>Applies a watcher rename in place (keeps the entry id, content index and history).</summary>
    private void ApplyRename(string oldPath, string newPath)
    {
        var scope = _scope;
        var roots = RootTuples();
        long? id = EntryStore.Resolve(_db.Writer, oldPath, roots);
        string? newParent = Path.GetDirectoryName(newPath);
        long? parentId = newParent == null ? null : EntryStore.Resolve(_db.Writer, newParent, roots);
        bool inScope = scope.IsInScope(newPath, out var root);
        var info = id == null ? null : EntryStore.Get(_db.Writer, id.Value);
        if (info == null || parentId == null || !inScope || root == null || root.RootId != info.RootId
            || scope.IsExcluded(root, Path.GetFileName(newPath), newPath, (FileAttributes)info.Attributes))
        {
            // Fall back to rescanning both parents (diff handles every case correctly).
            if (Path.GetDirectoryName(oldPath) is { } op) EnqueuePath(op, QueuePriority.Event, ScanMode.Shallow);
            if (newParent != null) EnqueuePath(newParent, QueuePriority.Event, ScanMode.Shallow);
            return;
        }
        var parent = EntryStore.Get(_db.Writer, parentId.Value);
        string newName = Path.GetFileName(newPath);
        var existing = EntryStore.FindChild(_db.Writer, parentId.Value, newName);
        var exInfo = existing is long ex && ex != info.Id ? EntryStore.Get(_db.Writer, ex) : null;
        using (var b = _writer.Begin())
        {
            if (exInfo != null) _writer.Delete(b, exInfo.Id, exInfo.Kind); // overwritten target
            _writer.Rename(b, info.Id, parentId.Value, newName, (parent?.Depth ?? 0) + 1, info.Kind, info.Attributes);
            if (info.Kind == EntryKind.File && _content.IsEligible(root, newPath, info.Size, (FileAttributes)info.Attributes)
                && !string.Equals(Path.GetExtension(oldPath), Path.GetExtension(newPath), StringComparison.OrdinalIgnoreCase))
                _writer.EnqueueContent(b, info.Id, QueuePriority.Event, ContentOp.Index);
            _index.Apply(b.Commit());
        }
        if (info.Kind == EntryKind.Directory) _entries.InvalidatePathCache();
    }

    private void EnqueueRoots(long? rootId, QueuePriority priority)
    {
        using var b = _writer.Begin();
        foreach (var r in _scope.Roots)
            if (rootId == null || r.RootId == rootId) _writer.EnqueueDirectory(b, r.RootEntryId, priority, ScanMode.Recursive);
        b.Commit();
        _wake.Set();
    }

    private void ReindexAll()
    {
        // Name index rebuilt from scratch: catalog entries are kept and fully reconciled with the disk; segments rebuilt.
        _index.MaybeMerge(force: true);
        EnqueueRoots(null, QueuePriority.InitialScan);
    }

    /// <summary>Opportunistic discovery (CLAUDE.md §16): a file opened in scope but in an unknown directory.</summary>
    private void Discover(string filePath)
    {
        var scope = _scope;
        if (!scope.IsInScope(filePath, out var root) || root == null) return;
        var roots = RootTuples();
        if (EntryStore.Resolve(_db.Writer, filePath, roots) != null) return; // already indexed
        if (DateTime.UtcNow - _discoveryHourStart > TimeSpan.FromHours(1)) { _discoveryHourStart = DateTime.UtcNow; _discoveryThisHour = 0; }
        if (_discoveryThisHour >= _discoveryBudget) { Log.Debug("Discovery", "Hourly budget exhausted"); return; }
        // Deepest known ancestor directory: scanning it (recursively, at discovery priority) inserts the unknown parents,
        // their subdirectories and the file itself, and queues eligible content.
        string? dir = Path.GetDirectoryName(filePath);
        while (dir != null)
        {
            long? id = EntryStore.Resolve(_db.Writer, dir, roots);
            if (id != null)
            {
                using var b = _writer.Begin();
                _writer.EnqueueDirectory(b, id.Value, QueuePriority.Discovery, ScanMode.Shallow);
                b.Commit();
                _discoveryThisHour++;
                Log.Debug("Discovery", "Queued " + Log.P(dir));
                return;
            }
            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>Synchronises the roots table with configuration; returns quickly (large deletions are batched).</summary>
    private void SyncRoots(SeekbeamConfig cfg)
    {
        _discoveryBudget = cfg.Discovery.MaxDirectoriesPerHour;
        var desired = cfg.Roots.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Path)).ToList();
        var existing = new List<(long Id, string Path, long EntryId)>();
        lock (_db.WriteLock)
        {
            using var cmd = _db.Writer.CreateCommand();
            cmd.CommandText = "SELECT id, path, entry_id FROM roots";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
        }
        var scopes = new List<RootScope>();
        foreach (var rc in desired)
        {
            if (_storage.IsUnderDataRoot(rc.Path) || rc.Path.Equals(_storage.DataRoot, StringComparison.OrdinalIgnoreCase))
            { Log.Warn("Roots", "Refusing root inside the Seekbeam data directory: " + Log.P(rc.Path)); continue; }
            var ex = existing.FirstOrDefault(e => e.Path.Equals(rc.Path, StringComparison.OrdinalIgnoreCase));
            long rootId, entryId;
            string cfgHash = string.Join("|", rc.MaxDepth, rc.IncludeHidden, rc.FollowLinks, rc.IndexContent, string.Join(";", rc.Excludes), string.Join(";", cfg.GlobalExcludes));
            if (ex.Id == 0)
            {
                using var b = _writer.Begin();
                using (var ins = b.Tx.Connection!.CreateCommand())
                {
                    ins.Transaction = b.Tx;
                    ins.CommandText = "INSERT INTO roots(path) VALUES($p) RETURNING id";
                    ins.Parameters.AddWithValue("$p", rc.Path);
                    rootId = (long)ins.ExecuteScalar()!;
                }
                SourceFileAccess.TryGetMetadata(rc.Path, out var md);
                entryId = _writer.Insert(b, 0, rootId, rc.Path, EntryKind.Directory, 0, md.LastWriteTimeUtc, md.CreationTimeUtc, (long)(md.Attributes | FileAttributes.Directory), 0, 0);
                Exec(b, "UPDATE roots SET entry_id=$e WHERE id=$id", ("$e", entryId), ("$id", rootId));
                _writer.EnqueueDirectory(b, entryId, QueuePriority.InitialScan, ScanMode.Recursive);
                _index.Apply(b.Commit());
                _db.SetMeta("rootcfg:" + rootId, cfgHash);
                Log.Info("Roots", "Added root " + Log.P(rc.Path));
            }
            else
            {
                rootId = ex.Id; entryId = ex.EntryId;
                if (_db.GetMeta("rootcfg:" + rootId) != cfgHash)
                {
                    _db.SetMeta("rootcfg:" + rootId, cfgHash);
                    using var b = _writer.Begin();
                    _writer.EnqueueDirectory(b, entryId, QueuePriority.InitialScan, ScanMode.Recursive);
                    b.Commit();
                    Log.Info("Roots", "Root settings changed, reconciling " + Log.P(rc.Path));
                }
            }
            scopes.Add(new RootScope { RootId = rootId, Path = rc.Path, Config = rc, Excludes = new ExclusionMatcher(rc.Excludes), RootEntryId = entryId });
        }
        // Removed (or disabled) roots: drop their entries.
        foreach (var ex in existing)
        {
            if (scopes.Any(s => s.RootId == ex.Id)) continue;
            using var b = _writer.Begin();
            _writer.Delete(b, ex.EntryId, EntryKind.Directory, id => _writer.EnqueueContent(b, id, QueuePriority.Maintenance, ContentOp.Delete));
            Exec(b, "DELETE FROM roots WHERE id=$id", ("$id", ex.Id));
            _index.Apply(b.Commit());
            _entries.InvalidatePathCache();
            Log.Info("Roots", "Removed root " + Log.P(ex.Path));
        }
        _content = new ContentEligibility(cfg.Content, _supportedExtensions());
        _scope = new ScopeRules(scopes, cfg.GlobalExcludes, _storage.DataRoot);
        ScopeChanged?.Invoke(_scope);
        _wake.Set();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(10));
        _scanner.Dispose();
    }
}

/// <summary>Windows background processing mode for worker threads: very low CPU, I/O and memory priority.</summary>
public static partial class ThreadPriorities
{
    private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(IntPtr hThread, int nPriority);

    public static void EnterBackgroundMode() => SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
    public static void LeaveBackgroundMode() => SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
}
