using System.Text.Json;
using System.Text.Json.Serialization;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Storage;

namespace Seekbeam.Core.Config;

public enum LauncherPosition { Center, NearCursor }
public enum CtrlSide { Both, Left, Right }
public enum ThemeMode { System, Light, Dark }
public enum ResourceMode { Auto, Economy, Balanced, Performance }
/// <summary>Isolation of the extractor host (TD-17). Auto = strongest level that works (AppContainer, else low integrity).</summary>
public enum SandboxMode { Auto, AppContainer, LowIntegrity, Off }

public sealed class SeekbeamConfig
{
    /// <summary>2: Content.ExcludedFolders (Downloads excluded from content extraction by default) and Content.Sandbox.
    /// 3: Apps.RunCommands and Apps.Aliases (the "hosts" alias is added once).</summary>
    public const int CurrentSchemaVersion = 3;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralConfig General { get; set; } = new();
    public HotkeyConfig Hotkey { get; set; } = new();
    public LauncherConfig Launcher { get; set; } = new();
    public SearchConfig Search { get; set; } = new();
    public List<RootConfig> Roots { get; set; } = [];
    public List<string> GlobalExcludes { get; set; } = [];
    public ContentConfig Content { get; set; } = new();
    public ResourceConfig Resources { get; set; } = new();
    public AppsConfig Apps { get; set; } = new();
    public PrivacyConfig Privacy { get; set; } = new();
    public WatchConfig Watch { get; set; } = new();
    public DiscoveryConfig Discovery { get; set; } = new();
    public NavigationConfig Navigation { get; set; } = new();
    public FolderIndexConfig FolderIndex { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();

    public static SeekbeamConfig CreateDefault()
    {
        var c = new SeekbeamConfig();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            c.Roots.Add(new RootConfig { Path = profile, Excludes = ["AppData", ".git", "node_modules", ".vs", "__pycache__", ".cache", ".nuget"] });
        c.GlobalExcludes = ["$Recycle.Bin", "System Volume Information", "$WinREAgent", "*.tmp", "~$*", "Thumbs.db"];
        c.Content.ExcludedFolders = [KnownFolders.Downloads];
        c.Apps.Aliases = [AliasConfig.Hosts()];
        return c;
    }

    /// <summary>Validates and clamps values; returns human-readable problems (non-fatal).</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        Launcher.MaxResults = Math.Clamp(Launcher.MaxResults, 1, 50);
        Launcher.Width = Math.Clamp(Launcher.Width, 400, 1600);
        Hotkey.DoubleTapMs = Math.Clamp(Hotkey.DoubleTapMs, 150, 1000);
        Search.FuzzyMinTokenLength = Math.Clamp(Search.FuzzyMinTokenLength, 3, 12);
        Content.MaxFileSizeMB = Math.Clamp(Content.MaxFileSizeMB, 1, 2048);
        Content.MaxCharsPerDocument = Math.Clamp(Content.MaxCharsPerDocument, 10_000, 20_000_000);
        Content.TimeoutSeconds = Math.Clamp(Content.TimeoutSeconds, 2, 600);
        Content.Archives.MaxDepth = Math.Clamp(Content.Archives.MaxDepth, 0, 5);
        Content.Archives.MaxEntries = Math.Clamp(Content.Archives.MaxEntries, 1, 1_000_000);
        Content.Archives.MaxCompressionRatio = Math.Clamp(Content.Archives.MaxCompressionRatio, 2, 10_000);
        Watch.ReconcileIntervalHours = Math.Clamp(Watch.ReconcileIntervalHours, 1, 24 * 30);
        Discovery.MaxDirectoriesPerHour = Math.Clamp(Discovery.MaxDirectoriesPerHour, 10, 1_000_000);
        FolderIndex.MaxDepth = Math.Clamp(FolderIndex.MaxDepth, 1, 64);
        FolderIndex.LearnedDepth = Math.Clamp(FolderIndex.LearnedDepth, 0, 8);
        FolderIndex.MaxLearned = Math.Clamp(FolderIndex.MaxLearned, 0, 2000);
        FolderIndex.ReconcileIntervalHours = Math.Clamp(FolderIndex.ReconcileIntervalHours, 1, 24 * 90);
        FolderIndex.Excludes = FolderIndex.Excludes.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        FolderIndex.ExcludedPaths = FolderIndex.ExcludedPaths.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        FolderIndex.LowerRanked = FolderIndex.LowerRanked.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var folders = new List<string>();
        foreach (var d in Content.ExcludedFolders)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            string full;
            try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(d.Trim())); if (full.EndsWith(':')) full += "\\"; }
            catch (Exception) { problems.Add("Invalid excluded content folder: " + d); continue; }
            if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase)) folders.Add(full);
        }
        Content.ExcludedFolders = folders;
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Apps.Aliases.ToList())
        {
            a.Keyword = (a.Keyword ?? "").Trim(); a.Target = (a.Target ?? "").Trim();
            a.Arguments = (a.Arguments ?? "").Trim(); a.WorkingDirectory = (a.WorkingDirectory ?? "").Trim();
            string? why = AliasConfig.Problem(a);
            if (why == null && !keywords.Add(a.Keyword)) why = "duplicate keyword";
            if (why != null) { Apps.Aliases.Remove(a); problems.Add($"Alias '{a.Keyword}' removed: {why}"); }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Roots.ToList())
        {
            if (string.IsNullOrWhiteSpace(r.Path)) { Roots.Remove(r); problems.Add("Empty root removed"); continue; }
            try { r.Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Path)); if (r.Path.EndsWith(':')) r.Path += "\\"; }
            catch (Exception) { problems.Add("Invalid root path: " + r.Path); Roots.Remove(r); continue; }
            if (!seen.Add(r.Path)) { Roots.Remove(r); problems.Add("Duplicate root removed: " + r.Path); }
            r.MaxDepth = r.MaxDepth < 0 ? -1 : Math.Min(r.MaxDepth, 256);
        }
        return problems;
    }
}

