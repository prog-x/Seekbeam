namespace Seekbeam.Extraction.Formats;

/// <summary>
/// Read-only wrapper that counts the bytes really produced by the inner stream (typically a decompression stream) and
/// throws <see cref="ExtractionException"/> (TooLarge) once <c>limit</c> is exceeded: declared archive sizes can lie.
/// </summary>
internal sealed class BoundedReadStream(Stream inner, long limit, string what) : Stream
{
    private long _count;
    public long BytesRead => _count;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _count; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        _count += n;
        if (_count > limit) throw new ExtractionException(ExtractionStatus.TooLarge, $"{what}: more than {limit} decompressed bytes");
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes of <paramref name="source"/> into memory (TooLarge beyond).</summary>
    public static MemoryStream ToMemory(Stream source, long declaredLength, long limit, string what, CancellationToken ct)
    {
        var ms = new MemoryStream((int)Math.Clamp(declaredLength, 0, Math.Min(limit, 16L * 1024 * 1024)));
        using var bounded = new BoundedReadStream(source, limit, what);
        byte[] buf = new byte[81920];
        int n;
        while ((n = bounded.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            ms.Write(buf, 0, n);
        }
        ms.Position = 0;
        return ms;
    }
}
