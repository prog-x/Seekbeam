using System.IO.Compression;
using System.Xml;

namespace Seekbeam.Extraction.Formats;

/// <summary>Callbacks for <see cref="SafeXml.Walk"/>.</summary>
internal interface IXmlTextHandler
{
    /// <summary>Element start; the reader is positioned on the element (attributes may be read).</summary>
    void Start(string localName, XmlReader reader);
    void End(string localName);
    void Text(string value);
}

/// <summary>Hardened XML reading: no DTD, no resolver, bounded document size.</summary>
internal static class SafeXml
{
    public static XmlReaderSettings Settings(long maxChars) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit, // DTDs (and therefore entity expansion / XXE) are rejected with an XmlException
        XmlResolver = null,
        // 0 would mean "unlimited": with DTDs prohibited no entity can be declared anyway, keep a tiny explicit cap.
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = Math.Max(0, maxChars),
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
        CheckCharacters = false,
    };

    public static XmlReader Create(Stream s, long maxChars) => XmlReader.Create(s, Settings(maxChars));

    /// <summary>Streams the document through <paramref name="handler"/> until the end or until the sink is full.</summary>
    public static void Walk(XmlReader r, IXmlTextHandler handler, TextSink sink, CancellationToken ct)
    {
        int n = 0;
        while (r.Read())
        {
            if ((++n & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            switch (r.NodeType)
            {
                case XmlNodeType.Element:
                    string name = r.LocalName;
                    bool empty = r.IsEmptyElement;
                    handler.Start(name, r);
                    r.MoveToElement();
                    if (empty) handler.End(name);
                    break;
                case XmlNodeType.EndElement:
                    handler.End(r.LocalName);
                    if (sink.IsFull) return;
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    handler.Text(r.Value);
                    break;
            }
        }
    }

    /// <summary>Reads simple metadata elements (e.g. Dublin Core) mapping local names to metadata keys; keywords are joined.</summary>
    public static void ReadMetadata(XmlReader r, IReadOnlyDictionary<string, string> map, TextSink sink, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int n = 0;
        while (!r.EOF)
        {
            if ((++n & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            if (r.NodeType == XmlNodeType.Element && map.TryGetValue(r.LocalName, out var key) && !r.IsEmptyElement)
            {
                string v = r.ReadElementContentAsString().Trim();
                if (v.Length > 1000) v = v[..1000];
                if (v.Length == 0) continue;
                if (!values.TryGetValue(key, out var existing)) values[key] = v;
                else if (key == "keywords" && existing.Length < 2000) values[key] = existing + ", " + v;
                continue; // ReadElementContentAsString already advanced the reader
            }
            r.Read();
        }
        foreach (var (k, v) in values) if (!sink.Metadata.ContainsKey(k)) sink.SetMetadata(k, v);
    }
}

/// <summary>
/// Read-only view of a ZIP-based package (OOXML / ODF) with zip-bomb guards. Parts are never written anywhere:
/// they are decompressed through a <see cref="BoundedReadStream"/> directly into the XML reader.
/// </summary>
internal sealed class ZipPackage : IDisposable
{
    /// <summary>Ratio checks only apply above this size: tiny highly compressible parts are harmless.</summary>
    public const long RatioCheckThreshold = 1024 * 1024;

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ExtractionLimits _limits;

    private ZipPackage(ZipArchive zip, ExtractionLimits limits)
    {
        _zip = zip;
        _limits = limits;
        int count = 0;
        foreach (var e in zip.Entries)
        {
            if (++count > limits.MaxArchiveEntries) throw new ExtractionException(ExtractionStatus.TooLarge, "too many package parts");
            _entries.TryAdd(e.FullName.Replace('\\', '/'), e);
        }
    }

    /// <exception cref="InvalidDataException">The stream is not a ZIP archive.</exception>
    public static ZipPackage Open(Stream s, ExtractionLimits limits) => new(new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true), limits);

    public IEnumerable<string> Names => _entries.Keys;
    public bool Contains(string name) => _entries.ContainsKey(name);

    /// <summary>Opens a part through the size / ratio guards; null when absent.</summary>
    public Stream? OpenPart(string name)
    {
        if (!_entries.TryGetValue(name, out var e)) return null;
        CheckEntry(e, _limits.MaxXmlPartBytes, _limits.MaxCompressionRatio, name);
        return new BoundedReadStream(e.Open(), _limits.MaxXmlPartBytes, name);
    }

    public XmlReader? OpenXml(string name) => OpenPart(name) is { } s ? SafeXml.Create(s, _limits.MaxXmlPartBytes) : null;

    internal static void CheckEntry(ZipArchiveEntry e, long maxBytes, int maxRatio, string name)
    {
        if (e.Length > maxBytes) throw new ExtractionException(ExtractionStatus.TooLarge, $"{name}: declared size {e.Length} exceeds limit");
        if (e.Length > RatioCheckThreshold && e.Length > (long)maxRatio * Math.Max(1, e.CompressedLength))
            throw new ExtractionException(ExtractionStatus.TooLarge, $"{name}: compression ratio exceeds {maxRatio}");
    }

    /// <summary>Sorts part names by their trailing number (slide2 before slide10).</summary>
    public static int NumericSuffix(string name)
    {
        int end = name.LastIndexOf('.');
        if (end < 0) end = name.Length;
        int start = end;
        while (start > 0 && char.IsAsciiDigit(name[start - 1])) start--;
        return start < end && end - start <= 9 ? int.Parse(name.AsSpan(start, end - start)) : int.MaxValue;
    }

    public void Dispose() => _zip.Dispose();
}
