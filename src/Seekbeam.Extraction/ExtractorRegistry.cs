using Seekbeam.Core.SourceAccess;

namespace Seekbeam.Extraction;

/// <summary>Maps extensions to extractors; extractors can be disabled by id.</summary>
public sealed class ExtractorRegistry
{
    private readonly Dictionary<string, IContentExtractor> _byExt = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IContentExtractor> _all = [];

    public ExtractorRegistry(IEnumerable<IContentExtractor> extractors, IEnumerable<string>? disabledIds = null, IEnumerable<string>? extraTextExtensions = null)
    {
        var disabled = new HashSet<string>(disabledIds ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var e in extractors)
        {
            if (disabled.Contains(e.Id)) continue;
            _all.Add(e);
            foreach (var ext in e.Extensions) _byExt.TryAdd(ext, e);
        }
        if (extraTextExtensions != null && _all.FirstOrDefault(e => e.Id == "text") is { } text)
            foreach (var ext in extraTextExtensions) _byExt.TryAdd(ext.TrimStart('.').ToLowerInvariant(), text);
    }

    /// <summary>The default set of built-in extractors.</summary>
    public static ExtractorRegistry CreateDefault(IEnumerable<string>? disabledIds = null, IEnumerable<string>? extraTextExtensions = null) =>
        new(BuiltInExtractors.All(), disabledIds, extraTextExtensions);

    public IReadOnlyList<IContentExtractor> All => _all;
    public IEnumerable<string> SupportedExtensions => _byExt.Keys;
    public IContentExtractor? ForExtension(string ext) => _byExt.GetValueOrDefault(ext.TrimStart('.'));

    /// <summary>Extracts text from a source file opened read-only through <see cref="SourceFileAccess"/>.</summary>
    public ExtractionResult ExtractFile(string path, ExtractionLimits limits, CancellationToken ct)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var extractor = ForExtension(ext);
        if (extractor == null) return new ExtractionResult { Status = ExtractionStatus.Unsupported };
        try
        {
            if (!SourceFileAccess.TryGetMetadata(path, out var md))
                return new ExtractionResult { Status = ExtractionStatus.Error, Error = "not found" };
            if (md.Size > limits.MaxFileBytes)
                return new ExtractionResult { Status = ExtractionStatus.TooLarge, ExtractorId = extractor.Id, ExtractorVersion = extractor.Version };
            using var stream = SourceFileAccess.OpenRead(path);
            return ExtractStream(stream, ext, limits, ct);
        }
        catch (SourceAccessException ex)
        {
            return new ExtractionResult { Status = ExtractionStatus.Error, Error = ex.Message, ExtractorId = extractor.Id, ExtractorVersion = extractor.Version };
        }
    }

    /// <summary>
    /// Extracts text from a source already opened read-only by Seekbeam (the sandboxed host receives a handle, never a path).
    /// Applies the same size limit as <see cref="ExtractFile"/>.
    /// </summary>
    public ExtractionResult ExtractOpened(Stream source, string ext, ExtractionLimits limits, CancellationToken ct)
    {
        var extractor = ForExtension(ext);
        if (extractor == null) return new ExtractionResult { Status = ExtractionStatus.Unsupported };
        if (source.Length > limits.MaxFileBytes)
            return new ExtractionResult { Status = ExtractionStatus.TooLarge, ExtractorId = extractor.Id, ExtractorVersion = extractor.Version };
        return ExtractStream(source, ext, limits, ct);
    }

    /// <summary>Extracts text from an already opened read-only stream.</summary>
    public ExtractionResult ExtractStream(Stream stream, string ext, ExtractionLimits limits, CancellationToken ct)
    {
        var extractor = ForExtension(ext);
        if (extractor == null) return new ExtractionResult { Status = ExtractionStatus.Unsupported };
        var sink = new TextSink(limits.MaxChars);
        var ctx = new ExtractionContext { Registry = this, Limits = limits };
        try
        {
            extractor.Extract(stream, ext, sink, ctx, ct);
            string text = sink.ToString();
            return new ExtractionResult
            {
                Status = text.Length == 0 && sink.Metadata.Count == 0 ? ExtractionStatus.Empty : ExtractionStatus.Ok,
                Text = text, Truncated = sink.Truncated, Metadata = sink.Metadata,
                ExtractorId = extractor.Id, ExtractorVersion = extractor.Version,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (ExtractionException ex)
        {
            return new ExtractionResult { Status = ex.Status, Error = ex.Message, Text = sink.ToString(), ExtractorId = extractor.Id, ExtractorVersion = extractor.Version };
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or EndOfStreamException or FormatException
                                        or ArgumentException or IndexOutOfRangeException or InvalidOperationException or NotSupportedException or IOException
                                        or OverflowException or NullReferenceException or KeyNotFoundException or InvalidCastException)
        {
            // Malformed input must never break indexing: classify and return whatever text was recovered.
            return new ExtractionResult { Status = ExtractionStatus.Malformed, Error = ex.GetType().Name + ": " + ex.Message, Text = sink.ToString(), ExtractorId = extractor.Id, ExtractorVersion = extractor.Version };
        }
    }
}
