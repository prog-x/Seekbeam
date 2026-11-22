using System.Buffers.Binary;
using System.Text;

namespace Seekbeam.Extraction.Formats;

/// <summary>
/// Legacy binary Office files (Word 97-2003, Excel BIFF8, PowerPoint 97-2003) read from a Compound File. The format
/// is chosen from the streams present, so a misnamed file still works.
/// </summary>
internal sealed class LegacyOfficeExtractor : IContentExtractor
{
    public string Id => "legacy-office";
    public int Version => 1;
    public IReadOnlyCollection<string> Extensions { get; } = ["doc", "dot", "xls", "xlt", "ppt", "pot", "pps"];

    public void Extract(Stream source, string extension, TextSink sink, ExtractionContext context, CancellationToken ct)
    {
        if (!source.CanSeek) source = BoundedReadStream.ToMemory(source, 0, context.Limits.MaxFileBytes, extension, ct);
        if (!CfbReader.HasSignature(source, source.Position)) throw new ExtractionException(ExtractionStatus.Malformed, "not a compound file");
        var cfb = new CfbReader(source, context.Limits.MaxFileBytes);

        if (cfb.HasStream("EncryptionInfo") || cfb.HasStream("EncryptedSummary"))
            throw new ExtractionException(ExtractionStatus.Encrypted, "encrypted document");

        ReadSummaryInformation(cfb, sink);

        if (cfb.HasStream("WordDocument")) DocReader.Extract(cfb, sink, ct);
        else if (cfb.HasStream("Workbook")) XlsReader.Extract(cfb.ReadStream("Workbook")!, sink, context.Limits.MaxChars, ct);
        else if (cfb.HasStream("Book")) throw new ExtractionException(ExtractionStatus.Unsupported, "BIFF5 workbook (Excel 95)");
        else if (cfb.HasStream("PowerPoint Document")) PptReader.Extract(cfb.ReadStream("PowerPoint Document")!, sink, ct);
        else throw new ExtractionException(ExtractionStatus.Unsupported, "unknown compound document");
    }

    /// <summary>
    /// "\u0005SummaryInformation" property set (MS-OLEPS): title (2), subject (3), author (4), keywords (5).
    /// Best effort: any inconsistency simply yields no metadata.
    /// </summary>
    private static void ReadSummaryInformation(CfbReader cfb, TextSink sink)
    {
        byte[]? d;
        try { d = cfb.ReadStream("\u0005SummaryInformation"); }
        catch (Exception e) when (e is InvalidDataException or ExtractionException) { return; }
        if (d == null || d.Length < 48) return;
        var span = d.AsSpan();
        // PropertySetStream: ByteOrder(2) Version(2) SystemIdentifier(4) CLSID(16) NumPropertySets(4) then FMTID(16) + Offset(4).
        uint secOff = BinaryPrimitives.ReadUInt32LittleEndian(span[44..]);
        if (secOff > d.Length - 8) return;
        var sec = span[(int)secOff..];
        uint secSize = BinaryPrimitives.ReadUInt32LittleEndian(sec);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(sec[4..]);
        if (secSize > sec.Length) secSize = (uint)sec.Length;
        sec = sec[..(int)secSize];
        if (count > 1000 || 8 + count * 8 > sec.Length) return;

        int codePage = 1252;
        var props = new List<(uint id, uint off)>();
        for (int i = 0; i < count; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(sec[(8 + i * 8)..]);
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(sec[(12 + i * 8)..]);
            if (off > sec.Length - 8) continue;
            if (id == 1 && BinaryPrimitives.ReadUInt16LittleEndian(sec[(int)off..]) == 0x0002) // VT_I2 code page
                codePage = BinaryPrimitives.ReadUInt16LittleEndian(sec[((int)off + 4)..]);
            props.Add((id, off));
        }
        foreach (var (id, off) in props)
        {
            string? key = id switch { 2 => "title", 3 => "subject", 4 => "author", 5 => "keywords", _ => null };
            if (key == null) continue;
            var p = sec[(int)off..];
            ushort vt = BinaryPrimitives.ReadUInt16LittleEndian(p);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
            if (len > p.Length - 8 || len > 4096) continue;
            string value = vt switch
            {
                0x001E when codePage == 1200 => Encoding.Unicode.GetString(p.Slice(8, (int)len & ~1)), // VT_LPSTR stored as UTF-16
                0x001E => TextDecoding.FromCodePage(codePage).GetString(p.Slice(8, (int)len)),
                0x001F => len * 2 <= p.Length - 8 ? Encoding.Unicode.GetString(p.Slice(8, (int)len * 2)) : "", // VT_LPWSTR (len in chars)
                _ => "",
            };
            sink.SetMetadata(key, value.TrimEnd('\0'));
        }
    }
}

