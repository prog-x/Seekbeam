using System.Diagnostics;
using Seekbeam.Core.Apps;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Config;
using Seekbeam.Core.Content;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Discovery;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.NameIndex;
using Seekbeam.Core.Resources;
using Seekbeam.Core.Search;
using Seekbeam.Core.Storage;
using Seekbeam.Core.Watching;

namespace Seekbeam.Core.Engine;

public sealed class EngineOptions
{
    /// <summary>Extensions handled by the extractors (provided by the host, which references Seekbeam.Extraction).</summary>
    public Func<IEnumerable<string>> SupportedExtensions { get; init; } = () => [];
    /// <summary>Factory of extraction workers (out-of-process in production).</summary>
    public Func<IExtractionService>? ExtractionFactory { get; init; }
    public bool StartBackgroundWork { get; init; } = true;
    public bool EnableWatchers { get; init; } = true;
    public bool EnableRecentItems { get; init; } = true;
    public bool EnableApps { get; init; } = true;
    /// <summary>Use an unthrottled gate (tests/benchmarks) instead of the resource manager.</summary>
    public bool Unthrottled { get; init; }
}

public sealed class EngineStats
{
    public long IndexedEntries { get; init; }
    public long Files { get; init; }
    public long Directories { get; init; }
    public int Apps { get; init; }
    public long NameIndexBytes { get; init; }
    public long CatalogBytes { get; init; }
    public long ContentBytes { get; init; }
    public long ContentDocs { get; init; }
    public long ContentFailed { get; init; }
    public int DeltaRecords { get; init; }
    public NameIndexerStats Names { get; init; } = new();
    public ContentIndexerStats Content { get; init; } = new();
    public WatcherStats Watch { get; init; } = new();
    public ResourceProfile Profile { get; init; } = ResourceProfile.Balanced;
    public SystemSample? System { get; init; }
    public bool NamesPaused { get; init; }
    public bool ContentPaused { get; init; }
    public long ProcessPrivateBytes { get; init; }
    public long ProcessWorkingSet { get; init; }
    public double ProcessCpuPercent { get; init; }
    public long DiscoveryEvents { get; init; }
    public IReadOnlyList<RootStatus> Roots { get; init; } = [];
    public IReadOnlyList<ErrorItem> RecentErrors { get; init; } = [];
}

/// <param name="MaxDepth">Configured depth limit (-1 = unlimited).</param>
/// <param name="DepthLimitedDirs">Directories at the depth limit that still contain unexplored items.</param>
/// <param name="DepthHiddenEntries">Direct children of those directories left unexplored (a lower bound of what is missing).</param>
public sealed record RootStatus(long Id, string Path, int State, DateTime? LastFullScanUtc, DateTime? LastReconcileUtc, string? LastError,
    int MaxDepth = -1, long DepthLimitedDirs = 0, long DepthHiddenEntries = 0);
public sealed record ErrorItem(DateTime TimeUtc, string Component, string? Path, string Message);

/// <summary>
/// Composition root of the Seekbeam engine (no UI dependency). Owns storage, catalog, name index, search, indexing,
/// watching, discovery, content and resource management.
/// </summary>
public sealed class SeekbeamEngine : IDisposable
{
    public SeekbeamStorage Storage { get; }
    public ConfigStore Config { get; }
    public CatalogDb Catalog { get; }
    public CatalogWriter Writer { get; }
    public NameIndexManager NameIndex { get; }
    public EntryStore Entries { get; }
    public UsageStore Usage { get; }
    public AppCatalog Apps { get; } = new();
    public AppScanner AppScanner { get; }
    public SearchEngine Search { get; }
    public NameIndexer Indexer { get; }
    public WatchService Watcher { get; }
    public ContentDb ContentDb { get; }
    public ContentIndexer? ContentIndexer { get; }
    public DeepSearch DeepSearch { get; }
    public ResourceManager? Resources { get; }
    public RecentItemsMonitor Recent { get; }
    private readonly IWorkGate _gate;
    private readonly EngineOptions _options;
    private readonly Timer _maintenance;
    private TimeSpan _lastCpu;
    private DateTime _lastCpuSample = DateTime.UtcNow;
    private double _cpuPercent;
    private bool _disposed;

