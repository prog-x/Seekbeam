using System.Xml;

namespace Seekbeam.Extraction.Formats;

/// <summary>Office Open XML (Word, Excel, PowerPoint) through ZipArchive + hardened XmlReader, part by part.</summary>
internal sealed class OoxmlExtractor : IContentExtractor
{
    public string Id => "ooxml";
    public int Version => 1;
    public IReadOnlyCollection<string> Extensions { get; } = ["docx", "docm", "dotx", "dotm", "xlsx", "xlsm", "xltx", "xltm", "pptx", "pptm", "potx", "potm", "ppsx", "ppsm"];

    private static readonly Dictionary<string, string> CoreMap = new(StringComparer.Ordinal)
    {
        ["title"] = "title", ["creator"] = "author", ["keywords"] = "keywords", ["subject"] = "subject",
    };

    public void Extract(Stream source, string extension, TextSink sink, ExtractionContext context, CancellationToken ct)
    {
        using var pkg = OpenPackage(source, context.Limits);
        if (pkg.OpenXml("docProps/core.xml") is { } core)
            using (core) SafeXml.ReadMetadata(core, CoreMap, sink, ct);

        if (pkg.Contains("word/document.xml")) ExtractWord(pkg, sink, ct);
        else if (pkg.Contains("xl/workbook.xml")) ExtractExcel(pkg, sink, context.Limits, ct);
        else if (pkg.Contains("ppt/presentation.xml") || extension.StartsWith("pp", StringComparison.OrdinalIgnoreCase)
                 || extension.StartsWith("pot", StringComparison.OrdinalIgnoreCase)) ExtractPowerPoint(pkg, sink, ct);
        else throw new ExtractionException(ExtractionStatus.Unsupported, "unknown OOXML package");
    }

