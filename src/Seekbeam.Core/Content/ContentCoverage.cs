using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.Resources;

namespace Seekbeam.Core.Content;

/// <summary>Where every file of the name index stands with respect to the content index (dashboard breakdown).</summary>
public sealed class ContentCoverageReport
{
    /// <summary>Files of the name index; equals the sum of the five state counters.</summary>
    public long Files { get; init; }
    public long Indexed { get; init; }
    public long NoText { get; init; }
    public long Pending { get; init; }
    /// <summary>Pending files found outside the content queue and handed back to it (see <see cref="ContentCoverage"/>).</summary>
    public long Requeued { get; init; }
    public long Failed { get; init; }
    public long NotIndexed { get; init; }
    /// <summary>NotIndexed decided by the rules (format, size, settings...), largest first.</summary>
    public IReadOnlyList<(ContentIneligibility Reason, long Count)> ByReason { get; init; } = [];
    /// <summary>NotIndexed decided by the extractor (Encrypted, TooLarge, Skipped), largest first.</summary>
    public IReadOnlyList<(ContentStatus Status, long Count)> ByExtractorStatus { get; init; } = [];
    /// <summary>Most frequent extensions among unsupported files ("" = no extension), largest first.</summary>
    public IReadOnlyList<(string Extension, long Count)> TopUnsupportedExtensions { get; init; } = [];
    public DateTime ComputedUtc { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Read-only census of the content state of every file, classified exactly like <see cref="ContentStateLookup"/>
/// (same <see cref="ContentStateLookup.Classify"/>, same <see cref="ContentEligibility.Check(RootScope, ReadOnlySpan{char}, long, FileAttributes, bool)"/>).
/// <para>
/// Constant memory: a streaming merge join of <c>entries</c> (files, by id), <c>content_queue</c> and content.db's
/// <c>docs</c>, read in key-paginated batches on pooled reader connections returned between batches (no long read
/// transaction, so WAL checkpoints are never held back; no ATTACH). Nothing is read from the source files: name,
/// size, attributes and ancestry come from the catalog. Membership of an excluded folder is decided by walking
/// <c>parent_id</c> with a per-directory cache (directories are far fewer than files), never by rebuilding paths.
/// </para>
/// <para>
/// Self-repair: a file that is pending but no longer in the content queue (dropped while its drive was offline, or
/// changed while an event was missed) would wait forever, since only a change of size or date queues it again. When
/// <c>requeue</c> is given, such files are handed to it, one batch at a time.
/// </para>
/// </summary>
public static class ContentCoverage
{
    public const int DefaultBatchSize = 10_000;
    private const int MaxDistinctExtensions = 4_096; // bounds the extension histogram on pathological name sets
    private const int MaxAncestry = 4_096;           // defensive bound on parent chains

    private readonly record struct FileRow(long Id, long ParentId, long RootId, string Name, long Size, long MTime, long Attrs);

    public static ContentCoverageReport Compute(CatalogDb catalog, ContentDb content, EntryStore entries, NameIndexer indexer,
        bool contentIndexerAvailable, IWorkGate gate, CancellationToken ct, int batchSize = DefaultBatchSize, Action<IReadOnlyList<long>>? requeue = null) =>
        // Snapshot of the rules: the census is consistent even if the settings change meanwhile.
        Compute(catalog, content, entries, indexer.Scope, indexer.Content, contentIndexerAvailable, gate, ct, batchSize, requeue);

    public static ContentCoverageReport Compute(CatalogDb catalog, ContentDb content, EntryStore entries, ScopeRules scope, ContentEligibility rules,
        bool contentIndexerAvailable, IWorkGate gate, CancellationToken ct, int batchSize = DefaultBatchSize, Action<IReadOnlyList<long>>? requeue = null)
    {
        var sw = Stopwatch.StartNew();
        var roots = scope.Roots.ToDictionary(r => r.RootId);
        var excludedDirs = rules.HasExcludedFolders ? ExcludedFolderIds(rules, scope, entries) : null;
        var dirCache = new Dictionary<long, bool>();

        long indexed = 0, noText = 0, pending = 0, failed = 0, notIndexed = 0, requeued = 0;
        var lost = new List<long>();
        var byReason = new Dictionary<ContentIneligibility, long>();
        var byStatus = new Dictionary<ContentStatus, long>();
        var exts = new Dictionary<string, long>(StringComparer.Ordinal);

        var rows = new List<FileRow>(batchSize);
        var queued = new List<long>(batchSize);
        var docs = new List<(long Id, ContentStateLookup.DocSummary Doc)>(batchSize);
        long last = 0;
        while (true)
        {
            gate.WaitTurn(WorkClass.Maintenance, ct); // pause, profile and interactive search come first
            rows.Clear(); queued.Clear(); docs.Clear(); lost.Clear();
            var con = catalog.RentReader();
            try
            {
                ReadFiles(con, last, batchSize, rows);
                if (rows.Count > 0 && contentIndexerAvailable) ReadQueued(con, rows[0].Id, rows[^1].Id, queued);
            }
            finally { catalog.ReturnReader(con); }
            if (rows.Count == 0) break;
            if (contentIndexerAvailable)
            {
                var ccon = content.RentReader();
                try { ReadDocs(ccon, rows[0].Id, rows[^1].Id, docs); }
                finally { content.ReturnReader(ccon); }
            }

            // Merge join: the three lists are sorted by entry id.
            con = excludedDirs != null ? catalog.RentReader() : null;
            try
            {
                int qi = 0, di = 0;
                foreach (var f in rows)
                {
                    ContentStateInfo state;
                    if (!contentIndexerAvailable) state = new(ContentState.NotIndexed, ContentIneligibility.Disabled);
                    else
                    {
                        while (qi < queued.Count && queued[qi] < f.Id) qi++;
                        while (di < docs.Count && docs[di].Id < f.Id) di++;
                        bool isQueued = qi < queued.Count && queued[qi] == f.Id;
                        ContentStateLookup.DocSummary? doc = di < docs.Count && docs[di].Id == f.Id ? docs[di].Doc : null;
                        var row = f;
                        state = ContentStateLookup.Classify(isQueued, doc, row.MTime, row.Size, () =>
                        {
                            if (!roots.TryGetValue(row.RootId, out var root)) return null;
                            bool inExcluded = excludedDirs != null && IsInExcludedDir(con!, row.ParentId, excludedDirs, dirCache);
                            return rules.Check(root, row.Name, row.Size, (FileAttributes)row.Attrs, inExcluded);
                        });
                    }
                    switch (state.State)
                    {
                        case ContentState.Indexed: indexed++; break;
                        case ContentState.NoText: noText++; break;
                        case ContentState.Pending:
                            pending++;
                            // Not queued and in a known location: nothing would ever process it.
                            if (requeue != null && contentIndexerAvailable && !(qi < queued.Count && queued[qi] == f.Id) && roots.ContainsKey(f.RootId)) lost.Add(f.Id);
                            break;
                        case ContentState.Failed: failed++; break;
                        default:
                            notIndexed++;
                            if (state.DocStatus is { } st) Add(byStatus, st);
                            else
                            {
                                Add(byReason, state.Reason);
                                if (state.Reason == ContentIneligibility.UnsupportedType)
                                {
                                    string ext = f.Name.Contains('.') ? ContentEligibility.Extension(f.Name).ToLowerInvariant() : "";
                                    if (exts.ContainsKey(ext) || exts.Count < MaxDistinctExtensions) Add(exts, ext);
                                }
                            }
                            break;
                    }
                }
            }
            finally { if (con != null) catalog.ReturnReader(con); }
            if (lost.Count > 0) { requeue!(lost); requeued += lost.Count; }
            last = rows[^1].Id;
            gate.Report(WorkClass.Maintenance, rows.Count, 0);
            if (rows.Count < batchSize) break;
        }

        return new ContentCoverageReport
        {
            Files = indexed + noText + pending + failed + notIndexed,
            Indexed = indexed, NoText = noText, Pending = pending, Failed = failed, NotIndexed = notIndexed, Requeued = requeued,
            ByReason = byReason.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList(),
            ByExtractorStatus = byStatus.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList(),
            TopUnsupportedExtensions = exts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(5).Select(kv => (kv.Key, kv.Value)).ToList(),
            ComputedUtc = DateTime.UtcNow, Elapsed = sw.Elapsed,
        };
    }

    private static void Add<T>(Dictionary<T, long> d, T key) where T : notnull =>
        d[key] = d.TryGetValue(key, out long v) ? v + 1 : 1;

    private static void ReadFiles(SqliteConnection con, long after, int limit, List<FileRow> rows)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id, parent_id, root_id, name, size, mtime, attrs FROM entries WHERE id > $last AND kind = 0 ORDER BY id LIMIT $n";
        cmd.Parameters.AddWithValue("$last", after);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new FileRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)));
    }

    private static void ReadQueued(SqliteConnection con, long lo, long hi, List<long> ids)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT entry_id FROM content_queue WHERE entry_id BETWEEN $lo AND $hi ORDER BY entry_id";
        cmd.Parameters.AddWithValue("$lo", lo);
        cmd.Parameters.AddWithValue("$hi", hi);
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
    }

    private static void ReadDocs(SqliteConnection con, long lo, long hi, List<(long, ContentStateLookup.DocSummary)> docs)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT entry_id, status, mtime, size FROM docs WHERE entry_id BETWEEN $lo AND $hi ORDER BY entry_id";
        cmd.Parameters.AddWithValue("$lo", lo);
        cmd.Parameters.AddWithValue("$hi", hi);
        using var r = cmd.ExecuteReader();
        while (r.Read()) docs.Add((r.GetInt64(0), new((ContentStatus)r.GetInt32(1), r.GetInt64(2), r.GetInt64(3))));
    }

    /// <summary>
    /// Entry ids of the directories whose subtree is excluded from content indexing: the excluded folders found in the
    /// catalog, plus the root entry of any location that lies inside an excluded folder.
    /// </summary>
    private static HashSet<long> ExcludedFolderIds(ContentEligibility rules, ScopeRules scope, EntryStore entries)
    {
        var ids = new HashSet<long>();
        var rootList = scope.Roots.Select(r => (r.RootId, r.Path, r.RootEntryId)).ToList();
        foreach (var root in scope.Roots)
        {
            // The whole location is inside an excluded folder (same prefix test as the path-based rule).
            if (rules.IsInExcludedFolder(System.IO.Path.TrimEndingDirectorySeparator(root.Path) + "\\")) ids.Add(root.RootEntryId);
        }
        foreach (string folder in rules.ExcludedFolders)
            if (entries.Resolve(System.IO.Path.TrimEndingDirectorySeparator(folder), rootList) is long id) ids.Add(id);
        return ids;
    }

    /// <summary>True if directory <paramref name="dirId"/> is, or is under, an excluded folder. Caches every directory visited.</summary>
    private static bool IsInExcludedDir(SqliteConnection con, long dirId, HashSet<long> excluded, Dictionary<long, bool> cache)
    {
        if (cache.TryGetValue(dirId, out bool known)) return known;
        var chain = new List<long>();
        bool result = false;
        long cur = dirId;
        for (int i = 0; cur != 0 && i < MaxAncestry; i++)
        {
            if (cache.TryGetValue(cur, out bool v)) { result = v; break; }
            chain.Add(cur);
            if (excluded.Contains(cur)) { result = true; break; }
            cur = ParentOf(con, cur);
        }
        foreach (long id in chain) cache[id] = result;
        return result;
    }

    private static long ParentOf(SqliteConnection con, long id)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT parent_id FROM entries WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is long p ? p : 0;
    }
}
