using System.Text.RegularExpressions;
using Seekbeam.Core.Config;

namespace Seekbeam.Core.Indexing;

/// <summary>Fast exclusion matcher compiled from glob patterns (names like "node_modules", "*.tmp", "~$*", or relative paths "build\out").</summary>
public sealed class ExclusionMatcher
{
    private readonly HashSet<string> _exactNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase); // from "*.ext"
    private readonly List<string> _prefixes = [];                                           // from "abc*"
    private readonly List<Regex> _nameRegexes = [];
    private readonly List<Regex> _pathRegexes = [];                                         // patterns containing a separator, matched on relative path

    public ExclusionMatcher(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            string p = raw.Trim().Replace('/', '\\').Trim('\\');
            if (p.Length == 0) continue;
            if (p.Contains('\\')) { _pathRegexes.Add(GlobToRegex(p)); continue; }
            bool hasWild = p.IndexOfAny(['*', '?']) >= 0;
            if (!hasWild) _exactNames.Add(p);
            else if (p.StartsWith("*.") && p.IndexOfAny(['*', '?'], 2) < 0) _extensions.Add(p[2..]);
            else if (p.EndsWith('*') && p.IndexOfAny(['*', '?']) == p.Length - 1) _prefixes.Add(p[..^1]);
            else _nameRegexes.Add(GlobToRegex(p));
        }
    }

    public bool IsEmpty => _exactNames.Count == 0 && _extensions.Count == 0 && _prefixes.Count == 0 && _nameRegexes.Count == 0 && _pathRegexes.Count == 0;

    private static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace(@"\*\*\\", "(.*\\\\)?").Replace(@"\*", "[^\\\\]*").Replace(@"\?", "[^\\\\]") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    /// <param name="name">Entry name.</param>
    /// <param name="relativePath">Path relative to the root (may be null when unknown).</param>
    public bool IsExcluded(string name, string? relativePath)
    {
        if (_exactNames.Count > 0 && _exactNames.Contains(name)) return true;
        if (_extensions.Count > 0)
        {
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && _extensions.Contains(name[(dot + 1)..])) return true;
        }
        foreach (var pre in _prefixes) if (name.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            foreach (var r in _nameRegexes) if (r.IsMatch(name)) return true;
            if (relativePath != null) foreach (var r in _pathRegexes) if (r.IsMatch(relativePath)) return true;
        }
        catch (RegexMatchTimeoutException) { }
        return false;
    }
}

/// <summary>Effective configuration of one indexed root.</summary>
public sealed class RootScope
{
    public required long RootId { get; init; }
    public required string Path { get; init; }
    public required RootConfig Config { get; init; }
    public required ExclusionMatcher Excludes { get; init; }
    public long RootEntryId { get; set; }

    public string? Relative(string fullPath)
    {
        string root = System.IO.Path.TrimEndingDirectorySeparator(Path);
        if (fullPath.Length < root.Length || !fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return fullPath.Length == root.Length ? "" : fullPath[(root.Length + (Path.EndsWith('\\') ? 0 : 1))..];
    }
}

/// <summary>
/// Indexing perimeter: roots, exclusions (global + per root), depth limits, hidden files, and the Seekbeam data
/// directory which is always excluded (it is never a protected source — FILE_IMMUTABILITY_POLICY §2).
/// </summary>
public sealed class ScopeRules
{
    private readonly ExclusionMatcher _global;
    private readonly string _dataRoot;
    public IReadOnlyList<RootScope> Roots { get; }

