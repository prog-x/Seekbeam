using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Seekbeam.Extraction.Formats;

/// <summary>PDF text through PdfPig (lenient parsing), page by page, in content-stream order.</summary>
internal sealed class PdfExtractor : IContentExtractor
{
    public string Id => "pdf";
    public int Version => 1;
    public IReadOnlyCollection<string> Extensions { get; } = ["pdf"];

    public void Extract(Stream source, string extension, TextSink sink, ExtractionContext context, CancellationToken ct)
    {
        // PdfPig needs random access; top-level and archive streams are seekable, but stay safe for other callers.
        Stream input = source.CanSeek
            ? source
            : BoundedReadStream.ToMemory(source, 0, context.Limits.MaxFileBytes, "pdf", ct);

        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(input, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException e) { throw new ExtractionException(ExtractionStatus.Encrypted, e.Message); }
        catch (Exception e) when (e is not OperationCanceledException and not ExtractionException and not OutOfMemoryException)
        {
            throw new ExtractionException(ExtractionStatus.Malformed, e.GetType().Name + ": " + e.Message);
        }

        using (doc)
        {
            try
            {
                var info = doc.Information;
                sink.SetMetadata("title", info.Title);
                sink.SetMetadata("author", info.Author);
                sink.SetMetadata("subject", info.Subject);
                sink.SetMetadata("keywords", info.Keywords);
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { /* metadata is optional */ }

            int pages;
            try { pages = doc.NumberOfPages; }
            catch (PdfDocumentEncryptedException e) { throw new ExtractionException(ExtractionStatus.Encrypted, e.Message); }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            {
                throw new ExtractionException(ExtractionStatus.Malformed, e.GetType().Name + ": " + e.Message);
            }
            sink.SetMetadata("pages", pages.ToString(System.Globalization.CultureInfo.InvariantCulture));

            int max = Math.Min(pages, context.Limits.MaxPdfPages), failed = 0;
            string? firstError = null;
            for (int i = 1; i <= max; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (sink.IsFull) break;
                try
                {
                    var page = doc.GetPage(i);
                    sink.Append(ContentOrderTextExtractor.GetText(page));
                    sink.Break();
                }
                catch (PdfDocumentEncryptedException e) { throw new ExtractionException(ExtractionStatus.Encrypted, e.Message); }
                catch (Exception e) when (e is not OperationCanceledException and not ExtractionException and not OutOfMemoryException)
                {
                    // One broken page must not lose the rest of the document.
                    failed++;
                    firstError ??= e.GetType().Name + ": " + e.Message;
                }
            }
            if (max > 0 && failed == max) throw new ExtractionException(ExtractionStatus.Malformed, firstError ?? "no readable page");
            if (pages > max) sink.SetMetadata("truncatedPages", (pages - max).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
