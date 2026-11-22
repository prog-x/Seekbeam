using System.Buffers;
using System.Text;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Config;
using Seekbeam.Core.NameIndex;
using Seekbeam.Core.Search;
using Seekbeam.Core.Text;

namespace Seekbeam.Core.Content;

public enum DeepScope { NamesAndContent, ContentOnly, NamesOnly }

/// <summary>How the words of a content query must match (see <see cref="DeepSearch.Parse(string, TermMatch)"/>).</summary>
public enum TermMatch { Exact, Prefix, Any }

/// <summary>How far the content query may be relaxed when the strict one finds nothing.</summary>
public enum ContentRelaxation
{
    /// <summary>All words, exactly.</summary>
    None,
    /// <summary>Then all words as prefixes (launcher).</summary>
    Prefix,
    /// <summary>Then any of the words (advanced search; results flagged as partial).</summary>
    PrefixThenAny,
}

public sealed class DeepSearchRequest
{
    public string Query { get; init; } = "";
    public DeepScope Scope { get; init; } = DeepScope.NamesAndContent;
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public string? Location { get; init; }
    public DateTime? ModifiedAfterUtc { get; init; }
    public DateTime? ModifiedBeforeUtc { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public bool FoldersOnly { get; init; }
    public int MaxResults { get; init; } = 100;
    /// <summary>Relaxation ladder used when the strict content query finds nothing.</summary>
    public ContentRelaxation Relaxation { get; init; } = ContentRelaxation.PrefixThenAny;
}

/// <summary>Parsed deep-search query: FTS5 expression for content + plain tokens for names/snippets.</summary>
public sealed class DeepQuery
{
    public string FtsExpression { get; init; } = "";
    public List<string> PositiveTerms { get; } = [];
    public string NameQuery { get; init; } = "";
}

/// <summary>
/// Full-text + name search with filters (ARCHITECTURE.md §5.4). Syntax: words (AND), "exact phrase", OR, -exclude,
/// prefix*, plus launcher filters (ext:, kind:, in:).
/// </summary>
public sealed class DeepSearch
{
    private readonly ContentDb _content;
    private readonly NameIndexManager _index;
    private readonly EntryStore _entries;
    private readonly Func<SearchConfig> _config;

    public DeepSearch(ContentDb content, NameIndexManager index, EntryStore entries, Func<SearchConfig> config)
    {
        _content = content; _index = index; _entries = entries; _config = config;
    }

    public static DeepQuery Parse(string query) => Parse(query, TermMatch.Exact);

    /// <summary>
    /// Parses a query. <paramref name="mode"/> relaxes the content expression: <see cref="TermMatch.Prefix"/> turns
    /// every plain word of 3+ characters into a prefix (<c>code</c> also finds <c>codes</c>); <see cref="TermMatch.Any"/>
    /// additionally requires only one of the words instead of all of them (ranked by bm25, which favours documents
    /// containing more of them). Quoted phrases, explicit prefixes, OR groups and exclusions keep their meaning.
    /// </summary>
    public static DeepQuery Parse(string query, TermMatch mode)
    {
        var fts = new StringBuilder();
        var nameParts = new List<string>();
        var dq = new DeepQuery();
        var pending = new List<string>();
        var negatives = new List<string>();
        bool or = false;
        foreach (var (word, quoted) in Tokenize(query))
        {
            if (!quoted && word == "OR") { or = true; continue; }
            if (!quoted && (word.StartsWith("ext:") || word.StartsWith("kind:") || word.StartsWith("type:") || word.StartsWith("in:")))
            { nameParts.Add(word); continue; }
            bool neg = !quoted && word.Length > 1 && word[0] == '-';
            string w = neg ? word[1..] : word;
            bool prefix = !quoted && w.EndsWith('*') && w.Length > 1;
            if (prefix) w = w.TrimEnd('*');
            string clean = w.Replace("\"", "");
            if (clean.Length == 0) continue;
            bool autoPrefix = mode != TermMatch.Exact && !quoted && !neg && clean.Length >= 3;
            string term = "\"" + clean + "\"" + (prefix || autoPrefix ? "*" : "");
            if (neg) { negatives.Add(term); nameParts.Add("-" + clean); continue; }
            if (or && pending.Count > 0) pending[^1] = "(" + pending[^1] + " OR " + term + ")";
            else pending.Add(term);
            or = false;
            dq.PositiveTerms.Add(clean);
            nameParts.Add(quoted ? "\"" + clean + "\"" : clean);
        }
        if (pending.Count > 0)
        {
            // FTS5 precedence is NOT > AND > OR: the OR list is parenthesised so that exclusions apply to all of it.
            if (mode == TermMatch.Any && pending.Count > 1) fts.Append('(').Append(string.Join(" OR ", pending)).Append(')');
            else fts.Append(string.Join(" AND ", pending));
            foreach (var n in negatives) fts.Append(" NOT ").Append(n);
        }
        return new DeepQuery { FtsExpression = fts.ToString(), NameQuery = string.Join(" ", nameParts) }.With(dq.PositiveTerms);
    }

