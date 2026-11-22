using System.Diagnostics;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Config;
using Seekbeam.Core.Content;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.Resources;
using Seekbeam.Core.Storage;

namespace Seekbeam.Bench;

/// <summary>
/// Content coverage census (dashboard breakdown) on a synthetic catalog: duration and peak private memory.
/// The catalog comes from <see cref="SearchBench.Populate"/>; content.db is filled once with a realistic mix
/// (most eligible files indexed, some failed / without text, the rest never extracted) and one top-level folder is
/// excluded from content indexing so that the ancestry walk is exercised.
/// </summary>
internal static class CoverageBench
{
    // Extensions handled by the extractors (subset of ExtractorRegistry; the bench does not reference Seekbeam.Extraction).
    private static readonly string[] Supported =
        ["txt", "md", "csv", "json", "log", "cs", "js", "html", "xml", "sql", "py", "yaml", "ini", "pdf", "docx", "xlsx", "pptx", "odt", "ods", "odp", "rtf", "doc", "xls", "ppt", "zip"];

    public static void Run(string dataDir, long count, int seed)
    {
        SearchBench.Populate(dataDir, count, seed);
        var storage = new SeekbeamStorage(dataDir);
        using var catalog = new CatalogDb(storage);
        using var content = new ContentDb(storage) { StoreText = false };
        var entries = new EntryStore(catalog);

        long rootEntry = CatalogDb.Scalar<long>(catalog.Writer, "SELECT id FROM entries WHERE parent_id = 0 ORDER BY id LIMIT 1");
        // The top-level folder with the most sub-entries, so that the ancestry walk covers a large subtree.
        long excludedDir = CatalogDb.Scalar<long>(catalog.Writer,
            $"SELECT d.id FROM entries d JOIN entries c ON c.parent_id = d.id WHERE d.parent_id = {rootEntry} AND d.kind = 1 GROUP BY d.id ORDER BY COUNT(*) DESC LIMIT 1");
        string excludedPath = entries.GetPath(excludedDir)!;
        var root = new RootScope { RootId = 1, Path = @"C:\BenchRoot", RootEntryId = rootEntry, Config = new RootConfig { Path = @"C:\BenchRoot" }, Excludes = new ExclusionMatcher([]) };
        var scope = new ScopeRules([root], [], dataDir);
        var rules = new ContentEligibility(new ContentConfig { ExcludedFolders = [excludedPath] }, Supported);
        Console.WriteLine($"Excluded folder: {excludedPath}");

        FillContentDb(catalog, content, scope, rules);

        var results = new List<(double Seconds, long PeakMB, ContentCoverageReport Report)>();
        for (int run = 0; run < 3; run++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long baseline = Bench.PrivateMB(), peak = baseline;
            using var stop = new CancellationTokenSource();
            var sampler = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested) { peak = Math.Max(peak, Bench.PrivateMB()); Thread.Sleep(20); }
            });
            var sw = Stopwatch.StartNew();
            var r = ContentCoverage.Compute(catalog, content, entries, scope, rules, true, UnthrottledGate.Instance, default);
            sw.Stop();
            stop.Cancel(); sampler.Wait();
            peak = Math.Max(peak, Bench.PrivateMB());
            results.Add((sw.Elapsed.TotalSeconds, peak - baseline, r));
            Console.WriteLine($"run {run + 1}: {sw.Elapsed.TotalSeconds:F2} s, private baseline {baseline} MB, peak +{peak - baseline} MB");
        }
        var rep = results[^1].Report;
        Console.WriteLine($"Files {rep.Files:N0} = indexed {rep.Indexed:N0} + no text {rep.NoText:N0} + failed {rep.Failed:N0} + pending {rep.Pending:N0} + not eligible {rep.NotIndexed:N0}");
        Console.WriteLine("Not eligible: " + string.Join(", ", rep.ByReason.Select(x => $"{x.Reason} {x.Count:N0}")));
        Console.WriteLine("Top unsupported extensions: " + string.Join(", ", rep.TopUnsupportedExtensions.Select(x => $"{(x.Extension.Length == 0 ? "(none)" : x.Extension)} {x.Count:N0}")));
        var secs = results.Select(r => r.Seconds).Order().ToList();
        Console.WriteLine($"COVERAGE: files={rep.Files:N0} median={secs[1]:F2} s ({rep.Files / secs[1]:N0} files/s) peak private +{results.Max(r => r.PeakMB)} MB");
    }

    /// <summary>One-time: documents for eligible files (93 % indexed, 2 % without text, 1 % failed; the rest stays pending).</summary>
    private static void FillContentDb(CatalogDb catalog, ContentDb content, ScopeRules scope, ContentEligibility rules)
    {
        if (content.Counts().Docs > 0) { Console.WriteLine($"content.db already has {content.Counts().Docs:N0} documents."); return; }
        var sw = Stopwatch.StartNew();
        var rnd = new Random(7);
        long last = 0, docs = 0;
        var root = scope.Roots[0];
        while (true)
        {
            var batch = new List<(long Id, string Name, long Size, long MTime, long Attrs, long Parent)>();
            var con = catalog.RentReader();
            try
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT id, name, size, mtime, attrs, parent_id FROM entries WHERE id > $l AND kind = 0 ORDER BY id LIMIT 20000";
                cmd.Parameters.AddWithValue("$l", last);
                using var r = cmd.ExecuteReader();
                while (r.Read()) batch.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
            }
            finally { catalog.ReturnReader(con); }
            if (batch.Count == 0) break;
            foreach (var f in batch)
            {
                // Excluded-folder membership is ignored here: those files simply get no document, as in real life.
                if (rules.Check(root, f.Name, f.Size, (FileAttributes)f.Attrs, false) != ContentIneligibility.None) continue;
                int p = rnd.Next(100);
                if (p >= 96) continue; // never extracted: pending
                var st = p < 93 ? ContentStatus.Indexed : p < 95 ? ContentStatus.Empty : ContentStatus.Failed;
                content.Upsert(f.Id, f.MTime, f.Size, "bench", 1, st, st == ContentStatus.Indexed ? "texte" : "", null);
                docs++;
            }
            last = batch[^1].Id;
        }
        Console.WriteLine($"Filled content.db with {docs:N0} documents in {sw.Elapsed.TotalSeconds:F0} s");
    }
}
