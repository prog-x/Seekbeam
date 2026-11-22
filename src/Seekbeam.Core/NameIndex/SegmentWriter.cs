using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Seekbeam.Core.Storage;

namespace Seekbeam.Core.NameIndex;

/// <summary>
/// Streaming k-way merge of id-sorted record sources into a new segment file. Stale records are dropped; for duplicate
/// ids the newest source wins. Memory use is O(k): the merge is re-run for each column (6 sequential passes).
/// </summary>
internal static class SegmentWriter
{
    private sealed class Cursor(IReadOnlyList<RecordSource> sources)
    {
        private readonly int[] _pos = new int[sources.Count];

        public void Reset() => Array.Clear(_pos);

        public bool Next(out int src, out int ord)
        {
            src = -1; ord = -1;
            int bestId = int.MaxValue;
            for (int s = 0; s < sources.Count; s++)
            {
                var so = sources[s];
                ref int p = ref _pos[s];
                while (p < so.Count && so.IsStale(p)) p++;
                if (p >= so.Count) continue;
                int id = so.Ids[p];
                if (id < bestId || (id == bestId)) { bestId = id; src = s; } // later (newer) source wins ties
            }
            if (src < 0) return false;
            ord = _pos[src];
            // Advance every source past bestId (drop older duplicates).
            for (int s = 0; s < sources.Count; s++)
            {
                var so = sources[s];
                ref int p = ref _pos[s];
                while (p < so.Count && so.Ids[p] == bestId) p++;
            }
            return true;
        }
    }

    /// <returns>Number of records written.</returns>
    public static int Write(SeekbeamStorage storage, string relativePath, IReadOnlyList<RecordSource> sourcesOldestFirst, long maxRev, CancellationToken ct = default)
    {
        var cur = new Cursor(sourcesOldestFirst);
        int count = 0; long textLen = 0, dirTextLen = 0;
        while (cur.Next(out int s, out int o))
        {
            count++;
            var off = sourcesOldestFirst[s].Offsets;
            textLen += off[o + 1] - off[o];
            if ((sourcesOldestFirst[s].Flags[o] & (byte)EntryFlags.Directory) != 0) dirTextLen += off[o + 1] - off[o];
        }
        if (textLen > int.MaxValue) throw new InvalidOperationException("Segment text exceeds 2 GB; split required.");
        ct.ThrowIfCancellationRequested();

        string tmpRel = relativePath + ".tmp";
        storage.TryDelete(tmpRel);
        using (var fs = storage.CreateNew(tmpRel, 1 << 20))
        {
            Span<byte> header = stackalloc byte[Segment.HeaderSize];
            header.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(header, Segment.Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], count);
            BinaryPrimitives.WriteInt64LittleEndian(header[16..], textLen);
            BinaryPrimitives.WriteInt64LittleEndian(header[24..], maxRev);
            BinaryPrimitives.WriteInt64LittleEndian(header[32..], DateTime.UtcNow.Ticks);
            BinaryPrimitives.WriteInt64LittleEndian(header[40..], dirTextLen);
            fs.Write(header);

            var buf = new byte[1 << 16];
            int bp = 0;
            void Flush() { fs.Write(buf, 0, bp); bp = 0; }
            void PutInt(int v) { if (bp + 4 > buf.Length) Flush(); BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(bp), v); bp += 4; }
            void PutByte(byte v) { if (bp + 1 > buf.Length) Flush(); buf[bp++] = v; }

            cur.Reset(); while (cur.Next(out int s, out int o)) PutInt(sourcesOldestFirst[s].Ids[o]);
            cur.Reset(); while (cur.Next(out int s, out int o)) PutInt(sourcesOldestFirst[s].Parents[o]);
            cur.Reset(); while (cur.Next(out int s, out int o)) PutByte(sourcesOldestFirst[s].Flags[o]);
            for (int pad = count; (pad & 3) != 0; pad++) PutByte(0);
            int pos = 0;
            cur.Reset();
            while (cur.Next(out int s, out int o))
            {
                PutInt(pos);
                var off = sourcesOldestFirst[s].Offsets;
                pos += off[o + 1] - off[o];
            }
            PutInt(pos);
            Flush();
            ct.ThrowIfCancellationRequested();
            cur.Reset();
            while (cur.Next(out int s, out int o))
            {
                var src = sourcesOldestFirst[s];
                var off = src.Offsets;
                var bytes = src.Text.Slice(off[o], off[o + 1] - off[o]);
                if (bp + bytes.Length > buf.Length) Flush();
                if (bytes.Length > buf.Length) fs.Write(bytes);
                else { bytes.CopyTo(buf.AsSpan(bp)); bp += bytes.Length; }
            }
            Flush();
            // Directory-name sidecar (v2).
            cur.Reset();
            while (cur.Next(out int s, out int o))
            {
                var src = sourcesOldestFirst[s];
                if ((src.Flags[o] & (byte)EntryFlags.Directory) == 0) continue;
                var off = src.Offsets;
                var bytes = src.Text.Slice(off[o], off[o + 1] - off[o]);
                if (bp + bytes.Length > buf.Length) Flush();
                if (bytes.Length > buf.Length) fs.Write(bytes);
                else { bytes.CopyTo(buf.AsSpan(bp)); bp += bytes.Length; }
            }
            Flush();
            fs.Flush();
            SeekbeamStorage.FlushDurable(fs);
        }
        storage.Move(tmpRel, relativePath, overwrite: true);
        return count;
    }
}
