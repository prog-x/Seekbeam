using Seekbeam.Core.Config;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.NameIndex;
using Seekbeam.Core.Search;

namespace Seekbeam.Core.Tests;

/// <summary>
/// Path fragments ("sou per see", "src\seek"), the folder index modes of the launcher (fallback / navigation), folders
/// first in navigation, and the "ranked lower" penalty (TD-20, TD-21).
/// </summary>
public sealed class PathSearchTests : IDisposable
{
    private const long Outside = (long)EntryFlags.Outside, Lowered = (long)(EntryFlags.Outside | EntryFlags.Lowered);
    private readonly TestEnv _env = new();

    public PathSearchTests()
    {
        // Indexed location.
        long docs = _env.AddRoot(@"C:\source");
        long perso = _env.Add(docs, "perso", dir: true);
        long seekbeam = _env.Add(perso, "Seekbeam", dir: true);
        long src = _env.Add(seekbeam, "src", dir: true);
        _env.Add(src, "Seekbeam.Core", dir: true);
        _env.Add(seekbeam, "seed.txt");
        _env.Add(perso, "seekbeam-notes.md");
        long notes = _env.Add(docs, "notes", dir: true);
        _env.Add(notes, "notes.txt");
        _env.Add(docs, "notes.docx");
        // Folder index (outside the location).
        long drive = _env.AddRoot(@"D:\", Outside);
        long data = _env.Add(drive, "Data", dir: true, xflags: Outside);
        long clients = _env.Add(data, "Clients", dir: true, xflags: Outside);
        long dupont = _env.Add(clients, "Dupont", dir: true, xflags: Outside);
        _env.Add(dupont, "Factures", dir: true, xflags: Outside);
        _env.Add(data, "Tool", dir: true, xflags: Outside);
        long pf = _env.Add(drive, "Program Files", dir: true, xflags: Lowered);
        _env.Add(pf, "Tool", dir: true, xflags: Lowered);
    }

    public void Dispose() => _env.Dispose();

    private List<string> Paths(string q, SearchPurpose p = SearchPurpose.Launch, int max = 20) =>
        _env.Search(q, max, p).Results.Select(r => r.FullPath).ToList();

    [Fact]
    public void FragmentsInPathOrderFindTheFolder()
    {
        Assert.Equal(@"C:\source\perso\Seekbeam", Paths("sou per see", SearchPurpose.Navigate).First());
        Assert.Equal(@"C:\source\perso\Seekbeam", Paths("per seek").First());
        Assert.Equal(@"D:\Data\Clients\Dupont\Factures", Paths("cli dup fact", SearchPurpose.Navigate).First());
    }

    [Fact]
    public void LastFragmentNamesTheTarget()
    {
        // "seek src": the folder src under Seekbeam, before Seekbeam itself or seed.txt.
        Assert.Equal(@"C:\source\perso\Seekbeam\src", Paths("seek src", SearchPurpose.Navigate).First());
    }

    [Fact]
    public void SeparatorImposesTheHierarchy()
    {
        Assert.Equal(@"C:\source\perso\Seekbeam\src", Paths(@"seek\src", SearchPurpose.Navigate).First());
        Assert.Contains(@"D:\Data\Clients\Dupont\Factures", Paths(@"clients\fact", SearchPurpose.Navigate));
        Assert.Contains(@"D:\Data\Clients\Dupont\Factures", Paths("data/dup/fac", SearchPurpose.Navigate));
        // Wrong order: nothing.
        Assert.Empty(Paths(@"fact\clients", SearchPurpose.Navigate));
        Assert.DoesNotContain(@"C:\source\perso\Seekbeam\src", Paths(@"src\seek", SearchPurpose.Navigate));
    }

    [Fact]
    public void ParserBuildsChains()
    {
        var q = QueryParser.Parse(@"src\seek doc");
        Assert.Equal(3, q.Tokens.Count);
        Assert.Single(q.Chains);
        Assert.Equal([0, 1], q.Chains[0]);
        Assert.Empty(QueryParser.Parse("src seek").Chains);
        Assert.Single(QueryParser.Parse("a/b/c").Chains[0].Skip(2));
    }

    [Fact]
    public void LauncherShowsFolderIndexOnlyAfterIndexedResults()
    {
        // "seek": every result of the location comes before any folder of the folder index.
        var res = _env.Search("tool").Results;
        Assert.Equal(2, res.Count); // only the folder index has "Tool": shown to fill the rows
        var mixed = _env.Search("data").Results.Select(r => r.FullPath).ToList();
        Assert.Contains(@"D:\Data", mixed);
        // A location result is never behind a folder-index one.
        var all = Paths("s");
        int firstOutside = all.FindIndex(p => p.StartsWith(@"D:\"));
        int lastInside = all.FindLastIndex(p => p.StartsWith(@"C:\"));
        Assert.True(firstOutside < 0 || lastInside < firstOutside, string.Join(" | ", all));
    }

    [Fact]
    public void KindFolderAndNavigationRankFolderIndexWithTheOthers()
    {
        Assert.Equal(@"D:\Data\Clients\Dupont", Paths("dupont kind:folder").First());
        Assert.Equal(@"D:\Data\Clients\Dupont", Paths("dupont", SearchPurpose.Navigate).First());
    }

    [Fact]
    public void NavigationListsFoldersFirstAndNoApps()
    {
        _env.Apps.Replace([Apps.AppEntry.Create("Notes App", @"C:\apps\notes.lnk", Apps.AppSource.StartMenu, @"C:\apps\notes.exe")]);
        var res = _env.Search("notes", 20, SearchPurpose.Navigate).Results;
        Assert.Equal(ResultKind.Folder, res[0].Kind);
        Assert.Equal(@"C:\source\notes", res[0].FullPath);
        Assert.DoesNotContain(res, r => r.Kind == ResultKind.App);
        int firstFile = res.ToList().FindIndex(r => r.Kind == ResultKind.File);
        int lastFolder = res.ToList().FindLastIndex(r => r.Kind == ResultKind.Folder);
        Assert.True(firstFile < 0 || lastFolder < firstFile);
        // The launcher still offers the application.
        Assert.Contains(_env.Search("notes").Results, r => r.Kind == ResultKind.App);
    }

    [Fact]
    public void LoweredFoldersComeAfterTheSameNameElsewhere()
    {
        var tools = Paths("tool", SearchPurpose.Navigate);
        Assert.Equal([@"D:\Data\Tool", @"D:\Program Files\Tool"], tools.Take(2).ToList());
    }

    [Fact]
    public void ApproximateMatchesNeitherHitRootPathsNorPassExactFiles()
    {
        // Found by the end-to-end check: "beta rapp" ≈ "seekbeam" + "AppData" in the root's whole path.
        using var env = new TestEnv();
        long root = env.AddRoot(@"C:\Users\Admin\AppData\Local\Temp\seekbeam-dialogcheck\62a9532e49364689ad4b9a9cfe73b90e");
        long rap = env.Add(env.Add(root, "Beta", dir: true), "Rapports", dir: true);
        env.Add(rap, "rapport.txt");
        var res = env.Search("beta rapp", 10, SearchPurpose.Navigate).Results.Select(r => r.Name).ToList();
        Assert.Equal(["Rapports", "rapport.txt"], res);
    }

    [Fact]
    public void RankedLowerOnlyBreaksTiesAndTheTargetNameWins()
    {
        // Real case (benchmark on a developer machine): "prog\micro" must give Program Files (x86)\Microsoft, although
        // Program Files is ranked lower, rather than a deep "…\Programs\…\SaisieMicro".
        using var env = new TestEnv();
        long c = env.AddRoot(@"C:\", Outside);
        long pf = env.Add(c, "Program Files (x86)", dir: true, xflags: Lowered);
        env.Add(pf, "Microsoft", dir: true, xflags: Lowered);
        long cur = env.Add(env.Add(c, "PC SOFT", dir: true, xflags: Outside), "Programs", dir: true, xflags: Outside);
        foreach (var n in new[] { "Data", "Controls", "Smart Controls", "FR", "WM" }) cur = env.Add(cur, n, dir: true, xflags: Outside);
        env.Add(cur, "SaisieMicro", dir: true, xflags: Outside);
        Assert.Equal(@"C:\Program Files (x86)\Microsoft", env.Search(@"prog\micro", 5, SearchPurpose.Navigate).Results[0].FullPath);
        Assert.Equal(@"C:\Program Files (x86)\Microsoft", env.Search("prog micro", 5, SearchPurpose.Navigate).Results[0].FullPath);
    }

    [Fact]
    public void DeepSearchNeverSeesTheFolderIndex()
    {
        using var snap = _env.Index.Acquire();
        var outcome = NameSearcher.Search(snap, QueryParser.Parse("dupont"), 10, new NameSearchOptions(), CancellationToken.None);
        Assert.Empty(outcome.Top);
    }
}

/// <summary>Where the folder index walks (pure planning, no disk).</summary>
public sealed class FolderIndexPlannerTests
{
    private static SeekbeamConfig Cfg(params RootConfig[] roots)
    {
        var c = new SeekbeamConfig { Roots = [.. roots] };
        c.FolderIndex.ExcludedPaths = [@"C:\Windows"];
        return c;
    }

    private static List<FolderIndexRoot> Plan(SeekbeamConfig c, string[] drives, string[]? learned = null, Func<string, bool>? exists = null) =>
        FolderIndexPlanner.Plan(c, drives, learned ?? [], exists ?? (_ => true), @"C:\Users\me\AppData\Local\Seekbeam");

    [Fact]
    public void WalksFixedDrivesAndFoldersExcludedFromLocations()
    {
        var c = Cfg(new RootConfig { Path = @"C:\Users\me", Excludes = ["AppData", "node_modules", "*.tmp", @"build\out"] });
        var plan = Plan(c, [@"C:\", @"D:\"]);
        Assert.Contains(plan, p => p.Path == @"C:\" && p.Origin == FolderIndexOrigin.Drive && p.Config.Watch);
        Assert.Contains(plan, p => p.Path == @"D:\" && p.Origin == FolderIndexOrigin.Drive);
        // AppData: excluded by name at the top of the location, so the drive walk (which stops at the location) never sees it.
        var appData = Assert.Single(plan, p => p.Origin == FolderIndexOrigin.ExcludedFromLocation);
        Assert.Equal(@"C:\Users\me\AppData", appData.Path);
        Assert.False(appData.Config.Watch);
        Assert.All(plan, p => { Assert.False(p.Config.IndexContent); Assert.True(p.Config.IncludeHidden); });
    }

    [Fact]
    public void DriveFullyCoveredByALocationIsNotWalked()
    {
        var plan = Plan(Cfg(new RootConfig { Path = @"D:\" }), [@"C:\", @"D:\"]);
        Assert.DoesNotContain(plan, p => p.Path == @"D:\");
    }

    [Fact]
    public void LearnedFoldersOnlyWhenNothingCoversThem()
    {
        var c = Cfg(new RootConfig { Path = @"C:\Users\me" });
        c.FolderIndex.LearnedDepth = 3;
        var plan = Plan(c, [@"C:\"], [@"\\server\share\Team", @"C:\Tools\X", @"C:\Users\me\Docs", @"E:\Usb\Photos"]);
        var learned = plan.Where(p => p.Origin == FolderIndexOrigin.Learned).Select(p => p.Path).ToList();
        Assert.Equal([@"\\server\share\Team", @"E:\Usb\Photos"], learned); // C:\ is walked; Docs is in the location
        Assert.All(plan.Where(p => p.Origin == FolderIndexOrigin.Learned), p => Assert.Equal(3, p.Config.MaxDepth));
    }

    [Fact]
    public void SwitchesAreHonoured()
    {
        var c = Cfg(new RootConfig { Path = @"C:\Users\me", Excludes = ["AppData"] });
        c.FolderIndex.FixedDrives = false;
        Assert.Equal([@"\\srv\s"], Plan(c, [@"C:\"], [@"\\srv\s"]).Select(p => p.Path));
        c.FolderIndex.LearnFolders = false;
        Assert.Empty(Plan(c, [@"C:\"], [@"\\srv\s"]));
        c.FolderIndex.FixedDrives = true; c.FolderIndex.Enabled = false;
        Assert.Empty(Plan(c, [@"C:\"], [@"\\srv\s"]));
    }

    [Fact]
    public void ExcludedPathsAndMissingFoldersAreSkipped()
    {
        var c = Cfg(new RootConfig { Path = @"C:\Windows\Temp", Excludes = ["Logs"] }, new RootConfig { Path = @"C:\Users\me", Excludes = ["Gone"] });
        var plan = Plan(c, [@"C:\"], [@"C:\Windows\X"], exists: p => !p.EndsWith("Gone"));
        Assert.DoesNotContain(plan, p => p.Path.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan, p => p.Path.EndsWith("Gone"));
    }

    [Fact]
    public void RankedLowerRules()
    {
        var root = new RootScope
        {
            RootId = 1, Path = @"C:\", Config = new RootConfig(), Excludes = new ExclusionMatcher([]), Kind = RootKind.FolderIndex,
            LoweredPrefixes = FolderIndexPlanner.ExpandedPrefixes([@"C:\Program Files"]), LoweredNames = FolderIndexPlanner.LoweredNames(["AppData", @"C:\x"]),
            ExcludedPrefixes = FolderIndexPlanner.ExpandedPrefixes([@"C:\Windows"]),
        };
        Assert.True(root.IsLowered(@"C:\Program Files"));
        Assert.True(root.IsLowered(@"C:\Program Files\Vendor"));
        Assert.False(root.IsLowered(@"C:\Program Files (x86)"));
        Assert.True(root.IsLowered(@"C:\Users\me\AppData\Roaming"));
        Assert.False(root.IsLowered(@"C:\Users\me\Documents"));
        Assert.True(root.UnderExcludedPrefix(@"C:\Windows"));
        Assert.True(root.UnderExcludedPrefix(@"C:\Windows\System32"));
        Assert.False(root.UnderExcludedPrefix(@"C:\WindowsApps2"));
        Assert.Equal(EntryFlags.Outside | EntryFlags.Lowered, (EntryFlags)DirectoryScanner.FolderXFlags(root, @"C:\Program Files\X"));
        Assert.Equal(EntryFlags.Outside, (EntryFlags)DirectoryScanner.FolderXFlags(root, @"C:\Data"));
    }
}

/// <summary>Live completion of a typed absolute path (read-only listing of the disk).</summary>
public sealed class PathCompleterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seekbeam-pc", Guid.NewGuid().ToString("N"));

    public PathCompleterTests()
    {
        foreach (var d in new[] { "Alpha", "alpine", "Beta", "Projets Alpha", "Été" }) Directory.CreateDirectory(Path.Combine(_dir, d));
        File.WriteAllText(Path.Combine(_dir, "alpha.txt"), "x");
        var hidden = Path.Combine(_dir, "AlphaHidden");
        Directory.CreateDirectory(hidden);
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.Directory);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData(@"C:\Win", true)]
    [InlineData(@"c:/Users", true)]
    [InlineData(@"\\server\share", true)]
    [InlineData("%APPDATA%\\Mic", true)]
    [InlineData("\"C:\\Program Files\"", true)]
    [InlineData("C:", false)]
    [InlineData("alpha beta", false)]
    [InlineData(@"src\seek", false)]
    [InlineData("50%", false)]
    public void RecognisesAbsolutePaths(string q, bool expected) => Assert.Equal(expected, PathCompleter.IsAbsolutePathQuery(q));

    [Fact]
    public void SplitsFolderAndPrefix()
    {
        Assert.Equal((@"C:\", "Win"), PathCompleter.Split(@"C:\Win"));
        Assert.Equal((@"C:\Users", ""), PathCompleter.Split(@"C:\Users\"));
        Assert.Equal((@"C:\Users", "Pub"), PathCompleter.Split("C:/Users/Pub"));
        Assert.Equal((@"\\server", ""), PathCompleter.Split(@"\\server\"));
        Assert.Equal((@"\\server", "sha"), PathCompleter.Split(@"\\server\sha"));
        Assert.Equal((@"\\server\share", "Doc"), PathCompleter.Split(@"\\server\share\Doc"));
        string temp = Path.TrimEndingDirectorySeparator(Environment.GetEnvironmentVariable("TEMP")!);
        Assert.Equal((temp, "x"), PathCompleter.Split(@"%TEMP%\x"));
    }

    [Fact]
    public void ListsFoldersFirstThenStartsWithThenContains()
    {
        var r = PathCompleter.Complete(_dir + @"\alp", 20, includeHidden: false, CancellationToken.None).Results;
        Assert.Equal(["Alpha", "alpine", "Projets Alpha", "alpha.txt"], r.Select(x => x.Name));
        Assert.Equal(ResultKind.Folder, r[0].Kind);
        Assert.Equal(Path.Combine(_dir, "Alpha"), r[0].FullPath);
        Assert.Equal(ResultKind.File, r[^1].Kind);
        Assert.Contains("AlphaHidden", PathCompleter.Complete(_dir + @"\alp", 20, includeHidden: true, CancellationToken.None).Results.Select(x => x.Name));
    }

    [Fact]
    public void TrailingSeparatorListsTheFolderAndAccentsAreIgnored()
    {
        Assert.Equal(6, PathCompleter.Complete(_dir + "\\", 50, false, CancellationToken.None).Results.Count);
        Assert.Equal("Été", Assert.Single(PathCompleter.Complete(_dir + @"\ete", 10, false, CancellationToken.None).Results).Name);
        Assert.Empty(PathCompleter.Complete(_dir + @"\missing\x", 10, false, CancellationToken.None).Results);
    }

    [Fact]
    public void HugeFoldersAreCutNotReadWhole()
    {
        for (int i = 0; i < 300; i++) File.WriteAllText(Path.Combine(_dir, $"zz{i:D4}.txt"), "");
        int saved = PathCompleter.MaxEntriesRead;
        PathCompleter.MaxEntriesRead = 50;
        try
        {
            var r = PathCompleter.Complete(_dir + @"\zz", 1000, false, CancellationToken.None);
            Assert.InRange(r.TotalMatches, 1, 50); // stopped at the limit, no exception
        }
        finally { PathCompleter.MaxEntriesRead = saved; }
    }

    [Fact]
    public void SearchEngineCompletesOnlyWhenAsked()
    {
        using var env = new TestEnv();
        var with = env.Engine.Search(new SearchRequest { Query = _dir + @"\Bet", PathCompletion = true }, CancellationToken.None);
        Assert.Equal("Beta", Assert.Single(with.Results).Name);
        var without = env.Engine.Search(new SearchRequest { Query = _dir + @"\Bet" }, CancellationToken.None);
        Assert.Empty(without.Results);
    }
}
