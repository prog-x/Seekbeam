using Seekbeam.Extraction.Formats;

namespace Seekbeam.Extraction;

/// <summary>The built-in, in-process extractors. All of them only read the stream they are given (FILE_IMMUTABILITY_POLICY).</summary>
public static class BuiltInExtractors
{
    public static IEnumerable<IContentExtractor> All() =>
    [
        new PlainTextExtractor(),
        new MarkupExtractor(),
        new OoxmlExtractor(),
        new OdfExtractor(),
        new RtfExtractor(),
        new PdfExtractor(),
        new LegacyOfficeExtractor(),
        new ZipExtractor(),
    ];
}
