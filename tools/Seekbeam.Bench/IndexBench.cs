using System.Diagnostics;
using System.Runtime.InteropServices;
using Seekbeam.Core.Config;
using Seekbeam.Core.Engine;
using Seekbeam.Core.Storage;

namespace Seekbeam.Bench;

/// <summary>Real indexing benchmark on a directory tree: names (and optionally content), with resource sampling.</summary>
internal static partial class IndexBench
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    public static void Run(string dataDir, string root, string mode, bool content)
    {
        var storage = new SeekbeamStorage(dataDir);
        new ConfigStore(storage).Update(c =>
        {
            c.Roots = [new RootConfig { Path = Path.GetFullPath(root), Excludes = [] }];
            c.GlobalExcludes = [];
            c.General.FirstRunCompleted = true;
            c.Content.Enabled = content;
            c.Watch.Enabled = false;
            c.Resources.Mode = Enum.Parse<ResourceMode>(mode, true);
        });
        var p = Process.GetCurrentProcess();
        GetProcessIoCounters(p.Handle, out var io0);
        var cpu0 = p.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        string host = Path.Combine(AppContext.BaseDirectory, "Seekbeam.ExtractorHost.exe");
        for (string? d = AppContext.BaseDirectory; d != null && !File.Exists(host); d = Path.GetDirectoryName(d))
            host = Path.Combine(d, "src", "Seekbeam.ExtractorHost", "bin", "Release", "net10.0-windows", "Seekbeam.ExtractorHost.exe");
        using var e = new SeekbeamEngine(storage, new EngineOptions
        {
            EnableApps = false, EnableRecentItems = false, EnableWatchers = false, FolderIndexDrives = () => [],
            SupportedExtensions = () => ["txt", "md", "csv", "json", "log", "cs", "js", "html", "xml", "sql", "py", "yaml", "ini"],
            ExtractionFactory = content && File.Exists(host) ? () => new Seekbeam.Core.Content.ExtractorClient(host) : null,
        });
        if (content && File.Exists(host)) e.ProvisionExtractorSandbox(Path.GetDirectoryName(host)!); // measured in its real sandbox
        e.Start();
        long peakPrivate = 0, peakWs = 0;
        int idle = 0;
        var searchTimes = new List<double>();
        long lastReport = 0;
        while (true)
        {
            Thread.Sleep(500);
            p.Refresh();
            peakPrivate = Math.Max(peakPrivate, p.PrivateMemorySize64);
            peakWs = Math.Max(peakWs, p.WorkingSet64);
            // Search latency while indexing (search must stay fast during indexing).
            var t = Stopwatch.GetTimestamp();
            e.Search.Search(new Seekbeam.Core.Search.SearchRequest { Query = "rapport 2024" }, CancellationToken.None);
            searchTimes.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            var s = e.Indexer.Stats();
            var cs = e.ContentIndexer?.Stats();
            bool busy = s.Busy || s.QueueLength > 0 || (cs != null && (cs.Busy || cs.QueueLength > 0));
            idle = busy ? 0 : idle + 1;
            if (sw.ElapsedMilliseconds - lastReport > 10_000)
            {
                lastReport = sw.ElapsedMilliseconds;
                Console.WriteLine($"  t={sw.Elapsed.TotalSeconds:F0}s entries={e.NameIndex.LiveCount:N0} queue={s.QueueLength:N0} rate={s.EntriesPerSecond:N0}/s " +
                                  $"content={cs?.Indexed ?? 0:N0} cq={cs?.QueueLength ?? 0:N0} private={p.PrivateMemorySize64 / 1048576} MB");
            }
            if (idle >= 4) break;
        }
        double secs = sw.Elapsed.TotalSeconds - 2;
        p.Refresh();
        GetProcessIoCounters(p.Handle, out var io1);
        var cpu = p.TotalProcessorTime - cpu0;
        long n = e.NameIndex.LiveCount;
        searchTimes.Sort();
        Console.WriteLine($"INDEX: {n:N0} entries in {secs:F1} s = {n / secs:N0} entries/s (mode {mode}, content {(content ? "on" : "off")})");
        if (e.ContentIndexer != null) Console.WriteLine($"CONTENT: {e.ContentDb.Counts().Docs:N0} documents, host restarts {e.ContentIndexer.Stats().HostRestarts}");
        Console.WriteLine($"CPU: {cpu.TotalSeconds:F1} s process time ({100 * cpu.TotalSeconds / (secs * Environment.ProcessorCount):F1} % of the machine, {cpu.TotalSeconds / secs:F2} cores)");
        Console.WriteLine($"MEMORY: peak private {peakPrivate / 1048576} MB, peak working set {peakWs / 1048576} MB, final private {p.PrivateMemorySize64 / 1048576} MB");
        Console.WriteLine($"IO (process): read {(io1.ReadTransferCount - io0.ReadTransferCount) / 1048576.0:F0} MB, written {(io1.WriteTransferCount - io0.WriteTransferCount) / 1048576.0:F0} MB (Seekbeam data dir only)");
        Console.WriteLine($"SEARCH DURING INDEXING: n={searchTimes.Count} p50={SearchBench.P(searchTimes, 50):F2} ms p95={SearchBench.P(searchTimes, 95):F2} ms max={searchTimes[^1]:F2} ms");
        Console.WriteLine($"INDEX SIZE: catalog {e.Catalog.FileSizeBytes() / 1048576.0:F1} MB + name segments {e.NameIndex.SegmentBytes / 1048576.0:F1} MB = {(e.Catalog.FileSizeBytes() + e.NameIndex.SegmentBytes) / (double)Math.Max(1, n):F0} bytes/entry; content {e.ContentDb.FileSizeBytes() / 1048576.0:F1} MB");
    }
}