    internal static ZipPackage OpenPackage(Stream source, ExtractionLimits limits)
    {
        long start = source.CanSeek ? source.Position : 0;
        try { return ZipPackage.Open(source, limits); }
        catch (InvalidDataException)
        {
            // Password-protected Office files are not ZIPs but CFB containers holding EncryptionInfo + EncryptedPackage.
            if (source.CanSeek && CfbReader.HasSignature(source, start))
            {
                bool encrypted = false;
                try
                {
                    source.Position = start;
                    var cfb = new CfbReader(source, limits.MaxFileBytes);
                    encrypted = cfb.HasStream("EncryptionInfo") || cfb.HasStream("EncryptedPackage");
                }
                catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException) { }
                if (encrypted) throw new ExtractionException(ExtractionStatus.Encrypted, "encrypted Office document");
                throw new ExtractionException(ExtractionStatus.Malformed, "compound file with an OOXML extension");
            }
            throw;
        }
    }

    private static IEnumerable<string> Parts(ZipPackage pkg, string prefix) =>
        pkg.Names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                             && n.IndexOf('/', prefix.Length) < 0)
           .OrderBy(ZipPackage.NumericSuffix).ThenBy(n => n, StringComparer.OrdinalIgnoreCase);

    private static void WalkPart(ZipPackage pkg, string name, IXmlTextHandler handler, TextSink sink, CancellationToken ct)
    {
        using var r = pkg.OpenXml(name);
        if (r == null) return;
        SafeXml.Walk(r, handler, sink, ct);
        sink.Break();
    }

    // ---------------------------------------------------------------- Word

    private static void ExtractWord(ZipPackage pkg, TextSink sink, CancellationToken ct)
    {
        var h = new WordHandler(sink);
        WalkPart(pkg, "word/document.xml", h, sink, ct);
        foreach (var prefix in (string[])["word/header", "word/footer"])
            foreach (var part in pkg.Names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(ZipPackage.NumericSuffix).ToList())
            {
                if (sink.IsFull) return;
                ct.ThrowIfCancellationRequested();
                WalkPart(pkg, part, h, sink, ct);
            }
        foreach (var part in (string[])["word/footnotes.xml", "word/endnotes.xml", "word/comments.xml"])
        {
            if (sink.IsFull) return;
            WalkPart(pkg, part, h, sink, ct);
        }
    }

    /// <summary>w:t = text, w:tab = space, w:br/w:cr = break, end of w:p = break; deleted text (w:delText) is ignored.</summary>
    private sealed class WordHandler(TextSink sink) : IXmlTextHandler
    {
        private int _inText;
        public void Start(string n, XmlReader r)
        {
            switch (n)
            {
                case "t": if (!r.IsEmptyElement) _inText++; break;
                case "tab": sink.Append(" "); break;
                case "br": case "cr": sink.Break(); break;
            }
        }
        public void End(string n)
        {
            if (n == "t" && _inText > 0) _inText--;
            else if (n == "p") sink.Break();
        }
        public void Text(string v) { if (_inText > 0) sink.Append(v); }
    }

    // ---------------------------------------------------------------- Excel

    private static void ExtractExcel(ZipPackage pkg, TextSink sink, ExtractionLimits limits, CancellationToken ct)
    {
        // Sheet names (workbook order).
        var sheetNames = new List<string>();
        using (var wb = pkg.OpenXml("xl/workbook.xml"))
            if (wb != null)
                while (wb.Read())
                    if (wb.NodeType == XmlNodeType.Element && wb.LocalName == "sheet" && wb.GetAttribute("name") is { Length: > 0 } sn && sheetNames.Count < 10_000)
                        sheetNames.Add(sn);
        if (sheetNames.Count > 0)
        {
            sink.SetMetadata("sheets", string.Join(", ", sheetNames.Take(100)));
            foreach (var s in sheetNames) sink.Append(s + " ");
            sink.Break();
        }

        var shared = ReadSharedStrings(pkg, limits.MaxChars, ct);
        var h = new SheetHandler(sink, shared);
        foreach (var part in Parts(pkg, "xl/worksheets/").ToList())
        {
            if (sink.IsFull) return;
            ct.ThrowIfCancellationRequested();
            WalkPart(pkg, part, h, sink, ct);
        }
    }

    /// <summary>Shared strings table (si = one string, made of t runs; rPh phonetic runs are skipped). Bounded by total characters.</summary>
    private static List<string?> ReadSharedStrings(ZipPackage pkg, int maxChars, CancellationToken ct)
    {
        var list = new List<string?>();
        using var r = pkg.OpenXml("xl/sharedStrings.xml");
        if (r == null) return list;
        long total = 0;
        var cur = new System.Text.StringBuilder();
        int inT = 0, inPh = 0, n = 0;
        bool inSi = false;
        while (r.Read())
        {
            if ((++n & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
            switch (r.NodeType)
            {
                case XmlNodeType.Element:
                    if (r.LocalName == "si") { inSi = !r.IsEmptyElement; cur.Clear(); if (r.IsEmptyElement) list.Add(""); }
                    else if (r.LocalName == "rPh" && !r.IsEmptyElement) inPh++;
                    else if (r.LocalName == "t" && !r.IsEmptyElement) inT++;
                    break;
                case XmlNodeType.EndElement:
                    if (r.LocalName == "si" && inSi)
                    {
                        inSi = false;
                        total += cur.Length;
                        list.Add(cur.ToString());
                        if (total > maxChars) return list; // later indices resolve to nothing
                    }
                    else if (r.LocalName == "rPh" && inPh > 0) inPh--;
                    else if (r.LocalName == "t" && inT > 0) inT--;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                    if (inSi && inT > 0 && inPh == 0 && cur.Length < maxChars) cur.Append(r.Value);
                    break;
            }
        }
        return list;
    }

    /// <summary>Cells: t="s" → shared string index in v; t="inlineStr" → is/t; other types → raw v (numbers, booleans, str). Formulas (f) are skipped.</summary>
    private sealed class SheetHandler(TextSink sink, List<string?> shared) : IXmlTextHandler
    {
        private string? _type;
        private bool _inV, _inIsT;
        public void Start(string n, XmlReader r)
        {
            switch (n)
            {
                case "c": _type = r.GetAttribute("t"); break;
                case "v": _inV = !r.IsEmptyElement; break;
                case "t": _inIsT = !r.IsEmptyElement && _type == "inlineStr"; break;
            }
        }
        public void End(string n)
        {
            switch (n)
            {
                case "v": _inV = false; break;
                case "t": _inIsT = false; break;
                case "c": _type = null; sink.Append(" "); break;
                case "row": sink.Break(); break;
            }
        }
        public void Text(string v)
        {
            if (_inIsT) { sink.Append(v); return; }
            if (!_inV) return;
            if (_type == "s")
            {
                if (int.TryParse(v.AsSpan().Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int idx)
                    && idx >= 0 && idx < shared.Count)
                    sink.Append(shared[idx]);
            }
            else sink.Append(v);
        }
    }

    // ---------------------------------------------------------------- PowerPoint

    private static void ExtractPowerPoint(ZipPackage pkg, TextSink sink, CancellationToken ct)
    {
        var h = new DrawingTextHandler(sink);
        foreach (var part in Parts(pkg, "ppt/slides/").Concat(Parts(pkg, "ppt/notesSlides/")).ToList())
        {
            if (sink.IsFull) return;
            ct.ThrowIfCancellationRequested();
            WalkPart(pkg, part, h, sink, ct);
        }
    }

    /// <summary>DrawingML text: a:t = text, end of a:p and a:br = break.</summary>
    private sealed class DrawingTextHandler(TextSink sink) : IXmlTextHandler
    {
        private int _inText;
        public void Start(string n, XmlReader r)
        {
            if (n == "t" && !r.IsEmptyElement) _inText++;
            else if (n == "br") sink.Break();
        }
        public void End(string n)
        {
            if (n == "t" && _inText > 0) _inText--;
            else if (n == "p") sink.Break();
        }
        public void Text(string v) { if (_inText > 0) sink.Append(v); }
    }
}
