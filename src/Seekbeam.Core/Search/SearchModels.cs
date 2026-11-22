namespace Seekbeam.Core.Search;

public enum ResultKind { File, Folder, App }

public sealed class SearchRequest
{
    public string Query { get; init; } = "";
    public int MaxResults { get; init; } = 8;
    public bool AllowFuzzy { get; init; } = true;
    public bool IncludeApps { get; init; } = true;
    public bool IncludeFiles { get; init; } = true;
}

public readonly record struct Highlight(int Start, int Length);

public sealed class SearchResult
{
    public ResultKind Kind { get; init; }
    public long EntryId { get; init; }
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string? Directory { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public int Score { get; init; }
    public bool Fuzzy { get; init; }
    public IReadOnlyList<Highlight> Highlights { get; init; } = [];
    /// <summary>For applications: launch information.</summary>
    public Apps.AppEntry? App { get; init; }
    public string? Snippet { get; init; }
    public IReadOnlyList<Highlight> SnippetHighlights { get; init; } = [];
    public override string ToString() => $"{Score,6} {Kind,-6} {FullPath}";
}

public sealed class SearchResultSet
{
    public static readonly SearchResultSet Empty = new() { Results = [] };
    public IReadOnlyList<SearchResult> Results { get; init; } = [];
    public int TotalMatches { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool UsedFuzzy { get; init; }
    public long IndexedEntries { get; init; }
    public string? Suggestion { get; init; }
    /// <summary>How the content results matched (advanced search and launcher content results).</summary>
    public ContentMatchMode ContentMatch { get; init; }
}

/// <summary>Which step of the relaxation ladder produced the content results.</summary>
public enum ContentMatchMode { None, AllWords, AllWordsAsPrefixes, SomeWords }

/// <summary>Usage/learning boosts keyed by entry id (small, bounded by the history size).</summary>
public sealed class UsageBoosts
{
    public static readonly UsageBoosts None = new([], new Dictionary<int, int>());
    private readonly long[] _bits;
    private readonly Dictionary<int, int> _map;
    public UsageBoosts(long[] bits, Dictionary<int, int> map) { _bits = bits; _map = map; }
    public bool IsEmpty => _map.Count == 0;
    public int Get(int id)
    {
        if (_map.Count == 0) return 0;
        int w = id >> 6;
        if ((uint)w >= (uint)_bits.Length || (_bits[w] & (1L << (id & 63))) == 0) return 0;
        return _map.TryGetValue(id, out int b) ? b : 0;
    }
    public static UsageBoosts Create(Dictionary<int, int> map)
    {
        if (map.Count == 0) return None;
        int max = map.Keys.Max();
        var bits = new long[(max >> 6) + 1];
        foreach (int id in map.Keys) bits[id >> 6] |= 1L << (id & 63);
        return new UsageBoosts(bits, map);
    }
}
