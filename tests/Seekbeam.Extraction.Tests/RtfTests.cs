using System.Text;

namespace Seekbeam.Extraction.Tests;

public class RtfTests
{
    private static ExtractionResult Rtf(string s) => Fx.Extract(Encoding.Latin1.GetBytes(s), "rtf");

    [Fact]
    public void HexAndUnicodeEscapesWithSkippedDestinations()
    {
        const string rtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Helvetica;}{\f1 Times;}}{\colortbl;\red0\green0\blue0;}" +
                           @"{\*\generator Riched20 10.0;}{\stylesheet{\s0 Normal;}}\viewkind4\uc1\pard\f0\fs20 caf\'e9 \u233?t\u233?\par}";
        var r = Rtf(rtf);
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Equal("café été", r.Text);
    }

    [Fact]
    public void InfoTitleBecomesMetadataAndIsNotText()
    {
        var r = Rtf(@"{\rtf1{\info{\title Mon titre}{\author Alice}{\*\company ACME}}Corps\par}");
        Assert.Equal("Mon titre", r.Metadata["title"]);
        Assert.Equal("Alice", r.Metadata["author"]);
        Assert.Equal("Corps", r.Text);
    }

    [Fact]
    public void ParagraphsTabsCellsAndSymbols()
    {
        var r = Rtf(@"{\rtf1 un\par deux\tab trois\cell quatre\row cinq\~six \{x\} a\\b\line fin}");
        Assert.Equal("un\ndeux trois quatre\ncinq six {x} a\\b\nfin", r.Text);
    }

    [Fact]
    public void UcSkipCountAndNegativeUnicode()
    {
        // \uc2 → two fallback chars skipped; \u-10179\u-8704 is the surrogate pair of U+1F600 written as signed 16-bit.
        var r = Rtf(@"{\rtf1\uc2 a\u233xyb {\uc0\u-10179\u-8704} c}");
        Assert.Equal("aéb \U0001F600 c", r.Text);
    }

    [Fact]
    public void BinaryDataAndPicturesAreSkipped()
    {
        var r = Rtf(@"{\rtf1 avant {\pict\pngblip 89504e470d0a}{\object\objemb{\*\objdata 0102}} \bin5 {}}\ milieu{\*\shppict x} après}");
        Assert.Equal("avant milieu après", r.Text);
    }

    [Fact]
    public void FieldInstructionsDroppedResultKept()
    {
        var r = Rtf(@"{\rtf1 voir {\field{\*\fldinst HYPERLINK ""http://x""}{\fldrslt lien}} ici}");
        Assert.Equal("voir lien ici", r.Text);
    }

    [Fact]
    public void AnsiCodePageIsHonoured()
    {
        // Code page 1251 (Cyrillic): 0xEF 0xF0 0xE8 → "при".
        var r = Rtf(@"{\rtf1\ansi\ansicpg1251 \'ef\'f0\'e8}");
        Assert.Equal("при", r.Text);
    }

    [Theory]
    [InlineData(@"{\rtf1 }}}} texte {{{")]
    [InlineData(@"\rtf1 sans accolades \'zz \u \'")]
    [InlineData(@"{\rtf1\bin999999 trop court}")]
    [InlineData(@"{\rtf1 \abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz123456789012345 x}")]
    [InlineData("")]
    public void MalformedRtfNeverThrows(string rtf)
    {
        var r = Rtf(rtf);
        Assert.True(r.Status is ExtractionStatus.Ok or ExtractionStatus.Empty, r.Error);
    }

    [Fact]
    public void DeepNestingIsBounded()
    {
        var s = "{\\rtf1 " + new string('{', 100_000) + "profond" + new string('}', 100_000) + " fin}";
        var r = Fx.WithTimeout(() => Rtf(s));
        Assert.Equal("profond fin", r.Text);
    }
}