public sealed class GeneralConfig
{
    public bool StartWithWindows { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool FirstRunCompleted { get; set; }
    /// <summary>UI language code (en, fr, de, it). Empty = Windows display language (English if not supported).</summary>
    public string Language { get; set; } = "";
}

public sealed class HotkeyConfig
{
    public bool DoubleCtrlEnabled { get; set; } = true;
    public CtrlSide CtrlSide { get; set; } = CtrlSide.Both;
    public int DoubleTapMs { get; set; } = 400;
    /// <summary>Alternative hotkey, e.g. "Alt+Space", "Ctrl+Shift+F". Empty = none.</summary>
    public string AlternativeHotkey { get; set; } = "";
    public string DeepSearchHotkey { get; set; } = "";
}

public sealed class LauncherConfig
{
    public LauncherPosition Position { get; set; } = LauncherPosition.Center;
    public int MaxResults { get; set; } = 8;
    public double Width { get; set; } = 720;
    public bool Topmost { get; set; } = true;
    public bool CloseOnFocusLoss { get; set; } = true;
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public bool ShowPath { get; set; } = true;
}

public sealed class SearchConfig
{
    public bool FuzzyEnabled { get; set; } = true;
    public int FuzzyMinTokenLength { get; set; } = 4;
    public bool MatchInPath { get; set; } = true;
    public bool IncludeHidden { get; set; }
    public bool IncludeSystem { get; set; }
    public int MaxSearchThreads { get; set; }  // 0 = auto
    public RankingWeights Weights { get; set; } = new();
}

public sealed class RankingWeights
{
    public int ExactName { get; set; } = 1000;
    public int NamePrefix { get; set; } = 300;
    public int WordStart { get; set; } = 120;
    public int Substring { get; set; } = 40;
    public int AllInName { get; set; } = 200;
    public int InOrder { get; set; } = 60;
    public int CoverageMax { get; set; } = 150;
    public int App { get; set; } = 250;
    public int Folder { get; set; } = 20;
    public int UsageMax { get; set; } = 400;
    public double UsageHalfLifeDays { get; set; } = 14;
    public int Favorite { get; set; } = 400;
    public int FuzzyPenaltyPerError { get; set; } = 250;
    public int DepthPenalty { get; set; } = 5;
    /// <summary>Path fragments found along the path in the order typed ("sou per see").</summary>
    public int PathInOrder { get; set; } = 150;
    /// <summary>Per fragment found at the start of a folder name of the path.</summary>
    public int PathWordStart { get; set; } = 40;
    /// <summary>The last fragment names the item itself (the folder the user is heading for).</summary>
    public int LastTokenInName { get; set; } = 100;
    /// <summary>Path fragments that lead to the item, and its name starts with the last one ("progmicro" → Microsoft).</summary>
    public int LeafPrefix { get; set; } = 150;
    /// <summary>Folder of the folder index (outside the indexed locations): the locations win on equal relevance.</summary>
    public int OutsidePenalty { get; set; } = 30;
    /// <summary>Folder ranked lower (Program Files, ProgramData, AppData…).</summary>
    public int LoweredPenalty { get; set; } = 100;
}

public sealed class RootConfig
{
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public const int DefaultMaxDepth = 10;
    /// <summary>Levels explored below the root (its direct children are level 1). -1 = unlimited.</summary>
    public int MaxDepth { get; set; } = DefaultMaxDepth;
    public bool IndexContent { get; set; } = true;
    public bool FollowLinks { get; set; }
    public bool IncludeHidden { get; set; } = true;
    /// <summary>Glob patterns on names (e.g. "node_modules", "*.bak") or relative paths ("build\\out").</summary>
    public List<string> Excludes { get; set; } = [];
    public bool Watch { get; set; } = true;
}

public sealed class ContentConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxFileSizeMB { get; set; } = 50;
    public int MaxCharsPerDocument { get; set; } = 1_000_000;
    public bool StoreTextForSnippets { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;
    public List<string> DisabledExtractors { get; set; } = [];
    public List<string> ExtraTextExtensions { get; set; } = [];
    public List<string> ExcludedExtensions { get; set; } = [];
    /// <summary>
    /// Folders (and their subfolders) whose files are indexed by NAME only: their content is never read. Default: Downloads,
    /// where files from the Internet and e-mail land and get parsed automatically without being opened (SECURITY.md).
    /// </summary>
    public List<string> ExcludedFolders { get; set; } = [];
    /// <summary>Extractor host isolation. Change only for troubleshooting; takes effect when the extractor host restarts.</summary>
    public SandboxMode Sandbox { get; set; } = SandboxMode.Auto;
    public ArchiveConfig Archives { get; set; } = new();
    public OcrConfig Ocr { get; set; } = new();
}

public sealed class ArchiveConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxDepth { get; set; } = 2;
    public int MaxEntries { get; set; } = 10_000;
    public int MaxEntrySizeMB { get; set; } = 50;
    public int MaxTotalSizeMB { get; set; } = 200;
    public int MaxCompressionRatio { get; set; } = 100;
}

