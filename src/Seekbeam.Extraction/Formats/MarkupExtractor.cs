using System.Net;
using System.Text;

namespace Seekbeam.Extraction.Formats;

/// <summary>HTML / XML family: a tolerant, linear-time tag stripper (no DOM, no XML parser, no DTD processing).</summary>
internal sealed class MarkupExtractor : IContentExtractor
{
    public string Id => "markup";
    public int Version => 1;

    public IReadOnlyCollection<string> Extensions { get; } =
        ["html", "htm", "xhtml", "xml", "svg", "rss", "atom", "plist", "xsd", "xsl", "xslt", "kml", "gpx", "resx", "nuspec", "config"];

    public void Extract(Stream source, string extension, TextSink sink, ExtractionContext context, CancellationToken ct)
    {
        var stripper = new MarkupStripper(sink, html: extension.TrimStart('.').ToLowerInvariant() is "html" or "htm" or "xhtml");
        TextDecoding.DecodeStream(source, honourDeclaration: true, stripper.Feed, () => sink.IsFull, ct);
        stripper.Finish();
    }
}

/// <summary>Character-level state machine; every input char is examined a constant number of times.</summary>
internal sealed class MarkupStripper(TextSink sink, bool html)
{
    private enum State { Text, TagStart, TagName, InTag, Bang, Comment, CData, Pi, Declaration, RawText }

    private static readonly HashSet<string> Inline = new(StringComparer.Ordinal)
    {
        "a", "abbr", "b", "bdi", "bdo", "cite", "code", "data", "dfn", "em", "i", "kbd", "mark", "q", "s", "samp", "small",
        "span", "strong", "sub", "sup", "time", "u", "var", "wbr", "font", "big", "tt", "strike", "ins", "del", "nobr",
    };

    private static readonly HashSet<string> Block = new(StringComparer.Ordinal)
    {
        "p", "div", "br", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "dl", "dt", "dd", "table", "thead",
        "tbody", "tfoot", "section", "article", "header", "footer", "nav", "aside", "main", "blockquote", "pre", "hr",
        "title", "option", "form", "fieldset", "legend", "figure", "figcaption", "address", "caption", "details", "summary",
        "body", "head", "html", "center", "item", "entry",
    };

    private const int MaxTextBuffer = 16 * 1024;
    private const int MaxQuotedRun = 4096;
    private const int MaxTitle = 1000;

    private State _state = State.Text;
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _tag = new();
    private readonly StringBuilder _bang = new();
    private StringBuilder? _title;
    private bool _closing, _selfClosing;
    private char _quote;
    private int _quotedRun;
    private char _prev, _prev2;
    private int _cdataBrackets;
    private string _rawEnd = "";
    private int _rawMatch;

