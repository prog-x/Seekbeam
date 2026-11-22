using Seekbeam.Core.Search;
using Seekbeam.Core.Text;
using Seekbeam.Core.Config;

namespace Seekbeam.Core.Apps;

public enum AppSource { StartMenu = 0, Desktop = 1, Packaged = 2, ExecutableFolder = 3, AppsFolder = 4 }

/// <summary>A launchable application. <see cref="AppUserModelId"/> is set for packaged (UWP/MSIX) apps.</summary>
public sealed class AppEntry
{
    public required string Name { get; init; }
    /// <summary>Path of the shortcut (.lnk) or executable, or "shell:AppsFolder\{AUMID}" for packaged apps.</summary>
    public required string LaunchPath { get; init; }
    public string? Target { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? IconLocation { get; init; }
    public string? AppUserModelId { get; init; }
    public AppSource Source { get; init; }
    public string Key => AppUserModelId != null ? "app:" + AppUserModelId : LaunchPath;
    internal byte[] NormalizedName { get; init; } = [];
    internal byte[] NormalizedTarget { get; init; } = [];

    public static AppEntry Create(string name, string launchPath, AppSource source, string? target = null, string? args = null,
        string? workdir = null, string? icon = null, string? aumid = null) => new()
    {
        Name = name, LaunchPath = launchPath, Source = source, Target = target, Arguments = args, WorkingDirectory = workdir,
        IconLocation = icon, AppUserModelId = aumid,
        NormalizedName = Normalizer.NormalizeToBytes(name),
        NormalizedTarget = Normalizer.NormalizeToBytes(Path.GetFileNameWithoutExtension(target ?? "") ?? ""),
    };
}

/// <summary>In-memory application index (hundreds to a few thousand entries). Thread-safe snapshot swap.</summary>
public sealed class AppCatalog
{
    private volatile AppEntry[] _apps = [];
    public IReadOnlyList<AppEntry> All => _apps;
    public int Count => _apps.Length;
    public void Replace(IEnumerable<AppEntry> apps)
    {
        // De-duplicate by display name + target (Start Menu user/common and Desktop often duplicate).
        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps.OrderBy(a => a.Source))
        {
            string key = a.Name + "|" + (a.AppUserModelId ?? a.Target ?? a.LaunchPath);
            if (seen.Add(key)) list.Add(a);
        }
        _apps = [.. list];
    }

    public List<(AppEntry App, int Score, int Errors, uint Mask)> Search(ParsedQuery q, NameSearchOptions opt, Func<string, int> usageBoost)
    {
        var res = new List<(AppEntry, int, int, uint)>();
        if (q.Tokens.Count == 0 || q.Kind is KindFilter.File or KindFilter.Folder || q.Extensions.Count > 0) return res;
        BitapPattern?[] fuzzy = new BitapPattern?[q.Tokens.Count];
        if (opt.AllowFuzzy)
            for (int t = 0; t < q.Tokens.Count; t++)
            {
                int e = BitapPattern.ErrorsFor(q.Tokens[t], opt.FuzzyMinTokenLength);
                if (e > 0 && q.Tokens[t].Length <= 63) fuzzy[t] = new BitapPattern(q.Tokens[t], e);
            }
        foreach (var app in _apps)
        {
            uint mask = 0; int errors = 0; bool ok = true;
            for (int t = 0; t < q.Tokens.Count && ok; t++)
            {
                var tok = q.Tokens[t];
                if (app.NormalizedName.AsSpan().IndexOf(tok) >= 0 || app.NormalizedTarget.AsSpan().IndexOf(tok) >= 0) { mask |= 1u << t; continue; }
                int e = fuzzy[t]?.Match(app.NormalizedName) ?? -1;
                if (e < 0) ok = false; else { errors += e; mask |= 1u << t; }
            }
            if (!ok) continue;
            bool excluded = q.Excludes.Any(x => app.NormalizedName.AsSpan().IndexOf(x) >= 0);
            if (excluded) continue;
            int score = Ranking.TextScore(app.NormalizedName, true, q, mask, opt.Weights, errors) - opt.Weights.Folder + opt.Weights.App;
            score += usageBoost(app.Key);
            res.Add((app, score, errors, mask));
        }
        return res;
    }
}
