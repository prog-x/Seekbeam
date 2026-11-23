using System.Diagnostics.CodeAnalysis;
using Seekbeam.Core.Search;
using Seekbeam.Core.Text;
using Seekbeam.Core.Config;

namespace Seekbeam.Core.Apps;

/// <summary>
/// Where an entry comes from. <see cref="RunCommand"/>: a command the Windows Run dialog (Win+R) resolves (executables
/// of the PATH folders, App Paths registrations, .msc/.cpl). <see cref="Alias"/>: a keyword defined by the user.
/// </summary>
public enum AppSource { StartMenu = 0, Desktop = 1, Packaged = 2, ExecutableFolder = 3, AppsFolder = 4, RunCommand = 5, Alias = 6 }

/// <summary>A launchable application. <see cref="AppUserModelId"/> is set for packaged (UWP/MSIX) apps.</summary>
public sealed class AppEntry
{
    public required string Name { get; init; }
    /// <summary>Path of the shortcut (.lnk) or executable, or "shell:AppsFolder\{AUMID}" for packaged apps.</summary>
    public required string LaunchPath { get; init; }
    public string? Target { get; init; }
    /// <summary>Arguments stored in a shortcut (informational: launching the .lnk applies them).</summary>
    public string? Arguments { get; init; }
    /// <summary>Arguments passed when launching <see cref="LaunchPath"/> (aliases).</summary>
    public string? LaunchArguments { get; init; }
    /// <summary>Launch elevated by default (aliases).</summary>
    public bool RunAsAdmin { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? IconLocation { get; init; }
    public string? AppUserModelId { get; init; }
    public AppSource Source { get; init; }
    public string Key => Source == AppSource.Alias ? "alias:" + Name : AppUserModelId != null ? "app:" + AppUserModelId : LaunchPath;
    /// <summary>Run commands and aliases can be typed followed by arguments ("mstsc /v:server").</summary>
    public bool IsCommand => Source is AppSource.RunCommand or AppSource.Alias;
    internal byte[] NormalizedName { get; init; } = [];
    internal byte[] NormalizedTarget { get; init; } = [];

    public static AppEntry Create(string name, string launchPath, AppSource source, string? target = null, string? args = null,
        string? workdir = null, string? icon = null, string? aumid = null, string? launchArgs = null, bool runAsAdmin = false) => new()
    {
        Name = name, LaunchPath = launchPath, Source = source, Target = target, Arguments = args, WorkingDirectory = workdir,
        IconLocation = icon, AppUserModelId = aumid, LaunchArguments = launchArgs, RunAsAdmin = runAsAdmin,
        NormalizedName = Normalizer.NormalizeToBytes(name),
        // Commands and aliases are found by their name only: "notepad" must not list every alias that uses Notepad.
        NormalizedTarget = source is AppSource.Alias or AppSource.RunCommand ? []
            : Normalizer.NormalizeToBytes(Path.GetFileNameWithoutExtension(target ?? "") ?? ""),
    };

    /// <summary>Builds the entry of a user alias; environment variables (%SystemRoot%…) are expanded. Null if unusable.</summary>
    public static AppEntry? FromAlias(AliasConfig a)
    {
        string keyword = (a.Keyword ?? "").Trim();
        string target = Environment.ExpandEnvironmentVariables((a.Target ?? "").Trim()).Trim('"');
        if (keyword.Length == 0 || target.Length == 0) return null;
        string? args = string.IsNullOrWhiteSpace(a.Arguments) ? null : Environment.ExpandEnvironmentVariables(a.Arguments.Trim());
        string? dir = string.IsNullOrWhiteSpace(a.WorkingDirectory) ? null : Environment.ExpandEnvironmentVariables(a.WorkingDirectory.Trim()).Trim('"');
        return Create(keyword, target, AppSource.Alias, target, workdir: dir, launchArgs: args, runAsAdmin: a.RunAsAdmin);
    }

    /// <summary>Joins stored and typed arguments.</summary>
    public static string? JoinArguments(string? stored, string? typed) =>
        string.IsNullOrWhiteSpace(typed) ? stored : string.IsNullOrWhiteSpace(stored) ? typed.Trim() : stored + " " + typed.Trim();
}

/// <summary>In-memory application index (hundreds to a few thousand entries). Thread-safe snapshot swap.</summary>
public sealed class AppCatalog
{
    /// <summary>Bonus of an alias whose keyword is typed exactly, so that it beats a run command of the same name.</summary>
    public const int AliasExactBonus = 100;
    /// <summary>Puts "name.msc"/"name.cpl" before a same-named "name.exe" when "name" is typed.</summary>
    private const int ConsoleTieBreak = 10;

