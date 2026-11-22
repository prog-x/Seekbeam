using Seekbeam.Core.Search;

namespace Seekbeam.Core.Tests;

public class NameIndexTests
{
    [Fact]
    public void SearchWorksFromDeltaImmediately()
    {
        using var env = new TestEnv();
        long root = env.AddRoot(@"C:\Data");
        env.Add(root, "rapport_annuel_2025.pdf");
        Assert.Contains("rapport_annuel_2025.pdf", env.Names("rapport"));
    }

    [Fact]
    public void FlushMergeAndReloadPreserveResults()
    {
        using var env = new TestEnv();
        env.Index.DeltaFlushThreshold = 1000;
        long root = env.AddRoot(@"C:\Data");
        var names = Enumerable.Range(0, 5000).Select(i => $"document_{i:D5}.txt").ToList();
        foreach (var chunk in names.Chunk(700)) env.AddMany(root, chunk);
        Assert.True(env.Index.SegmentCount >= 1);
        Assert.Equal(5001, env.Index.LiveCount);
        Assert.Equal("document_04321.txt", env.Names("04321").Single());

        env.Reopen(crash: false);
        Assert.Equal(5001, env.Index.LiveCount);
        Assert.Equal("document_04321.txt", env.Names("04321").Single());
    }

    [Fact]
    public void DeletesAndRenamesAreReflectedAcrossLevelsAndRestarts()
    {
        using var env = new TestEnv();
        env.Index.DeltaFlushThreshold = 100;
        long root = env.AddRoot(@"C:\Data");
        var ids = env.AddMany(root, Enumerable.Range(0, 300).Select(i => $"file_{i:D3}.txt"));
        env.Index.MaybeMerge(force: true); // everything in the base segment
        env.Delete(ids[10]);
        env.Rename(ids[20], root, "renamed_special.txt");
        Assert.Empty(env.Names("file_010"));
        Assert.Empty(env.Names("file_020"));
        Assert.Single(env.Names("renamed_special"));

        env.Reopen(crash: false);
        Assert.Empty(env.Names("file_010"));
        Assert.Empty(env.Names("file_020"));
        Assert.Single(env.Names("renamed_special"));
        env.Index.MaybeMerge(force: true);
        Assert.Empty(env.Names("file_010"));
        Assert.Equal(300, env.Index.LiveCount); // 300 files + root - 1 deleted
    }

    [Fact]
    public void CrashWithoutFlushIsRecoveredFromCatalog()
    {
        using var env = new TestEnv();
        env.Index.DeltaFlushThreshold = 50;
        long root = env.AddRoot(@"C:\Data");
        var ids = env.AddMany(root, Enumerable.Range(0, 120).Select(i => $"alpha_{i:D3}.log")); // flushed to segments
        env.AddMany(root, ["only_in_delta.txt"]);                                              // delta only
        env.Delete(ids[5]);
        env.Rename(ids[6], root, "moved_after_flush.log");

        env.Reopen(crash: true);
        Assert.Single(env.Names("only_in_delta"));
        Assert.Empty(env.Names("alpha_005"));
        Assert.Empty(env.Names("alpha_006"));
        Assert.Single(env.Names("moved_after_flush"));
        Assert.Equal(121, env.Index.LiveCount);
    }

    [Fact]
    public void CorruptManifestTriggersRebuild()
    {
        using var env = new TestEnv();
        env.Index.DeltaFlushThreshold = 10;
        long root = env.AddRoot(@"C:\Data");
        env.AddMany(root, Enumerable.Range(0, 40).Select(i => $"beta_{i}.md"));
        env.Index.Dispose();
        env.Db.Dispose();
        File.WriteAllText(Path.Combine(env.Storage.IndexDir, "manifest.json"), "{ not json");
        env.Reopen(crash: false);
        Assert.Equal(41, env.Index.LiveCount);
        Assert.Contains("beta_33.md", env.Names("beta_33"));
    }

    [Fact]
    public void ConcurrentSearchesDuringIndexingAreSafe()
    {
        using var env = new TestEnv();
        env.Index.DeltaFlushThreshold = 2000;
        long root = env.AddRoot(@"C:\Data");
        using var cts = new CancellationTokenSource();
        var errors = new List<Exception>();
        var searchers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { env.Search("item 7"); }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            }
        })).ToArray();
        for (int b = 0; b < 30; b++) env.AddMany(root, Enumerable.Range(0, 500).Select(i => $"item_{b}_{i}.dat"));
        cts.Cancel();
        Task.WaitAll(searchers);
        Assert.Empty(errors);
        Assert.Equal(15001, env.Index.LiveCount);
    }
}
