using System.Buffers;
using Seekbeam.Core.Config;
using Seekbeam.Core.NameIndex;

namespace Seekbeam.Core.Search;

/// <summary>One scored candidate from the name index.</summary>
public readonly record struct NameCandidate(int Id, int Score, byte Flags, int Errors);

public sealed class NameSearchOptions
{
    public bool MatchInPath { get; init; } = true;
    public bool IncludeHidden { get; init; }
    public bool IncludeSystem { get; init; }
    public bool AllowFuzzy { get; init; } = true;
    public int FuzzyMinTokenLength { get; init; } = 4;
    public int FuzzyThreshold { get; init; } = 3;
    public int MaxThreads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    public RankingWeights Weights { get; init; } = new();
    public UsageBoosts Usage { get; init; } = UsageBoosts.None;
}

/// <summary>
/// Parallel scan of the name index (ARCHITECTURE.md §5.3): vectorised substring search per token over each block,
/// ancestor-directory matching for path tokens (memoised), filters, scoring and per-thread top-K heaps,
/// followed by an optional bitap fuzzy pass when exact results are scarce.
/// </summary>
public static class NameSearcher
{
    public const int BlockSize = 32_768;

    private readonly record struct Block(RecordSource Source, int Lo, int Hi);

    private sealed class Worker
    {
        public readonly ushort[] Mask = new ushort[BlockSize];
        public readonly Dictionary<int, (uint Mask, int Depth)> Memo = new();
        public readonly PriorityQueue<NameCandidate, (int, int)> Heap = new();
        public int Matches;
    }

    public sealed class Outcome
    {
        public List<NameCandidate> Top { get; init; } = [];
        public int TotalMatches { get; init; }
        public bool UsedFuzzy { get; init; }
    }

    public static Outcome Search(NameIndexSnapshot snap, ParsedQuery q, int k, NameSearchOptions opt, CancellationToken ct)
    {
        if (q.IsEmpty || q.Kind == KindFilter.App) return new Outcome();
        int heapCap = Math.Max(k * 4, 64);
        var exact = Run(snap, q, heapCap, opt, fuzzy: false, ct);
        bool fuzzyEligible = opt.AllowFuzzy && q.Tokens.Any(t => BitapPattern.ErrorsFor(t, opt.FuzzyMinTokenLength) > 0);
        if (exact.TotalMatches >= opt.FuzzyThreshold || !fuzzyEligible)
            return Finalize(snap, exact.Top, exact.TotalMatches, false, k, opt);
        var fz = Run(snap, q, heapCap, opt, fuzzy: true, ct);
        // Fuzzy results include exact ones (0 errors); keep the best score per id.
        var merged = exact.Top.Concat(fz.Top).GroupBy(c => c.Id).Select(g => g.MaxBy(c => c.Score)).ToList();
        return Finalize(snap, merged, Math.Max(exact.TotalMatches, fz.TotalMatches), fz.TotalMatches > exact.TotalMatches, k, opt);
    }

    private static List<Block> Blocks(NameIndexSnapshot snap)
    {
        var blocks = new List<Block>();
        foreach (var src in snap.NewestFirst())
            for (int lo = 0; lo < src.Count; lo += BlockSize)
                blocks.Add(new Block(src, lo, Math.Min(src.Count, lo + BlockSize)));
        return blocks;
    }