    public SeekbeamEngine(SeekbeamStorage storage, EngineOptions options)
    {
        _options = options;
        Storage = storage;
        storage.CleanTemp();
        Config = new ConfigStore(storage);
        var cfg = Config.Current;
        Log.MinLevel = cfg.Logging.Level;
        Log.RedactPaths = cfg.Logging.RedactPaths;
        Log.MaxFiles = cfg.Logging.MaxFiles;

        Catalog = new CatalogDb(storage);
        Writer = new CatalogWriter(Catalog);
        NameIndex = new NameIndexManager(storage, Catalog);
        Entries = new EntryStore(Catalog);
        Usage = new UsageStore(Catalog) { Weights = cfg.Search.Weights, LearningEnabled = cfg.Privacy.Learning, QueryHistoryEnabled = cfg.Privacy.QueryHistory, MaxQueryHistory = cfg.Privacy.MaxQueryHistory };
        Search = new SearchEngine(NameIndex, Entries, Apps, Usage, () => Config.Current.Search);
        if (options.Unthrottled) _gate = UnthrottledGate.Instance;
        else
        {
            Resources = new ResourceManager(() => Config.Current.Resources, () => Search.IsSearching);
            _gate = Resources;
        }
        Indexer = new NameIndexer(Catalog, Writer, NameIndex, Entries, storage, _gate, options.SupportedExtensions);
        Watcher = new WatchService(Indexer);
        Indexer.ScopeChanged += scope =>
        {
            Watcher.Configure(scope, options.EnableWatchers && Config.Current.Watch.Enabled);
            // Content re-check requested by a change of excluded folders: only now are the new eligibility rules in
            // place (they are rebuilt on the indexer thread), so the worker cannot judge a file with the old ones.
            if (Interlocked.Exchange(ref _contentRecheckPending, 0) == 1) ContentIndexer?.ReindexAll(clearExisting: false);
        };
        ContentDb = new ContentDb(storage) { StoreText = cfg.Content.StoreTextForSnippets };
        DeepSearch = new DeepSearch(ContentDb, NameIndex, Entries, () => Config.Current.Search);
        if (options.ExtractionFactory != null)
        {
            ContentIndexer = new ContentIndexer(Catalog, ContentDb, Entries, _gate, () => Config.Current, () => Indexer, options.ExtractionFactory);
            Indexer.ContentQueued += ContentIndexer.Wake;
            ContentIndexer.QueueDrained += OnQueueDrained;
        }
        Indexer.QueueDrained += OnQueueDrained;
        AppScanner = new AppScanner(Apps);
        Recent = new RecentItemsMonitor(Indexer, path => Usage.RecordLaunch(path, null));
        Usage.Resolver = path => Entries.Resolve(path, Indexer.Scope.Roots.Select(r => (r.RootId, r.Path, r.RootEntryId)).ToList());
        _contentFolders = FolderKey(cfg);
        Config.Changed += OnConfigChanged;
        if (Resources != null) Resources.ProfileChanged += ApplyProfile;
        _maintenance = new Timer(_ => Maintenance(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));
    }

