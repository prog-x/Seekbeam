using System.Text;

namespace Seekbeam.Extraction.Formats;

/// <summary>
/// Hand-written RTF tokenizer (single pass, linear): groups, control words/symbols, \'hh bytes in the \ansicpg code page,
/// \uN with \ucN fallback skipping, \binN raw data, and skipped destinations (font/colour tables, pictures, objects…).
/// </summary>
internal sealed class RtfExtractor : IContentExtractor
{
    public string Id => "rtf";
    public int Version => 1;
    public IReadOnlyCollection<string> Extensions { get; } = ["rtf"];

    public void Extract(Stream source, string extension, TextSink sink, ExtractionContext context, CancellationToken ct) =>
        new RtfParser(source, sink, ct).Run();
}

internal sealed class RtfParser(Stream source, TextSink sink, CancellationToken ct)
{
    private enum Dest : byte { Normal, Skip, Meta }

    private struct GroupState
    {
        public Dest Dest;
        public string? MetaKey;
        public int Uc;
    }

    private static readonly HashSet<string> SkipDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "themedata", "colorschememapping", "datastore",
        "xmlnstbl", "listtable", "listoverridetable", "rsidtbl", "generator", "latentstyles", "revtbl", "filetbl",
        "fldinst", "pntext", "pntxta", "pntxtb", "listtext", "nonshppict", "objdata", "bkmkstart", "bkmkend", "protusertbl",
        "mmathPr", "wgrffmtfilter", "passwordhash", "pgdsctbl", "xmlopen", "fchars", "lchars", "userprops",
    };

    private static readonly Dictionary<string, string> MetaDestinations = new(StringComparer.Ordinal)
    {
        ["title"] = "title", ["author"] = "author", ["subject"] = "subject", ["keywords"] = "keywords",
    };

    private const int MaxDepth = 512;
    private const int MaxMeta = 1000;

    private readonly GroupState[] _stack = new GroupState[MaxDepth];
    private int _depth;          // number of states pushed on _stack
    private int _overflowDepth;  // groups opened beyond MaxDepth (share the current state)
    private GroupState _cur = new() { Uc = 1 };
    private Encoding _encoding = TextDecoding.Windows1252;
    private int _pendingSkip;    // fallback characters still to skip after \uN

    private readonly byte[] _bytes = new byte[256];
    private int _byteCount;
    private readonly char[] _out = new char[8192];
    private int _outCount;
    private readonly Dictionary<string, StringBuilder> _meta = new(StringComparer.Ordinal);

    private readonly byte[] _buf = new byte[64 * 1024];
    private int _bufLen, _bufPos;
    private long _ops;

    private int Next()
    {
        if (_bufPos >= _bufLen)
        {
            _bufLen = source.Read(_buf, 0, _buf.Length);
            _bufPos = 0;
            if (_bufLen <= 0) { _bufLen = 0; return -1; }
        }
        return _buf[_bufPos++];
    }

    private int Peek()
    {
        int c = Next();
        if (c >= 0) _bufPos--;
        return c;
    }

    public void Run()
    {
        int c;
        while ((c = Next()) >= 0)
        {
            if ((++_ops & 0x3FFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (sink.IsFull) break;
            }
            switch (c)
            {
                case '{':
                    FlushBytes();
                    _pendingSkip = 0;
                    if (_depth < MaxDepth) _stack[_depth++] = _cur; else _overflowDepth++;
                    break;
                case '}':
                    FlushBytes();
                    _pendingSkip = 0;
                    if (_overflowDepth > 0) _overflowDepth--;
                    else if (_depth > 0) _cur = _stack[--_depth]; // unbalanced '}' at depth 0 is ignored
                    break;
                case '\\':
                    ControlSequence();
                    break;
                case '\r':
                case '\n':
                    break;
                default:
                    if (_pendingSkip > 0) { _pendingSkip--; break; }
                    if (c < 0x80) { FlushBytes(); EmitChar((char)c); }
                    else AddByte((byte)c);
                    break;
            }
        }
        FlushBytes();
        FlushOut();
        foreach (var (k, v) in _meta) sink.SetMetadata(k, v.ToString());
    }

    private void ControlSequence()
    {
        int c = Next();
        if (c < 0) return;
        if (IsAsciiLetter(c))
        {
            Span<char> word = stackalloc char[32];
            int len = 0;
            while (c >= 0 && IsAsciiLetter(c)) { if (len < word.Length) word[len++] = (char)c; c = Next(); }
            bool hasParam = false, neg = false;
            long param = 0;
            if (c == '-') { neg = true; c = Next(); }
            int digits = 0;
            while (c >= '0' && c <= '9') { if (digits++ < 10) param = param * 10 + (c - '0'); hasParam = true; c = Next(); }
            if (neg) param = -param;
            if (c != ' ' && c >= 0) _bufPos--; // the delimiter (anything but a space) belongs to the next token
            ControlWord(new string(word[..len]), hasParam ? (int)Math.Clamp(param, int.MinValue, int.MaxValue) : (int?)null);
            return;
        }
        switch (c)
        {
            case '\'':
                int h1 = Hex(Next()), h2 = h1 < 0 ? -1 : Hex(Peek());
                if (h2 >= 0) Next();
                if (h1 < 0 || h2 < 0) return;
                if (_pendingSkip > 0) { _pendingSkip--; return; }
                AddByte((byte)(h1 * 16 + h2));
                return;
            case '*':
                _cur.Dest = Dest.Skip; // ignorable destination
                return;
            case '\\': case '{': case '}':
                if (_pendingSkip > 0) { _pendingSkip--; return; }
                FlushBytes(); EmitChar((char)c);
                return;
            case '~':
                FlushBytes(); EmitChar(' ');
                return;
            case '_':
                FlushBytes(); EmitChar('-');
                return;
            case '\r': case '\n':
                FlushBytes(); EmitBreak();
                return;
            default:
                return; // \- optional hyphen, \| \: etc.
        }
    }

    private void ControlWord(string word, int? param)
    {
        if (_pendingSkip > 0 && word != "u") { _pendingSkip--; if (word != "bin") return; }
        switch (word)
        {
            case "par": case "line": case "sect": case "page": case "row":
                FlushBytes(); EmitBreak(); return;
            case "tab": case "cell": case "emspace": case "enspace":
                FlushBytes(); EmitChar(' '); return;
            case "u":
                if (param is { } p)
                {
                    FlushBytes();
                    EmitChar((char)(p < 0 ? p + 65536 : p & 0xFFFF));
                    _pendingSkip = _cur.Uc;
                }
                return;
            case "uc":
                _cur.Uc = Math.Clamp(param ?? 1, 0, 16); return;
            case "ansicpg":
                if (param is { } cp) { FlushBytes(); _encoding = TextDecoding.FromCodePage(cp); }
                return;
            case "mac": FlushBytes(); _encoding = TextDecoding.FromCodePage(10000); return;
            case "pc": FlushBytes(); _encoding = TextDecoding.FromCodePage(437); return;
            case "pca": FlushBytes(); _encoding = TextDecoding.FromCodePage(850); return;
            case "bin":
                // \binN: N raw bytes follow; they are never text.
                FlushBytes();
                for (long n = Math.Max(0, param ?? 0); n > 0; n--) if (Next() < 0) return;
                return;
            case "emdash": FlushBytes(); EmitChar('—'); return;
            case "endash": FlushBytes(); EmitChar('–'); return;
            case "bullet": FlushBytes(); EmitChar('•'); return;
            case "lquote": case "rquote": FlushBytes(); EmitChar('\''); return;
            case "ldblquote": case "rdblquote": FlushBytes(); EmitChar('"'); return;
        }
        if (MetaDestinations.TryGetValue(word, out var key)) { FlushBytes(); _cur.Dest = Dest.Meta; _cur.MetaKey = key; return; }
        if (SkipDestinations.Contains(word)) { FlushBytes(); _cur.Dest = Dest.Skip; }
    }

    private static bool IsAsciiLetter(int c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';
    private static int Hex(int c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    private void AddByte(byte b)
    {
        if (_byteCount == _bytes.Length) FlushBytes();
        _bytes[_byteCount++] = b;
    }

    /// <summary>Decodes accumulated 8-bit bytes together so double-byte code pages decode correctly.</summary>
    private void FlushBytes()
    {
        if (_byteCount == 0) return;
        Span<char> chars = stackalloc char[_bytes.Length];
        int n = _encoding.GetChars(_bytes.AsSpan(0, _byteCount), chars);
        _byteCount = 0;
        foreach (char ch in chars[..n]) EmitChar(ch);
    }

    private void EmitChar(char c)
    {
        switch (_cur.Dest)
        {
            case Dest.Normal:
                if (_outCount == _out.Length) FlushOut();
                _out[_outCount++] = c;
                break;
            case Dest.Meta when _cur.MetaKey != null:
                if (!_meta.TryGetValue(_cur.MetaKey, out var sb)) _meta[_cur.MetaKey] = sb = new StringBuilder();
                if (sb.Length < MaxMeta) sb.Append(c);
                break;
        }
    }

    private void EmitBreak()
    {
        if (_cur.Dest != Dest.Normal) return;
        FlushOut();
        sink.Break();
    }

    private void FlushOut()
    {
        if (_outCount == 0) return;
        sink.Append(_out.AsSpan(0, _outCount));
        _outCount = 0;
    }
}
