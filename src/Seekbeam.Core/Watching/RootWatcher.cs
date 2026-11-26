using System.Collections.Concurrent;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.SourceAccess;

namespace Seekbeam.Core.Watching;

public sealed class WatcherStats
{
    public int Watchers { get; init; }
    public long EventsReceived { get; init; }
    public long EventsIgnored { get; init; }
    public long Overflows { get; init; }
    public long DirectoriesQueued { get; init; }
    public DateTime? LastEventUtc { get; init; }
}

/// <summary>
/// File-system change monitoring with ReadDirectoryChangesW (FileSystemWatcher opens directories with
/// FILE_LIST_DIRECTORY only — read-only). Events are coalesced per directory (1.5 s window) and turned into shallow
/// rescans of the affected directories (idempotent diff); renames are applied in place. Buffer overflows schedule a
/// reconciliation of the root. Roots whose direct children are excluded (e.g. AppData under the profile) are watched
/// per top-level child to avoid excluded noise.
/// </summary>
public sealed class WatchService : IDisposable
{
    private readonly NameIndexer _indexer;
    private readonly ConcurrentDictionary<string, byte> _pendingDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string Old, string New)> _renames = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _lock = new();
    private readonly Timer _flushTimer;
    private ScopeRules? _scope;
    private long _events, _ignored, _overflows, _queued;
    private DateTime? _lastEvent;
    private readonly ConcurrentDictionary<long, DateTime> _overflowedRoots = new();
    private bool _disposed;
    public TimeSpan CoalesceWindow { get; set; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>Above this many events per window a root falls back to a delayed reconciliation (burst protection).</summary>
    public int BurstThreshold { get; set; } = 5000;

    public WatchService(NameIndexer indexer)
    {
        _indexer = indexer;
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public WatcherStats Stats() => new()
    {
        Watchers = _watchers.Count, EventsReceived = Interlocked.Read(ref _events), EventsIgnored = Interlocked.Read(ref _ignored),
        Overflows = Interlocked.Read(ref _overflows), DirectoriesQueued = Interlocked.Read(ref _queued), LastEventUtc = _lastEvent,
    };

    /// <summary>(Re)creates watchers for the current scope.</summary>
    public void Configure(ScopeRules scope, bool enabled)
    {
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            _scope = scope;
            if (!enabled) return;
            foreach (var root in scope.Roots)
            {
                if (!root.Config.Watch) continue;
                if (!SourceFileAccess.DirectoryExists(root.Path)) { Log.Warn("Watch", "Root not available, not watched: " + Log.P(root.Path)); continue; }
                try { CreateForRoot(root); }
                catch (Exception ex) { Log.Warn("Watch", $"Cannot watch {Log.P(root.Path)}: {ex.Message}"); }
            }
            Log.Info("Watch", $"{_watchers.Count} watcher(s) active");
        }
    }

    private void CreateForRoot(RootScope root)
    {
        // If the root has excluded direct children, watch each non-excluded top-level child recursively and the root itself
        // non-recursively: excluded noisy trees (AppData, node_modules…) never reach us and cannot overflow the buffer.
        bool split = root.Config.Excludes.Any(e => !e.Contains('\\') && !e.Contains('/')) || root.ExcludedPrefixes.Length > 0;
        bool foldersOnly = root.FoldersOnly;
        if (!split) { Add(root.Path, recursive: true, foldersOnly); return; }
        Add(root.Path, recursive: false, foldersOnly);
        using var reader = SourceFileAccess.CreateDirectoryReader();
        var children = new List<(string Name, FileAttributes A, uint Tag)>();
        reader.Read(root.Path, (in RawEntry e) => { if (e.IsDirectory) children.Add((e.Name.ToString(), e.Attributes, e.ReparseTag)); }, out _);
        foreach (var (name, attrs, tag) in children)
        {
            string full = EntryStore.Combine(root.Path, name);
            if (_scope!.IsExcluded(root, name, full, attrs)) continue;
            if (!LinkPolicy.IsTraversableDirectory(tag, attrs)) continue;
            if (foldersOnly && _scope.IsOtherRoot(root, full)) continue; // an indexed location has its own watcher
            try { Add(full, recursive: true, foldersOnly); }
            catch (Exception ex) { Log.Debug("Watch", $"Cannot watch {Log.P(full)}: {ex.Message}"); }
        }
    }

    /// <param name="foldersOnly">
    /// Folder index (TD-21): only directory creations, deletions and renames are reported (FILE_NOTIFY_CHANGE_DIR_NAME),
    /// so watching a whole drive costs nothing when files are written.
    /// </param>
    private void Add(string path, bool recursive, bool foldersOnly = false)
    {
        var w = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = recursive,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = foldersOnly ? NotifyFilters.DirectoryName
                : NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.CreationTime,
        };
        w.Created += (_, e) => OnChange(e.FullPath, created: true);
        w.Changed += (_, e) => OnChange(e.FullPath, created: false);
        w.Deleted += (_, e) => OnChange(e.FullPath, created: false);
        w.Renamed += (_, e) => OnRename(e.OldFullPath, e.FullPath);
        w.Error += (_, e) => OnError(path, e.GetException());
        w.EnableRaisingEvents = true;
        _watchers.Add(w);
    }

    private bool Relevant(string fullPath)
    {
        var scope = _scope;
        if (scope == null) return false;
        if (!scope.IsInScope(fullPath, out _)) { Interlocked.Increment(ref _ignored); return false; }
        return true;
    }

    private void OnChange(string fullPath, bool created)
    {
        Interlocked.Increment(ref _events);
        _lastEvent = DateTime.UtcNow;
        if (!Relevant(fullPath)) return;
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent == null) return;
        _pendingDirs.TryAdd(parent, 0);
        // A new top-level directory under a split root needs its own watcher: refresh watchers lazily.
        if (created && _scope?.Roots.Any(r => string.Equals(Path.TrimEndingDirectorySeparator(r.Path), parent, StringComparison.OrdinalIgnoreCase) && r.Config.Excludes.Count > 0) == true
            && SourceFileAccess.DirectoryExists(fullPath))
            ThreadPool.QueueUserWorkItem(_ => { var s = _scope; if (s != null) Configure(s, true); });
        Arm();
    }

    private void OnRename(string oldPath, string newPath)
    {
        Interlocked.Increment(ref _events);
        _lastEvent = DateTime.UtcNow;
        bool o = Relevant(oldPath), n = Relevant(newPath);
        if (!o && !n) return;
        if (o && n) _renames.Enqueue((oldPath, newPath));
        else
        {
            // Moved in or out of scope: rescan the in-scope parent.
            string? p = Path.GetDirectoryName(o ? oldPath : newPath);
            if (p != null) _pendingDirs.TryAdd(p, 0);
        }
        Arm();
    }

    private void OnError(string path, Exception ex)
    {
        Interlocked.Increment(ref _overflows);
        Log.Warn("Watch", $"Watcher error on {Log.P(path)} ({ex.GetType().Name}); scheduling reconciliation");
        var root = _scope?.FindRoot(path);
        // Folder index: a whole drive. Re-walked at the lowest priority, at most once an hour (it never delays the locations).
        var minGap = root?.FoldersOnly == true ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);
        if (root != null && (!_overflowedRoots.TryGetValue(root.RootId, out var last) || DateTime.UtcNow - last > minGap))
        {
            _overflowedRoots[root.RootId] = DateTime.UtcNow;
            _indexer.Post(root.FoldersOnly ? new ReconcileFolderIndexCommand(Catalog.QueuePriority.Maintenance)
                                           : new ReconcileCommand(root.RootId, Catalog.QueuePriority.Reconcile));
        }
    }

    private int _armed;
    private void Arm()
    {
        if (Interlocked.Exchange(ref _armed, 1) == 0) _flushTimer.Change(CoalesceWindow, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _armed, 0);
        while (_renames.TryDequeue(out var r)) _indexer.Post(new RenameCommand(r.Old, r.New));
        var all = _pendingDirs.Keys.ToList();
        foreach (var d in all) _pendingDirs.TryRemove(d, out _);
        // Folder index: folder names only, each rescan is one cheap listing. A burst (a program being installed) is
        // rescanned folder by folder at reconciliation priority, never as a re-walk of the whole drive.
        var scope = _scope;
        bool burst = all.Count > BurstThreshold;
        var dirs = new List<string>(all.Count);
        foreach (var d in all)
        {
            if (scope?.FindRoot(d) is { FoldersOnly: true })
            {
                _indexer.Post(new RescanPathCommand(d, burst ? Catalog.QueuePriority.Reconcile : Catalog.QueuePriority.Event, Catalog.ScanMode.Shallow));
                Interlocked.Increment(ref _queued);
            }
            else dirs.Add(d);
        }
        if (dirs.Count > BurstThreshold)
        {
            // Burst: one reconciliation per affected location instead of thousands of rescans.
            foreach (var rootId in dirs.Select(d => scope?.FindRoot(d)?.RootId).Where(id => id != null).Distinct())
                _indexer.Post(new ReconcileCommand(rootId, Catalog.QueuePriority.Event));
            return;
        }
        foreach (var d in dirs)
        {
            _indexer.Post(new RescanPathCommand(d, Catalog.QueuePriority.Event, Catalog.ScanMode.Shallow));
            Interlocked.Increment(ref _queued);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
        _flushTimer.Dispose();
    }
}
