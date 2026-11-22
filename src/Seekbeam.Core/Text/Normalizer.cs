using System.Buffers;
using System.Globalization;
using System.Text;

namespace Seekbeam.Core.Text;

/// <summary>
/// Folds text for matching: lower case, diacritics removed, common ligatures expanded, encoded as UTF-8.
/// The same function is used for indexed names and for queries, so matching is case- and accent-insensitive
/// by construction. UTF-8 is self-synchronising: a byte-level substring match never straddles two characters.
/// </summary>
public static class Normalizer
{
    private const int TableSize = 0x2000; // covers Latin, Greek, Cyrillic, Latin Extended Additional, combining marks
    private static readonly byte[]?[] s_table = BuildTable();
    private static readonly byte[] s_asciiLower = BuildAscii();

    private static byte[] BuildAscii()
    {
        var t = new byte[128];
        for (int i = 0; i < 128; i++) t[i] = (byte)(i is >= 'A' and <= 'Z' ? i + 32 : i);
        return t;
    }

    private static byte[]?[] BuildTable()
    {
        var t = new byte[]?[TableSize];
        var special = new Dictionary<char, string>
        {
            ['ß'] = "ss", ['ẞ'] = "ss", ['æ'] = "ae", ['Æ'] = "ae", ['œ'] = "oe", ['Œ'] = "oe", ['ø'] = "o", ['Ø'] = "o",
            ['đ'] = "d", ['Đ'] = "d", ['ł'] = "l", ['Ł'] = "l", ['þ'] = "th", ['Þ'] = "th", ['ð'] = "d", ['Ð'] = "d",
            ['ı'] = "i", ['ħ'] = "h", ['Ħ'] = "h", ['ŧ'] = "t", ['Ŧ'] = "t", [' '] = " ", ['’'] = "'", ['‘'] = "'",
            ['–'] = "-", ['—'] = "-",
        };
        for (int c = 0x80; c < TableSize; c++)
        {
            char ch = (char)c;
            string folded;
            if (special.TryGetValue(ch, out var s)) folded = s;
            else if (char.IsSurrogate(ch)) continue;
            else
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) { t[c] = []; continue; }
                string d = ch.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormD);
                var sb = new StringBuilder(d.Length);
                foreach (char x in d)
                    if (CharUnicodeInfo.GetUnicodeCategory(x) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)) sb.Append(x);
                folded = sb.ToString();
            }
            t[c] = Encoding.UTF8.GetBytes(folded);
        }
        // Ligature/typographic characters outside the table range.
        return t;
    }

    /// <summary>Maximum UTF-8 bytes produced per UTF-16 input char.</summary>
    public const int MaxBytesPerChar = 4;

    /// <summary>Normalizes <paramref name="input"/> into <paramref name="dest"/>; returns bytes written.
    /// <paramref name="dest"/> must be at least input.Length * MaxBytesPerChar long.</summary>
    public static int Normalize(ReadOnlySpan<char> input, Span<byte> dest, Span<int> charIndexOfByte = default)
    {
        int o = 0;
        bool map = !charIndexOfByte.IsEmpty;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c < 0x80)
            {
                if (map) charIndexOfByte[o] = i;
                dest[o++] = s_asciiLower[c];
                continue;
            }
            if (c < TableSize && s_table[c] is { } bytes)
            {
                for (int k = 0; k < bytes.Length; k++) { if (map) charIndexOfByte[o] = i; dest[o++] = bytes[k]; }
                continue;
            }
            // Generic path: lower-case and encode (surrogate pairs kept intact).
            if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                Span<char> pair = [c, input[i + 1]];
                int n = Encoding.UTF8.GetBytes(pair, dest[o..]);
                if (map) for (int k = 0; k < n; k++) charIndexOfByte[o + k] = i;
                o += n; i++;
                continue;
            }
            if (char.IsSurrogate(c)) continue; // lone surrogate: drop
            Span<char> one = [char.ToLowerInvariant(c)];
            int m = Encoding.UTF8.GetBytes(one, dest[o..]);
            if (map) for (int k = 0; k < m; k++) charIndexOfByte[o + k] = i;
            o += m;
        }
        return o;
    }

    public static byte[] NormalizeToBytes(ReadOnlySpan<char> input)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(16, input.Length * MaxBytesPerChar));
        try
        {
            int n = Normalize(input, rented);
            return rented.AsSpan(0, n).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    public static string NormalizeToString(ReadOnlySpan<char> input) => Encoding.UTF8.GetString(NormalizeToBytes(input));

    /// <summary>True if the byte at <paramref name="pos"/> starts a "word" in a normalized name.</summary>
    public static bool IsWordStart(ReadOnlySpan<byte> s, int pos)
    {
        if (pos <= 0) return true;
        byte prev = s[pos - 1], cur = s[pos];
        if (IsSeparator(prev)) return true;
        bool pd = prev is >= (byte)'0' and <= (byte)'9', cd = cur is >= (byte)'0' and <= (byte)'9';
        return pd != cd && (IsAlnum(prev) && IsAlnum(cur));
    }

    public static bool IsSeparator(byte b) => b is (byte)' ' or (byte)'_' or (byte)'-' or (byte)'.' or (byte)'(' or (byte)')'
        or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)',' or (byte)';' or (byte)'+' or (byte)'&' or (byte)'@'
        or (byte)'#' or (byte)'!' or (byte)'~' or (byte)'\'' or (byte)'\\' or (byte)'/' or (byte)'=' or (byte)'$' or (byte)'%';

    private static bool IsAlnum(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' || b >= 0x80;
}
