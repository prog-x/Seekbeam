using System.Diagnostics;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Config;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.Engine;
using Seekbeam.Core.Search;
using Seekbeam.Core.Storage;

namespace Seekbeam.Core.Tests;

/// <summary>End-to-end tests on a real directory tree (scan, exclusions, watcher, links, permissions, reconciliation).</summary>
public sealed class IndexingIntegrationTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "seekbeam-it", Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _data;
    private SeekbeamEngine? _engine;

    public IndexingIntegrationTests()
    {
        _src = Path.Combine(_base, "source");
        _data = Path.Combine(_base, "data");
        Directory.CreateDirectory(_src);
        Write("Projets/Alpha/notes_réunion.txt", "a");
        Write("Projets/Alpha/budget 2026.xlsx", "b");
        Write("Projets/Beta/rapport_final.docx", "c");
        Write("Archives/2024/facture_001.pdf", "d");
        Write("node_modules/lib/index.js", "e");
        Write("Projets/Alpha/~$tmp.docx", "f");
        Write("deep/l1/l2/l3/l4/l5/profond.txt", "g");
    }

    private void Write(string rel, string content)
    {
        string p = Path.Combine(_src, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private SeekbeamEngine StartEngine(Action<SeekbeamConfig>? configure = null)
    {
        var storage = new SeekbeamStorage(_data);
        var cfgStore = new ConfigStore(storage);
        cfgStore.Update(c =>
        {
            c.Roots = [new RootConfig { Path = _src, Excludes = ["node_modules"] }];
            c.GlobalExcludes = ["~$*"];
            c.General.FirstRunCompleted = true;
            configure?.Invoke(c);
        });
        _engine = new SeekbeamEngine(storage, new EngineOptions { Unthrottled = true, EnableApps = false, EnableRecentItems = false, FolderIndexDrives = () => [] });
        _engine.Watcher.CoalesceWindow = TimeSpan.FromMilliseconds(200);
        _engine.Start();
        WaitIdle();
        return _engine;
    }

    private void StopEngine() { _engine?.Dispose(); _engine = null; Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }

    private void WaitIdle(int timeoutMs = 20_000)
    {
        var sw = Stopwatch.StartNew();
        Thread.Sleep(300);
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var s = _engine!.Indexer.Stats();
            if (!s.Busy && s.QueueLength == 0) { Thread.Sleep(150); if (!_engine.Indexer.Stats().Busy) return; }
            Thread.Sleep(50);
        }
        throw new TimeoutException("Indexer did not become idle");
    }

    private List<string> Find(string q) => _engine!.Search.Search(new SearchRequest { Query = q, MaxResults = 50, AllowFuzzy = false, IncludeApps = false }, CancellationToken.None)
        .Results.Select(r => r.FullPath).ToList();

    private bool WaitFor(Func<bool> cond, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(100); }
        return cond();
    }

    [Fact]
    public void InitialScanRespectsExclusionsAndFindsFiles()
    {
        StartEngine();
        Assert.Contains(Path.Combine(_src, @"Projets\Alpha\notes_réunion.txt"), Find("reunion notes"));
        Assert.Contains(Path.Combine(_src, @"Projets\Beta\rapport_final.docx"), Find("beta rapport"));
        Assert.Empty(Find("index.js"));      // node_modules excluded
        Assert.Empty(Find("tmp.docx"));      // ~$* excluded globally
        Assert.Contains(Path.Combine(_src, @"deep\l1\l2\l3\l4\l5\profond.txt"), Find("profond"));
    }

    /// <summary>
    /// Regression (flaky on CI): a command posted but not yet executed is work in progress. Before the fix the indexer
    /// reported "idle, empty queue" until it picked the command up, so a search right after start could find nothing.
    /// </summary>
    [Fact]
    public void PostedCommandCountsAsWorkUntilItsQueueDrains()
    {
        var storage = new SeekbeamStorage(_data);
        new ConfigStore(storage).Update(c => { c.Roots = [new RootConfig { Path = _src }]; c.General.FirstRunCompleted = true; });
        _engine = new SeekbeamEngine(storage, new EngineOptions { Unthrottled = true, EnableApps = false, EnableRecentItems = false, FolderIndexDrives = () => [] });
        _engine.Indexer.Post(new ReconcileCommand(null, QueuePriority.Reconcile)); // indexer thread not started yet
        Assert.True(_engine.Indexer.Stats().Busy, "a pending command must count as work");
        _engine.Start();
        WaitIdle();
        Assert.False(_engine.Indexer.Stats().Busy);
        Assert.Contains(Path.Combine(_src, @"Projets\Alpha\notes_réunion.txt"), Find("reunion notes"));
    }

    [Fact]
    public void MaxDepthIsRespected()
    {
        StartEngine(c => c.Roots[0].MaxDepth = 3);
        Assert.Empty(Find("profond"));
        Assert.NotEmpty(Find("l2"));   // deep=1, l1=2, l2=3: depth-3 directory is indexed
        Assert.Empty(Find("l3"));      // depth 4: beyond the limit
    }

    [Fact]
    public void DefaultMaxDepthIsTen() => Assert.Equal(10, new RootConfig().MaxDepth);

    [Fact]
    public void DepthLimitedFoldersAreReportedAndClearedWhenLimitIsRaised()
    {
        Write("deep/l1/l2/autre.txt", "h");
        Write("deep/l1/l2/ignore.tmp~", "i");
        StartEngine(c => { c.Roots[0].MaxDepth = 3; c.GlobalExcludes.Add("*.tmp~"); });
        var root = Assert.Single(_engine!.Roots());
        Assert.Equal(3, root.MaxDepth);
        Assert.Equal(1, root.DepthLimitedDirs);       // only deep\l1\l2 (depth 3) has unexplored children
        Assert.Equal(2, root.DepthHiddenEntries);     // l3 and autre.txt; the excluded file is not counted
        var dir = Assert.Single(_engine.DepthLimitedDirectories());
        Assert.Equal(Path.Combine(_src, @"deep\l1\l2"), dir.Path);
        Assert.True(_engine.DepthLimitedCount() > 0);
        Assert.Single(_engine.TakeNewDepthLimitNotices());
        Assert.Empty(_engine.TakeNewDepthLimitNotices());  // told once, not at every drain

        _engine.Config.Update(c => c.Roots[0].MaxDepth = -1);
        Assert.True(WaitFor(() => _engine.Roots()[0].DepthLimitedDirs == 0 && Find("profond").Count == 1, 20_000));
        Assert.Empty(_engine.DepthLimitedDirectories());
    }

    [Fact]
    public void WatcherAppliesCreateModifyDeleteRenameMove()
    {
        StartEngine();
        Write("Projets/Alpha/nouveau_document.md", "x");
        Assert.True(WaitFor(() => Find("nouveau_document").Count == 1), "create");

        File.Move(Path.Combine(_src, @"Projets\Alpha\nouveau_document.md"), Path.Combine(_src, @"Projets\Alpha\renomme_document.md"));
        Assert.True(WaitFor(() => Find("renomme_document").Count == 1 && Find("nouveau_document").Count == 0), "rename");

        File.Move(Path.Combine(_src, @"Projets\Alpha\renomme_document.md"), Path.Combine(_src, @"Archives\renomme_document.md"));
        Assert.True(WaitFor(() => Find("renomme_document").SingleOrDefault() == Path.Combine(_src, @"Archives\renomme_document.md")), "move");

        Directory.Move(Path.Combine(_src, @"Projets\Beta"), Path.Combine(_src, @"Projets\Gamma"));
        Assert.True(WaitFor(() => Find("rapport_final").SingleOrDefault() == Path.Combine(_src, @"Projets\Gamma\rapport_final.docx")), "dir rename");

        File.Delete(Path.Combine(_src, @"Archives\renomme_document.md"));
        Assert.True(WaitFor(() => Find("renomme_document").Count == 0), "delete");

        Directory.CreateDirectory(Path.Combine(_src, @"Nouveau\Sous"));
        Write("Nouveau/Sous/fichier_cree.txt", "y");
        Assert.True(WaitFor(() => Find("fichier_cree").Count == 1), "new subtree");
    }

    [Fact]
    public void OfflineChangesAreReconciledAtStartup()
    {
        StartEngine();
        StopEngine();
        File.Delete(Path.Combine(_src, @"Archives\2024\facture_001.pdf"));
        Write("Archives/2024/facture_002.pdf", "z");
        StartEngine();
        Assert.True(WaitFor(() => Find("facture_002").Count == 1 && Find("facture_001").Count == 0));
    }

    [Fact]
    public void JunctionCyclesDoNotLoop()
    {
        // Junction pointing to an ancestor: default policy does not follow links; with FollowLinks the cycle guard applies.
        string link = Path.Combine(_src, @"Projets\Alpha\boucle");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_src}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "junction created");
        StartEngine(c => c.Roots[0].FollowLinks = true);
        Assert.Single(Find("budget 2026"));   // not duplicated through the junction
        Assert.Contains(link, Find("boucle")); // the link itself is indexed
        StopEngine();
        Directory.Delete(link); // removes the junction only
    }

    [Fact]
    public void AccessDeniedDirectoryIsSkippedWithoutFailure()
    {
        string secret = Path.Combine(_src, "Secret");
        Directory.CreateDirectory(secret);
        File.WriteAllText(Path.Combine(secret, "cache.txt"), "s");
        string user = Environment.UserDomainName + "\\" + Environment.UserName;
        Run("icacls", $"\"{secret}\" /deny \"{user}\":(RD)");
        try
        {
            StartEngine();
            Assert.Empty(Find("cache.txt"));
            Assert.NotEmpty(Find("rapport_final"));
            Assert.Contains(_engine!.RecentErrors(), e => e.Message.Contains("Access denied"));
        }
        finally { Run("icacls", $"\"{secret}\" /remove:d \"{user}\""); }
    }

    [Fact]
    public void RemovingARootRemovesItsEntries()
    {
        var e = StartEngine();
        Assert.NotEmpty(Find("rapport_final"));
        e.Config.Update(c => c.Roots.Clear());
        Assert.True(WaitFor(() => Find("rapport_final").Count == 0));
    }

    private static void Run(string exe, string args)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
    }

    public void Dispose()
    {
        StopEngine();
        try { Directory.Delete(_base, true); } catch { }
    }
}
