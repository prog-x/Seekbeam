using System.Text;
using System.Text.Unicode;

namespace Seekbeam.Extraction.Formats;

internal delegate void CharsHandler(ReadOnlySpan<char> chars);

/// <summary>Encoding detection (BOM, UTF-8 validation, Windows-1252 fallback), binary detection and streaming decoding.</summary>
internal static class TextDecoding
{
    public const int PrefixBytes = 64 * 1024;
    private const int BinarySampleBytes = 8 * 1024;

    // Field initializers run in textual order: the provider is registered (once) before Windows1252 is resolved.
    private static readonly bool s_registered = Register();
    private static bool Register() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return true; }

    public static Encoding Windows1252 { get; } = GetEncodingCore(1252) ?? Encoding.Latin1;

    private static Encoding? GetEncodingCore(int codePage)
    {
        _ = s_registered;
        try { return Encoding.GetEncoding(codePage); } catch (Exception e) when (e is ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>Returns the encoding for a Windows code page, or Windows-1252 when unknown.</summary>
    public static Encoding FromCodePage(int codePage) =>
        codePage == 1252 ? Windows1252 : codePage is > 0 and < 65536 ? GetEncodingCore(codePage) ?? Windows1252 : Windows1252;

    public static Encoding? FromName(string name)
    {
        _ = Windows1252; // ensures the code page provider is registered
        try { return Encoding.GetEncoding(name); } catch (Exception e) when (e is ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>Detects the encoding of <paramref name="prefix"/>; <paramref name="declared"/> (e.g. from an XML declaration) is used when there is no BOM.</summary>
    /// <exception cref="ExtractionException">Unsupported when the content looks binary.</exception>
    public static Encoding Detect(ReadOnlySpan<byte> prefix, bool prefixIsWholeStream, out int bomLength, Encoding? declared = null)
    {
        bomLength = 0;
        if (prefix.Length >= 4 && prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0 && prefix[3] == 0) { bomLength = 4; return new UTF32Encoding(false, false); }
        if (prefix.Length >= 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xFE && prefix[3] == 0xFF) { bomLength = 4; return new UTF32Encoding(true, false); }
        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF) { bomLength = 3; return new UTF8Encoding(false); }
        if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE) { bomLength = 2; return new UnicodeEncoding(false, false); }
        if (prefix.Length >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF) { bomLength = 2; return new UnicodeEncoding(true, false); }

        // No BOM: NUL bytes either mean BOM-less UTF-16 (NULs on one parity only) or binary content.
        var sample = prefix[..Math.Min(prefix.Length, BinarySampleBytes)];
        int nulEven = 0, nulOdd = 0;
        for (int i = 0; i < sample.Length; i++)
            if (sample[i] == 0) { if ((i & 1) == 0) nulEven++; else nulOdd++; }
        if (nulEven + nulOdd > 0)
        {
            int half = Math.Max(1, sample.Length / 2);
            if (nulOdd > half * 3 / 10 && nulEven <= half / 50) return new UnicodeEncoding(false, false);
            if (nulEven > half * 3 / 10 && nulOdd <= half / 50) return new UnicodeEncoding(true, false);
            if ((nulEven + nulOdd) * 100 > sample.Length || nulEven + nulOdd > 16)
                throw new ExtractionException(ExtractionStatus.Unsupported, "binary");
        }

        if (declared != null && declared is not UnicodeEncoding && declared is not UTF32Encoding) return declared;
        return IsValidUtf8Prefix(prefix, prefixIsWholeStream) ? new UTF8Encoding(false) : Windows1252;
    }

    /// <summary>Validates UTF-8, tolerating a multi-byte sequence cut at the end of a partial prefix.</summary>
    internal static bool IsValidUtf8Prefix(ReadOnlySpan<byte> data, bool isWhole)
    {
        if (!isWhole)
        {
            // Walk back over at most 3 continuation bytes to the last lead byte; drop it if its sequence is incomplete.
            int i = data.Length - 1, back = 0;
            while (i >= 0 && back < 3 && (data[i] & 0xC0) == 0x80) { i--; back++; }
            if (i >= 0)
            {
                byte lead = data[i];
                int need = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
                if (data.Length - i < need) data = data[..i];
            }
        }
        return Utf8.IsValid(data);
    }

    /// <summary>Reads up to <paramref name="max"/> bytes (fewer only at end of stream).</summary>
    public static int ReadPrefix(Stream s, byte[] buffer, int max)
    {
        int total = 0;
        while (total < max)
        {
            int n = s.Read(buffer, total, max - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Finds a cheap charset declaration (<c>encoding="…"</c> / <c>charset=…</c>) in the first KB of an ASCII-compatible prefix.</summary>
    public static Encoding? FindDeclaredEncoding(ReadOnlySpan<byte> prefix)
    {
        var head = prefix[..Math.Min(prefix.Length, 2048)];
        foreach (var key in (ReadOnlySpan<string>)["encoding=", "charset="])
        {
            int idx = IndexOfAsciiIgnoreCase(head, key);
            if (idx < 0) continue;
            int p = idx + key.Length;
            while (p < head.Length && (head[p] == '"' || head[p] == '\'' || head[p] == ' ')) p++;
            int start = p;
            while (p < head.Length && p - start < 40 && (char.IsAsciiLetterOrDigit((char)head[p]) || head[p] is (byte)'-' or (byte)'_' or (byte)'.' or (byte)':')) p++;
            if (p > start && FromName(Encoding.ASCII.GetString(head[start..p])) is { } enc) return enc;
        }
        return null;
    }

    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> hay, string needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && char.ToLowerInvariant((char)hay[i + j]) == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    /// <summary>
    /// Streams <paramref name="source"/> through the detected encoding with bounded buffers, calling <paramref name="onChars"/>
    /// per chunk, until end of stream or <paramref name="stop"/> returns true.
    /// </summary>
    public static void DecodeStream(Stream source, bool honourDeclaration, CharsHandler onChars, Func<bool> stop, CancellationToken ct)
    {
        byte[] buf = new byte[PrefixBytes];
        int len = ReadPrefix(source, buf, buf.Length);
        if (len == 0) return;
        bool whole = len < buf.Length;
        Encoding? declared = honourDeclaration ? FindDeclaredEncoding(buf.AsSpan(0, len)) : null;
        var enc = Detect(buf.AsSpan(0, len), whole, out int bom, declared);
        var decoder = enc.GetDecoder();
        char[] chars = new char[enc.GetMaxCharCount(buf.Length)];

        int offset = bom;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n = decoder.GetChars(buf, offset, len - offset, chars, 0, flush: false);
            if (n > 0) onChars(chars.AsSpan(0, n));
            if (stop()) return;
            len = ReadPrefix(source, buf, buf.Length);
            offset = 0;
            if (len == 0) break;
        }
        int last = decoder.GetChars(buf, 0, 0, chars, 0, flush: true);
        if (last > 0) onChars(chars.AsSpan(0, last));
    }
}