    public ScopeRules(IEnumerable<RootScope> roots, IEnumerable<string> globalExcludes, string dataRoot)
    {
        Roots = roots.OrderByDescending(r => r.Path.Length).ToList();
        _global = new ExclusionMatcher(globalExcludes);
        _dataRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dataRoot));
    }

    /// <summary>Perimeter computed directly from a configuration (enabled roots), for checks made before the indexer runs.</summary>
    public static ScopeRules FromConfig(SeekbeamConfig cfg, string dataRoot)
    {
        long id = 0;
        var roots = cfg.Roots.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new RootScope { RootId = ++id, Path = r.Path, Config = r, Excludes = new ExclusionMatcher(r.Excludes) });
        return new ScopeRules(roots, cfg.GlobalExcludes, dataRoot);
    }

    /// <summary>
    /// True if writing at <paramref name="fullPath"/> could touch a protected source: the path is inside the perimeter, or an
    /// indexed root lies below it (an inheritable change there would reach the root). Used to protect Seekbeam's few writes
    /// outside its data directory (sandbox provisioning).
    /// </summary>
    public bool TouchesScope(string fullPath)
    {
        string p = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(fullPath));
        if (IsInScope(p, out _)) return true;
        foreach (var r in Roots)
        {
            string rp = System.IO.Path.TrimEndingDirectorySeparator(r.Path);
            if (rp.Length > p.Length && rp.StartsWith(p, StringComparison.OrdinalIgnoreCase) && (rp[p.Length] == '\\' || p.EndsWith('\\'))) return true;
        }
        return false;
    }

    /// <summary>The most specific root containing <paramref name="fullPath"/>, or null.</summary>
    public RootScope? FindRoot(string fullPath)
    {
        foreach (var r in Roots)
        {
            string rp = System.IO.Path.TrimEndingDirectorySeparator(r.Path);
            if (fullPath.Equals(rp, StringComparison.OrdinalIgnoreCase)) return r;
            if (fullPath.Length > rp.Length && fullPath.StartsWith(rp, StringComparison.OrdinalIgnoreCase)
                && (fullPath[rp.Length] == '\\' || rp.EndsWith('\\'))) return r;
        }
        return null;
    }

    public RootScope? ById(long rootId) => Roots.FirstOrDefault(r => r.RootId == rootId);

    public bool IsDataDirectory(string fullPath) =>
        fullPath.Equals(_dataRoot, StringComparison.OrdinalIgnoreCase)
        || (fullPath.Length > _dataRoot.Length && fullPath.StartsWith(_dataRoot, StringComparison.OrdinalIgnoreCase) && fullPath[_dataRoot.Length] == '\\');

    /// <summary>True if a child named <paramref name="name"/> at <paramref name="fullPath"/> must not be indexed.</summary>
    public bool IsExcluded(RootScope root, string name, string fullPath, FileAttributes attrs)
    {
        if (IsDataDirectory(fullPath)) return true;
        if (!root.Config.IncludeHidden && (attrs & FileAttributes.Hidden) != 0) return true;
        string? rel = null;
        if (_global.IsExcluded(name, null)) return true;
        if (root.Excludes.IsEmpty) return false;
        rel = root.Relative(fullPath);
        return root.Excludes.IsExcluded(name, rel);
    }

    /// <summary>True if <paramref name="fullPath"/> (any depth) is inside the perimeter: in a root and no ancestor excluded.</summary>
    public bool IsInScope(string fullPath, out RootScope? root)
    {
        root = FindRoot(fullPath);
        if (root == null || IsDataDirectory(fullPath)) return false;
        string? rel = root.Relative(fullPath);
        if (string.IsNullOrEmpty(rel)) return true;
        string cur = System.IO.Path.TrimEndingDirectorySeparator(root.Path);
        int depth = 0;
        foreach (var part in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur + "\\" + part;
            depth++;
            if (_global.IsExcluded(part, null) || root.Excludes.IsExcluded(part, root.Relative(cur))) return false;
        }
        return root.Config.MaxDepth < 0 || depth <= root.Config.MaxDepth;
    }
}

/// <summary>Decides whether a file's content should be extracted.</summary>
public sealed class ContentEligibility
{
    private readonly HashSet<string> _extensions;
    private readonly HashSet<string> _excluded;
    private readonly string[] _excludedFolders; // normalized, with a trailing separator
    public bool Enabled { get; }
    public long MaxBytes { get; }

