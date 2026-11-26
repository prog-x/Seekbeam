using System.Text;
using Seekbeam.Core.Text;

namespace Seekbeam.Core.Search;

public enum KindFilter { Any, File, Folder, App }

/// <summary>Parsed launcher query. Tokens are normalized UTF-8 (see <see cref="Normalizer"/>).</summary>
public sealed class ParsedQuery
{
    public const int MaxTokens = 12;
    public List<byte[]> Tokens { get; } = [];
    public List<byte[]> Excludes { get; } = [];
    public List<byte[]> Extensions { get; } = [];  // normalized, without dot
    public List<byte[]> PathFilters { get; } = []; // in:xxx — must match an ancestor directory name
    /// <summary>
    /// Path chains typed with a separator ("src\seek", "proj/seek/doc"): indexes into <see cref="Tokens"/>, each token having
    /// to occur in a path segment strictly below the segment of the previous one. At most <see cref="MaxChains"/>.
    /// </summary>
    public List<int[]> Chains { get; } = [];
    public const int MaxChains = 4;
    public KindFilter Kind { get; set; } = KindFilter.Any;
    /// <summary>Full query normalized (tokens joined by a space) for exact-name detection.</summary>
    public byte[] Joined { get; set; } = [];
    public string Raw { get; set; } = "";
    public bool IsEmpty => Tokens.Count == 0 && Extensions.Count == 0 && PathFilters.Count == 0;
}

public static class QueryParser
{
    /// <summary>
    /// Launcher syntax: words (any order, substring), "exact phrase", -exclude, ext:pdf,docx, kind:file|folder|app,
    /// in:dirname. Everything is case- and accent-insensitive.
    /// </summary>
    public static ParsedQuery Parse(string? query)
    {
        var q = new ParsedQuery { Raw = query ?? "" };
        if (string.IsNullOrWhiteSpace(query)) return q;
        foreach (var (word, quoted) in Split(query))
        {
            if (!quoted)
            {
                int colon = word.IndexOf(':');
                if (colon > 1 && colon < word.Length - 1)
                {
                    string key = word[..colon].ToLowerInvariant(), val = word[(colon + 1)..];
                    switch (key)
                    {
                        case "ext":
                            foreach (var e in val.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries))
                                q.Extensions.Add(Normalizer.NormalizeToBytes(e.TrimStart('.', '*')));
                            continue;
                        case "kind" or "type":
                            q.Kind = val.ToLowerInvariant() switch
                            {
                                "file" or "f" or "fichier" => KindFilter.File,
                                "folder" or "dir" or "d" or "dossier" or "rep" => KindFilter.Folder,
                                "app" or "a" or "application" => KindFilter.App,
                                _ => KindFilter.Any,
                            };
                            continue;
                        case "in" or "dans":
                            q.PathFilters.Add(Normalizer.NormalizeToBytes(val));
                            continue;
                    }
                }
                if (word.Length > 1 && word[0] == '-') { q.Excludes.Add(Normalizer.NormalizeToBytes(word.AsSpan(1))); continue; }
                if (word.IndexOfAny(['\\', '/']) >= 0)
                {
                    // Path fragments: "src\seek" = "seek" in a folder (or name) below a folder matching "src".
                    var chain = new List<int>();
                    foreach (var part in word.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        int idx = AddToken(q, Normalizer.NormalizeToBytes(part));
                        if (idx >= 0 && (chain.Count == 0 || chain[^1] != idx)) chain.Add(idx);
                    }
                    if (chain.Count > 1 && q.Chains.Count < ParsedQuery.MaxChains) q.Chains.Add([.. chain]);
                    continue;
                }
            }
            AddToken(q, Normalizer.NormalizeToBytes(word));
        }
        // Longest tokens first: the most selective scan runs first and dominates scoring stability.
        var joined = new List<byte>();
        foreach (var t in q.Tokens) { if (joined.Count > 0) joined.Add((byte)' '); joined.AddRange(t); }
        q.Joined = [.. joined];
        return q;
    }

    /// <summary>Adds a token (deduplicated) and returns its index, or -1 if empty or over the limit.</summary>
    private static int AddToken(ParsedQuery q, byte[] t)
    {
        if (t.Length == 0) return -1;
        for (int i = 0; i < q.Tokens.Count; i++) if (q.Tokens[i].AsSpan().SequenceEqual(t)) return i;
        if (q.Tokens.Count >= ParsedQuery.MaxTokens) return -1;
        q.Tokens.Add(t);
        return q.Tokens.Count - 1;
    }

    private static IEnumerable<(string Word, bool Quoted)> Split(string s)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '"')
            {
                int end = s.IndexOf('"', i + 1);
                if (end < 0) end = s.Length;
                string phrase = s[(i + 1)..end];
                if (phrase.Length > 0) yield return (phrase, true);
                i = end + 1;
                continue;
            }
            sb.Clear();
            while (i < s.Length && !char.IsWhiteSpace(s[i])) sb.Append(s[i++]);
            yield return (sb.ToString(), false);
        }
    }
}
