using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Seekbeam.Extraction.Tests;

internal static class Fx
{
    public static readonly ExtractorRegistry Registry = ExtractorRegistry.CreateDefault();

    /// <summary>Limits without the file-size pre-check (isolates immutability tests from SourceFileAccess size reporting).</summary>
    public static readonly ExtractionLimits NoSizeLimit = new() { MaxFileBytes = long.MaxValue };

    public static ExtractionResult Extract(byte[] bytes, string ext, ExtractionLimits? limits = null) =>
        Registry.ExtractStream(new MemoryStream(bytes, writable: false), ext, limits ?? new ExtractionLimits(), CancellationToken.None);

    public static ExtractionResult Extract(string text, string ext, Encoding? enc = null) =>
        Extract((enc ?? new UTF8Encoding(false)).GetBytes(text), ext);

    /// <summary>Builds a ZIP in memory from (name, content) parts; string contents are UTF-8.</summary>
    public static byte[] Zip(params (string name, object content)[] parts)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in parts)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = e.Open();
                byte[] data = content as byte[] ?? Encoding.UTF8.GetBytes((string)content);
                s.Write(data);
            }
        return ms.ToArray();
    }

    /// <summary>Patches ZIP local + central headers of one entry (sizes and/or general purpose flags).</summary>
    public static void PatchZip(byte[] zip, string entryName, uint? uncompressedSize = null, ushort? orFlags = null)
    {
        byte[] name = Encoding.UTF8.GetBytes(entryName);
        for (int i = 0; i + 46 <= zip.Length; i++)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i));
            if (sig == 0x04034b50 && Matches(zip, i + 30, BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(i + 26)), name))
            {
                if (orFlags is { } f) Or16(zip, i + 6, f);
                if (uncompressedSize is { } u) BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 22), u);
            }
            else if (sig == 0x02014b50 && Matches(zip, i + 46, BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(i + 28)), name))
            {
                if (orFlags is { } f) Or16(zip, i + 8, f);
                if (uncompressedSize is { } u) BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 24), u);
            }
        }
    }

    private static bool Matches(byte[] b, int off, int len, byte[] name) =>
        len == name.Length && off + len <= b.Length && b.AsSpan(off, len).SequenceEqual(name);

    private static void Or16(byte[] b, int off, ushort f) =>
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(off)) | f));

    /// <summary>Runs <paramref name="action"/> and fails if it does not complete within <paramref name="seconds"/>.</summary>
    public static T WithTimeout<T>(Func<T> action, int seconds = 5)
    {
        var task = Task.Run(action);
        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), "operation did not complete in time");
        return task.Result;
    }
}

/// <summary>
/// Minimal MS-CFB writer for tests: version 3 (512-byte sectors), one FAT sector (≤ 128 sectors), root-level streams;
/// streams smaller than 4096 bytes go to the mini stream.
/// </summary>
internal static class CfbWriter
{
    private const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF, FatSect = 0xFFFFFFFD, NoStream = 0xFFFFFFFF;

