using System.Text;

namespace Seekbeam.Extraction.Tests;

public class OoxmlTests
{
    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
    private const string A = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    private const string X = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";

    private const string Core = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/">
        <dc:title>Mon titre</dc:title><dc:creator>Alice</dc:creator><cp:keywords>k1 k2</cp:keywords><dc:subject>Sujet</dc:subject></cp:coreProperties>
        """;

    internal static byte[] Docx(string body, params (string, object)[] extra) => Fx.Zip(
        [
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("docProps/core.xml", Core),
            ("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document {W}><w:body>{body}</w:body></w:document>"),
            .. extra,
        ]);

    [Fact]
    public void WordTextBreaksHeadersAndMetadata()
    {
        var docx = Docx(
            "<w:p><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/></w:tabs></w:pPr><w:r><w:t>Bonjour</w:t></w:r><w:r><w:tab/><w:t xml:space=\"preserve\"> le </w:t><w:t>mon</w:t></w:r><w:r><w:t>de</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t>Deuxième</w:t><w:br/><w:t>ligne</w:t></w:r><w:del><w:r><w:delText>supprimé</w:delText></w:r></w:del>" +
            "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>HYPERLINK x</w:instrText></w:r></w:p>",
            ("word/header1.xml", $"<w:hdr {W}><w:p><w:r><w:t>EnTete</w:t></w:r></w:p></w:hdr>"),
            ("word/footnotes.xml", $"<w:footnotes {W}><w:footnote><w:p><w:r><w:t>Note bas</w:t></w:r></w:p></w:footnote></w:footnotes>"));
        var r = Fx.Extract(docx, "docx");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Contains("Bonjour le monde", r.Text);
        Assert.Contains("Deuxième\nligne", r.Text);
        Assert.Contains("EnTete", r.Text);
        Assert.Contains("Note bas", r.Text);
        Assert.DoesNotContain("supprimé", r.Text);
        Assert.DoesNotContain("HYPERLINK", r.Text);
        Assert.Equal("Mon titre", r.Metadata["title"]);
        Assert.Equal("Alice", r.Metadata["author"]);
        Assert.Equal("k1 k2", r.Metadata["keywords"]);
        Assert.Equal("Sujet", r.Metadata["subject"]);
    }

    [Fact]
    public void ExcelSharedInlineAndNumericCells()
    {
        var xlsx = Fx.Zip(
            ("xl/workbook.xml", $"<workbook {X}><sheets><sheet name=\"Ventes\" sheetId=\"1\"/><sheet name=\"Stock\" sheetId=\"2\"/></sheets></workbook>"),
            ("xl/sharedStrings.xml", $"<sst {X} count=\"3\" uniqueCount=\"3\"><si><t>Produit</t></si><si><r><t>Pri</t></r><r><t>x</t></r><rPh><t>phonetique</t></rPh></si><si><t/></si></sst>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet {X}><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c t=\"s\"><v>99</v></c></row>" +
                                         "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>EnLigne</t></is></c><c r=\"B2\"><v>12.5</v></c><c r=\"C2\"><f>SUM(B2)</f><v>13</v></c></row></sheetData></worksheet>"),
            ("xl/worksheets/sheet2.xml", $"<worksheet {X}><sheetData><row><c t=\"str\"><v>texte formule</v></c></row></sheetData></worksheet>"));
        var r = Fx.Extract(xlsx, "xlsx");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Contains("Ventes Stock", r.Text);
        Assert.Contains("Produit Prix", r.Text);
        Assert.Contains("EnLigne 12.5 13", r.Text);
        Assert.Contains("texte formule", r.Text);
        Assert.DoesNotContain("phonetique", r.Text);
        Assert.DoesNotContain("SUM", r.Text);
        Assert.Equal("Ventes, Stock", r.Metadata["sheets"]);
    }

    [Fact]
    public void PowerPointSlidesInNumericOrderAndNotes()
    {
        static string Slide(string t) => $"<p:sld {A}><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{t}</a:t></a:r><a:br/><a:r><a:t>suite</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>";
        var pptx = Fx.Zip(
            ("ppt/presentation.xml", $"<p:presentation {A}/>"),
            ("ppt/slides/slide10.xml", Slide("Dixieme")),
            ("ppt/slides/slide2.xml", Slide("Deuxieme")),
            ("ppt/slides/slide1.xml", Slide("Premiere")),
            ("ppt/slides/_rels/slide1.xml.rels", "<Relationships/>"),
            ("ppt/notesSlides/notesSlide1.xml", $"<p:notes {A}><a:p><a:r><a:t>Notes orateur</a:t></a:r></a:p></p:notes>"));
        var r = Fx.Extract(pptx, "pptx");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        int i1 = r.Text.IndexOf("Premiere"), i2 = r.Text.IndexOf("Deuxieme"), i10 = r.Text.IndexOf("Dixieme");
        Assert.True(i1 >= 0 && i1 < i2 && i2 < i10, r.Text);
        Assert.Contains("Premiere\nsuite", r.Text);
        Assert.Contains("Notes orateur", r.Text);
    }

    [Fact]
    public void HighCompressionRatioPartIsTooLarge()
    {
        var huge = new StringBuilder($"<w:document {W}><w:body><w:p><w:r><w:t>");
        huge.Append('a', 3 * 1024 * 1024).Append("</w:t></w:r></w:p></w:body></w:document>");
        var docx = Fx.Zip(("word/document.xml", huge.ToString()));
        var r = Fx.Extract(docx, "docx");
        Assert.Equal(ExtractionStatus.TooLarge, r.Status);
    }

    [Fact]
    public void DeclaredSizeAboveLimitIsTooLarge()
    {
        var docx = Docx("<w:p><w:r><w:t>" + new string('b', 50_000) + "</w:t></w:r></w:p>");
        var r = Fx.Extract(docx, "docx", new ExtractionLimits { MaxXmlPartBytes = 10_000 });
        Assert.Equal(ExtractionStatus.TooLarge, r.Status);
    }

    [Fact]
    public void LyingDeclaredSizeIsCaughtByRealByteCount()
    {
        var docx = Docx("<w:p><w:r><w:t>" + new string('c', 200_000) + "</w:t></w:r></w:p>");
        Fx.PatchZip(docx, "word/document.xml", uncompressedSize: 100);
        var r = Fx.Extract(docx, "docx", new ExtractionLimits { MaxXmlPartBytes = 10_000 });
        Assert.True(r.Status is ExtractionStatus.TooLarge or ExtractionStatus.Malformed, r.Status + " " + r.Error);
        Assert.True(r.Text.Length < 20_000);
    }

    [Fact]
    public void XxeAndEntityExpansionAreRejected()
    {
        const string xxe = "<?xml version=\"1.0\"?><!DOCTYPE w:document [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\"><!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">]>" +
                           $"<w:document {W}><w:body><w:p><w:r><w:t>&xxe;&lol2;</w:t></w:r></w:p></w:body></w:document>";
        var docx = Fx.Zip(("word/document.xml", xxe));
        var r = Fx.Extract(docx, "docx");
        Assert.Equal(ExtractionStatus.Malformed, r.Status);
        Assert.DoesNotContain("fonts", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lol", r.Text);
    }

    [Fact]
    public void GarbageIsMalformed()
    {
        var r = Fx.Extract(Encoding.ASCII.GetBytes("this is not a zip file at all"), "docx");
        Assert.Equal(ExtractionStatus.Malformed, r.Status);
    }

    [Fact]
    public void EncryptedOfficeFileIsClassified()
    {
        var cfb = CfbWriter.Build([("EncryptionInfo", new byte[200]), ("EncryptedPackage", new byte[5000])]);
        Assert.Equal(ExtractionStatus.Encrypted, Fx.Extract(cfb, "docx").Status);
        var plain = CfbWriter.Build([("Other", new byte[10])]);
        Assert.Equal(ExtractionStatus.Malformed, Fx.Extract(plain, "xlsx").Status);
    }
}

public class OdfTests
{
    private const string Ns = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
                              "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
                              "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" " +
                              "xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\"";

    private static byte[] Odf(string body, string? manifest = null) => Fx.Zip(
        ("mimetype", "application/vnd.oasis.opendocument.text"),
        ("META-INF/manifest.xml", manifest ?? $"<manifest:manifest {Ns}><manifest:file-entry manifest:full-path=\"content.xml\"/></manifest:manifest>"),
        ("meta.xml", $"<office:document-meta {Ns}><office:meta><dc:title>Titre ODF</dc:title><meta:initial-creator>Bob</meta:initial-creator><meta:keyword>a</meta:keyword><meta:keyword>b</meta:keyword></office:meta></office:document-meta>"),
        ("content.xml", $"<office:document-content {Ns}><office:automatic-styles><style>NePasIndexer</style></office:automatic-styles><office:body>{body}</office:body></office:document-content>"));

    [Fact]
    public void TextDocument()
    {
        var odt = Odf("<office:text><text:h>Titre</text:h><text:p>Un<text:s/>deux<text:tab/>trois<text:span>quatre</text:span>cinq</text:p><text:p>ligne<text:line-break/>suivante</text:p></office:text>");
        var r = Fx.Extract(odt, "odt");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Contains("Titre\nUn deux troisquatrecinq\nligne\nsuivante", r.Text);
        Assert.DoesNotContain("NePasIndexer", r.Text);
        Assert.Equal("Titre ODF", r.Metadata["title"]);
        Assert.Equal("Bob", r.Metadata["author"]);
        Assert.Equal("a, b", r.Metadata["keywords"]);
    }

    [Fact]
    public void Spreadsheet()
    {
        var ods = Odf("<office:spreadsheet><table:table><table:table-row><table:table-cell><text:p>A1</text:p></table:table-cell><table:table-cell><text:p>B1</text:p></table:table-cell></table:table-row>" +
                      "<table:table-row><table:table-cell><text:p>A2</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet>");
        var r = Fx.Extract(ods, "ods");
        Assert.Contains("A1", r.Text);
        Assert.Contains("B1", r.Text);
        Assert.Contains("A2", r.Text);
        Assert.DoesNotContain("A1B1", r.Text);
    }

    [Fact]
    public void Presentation()
    {
        var odp = Odf("<office:presentation><draw:page><draw:frame><draw:text-box><text:p>Diapo un</text:p></draw:text-box></draw:frame></draw:page><draw:page><draw:frame><draw:text-box><text:p>Diapo deux</text:p></draw:text-box></draw:frame></draw:page></office:presentation>");
        var r = Fx.Extract(odp, "odp");
        Assert.Contains("Diapo un", r.Text);
        Assert.Contains("Diapo deux", r.Text);
    }

    [Fact]
    public void EncryptedOdfIsClassified()
    {
        var odt = Odf("<office:text/>", $"<manifest:manifest {Ns}><manifest:file-entry manifest:full-path=\"content.xml\"><manifest:encryption-data manifest:checksum=\"x\"/></manifest:file-entry></manifest:manifest>");
        Assert.Equal(ExtractionStatus.Encrypted, Fx.Extract(odt, "odt").Status);
    }

    [Fact]
    public void RatioBombInContentIsTooLarge()
    {
        var sb = new StringBuilder($"<office:document-content {Ns}><office:body><office:text><text:p>");
        sb.Append(' ', 4 * 1024 * 1024).Append("</text:p></office:text></office:body></office:document-content>");
        var odt = Fx.Zip(("content.xml", sb.ToString()));
        Assert.Equal(ExtractionStatus.TooLarge, Fx.Extract(odt, "odt").Status);
    }
}
