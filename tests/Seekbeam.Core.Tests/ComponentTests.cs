using Seekbeam.Core.Apps;
using Seekbeam.Core.Config;
using Seekbeam.Core.Content;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.Resources;

namespace Seekbeam.Core.Tests;

public class ExclusionMatcherTests
{
    [Theory]
    [InlineData("node_modules", null, true)]
    [InlineData("NODE_MODULES", null, true)]
    [InlineData("report.tmp", null, true)]
    [InlineData("~$doc.docx", null, true)]
    [InlineData("readme.md", null, false)]
    [InlineData("out", @"build\out", true)]
    [InlineData("out", @"src\out", false)]
    [InlineData("cache", @"a\b\cache", true)]
    public void Matches(string name, string? rel, bool expected)
    {
        var m = new ExclusionMatcher(["node_modules", "*.tmp", "~$*", @"build\out", @"**\cache"]);
        Assert.Equal(expected, m.IsExcluded(name, rel));
    }
}

public class ResourceDecisionTests
{
    private static SystemSample S(double cpu = 10, int mem = 40, double idle = 5, bool battery = false, bool busy = false) => new(cpu, mem, idle, battery, busy, DateTime.UtcNow);

    [Fact]
    public void AutoModeAdaptsToSystemState()
    {
        var cfg = new ResourceConfig();
        Assert.Equal(20_000, ResourceManager.Decide(cfg, S(), DateTime.Now).NamesPerSecond);
        Assert.False(ResourceManager.Decide(cfg, S(busy: true), DateTime.Now).NamesAllowed);
        Assert.Equal(ResourceProfile.Economy.NamesPerSecond, ResourceManager.Decide(cfg, S(battery: true), DateTime.Now).NamesPerSecond);
        Assert.False(ResourceManager.Decide(cfg, S(cpu: 95), DateTime.Now).ContentAllowed);
        Assert.Equal(0, ResourceManager.Decide(cfg, S(idle: 600), DateTime.Now).NamesPerSecond); // performance = unlimited
    }

    [Fact]
    public void FixedModesIgnoreSignals()
    {
        var cfg = new ResourceConfig { Mode = ResourceMode.Economy };
        var p = ResourceManager.Decide(cfg, S(idle: 9999), DateTime.Now);
        Assert.Equal(ResourceProfile.Economy.NamesPerSecond, p.NamesPerSecond);
        Assert.True(p.ContentAllowed);
    }

    [Fact]
    public void ContentTimeWindows()
    {
        Assert.True(ResourceManager.InAnyWindow(["22:00-07:00"], DateTime.Today.AddHours(23)));
        Assert.True(ResourceManager.InAnyWindow(["22:00-07:00"], DateTime.Today.AddHours(3)));
        Assert.False(ResourceManager.InAnyWindow(["22:00-07:00"], DateTime.Today.AddHours(12)));
        var cfg = new ResourceConfig { ContentTimeWindows = ["01:00-02:00"] };
        Assert.False(ResourceManager.Decide(cfg, S(), DateTime.Today.AddHours(12)).ContentAllowed);
    }

    [Fact]
    public void PausePreventsWork()
    {
        using var rm = new ResourceManager(() => new ResourceConfig { Mode = ResourceMode.Performance }, () => false);
        rm.Pause(WorkClass.Content);
        using var cts = new CancellationTokenSource(300);
        Assert.ThrowsAny<OperationCanceledException>(() => rm.WaitTurn(WorkClass.Content, cts.Token));
        rm.WaitTurn(WorkClass.Names, CancellationToken.None); // other classes unaffected
        rm.Resume();
        rm.WaitTurn(WorkClass.Content, CancellationToken.None);
    }

    [Fact]
    public void SearchHasPriorityOverIndexing()
    {
        bool searching = true;
        using var rm = new ResourceManager(() => new ResourceConfig { Mode = ResourceMode.Performance }, () => searching);
        var t = Task.Run(() => rm.WaitTurn(WorkClass.Names, CancellationToken.None));
        Assert.False(t.Wait(200));
        searching = false;
        Assert.True(t.Wait(2000));
    }
}

public class LnkParserTests
{
    [Fact]
    public void ParsesRealStartMenuShortcuts()
    {
        var lnks = AppScanner.StartMenuFolders().Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })).Take(40).ToList();
        if (lnks.Count == 0) return; // no shortcuts on this machine
        var before = lnks.ToDictionary(p => p, p => (File.GetLastWriteTimeUtc(p), new FileInfo(p).Length));
        int withTarget = lnks.Count(p => LnkParser.TryParseFile(p)?.Target != null);
        Assert.True(withTarget > lnks.Count / 2, $"{withTarget}/{lnks.Count} shortcuts resolved");
        foreach (var p in lnks) Assert.Equal(before[p], (File.GetLastWriteTimeUtc(p), new FileInfo(p).Length)); // untouched
    }

    [Fact]
    public void GarbageIsRejected()
    {
        Assert.Null(LnkParser.TryParse(new byte[10]));
        var junk = new byte[200]; junk[0] = 0x4C; junk[4] = 0x01; junk[5] = 0x14; junk[6] = 0x02;
        junk[0x14] = 0xFF; junk[0x15] = 0xFF; // all flags, truncated data
        _ = LnkParser.TryParse(junk); // must not throw
    }
}