/// <summary>Word 97-2003 (MS-DOC): FIB → CLX piece table → text pieces (8-bit cp1252 or UTF-16LE).</summary>
internal static class DocReader
{
    public static void Extract(CfbReader cfb, TextSink sink, CancellationToken ct)
    {
        var wd = cfb.ReadStream("WordDocument")!;
        if (wd.Length < 0x22) throw new InvalidDataException("FIB too short");
        var w = wd.AsSpan();
        // FibBase: wIdent (0x00) = 0xA5EC, nFib (0x02), flags (0x0A): fEncrypted = 0x0100, fWhichTblStm = 0x0200.
        if (BinaryPrimitives.ReadUInt16LittleEndian(w) != 0xA5EC) throw new InvalidDataException("bad FIB signature");
        ushort nFib = BinaryPrimitives.ReadUInt16LittleEndian(w[2..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(w[0x0A..]);
        if ((flags & 0x0100) != 0) throw new ExtractionException(ExtractionStatus.Encrypted, "encrypted Word document");
        if (nFib < 0x00C0) throw new ExtractionException(ExtractionStatus.Unsupported, "pre-Word 97 document");
        string tableName = (flags & 0x0200) != 0 ? "1Table" : "0Table";

        // FibBase (32) | csw (2) | FibRgW (csw*2) | cslw (2) | FibRgLw (cslw*4) | cbRgFcLcb (2) | FibRgFcLcb (cbRgFcLcb*8).
        int p = 32;
        int csw = BinaryPrimitives.ReadUInt16LittleEndian(w[p..]); p += 2 + csw * 2;
        if (p + 2 > w.Length) throw new InvalidDataException("FIB truncated");
        int cslw = BinaryPrimitives.ReadUInt16LittleEndian(w[p..]); p += 2;
        int rgLw = p;
        p += cslw * 4;
        if (p + 2 > w.Length) throw new InvalidDataException("FIB truncated");
        int cbRgFcLcb = BinaryPrimitives.ReadUInt16LittleEndian(w[p..]); p += 2;
        int rgFcLcb = p;
        int ccpText = cslw > 3 && rgLw + 16 <= w.Length ? BinaryPrimitives.ReadInt32LittleEndian(w[(rgLw + 12)..]) : 0;

        uint fcClx = 0, lcbClx = 0;
        if (cbRgFcLcb > 33 && rgFcLcb + 34 * 8 <= w.Length)
        {
            fcClx = BinaryPrimitives.ReadUInt32LittleEndian(w[(rgFcLcb + 33 * 8)..]);
            lcbClx = BinaryPrimitives.ReadUInt32LittleEndian(w[(rgFcLcb + 33 * 8 + 4)..]);
        }

        var writer = new DocTextWriter(sink);
        byte[]? table = lcbClx > 0 ? cfb.ReadStream(tableName) : null;
        if (table == null || lcbClx == 0 || fcClx > table.Length || lcbClx > table.Length - fcClx)
        {
            // Best effort without a piece table: assume one 8-bit piece of ccpText chars at the usual offset 0x800.
            int n = (int)Math.Clamp(ccpText, 0, Math.Max(0, w.Length - 0x800));
            if (n > 0) writer.Write(TextDecoding.Windows1252.GetString(w.Slice(0x800, n)));
            writer.Flush();
            return;
        }

        var clx = table.AsSpan((int)fcClx, (int)lcbClx);
        int q = 0;
        // Clx = Prc* (0x01, cbGrpprl:int16, grpprl) followed by Pcdt (0x02, lcb:uint32, PlcPcd).
        while (q < clx.Length && clx[q] == 0x01)
        {
            if (q + 3 > clx.Length) throw new InvalidDataException("CLX truncated");
            int cb = BinaryPrimitives.ReadInt16LittleEndian(clx[(q + 1)..]);
            if (cb < 0) throw new InvalidDataException("bad Prc");
            q += 3 + cb;
        }
        if (q + 5 > clx.Length || clx[q] != 0x02) throw new InvalidDataException("Pcdt not found");
        uint lcb = BinaryPrimitives.ReadUInt32LittleEndian(clx[(q + 1)..]);
        if (lcb > clx.Length - q - 5 || lcb < 16) throw new InvalidDataException("bad PlcPcd size");
        var plc = clx.Slice(q + 5, (int)lcb);
        // PlcPcd: (n + 1) CPs (4 bytes each) followed by n PCDs (8 bytes each) → n = (lcb - 4) / 12.
        int pieces = (plc.Length - 4) / 12;
        int pcdBase = (pieces + 1) * 4;
        for (int i = 0; i < pieces; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (sink.IsFull) break;
            int cpStart = BinaryPrimitives.ReadInt32LittleEndian(plc[(i * 4)..]);
            int cpEnd = BinaryPrimitives.ReadInt32LittleEndian(plc[((i + 1) * 4)..]);
            int chars = cpEnd - cpStart;
            if (chars <= 0) continue;
            // PCD: 2 bytes flags, FcCompressed (4 bytes: fc in bits 0-29, fCompressed = bit 30), 2 bytes prm.
            uint fcRaw = BinaryPrimitives.ReadUInt32LittleEndian(plc[(pcdBase + i * 8 + 2)..]);
            bool compressed = (fcRaw & 0x40000000) != 0;
            long fc = fcRaw & 0x3FFFFFFF;
            if (compressed)
            {
                long off = fc / 2;
                if (off >= w.Length) continue;
                int n = (int)Math.Min(chars, w.Length - off);
                writer.Write(TextDecoding.Windows1252.GetString(w.Slice((int)off, n)));
            }
            else
            {
                if (fc >= w.Length) continue;
                int n = (int)Math.Min((long)chars * 2, (w.Length - fc) & ~1L);
                writer.Write(Encoding.Unicode.GetString(w.Slice((int)fc, n)));
            }
        }
        writer.Flush();
    }

    /// <summary>Maps Word special characters: paragraph/cell/page marks → break, field codes (0x13…0x14) dropped, results kept.</summary>
    private sealed class DocTextWriter(TextSink sink)
    {
        private readonly StringBuilder _sb = new();
        private readonly Stack<bool> _fields = new(); // true while inside the field code part
        private int _inCode;

        public void Write(string s)
        {
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\x13':
                        if (_fields.Count < 64) { _fields.Push(true); _inCode++; }
                        break;
                    case '\x14':
                        if (_fields.Count > 0 && _fields.Peek()) { _fields.Pop(); _fields.Push(false); _inCode--; }
                        break;
                    case '\x15':
                        if (_fields.Count > 0 && _fields.Pop()) _inCode--;
                        break;
                    case '\r': case '\x07': case '\x0B': case '\x0C':
                        if (_inCode == 0) { Flush(); sink.Break(); }
                        break;
                    case '\t': case '\x1E':
                        if (_inCode == 0) _sb.Append(c == '\t' ? ' ' : '-');
                        break;
                    default:
                        if (_inCode == 0 && c >= ' ') _sb.Append(c);
                        break;
                }
                if (_sb.Length >= 8192) Flush();
            }
        }

        public void Flush()
        {
            if (_sb.Length == 0) return;
            sink.Append(_sb.ToString());
            _sb.Clear();
        }
    }
}

