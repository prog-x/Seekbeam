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
/// <summary>A folder outside the index was used (dialog, Explorer, recent item): remember it in the folder index (TD-21).</summary>
public sealed record LearnFolderCommand(string FolderPath) : IndexCommand;
/// <summary>Full re-walk of the folder index (periodic; directory events are watched in between).</summary>
public sealed record ReconcileFolderIndexCommand(QueuePriority Priority) : IndexCommand;

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

    private readonly Func<IEnumerable<string>> _fixedDrives;
    private SeekbeamConfig? _lastConfig;

    /// <summary>
    /// Folders never walked by the folder index whatever the configuration: Seekbeam's own program folder (it receives the
    /// sandbox's read permission, audit W7, so it must stay outside the indexing perimeter). Set before <c>Start</c>.
    /// </summary>
    public IReadOnlyList<string> ExcludedFromFolderIndex { get; set; } = [];

    /// <param name="fixedDrives">Drives walked by the folder index (default: the ready fixed drives of the machine).</param>
    public NameIndexer(CatalogDb db, CatalogWriter writer, NameIndexManager index, EntryStore entries, SeekbeamStorage storage,
        IWorkGate gate, Func<IEnumerable<string>> supportedExtensions, Func<IEnumerable<string>>? fixedDrives = null)
    {
        _db = db; _writer = writer; _index = index; _entries = entries; _storage = storage; _gate = gate;
        _supportedExtensions = supportedExtensions;
        _fixedDrives = fixedDrives ?? FixedDrives;
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
            case LearnFolderCommand l: LearnFolder(l.FolderPath); break;
            case ReconcileFolderIndexCommand rf: EnqueueFolderIndex(rf.Priority); break;
        }
    }

    /// <summary>Ready fixed drives of the machine ("C:\", "D:\").</summary>
    public static IEnumerable<string> FixedDrives()
    {
        var list = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.IsReady) list.Add(d.Name); // "C:\"
        }
        catch (Exception ex) { Log.Warn("FolderIndex", "Drive list: " + ex.Message); }
        return list;
    }

    private void EnqueueFolderIndex(QueuePriority priority)
    {
        using var b = _writer.Begin();
        foreach (var r in _scope.Roots)
            if (r.FoldersOnly) _writer.EnqueueDirectory(b, r.RootEntryId, priority, ScanMode.Recursive);
        b.Commit();
        _db.SetMeta("folderindex:reconciled", DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _wake.Set();
    }

    /// <summary>Last full walk of the folder index (null if never).</summary>
    public DateTime? FolderIndexReconciledUtc =>
        long.TryParse(_db.GetMeta("folderindex:reconciled"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long t)
            ? new DateTime(t, DateTimeKind.Utc) : null;

    /// <summary>
    /// Remembers a folder used outside the index. Covered folders are ignored (an indexed location, or the drive walk);
    /// a new one becomes a root of the folder index with <see cref="FolderIndexConfig.LearnedDepth"/> levels below it.
    /// </summary>
    private void LearnFolder(string folder)
    {
        var cfg = _lastConfig;
        if (cfg == null || !cfg.FolderIndex.Enabled || !cfg.FolderIndex.LearnFolders) return;
        string p = FolderIndexPlanner.Normalize(folder);
        if (p.Length < 3 || _storage.IsUnderDataRoot(p)) return;
        if (_scope.IsInScope(p, out _)) return; // already in the index (location or folder index)
        bool known;
        lock (_db.WriteLock)
        {
            using var cmd = _db.Writer.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM learned_folders WHERE path=$p";
            cmd.Parameters.AddWithValue("$p", p);
            known = (long)cmd.ExecuteScalar()! > 0;
            cmd.CommandText = "INSERT INTO learned_folders(path,last_used) VALUES($p,$t) ON CONFLICT(path) DO UPDATE SET last_used=$t; " +
                              "DELETE FROM learned_folders WHERE path IN (SELECT path FROM learned_folders ORDER BY last_used DESC LIMIT -1 OFFSET $max);";
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("$max", cfg.FolderIndex.MaxLearned);
            cmd.ExecuteNonQuery();
        }
        if (known) return;
        Log.Info("FolderIndex", "Learned folder " + Log.P(p));
        SyncRoots(cfg);
    }

    private List<string> LearnedFolders()
    {
        var list = new List<string>();
        lock (_db.WriteLock)
        {
            using var cmd = _db.Writer.CreateCommand();
            cmd.CommandText = "SELECT path FROM learned_folders ORDER BY last_used DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }
        return list;
    }

    /// <summary>Forgets every learned folder (privacy reset); their folders leave the index at the next sync.</summary>
    public void ForgetLearnedFolders()
    {
        lock (_db.WriteLock) CatalogDb.Exec(_db.Writer, "DELETE FROM learned_folders;");
        if (_lastConfig is { } c) Post(new SyncRootsCommand(c));
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
            _writer.Rename(b, info.Id, parentId.Value, newName, (parent?.Depth ?? 0) + 1, info.Kind, info.Attributes,
                root.FoldersOnly ? DirectoryScanner.FolderXFlags(root, newPath) : info.XFlags);
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
        // "All roots" means the indexed locations: the folder index has its own, much less frequent, reconciliation.
        foreach (var r in _scope.Roots)
            if (rootId == null ? !r.FoldersOnly : r.RootId == rootId) _writer.EnqueueDirectory(b, r.RootEntryId, priority, ScanMode.Recursive);
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

    private readonly record struct DesiredRoot(RootConfig Config, RootKind Kind, FolderIndexOrigin? Origin);

    /// <summary>
    /// Synchronises the roots table with configuration: the indexed locations, and the start points of the folder index
    /// planned by <see cref="FolderIndexPlanner"/>. Returns quickly (large deletions are batched).
    /// </summary>
    private void SyncRoots(SeekbeamConfig cfg)
    {
        _lastConfig = cfg;
        _discoveryBudget = cfg.Discovery.MaxDirectoriesPerHour;
        var desired = new List<DesiredRoot>();
        foreach (var rc in cfg.Roots.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Path)))
        {
            if (_storage.IsUnderDataRoot(rc.Path) || rc.Path.Equals(_storage.DataRoot, StringComparison.OrdinalIgnoreCase))
            { Log.Warn("Roots", "Refusing root inside the Seekbeam data directory: " + Log.P(rc.Path)); continue; }
            desired.Add(new DesiredRoot(rc, RootKind.Location, null));
        }
        List<FolderIndexRoot> plan = [];
        try { plan = FolderIndexPlanner.Plan(cfg, _fixedDrives(), LearnedFolders(), SourceFileAccess.DirectoryExists, _storage.DataRoot); }
        catch (Exception ex) { Log.Warn("FolderIndex", "Planning failed: " + ex.Message); }
        foreach (var p in plan)
            if (!_storage.IsUnderDataRoot(p.Path) && !desired.Any(d => d.Config.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase)))
                desired.Add(new DesiredRoot(p.Config, RootKind.FolderIndex, p.Origin));

        var existing = new List<(long Id, string Path, long EntryId, RootKind Kind)>();
        lock (_db.WriteLock)
        {
            using var cmd = _db.Writer.CreateCommand();
            cmd.CommandText = "SELECT id, path, entry_id, kind FROM roots";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), (RootKind)r.GetInt32(3)));
        }
        bool Wanted((long Id, string Path, long EntryId, RootKind Kind) e) =>
            desired.Any(d => d.Kind == e.Kind && d.Config.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase));

        // Removed roots first (a path may change kind: the folder-index copy must go before the location is created).
        var removedLocations = new List<string>();
        foreach (var ex in existing.Where(e => !Wanted(e)).ToList())
        {
            DeleteRootInChunks(ex.Id, ex.EntryId);
            _entries.InvalidatePathCache();
            existing.Remove(ex);
            if (ex.Kind == RootKind.Location) removedLocations.Add(ex.Path);
            Log.Info("Roots", (ex.Kind == RootKind.FolderIndex ? "Folder index: removed " : "Removed root ") + Log.P(ex.Path));
        }

        var fi = cfg.FolderIndex;
        string[] excludedPrefixes = FolderIndexPlanner.ExpandedPrefixes(fi.ExcludedPaths.Concat(ExcludedFromFolderIndex));
        string[] loweredPrefixes = FolderIndexPlanner.ExpandedPrefixes(fi.LowerRanked.Where(e => e.IndexOfAny(['\\', ':', '%']) >= 0));
        var loweredNames = FolderIndexPlanner.LoweredNames(fi.LowerRanked);
        var scopes = new List<RootScope>();
        var newLocations = new List<string>();
        foreach (var d in desired)
        {
            var rc = d.Config;
            bool folders = d.Kind == RootKind.FolderIndex;
            var scopeTemplate = new RootScope
            {
                RootId = 0, Path = rc.Path, Config = rc, Excludes = new ExclusionMatcher(rc.Excludes), Kind = d.Kind,
                ExcludedPrefixes = folders ? excludedPrefixes : [], LoweredPrefixes = folders ? loweredPrefixes : [],
                LoweredNames = folders ? loweredNames : new(StringComparer.OrdinalIgnoreCase), RelativeToVolume = folders,
            };
            var ex = existing.FirstOrDefault(e => e.Kind == d.Kind && e.Path.Equals(rc.Path, StringComparison.OrdinalIgnoreCase));
            long rootId, entryId;
            // Locations keep the 1.0 format: an upgrade must not trigger a full rescan of every location.
            string cfgHash = string.Join("|", rc.MaxDepth, rc.IncludeHidden, rc.FollowLinks, rc.IndexContent, string.Join(";", rc.Excludes), string.Join(";", cfg.GlobalExcludes));
            if (folders) cfgHash = "folders|" + cfgHash + "|" + string.Join(";", excludedPrefixes) + "#" + string.Join(";", fi.LowerRanked);
            // The folder index never delays the indexed locations: its walk runs after them, at reconciliation priority.
            var scanPriority = folders ? QueuePriority.Reconcile : QueuePriority.InitialScan;
            if (ex.Id == 0)
            {
                using var b = _writer.Begin();
                using (var ins = b.Tx.Connection!.CreateCommand())
                {
                    ins.Transaction = b.Tx;
                    ins.CommandText = "INSERT INTO roots(path, kind) VALUES($p, $k) RETURNING id";
                    ins.Parameters.AddWithValue("$p", rc.Path);
                    ins.Parameters.AddWithValue("$k", (int)d.Kind);
                    rootId = (long)ins.ExecuteScalar()!;
                }
                SourceFileAccess.TryGetMetadata(rc.Path, out var md);
                entryId = _writer.Insert(b, 0, rootId, rc.Path, EntryKind.Directory, 0, md.LastWriteTimeUtc, md.CreationTimeUtc,
                    (long)(md.Attributes | FileAttributes.Directory), 0, 0, DirectoryScanner.FolderXFlags(scopeTemplate, rc.Path));
                Exec(b, "UPDATE roots SET entry_id=$e WHERE id=$id", ("$e", entryId), ("$id", rootId));
                _writer.EnqueueDirectory(b, entryId, scanPriority, ScanMode.Recursive);
                _index.Apply(b.Commit());
                _db.SetMeta("rootcfg:" + rootId, cfgHash);
                if (folders) Log.Info("FolderIndex", $"Added {d.Origin} " + Log.P(rc.Path));
                else { Log.Info("Roots", "Added root " + Log.P(rc.Path)); newLocations.Add(rc.Path); }
            }
            else
            {
                rootId = ex.Id; entryId = ex.EntryId;
                if (_db.GetMeta("rootcfg:" + rootId) != cfgHash)
                {
                    _db.SetMeta("rootcfg:" + rootId, cfgHash);
                    using var b = _writer.Begin();
                    _writer.EnqueueDirectory(b, entryId, scanPriority, ScanMode.Recursive);
                    b.Commit();
                    Log.Info("Roots", "Root settings changed, reconciling " + Log.P(rc.Path));
                }
            }
            scopes.Add(new RootScope
            {
                RootId = rootId, Path = rc.Path, Config = rc, Excludes = scopeTemplate.Excludes, RootEntryId = entryId, Kind = d.Kind,
                ExcludedPrefixes = scopeTemplate.ExcludedPrefixes, LoweredPrefixes = scopeTemplate.LoweredPrefixes,
                LoweredNames = scopeTemplate.LoweredNames, RelativeToVolume = scopeTemplate.RelativeToVolume,
            });
        }
        _content = new ContentEligibility(cfg.Content, _supportedExtensions());
        _scope = new ScopeRules(scopes, cfg.GlobalExcludes, _storage.DataRoot);
        RebalanceFolderIndex(newLocations, removedLocations);
        ScopeChanged?.Invoke(_scope);
        _wake.Set();
    }

    /// <summary>Rows per transaction when a large tree is deleted (CLAUDE.md §13: small batches, short transactions).</summary>
    internal const int DeleteChunk = 20_000;

    /// <summary>
    /// Removes a root and every entry of it, a bounded number of rows per transaction (a folder-index root can be a whole
    /// drive). Rows are taken by root_id, so a partially deleted tree never leaves unreachable rows behind; the root entry
    /// and its roots row go last. Interrupted (crash), the next sync finds the root still unwanted and continues.
    /// </summary>
    private void DeleteRootInChunks(long rootId, long rootEntryId)
    {
        var ids = new List<(long Id, int Kind)>(DeleteChunk);
        while (true)
        {
            ids.Clear();
            lock (_db.WriteLock)
            {
                using var cmd = _db.Writer.CreateCommand();
                cmd.CommandText = "SELECT id, kind FROM entries WHERE root_id=$r AND id<>$e LIMIT $n";
                cmd.Parameters.AddWithValue("$r", rootId);
                cmd.Parameters.AddWithValue("$e", rootEntryId);
                cmd.Parameters.AddWithValue("$n", DeleteChunk);
                using var r = cmd.ExecuteReader();
                while (r.Read()) ids.Add((r.GetInt64(0), r.GetInt32(1)));
            }
            if (ids.Count == 0) break;
            using var b = _writer.Begin();
            foreach (var (id, kind) in ids)
            {
                _writer.DeleteOne(b, id);
                if (kind == (int)EntryKind.File) _writer.EnqueueContent(b, id, QueuePriority.Maintenance, ContentOp.Delete);
            }
            _index.Apply(b.Commit());
        }
        using (var b = _writer.Begin())
        {
            _writer.DeleteOne(b, rootEntryId);
            Exec(b, "DELETE FROM roots WHERE id=$id", ("$id", rootId));
            _index.Apply(b.Commit());
        }
    }

    /// <summary>Deletes a folder and its subtree, deepest rows first, a bounded number of rows per transaction.</summary>
    private void DeleteSubtreeInChunks(long dirId)
    {
        var ids = new List<long>();
        lock (_db.WriteLock)
        {
            using var cmd = _db.Writer.CreateCommand();
            // Breadth-first (SQLite's recursive queue is FIFO): reversed, every child comes before its parent.
            cmd.CommandText = "WITH RECURSIVE sub(id,kind) AS (SELECT id,kind FROM entries WHERE parent_id=$id " +
                              "UNION ALL SELECT e.id,e.kind FROM entries e JOIN sub ON e.parent_id=sub.id WHERE sub.kind=1) SELECT id FROM sub";
            cmd.Parameters.AddWithValue("$id", dirId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        ids.Reverse();
        ids.Add(dirId);
        for (int i = 0; i < ids.Count; i += DeleteChunk)
        {
            using var b = _writer.Begin();
            for (int j = i; j < Math.Min(ids.Count, i + DeleteChunk); j++) _writer.DeleteOne(b, ids[j]);
            _index.Apply(b.Commit());
        }
    }

    /// <summary>
    /// Keeps the folder index and the indexed locations disjoint right away (instead of at the next walk): a new location
    /// drops the folder-index copy of its folder; a removed location is walked again by the folder index.
    /// </summary>
    private void RebalanceFolderIndex(List<string> newLocations, List<string> removedLocations)
    {
        var folderRoots = _scope.Roots.Where(r => r.FoldersOnly).Select(r => (r.RootId, r.Path, r.RootEntryId)).ToList();
        if (folderRoots.Count == 0) return;
        foreach (var path in newLocations)
        {
            if (EntryStore.Resolve(_db.Writer, path, folderRoots) is not long id) continue;
            var info = EntryStore.Get(_db.Writer, id);
            if (info == null || info.ParentId == 0 || (info.XFlags & (long)EntryFlags.Outside) == 0) continue;
            DeleteSubtreeInChunks(id);
            _entries.InvalidatePathCache();
        }
        foreach (var path in removedLocations)
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is { } parent && _scope.FindRoot(parent) is { FoldersOnly: true })
                EnqueuePath(parent, QueuePriority.Reconcile, ScanMode.Shallow);
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