    private static IEnumerable<(string, bool)> Tokenize(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == '"')
            {
                int e = s.IndexOf('"', i + 1); if (e < 0) e = s.Length;
                yield return (s[(i + 1)..e], true);
                i = e + 1; continue;
            }
            int st = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            yield return (s[st..i], false);
        }
    }

    public SearchResultSet Search(DeepSearchRequest req, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dq = Parse(req.Query);
        var scores = new Dictionary<long, (double Score, bool Content)>();
        int k = Math.Clamp(req.MaxResults, 1, 1000);

        var contentMatch = ContentMatchMode.None;
        if (req.Scope != DeepScope.NamesOnly && dq.FtsExpression.Length > 0)
        {
            // Strict first; relax only when it finds nothing, so precise queries keep precise results.
            var ladder = req.Relaxation switch
            {
                ContentRelaxation.Prefix => new[] { TermMatch.Exact, TermMatch.Prefix },
                ContentRelaxation.PrefixThenAny => [TermMatch.Exact, TermMatch.Prefix, TermMatch.Any],
                _ => [TermMatch.Exact],
            };
            List<ContentHit> hits = [];
            string tried = "";
            foreach (var mode in ladder)
            {
                var attempt = mode == TermMatch.Exact ? dq : Parse(req.Query, mode);
                if (attempt.FtsExpression == tried || attempt.FtsExpression.Length == 0) continue; // nothing to relax
                tried = attempt.FtsExpression;
                hits = _content.Search(attempt.FtsExpression, Math.Max(k * 5, 500));
                if (hits.Count == 0) { ct.ThrowIfCancellationRequested(); continue; }
                contentMatch = mode switch { TermMatch.Exact => ContentMatchMode.AllWords, TermMatch.Prefix => ContentMatchMode.AllWordsAsPrefixes, _ => ContentMatchMode.SomeWords };
                break;
            }
            for (int i = 0; i < hits.Count; i++)
            {
                // bm25 is negative (lower = better): rank-based score keeps it comparable with name scores.
                double s = 1200 - i * (600.0 / Math.Max(1, hits.Count));
                scores[hits[i].EntryId] = (s, true);
            }
        }
        ct.ThrowIfCancellationRequested();
        if (req.Scope != DeepScope.ContentOnly && dq.NameQuery.Trim().Length > 0)
        {
            var cfg = _config();
            var q = QueryParser.Parse(dq.NameQuery);
            using var snap = _index.Acquire();
            var outcome = NameSearcher.Search(snap, q, Math.Max(k * 3, 200), new NameSearchOptions
            {
                MatchInPath = true, IncludeHidden = cfg.IncludeHidden, IncludeSystem = cfg.IncludeSystem, AllowFuzzy = cfg.FuzzyEnabled,
                FuzzyMinTokenLength = cfg.FuzzyMinTokenLength, Weights = cfg.Weights,
            }, ct);
            foreach (var c in outcome.Top)
                scores[c.Id] = scores.TryGetValue(c.Id, out var e) ? (e.Score + c.Score + 300, true) : (c.Score, false);
        }
        ct.ThrowIfCancellationRequested();

        var ordered = scores.OrderByDescending(kv => kv.Value.Score).Select(kv => kv.Key).ToList();
        var results = new List<SearchResult>();
        var exts = new HashSet<string>(req.Extensions.Select(e => e.TrimStart('.')), StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in ordered.Chunk(200))
        {
            foreach (var (info, path) in _entries.GetWithPaths(chunk.Select(x => x)))
            {
                if (req.FoldersOnly && !info.IsDirectory) continue;
                if (exts.Count > 0 && (info.IsDirectory || !exts.Contains(Path.GetExtension(info.Name).TrimStart('.')))) continue;
                if (req.Location != null && !path.StartsWith(req.Location, StringComparison.OrdinalIgnoreCase)) continue;
                if (req.ModifiedAfterUtc is { } a && info.ModifiedUtc < a) continue;
                if (req.ModifiedBeforeUtc is { } bdt && info.ModifiedUtc > bdt) continue;
                if (req.MinSize is { } mn && info.Size < mn) continue;
                if (req.MaxSize is { } mx && info.Size > mx) continue;
                var (score, content) = scores[info.Id];
                string? snippet = null; IReadOnlyList<Highlight> sh = [];
                if (content && results.Count < 60 && dq.PositiveTerms.Count > 0)
                {
                    string? text = _content.GetText(info.Id);
                    if (text != null) (snippet, sh) = Snippets.Build(text, dq.PositiveTerms, 220);
                }
                var q = QueryParser.Parse(dq.NameQuery);
                results.Add(new SearchResult
                {
                    Kind = info.IsDirectory ? ResultKind.Folder : ResultKind.File, EntryId = info.Id, Name = info.ParentId == 0 ? path : info.Name, FullPath = path,
                    Directory = Path.GetDirectoryName(path), Size = info.Size, ModifiedUtc = info.ModifiedUtc, Score = (int)score,
                    Highlights = SearchEngine.ComputeHighlights(info.Name, q.Tokens), Snippet = snippet, SnippetHighlights = sh,
                });
                if (results.Count >= k) break;
            }
            if (results.Count >= k) break;
            ct.ThrowIfCancellationRequested();
        }
        return new SearchResultSet { Results = results, TotalMatches = scores.Count, Elapsed = sw.Elapsed, ContentMatch = contentMatch };
    }
}

