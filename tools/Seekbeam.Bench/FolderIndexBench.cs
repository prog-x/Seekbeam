using System.Diagnostics;
using Seekbeam.Core.Config;
using Seekbeam.Core.Engine;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.Search;
using Seekbeam.Core.Storage;

namespace Seekbeam.Bench;

/// <summary>
/// Folder index on the real machine (TD-21): walk of every fixed drive with the default exclusions (folder listings only,
/// read-only), then "go to folder" searches over it. Measures time, CPU, memory, catalog size and search latency.
/// </summary>
internal static class FolderIndexBench
{
    private static readonly string[] Queries =
    [
        "prog fil", "win sys", "users pub", "app roam", "doc", "progra micro", "sou per", "temp", "pro files com", "data",
        "windows kits", "dotnet sdk", "local prog", @"prog\micro", "nuget", "steam", "office", "fonts", "user", "x",
    ];

    public static void Run(string dataDir, string mode)
    {
        var storage = new SeekbeamStorage(dataDir);
        new ConfigStore(storage).Update(c =>
        {
            c.Roots = []; // no indexed location: everything is the folder index
            c.General.FirstRunCompleted = true;
            c.Watch.Enabled = false;
            c.Resources.Mode = Enum.Parse<ResourceMode>(mode, true);
            c.FolderIndex.Enabled = true;
        });
        var p = Process.GetCurrentProcess();
        var cpu0 = p.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        var drives = NameIndexer.FixedDrives().ToList();
        Console.WriteLine("Drives: " + string.Join(", ", drives));
        using var e = new SeekbeamEngine(storage, new EngineOptions { EnableApps = false, EnableRecentItems = false, EnableWatchers = false });
        e.Start();
        long peakPrivate = 0;
        int idle = 0;
        var during = new List<double>();
        while (true)
        {
            Thread.Sleep(500);
            p.Refresh();
            peakPrivate = Math.Max(peakPrivate, p.PrivateMemorySize64);
            var t = Stopwatch.GetTimestamp();
            e.Search.Search(new SearchRequest { Query = "prog fil", Purpose = SearchPurpose.Navigate }, CancellationToken.None);
            during.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            var s = e.Indexer.Stats();
            idle = s.Busy || s.QueueLength > 0 ? 0 : idle + 1;
            if (idle >= 4) break;
        }
        double secs = sw.Elapsed.TotalSeconds - 2;
        var cpu = p.TotalProcessorTime - cpu0;
        var stats = e.Stats();
        Console.WriteLine($"WALK: {stats.FolderIndexFolders:N0} folders in {secs:F1} s = {stats.FolderIndexFolders / secs:N0} folders/s (mode {mode})");
        Console.WriteLine($"CPU: {cpu.TotalSeconds:F1} s ({cpu.TotalSeconds / secs:F2} cores) · peak private {peakPrivate / 1048576} MB · final private {p.PrivateMemorySize64 / 1048576} MB");
        Console.WriteLine($"SIZE: catalog {stats.CatalogBytes / 1048576.0:F1} MB, name index {stats.NameIndexBytes / 1048576.0:F1} MB");
        during.Sort();
        Console.WriteLine($"SEARCH DURING WALK: p50 {during[during.Count / 2]:F1} ms, p95 {during[(int)(during.Count * 0.95)]:F1} ms, max {during[^1]:F1} ms ({during.Count} samples)");
        var times = new List<double>();
        foreach (var q in Queries) e.Search.Search(new SearchRequest { Query = q, Purpose = SearchPurpose.Navigate }, CancellationToken.None); // warm-up
        for (int rep = 0; rep < 20; rep++)
            foreach (var q in Queries)
            {
                var t = Stopwatch.GetTimestamp();
                e.Search.Search(new SearchRequest { Query = q, Purpose = SearchPurpose.Navigate, MaxResults = 8 }, CancellationToken.None);
                times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
        times.Sort();
        Console.WriteLine($"NAVIGATE SEARCH ({Queries.Length} queries x 20): p50 {times[times.Count / 2]:F1} ms, p95 {times[(int)(times.Count * 0.95)]:F1} ms, p99 {times[(int)(times.Count * 0.99)]:F1} ms");
        foreach (var q in new[] { "prog fil", "win sys", @"prog\micro" })
        {
            var r = e.Search.Search(new SearchRequest { Query = q, Purpose = SearchPurpose.Navigate, MaxResults = 3 }, CancellationToken.None);
            Console.WriteLine($"  '{q}' -> {string.Join(" | ", r.Results.Select(x => x.FullPath))}");
        }
    }
}
