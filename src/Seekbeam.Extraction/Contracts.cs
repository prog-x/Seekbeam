using System.Text;

namespace Seekbeam.Extraction;

/// <summary>Resource limits applied to one extraction (file and nested archive entries).</summary>
public sealed class ExtractionLimits
{
    /// <summary>Maximum characters of text kept (the sink stops accepting text beyond this).</summary>
    public int MaxChars { get; init; } = 1_000_000;
    /// <summary>Maximum source size in bytes (checked by the caller before extraction).</summary>
    public long MaxFileBytes { get; init; } = 50L * 1024 * 1024;
    public int MaxArchiveDepth { get; init; } = 2;
    public int MaxArchiveEntries { get; init; } = 10_000;
    public long MaxArchiveEntryBytes { get; init; } = 50L * 1024 * 1024;
    public long MaxArchiveTotalBytes { get; init; } = 200L * 1024 * 1024;
    public int MaxCompressionRatio { get; init; } = 100;
    public int MaxPdfPages { get; init; } = 2_000;
    /// <summary>Maximum uncompressed bytes read from one XML part of an OOXML/ODF package.</summary>
    public long MaxXmlPartBytes { get; init; } = 200L * 1024 * 1024;
}

public enum ExtractionStatus { Ok = 0, Empty = 1, Unsupported = 2, Encrypted = 3, TooLarge = 4, Malformed = 5, Timeout = 6, Error = 7, Skipped = 8 }

/// <summary>Bounded text accumulator. Extractors append text; once full, <see cref="IsFull"/> is true and further text is ignored.</summary>
public sealed class TextSink
{
    private readonly StringBuilder _sb;
    private readonly int _max;
    private bool _lastWasSpace = true;
    public TextSink(int maxChars) { _max = Math.Max(1, maxChars); _sb = new StringBuilder(Math.Min(_max, 64 * 1024)); }
    public bool IsFull => _sb.Length >= _max;
    public bool Truncated { get; private set; }
    public int Length => _sb.Length;
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Appends text, collapsing runs of whitespace/control characters to a single space.</summary>
    public void Append(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (_sb.Length >= _max) { Truncated = true; return; }
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '�')
            {
                if (_lastWasSpace) continue;
                _sb.Append(c == '\n' || c == '\r' ? '\n' : ' ');
                _lastWasSpace = true;
            }
            else { _sb.Append(c); _lastWasSpace = false; }
        }
    }

    public void Append(string? text) { if (!string.IsNullOrEmpty(text)) Append(text.AsSpan()); }

    /// <summary>Appends a paragraph/line separator.</summary>
    public void Break()
    {
        if (_sb.Length == 0 || _sb.Length >= _max) return;
        if (_lastWasSpace && _sb.Length > 0 && _sb[^1] == ' ') _sb[^1] = '\n';
        else if (!_lastWasSpace) { _sb.Append('\n'); _lastWasSpace = true; }
    }

    public void SetMetadata(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Metadata[key] = value.Trim();
    }

    public override string ToString() => _sb.ToString().Trim();
}

/// <summary>Context passed to extractors (archive nesting, dispatch to other extractors).</summary>
public sealed class ExtractionContext
{
    public required ExtractorRegistry Registry { get; init; }
    public required ExtractionLimits Limits { get; init; }
    /// <summary>Current archive nesting depth (0 = top-level file).</summary>
    public int ArchiveDepth { get; init; }
    /// <summary>Remaining uncompressed byte budget shared by all nested archive entries of the top-level file.</summary>
    public ArchiveBudget Budget { get; init; } = new();
    public ExtractionContext Nested() => new() { Registry = Registry, Limits = Limits, ArchiveDepth = ArchiveDepth + 1, Budget = Budget };
}

public sealed class ArchiveBudget
{
    public long BytesUsed;
    public int EntriesUsed;
}

/// <summary>Signals a recoverable, classified extraction failure.</summary>
public sealed class ExtractionException(ExtractionStatus status, string message) : Exception(message)
{
    public ExtractionStatus Status { get; } = status;
}

/// <summary>
/// A content extractor. Implementations MUST only read from <paramref name="source"/> (never open other files, never
/// write any file, never create temporary files — FILE_IMMUTABILITY_POLICY) and must honour limits and cancellation.
/// </summary>
public interface IContentExtractor
{
    string Id { get; }
    int Version { get; }
    /// <summary>Lower-case extensions without dot.</summary>
    IReadOnlyCollection<string> Extensions { get; }
    /// <param name="source">Read-only stream (seekable for top-level files; archive entries are provided as seekable MemoryStreams).</param>
    void Extract(Stream source, string extension, TextSink sink, ExtractionContext context, CancellationToken ct);
}

public sealed class ExtractionResult
{
    public ExtractionStatus Status { get; init; }
    public string Text { get; init; } = "";
    public string? Error { get; init; }
    public string? ExtractorId { get; init; }
    public int ExtractorVersion { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