    public ContentEligibility(ContentConfig cfg, IEnumerable<string> supportedExtensions)
    {
        Enabled = cfg.Enabled;
        MaxBytes = cfg.MaxFileSizeMB * 1024L * 1024L;
        _extensions = new HashSet<string>(supportedExtensions.Select(e => e.TrimStart('.')), StringComparer.OrdinalIgnoreCase);
        foreach (var e in cfg.ExtraTextExtensions) _extensions.Add(e.TrimStart('.'));
        _excluded = new HashSet<string>(cfg.ExcludedExtensions.Select(e => e.TrimStart('.')), StringComparer.OrdinalIgnoreCase);
        if (!cfg.Archives.Enabled) _excluded.Add("zip");
        _excludedFolders = cfg.ExcludedFolders.Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => System.IO.Path.TrimEndingDirectorySeparator(d) + "\\").ToArray();
    }

    public static readonly ContentEligibility None = new(new ContentConfig { Enabled = false }, []);

    /// <summary>True if <paramref name="fullPath"/> lies in a folder whose content must never be read (Content.ExcludedFolders).</summary>
    public bool IsInExcludedFolder(string fullPath)
    {
        foreach (var d in _excludedFolders)
            if (fullPath.Length >= d.Length && fullPath.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public bool IsEligible(RootScope root, string fullPath, long size, FileAttributes attrs) =>
        Check(root, fullPath, size, attrs) == ContentIneligibility.None;

    /// <summary>Why the content of a file is not extracted (<see cref="ContentIneligibility.None"/> = it is eligible).</summary>
    public ContentIneligibility Check(RootScope root, string fullPath, long size, FileAttributes attrs) =>
        Check(root, System.IO.Path.GetFileName(fullPath.AsSpan()), size, attrs,
              _excludedFolders.Length > 0 && IsInExcludedFolder(fullPath));

    /// <summary>
    /// The rules themselves (single source of truth), without the full path: <paramref name="inExcludedFolder"/> is
    /// decided by the caller (path prefix above, or ancestor ids in <see cref="Content.ContentCoverage"/>).
    /// </summary>
    public ContentIneligibility Check(RootScope root, ReadOnlySpan<char> fileName, long size, FileAttributes attrs, bool inExcludedFolder)
    {
        if (!Enabled) return ContentIneligibility.Disabled;
        if (!root.Config.IndexContent) return ContentIneligibility.RootDisabled;
        if (size == 0) return ContentIneligibility.EmptyFile; // empty files have no content
        if (size > MaxBytes) return ContentIneligibility.TooLarge;
        if ((((uint)attrs) & (SourceAccess.SourceFileAccess.RecallOnDataAccess | SourceAccess.SourceFileAccess.RecallOnOpen | (uint)FileAttributes.Offline)) != 0)
            return ContentIneligibility.CloudPlaceholder; // reading would hydrate it (TD-13)
        if ((attrs & FileAttributes.ReparsePoint) != 0 && !root.Config.FollowLinks) return ContentIneligibility.Link;
        if (inExcludedFolder) return ContentIneligibility.ExcludedFolder; // e.g. Downloads: names only
        string ext = Extension(fileName);
        if (_excluded.Contains(ext)) return ContentIneligibility.ExcludedType;
        return _extensions.Contains(ext) ? ContentIneligibility.None : ContentIneligibility.UnsupportedType;
    }

    /// <summary>Extension used by the rules: after the last dot, or the whole name for "Dockerfile"/"Makefile".</summary>
    public static string Extension(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return (dot >= 0 ? fileName[(dot + 1)..] : fileName).ToString();
    }

    /// <summary>True if some folders are excluded from content indexing (callers can skip ancestry checks otherwise).</summary>
    public bool HasExcludedFolders => _excludedFolders.Length > 0;

    /// <summary>The excluded folders (normalized, trailing separator).</summary>
    public IReadOnlyList<string> ExcludedFolders => _excludedFolders;
}

/// <summary>Reason why a file's content is not extracted (a code, translated by the UI).</summary>
public enum ContentIneligibility { None, Disabled, RootDisabled, EmptyFile, TooLarge, CloudPlaceholder, Link, ExcludedFolder, ExcludedType, UnsupportedType }
