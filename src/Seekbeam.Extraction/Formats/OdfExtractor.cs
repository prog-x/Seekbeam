using System.Xml;

namespace Seekbeam.Extraction.Formats;

/// <summary>OpenDocument (text, spreadsheet, presentation, drawing): content.xml + meta.xml.</summary>
internal sealed class OdfExtractor : IContentExtractor
{
    public string Id => "odf";
    public int Version => 1;
    public IReadOnlyCollection<string> Extensions { get; } = ["odt", "ods", "odp", "odg", "ott", "ots", "otp", "otg"];

    private static readonly Dictionary<string, string> MetaMap = new(StringComparer.Ordinal)
    {
        ["title"] = "title", ["creator"] = "author", ["initial-creator"] = "author", ["keyword"] = "keywords", ["subject"] = "subject",
    };

    public void Extract(Stream source, string extension, TextSink sink, ExtractionContext context, CancellationToken ct)
    {
        using var pkg = ZipPackage.Open(source, context.Limits);

        // Password-protected documents keep a plain manifest that declares encryption-data for the encrypted parts.
        if (pkg.OpenPart("META-INF/manifest.xml") is { } manifest)
            using (manifest)
            using (var r = SafeXml.Create(manifest, 4 * 1024 * 1024))
                while (r.Read())
                    if (r.NodeType == XmlNodeType.Element && r.LocalName == "encryption-data")
                        throw new ExtractionException(ExtractionStatus.Encrypted, "encrypted OpenDocument");

        if (pkg.OpenXml("meta.xml") is { } meta)
            using (meta) SafeXml.ReadMetadata(meta, MetaMap, sink, ct);

        using var content = pkg.OpenXml("content.xml") ?? throw new ExtractionException(ExtractionStatus.Malformed, "content.xml missing");
        SafeXml.Walk(content, new ContentHandler(sink), sink, ct);
    }

    /// <summary>Text inside office:body; paragraphs, headings and table rows end with a break; text:s / text:tab / cells → space.</summary>
    private sealed class ContentHandler(TextSink sink) : IXmlTextHandler
    {
        private int _inBody;
        public void Start(string n, XmlReader r)
        {
            switch (n)
            {
                case "body": if (!r.IsEmptyElement) _inBody++; break;
                case "s": case "tab": sink.Append(" "); break;
                case "line-break": sink.Break(); break;
            }
        }
        public void End(string n)
        {
            switch (n)
            {
                case "body": if (_inBody > 0) _inBody--; break;
                case "p": case "h": case "table-row": case "list-item": case "page": sink.Break(); break;
                case "table-cell": case "frame": sink.Append(" "); break;
            }
        }
        public void Text(string v) { if (_inBody > 0) sink.Append(v); }
    }
}
