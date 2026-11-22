using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Seekbeam.Extraction.Tests;

public class PdfTests
{
    internal static byte[] BuildPdf(params string[] pages)
    {
        var b = new PdfDocumentBuilder();
        b.DocumentInformation.Title = "Titre PDF";
        b.DocumentInformation.Author = "Carole";
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = b.AddPage(PageSize.A4);
            page.AddText(text, 12, new PdfPoint(25, 700), font);
        }
        return b.Build();
    }

    [Fact]
    public void ExtractsTextAndMetadata()
    {
        var r = Fx.Extract(BuildPdf("Bonjour Seekbeam 2026", "Seconde page"), "pdf");
        Assert.Equal(ExtractionStatus.Ok, r.Status);
        Assert.Contains("Bonjour Seekbeam 2026", r.Text);
        Assert.Contains("Seconde page", r.Text);
        Assert.Equal("Titre PDF", r.Metadata["title"]);
        Assert.Equal("Carole", r.Metadata["author"]);
        Assert.Equal("2", r.Metadata["pages"]);
    }

    [Fact]
    public void PageLimitIsHonoured()
    {
        var r = Fx.Extract(BuildPdf("page un", "page deux", "page trois"), "pdf", new ExtractionLimits { MaxPdfPages = 1 });
        Assert.Contains("page un", r.Text);
        Assert.DoesNotContain("page deux", r.Text);
        Assert.Equal("2", r.Metadata["truncatedPages"]);
    }

    [Fact]
    public void TruncatedPdfIsHandled()
    {
        var pdf = BuildPdf("Bonjour Seekbeam 2026");
        var truncated = pdf.AsSpan(0, pdf.Length / 2).ToArray();
        var r = Fx.WithTimeout(() => Fx.Extract(truncated, "pdf"));
        // Lenient parsing may recover part of it; it must never throw nor report a crash.
        Assert.True(r.Status is ExtractionStatus.Malformed or ExtractionStatus.Ok or ExtractionStatus.Empty, r.Status + " " + r.Error);
    }

    [Fact]
    public void GarbageIsMalformed()
    {
        var garbage = new byte[10_000];
        new Random(42).NextBytes(garbage);
        var r = Fx.WithTimeout(() => Fx.Extract(garbage, "pdf"));
        Assert.Equal(ExtractionStatus.Malformed, r.Status);
    }

    [Fact]
    public void HeaderOnlyIsMalformed()
    {
        var r = Fx.WithTimeout(() => Fx.Extract("%PDF-1.7\n%%EOF"u8.ToArray(), "pdf"));
        Assert.Equal(ExtractionStatus.Malformed, r.Status);
    }
}
