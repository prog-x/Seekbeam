using System.Buffers;
using System.Diagnostics;
using Seekbeam.Core.Apps;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Config;
using Seekbeam.Core.NameIndex;
using Seekbeam.Core.Text;

namespace Seekbeam.Core.Search;

/// <summary>Launcher search: name index + applications, ranked, hydrated, highlighted. Thread-safe.</summary>
public sealed class SearchEngine
{
    private readonly NameIndexManager _index;
    private readonly EntryStore _entries;
    private readonly AppCatalog _apps;
    private readonly UsageStore _usage;
    private readonly Func<SearchConfig> _config;
    private int _active;

    /// <summary>True while at least one search runs (indexing workers yield).</summary>
    public bool IsSearching => Volatile.Read(ref _active) > 0;
    public event Action<bool>? SearchingChanged;

    public SearchEngine(NameIndexManager index, EntryStore entries, AppCatalog apps, UsageStore usage, Func<SearchConfig> config)
    {
        _index = index; _entries = entries; _apps = apps; _usage = usage; _config = config;
    }

    public SearchResultSet Search(SearchRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var q = QueryParser.Parse(req.Query);
        if (q.IsEmpty) return SearchResultSet.Empty;
        if (Interlocked.Increment(ref _active) == 1) SearchingChanged?.Invoke(true);
        try
        {
            var cfg = _config();
            var opt = new NameSearchOptions
            {
                MatchInPath = cfg.MatchInPath,
                IncludeHidden = cfg.IncludeHidden,
                IncludeSystem = cfg.IncludeSystem,
                AllowFuzzy = req.AllowFuzzy && cfg.FuzzyEnabled,
                FuzzyMinTokenLength = cfg.FuzzyMinTokenLength,
                FuzzyThreshold = Math.Min(3, req.MaxResults),
                MaxThreads = cfg.MaxSearchThreads > 0 ? cfg.MaxSearchThreads : Math.Clamp(Environment.ProcessorCount / 2, 1, 8),
                Weights = cfg.Weights,
                Usage = _usage.Boosts,
            };
            int k = Math.Clamp(req.MaxResults, 1, 500);

            var apps = req.IncludeApps ? _apps.Search(q, opt, _usage.BoostForKey) : [];
            NameSearcher.Outcome outcome = new();
            long indexed = 0;
            if (req.IncludeFiles)
            {
                using var snap = _index.Acquire();
                indexed = snap.LiveCount;
                outcome = NameSearcher.Search(snap, q, k, opt, ct);
            }
            ct.ThrowIfCancellationRequested();

            // Merge apps and files by score, then hydrate only the winners.
            var merged = new List<(int Score, AppEntry? App, NameCandidate? File, int Errors)>();
            foreach (var a in apps) merged.Add((a.Score, a.App, null, a.Errors));
            foreach (var c in outcome.Top) merged.Add((c.Score, null, c, c.Errors));
            merged.Sort((x, y) => y.Score.CompareTo(x.Score));
            if (merged.Count > k) merged.RemoveRange(k, merged.Count - k);

            var fileIds = merged.Where(m => m.File != null).Select(m => (long)m.File!.Value.Id).ToList();
            var hydrated = _entries.GetWithPaths(fileIds).ToDictionary(h => h.Info.Id);
            var results = new List<SearchResult>(merged.Count);
            foreach (var m in merged)
            {
                if (m.App != null)
                {
                    results.Add(new SearchResult
                    {
                        Kind = ResultKind.App, Name = m.App.Name, FullPath = m.App.LaunchPath, Directory = m.App.Target,
                        Score = m.Score, Fuzzy = m.Errors > 0, App = m.App, Highlights = ComputeHighlights(m.App.Name, q.Tokens),
                    });
                }
                else if (hydrated.TryGetValue(m.File!.Value.Id, out var h))
                {
                    results.Add(new SearchResult
                    {
                        Kind = h.Info.IsDirectory ? ResultKind.Folder : ResultKind.File,
                        EntryId = h.Info.Id, Name = h.Info.ParentId == 0 ? h.FullPath : h.Info.Name, FullPath = h.FullPath,
                        Directory = Path.GetDirectoryName(h.FullPath), Size = h.Info.Size, ModifiedUtc = h.Info.ModifiedUtc,
                        Score = m.Score, Fuzzy = m.Errors > 0, Highlights = ComputeHighlights(h.Info.Name, q.Tokens),
                    });
                }
            }
            return new SearchResultSet
            {
                Results = results,
                TotalMatches = outcome.TotalMatches + apps.Count,
                Elapsed = sw.Elapsed,
                UsedFuzzy = outcome.UsedFuzzy,
                IndexedEntries = indexed,
            };
        }
        finally
        {
            if (Interlocked.Decrement(ref _active) == 0) SearchingChanged?.Invoke(false);
        }
    }

    /// <summary>Character ranges of <paramref name="name"/> matching the normalized tokens (accent/case-insensitive).</summary>
    public static List<Highlight> ComputeHighlights(string name, IReadOnlyList<byte[]> tokens)
    {
        var res = new List<Highlight>();
        if (tokens.Count == 0 || name.Length == 0) return res;
        int cap = name.Length * Normalizer.MaxBytesPerChar;
        byte[] buf = ArrayPool<byte>.Shared.Rent(cap);
        int[] map = ArrayPool<int>.Shared.Rent(cap);
        try
        {
            int n = Normalizer.Normalize(name, buf, map);
            var norm = buf.AsSpan(0, n);
            foreach (var tok in tokens)
            {
                int pos = Ranking.FindBest(norm, tok, out _);
                if (pos < 0) continue;
                int startChar = map[pos], endChar = map[pos + tok.Length - 1] + 1;
                if (endChar < name.Length && char.IsLowSurrogate(name[endChar])) endChar++;
                res.Add(new Highlight(startChar, endChar - startChar));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); ArrayPool<int>.Shared.Return(map); }
        // Merge overlaps.
        res.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<Highlight>();
        foreach (var h in res)
        {
            if (merged.Count > 0 && h.Start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                int end = Math.Max(last.Start + last.Length, h.Start + h.Length);
                merged[^1] = new Highlight(last.Start, end - last.Start);
            }
            else merged.Add(h);
        }
        return merged;
    }
}