    private sealed record Snapshot(AppEntry[] Apps, Dictionary<string, AppEntry> Commands);
    private volatile Snapshot _snap = new([], new(StringComparer.OrdinalIgnoreCase));
    public IReadOnlyList<AppEntry> All => _snap.Apps;
    public int Count => _snap.Apps.Length;

    public void Replace(IEnumerable<AppEntry> apps)
    {
        // De-duplicate by display name + target (Start Menu user/common and Desktop often duplicate).
        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commands = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps.OrderBy(a => a.Source == AppSource.Alias ? -1 : (int)a.Source)) // aliases win over run commands
        {
            string key = a.Source == AppSource.Alias ? a.Key : a.Name + "|" + (a.AppUserModelId ?? a.Target ?? a.LaunchPath);
            if (!seen.Add(key)) continue;
            list.Add(a);
            if (a.IsCommand) commands.TryAdd(a.Name, a);
        }
        _snap = new Snapshot([.. list], commands);
    }

    /// <summary>Exact lookup of a run command or alias by the word typed ("mstsc", "mstsc.exe", "services.msc", "hosts").</summary>
    public bool TryGetCommand(string word, [NotNullWhen(true)] out AppEntry? entry)
    {
        var commands = _snap.Commands;
        if (commands.TryGetValue(word, out entry)) return true;
        foreach (var ext in RunCommands.ImplicitExtensions)
            if (word.Length > ext.Length && word.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                && commands.TryGetValue(word[..^ext.Length], out entry)
                && entry.Source == AppSource.RunCommand && entry.LaunchPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        entry = null;
        return false;
    }

    /// <summary>
    /// Run commands are hundreds of terse System32 names (msdt, mshta, msra…): they match only when the query is the
    /// command name or its prefix (≥ 3 characters), never by substring or typo, so that they never flood the results.
    /// "services" is an exact match of "services.msc".
    /// </summary>
    internal static bool MatchRunCommand(AppEntry app, ParsedQuery q, out bool exact)
    {
        exact = false;
        if (q.Tokens.Count != 1) return false;
        ReadOnlySpan<byte> name = app.NormalizedName, tok = q.Tokens[0];
        int dot = name.LastIndexOf((byte)'.');
        exact = name.SequenceEqual(tok) || (dot > 0 && name[..dot].SequenceEqual(tok));
        return exact || (tok.Length >= RunCommands.MinPrefixLength && name.StartsWith(tok));
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
        foreach (var app in _snap.Apps)
        {
            if (app.Source == AppSource.RunCommand)
            {
                if (!MatchRunCommand(app, q, out bool exact)) continue;
                // Scored like a file name (extension ignored); the application bonus only for the exact command.
                int s = Ranking.TextScore(app.NormalizedName, false, q, 1u, opt.Weights, 0) + (exact ? opt.Weights.App : 0);
                // "services": the console (services.msc) is what people want, not the same-named process (services.exe).
                if (exact && !app.NormalizedName.AsSpan().SequenceEqual(q.Tokens[0])) s += ConsoleTieBreak;
                res.Add((app, s + usageBoost(app.Key), 0, 1u));
                continue;
            }
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
            if (app.Source == AppSource.Alias && app.NormalizedName.AsSpan().SequenceEqual(q.Joined)) score += AliasExactBonus;
            score += usageBoost(app.Key);
            res.Add((app, score, errors, mask));
        }
        return res;
    }
}