public sealed class OcrConfig
{
    public bool Enabled { get; set; }
    public int MaxImageSizeMB { get; set; } = 20;
    public List<string> Extensions { get; set; } = ["png", "jpg", "jpeg", "tif", "tiff", "bmp"];
}

public sealed class ResourceConfig
{
    public ResourceMode Mode { get; set; } = ResourceMode.Auto;
    public bool PauseInFullscreen { get; set; } = true;
    public bool EconomyOnBattery { get; set; } = true;
    public bool PauseContentOnBattery { get; set; }
    public int UserIdleSeconds { get; set; } = 120;
    public int ContentWorkers { get; set; }       // 0 = from mode
    public int ExtractorMemoryMB { get; set; }    // 0 = from mode
    /// <summary>Optional allowed time windows for content indexing, e.g. "22:00-07:00". Empty = always.</summary>
    public List<string> ContentTimeWindows { get; set; } = [];
}

public sealed class AppsConfig
{
    public bool StartMenu { get; set; } = true;
    public bool Desktop { get; set; } = true;
    public bool PackagedApps { get; set; } = true;
    public List<string> ExecutableFolders { get; set; } = [];
    /// <summary>Commands of the Run dialog (Win+R): PATH folders, App Paths, .msc and .cpl ("mstsc", "services.msc").</summary>
    public bool RunCommands { get; set; } = true;
    /// <summary>User keywords launching a program, file, folder or URI with fixed arguments.</summary>
    public List<AliasConfig> Aliases { get; set; } = [];
}

/// <summary>
/// A keyword typed in the launcher ("hosts") that launches <see cref="Target"/> with <see cref="Arguments"/>.
/// Environment variables (%SystemRoot%, %USERPROFILE%…) are expanded at launch time. Seekbeam only asks Windows to
/// start the target: what that program then does is outside Seekbeam's control (FILE_IMMUTABILITY_POLICY §8).
/// </summary>
public sealed class AliasConfig
{
    public string Keyword { get; set; } = "";
    /// <summary>Program, document, folder or URI (ms-settings:, https:…).</summary>
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public bool RunAsAdmin { get; set; }