    public void Feed(ReadOnlySpan<char> chars)
    {
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
        again:
            switch (_state)
            {
                case State.Text:
                    if (c == '<') { FlushText(); _state = State.TagStart; _tag.Clear(); _closing = false; _selfClosing = false; }
                    else
                    {
                        _text.Append(c);
                        if (_text.Length >= MaxTextBuffer) FlushText(partial: true);
                    }
                    break;

                case State.TagStart:
                    if (c == '/' && !_closing) { _closing = true; }
                    else if (c == '!' && !_closing) { _state = State.Bang; _bang.Clear(); }
                    else if (c == '?' && !_closing) { _state = State.Pi; _prev = '\0'; }
                    else if (char.IsLetter(c)) { _tag.Append(char.ToLowerInvariant(c)); _state = State.TagName; }
                    else
                    {
                        // Not a tag ("a < b"): keep the characters as text.
                        _text.Append('<');
                        if (_closing) _text.Append('/');
                        _state = State.Text;
                        goto again;
                    }
                    break;

                case State.TagName:
                    if (char.IsLetterOrDigit(c) || c is ':' or '-' or '_' or '.')
                    {
                        if (_tag.Length < 64) _tag.Append(char.ToLowerInvariant(c));
                    }
                    else { _state = State.InTag; _quote = '\0'; _quotedRun = 0; goto again; }
                    break;

                case State.InTag:
                    if (_quote != '\0')
                    {
                        // A runaway quote in malformed markup must not swallow the rest of the document.
                        if (c == _quote || ++_quotedRun > MaxQuotedRun) _quote = '\0';
                    }
                    else if (c is '"' or '\'') { _quote = c; _quotedRun = 0; }
                    else if (c == '>') EndTag();
                    else if (c == '<') { EndTag(); goto again; } // unterminated tag: recover at the next '<'
                    else _selfClosing = c == '/';
                    break;

                case State.Bang:
                    _bang.Append(c);
                    if (_bang.Length == 2 && _bang[0] == '-' && _bang[1] == '-') { _state = State.Comment; _prev = _prev2 = '\0'; }
                    else if (_bang.Length == 7 && _bang.ToString() == "[CDATA[") { _state = State.CData; _cdataBrackets = 0; }
                    else if (!"--".StartsWith(_bang.ToString(), StringComparison.Ordinal) && !"[CDATA[".StartsWith(_bang.ToString(), StringComparison.Ordinal))
                    {
                        _state = State.Declaration;
                        if (c == '>') _state = State.Text;
                    }
                    break;

                case State.Comment:
                    if (c == '>' && _prev == '-' && _prev2 == '-') _state = State.Text;
                    _prev2 = _prev; _prev = c;
                    break;

                case State.CData:
                    if (c == ']') { _cdataBrackets++; break; }
                    if (c == '>' && _cdataBrackets >= 2)
                    {
                        _text.Append(']', _cdataBrackets - 2);
                        FlushText(raw: true);
                        _state = State.Text;
                        break;
                    }
                    if (_cdataBrackets > 0) { _text.Append(']', _cdataBrackets); _cdataBrackets = 0; }
                    _text.Append(c);
                    if (_text.Length >= MaxTextBuffer) FlushText(raw: true);
                    break;

                case State.Pi:
                    if (c == '>' && _prev == '?') _state = State.Text;
                    _prev = c;
                    break;

                case State.Declaration:
                    if (c == '>') _state = State.Text;
                    break;

                case State.RawText:
                    // Skip <script>/<style> content until "</script" (case-insensitive).
                    if (char.ToLowerInvariant(c) == _rawEnd[_rawMatch])
                    {
                        if (++_rawMatch == _rawEnd.Length)
                        {
                            _state = State.InTag; _closing = true; _quote = '\0'; _quotedRun = 0; _selfClosing = false;
                            _tag.Clear().Append(_rawEnd, 2, _rawEnd.Length - 2);
                        }
                    }
                    else _rawMatch = c == '<' ? 1 : 0;
                    break;
            }
        }
    }

    public void Finish()
    {
        if (_state == State.CData && _cdataBrackets > 0) _text.Append(']', _cdataBrackets);
        if (_state is State.Text or State.CData) FlushText(raw: _state == State.CData);
        if (_title != null) { sink.SetMetadata("title", _title.ToString()); _title = null; }
    }

    private void EndTag()
    {
        _state = State.Text;
        string name = _tag.ToString();
        if (name.Length == 0) return;
        if (!_closing && !_selfClosing && name is "script" or "style")
        {
            _state = State.RawText;
            _rawEnd = "</" + name;
            _rawMatch = 0;
            return;
        }
        if (name == "title")
        {
            if (!_closing && !_selfClosing) _title ??= new StringBuilder();
            else if (_closing && _title != null) { sink.SetMetadata("title", _title.ToString()); _title = null; }
        }
        // HTML inline tags can split words ("par<b>tie</b>l"); in generic XML every element boundary separates words.
        if (html && Inline.Contains(name)) return;
        if (Block.Contains(name)) sink.Break();
        else sink.Append(" ");
    }

    private void FlushText(bool partial = false, bool raw = false)
    {
        if (_text.Length == 0) return;
        string s;
        string keep = "";
        if (raw) s = _text.ToString();
        else
        {
            string all = _text.ToString();
            if (partial)
            {
                // Do not split an entity reference across two flushes.
                int amp = all.LastIndexOf('&');
                if (amp >= 0 && amp >= all.Length - 32 && all.IndexOf(';', amp) < 0) { keep = all[amp..]; all = all[..amp]; }
            }
            s = all.Contains('&') ? WebUtility.HtmlDecode(all) : all;
        }
        _text.Clear().Append(keep);
        sink.Append(s);
        if (_title != null && _title.Length < MaxTitle) _title.Append(s, 0, Math.Min(s.Length, MaxTitle - _title.Length));
    }
}
