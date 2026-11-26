using Seekbeam.Core.Apps;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.SourceAccess;

namespace Seekbeam.Core.Discovery;

/// <summary>
/// Opportunistic discovery source (TD-11): the user's "Recent Items" folder, where the Windows shell drops a shortcut
/// each time a document is opened through Explorer, the common file dialogs or SHAddToRecentDocs. Non-intrusive: no
/// hooks, no drivers, no elevation; shortcuts are parsed read-only. Targets inside the indexing perimeter are handed
/// to <see cref="NameIndexer"/> (DiscoverCommand) and recorded as usage signals when learning is enabled.
/// </summary>
public sealed class RecentItemsMonitor : IDisposable
{
    private readonly NameIndexer _indexer;
    private readonly Action<string> _onOpened;
    private FileSystemWatcher? _watcher;
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.OrdinalIgnoreCase);
    public long EventsSeen { get; private set; }
    public long TargetsDiscovered { get; private set; }
    public string Folder { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Recent);

    public RecentItemsMonitor(NameIndexer indexer, Action<string> onOpened)
    {
        _indexer = indexer;
        _onOpened = onOpened;
    }

    public void Start()
    {
        if (_watcher != null || string.IsNullOrEmpty(Folder) || !SourceFileAccess.DirectoryExists(Folder)) return;
        _watcher = new FileSystemWatcher(Folder, "*.lnk") { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        _watcher.Created += (_, e) => Handle(e.FullPath);
        _watcher.Changed += (_, e) => Handle(e.FullPath);
        _watcher.Renamed += (_, e) => Handle(e.FullPath);
        _watcher.EnableRaisingEvents = true;
        Log.Info("Discovery", "Watching recent items");
    }

    public void Stop() { _watcher?.Dispose(); _watcher = null; }

    private void Handle(string lnkPath)
    {
        EventsSeen++;
        // The shell writes the .lnk then updates it: let it settle, and parse each shortcut at most once per 10 s.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(500);
            lock (_seen)
            {
                if (_seen.TryGetValue(lnkPath, out var t) && DateTime.UtcNow - t < TimeSpan.FromSeconds(10)) return;
                _seen[lnkPath] = DateTime.UtcNow;
                if (_seen.Count > 512) _seen.Clear();
            }
            var info = LnkParser.TryParseFile(lnkPath);
            if (info?.Target is not { Length: > 3 } target) return;
            ReportOpened(target);
        });
    }

    /// <summary>Called with a file opened outside the index (its folder may be learned by the folder index, TD-21).</summary>
    public Action<string>? OutsideIndex { get; set; }

    /// <summary>Entry point shared with "file opened from Seekbeam".</summary>
    public void ReportOpened(string target)
    {
        if (!SourceFileAccess.FileExists(target)) return;
        if (!_indexer.Scope.IsInScope(target, out var root) || root!.FoldersOnly)
        {
            try { OutsideIndex?.Invoke(target); } catch (Exception ex) { Log.Debug("Discovery", ex.Message); }
            return;
        }
        TargetsDiscovered++;
        _indexer.Post(new DiscoverCommand(target));
        try { _onOpened(target); } catch (Exception ex) { Log.Debug("Discovery", ex.Message); }
    }

    public void Dispose() => Stop();
}
