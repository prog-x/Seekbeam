using System.Runtime.InteropServices;
using Seekbeam.Core.Config;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.SourceAccess;

namespace Seekbeam.Core.Apps;

/// <summary>
/// Builds the application index: Start Menu (user + common) and Desktop (user + public) shortcuts parsed with
/// <see cref="LnkParser"/>, packaged apps (UWP/MSIX) enumerated from shell:AppsFolder, .exe files of configured
/// folders, Win+R commands (<see cref="RunCommands"/>) and user aliases. Everything is read-only.
/// Run commands are refreshed on their own timer (<see cref="CommandsRefreshInterval"/>): a listing of the PATH folders
/// costs a few milliseconds, whereas watching System32 would wake Seekbeam at every Windows update.
/// </summary>
public sealed class AppScanner : IDisposable
{
    private readonly AppCatalog _catalog;
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _debounce;
    private Timer? _commandsTimer;
    private AppsConfig _cfg = new();
    private readonly object _publishLock = new();
    private List<AppEntry> _base = [];
    private List<AppEntry> _commands = [];
    public static readonly TimeSpan CommandsRefreshInterval = TimeSpan.FromMinutes(15);
    public DateTime LastRefreshUtc { get; private set; }
    public TimeSpan LastRefreshDuration { get; private set; }

    public AppScanner(AppCatalog catalog) { _catalog = catalog; }