    /// <summary>Opens the hosts file in Notepad, elevated (saving it requires administrator rights).</summary>
    public static AliasConfig Hosts() => new()
    {
        Keyword = "hosts", Target = @"%SystemRoot%\System32\notepad.exe",
        Arguments = @"%SystemRoot%\System32\drivers\etc\hosts", RunAsAdmin = true,
    };

    public const string EmptyKeyword = "empty keyword", SpaceInKeyword = "keyword contains a space", EmptyTarget = "empty target";

    /// <summary>Why the alias is unusable (a code, not a sentence for the UI), or null.</summary>
    public static string? Problem(AliasConfig a) =>
        string.IsNullOrWhiteSpace(a.Keyword) ? EmptyKeyword
        : a.Keyword.Trim().Any(char.IsWhiteSpace) ? SpaceInKeyword
        : string.IsNullOrWhiteSpace(a.Target) ? EmptyTarget
        : null;
}

public sealed class PrivacyConfig
{
    public bool QueryHistory { get; set; } = true;
    public bool Learning { get; set; } = true;
    public int MaxQueryHistory { get; set; } = 500;
}

public sealed class WatchConfig
{
    public bool Enabled { get; set; } = true;
    public bool ReconcileAtStartup { get; set; } = true;
    public int ReconcileIntervalHours { get; set; } = 24;
}

public sealed class DiscoveryConfig
{
    public bool Enabled { get; set; } = true;
    public bool WatchRecentItems { get; set; } = true;
    public int MaxDirectoriesPerHour { get; set; } = 2000;
}

/// <summary>
/// Driving the Open/Save/folder dialogs and Explorer windows from the launcher (Listary-like). Every option can be
/// switched off on its own. Seekbeam only navigates those windows: it never validates a dialog (TECHNICAL_DECISIONS TD-20).
/// </summary>
public sealed class NavigationConfig
{
    /// <summary>Double Ctrl over an Open/Save/folder dialog opens the launcher in "go to folder" mode for that dialog.</summary>
    public bool DialogIntegration { get; set; } = true;
    /// <summary>Typing a letter or digit in the file list of a dialog opens the launcher with it (Esc gives the letters back).</summary>
    public bool DirectTyping { get; set; } = true;
    /// <summary>Double Ctrl over an Explorer window: choosing a folder navigates that window.</summary>
    public bool Explorer { get; set; } = true;
    /// <summary>Ctrl+G in a dialog jumps to the folder of the last Explorer window; open Explorer folders are listed.</summary>
    public bool QuickSwitch { get; set; } = true;
    /// <summary>With an empty query, the "go to folder" mode lists favourite and recent folders.</summary>
    public bool RecentFolders { get; set; } = true;
    /// <summary>Typing an absolute path ("C:\Win", "\\server\", "%APPDATA%\") lists the matching entries live from the disk.</summary>
    public bool PathCompletion { get; set; } = true;
}

/// <summary>
/// Lightweight index of FOLDER names outside the indexed locations (TD-21): the whole tree of the fixed local drives
/// (minus exclusions) and folders learned from use (network or removable locations). Files and content are never read
/// there: only directory listings, through SourceFileAccess.
/// </summary>
public sealed class FolderIndexConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Walk the folder tree of every fixed local drive.</summary>
    public bool FixedDrives { get; set; } = true;
    /// <summary>Remember folders outside the index that are used (dialogs, Explorer, recent items), with a few levels below.</summary>
    public bool LearnFolders { get; set; } = true;
    public int MaxDepth { get; set; } = 16;
    /// <summary>Levels indexed below a learned folder.</summary>
    public int LearnedDepth { get; set; } = 2;
    public int MaxLearned { get; set; } = 200;
    /// <summary>Full re-walk of the drive trees (directory events are watched in between).</summary>
    public int ReconcileIntervalHours { get; set; } = 24 * 7;
    /// <summary>Folder names or relative-path patterns never indexed (same syntax as the location exclusions).</summary>
    public List<string> Excludes { get; set; } = DefaultExcludes();
    /// <summary>Absolute folders never indexed (environment variables allowed).</summary>
    public List<string> ExcludedPaths { get; set; } = [@"%SystemRoot%"];
    /// <summary>Folders ranked lower: absolute paths (environment variables allowed) or a folder name matching anywhere.</summary>
    public List<string> LowerRanked { get; set; } = [@"%ProgramFiles%", @"%ProgramFiles(x86)%", @"%ProgramData%", "AppData"];

    public static List<string> DefaultExcludes() =>
    [
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS", "Config.Msi", "Recovery",
        "WinSxS", "WindowsApps", "node_modules", ".git", ".svn", ".hg", "bin", "obj", "__pycache__", ".vs", ".idea", ".gradle",
        "Cache", "Code Cache", "GPUCache", "cache2", "CacheStorage", "Service Worker", "INetCache", "Temporary Internet Files",
        @"**\AppData\Local\Temp", @"**\AppData\Local\Packages", @"**\.nuget\packages",
    ];
}