internal static class DeepQueryExt
{
    public static DeepQuery With(this DeepQuery q, List<string> terms) { q.PositiveTerms.AddRange(terms); return q; }
}

/// <summary>Accent/case-insensitive snippet extraction around the first matching terms.</summary>
public static class Snippets
{
    public static (string Snippet, IReadOnlyList<Highlight> Highlights) Build(string text, IReadOnlyList<string> terms, int width)
    {
        var normTerms = terms.Select(t => Normalizer.NormalizeToBytes(t)).Where(t => t.Length > 0).ToList();
        if (normTerms.Count == 0 || text.Length == 0) return (Trim(text, 0, width), []);
        // Scan the text window by window (bounded memory) for the first window containing a term.
        const int Window = 32 * 1024;
        byte[] buf = ArrayPool<byte>.Shared.Rent(Window * Normalizer.MaxBytesPerChar);
        int[] map = ArrayPool<int>.Shared.Rent(Window * Normalizer.MaxBytesPerChar);
        try
        {
            for (int start = 0; start < text.Length; start += Window - 256)
            {
                int len = Math.Min(Window, text.Length - start);
                int n = Normalizer.Normalize(text.AsSpan(start, len), buf, map);
                var span = buf.AsSpan(0, n);
                int best = -1, bestLen = 0;
                foreach (var t in normTerms)
                {
                    int p = span.IndexOf(t);
                    if (p >= 0 && (best < 0 || p < best)) { best = p; bestLen = t.Length; }
                }
                if (best < 0) { if (start + len >= text.Length) break; continue; }
                int center = start + map[best];
                int from = Math.Max(0, center - width / 3);
                int to = Math.Min(text.Length, from + width);
                // Align to word boundaries.
                while (from > 0 && from > center - width / 2 && !char.IsWhiteSpace(text[from - 1])) from--;
                while (to < text.Length && to < from + width + 30 && !char.IsWhiteSpace(text[to])) to++;
                string snippet = text[from..to].Replace('\n', ' ').Replace('\r', ' ');
                var hl = SearchEngine.ComputeHighlights(snippet, normTerms);
                // Highlight all occurrences, not only the first.
                var all = new List<Highlight>();
                foreach (var t in normTerms) all.AddRange(AllOccurrences(snippet, t));
                all.Sort((a, b) => a.Start.CompareTo(b.Start));
                string prefix = from > 0 ? "…" : "";
                var shifted = all.Select(h => new Highlight(h.Start + prefix.Length, h.Length)).ToList();
                return (prefix + snippet + (to < text.Length ? "…" : ""), shifted.Count > 0 ? shifted : hl);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); ArrayPool<int>.Shared.Return(map); }
        return (Trim(text, 0, width), []);
    }

    private static IEnumerable<Highlight> AllOccurrences(string s, byte[] term)
    {
        var buf = new byte[s.Length * Normalizer.MaxBytesPerChar];
        var map = new int[buf.Length];
        int n = Normalizer.Normalize(s, buf, map);
        var span = buf.AsSpan(0, n);
        int pos = 0;
        var res = new List<Highlight>();
        while (pos < n)
        {
            int p = span[pos..].IndexOf(term);
            if (p < 0) break;
            int a = pos + p;
            int startChar = map[a], endChar = map[a + term.Length - 1] + 1;
            res.Add(new Highlight(startChar, endChar - startChar));
            pos = a + term.Length;
        }
        return res;
    }

    private static string Trim(string text, int from, int width) =>
        text.Length - from <= width ? text[from..].Replace('\n', ' ') : text.Substring(from, width).Replace('\n', ' ') + "…";
}