public class DeepQueryTests
{
    [Theory]
    [InlineData("contrat client", "\"contrat\" AND \"client\"")]
    [InlineData("\"cahier des charges\" lucens", "\"cahier des charges\" AND \"lucens\"")]
    [InlineData("facture OR devis", "(\"facture\" OR \"devis\")")]
    [InlineData("rapport -brouillon", "\"rapport\" NOT \"brouillon\"")]
    [InlineData("vidéo*", "\"vidéo\"*")]
    [InlineData("a\"b", "\"ab\"")]
    public void BuildsSafeFtsExpressions(string q, string expected) => Assert.Equal(expected, DeepSearch.Parse(q).FtsExpression);

    [Theory]
    [InlineData("Alice Code LUMEN", TermMatch.Prefix, "\"Alice\"* AND \"Code\"* AND \"LUMEN\"*")]
    [InlineData("le code", TermMatch.Prefix, "\"le\" AND \"code\"*")]                         // < 3 characters: no prefix (too broad)
    [InlineData("\"cahier des charges\" lucens", TermMatch.Prefix, "\"cahier des charges\" AND \"lucens\"*")] // phrases stay exact
    [InlineData("Alice Code LUMEN", TermMatch.Any, "(\"Alice\"* OR \"Code\"* OR \"LUMEN\"*)")]
    [InlineData("code facture -brouillon", TermMatch.Any, "(\"code\"* OR \"facture\"*) NOT \"brouillon\"")] // exclusion applies to the whole OR
    [InlineData("rapport", TermMatch.Any, "\"rapport\"*")]
    public void RelaxedExpressions(string q, TermMatch mode, string expected) => Assert.Equal(expected, DeepSearch.Parse(q, mode).FtsExpression);

    [Fact]
    public void SnippetsAreAccentInsensitiveAndHighlighted()
    {
        string text = string.Concat(Enumerable.Repeat("lorem ipsum dolor ", 50)) + "Le cahier des charges de la vidéosurveillance à Lucens est validé. " + string.Concat(Enumerable.Repeat("sit amet ", 50));
        var (snip, hl) = Snippets.Build(text, ["videosurveillance", "lucens"], 120);
        Assert.Contains("vidéosurveillance", snip);
        Assert.Contains(hl, h => snip.Substring(h.Start, h.Length).Equals("vidéosurveillance", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hl, h => snip.Substring(h.Start, h.Length) == "Lucens");
    }
}

public class SourceAccessTests
{
    [Fact]
    public void MetadataMatchesFileSystem()
    {
        string p = Path.Combine(Path.GetTempPath(), "seekbeam-md-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(p, "12345");
        try
        {
            Assert.True(Seekbeam.Core.SourceAccess.SourceFileAccess.TryGetMetadata(p, out var md));
            var fi = new FileInfo(p);
            Assert.Equal(5, md.Size);
            Assert.Equal(fi.LastWriteTimeUtc.ToFileTimeUtc(), md.LastWriteTimeUtc);
            Assert.Equal(fi.CreationTimeUtc.ToFileTimeUtc(), md.CreationTimeUtc);
            Assert.True(Seekbeam.Core.SourceAccess.SourceFileAccess.TryGetIdentity(p, out var id));
            Assert.NotEqual(0UL, id.FileIndex);
            Assert.Equal(1U, id.LinkCount);
            using var s = Seekbeam.Core.SourceAccess.SourceFileAccess.OpenRead(p);
            Assert.False(s.CanWrite);
            Assert.Throws<NotSupportedException>(() => s.WriteByte(1));
        }
        finally { File.Delete(p); }
    }
}

public class AppIndexTests
{
    [Fact]
    public void ScansStartMenuAndPackagedApps()
    {
        var cat = new AppCatalog();
        using var s = new AppScanner(cat);
        s.Configure(new AppsConfig());
        s.Refresh();
        Assert.True(cat.Count > 5, $"{cat.Count} apps");
        var packaged = AppsFolder.Enumerate();
        Assert.NotEmpty(packaged);
        Assert.Contains(packaged, a => a.AppUserModelId != null || a.LaunchPath.StartsWith("shell:AppsFolder"));
    }
}
