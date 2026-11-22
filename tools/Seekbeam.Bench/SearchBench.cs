using System.Diagnostics;
using Seekbeam.Core.Apps;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Config;
using Seekbeam.Core.NameIndex;
using Seekbeam.Core.Search;
using Seekbeam.Core.Storage;
using Seekbeam.Tools;

namespace Seekbeam.Bench;

internal static class SearchBench
{
    public static readonly string[] Queries =
    [
        "ant 202611 cahier", "rapport", "doc", "facture 2024", "pdf", "a", "zzqx", "surveillance data", "proj 2023 final",
        "budget prévisionnel", "img_1234", "cahier des charges", "contrat client", "readme", "config json", "réunion compte rendu",
        "ete", "zurich", "v2", "backup", "audit sécurité", "notes", "export csv", "projets rapport", "documents facture 2025",
        "kubernetes", "docker compose", "invoice", "spec design", "test data", "rapprot", "facutre", "survellance", "presentaton",
        "ext:pdf rapport", "kind:folder projets", "in:clients contrat", "-final rapport", "文件", "отчёт",
    ];

    /// <summary>Populates a catalog with a synthetic tree of <paramref name="count"/> entries via the real write path.</summary>
    public static void Populate(string dataDir, long count, int seed)
    {
        var storage = new SeekbeamStorage(dataDir);
        using var db = new CatalogDb(storage);
        long existing = CatalogDb.Scalar<long>(db.Writer, "SELECT COUNT(*) FROM entries");
        if (existing >= count) { Console.WriteLine($"Catalog already has {existing:N0} entries."); return; }
        if (existing > 0) throw new InvalidOperationException("Data dir contains a different dataset; use an empty directory.");
        var writer = new CatalogWriter(db);
        using var index = new NameIndexManager(storage, db);
        index.Load();
        var gen = new NameGenerator(seed);
        var sw = Stopwatch.StartNew();
        var dirIds = new List<long>();
        var depths = new List<int>();
        CatalogWriter.Batch? batch = writer.Begin();
        long rootId = writer.Insert(batch, 0, 1, @"C:\BenchRoot", EntryKind.Directory, 0, 0, 0, 16, 0, 0);
        long n = 0;
        long mtime = DateTime.UtcNow.ToFileTimeUtc();
        void Tick()
        {
            if (++n % 5000 != 0) return;
            index.Apply(batch!.Commit());
            batch.Dispose();
            batch = writer.Begin();
            if (n % 500_000 == 0) Console.WriteLine($"  {n:N0} entries, {n / sw.Elapsed.TotalSeconds:N0}/s, private {Bench.PrivateMB()} MB");
        }
        gen.GenerateTree(count, 12,
            (parent, name, depth) =>
            {
                long pid = parent < 0 ? rootId : dirIds[parent];
                long id = writer.Insert(batch!, pid, 1, name, EntryKind.Directory, 0, mtime, mtime, 16, 0, depth);
                dirIds.Add(id); depths.Add(depth);
                Tick();
                return dirIds.Count - 1;
            },
            (parent, name) =>
            {
                long pid = parent < 0 ? rootId : dirIds[parent];
                writer.Insert(batch!, pid, 1, name, EntryKind.File, gen.Next(5_000_000), mtime, mtime, 32, 0, depths[parent] + 1);
                Tick();
            });
        index.Apply(batch!.Commit());
        batch.Dispose();
        index.MaybeMerge(force: true);
        Console.WriteLine($"Populated {n:N0} entries in {sw.Elapsed.TotalSeconds:F1} s ({n / sw.Elapsed.TotalSeconds:N0}/s), merges={index.MergeCount}");
    }

    public static void Run(string dataDir, int repetitions)
    {
        var storage = new SeekbeamStorage(dataDir);
        var swLoad = Stopwatch.StartNew();
        using var db = new CatalogDb(storage);
        using var index = new NameIndexManager(storage, db);
        index.Load();
        var entries = new EntryStore(db);
        var usage = new UsageStore(db);
        var cfg = new SearchConfig();
        var engine = new SearchEngine(index, entries, new AppCatalog(), usage, () => cfg);
        Console.WriteLine($"Loaded index: {index.LiveCount:N0} live entries, segments {index.SegmentBytes / 1048576.0:F1} MB, " +
                          $"catalog {db.FileSizeBytes() / 1048576.0:F1} MB, load {swLoad.ElapsedMilliseconds} ms, private {Bench.PrivateMB()} MB");
        // Warm-up (page cache).
        foreach (var q in Queries) engine.Search(new SearchRequest { Query = q }, CancellationToken.None);

        var all = new List<double>();
        Console.WriteLine($"{"query",-26} {"matches",10} {"p50",8} {"p95",8} {"max",8}  top result");
        foreach (var q in Queries)
        {
            var times = new List<double>();
            SearchResultSet last = SearchResultSet.Empty;
            for (int i = 0; i < repetitions; i++)
            {
                var t = Stopwatch.GetTimestamp();
                last = engine.Search(new SearchRequest { Query = q, MaxResults = 8 }, CancellationToken.None);
                times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
            all.AddRange(times);
            times.Sort();
            string top = last.Results.Count > 0 ? last.Results[0].Name : "-";
            if (top.Length > 40) top = top[..40];
            Console.WriteLine($"{q,-26} {last.TotalMatches,10:N0} {P(times, 50),8:F2} {P(times, 95),8:F2} {times[^1],8:F2}  {top}{(last.UsedFuzzy ? " (fuzzy)" : "")}");
        }
        all.Sort();
        Console.WriteLine($"ALL QUERIES: n={all.Count} p50={P(all, 50):F2} ms p95={P(all, 95):F2} ms p99={P(all, 99):F2} ms max={all[^1]:F2} ms");

        // Typing simulation: successive prefixes with cancellation of the previous query.
        string typed = "cahier des charges video";
        var ts = new List<double>();
        for (int i = 1; i <= typed.Length; i++)
        {
            var t = Stopwatch.GetTimestamp();
            engine.Search(new SearchRequest { Query = typed[..i] }, CancellationToken.None);
            ts.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }
        ts.Sort();
        Console.WriteLine($"TYPING '{typed}': p50={P(ts, 50):F2} ms p95={P(ts, 95):F2} ms max={ts[^1]:F2} ms");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var p = Process.GetCurrentProcess();
        Console.WriteLine($"MEMORY after search: private {p.PrivateMemorySize64 / 1048576} MB, working set {p.WorkingSet64 / 1048576} MB, managed heap {GC.GetTotalMemory(false) / 1048576} MB");
    }

    public static double P(List<double> sorted, int pct) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(pct / 100.0 * sorted.Count) - 1)];
}
