namespace Seekbeam.Core.Search;

/// <summary>
/// Places the content results (documents whose text contains the words) in the launcher list, next to the name
/// results. The launcher shows <c>max</c> rows: a content result must be visible there, not appended below the fold.
/// <list type="bullet">
/// <item>content results fill the free rows of a partially filled list;</item>
/// <item>a full list still keeps a few rows for them (<see cref="ReservedRows"/>), taken from the weakest name results;</item>
/// <item>they rank above approximate (fuzzy) name matches: every word found in the text beats a misspelled name.</item>
/// </list>
/// </summary>
public static class LauncherMerge
{
    /// <summary>Rows kept for content results when the name results already fill the list (0 for very short lists).</summary>
    public static int ReservedRows(int max) => Math.Min(2, max / 3);

    /// <summary>How many content results to request, beyond the ones already shown by name.</summary>
    public static int ContentRows(int nameCount, int max) => Math.Max(max - nameCount, ReservedRows(max));

    /// <summary>
    /// Merged list. <paramref name="content"/> is in rank order; entries already among <paramref name="names"/> are
    /// skipped. <paramref name="added"/> is the number of content results placed in the list.
    /// </summary>
    public static List<SearchResult> Merge(IReadOnlyList<SearchResult> names, IReadOnlyList<SearchResult> content, int max, out int added)
    {
        max = Math.Max(1, max);
        var shown = names.Where(n => n.EntryId > 0).Select(n => n.EntryId).ToHashSet();
        var extra = new List<SearchResult>();
        int rows = ContentRows(names.Count, max);
        foreach (var c in content)
        {
            if (extra.Count >= rows) break;
            if (c.EntryId > 0 && shown.Add(c.EntryId)) extra.Add(c);
        }
        added = extra.Count;
        var kept = names.Take(Math.Max(0, Math.Min(names.Count, max - extra.Count))).ToList();
        int at = kept.FindIndex(n => n.Fuzzy);
        if (at < 0) at = kept.Count;
        kept.InsertRange(at, extra);
        return kept;
    }
}