    public static byte[] Build(IReadOnlyList<(string name, byte[] data)> streams, Action<uint[]>? patchFat = null)
    {
        const int S = 512, M = 64;
        int n = streams.Count;

        // Mini stream layout.
        var mini = new MemoryStream();
        var miniFat = new List<uint>();
        var start = new uint[n];
        for (int i = 0; i < n; i++)
        {
            var d = streams[i].data;
            if (d.Length >= 4096 || d.Length == 0) continue;
            int first = miniFat.Count, count = (d.Length + M - 1) / M;
            start[i] = (uint)first;
            for (int k = 0; k < count; k++) miniFat.Add(k == count - 1 ? EndOfChain : (uint)(first + k + 1));
            mini.Write(d);
            mini.Write(new byte[count * M - d.Length]);
        }

        // Sector layout: 0 = FAT, then directory, mini FAT, mini stream, big streams.
        var fat = Enumerable.Repeat(FreeSect, 128).ToArray();
        fat[0] = FatSect;
        int next = 1;
        uint Alloc(int sectors)
        {
            if (sectors == 0) return EndOfChain;
            uint first = (uint)next;
            for (int k = 0; k < sectors; k++) fat[next + k] = k == sectors - 1 ? EndOfChain : (uint)(next + k + 1);
            next += sectors;
            return first;
        }
        int dirSectors = ((n + 1) * 128 + S - 1) / S;
        uint dirStart = Alloc(dirSectors);
        int miniFatSectors = (miniFat.Count * 4 + S - 1) / S;
        uint miniFatStart = Alloc(miniFatSectors);
        int miniStreamSectors = (int)((mini.Length + S - 1) / S);
        uint miniStreamStart = Alloc(miniStreamSectors);
        for (int i = 0; i < n; i++)
        {
            var d = streams[i].data;
            if (d.Length >= 4096) start[i] = Alloc((d.Length + S - 1) / S);
            else if (d.Length == 0) start[i] = EndOfChain;
        }
        if (next > 128) throw new InvalidOperationException("test CFB too large");
        patchFat?.Invoke(fat);

        var file = new byte[(next + 1) * S];
        var h = file.AsSpan(0, S);
        ReadOnlySpan<byte> sig = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        sig.CopyTo(h);
        W16(h, 0x18, 0x3E); W16(h, 0x1A, 3); W16(h, 0x1C, 0xFFFE); W16(h, 0x1E, 9); W16(h, 0x20, 6);
        W32(h, 0x2C, 1); W32(h, 0x30, dirStart); W32(h, 0x38, 4096);
        W32(h, 0x3C, miniFatSectors > 0 ? miniFatStart : EndOfChain); W32(h, 0x40, (uint)miniFatSectors);
        W32(h, 0x44, EndOfChain); W32(h, 0x48, 0);
        for (int i = 0; i < 109; i++) W32(h, 0x4C + i * 4, i == 0 ? 0 : FreeSect);

        Span<byte> Sector(uint sid) => file.AsSpan((int)(sid + 1) * S, S);
        for (int i = 0; i < 128; i++) W32(Sector(0), i * 4, fat[i]);

        // Directory: root (0) whose child is entry 1; streams chained through their right sibling pointers.
        var dir = new byte[dirSectors * S];
        void Entry(int idx, string name, byte type, uint left, uint right, uint child, uint st, long size)
        {
            var e = dir.AsSpan(idx * 128, 128);
            var nb = Encoding.Unicode.GetBytes(name);
            nb.CopyTo(e);
            W16(e, 0x40, (ushort)(nb.Length + 2));
            e[0x42] = type; e[0x43] = 1;
            W32(e, 0x44, left); W32(e, 0x48, right); W32(e, 0x4C, child);
            W32(e, 0x74, st);
            BinaryPrimitives.WriteUInt64LittleEndian(e[0x78..], (ulong)size);
        }
        Entry(0, "Root Entry", 5, NoStream, NoStream, n > 0 ? 1u : NoStream, mini.Length > 0 ? miniStreamStart : EndOfChain, mini.Length);
        for (int i = 0; i < n; i++)
            Entry(i + 1, streams[i].name, 2, NoStream, i + 1 < n ? (uint)(i + 2) : NoStream, NoStream, start[i], streams[i].data.Length);
        for (int i = n + 1; i < dirSectors * 4; i++) { W32(dir, i * 128 + 0x44, NoStream); W32(dir, i * 128 + 0x48, NoStream); W32(dir, i * 128 + 0x4C, NoStream); }
        for (int k = 0; k < dirSectors; k++) dir.AsSpan(k * S, S).CopyTo(Sector((uint)(dirStart + k)));

        var mf = new byte[miniFatSectors * S];
        for (int i = 0; i < mf.Length / 4; i++) W32(mf, i * 4, i < miniFat.Count ? miniFat[i] : FreeSect);
        for (int k = 0; k < miniFatSectors; k++) mf.AsSpan(k * S, S).CopyTo(Sector((uint)(miniFatStart + k)));

        var mb = mini.ToArray();
        for (int k = 0; k < miniStreamSectors; k++)
            mb.AsSpan(k * S, Math.Min(S, mb.Length - k * S)).CopyTo(Sector((uint)(miniStreamStart + k)));

        for (int i = 0; i < n; i++)
        {
            var d = streams[i].data;
            if (d.Length < 4096) continue;
            for (int k = 0; k * S < d.Length; k++)
                d.AsSpan(k * S, Math.Min(S, d.Length - k * S)).CopyTo(Sector((uint)(start[i] + k)));
        }
        return file;
    }

    private static void W16(Span<byte> b, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b[off..], v);
    private static void W32(Span<byte> b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[off..], v);
}

/// <summary>Little-endian byte builder for binary fixtures.</summary>
internal sealed class Bin
{
    private readonly MemoryStream _ms = new();
    public int Length => (int)_ms.Length;
    public Bin U8(int v) { _ms.WriteByte((byte)v); return this; }
    public Bin U16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); _ms.Write(b); return this; }
    public Bin U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _ms.Write(b); return this; }
    public Bin F64(double v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, v); _ms.Write(b); return this; }
    public Bin Bytes(ReadOnlySpan<byte> b) { _ms.Write(b); return this; }
    public Bin Latin1(string s) => Bytes(Encoding.Latin1.GetBytes(s));
    public Bin Utf16(string s) => Bytes(Encoding.Unicode.GetBytes(s));
    public Bin Zeros(int n) => Bytes(new byte[n]);
    public Bin PadTo(int len) => Length < len ? Zeros(len - Length) : this;
    public byte[] ToArray() => _ms.ToArray();
}
