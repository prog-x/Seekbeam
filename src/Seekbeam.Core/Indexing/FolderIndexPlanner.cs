using Seekbeam.Core.Config;

namespace Seekbeam.Core.Indexing;

/// <summary>One start point of the folder index (a root of kind <see cref="RootKind.FolderIndex"/>).</summary>
/// <param name="Origin">Why it exists: a fixed drive, a folder excluded from an indexed location, or a learned folder.</param>
public sealed record FolderIndexRoot(string Path, FolderIndexOrigin Origin, RootConfig Config);

public enum FolderIndexOrigin { Drive, ExcludedFromLocation, Learned }

/// <summary>
/// Decides where the folder index walks (TD-21). Pure function of the configuration and of a few facts about the
/// machine, so that it can be tested without touching the disk:
/// <list type="bullet">
/// <item>every fixed drive, unless an indexed location already covers the whole drive;</item>
/// <item>the folders an indexed location excludes by name at its top level ("AppData" under the profile): the drive walk
/// stops at an indexed location, so they would otherwise never be reached;</item>
/// <item>the folders learned from use that nothing else covers (network shares, removable drives, excluded areas are not).</item>
/// </list>
/// The indexed locations themselves are never walked again: the folder index skips them (ScopeRules.IsOtherRoot).
/// </summary>
public static class FolderIndexPlanner
{
    public static List<FolderIndexRoot> Plan(SeekbeamConfig cfg, IEnumerable<string> fixedDrives, IEnumerable<string> learned,
        Func<string, bool> directoryExists, string dataRoot)
    {
        var res = new List<FolderIndexRoot>();
        var fi = cfg.FolderIndex;
        if (!fi.Enabled) return res;
        var locations = ScopeRules.FromConfig(cfg, dataRoot);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locationPaths = new HashSet<string>(locations.Locations.Select(l => Normalize(l.Path)), StringComparer.OrdinalIgnoreCase);
        var drives = fixedDrives.Select(Normalize).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var excludedPrefixes = ExpandedPrefixes(fi.ExcludedPaths);
        var excludeNames = new ExclusionMatcher(fi.Excludes);

        void Add(string path, FolderIndexOrigin origin, int depth, bool watch)
        {
            string p = Normalize(path);
            if (p.Length == 0 || !seen.Add(p)) return;
            if (locationPaths.Contains(p)) return;
            if (excludedPrefixes.Any(x => (p + "\\").StartsWith(x, StringComparison.OrdinalIgnoreCase))) return;
            res.Add(new FolderIndexRoot(p, origin, new RootConfig
            {
                Path = p, Enabled = true, MaxDepth = depth, IndexContent = false, FollowLinks = false, IncludeHidden = true,
                Excludes = [.. fi.Excludes], Watch = watch,
            }));
        }

        if (fi.FixedDrives)
        {
            foreach (var d in drives) Add(d, FolderIndexOrigin.Drive, fi.MaxDepth, watch: true);
            // Top-level folders an indexed location excludes by name (AppData under the profile, .vs, .cache…).
            foreach (var loc in locations.Locations)
            {
                if (!drives.Any(d => IsUnder(Normalize(loc.Path), d))) continue; // only on the walked drives
                foreach (var pattern in loc.Config.Excludes)
                {
                    string name = pattern.Trim().Trim('\\', '/');
                    if (name.Length == 0 || name.IndexOfAny(['*', '?', '\\', '/']) >= 0) continue;
                    if (excludeNames.IsExcluded(name, null)) continue; // excluded from the folder index too (node_modules, .git…)
                    string child = Combine(Normalize(loc.Path), name);
                    if (locations.IsInScope(child, out _)) continue;   // another location covers it
                    if (directoryExists(child)) Add(child, FolderIndexOrigin.ExcludedFromLocation, fi.MaxDepth, watch: false);
                }
            }
        }
        if (fi.LearnFolders)
        {
            foreach (var l in learned)
            {
                string p = Normalize(l);
                if (p.Length == 0 || locations.IsInScope(p, out _)) continue;
                if (fi.FixedDrives && drives.Any(d => IsUnder(p, d))) continue; // the drive walk covers it (or excludes it on purpose)
                Add(p, FolderIndexOrigin.Learned, fi.LearnedDepth, watch: false);
            }
        }
        return res;
    }

    /// <summary>Absolute folders with environment variables expanded, normalized with a trailing separator.</summary>
    public static string[] ExpandedPrefixes(IEnumerable<string> paths) =>
        paths.Select(p => Environment.ExpandEnvironmentVariables(p.Trim()))
             .Where(p => p.Length > 2 && !p.Contains('%') && System.IO.Path.IsPathFullyQualified(p))
             .Select(p => Normalize(p) + (Normalize(p).EndsWith('\\') ? "" : "\\"))
             .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Names (no separator) of <see cref="FolderIndexConfig.LowerRanked"/>.</summary>
    public static HashSet<string> LoweredNames(IEnumerable<string> entries) =>
        new(entries.Select(e => e.Trim()).Where(e => e.Length > 0 && e.IndexOfAny(['\\', '/', ':', '%']) < 0), StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            string p = System.IO.Path.GetFullPath(path.Trim());
            p = System.IO.Path.TrimEndingDirectorySeparator(p);
            return p.EndsWith(':') ? p + "\\" : p;
        }
        catch (Exception) { return ""; }
    }

    public static bool IsUnder(string path, string folder)
    {
        string f = System.IO.Path.TrimEndingDirectorySeparator(folder);
        if (path.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        return path.Length > f.Length && path.StartsWith(f, StringComparison.OrdinalIgnoreCase) && (f.EndsWith('\\') || path[f.Length] == '\\');
    }

    private static string Combine(string dir, string name) => dir.EndsWith('\\') ? dir + name : dir + "\\" + name;
}