/// <summary>Excel 97-2003 BIFF8 workbook stream: SST (with CONTINUE handling), LABELSST, LABEL, NUMBER, RK, MULRK, formulas' cached values.</summary>
internal static class XlsReader
{
    private const ushort BOF = 0x0809, FILEPASS = 0x002F, BOUNDSHEET = 0x0085, SST = 0x00FC, CONTINUE = 0x003C,
        LABELSST = 0x00FD, LABEL = 0x0204, NUMBER = 0x0203, RK = 0x027E, MULRK = 0x00BD, FORMULA = 0x0006, STRING = 0x0207;

    public static void Extract(byte[] data, TextSink sink, int maxChars, CancellationToken ct)
    {
        var sst = new List<string>();
        var sheets = new List<string>();
        int pos = 0, lastRow = -1, n = 0;
        while (pos + 4 <= data.Length)
        {
            if ((++n & 0x3FF) == 0) { ct.ThrowIfCancellationRequested(); if (sink.IsFull) return; }
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
            int body = pos + 4;
            if (body + len > data.Length) break; // truncated record
            var r = data.AsSpan(body, len);
            switch (type)
            {
                case FILEPASS:
                    throw new ExtractionException(ExtractionStatus.Encrypted, "encrypted workbook");
                case BOF:
                    // dt = 0x0010: worksheet substream begins.
                    if (len >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(r[2..]) == 0x0010) { sink.Break(); lastRow = -1; }
                    break;
                case BOUNDSHEET when len >= 8:
                    // lbPlyPos (4), hsState (1), dt (1), ShortXLUnicodeString: cch (1), fHighByte (1), chars.
                    var name = ReadShortString(r[6..]);
                    if (name.Length > 0 && sheets.Count < 10_000)
                    {
                        sheets.Add(name);
                        sink.Append(name + " ");
                    }
                    break;
                case SST:
                    ReadSst(data, pos, sst, maxChars, ct);
                    sink.Break();
                    break;
                case LABELSST when len >= 10:
                {
                    Row(BinaryPrimitives.ReadUInt16LittleEndian(r), ref lastRow, sink);
                    int idx = BinaryPrimitives.ReadInt32LittleEndian(r[6..]);
                    if (idx >= 0 && idx < sst.Count) Cell(sink, sst[idx]);
                    break;
                }
                case LABEL when len >= 9:
                {
                    Row(BinaryPrimitives.ReadUInt16LittleEndian(r), ref lastRow, sink);
                    Cell(sink, ReadXLUnicodeString(r[6..]));
                    break;
                }
                case NUMBER when len >= 14:
                    Row(BinaryPrimitives.ReadUInt16LittleEndian(r), ref lastRow, sink);
                    Cell(sink, Num(BinaryPrimitives.ReadDoubleLittleEndian(r[6..])));
                    break;
                case RK when len >= 10:
                    Row(BinaryPrimitives.ReadUInt16LittleEndian(r), ref lastRow, sink);
                    Cell(sink, Num(DecodeRk(BinaryPrimitives.ReadUInt32LittleEndian(r[6..]))));
                    break;
                case MULRK when len >= 6:
                    // rw (2), colFirst (2), RkRec[] (ixfe 2 + RK 4), colLast (2).
                    Row(BinaryPrimitives.ReadUInt16LittleEndian(r), ref lastRow, sink);
                    for (int o = 4; o + 6 <= len - 2; o += 6) Cell(sink, Num(DecodeRk(BinaryPrimitives.ReadUInt32LittleEndian(r[(o + 2)..]))));
                    break;
                case FORMULA when len >= 20:
                    Row(BinaryPrimitives.ReadUInt16LittleEndian(r), ref lastRow, sink);
                    // FormulaValue (8 bytes at 6): a number unless the last two bytes are 0xFFFF (string/bool/error/blank).
                    if (BinaryPrimitives.ReadUInt16LittleEndian(r[12..]) != 0xFFFF) Cell(sink, Num(BinaryPrimitives.ReadDoubleLittleEndian(r[6..])));
                    break;
                case STRING when len >= 3:
                    Cell(sink, ReadXLUnicodeString(r));
                    break;
            }
            pos = body + len;
        }
        if (sheets.Count > 0) sink.SetMetadata("sheets", string.Join(", ", sheets.Take(100)));
    }

