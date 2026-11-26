using System.Diagnostics;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Config;
using Seekbeam.Core.Engine;
using Seekbeam.Core.Search;
using Seekbeam.Core.Storage;

namespace Seekbeam.Core.Tests;

/// <summary>
/// The folder index end to end (TD-21), on a real tree that plays the part of a fixed drive: folders outside the indexed
/// location are found, files there never are, exclusions and "ranked lower" rules apply, the location is not duplicated,
/// directory events are followed, learned folders join, and switching the feature off removes it.
/// </summary>
public sealed class FolderIndexIntegrationTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "seekbeam-fi", Guid.NewGuid().ToString("N"));
    private readonly string _drive, _home, _data, _share;
    private SeekbeamEngine? _engine;

    public FolderIndexIntegrationTests()
    {
        _drive = Path.Combine(_base, "drive");
        _home = Path.Combine(_drive, "Users", "me");
        _data = Path.Combine(_base, "seekbeam-data");
        _share = Path.Combine(_base, "share");
        foreach (var d in new[]
        {
            @"Users\me\Projets\Alpha", @"Users\me\AppData\Roaming\OutilPerso", @"Data\Clients\Dupont\Factures 2026",
            @"Data\OutilPerso", @"Program Files\Vendor\OutilPerso", @"Windows\System32\drivers", @"Data\node_modules\lib", @"Data\.git\objects",
        }) Directory.CreateDirectory(Path.Combine(_drive, d));
        File.WriteAllText(Path.Combine(_home, @"Projets\Alpha\notes.txt"), "a");
        File.WriteAllText(Path.Combine(_drive, @"Data\Clients\contrat_dupont.pdf"), "b");
        Directory.CreateDirectory(Path.Combine(_share, @"Equipe\Q1\Rapports"));
        Directory.CreateDirectory(Path.Combine(_share, @"Equipe\Q1\Rapports\Trop profond"));
    }

    public void Dispose()
    {
        StopEngine();
        try { Directory.Delete(_base, true); } catch { }
    }

    private SeekbeamEngine StartEngine(Action<SeekbeamConfig>? configure = null, Action<SeekbeamEngine>? beforeStart = null)
    {
        var storage = new SeekbeamStorage(_data);
        new ConfigStore(storage).Update(c =>
        {
            c.Roots = [new RootConfig { Path = _home, Excludes = ["AppData"] }];
            c.GlobalExcludes = [];
            c.General.FirstRunCompleted = true;
            c.FolderIndex.ExcludedPaths = [Path.Combine(_drive, "Windows")];
            c.FolderIndex.LowerRanked = [Path.Combine(_drive, "Program Files"), "AppData"];
            c.FolderIndex.LearnedDepth = 2;
            configure?.Invoke(c);
        });
        _engine = new SeekbeamEngine(storage, new EngineOptions
        {
            Unthrottled = true, EnableApps = false, EnableRecentItems = false, FolderIndexDrives = () => [_drive],
        });
        _engine.Watcher.CoalesceWindow = TimeSpan.FromMilliseconds(200);
        beforeStart?.Invoke(_engine);
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

    private static bool WaitFor(Func<bool> cond, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(100); }
        return cond();
    }

    private List<SearchResult> Nav(string q) => _engine!.Search.Search(
        new SearchRequest { Query = q, MaxResults = 50, AllowFuzzy = false, Purpose = SearchPurpose.Navigate }, CancellationToken.None).Results.ToList();

    private List<string> NavPaths(string q) => Nav(q).Select(r => r.FullPath).ToList();

    private List<string> Launch(string q) => _engine!.Search.Search(
        new SearchRequest { Query = q, MaxResults = 50, AllowFuzzy = false, IncludeApps = false }, CancellationToken.None).Results.Select(r => r.FullPath).ToList();

    private string D(string rel) => Path.Combine(_drive, rel);

    [Fact]
    public void FoldersOutsideTheLocationAreFoundButNeverTheirFiles()
    {
        StartEngine();
        Assert.Equal(D(@"Data\Clients\Dupont"), NavPaths("dupont").First());
        Assert.Contains(D(@"Data\Clients\Dupont\Factures 2026"), NavPaths("clients fact"));
        Assert.Equal(D(@"Data\Clients\Dupont\Factures 2026"), NavPaths(@"dupont\fac").First());
        // Files of the folder index are never recorded.
        Assert.DoesNotContain(NavPaths("contrat"), p => p.Contains("contrat_dupont"));
        Assert.DoesNotContain(Launch("contrat"), p => p.Contains("contrat_dupont"));
        // The launcher offers them to fill the rows, after the location.
        Assert.Contains(D(@"Data\Clients\Dupont"), Launch("dupont"));
        var stats = _engine!.Stats();
        Assert.True(stats.FolderIndexFolders >= 8, "folder index: " + stats.FolderIndexFolders);
        Assert.DoesNotContain(_engine.Roots(), r => r.Path.Equals(_drive, StringComparison.OrdinalIgnoreCase)); // dashboard: locations only
    }

    [Fact]
    public void ExclusionsAndTheLocationAreRespected()
    {
        StartEngine();
        Assert.Empty(NavPaths("system32"));          // excluded absolute folder (Windows)
        Assert.Empty(NavPaths("drivers"));
        Assert.Empty(NavPaths("node_modules"));      // excluded by name
        Assert.Empty(NavPaths("objects"));           // below .git
        // The location is indexed once, by the location (no copy in the folder index).
        var alpha = Nav("alpha").Where(r => r.FullPath.EndsWith(@"Projets\Alpha", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(alpha);
        Assert.True(alpha[0].EntryId > 0);
        Assert.Contains(Path.Combine(_home, @"Projets\Alpha\notes.txt"), Launch("notes"));
    }

    [Fact]
    public void ExcludedTopFolderOfTheLocationIsReachedAndRankedLower()
    {
        StartEngine();
        var outils = NavPaths("outilperso");
        Assert.Equal(3, outils.Count);
        Assert.Equal(D(@"Data\OutilPerso"), outils[0]);      // the plain one first
        Assert.Contains(Path.Combine(_home, @"AppData\Roaming\OutilPerso"), outils); // AppData (excluded from the location) is reached
        Assert.Contains(D(@"Program Files\Vendor\OutilPerso"), outils);
    }

    [Fact]
    public void DirectoryEventsAreFollowedFilesAreIgnored()
    {
        StartEngine();
        Directory.CreateDirectory(D(@"Data\Clients\Martin"));
        Assert.True(WaitFor(() => NavPaths("martin").Contains(D(@"Data\Clients\Martin"))), "new folder not indexed");
        Directory.Move(D(@"Data\Clients\Martin"), D(@"Data\Clients\Martinez"));
        Assert.True(WaitFor(() => NavPaths("martinez").Contains(D(@"Data\Clients\Martinez")) && !NavPaths("martin").Contains(D(@"Data\Clients\Martin"))));
        File.WriteAllText(D(@"Data\Clients\nouveau_client.txt"), "x");
        Thread.Sleep(1200);
        WaitIdle();
        Assert.Empty(NavPaths("nouveau_client"));
    }

    [Fact]
    public void BurstOfNewFoldersIsRescannedFolderByFolder()
    {
        // Found by code review: a burst under a drive root re-walked the whole drive at the highest priority.
        StartEngine();
        _engine!.Watcher.BurstThreshold = 10;
        for (int i = 0; i < 40; i++) Directory.CreateDirectory(D($@"Data\Install\Module{i:D2}"));
        Assert.True(WaitFor(() => NavPaths("module").Count == 40, 20_000), "burst not indexed: " + NavPaths("module").Count);
    }

    [Fact]
    public void LearnedFolderJoinsWithItsLevelsBelow()
    {
        StartEngine();
        string team = Path.Combine(_share, "Equipe");
        Assert.Empty(NavPaths("rapports"));
        _engine!.RecordNavigation(team);
        Assert.True(WaitFor(() => { WaitIdle(); return NavPaths("rapports").Contains(Path.Combine(team, @"Q1\Rapports")); }), "learned folder not indexed");
        Assert.Empty(NavPaths("profond")); // LearnedDepth = 2
        Assert.Contains(_engine.RecentLocations(10), r => r.FullPath.Equals(team, StringComparison.OrdinalIgnoreCase));
        // Persisted: still there after a restart.
        StopEngine();
        StartEngine();
        Assert.Contains(Path.Combine(team, @"Q1\Rapports"), NavPaths("rapports"));
    }

    [Fact]
    public void RemovingTheLocationHandsItsFoldersToTheFolderIndex()
    {
        StartEngine();
        _engine!.Config.Update(c => c.Roots = []);
        Assert.True(WaitFor(() => { WaitIdle(); return NavPaths("alpha").Contains(Path.Combine(_home, @"Projets\Alpha")); }), "folder index did not take over");
        Assert.True(Nav("alpha").All(r => r.EntryId > 0));
        Assert.Empty(Launch("notes.txt")); // its files are no longer indexed
    }

    [Fact]
    public void SwitchingOffRemovesTheFolderIndex()
    {
        StartEngine();
        Assert.NotEmpty(NavPaths("dupont"));
        _engine!.Config.Update(c => c.FolderIndex.Enabled = false);
        Assert.True(WaitFor(() => { WaitIdle(); return NavPaths("dupont").Count == 0; }), "folder index not removed");
        Assert.Equal(0, _engine.Stats().FolderIndexFolders);
        Assert.Contains(Path.Combine(_home, @"Projets\Alpha"), NavPaths("alpha")); // the location stays
    }

    [Fact]
    public void ExcludeActionOnAFolderOfTheIndexAddsAnAbsoluteExclusion()
    {
        StartEngine();
        _engine!.ExcludePath(D(@"Data\Clients"));
        Assert.Contains(D(@"Data\Clients"), _engine.Config.Current.FolderIndex.ExcludedPaths);
        Assert.True(WaitFor(() => { WaitIdle(); return NavPaths("dupont").Count == 0; }), "excluded folder still indexed");
    }

    [Fact]
    public void LinksOfALocationAreStillFollowedIntoTheFolderIndexArea()
    {
        // Found by code review: the target of a followed junction lay under a folder-index root (a drive), and was refused.
        string link = Path.Combine(_home, "LienClients");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{D(@"Data\Clients")}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        StartEngine(c => c.Roots[0].FollowLinks = true);
        Assert.Contains(Path.Combine(link, "contrat_dupont.pdf"), Launch("contrat_dupont"));
    }

    [Fact]
    public void ProgramFolderIsNeverWalked()
    {
        // The program folder receives the sandbox's read ACE (audit W7): it must stay outside the folder index.
        StartEngine(beforeStart: e => e.Indexer.ExcludedFromFolderIndex = [D(@"Data\Clients")]);
        Assert.Empty(NavPaths("dupont"));
        Assert.Contains(D(@"Data\OutilPerso"), NavPaths("outilperso"));
        Assert.False(_engine!.Indexer.Scope.IsInScope(D(@"Data\Clients\Dupont"), out _));
    }

    [Fact]
    public void RemovingTheFolderIndexDeletesInSmallTransactions()
    {
        // More folders than one delete batch: everything goes, in several short transactions, nothing left behind.
        for (int i = 0; i < Engine_DeleteChunk + 500; i++) Directory.CreateDirectory(D($@"Bulk\{i / 100:D3}\{i:D5}"));
        StartEngine();
        Assert.True(_engine!.Stats().FolderIndexFolders > Engine_DeleteChunk);
        _engine.Config.Update(c => c.FolderIndex.Enabled = false);
        Assert.True(WaitFor(() => { WaitIdle(); return _engine.Stats().FolderIndexFolders == 0; }, 60_000));
        Assert.Equal(0L, CatalogDb.Scalar<long>(_engine.Catalog.Writer, "SELECT COUNT(*) FROM roots WHERE kind=1"));
        Assert.Contains(Path.Combine(_home, @"Projets\Alpha\notes.txt"), Launch("notes"));
    }

    private const int Engine_DeleteChunk = Seekbeam.Core.Indexing.NameIndexer.DeleteChunk;

    [Fact]
    public void CatalogV2IsMigrated()
    {
        // A v2 catalog (before the folder index): no roots.kind, no entries.xflags, no learned_folders.
        var storage = new SeekbeamStorage(_data);
        using (var db = new CatalogDb(storage))
        {
            CatalogDb.Exec(db.Writer, "DROP TABLE learned_folders; ALTER TABLE roots DROP COLUMN kind; ALTER TABLE entries DROP COLUMN xflags; PRAGMA user_version=2;");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        StartEngine();
        Assert.Equal(D(@"Data\Clients\Dupont"), NavPaths("dupont").First());
        Assert.Contains(Path.Combine(_home, @"Projets\Alpha\notes.txt"), Launch("notes"));
        Assert.Equal(3L, CatalogDb.Scalar<long>(_engine!.Catalog.Writer, "PRAGMA user_version;"));
    }
}
