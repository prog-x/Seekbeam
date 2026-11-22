using System.Text;

namespace Seekbeam.Extraction.Tests;

public class PlainTextTests
{
    [Fact]
    public void Utf8WithoutBom()
    {
        var r = Fx.Extract("Café crème\nligne 2", "txt");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Equal("Café crème\nligne 2", r.Text);
        Assert.Equal("text", r.ExtractorId);
    }

    [Fact]
    public void Utf8WithBom()
    {
        var r = Fx.Extract("été", "md", new UTF8Encoding(true));
        Assert.Equal("été", r.Text);
    }

    [Fact]
    public void Utf16LeBom()
    {
        var r = Fx.Extract("Bonjour ☃ été", "log", new UnicodeEncoding(false, true));
        Assert.Equal("Bonjour ☃ été", r.Text);
    }

    [Fact]
    public void Utf16BeBom()
    {
        var r = Fx.Extract("Grüße", "txt", new UnicodeEncoding(true, true));
        Assert.Equal("Grüße", r.Text);
    }

    [Fact]
    public void Utf16LeWithoutBomIsDetected()
    {
        var r = Fx.Extract(Encoding.Unicode.GetBytes("plain ascii text in utf16 without any bom"), "txt");
        Assert.Equal("plain ascii text in utf16 without any bom", r.Text);
    }

    [Fact]
    public void Windows1252Fallback()
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes("caf"), 0xE9, (byte)' ', 0x80, (byte)' ', .. Encoding.ASCII.GetBytes("ok")];
        var r = Fx.Extract(bytes, "txt");
        Assert.Equal("café € ok", r.Text);
    }

    [Fact]
    public void Utf8MultiByteCharCutAtPrefixBoundaryStaysUtf8()
    {
        // 64 KB prefix boundary falls inside "é" (2 bytes): must still be detected as UTF-8.
        var sb = new StringBuilder();
        sb.Append('a', 64 * 1024 - 1).Append("é fin");
        var r = Fx.Extract(sb.ToString(), "txt");
        Assert.EndsWith("é fin", r.Text);
    }

    [Fact]
    public void BinaryContentIsUnsupported()
    {
        var bytes = new byte[4096];
        new Random(1).NextBytes(bytes);
        for (int i = 0; i < bytes.Length; i += 7) bytes[i] = 0;
        var r = Fx.Extract(bytes, "txt");
        Assert.Equal(ExtractionStatus.Unsupported, r.Status);
        Assert.Equal("", r.Text);
    }

    [Fact]
    public void EmptyFileIsEmpty() => Assert.Equal(ExtractionStatus.Empty, Fx.Extract([], "txt").Status);

    [Fact]
    public void StopsWhenSinkIsFull()
    {
        var text = string.Join(' ', Enumerable.Repeat("motlong", 200_000));
        var r = Fx.Registry.ExtractStream(new MemoryStream(Encoding.UTF8.GetBytes(text)), "txt", new ExtractionLimits { MaxChars = 1000 }, CancellationToken.None);
        Assert.True(r.Truncated);
        Assert.True(r.Text.Length <= 1000);
    }

    [Fact]
    public void CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Fx.Registry.ExtractStream(new MemoryStream(Encoding.UTF8.GetBytes("abc")), "txt", new ExtractionLimits(), cts.Token));
    }
}

public class MarkupTests
{
    [Fact]
    public void HtmlStripsScriptsStylesCommentsAndDecodesEntities()
    {
        const string html = """
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><title>Mon &amp; titre</title>
            <style>body { color: red; } .x:after { content: "<p>"; }</style>
            <script>var s = "</div>"; if (a < b) alert('caché');</script></head>
            <body><!-- commentaire secret --><h1>Caf&eacute; cr&egrave;me</h1><p>Neige &#x2603; et &#233;t&eacute;</p>
            <p>par<b>tie</b>l</p><div>bloc</div><SCRIPT type="x">ignored()</SCRIPT>fin</body></html>
            """;
        var r = Fx.Extract(html, "html");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Equal("Mon & titre", r.Metadata["title"]);
        Assert.Contains("Café crème", r.Text);
        Assert.Contains("Neige ☃ et été", r.Text);
        Assert.Contains("partiel", r.Text);
        Assert.Contains("bloc", r.Text);
        Assert.Contains("fin", r.Text);
        Assert.DoesNotContain("color", r.Text);
        Assert.DoesNotContain("alert", r.Text);
        Assert.DoesNotContain("ignored", r.Text);
        Assert.DoesNotContain("secret", r.Text);
        Assert.DoesNotContain("DOCTYPE", r.Text);
    }

    [Fact]
    public void XmlKeepsCDataAndSeparatesElements()
    {
        const string xml = """<?xml version="1.0"?><root><a>un</a><b>deux</b><c><![CDATA[x < y ]] z]]></c><!-- no --></root>""";
        var r = Fx.Extract(xml, "xml");
        Assert.Equal("un deux x < y ]] z", r.Text);
    }

    [Fact]
    public void XmlEncodingDeclarationIsHonoured()
    {
        var bytes = Encoding.Latin1.GetBytes("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><r>caf\u00e9</r>");
        var r = Fx.Extract(bytes, "xml");
        Assert.Equal("café", r.Text);
    }

    [Fact]
    public void DtdIsNeverProcessed()
    {
        const string xml = """<?xml version="1.0"?><!DOCTYPE r [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><r>&x; texte</r>""";
        var r = Fx.Extract(xml, "xml");
        Assert.Contains("texte", r.Text);
        Assert.DoesNotContain("fonts", r.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<p>ouvert <b>jamais fermé")]
    [InlineData("<<<>>> a < b > c")]
    [InlineData("<div class=\"non terminé>texte après")]
    [InlineData("<!-- commentaire jamais fermé")]
    [InlineData("<script>sans fin")]
    [InlineData("<![CDATA[ sans fin")]
    [InlineData("&amp &#xZZ; &unknown; &#99999999;")]
    public void MalformedMarkupNeverThrows(string html)
    {
        var r = Fx.Extract(html, "html");
        Assert.True(r.Status is ExtractionStatus.Ok or ExtractionStatus.Empty, r.Error);
    }

    [Fact]
    public void HugeMalformedInputIsLinear()
    {
        // Pathological input for backtracking parsers: many unterminated tags/quotes/comments.
        var sb = new StringBuilder();
        for (int i = 0; i < 200_000; i++) sb.Append("<a href=\"x <!-- <![CDATA[ ");
        var r = Fx.WithTimeout(() => Fx.Extract(sb.ToString(), "html"), 10);
        Assert.NotEqual(ExtractionStatus.Error, r.Status);
    }

    [Fact]
    public void EntityIsNotSplitAcrossBufferFlushes()
    {
        var sb = new StringBuilder("<p>");
        sb.Append('x', 16 * 1024 - 5).Append(" &eacute;t&eacute; ok</p>");
        var r = Fx.Extract(sb.ToString(), "htm");
        Assert.EndsWith("été ok", r.Text);
    }
}