    private static void Row(int row, ref int lastRow, TextSink sink)
    {
        if (lastRow >= 0 && row != lastRow) sink.Break();
        lastRow = row;
    }

    private static void Cell(TextSink sink, string? s)
    {
        if (string.IsNullOrEmpty(s)) return;
        sink.Append(s);
        sink.Append(" ");
    }

    private static string Num(double d) =>
        double.IsFinite(d) ? d.ToString("G15", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>RK: bit 0 = value/100, bit 1 = 30-bit signed integer, else the high 30 bits of an IEEE double.</summary>
    internal static double DecodeRk(uint rk)
    {
        double v = (rk & 2) != 0 ? (int)rk >> 2 : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
        return (rk & 1) != 0 ? v / 100 : v;
    }

    private static string ReadShortString(ReadOnlySpan<byte> b)
    {
        if (b.Length < 2) return "";
        int cch = b[0];
        bool high = (b[1] & 1) != 0;
        var chars = b[2..];
        return high
            ? Encoding.Unicode.GetString(chars[..Math.Min(cch * 2, chars.Length & ~1)])
            : Encoding.Latin1.GetString(chars[..Math.Min(cch, chars.Length)]);
    }

    /// <summary>XLUnicodeString inside a single record: cch (2), flags (1), chars (rich/ext data ignored).</summary>
    private static string ReadXLUnicodeString(ReadOnlySpan<byte> b)
    {
        if (b.Length < 3) return "";
        int cch = BinaryPrimitives.ReadUInt16LittleEndian(b);
        byte f = b[2];
        int o = 3;
        if ((f & 0x08) != 0) o += 2; // cRun
        if ((f & 0x04) != 0) o += 4; // cbExtRst
        if (o > b.Length) return "";
        var chars = b[o..];
        return (f & 1) != 0
            ? Encoding.Unicode.GetString(chars[..Math.Min(cch * 2, chars.Length & ~1)])
            : Encoding.Latin1.GetString(chars[..Math.Min(cch, chars.Length)]);
    }

    /// <summary>
    /// SST + CONTINUE records. A string's characters may continue in the next CONTINUE record, which then starts with
    /// a new 1-byte option flag (fHighByte) — the classic trap. Rich-text runs and ExtRst blocks are skipped across boundaries.
    /// </summary>
    private static void ReadSst(byte[] data, int sstPos, List<string> sst, int maxChars, CancellationToken ct)
    {
        var segs = new List<(int start, int end)>();
        int pos = sstPos;
        bool first = true;
        while (pos + 4 <= data.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
            if ((first ? type != SST : type != CONTINUE) || pos + 4 + len > data.Length) break;
            segs.Add((pos + 4, pos + 4 + len));
            pos += 4 + len;
            first = false;
        }
        var rd = new SegmentReader(data, segs);
        if (!rd.TrySkip(4) || !rd.TryReadInt32(out int unique)) return; // cstTotal, cstUnique
        long total = 0;
        var sb = new StringBuilder();
        for (int i = 0; i < unique; i++)
        {
            if ((i & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            if (!rd.TryReadUInt16(out int cch) || !rd.TryReadByte(out byte flags)) return;
            int cRun = 0, cbExt = 0;
            if ((flags & 0x08) != 0 && !rd.TryReadUInt16(out cRun)) return;
            if ((flags & 0x04) != 0 && !rd.TryReadInt32(out cbExt)) return;
            sb.Clear();
            bool high = (flags & 1) != 0;
            int remaining = cch;
            while (remaining > 0)
            {
                if (rd.AtSegmentEnd)
                {
                    // Characters continue in the next CONTINUE record, prefixed by a fresh option byte.
                    if (!rd.NextSegment() || !rd.TryReadByte(out byte f2)) { sst.Add(sb.ToString()); return; }
                    high = (f2 & 1) != 0;
                }
                int took = rd.ReadChars(remaining, high, sb, total < maxChars);
                if (took == 0) { sst.Add(sb.ToString()); return; }
                remaining -= took;
            }
            if (!rd.TrySkip(cRun * 4L + Math.Max(0, cbExt))) { sst.Add(sb.ToString()); return; }
            total += sb.Length;
            sst.Add(sb.ToString());
        }
    }

    /// <summary>Sequential reader over the data of an SST record and its CONTINUE records.</summary>
    private sealed class SegmentReader(byte[] data, List<(int start, int end)> segs)
    {
        private int _seg, _pos = segs.Count > 0 ? segs[0].start : 0;

        public bool AtSegmentEnd => _seg >= segs.Count || _pos >= segs[_seg].end;

        public bool NextSegment()
        {
            if (++_seg >= segs.Count) return false;
            _pos = segs[_seg].start;
            return true;
        }

        private bool Ensure()
        {
            while (_seg < segs.Count && _pos >= segs[_seg].end)
                if (!NextSegment()) return false;
            return _seg < segs.Count;
        }

        public bool TryReadByte(out byte b)
        {
            b = 0;
            if (!Ensure()) return false;
            b = data[_pos++];
            return true;
        }

        public bool TryReadUInt16(out int v)
        {
            v = 0;
            if (!TryReadByte(out byte a) || !TryReadByte(out byte b)) return false;
            v = a | (b << 8);
            return true;
        }

        public bool TryReadInt32(out int v)
        {
            v = 0;
            if (!TryReadUInt16(out int lo) || !TryReadUInt16(out int hi)) return false;
            v = lo | (hi << 16);
            return true;
        }

        /// <summary>Skips bytes across segment boundaries (no option byte is re-read for non-character data).</summary>
        public bool TrySkip(long n)
        {
            while (n > 0)
            {
                if (!Ensure()) return false;
                int step = (int)Math.Min(n, segs[_seg].end - _pos);
                _pos += step;
                n -= step;
            }
            return true;
        }

        /// <summary>Reads up to <paramref name="max"/> characters from the current segment only.</summary>
        public int ReadChars(int max, bool high, StringBuilder sb, bool keep)
        {
            if (_seg >= segs.Count) return 0;
            int avail = segs[_seg].end - _pos;
            int width = high ? 2 : 1;
            int n = Math.Min(max, avail / width);
            if (n == 0) { _pos = segs[_seg].end; return 0; }
            if (keep)
            {
                var span = data.AsSpan(_pos, n * width);
                sb.Append(high ? Encoding.Unicode.GetString(span) : Encoding.Latin1.GetString(span));
            }
            _pos += n * width;
            return n;
        }
    }
}

/// <summary>PowerPoint 97-2003 "PowerPoint Document" stream: record tree walk collecting TextCharsAtom / TextBytesAtom.</summary>
internal static class PptReader
{
    private const ushort TextCharsAtom = 0x0FA0, TextBytesAtom = 0x0FA8;
    private const int MaxDepth = 32;

    public static void Extract(byte[] data, TextSink sink, CancellationToken ct)
    {
        // Explicit stack of (end offset) for containers: recVer == 0xF marks a container whose body is a record list.
        var ends = new Stack<int>();
        int pos = 0, end = data.Length, n = 0;
        while (true)
        {
            if (pos + 8 > end)
            {
                if (ends.Count == 0) break;
                pos = end;
                end = ends.Pop();
                continue;
            }
            if ((++n & 0x3FF) == 0) { ct.ThrowIfCancellationRequested(); if (sink.IsFull) return; }
            ushort verInst = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2));
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            int body = pos + 8;
            if (len > end - body) len = (uint)(end - body); // clamp to the enclosing record
            int next = body + (int)len;
            if ((verInst & 0x000F) == 0x000F && ends.Count < MaxDepth)
            {
                ends.Push(end);
                end = next;
                pos = body;
                continue;
            }
            if (type == TextCharsAtom)
            {
                sink.Append(Encoding.Unicode.GetString(data, body, (int)len & ~1));
                sink.Break();
            }
            else if (type == TextBytesAtom)
            {
                sink.Append(Encoding.Latin1.GetString(data, body, (int)len));
                sink.Break();
            }
            pos = next;
        }
    }
}