    private static Outcome Finalize(NameIndexSnapshot snap, List<NameCandidate> cands, int total, bool fuzzy, int k, NameSearchOptions opt)
    {
        // Refinement on the short list: depth penalty (needs parent walk) and stable ordering.
        var memo = new Dictionary<int, (uint, int)>();
        var refined = new List<NameCandidate>(cands.Count);
        foreach (var c in cands)
        {
            int depth = 0;
            if (snap.TryFind(c.Id, out var src, out int ord))
                depth = AncestorInfo(snap, src.Parents[ord], [], memo).Depth;
            refined.Add(c with { Score = c.Score - opt.Weights.DepthPenalty * Math.Min(depth, 30) });
        }
        refined.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Id.CompareTo(b.Id));
        if (refined.Count > k) refined.RemoveRange(k, refined.Count - k);
        return new Outcome { Top = refined, TotalMatches = total, UsedFuzzy = fuzzy };
    }

    private sealed class Terms
    {
        public required List<byte[]> All;     // tokens, then path filters, then excludes
        public int TokenCount, FilterCount, ExcludeCount;
        public uint TokenMask, FilterMask, ExcludeMask;
        public BitapPattern?[]? Fuzzy;         // per token
        public byte[][]? FuzzyPieces;          // pigeonhole pieces of the longest fuzzy token
    }

    private static Terms BuildTerms(ParsedQuery q, NameSearchOptions opt, bool fuzzy)
    {
        var all = new List<byte[]>();
        all.AddRange(q.Tokens);
        all.AddRange(q.PathFilters.Take(4));
        all.AddRange(q.Excludes.Take(8));
        var t = new Terms
        {
            All = all,
            TokenCount = q.Tokens.Count,
            FilterCount = Math.Min(q.PathFilters.Count, 4),
            ExcludeCount = Math.Min(q.Excludes.Count, 8),
        };
        t.TokenMask = t.TokenCount == 0 ? 0 : (1u << t.TokenCount) - 1;
        t.FilterMask = ((1u << t.FilterCount) - 1) << t.TokenCount;
        t.ExcludeMask = ((1u << t.ExcludeCount) - 1) << (t.TokenCount + t.FilterCount);
        if (fuzzy)
        {
            t.Fuzzy = new BitapPattern?[t.TokenCount];
            int longest = -1;
            for (int i = 0; i < t.TokenCount; i++)
            {
                var tok = q.Tokens[i];
                int e = BitapPattern.ErrorsFor(tok, opt.FuzzyMinTokenLength);
                if (e > 0 && tok.Length <= 63) t.Fuzzy[i] = new BitapPattern(tok, e);
                if (t.Fuzzy[i] != null && (longest < 0 || tok.Length > q.Tokens[longest].Length)) longest = i;
            }
            if (longest >= 0)
            {
                // Pigeonhole: a match with ≤ e errors contains one of (e+1) disjoint pieces exactly.
                var tok = q.Tokens[longest];
                int parts = t.Fuzzy[longest]!.MaxErrors + 1;
                var pieces = new List<byte[]>();
                int len = tok.Length / parts;
                for (int p = 0; p < parts; p++)
                {
                    int start = p * len, end = p == parts - 1 ? tok.Length : start + len;
                    if (end > start) pieces.Add(tok[start..end]);
                }
                t.FuzzyPieces = [.. pieces];
            }
        }
        return t;
    }

    private static (List<NameCandidate> Top, int TotalMatches) Run(NameIndexSnapshot snap, ParsedQuery q, int heapCap, NameSearchOptions opt, bool fuzzy, CancellationToken ct)
    {
        var terms = BuildTerms(q, opt, fuzzy);
        if (fuzzy && terms.FuzzyPieces == null) return ([], 0);
        var blocks = Blocks(snap);
        // A token missing from a name can only be satisfied by an ancestor if it occurs in some directory name:
        // checked once per query on the small directory-name sidecars (≈8 % of the text).
        uint feasible = 0;
        if (!fuzzy && terms.TokenCount > 1)
            foreach (var src in snap.NewestFirst())
                for (int t = 0; t < terms.TokenCount; t++)
                    if ((feasible & (1u << t)) == 0 && src.DirText.IndexOf(terms.All[t]) >= 0) feasible |= 1u << t;
        var workers = new System.Collections.Concurrent.ConcurrentBag<Worker>();
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, opt.MaxThreads), CancellationToken = ct };
        Parallel.For(0, blocks.Count, po,
            () => new Worker(),
            (i, _, w) =>
            {
                var mask = w.Mask.AsSpan(0, blocks[i].Hi - blocks[i].Lo);
                MarkBlock(blocks[i], terms, fuzzy, mask);
                EvalBlock(snap, blocks[i], q, terms, opt, fuzzy, heapCap, w, mask, feasible, ct);
                return w;
            },
            w => workers.Add(w));
        var all = new List<NameCandidate>();
        int total = 0;
        foreach (var w in workers)
        {
            total += w.Matches;
            while (w.Heap.Count > 0) all.Add(w.Heap.Dequeue());
        }
        all.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Id.CompareTo(b.Id));
        if (all.Count > heapCap) all.RemoveRange(heapCap, all.Count - heapCap);
        return (all, total);
    }

    private const ushort FuzzyCandidateBit = 1 << 15;

    /// <summary>Sets per-record token bits for one block.</summary>
    private static void MarkBlock(Block b, Terms terms, bool fuzzy, Span<ushort> mask)
    {
        var src = b.Source;
        var offsets = src.Offsets;
        int n = b.Hi - b.Lo;
        mask.Clear();
        int start = offsets[b.Lo], end = offsets[b.Hi];
        var region = src.Text[start..end];
        if (fuzzy)
        {
            // Candidates: records containing any pigeonhole piece.
            foreach (var piece in terms.FuzzyPieces!) MarkHits(region, start, offsets, b.Lo, n, piece, FuzzyCandidateBit, mask, wordStartOnly: false);
            return;
        }
        for (int t = 0; t < terms.TokenCount; t++)
            MarkHits(region, start, offsets, b.Lo, n, terms.All[t], (ushort)(1 << t), mask, wordStartOnly: terms.All[t].Length == 1);
    }

    /// <summary>Filters, path matching, scoring and top-K for one block (text still hot in cache).</summary>
    private static void EvalBlock(NameIndexSnapshot snap, Block b, ParsedQuery q, Terms terms, NameSearchOptions opt, bool fuzzy, int heapCap, Worker w,
        ReadOnlySpan<ushort> mask, uint feasibleInPath, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        var src = b.Source;
        var offsets = src.Offsets;
        var text = src.Text;
        int n = b.Hi - b.Lo;
        bool scanAll = !fuzzy && terms.TokenCount == 0;

        var parents = src.Parents;
        var flags = src.Flags;
        var ids = src.Ids;
        var weights = opt.Weights;
        for (int r = 0; r < n; r++)
        {
            uint m = mask[r];
            if (m == 0 && !scanAll) continue;
            int i = b.Lo + r;
            if (src.IsStale(i)) continue;
            var f = (EntryFlags)flags[i];
            bool isDir = (f & EntryFlags.Directory) != 0;
            if (q.Kind == KindFilter.File && isDir) continue;
            if (q.Kind == KindFilter.Folder && !isDir) continue;
            if (!opt.IncludeHidden && (f & EntryFlags.Hidden) != 0 && (f & EntryFlags.Root) == 0) continue;
            if (!opt.IncludeSystem && (f & EntryFlags.System) != 0 && (f & EntryFlags.Root) == 0) continue;
            int ns = offsets[i];
            var name = text.Slice(ns, offsets[i + 1] - ns - 1);
            if (q.Extensions.Count > 0 && (isDir || !HasExtension(name, q.Extensions))) continue;

            int errors = 0;
            uint nameMask;
            if (fuzzy)
            {
                nameMask = 0;
                bool ok = true;
                for (int t = 0; t < terms.TokenCount && ok; t++)
                {
                    var tok = terms.All[t];
                    if (name.IndexOf(tok) >= 0) { nameMask |= 1u << t; continue; }
                    var bp = terms.Fuzzy![t];
                    int e = bp?.Match(name) ?? -1;
                    if (e < 0) ok = false; else { errors += e; nameMask |= 1u << t; }
                }
                if (!ok || errors == 0) continue; // errors == 0 already found by the exact pass
            }
            else nameMask = m & terms.TokenMask;

            // Excludes on the name itself.
            bool excluded = false;
            for (int e = 0; e < terms.ExcludeCount && !excluded; e++)
                if (name.IndexOf(terms.All[terms.TokenCount + terms.FilterCount + e]) >= 0) excluded = true;
            if (excluded) continue;

            uint missing = terms.TokenMask & ~nameMask;
            if (!fuzzy && missing != 0 && (!opt.MatchInPath || (missing & ~feasibleInPath) != 0)) continue; // cannot be satisfied by any ancestor
            bool needAncestors = (!fuzzy && missing != 0) || terms.FilterCount > 0 || terms.ExcludeCount > 0;
            if (needAncestors)
            {
                if ((f & EntryFlags.Root) != 0) { if (nameMask != terms.TokenMask || terms.FilterCount > 0) continue; }
                else
                {
                    var (anc, _) = AncestorInfo(snap, parents[i], terms.All, w.Memo);
                    if ((anc & terms.ExcludeMask) != 0) continue;
                    if ((anc & terms.FilterMask) != terms.FilterMask) continue;
                    uint combined = nameMask | (opt.MatchInPath ? anc & terms.TokenMask : 0);
                    if (combined != terms.TokenMask) continue;
                }
            }
            else if (nameMask != terms.TokenMask) continue;

            w.Matches++;
            int score = Ranking.TextScore(name, isDir, q, nameMask, weights, errors);
            int id = ids[i];
            score += opt.Usage.Get(id);
            var cand = new NameCandidate(id, score, flags[i], errors);
            if (w.Heap.Count < heapCap) w.Heap.Enqueue(cand, (score, -id));
            else if (w.Heap.TryPeek(out _, out var min) && (score, -id).CompareTo(min) > 0) w.Heap.EnqueueDequeue(cand, (score, -id));
        }
    }

    /// <summary>Sets <paramref name="bit"/> for every record of the block whose text contains <paramref name="pat"/>.</summary>
    private static void MarkHits(ReadOnlySpan<byte> region, int regionStart, ReadOnlySpan<int> offsets, int lo, int n, ReadOnlySpan<byte> pat, ushort bit, Span<ushort> mask, bool wordStartOnly)
    {
        int pos = 0;
        int rec = 0; // relative record index
        while (pos < region.Length)
        {
            int idx = region[pos..].IndexOf(pat);
            if (idx < 0) return;
            int abs = regionStart + pos + idx;
            // Advance rec so that offsets[lo+rec] <= abs < offsets[lo+rec+1]; gallop with binary search for large gaps.
            if (offsets[lo + rec + 1] <= abs)
            {
                if (abs - offsets[lo + rec + 1] > 2048)
                {
                    int found = offsets.Slice(lo + rec, n - rec + 1).BinarySearch(abs);
                    rec += found >= 0 ? found : ~found - 1;
                }
                while (offsets[lo + rec + 1] <= abs) rec++;
            }
            if (wordStartOnly)
            {
                // Single-character tokens only match at the start of a word (typing "a" lists names starting with a).
                int recStart = offsets[lo + rec];
                var recText = region.Slice(recStart - regionStart, offsets[lo + rec + 1] - recStart - 1);
                if (!Text.Normalizer.IsWordStart(recText, abs - recStart)) { pos = abs - regionStart + 1; continue; }
            }
            mask[rec] |= bit;
            pos = offsets[lo + rec + 1] - regionStart; // next record
        }
    }

    private static bool HasExtension(ReadOnlySpan<byte> name, List<byte[]> exts)
    {
        int dot = name.LastIndexOf((byte)'.');
        if (dot <= 0) return false;
        var ext = name[(dot + 1)..];
        foreach (var e in exts) if (ext.SequenceEqual(e)) return true;
        return false;
    }

    /// <summary>OR of term bits matched by the names of <paramref name="dirId"/> and all its ancestors, plus depth.</summary>
    private static (uint Mask, int Depth) AncestorInfo(NameIndexSnapshot snap, int dirId, List<byte[]> terms, Dictionary<int, (uint, int)> memo)
    {
        if (dirId == 0) return (0, 0);
        if (memo.TryGetValue(dirId, out var hit)) return hit;
        // Iterative walk up to the first memoised ancestor.
        Span<int> chainIds = stackalloc int[128];
        Span<uint> chainMasks = stackalloc uint[128];
        int len = 0;
        int cur = dirId;
        (uint Mask, int Depth) baseInfo = (0, 0);
        while (cur != 0 && len < 128)
        {
            if (memo.TryGetValue(cur, out var m)) { baseInfo = m; break; }
            uint mk = 0;
            int parent = 0;
            if (snap.TryFind(cur, out var src, out int ord))
            {
                var nm = src.NameAt(ord);
                for (int t = 0; t < terms.Count; t++) if (nm.IndexOf(terms[t]) >= 0) mk |= 1u << t;
                parent = src.Parents[ord];
            }
            chainIds[len] = cur; chainMasks[len] = mk; len++;
            cur = parent;
        }
        uint acc = baseInfo.Mask;
        int depth = baseInfo.Depth;
        for (int i = len - 1; i >= 0; i--)
        {
            acc |= chainMasks[i];
            depth++;
            memo[chainIds[i]] = (acc, depth);
        }
        return memo[dirId];
    }
}