public sealed class LoggingConfig
{
    public LogLevel Level { get; set; } = LogLevel.Info;
    public bool RedactPaths { get; set; }
    public int MaxFiles { get; set; } = 5;
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(SeekbeamConfig))]
internal partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>Loads/saves config.json via SeekbeamStorage (atomic replace). Raises <see cref="Changed"/>.</summary>
public sealed class ConfigStore
{
    private const string FileName = "config.json";
    private readonly SeekbeamStorage _storage;
    private readonly object _lock = new();
    public SeekbeamConfig Current { get; private set; }
    public event Action<SeekbeamConfig>? Changed;

    public ConfigStore(SeekbeamStorage storage)
    {
        _storage = storage;
        Current = Load();
    }

    private SeekbeamConfig Load()
    {
        string? json = null;
        try { json = _storage.ReadAllText(FileName); }
        catch (Exception ex) { Log.Error("Config", "Cannot read config", ex); }
        SeekbeamConfig cfg;
        if (json == null) cfg = SeekbeamConfig.CreateDefault();
        else
        {
            try { cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.SeekbeamConfig) ?? SeekbeamConfig.CreateDefault(); }
            catch (JsonException ex)
            {
                Log.Error("Config", "Invalid config.json, keeping a backup and using defaults", ex);
                _storage.WriteAllTextAtomic("config.invalid.json", json);
                cfg = SeekbeamConfig.CreateDefault();
            }
        }
        bool migrated = json != null && Migrate(cfg);
        foreach (var p in cfg.Validate()) Log.Warn("Config", p);
        if (json == null || migrated) Save(cfg, raise: false);
        return cfg;
    }

    /// <summary>Upgrades an older config in place; returns true if anything changed (the file is then rewritten).</summary>
    private static bool Migrate(SeekbeamConfig cfg)
    {
        // Schema migrations go here (SchemaVersion < CurrentSchemaVersion).
        if (cfg.SchemaVersion < 2 && !cfg.Content.ExcludedFolders.Any(KnownFolders.IsDownloads))
            cfg.Content.ExcludedFolders.Add(KnownFolders.Downloads); // content of Downloads is no longer read by default
        if (cfg.SchemaVersion < 3 && !cfg.Apps.Aliases.Any(a => "hosts".Equals(a.Keyword, StringComparison.OrdinalIgnoreCase)))
            cfg.Apps.Aliases.Add(AliasConfig.Hosts());
        bool changed = cfg.SchemaVersion != SeekbeamConfig.CurrentSchemaVersion;
        cfg.SchemaVersion = SeekbeamConfig.CurrentSchemaVersion;
        return changed;
    }

    public static string Serialize(SeekbeamConfig cfg) => JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.SeekbeamConfig);
    public static SeekbeamConfig Clone(SeekbeamConfig cfg) => JsonSerializer.Deserialize(Serialize(cfg), ConfigJsonContext.Default.SeekbeamConfig)!;

    public void Save(SeekbeamConfig cfg, bool raise = true)
    {
        lock (_lock)
        {
            cfg.Validate();
            _storage.WriteAllTextAtomic(FileName, Serialize(cfg));
            Current = cfg;
        }
        if (raise) Changed?.Invoke(cfg);
    }

    public void Update(Action<SeekbeamConfig> mutate)
    {
        var copy = Clone(Current);
        mutate(copy);
        Save(copy);
    }
}