    /// <summary>Loads indexes and starts background work. Search is usable as soon as this returns.</summary>
    public void Start()
    {
        var sw = Stopwatch.StartNew();
        NameIndex.Load();
        Log.Info("Engine", $"Name index ready in {sw.ElapsedMilliseconds} ms ({NameIndex.LiveCount:N0} entries)");
        var cfg = Config.Current;
        Indexer.Post(new SyncRootsCommand(cfg));
        if (cfg.Watch.ReconcileAtStartup && cfg.General.FirstRunCompleted)
            Indexer.Post(new ReconcileCommand(null, QueuePriority.Reconcile)); // events missed while Seekbeam was not running
        if (!cfg.General.FirstRunCompleted) Config.Update(c => c.General.FirstRunCompleted = true);
        if (_options.StartBackgroundWork)
        {
            Indexer.Start();
            ContentIndexer?.Start(Resources?.Profile.ContentWorkers ?? 1);
            if (Resources != null) ApplyProfile(Resources.Profile);
        }
        if (_options.EnableApps)
            ThreadPool.QueueUserWorkItem(_ => { AppScanner.Configure(Config.Current.Apps); AppScanner.Refresh(); });
        if (_options.EnableRecentItems && cfg.Discovery.Enabled && cfg.Discovery.WatchRecentItems) Recent.Start();
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(3000); Usage.RebuildIdMap(); });
    }

    private void ApplyProfile(ResourceProfile p)
    {
        ContentIndexer?.SetLimits(p.HostMemoryMB, p.HostCpuPercent);
        if (ContentIndexer != null && p.ContentWorkers > ContentIndexer.Stats().Workers) ContentIndexer.Start(p.ContentWorkers);
    }

    private string _contentFolders;
    private int _contentRecheckPending;
    private static string FolderKey(SeekbeamConfig c) =>
        string.Join("|", c.Content.ExcludedFolders.Select(d => d.ToUpperInvariant()).Order(StringComparer.Ordinal));

    private void OnConfigChanged(SeekbeamConfig cfg)
    {
        Log.MinLevel = cfg.Logging.Level;
        Log.RedactPaths = cfg.Logging.RedactPaths;
        Usage.Weights = cfg.Search.Weights;
        Usage.LearningEnabled = cfg.Privacy.Learning;
        Usage.QueryHistoryEnabled = cfg.Privacy.QueryHistory;
        ContentDb.StoreText = cfg.Content.StoreTextForSnippets;
        string folders = FolderKey(cfg);
        if (folders != _contentFolders)
        {
            // Newly excluded folders: their stored text is dropped; re-allowed ones get extracted. The worker re-checks
            // eligibility per file and skips unchanged documents, so this costs one metadata read per file.
            _contentFolders = folders;
            // Queued when the SyncRootsCommand below has applied the new rules (see the ScopeChanged handler).
            Interlocked.Exchange(ref _contentRecheckPending, 1);
        }
        Indexer.Post(new SyncRootsCommand(cfg));
        if (_options.EnableApps) ThreadPool.QueueUserWorkItem(_ => { AppScanner.Configure(cfg.Apps); AppScanner.Refresh(); });
        if (_options.EnableRecentItems && cfg.Discovery.Enabled && cfg.Discovery.WatchRecentItems) Recent.Start(); else Recent.Stop();
    }

    /// <summary>
    /// One-time set-up of the extractor AppContainer sandbox (profile + read ACL on Seekbeam's program directory, TD-17).
    /// Never writes inside the indexing perimeter. Call before <see cref="Start"/>; on failure extraction falls back to low integrity.
    /// </summary>
    public SandboxProvisioningStatus ProvisionExtractorSandbox(string programDirectory)
    {
        var scope = ScopeRules.FromConfig(Config.Current, Storage.DataRoot);
        var status = SandboxProvisioning.Ensure(programDirectory, scope.TouchesScope, Storage.DataRoot);
        SandboxProvisioningResult = status;
        return status;
    }

    public SandboxProvisioningStatus? SandboxProvisioningResult { get; private set; }

    /// <summary>Records that the user opened/launched a result (learning + opportunistic discovery).</summary>
    public void RecordOpen(SearchResult r)
    {
        if (r.Kind == ResultKind.App && r.App != null) { Usage.RecordLaunch(r.App.Key, null); return; }
        Usage.RecordLaunch(r.FullPath, r.EntryId == 0 ? null : r.EntryId);
        if (r.Kind == ResultKind.File) Recent.ReportOpened(r.FullPath);
    }

    /// <summary>Adds an exclusion for a path (from the "exclude from index" action). Never touches the file itself.</summary>
    public void ExcludePath(string fullPath)
    {
        var root = Indexer.Scope.FindRoot(fullPath);
        if (root == null) return;
        string? rel = root.Relative(fullPath);
        if (string.IsNullOrEmpty(rel)) return;
        Config.Update(c =>
        {
            var rc = c.Roots.FirstOrDefault(x => x.Path.Equals(root.Path, StringComparison.OrdinalIgnoreCase));
            if (rc != null && !rc.Excludes.Contains(rel, StringComparer.OrdinalIgnoreCase)) rc.Excludes.Add(rel);
        });
    }

    public void ReindexPath(string fullPath, bool content)
    {
        string? dir = SourceAccess.SourceFileAccess.DirectoryExists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        if (dir != null) Indexer.Post(new RescanPathCommand(dir, QueuePriority.Event, ScanMode.Recursive));
        if (content && Entries.Resolve(fullPath, Indexer.Scope.Roots.Select(r => (r.RootId, r.Path, r.RootEntryId)).ToList()) is long id)
        {
            lock (Catalog.WriteLock)
            {
                using var cmd = Catalog.Writer.CreateCommand();
                cmd.CommandText = "INSERT INTO content_queue(entry_id,priority,op,seq,attempts,not_before) VALUES($id,0,0,0,0,0) ON CONFLICT(entry_id) DO UPDATE SET priority=0, attempts=0, not_before=0";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            ContentDb.Delete(id);
            ContentIndexer?.Wake();
        }
    }

    // ------------------------------------------------------------------ content coverage (dashboard breakdown)

    private readonly object _coverageLock = new();
    private readonly CancellationTokenSource _coverageCts = new();
    private Task<ContentCoverageReport?>? _coverageTask;
    private Timer? _coverageDebounce;

    /// <summary>Last census of the content state of every file (null until computed once).</summary>
    public ContentCoverageReport? LastContentCoverage { get; private set; }

    /// <summary>True while a census is running.</summary>
    public bool ContentCoverageRunning { get { lock (_coverageLock) return _coverageTask is { IsCompleted: false }; } }

    /// <summary>Raised (on a background thread) when <see cref="LastContentCoverage"/> has been updated.</summary>
    public event Action<ContentCoverageReport>? ContentCoverageUpdated;

    /// <summary>
    /// Recomputes the census in the background at low priority, yielding to searches like the rest of the indexing.
    /// If one is already running, returns it instead of starting another. Null if cancelled or failed.
    /// </summary>
    public Task<ContentCoverageReport?> RefreshContentCoverage(CancellationToken ct = default)
    {
        lock (_coverageLock)
        {
            if (_coverageTask is { IsCompleted: false } running) return running;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_coverageCts.Token, ct);
            _coverageTask = Task.Factory.StartNew(() =>
            {
                var thread = Thread.CurrentThread;
                var previous = thread.Priority;
                thread.Priority = ThreadPriority.BelowNormal;
                try
                {
                    var report = ContentCoverage.Compute(Catalog, ContentDb, Entries, Indexer, ContentIndexer != null, _gate, linked.Token);
                    LastContentCoverage = report;
                    Log.Info("Engine", $"Content coverage: {report.Files:N0} files in {report.Elapsed.TotalMilliseconds:F0} ms");
                    ContentCoverageUpdated?.Invoke(report);
                    return report;
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex) { Log.Warn("Engine", "Content coverage: " + ex.Message); return null; }
                finally { thread.Priority = previous; linked.Dispose(); }
            }, linked.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .ContinueWith(t => t.IsCanceled ? null : t.Result, TaskScheduler.Default);
            return _coverageTask;
        }
    }

    /// <summary>
    /// End of indexing (both queues empty): recompute the census a few seconds later. Debounced, and at most once a
    /// minute, so that the drains caused by every single file change do not keep it running.
    /// </summary>
    private void OnQueueDrained()
    {
        if (_disposed || Indexer.Stats().QueueLength > 0 || (ContentIndexer?.Stats().QueueLength ?? 0) > 0) return;
        // A census taken while work was still pending is out of date as soon as the queues drain: refresh it promptly.
        var last = LastContentCoverage;
        var age = DateTime.UtcNow - (last?.ComputedUtc ?? DateTime.MinValue);
        var delay = last is null or { Pending: > 0 } || age >= TimeSpan.FromMinutes(1) ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1) - age;
        lock (_coverageLock)
        {
            _coverageDebounce ??= new Timer(_ => { if (!_disposed) _ = RefreshContentCoverage(); });
            _coverageDebounce.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Content results for the launcher, shown under the name results: all words, then all words as prefixes (never
    /// "some words", which would be noise in a launcher). Null when the query does not lend itself to a content
    /// search: too short, or restricted to folders/applications/a folder name (<c>kind:</c>, <c>in:</c>).
    /// <c>ext:</c> filters are honoured.
    /// </summary>
    public SearchResultSet? SearchContentForLauncher(string query, int max, CancellationToken ct)
    {
        if (ContentIndexer == null) return null;
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith("kind:", StringComparison.OrdinalIgnoreCase) || w.StartsWith("type:", StringComparison.OrdinalIgnoreCase)
                           || w.StartsWith("in:", StringComparison.OrdinalIgnoreCase))) return null;
        var exts = words.Where(w => w.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(w => w[4..].Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();
        string text = string.Join(" ", words.Where(w => !w.StartsWith("ext:", StringComparison.OrdinalIgnoreCase)));
        if (text.Count(char.IsLetterOrDigit) < 3) return null; // one or two characters: far too many documents, no value
        return DeepSearch.Search(new DeepSearchRequest
        {
            Query = text, Scope = DeepScope.ContentOnly, Extensions = exts, MaxResults = max, Relaxation = ContentRelaxation.Prefix,
        }, ct);
    }

    /// <summary>Content-index state of a file result (for the launcher's status dot). Read-only and cheap (primary-key lookups).</summary>
    public ContentStateInfo ContentState(long entryId) =>
        ContentStateLookup.Get(entryId, Catalog, ContentDb, Entries, Indexer, ContentIndexer != null);

    public void ReindexNames() => Indexer.Post(new ReindexAllCommand());
    public void ReindexContent() => ContentIndexer?.ReindexAll(clearExisting: true);
    public void Reconcile() => Indexer.Post(new ReconcileCommand(null, QueuePriority.Reconcile));

    private void Maintenance()
    {
        try
        {
            if (Indexer.Stats().Busy) return;
            var cfg = Config.Current;
            // Periodic reconciliation (corrects events missed by watchers).
            var roots = Roots();
            if (roots.Any(r => r.LastReconcileUtc is null || DateTime.UtcNow - r.LastReconcileUtc > TimeSpan.FromHours(cfg.Watch.ReconcileIntervalHours)))
                Indexer.Post(new ReconcileCommand(null, QueuePriority.Reconcile));
            ContentDb.DeleteOrphans(id => Entries.Get(id) != null, 2000);
            Catalog.Checkpoint();
            Usage.RebuildIdMap();
        }
        catch (Exception ex) { Log.Warn("Engine", "Maintenance: " + ex.Message); }
    }

    public IReadOnlyList<RootStatus> Roots()
    {
        var list = new List<RootStatus>();
        var con = Catalog.RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT r.id, r.path, r.state, r.last_full_scan, r.last_reconcile, r.last_error, IFNULL(d.n,0), IFNULL(d.h,0) FROM roots r " +
                              "LEFT JOIN (SELECT root_id, COUNT(*) n, SUM(hidden) h FROM depth_limited GROUP BY root_id) d ON d.root_id = r.id ORDER BY r.path";
            using var r = cmd.ExecuteReader();
            var cfgRoots = Config.Current.Roots;
            while (r.Read())
            {
                string path = r.GetString(1);
                int maxDepth = cfgRoots.FirstOrDefault(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.MaxDepth ?? -1;
                list.Add(new RootStatus(r.GetInt64(0), path, r.GetInt32(2), r.GetInt64(3) > 0 ? new DateTime(r.GetInt64(3), DateTimeKind.Utc) : null,
                    r.GetInt64(4) > 0 ? new DateTime(r.GetInt64(4), DateTimeKind.Utc) : null, r.IsDBNull(5) ? null : r.GetString(5),
                    maxDepth, r.GetInt64(6), r.GetInt64(7)));
            }
        }
        finally { Catalog.ReturnReader(con); }
        return list;
    }

    private long _depthLimitedCache = -1, _depthLimitedAt;

    /// <summary>Total of depth-limited directories over all roots (cached 10 s; cheap enough for the launcher's "no result" hint).</summary>
    public long DepthLimitedCount()
    {
        long now = Environment.TickCount64;
        if (_depthLimitedCache >= 0 && now - Interlocked.Read(ref _depthLimitedAt) < 10_000) return _depthLimitedCache;
        var con = Catalog.RentReader();
        try { _depthLimitedCache = CatalogDb.Scalar<long>(con, "SELECT COUNT(*) FROM depth_limited"); }
        catch (Exception) { _depthLimitedCache = 0; }
        finally { Catalog.ReturnReader(con); }
        Interlocked.Exchange(ref _depthLimitedAt, now);
        return _depthLimitedCache;
    }

    /// <summary>Paths of directories whose content is not explored because of their root's MaxDepth (largest first).</summary>
    public IReadOnlyList<(string Path, int Hidden)> DepthLimitedDirectories(long? rootId = null, int max = 20)
    {
        var ids = new List<(long Id, int Hidden)>();
        var con = Catalog.RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT dir_id, hidden FROM depth_limited WHERE $r IS NULL OR root_id = $r ORDER BY hidden DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$r", (object?)rootId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$n", max);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add((r.GetInt64(0), r.GetInt32(1)));
        }
        finally { Catalog.ReturnReader(con); }
        var list = new List<(string, int)>();
        foreach (var (id, hidden) in ids)
            if (Entries.GetPath(id) is { } p) list.Add((p, hidden));
        return list;
    }

    /// <summary>
    /// Roots whose depth-limit situation changed since the user was last told (persisted, so a notice is not repeated at every start).
    /// Call after the name queue drains.
    /// </summary>
    public IReadOnlyList<RootStatus> TakeNewDepthLimitNotices()
    {
        var changed = new List<RootStatus>();
        foreach (var r in Roots())
        {
            string key = "depthnotice:" + r.Id;
            string value = r.DepthLimitedDirs > 0 ? r.MaxDepth + ":" + r.DepthLimitedDirs : "";
            string previous = Catalog.GetMeta(key) ?? "";
            if (value == previous) continue;
            Catalog.SetMeta(key, value);
            // Only notify when it appears or grows noticeably, not on every small fluctuation.
            long before = previous.Split(':') is [var d, var n] && d == r.MaxDepth.ToString() && long.TryParse(n, out var v) ? v : 0;
            if (r.DepthLimitedDirs > 0 && r.DepthLimitedDirs > before * 1.2) changed.Add(r);
        }
        return changed;
    }

    public IReadOnlyList<ErrorItem> RecentErrors(int max = 100)
    {
        var list = new List<ErrorItem>();
        var con = Catalog.RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT time, component, path, message FROM errors ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", max);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new ErrorItem(new DateTime(r.GetInt64(0), DateTimeKind.Utc), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3)));
        }
        finally { Catalog.ReturnReader(con); }
        return list;
    }

    public void ClearErrors()
    {
        lock (Catalog.WriteLock) CatalogDb.Exec(Catalog.Writer, "DELETE FROM errors;");
    }

    public EngineStats Stats()
    {
        var p = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var cpu = p.TotalProcessorTime;
        double dt = (now - _lastCpuSample).TotalSeconds;
        if (dt >= 1) { _cpuPercent = 100.0 * (cpu - _lastCpu).TotalSeconds / (dt * Environment.ProcessorCount); _lastCpu = cpu; _lastCpuSample = now; }
        var (files, dirs) = Entries.Counts();
        var (docs, failed, _) = ContentDb.Counts();
        return new EngineStats
        {
            IndexedEntries = NameIndex.LiveCount, Files = files, Directories = dirs, Apps = Apps.Count,
            NameIndexBytes = NameIndex.SegmentBytes + NameIndex.DeltaMemoryBytes, CatalogBytes = Catalog.FileSizeBytes(),
            ContentBytes = ContentDb.FileSizeBytes(), ContentDocs = docs, ContentFailed = failed, DeltaRecords = NameIndex.DeltaCount,
            Names = Indexer.Stats(), Content = ContentIndexer?.Stats() ?? new ContentIndexerStats(), Watch = Watcher.Stats(),
            Profile = Resources?.Profile ?? ResourceProfile.Performance, System = Resources?.LastSample,
            NamesPaused = Resources?.IsPaused(WorkClass.Names) ?? false, ContentPaused = Resources?.IsPaused(WorkClass.Content) ?? false,
            ProcessPrivateBytes = p.PrivateMemorySize64, ProcessWorkingSet = p.WorkingSet64, ProcessCpuPercent = _cpuPercent,
            DiscoveryEvents = Recent.TargetsDiscovered, Roots = Roots(), RecentErrors = RecentErrors(50),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _maintenance.Dispose();
        _coverageDebounce?.Dispose();
        _coverageCts.Cancel();
        try { _coverageTask?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        Recent.Dispose();
        Watcher.Dispose();
        AppScanner.Dispose();
        ContentIndexer?.Dispose();
        Indexer.Dispose();
        Resources?.Dispose();
        NameIndex.Dispose();
        ContentDb.Dispose();
        Catalog.Dispose();
        Log.Info("Engine", "Stopped cleanly");
        Log.Flush(TimeSpan.FromSeconds(2));
    }
}
