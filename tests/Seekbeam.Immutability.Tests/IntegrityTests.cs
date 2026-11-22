using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Seekbeam.Core.Config;
using Seekbeam.Core.Content;
using Seekbeam.Core.Engine;
using Seekbeam.Core.Resources;
using Seekbeam.Core.Search;
using Seekbeam.Core.Storage;
using Seekbeam.Extraction;
using Seekbeam.Tools;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Seekbeam.Immutability.Tests;

/// <summary>
/// FILE_IMMUTABILITY_POLICY §7 — integrity tests. A reference snapshot (SHA-256, size, attributes, creation/write/access
/// times, SDDL, alternate streams, reparse targets, tree structure) is taken before each scenario and compared after.
/// Any difference fails the test, except LastAccessTime, which NTFS may update on read (OS side effect, reported).
/// </summary>
[Collection("Integrity")]
public sealed class IntegrityTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "seekbeam-integrity", Guid.NewGuid().ToString("N"));
    private AuditDataset.Info? _info;

    private AuditDataset.Info Dataset() => _info = AuditDataset.Build(Path.Combine(_base, "source"));

    private static string HostPath => Path.Combine(StaticAnalysisTests.RepoRoot, "src", "Seekbeam.ExtractorHost", "bin", "Debug", "net10.0-windows", "Seekbeam.ExtractorHost.exe");
    private static string AppPath => Path.Combine(StaticAnalysisTests.RepoRoot, "src", "Seekbeam.App", "bin", "Debug", "net10.0-windows", "Seekbeam.exe");

    private static void Configure(SeekbeamConfig c, string root)
    {
        // Unlimited depth: the dataset includes a path longer than 260 characters, 13 levels deep, which must be extracted too.
        c.Roots = [new RootConfig { Path = root, FollowLinks = true, IncludeHidden = true, Excludes = [], MaxDepth = -1 }];
        c.GlobalExcludes = [];
        c.General.FirstRunCompleted = true;
        c.General.StartWithWindows = false;
        c.Resources.Mode = ResourceMode.Performance;
        c.Content.TimeoutSeconds = 20;
        c.Search.IncludeHidden = true;
        c.Search.IncludeSystem = true;
    }

    private void AssertUnchanged(Dictionary<string, SnapshotEntry> before, Dictionary<string, SnapshotEntry> after, ISet<string>? ignore = null)
    {
        var diffs = SourceSnapshot.Compare(before, after, ignore);
        var real = diffs.Where(d => !d.IsAccessTimeOnly).ToList();
        output.WriteLine($"Snapshot: {before.Count} entries; {real.Count} modification(s); {diffs.Count - real.Count} last-access-time update(s) by the OS.");
        foreach (var d in real) output.WriteLine("  MODIFIED " + d);
        Assert.Empty(real);
    }

    private static bool WaitIdle(SeekbeamEngine e, int timeoutMs = 120_000)
    {
        var sw = Stopwatch.StartNew();
        int stable = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var s = e.Stats();
            bool idle = !s.Names.Busy && s.Names.QueueLength == 0 && !s.Content.Busy && s.Content.QueueLength == 0;
            stable = idle ? stable + 1 : 0;
            if (stable >= 4) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    private SeekbeamEngine StartEngine(string dataDir, string root)
    {
        var storage = new SeekbeamStorage(dataDir);
        new ConfigStore(storage).Update(c => Configure(c, root));
        var registry = ExtractorRegistry.CreateDefault();
        var e = new SeekbeamEngine(storage, new EngineOptions
        {
            EnableApps = false, EnableRecentItems = false,
            SupportedExtensions = () => registry.SupportedExtensions,
            ExtractionFactory = () => new ExtractorClient(HostPath),
        });
        // Real conditions: the extractor host runs in its AppContainer sandbox (TD-17).
        e.ProvisionExtractorSandbox(Path.GetDirectoryName(HostPath)!);
        e.Start();
        return e;
    }

    [Fact]
    public void FullIndexingOfEveryFormatLinksErrorsAndLocksModifiesNothing()
    {
        var info = Dataset();
        var before = SourceSnapshot.Take(info.Root);
        var beforeOutside = SourceSnapshot.Take(info.OutsideTarget);
        // A file held open exclusively by "another application" during the whole run.
        using (var locker = new FileStream(info.LockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var e = StartEngine(Path.Combine(_base, "data"), info.Root);
            e.Resources!.Pause();          // pause / resume
            Thread.Sleep(800);
            e.Resources.Resume();
            Assert.True(WaitIdle(e), "indexing did not finish");
            // Opportunistic discovery + reconciliation on the same tree.
            e.Recent.ReportOpened(Path.Combine(info.Root, "Documents", "memo.txt"));
            e.Reconcile();
            Thread.Sleep(500);
            Assert.True(WaitIdle(e), "reconcile did not finish");

            // Sanity: the run really read the files.
            var found = AuditDataset.ContentMarkers.Where(m => e.DeepSearch.Search(new DeepSearchRequest { Query = m, Scope = DeepScope.ContentOnly }, default).Results.Count > 0).ToList();
            output.WriteLine($"Content markers found: {found.Count}/{AuditDataset.ContentMarkers.Length}: missing {string.Join(", ", AuditDataset.ContentMarkers.Except(found))}");
            Assert.True(found.Count >= AuditDataset.ContentMarkers.Length - 2, "content extraction coverage too low");
            // …and all of it was extracted inside the AppContainer sandbox, through brokered read-only handles (TD-17).
            Assert.Equal(SandboxLevel.AppContainer, ExtractorClient.LastLevel);
            Assert.NotEmpty(e.Search.Search(new SearchRequest { Query = "outside_file" }, default).Results); // junction to outside followed
            Assert.Single(e.Search.Search(new SearchRequest { Query = "memo.txt", AllowFuzzy = false }, default).Results); // cycle not duplicated
            output.WriteLine($"Indexed entries: {e.NameIndex.LiveCount}, content docs: {e.ContentDb.Counts().Docs}, errors: {e.RecentErrors().Count}");
        }
        AssertUnchanged(before, SourceSnapshot.Take(info.Root));
        AssertUnchanged(beforeOutside, SourceSnapshot.Take(info.OutsideTarget));
    }

    [Fact]
    public void IncrementalWatcherChangesByOthersAreTheOnlyChanges()
    {
        var info = Dataset();
        var before = SourceSnapshot.Take(info.Root);
        using var e = StartEngine(Path.Combine(_base, "data"), info.Root);
        Assert.True(WaitIdle(e));
        // Changes made by "the user" (this test), not by Seekbeam.
        string created = Path.Combine(info.Root, "Documents", "nouveau.txt");
        string modified = Path.Combine(info.Root, "Documents", "notes.md");
        string deleted = Path.Combine(info.Root, "Documents", "table.csv");
        string renamedFrom = Path.Combine(info.Root, "Documents", "data.json"), renamedTo = Path.Combine(info.Root, "Documents", "data_renamed.json");
        File.WriteAllText(created, "créé par l'utilisateur");
        File.WriteAllText(modified, "modifié par l'utilisateur");
        File.Delete(deleted);
        File.Move(renamedFrom, renamedTo);
        Thread.Sleep(2500);
        Assert.True(WaitIdle(e));
        Assert.Single(e.Search.Search(new SearchRequest { Query = "data_renamed" }, default).Results);
        e.Reconcile();
        Thread.Sleep(500);
        Assert.True(WaitIdle(e));
        e.Dispose();

        var after = SourceSnapshot.Take(info.Root);
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Documents", @"Documents\nouveau.txt", @"Documents\notes.md", @"Documents\table.csv", @"Documents\data.json", @"Documents\data_renamed.json" };
        AssertUnchanged(before, after, touched);
        // The user's changes are exactly what the user wrote.
        Assert.Equal(Hash("créé par l'utilisateur"), after[@"Documents\nouveau.txt"].Sha256);
        Assert.Equal(Hash("modifié par l'utilisateur"), after[@"Documents\notes.md"].Sha256);
        Assert.False(after.ContainsKey(@"Documents\table.csv"));
        Assert.Equal(before[@"Documents\data.json"].Sha256, after[@"Documents\data_renamed.json"].Sha256);
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(s)));

    [Fact]
    public void RealProcessKilledDuringIndexingThenRecoveredModifiesNothing()
    {
        Assert.True(File.Exists(AppPath), "Seekbeam.exe not built: " + AppPath);
        var info = Dataset();
        var before = SourceSnapshot.Take(info.Root);
        string data = Path.Combine(_base, "appdata");
        var storage = new SeekbeamStorage(data);
        new ConfigStore(storage).Update(c => Configure(c, info.Root));
        SqliteConnection.ClearAllPools();

        var p1 = Process.Start(new ProcessStartInfo(AppPath, $"--background --data \"{data}\"") { UseShellExecute = false })!;
        Thread.Sleep(2500);
        KillTree(p1); // forced stop / crash in the middle of indexing

        var p2 = Process.Start(new ProcessStartInfo(AppPath, $"--background --data \"{data}\"") { UseShellExecute = false })!;
        Assert.True(WaitQueuesEmpty(Path.Combine(data, "db", "catalog.db"), 120_000), "recovery did not complete");
        using (var exit = Process.Start(new ProcessStartInfo(AppPath, $"--exit --data \"{data}\"") { UseShellExecute = false })!) exit.WaitForExit(10_000);
        Assert.True(p2.WaitForExit(30_000), "graceful exit failed");
        Thread.Sleep(500);
        string log = string.Join("\n", Directory.GetFiles(Path.Combine(data, "logs")).Select(f => File.ReadAllText(f)));
        Assert.Contains("ended abnormally", log);   // crash detected
        Assert.Contains("Stopped cleanly", log);    // graceful shutdown via --exit
        AssertUnchanged(before, SourceSnapshot.Take(info.Root));
    }

    private static bool WaitQueuesEmpty(string db, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        int stable = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT (SELECT COUNT(*) FROM name_queue) + (SELECT COUNT(*) FROM content_queue), (SELECT COUNT(*) FROM entries)";
                using var r = cmd.ExecuteReader();
                r.Read();
                stable = r.GetInt64(0) == 0 && r.GetInt64(1) > 10 ? stable + 1 : 0;
                if (stable >= 6) return true;
            }
            catch (SqliteException) { stable = 0; }
            Thread.Sleep(500);
        }
        return false;
    }

    private static void KillTree(Process p)
    {
        try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
        foreach (var h in Process.GetProcessesByName("Seekbeam.ExtractorHost")) { try { h.Kill(); } catch { } }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (_info != null) AuditDataset.Cleanup(_info);
        try { Directory.Delete(_base, true); } catch { }
    }
}
