using Seekbeam.Core.Apps;
using Seekbeam.Core.Search;

namespace Seekbeam.Core.Tests;

public class SearchTests : IDisposable
{
    private readonly TestEnv _env = new();
    private readonly long _root, _projets, _alpha;

    public SearchTests()
    {
        _root = _env.AddRoot(@"C:\Users\Test\Documents");
        _projets = _env.Add(_root, "Projets", dir: true);
        _alpha = _env.Add(_projets, "Alpha", dir: true);
        _env.Add(_alpha, "notes.txt");
        _env.Add(_alpha, "budget_prévisionnel.xlsx");
        _env.Add(_root, "20261121_cahier_des_charges_videosurveillance_datacenter_Anthropic_Lucens.docx");
        _env.Add(_root, "cahier.txt");
        _env.Add(_root, "cahier des charges.pdf");
        _env.Add(_root, "Été 2024 photos", dir: true);
        _env.Add(_root, "rapport_final_v2.docx");
        _env.Add(_root, "rapport.docx");
        _env.Add(_root, "mon_rapport_annuel.pdf");
        _env.Add(_root, "Thumbs.db", attrs: (long)(FileAttributes.Hidden | FileAttributes.System));
        _env.AddMany(_root, Enumerable.Range(0, 200).Select(i => $"filler_{i}_document.txt"));
    }

    public void Dispose() => _env.Dispose();

    [Fact]
    public void SpecExample_TokensInAnyOrderPartialAndCaseInsensitive()
    {
        var top = _env.Names("Ant 202611 cahier").First();
        Assert.Equal("20261121_cahier_des_charges_videosurveillance_datacenter_Anthropic_Lucens.docx", top);
        Assert.Equal(top, _env.Names("cahier ant 202611").First());
        Assert.Equal(top, _env.Names("LUCENS surveillance").First());
    }

    [Fact]
    public void AccentInsensitiveBothWays()
    {
        Assert.Contains("Été 2024 photos", _env.Names("ete 2024"));
        Assert.Contains("budget_prévisionnel.xlsx", _env.Names("previsionnel"));
        Assert.Contains("budget_prévisionnel.xlsx", _env.Names("PRÉVISIONNEL"));
    }

    [Fact]
    public void PathTokensMatchAncestorDirectories()
    {
        var names = _env.Names("alpha notes");
        Assert.Equal("notes.txt", names.First());
        Assert.Contains("notes.txt", _env.Names("projets notes"));
        Assert.Contains("notes.txt", _env.Names("notes in:projets"));
        Assert.DoesNotContain("cahier.txt", _env.Names("cahier in:projets"));
    }

    [Fact]
    public void ExactNameRanksFirst()
    {
        Assert.Equal("rapport.docx", _env.Names("rapport").First());
        Assert.Equal("cahier.txt", _env.Names("cahier").First());
        Assert.Equal("cahier des charges.pdf", _env.Names("cahier des charges").First());
    }

    [Fact]
    public void FiltersAndExclusions()
    {
        Assert.All(_env.Names("rapport ext:pdf"), n => Assert.EndsWith(".pdf", n));
        Assert.DoesNotContain("rapport_final_v2.docx", _env.Names("rapport -final"));
        Assert.All(_env.Search("e kind:folder").Results, r => Assert.Equal(ResultKind.Folder, r.Kind));
    }

    [Fact]
    public void HiddenSystemFilesExcludedByDefault()
    {
        Assert.Empty(_env.Names("thumbs"));
        _env.SearchConfig.IncludeHidden = true;
        _env.SearchConfig.IncludeSystem = true;
        Assert.Contains("Thumbs.db", _env.Names("thumbs"));
    }

    [Fact]
    public void TypoToleranceFindsCloseNames()
    {
        var res = _env.Search("videosurvellance");
        Assert.True(res.UsedFuzzy);
        Assert.Contains(res.Results, r => r.Name.Contains("videosurveillance") && r.Fuzzy);
        Assert.Contains("rapport.docx", _env.Names("raport"));
    }

    [Fact]
    public void FullPathAndHighlightsAreReturned()
    {
        var r = _env.Search("budget prev").Results.First();
        Assert.Equal(@"C:\Users\Test\Documents\Projets\Alpha\budget_prévisionnel.xlsx", r.FullPath);
        Assert.Equal(2, r.Highlights.Count);
        Assert.Equal("budget", r.Name.Substring(r.Highlights[0].Start, r.Highlights[0].Length));
        Assert.Equal("prév", r.Name.Substring(r.Highlights[1].Start, r.Highlights[1].Length));
    }

    [Fact]
    public void AppsRankAboveFilesAndUsageNeverBeatsExactMatch()
    {
        _env.Apps.Replace([AppEntry.Create("Rapport Manager", @"C:\apps\rm.lnk", AppSource.StartMenu, @"C:\apps\rm.exe")]);
        Assert.Equal("Rapport Manager", _env.Names("rapport man").First());
        // Heavy usage of a partial match must not outrank the exact name.
        var partial = _env.Search("rapport").Results.First(r => r.Name == "mon_rapport_annuel.pdf");
        for (int i = 0; i < 200; i++) _env.Usage.RecordLaunch(partial.FullPath, partial.EntryId);
        Assert.Equal("rapport.docx", _env.Names("rapport").Where(n => n != "Rapport Manager").First());
        // But usage does reorder comparable matches.
        var top = _env.Names("rapport").Where(n => n is "mon_rapport_annuel.pdf" or "rapport_final_v2.docx").First();
        Assert.Equal("mon_rapport_annuel.pdf", top);
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _env.Engine.Search(new SearchRequest { Query = "document" }, cts.Token));
    }
}
