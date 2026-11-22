namespace Seekbeam.Core.Search;

/// <summary>
/// Approximate substring matching (Wu–Manber shift-and with k errors: substitution, insertion, deletion)
/// over normalized UTF-8 bytes. Pattern length ≤ 63 bytes.
/// </summary>
public sealed class BitapPattern
{
    private readonly ulong[] _masks = new ulong[256];
    private readonly ulong _accept;
    public int Length { get; }
    public int MaxErrors { get; }
    public byte[] Pattern { get; }

    public BitapPattern(byte[] pattern, int maxErrors)
    {
        if (pattern.Length is 0 or > 63) throw new ArgumentOutOfRangeException(nameof(pattern));
        Pattern = pattern;
        Length = pattern.Length;
        MaxErrors = Math.Clamp(maxErrors, 0, 3);
        for (int i = 0; i < pattern.Length; i++) _masks[pattern[i]] |= 1UL << i;
        _accept = 1UL << (pattern.Length - 1);
    }

    /// <summary>
    /// Returns the minimum number of errors (≤ MaxErrors) for a match anywhere in <paramref name="text"/>, or -1.
    /// Errors are substitution, insertion, deletion and adjacent transposition (optimal string alignment), so
    /// "rapprot" matches "rapport" with one error.
    /// </summary>
    public int Match(ReadOnlySpan<byte> text)
    {
        int k = MaxErrors;
        Span<ulong> r = stackalloc ulong[4];
        Span<ulong> r2 = stackalloc ulong[4]; // states two text positions back (for transpositions)
        for (int d = 0; d <= k; d++) { r[d] = (1UL << d) - 1; r2[d] = r[d]; }
        int best = -1;
        for (int d = 0; d <= k; d++) if ((r[d] & _accept) != 0) { best = d; break; } // pattern shorter than k
        ulong prevMask = 0;
        foreach (byte c in text)
        {
            ulong m = _masks[c];
            ulong prevOld = r[0];
            ulong prev2 = r2[0];
            r2[0] = r[0];
            r[0] = ((r[0] << 1) | 1) & m;
            for (int d = 1; d <= k; d++)
            {
                ulong old = r[d];
                ulong transposition = ((prev2 << 2) | 2) & prevMask & (m << 1);
                r[d] = (((old << 1) | 1) & m)   // match
                       | prevOld                 // insertion (extra text byte)
                       | ((prevOld << 1) | 1)    // substitution
                       | ((r[d - 1] << 1) | 1)   // deletion (skip pattern byte)
                       | transposition;          // swap of two adjacent bytes
                prevOld = old;
                prev2 = r2[d];
                r2[d] = old;
            }
            prevMask = m;
            for (int d = 0; d <= k; d++)
            {
                if ((r[d] & _accept) != 0)
                {
                    if (d == 0) return 0;
                    if (best < 0 || d < best) best = d;
                    break;
                }
            }
        }
        return best;
    }

    /// <summary>Errors allowed for a token of this length (configurable minimum length).</summary>
    public static int ErrorsFor(int length, int minLength) => length < minLength ? 0 : length >= 8 ? 2 : 1;

    /// <summary>
    /// Errors allowed for a token. Tokens containing digits get no typo tolerance: an error in a number
    /// ("2024" → "2023", "file_010" → "file_011") produces matches that look relevant but are wrong.
    /// </summary>
    public static int ErrorsFor(ReadOnlySpan<byte> token, int minLength)
    {
        if (token.Length > 63 || token.IndexOfAnyInRange((byte)'0', (byte)'9') >= 0) return 0;
        return ErrorsFor(token.Length, minLength);
    }
}
