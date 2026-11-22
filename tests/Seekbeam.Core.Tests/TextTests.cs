using System.Text;
using Seekbeam.Core.Search;
using Seekbeam.Core.Text;

namespace Seekbeam.Core.Tests;

public class NormalizerTests
{
    [Theory]
    [InlineData("Élève", "eleve")]
    [InlineData("ÇA VA", "ca va")]
    [InlineData("Straße", "strasse")]
    [InlineData("Œuvre_Ægis", "oeuvre_aegis")]
    [InlineData("Zürich-Genève", "zurich-geneve")]
    [InlineData("Łódź", "lodz")]
    [InlineData("ABC123", "abc123")]
    [InlineData("Ṽiệt Nam", "viet nam")]
    public void FoldsCaseAndDiacritics(string input, string expected) =>
        Assert.Equal(expected, Normalizer.NormalizeToString(input));

    [Fact]
    public void DecomposedInputMatchesComposed()
    {
        string nfd = "été"; // "été" in NFD
        Assert.Equal("ete", Normalizer.NormalizeToString(nfd));
    }

    [Fact]
    public void NonLatinScriptsAreLowercasedAndKept()
    {
        Assert.Equal("привет", Normalizer.NormalizeToString("ПРИВЕТ"));
        Assert.Equal("文件", Normalizer.NormalizeToString("文件"));
        Assert.Equal("😀x", Normalizer.NormalizeToString("😀X"));
    }

    [Fact]
    public void WordStartDetection()
    {
        var s = Encoding.UTF8.GetBytes("20261121_cahier des charges v2");
        Assert.True(Normalizer.IsWordStart(s, 0));
        Assert.True(Normalizer.IsWordStart(s, 9));   // after '_'
        Assert.False(Normalizer.IsWordStart(s, 10));
        Assert.True(Normalizer.IsWordStart(s, 29));  // digit after letter 'v'
    }
}

public class QueryParserTests
{
    [Fact]
    public void ParsesTokensOperatorsAndFilters()
    {
        var q = QueryParser.Parse("Ant  \"cahier des\" -brouillon ext:pdf,docx kind:file in:Projets 202611");
        Assert.Equal(["ant", "cahier des", "202611"], q.Tokens.Select(Encoding.UTF8.GetString));
        Assert.Equal(["brouillon"], q.Excludes.Select(Encoding.UTF8.GetString));
        Assert.Equal(["pdf", "docx"], q.Extensions.Select(Encoding.UTF8.GetString));
        Assert.Equal(["projets"], q.PathFilters.Select(Encoding.UTF8.GetString));
        Assert.Equal(KindFilter.File, q.Kind);
        Assert.Equal("ant cahier des 202611", Encoding.UTF8.GetString(q.Joined));
    }

    [Fact]
    public void EmptyAndWhitespaceQueriesAreEmpty()
    {
        Assert.True(QueryParser.Parse("").IsEmpty);
        Assert.True(QueryParser.Parse("   ").IsEmpty);
    }

    [Fact]
    public void DuplicateTokensAreRemoved() => Assert.Single(QueryParser.Parse("abc ABC Abc").Tokens);
}

public class BitapTests
{
    private static int M(string pat, string text, int k) => new BitapPattern(Encoding.UTF8.GetBytes(pat), k).Match(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("cahier", "le_cahier_final", 1, 0)]
    [InlineData("cahier", "le_cahir_final", 1, 1)]       // deletion in text
    [InlineData("cahier", "le_cahiier_final", 1, 1)]     // insertion
    [InlineData("cahier", "le_cahter_final", 1, 1)]      // substitution
    [InlineData("cahier", "le_cxhtxr_final", 1, -1)]
    [InlineData("videosurveillance", "videosurvellance_2026", 2, 1)]
    [InlineData("videosurveillance", "vidosurveilance", 2, 2)]
    [InlineData("rapprot", "rapport_2025", 1, 1)]          // adjacent transposition
    [InlineData("facutre", "facture.pdf", 1, 1)]
    [InlineData("ab", "ba", 1, 1)]
    public void ApproximateMatch(string pat, string text, int k, int expected) => Assert.Equal(expected, M(pat, text, k));
}
