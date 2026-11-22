using Seekbeam.Core.Config;
using Seekbeam.Core.Text;

namespace Seekbeam.Core.Search;

/// <summary>
/// Additive, documented relevance model (ARCHITECTURE.md §6). Text signals dominate; usage boosts are capped below
/// the gap between an exact and a partial match so history never overrides a clearly better textual match.
/// </summary>
public static class Ranking
{
    public static int TextScore(ReadOnlySpan<byte> name, bool isDir, ParsedQuery q, uint nameMask, RankingWeights w, int errors)
    {
        int score = 0;
        var tokens = q.Tokens;
        ReadOnlySpan<byte> core = name;
        if (!isDir)
        {
            int dot = name.LastIndexOf((byte)'.');
            if (dot > 0) core = name[..dot];
        }
        if (q.Joined.Length > 0 && (SeparatorInsensitiveEquals(core, q.Joined) || SeparatorInsensitiveEquals(name, q.Joined)))
            score += w.ExactName;
        if (tokens.Count > 0 && (nameMask & 1) != 0 && name.StartsWith(tokens[0]))
            score += w.NamePrefix;

        int inName = 0, matchedLen = 0, lastPos = -1;
        bool inOrder = true;
        for (int t = 0; t < tokens.Count; t++)
        {
            if ((nameMask & (1u << t)) == 0) { inOrder = false; continue; }
            var tok = tokens[t];
            int pos = FindBest(name, tok, out bool wordStart);
            if (pos < 0) { inOrder = false; continue; } // fuzzy-only match
            inName++;
            matchedLen += tok.Length;
            score += wordStart ? w.WordStart : w.Substring;
            if (pos < lastPos) inOrder = false;
            lastPos = pos;
        }
        if (tokens.Count > 0 && inName == tokens.Count) score += w.AllInName;
        if (tokens.Count > 1 && inOrder && inName == tokens.Count) score += w.InOrder;
        if (core.Length > 0 && matchedLen > 0)
            score += (int)(Math.Min(1.0, matchedLen / (double)core.Length) * w.CoverageMax);
        if (isDir) score += w.Folder;
        score -= errors * w.FuzzyPenaltyPerError;
        return score;
    }

    /// <summary>Usage boost: min(UsageMax, 80·log2(1+launches)) × recency decay (half-life). Capped.</summary>
    public static int UsageBoost(int launches, DateTime lastUsedUtc, RankingWeights w, DateTime nowUtc)
    {
        if (launches <= 0) return 0;
        double freq = Math.Min(w.UsageMax, 80 * Math.Log2(1 + launches));
        double ageDays = Math.Max(0, (nowUtc - lastUsedUtc).TotalDays);
        double decay = Math.Pow(0.5, ageDays / Math.Max(0.5, w.UsageHalfLifeDays));
        return (int)(freq * (0.35 + 0.65 * decay));
    }

    /// <summary>First occurrence at a word start if any (up to a few occurrences), else first occurrence.</summary>
    public static int FindBest(ReadOnlySpan<byte> name, ReadOnlySpan<byte> tok, out bool wordStart)
    {
        int first = name.IndexOf(tok);
        wordStart = false;
        if (first < 0) return -1;
        int pos = first;
        for (int guard = 0; guard < 6 && pos >= 0; guard++)
        {
            if (Normalizer.IsWordStart(name, pos)) { wordStart = true; return pos; }
            int next = name[(pos + 1)..].IndexOf(tok);
            pos = next < 0 ? -1 : pos + 1 + next;
        }
        return first;
    }

    private static bool IsSep(byte b) => b is (byte)' ' or (byte)'_' or (byte)'-' or (byte)'.';

    public static bool SeparatorInsensitiveEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            byte x = a[i], y = b[i];
            if (x == y) continue;
            if (IsSep(x) && IsSep(y)) continue;
            return false;
        }
        return true;
    }
}