    public static IEnumerable<string> StartMenuFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
    ];

    public static IEnumerable<string> DesktopFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
    ];

    public void Configure(AppsConfig cfg)
    {
        _cfg = cfg;
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _debounce ??= new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        _commandsTimer ??= new Timer(_ => RefreshCommands(), null, Timeout.Infinite, Timeout.Infinite);
        _commandsTimer.Change(cfg.RunCommands ? CommandsRefreshInterval : Timeout.InfiniteTimeSpan,
            cfg.RunCommands ? CommandsRefreshInterval : Timeout.InfiniteTimeSpan);
        var folders = new List<string>();
        if (cfg.StartMenu) folders.AddRange(StartMenuFolders());
        if (cfg.Desktop) folders.AddRange(DesktopFolders());
        folders.AddRange(cfg.ExecutableFolders);
        foreach (var f in folders.Where(f => !string.IsNullOrEmpty(f) && SourceFileAccess.DirectoryExists(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var w = new FileSystemWatcher(f) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
                FileSystemEventHandler h = (_, _) => _debounce.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
                w.Created += h; w.Deleted += h; w.Changed += h; w.Renamed += (_, _) => _debounce.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { Log.Debug("Apps", "Cannot watch app folder: " + ex.Message); }
        }
    }

    public void Refresh()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var apps = new List<AppEntry>();
        try
        {
            if (_cfg.StartMenu) foreach (var f in StartMenuFolders()) ScanShortcuts(f, AppSource.StartMenu, apps, maxDepth: 6);
            if (_cfg.Desktop) foreach (var f in DesktopFolders()) ScanShortcuts(f, AppSource.Desktop, apps, maxDepth: 0);
            foreach (var f in _cfg.ExecutableFolders) ScanExecutables(f, apps, maxDepth: 2);
            if (_cfg.PackagedApps)
            {
                var known = new HashSet<string>(apps.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var a in AppsFolder.Enumerate())
                    if (a.AppUserModelId != null && !known.Contains(a.Name)) apps.Add(a);
            }
        }
        catch (Exception ex) { Log.Error("Apps", "Refresh failed", ex); }
        lock (_publishLock) _base = apps;
        RefreshCommands();
        LastRefreshUtc = DateTime.UtcNow;
        LastRefreshDuration = sw.Elapsed;
        Log.Info("Apps", $"{_catalog.Count} applications indexed in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>Rescans the Win+R commands only, then republishes the catalog.</summary>
    public void RefreshCommands()
    {
        List<AppEntry> commands = [];
        try { if (_cfg.RunCommands) commands = RunCommands.Scan(); }
        catch (Exception ex) { Log.Warn("Apps", "Run commands scan failed: " + ex.Message); }
        lock (_publishLock)
        {
            _commands = commands;
            var aliases = _cfg.Aliases.Select(AppEntry.FromAlias).OfType<AppEntry>();
            _catalog.Replace(aliases.Concat(_base).Concat(_commands));
        }
    }

    private static readonly string[] UninstallWords = ["uninstall", "désinstaller", "desinstaller", "uninst", "remove "];

    private static void ScanShortcuts(string folder, AppSource source, List<AppEntry> into, int maxDepth)
    {
        if (string.IsNullOrEmpty(folder) || !SourceFileAccess.DirectoryExists(folder)) return;
        using var reader = SourceFileAccess.CreateDirectoryReader();
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((folder, 0));
        var entries = new List<(string Name, bool Dir)>();
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            entries.Clear();
            reader.Read(dir, (in RawEntry e) => entries.Add((e.Name.ToString(), e.IsDirectory && !e.IsReparsePoint)), out _);
            foreach (var (name, isDir) in entries)
            {
                string full = Path.Combine(dir, name);
                if (isDir) { if (depth < maxDepth) stack.Push((full, depth + 1)); continue; }
                string ext = Path.GetExtension(name).ToLowerInvariant();
                string display = Path.GetFileNameWithoutExtension(name);
                if (UninstallWords.Any(w => display.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                if (ext == ".lnk")
                {
                    var info = LnkParser.TryParseFile(full);
                    if (info?.Target is { } t && t.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) continue;
                    into.Add(AppEntry.Create(display, full, source, info?.Target, info?.Arguments, info?.WorkingDirectory,
                        info?.IconLocation is { Length: > 0 } icon ? icon + "," + info.IconIndex : null));
                }
                else if (ext is ".url" or ".appref-ms" or ".exe")
                    into.Add(AppEntry.Create(display, full, source, ext == ".exe" ? full : null));
            }
        }
    }

    private static void ScanExecutables(string folder, List<AppEntry> into, int maxDepth)
    {
        if (!SourceFileAccess.DirectoryExists(folder)) return;
        using var reader = SourceFileAccess.CreateDirectoryReader();
        var stack = new Stack<(string, int)>();
        stack.Push((folder, 0));
        var entries = new List<(string Name, bool Dir)>();
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            entries.Clear();
            reader.Read(dir, (in RawEntry e) => entries.Add((e.Name.ToString(), e.IsDirectory && !e.IsReparsePoint)), out _);
            foreach (var (name, isDir) in entries)
            {
                string full = Path.Combine(dir, name);
                if (isDir) { if (depth < maxDepth) stack.Push((full, depth + 1)); }
                else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    into.Add(AppEntry.Create(Path.GetFileNameWithoutExtension(name), full, AppSource.ExecutableFolder, full));
            }
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _debounce?.Dispose();
        _commandsTimer?.Dispose();
    }
}

/// <summary>Enumerates shell:AppsFolder (all Start apps incl. UWP/MSIX with their AppUserModelID). Read-only COM.</summary>
public static class AppsFolder
{
    public static List<AppEntry> Enumerate()
    {
        var res = new List<AppEntry>();
        // COM shell objects prefer STA: run the enumeration on a dedicated STA thread.
        var t = new Thread(() =>
        {
            // Shell.Application (IDispatch) is the most reliable way to enumerate this virtual folder; read-only.
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return;
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                if (folder == null) return;
                foreach (dynamic item in folder.Items())
                {
                    string? name = item.Name as string, parsing = item.Path as string;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsing)) continue;
                    if (parsing.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue; // web links pinned as "apps"
                    bool packaged = parsing.Contains('!') && !parsing.Contains('\\');
                    res.Add(AppEntry.Create(name, "shell:AppsFolder\\" + parsing, AppSource.Packaged, aumid: packaged ? parsing : null));
                }
                Marshal.FinalReleaseComObject(shell);
            }
            catch (Exception ex) { Log.Warn("Apps", "AppsFolder enumeration failed: " + ex.Message); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join(TimeSpan.FromSeconds(20));
        return res;
    }

}
