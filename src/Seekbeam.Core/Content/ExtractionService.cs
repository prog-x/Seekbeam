using System.Text;

namespace Seekbeam.Core.Content;

/// <summary>Status codes shared with Seekbeam.Extraction (same numeric values as ExtractionStatus).</summary>
public enum ExtractStatus : byte { Ok = 0, Empty = 1, Unsupported = 2, Encrypted = 3, TooLarge = 4, Malformed = 5, Timeout = 6, Error = 7, Skipped = 8, HostCrashed = 9 }

public sealed class ExtractRequest
{
    /// <summary>Source path (Seekbeam side only: never sent to the extractor host, which receives an opened read-only handle).</summary>
    public string Path { get; init; } = "";
    /// <summary>Wire fields: handle value valid in the host process, and the lower-case extension used to pick the extractor.</summary>
    public long SourceHandle { get; init; }
    public string Extension { get; init; } = "";
    public int MaxChars { get; init; } = 1_000_000;
    public long MaxFileBytes { get; init; } = 50L * 1024 * 1024;
    public int ArchiveMaxDepth { get; init; } = 2;
    public int ArchiveMaxEntries { get; init; } = 10_000;
    public long ArchiveMaxEntryBytes { get; init; } = 50L * 1024 * 1024;
    public long ArchiveMaxTotalBytes { get; init; } = 200L * 1024 * 1024;
    public int ArchiveMaxRatio { get; init; } = 100;
    public bool ArchivesEnabled { get; init; } = true;
    public List<string> DisabledExtractors { get; init; } = [];
    public List<string> ExtraTextExtensions { get; init; } = [];
}

public sealed class ExtractReply
{
    public ExtractStatus Status { get; init; }
    public string Text { get; init; } = "";
    public string? Error { get; init; }
    public string Extractor { get; init; } = "";
    public int Version { get; init; }
    public bool Truncated { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>Extracts text from a source file. Implementations must only read the source (FILE_IMMUTABILITY_POLICY).</summary>
public interface IExtractionService : IDisposable
{
    ExtractReply Extract(ExtractRequest request, TimeSpan timeout, CancellationToken ct);
    /// <summary>Applies resource limits to the extraction worker (memory in MB, CPU hard cap in percent of the machine).</summary>
    void SetLimits(int memoryMB, int cpuPercent);
}

/// <summary>Length-prefixed binary protocol between Seekbeam and Seekbeam.ExtractorHost (stdin/stdout).</summary>
public static class ExtractorProtocol
{
    public const int Magic = 0x46584558; // "FXEX"
    public const int Version = 2;
    public const int HelloMagic = 0x4F4C4548; // "HELO": sent once by the host at start-up (protocol version + isolation seen from inside)

    public static void WriteRequest(BinaryWriter w, int id, ExtractRequest r)
    {
        w.Write(Magic); w.Write(id);
        w.Write(r.SourceHandle); w.Write(r.Extension); w.Write(r.MaxChars); w.Write(r.MaxFileBytes);
        w.Write(r.ArchiveMaxDepth); w.Write(r.ArchiveMaxEntries); w.Write(r.ArchiveMaxEntryBytes); w.Write(r.ArchiveMaxTotalBytes);
        w.Write(r.ArchiveMaxRatio); w.Write(r.ArchivesEnabled);
        w.Write(r.DisabledExtractors.Count); foreach (var d in r.DisabledExtractors) w.Write(d);
        w.Write(r.ExtraTextExtensions.Count); foreach (var d in r.ExtraTextExtensions) w.Write(d);
        w.Flush();
    }

    public static (int Id, ExtractRequest Request)? ReadRequest(BinaryReader rd)
    {
        int magic;
        try { magic = rd.ReadInt32(); } catch (EndOfStreamException) { return null; }
        if (magic != Magic) throw new InvalidDataException("Bad request magic");
        int id = rd.ReadInt32();
        var req = new ExtractRequest
        {
            SourceHandle = rd.ReadInt64(), Extension = rd.ReadString(), MaxChars = rd.ReadInt32(), MaxFileBytes = rd.ReadInt64(),
            ArchiveMaxDepth = rd.ReadInt32(), ArchiveMaxEntries = rd.ReadInt32(), ArchiveMaxEntryBytes = rd.ReadInt64(), ArchiveMaxTotalBytes = rd.ReadInt64(),
            ArchiveMaxRatio = rd.ReadInt32(), ArchivesEnabled = rd.ReadBoolean(),
        };
        int n = rd.ReadInt32(); for (int i = 0; i < n; i++) req.DisabledExtractors.Add(rd.ReadString());
        n = rd.ReadInt32(); for (int i = 0; i < n; i++) req.ExtraTextExtensions.Add(rd.ReadString());
        return (id, req);
    }

    /// <summary>Host → Seekbeam, once at start-up: protocol version and the isolation the host observes on its own token.</summary>
    public static void WriteHello(BinaryWriter w, SandboxLevel observed)
    {
        w.Write(HelloMagic); w.Write(Version); w.Write((byte)observed);
        w.Flush();
    }

    public static SandboxLevel ReadHello(BinaryReader rd)
    {
        if (rd.ReadInt32() != HelloMagic) throw new InvalidDataException("Bad hello magic");
        int v = rd.ReadInt32();
        if (v != Version) throw new InvalidDataException($"Extractor host protocol {v}, expected {Version}");
        byte level = rd.ReadByte();
        if (level > (byte)SandboxLevel.AppContainer) throw new InvalidDataException("Bad sandbox level");
        return (SandboxLevel)level;
    }

    public static void WriteReply(BinaryWriter w, int id, ExtractReply r)
    {
        w.Write(Magic); w.Write(id); w.Write((byte)r.Status); w.Write(r.Truncated);
        w.Write(r.Extractor); w.Write(r.Version); w.Write(r.Error ?? "");
        w.Write(r.Metadata.Count); foreach (var (k, v) in r.Metadata) { w.Write(k); w.Write(v); }
        var bytes = Encoding.UTF8.GetBytes(r.Text);
        w.Write(bytes.Length); w.Write(bytes);
        w.Flush();
    }

    public static (int Id, ExtractReply Reply) ReadReply(BinaryReader rd)
    {
        if (rd.ReadInt32() != Magic) throw new InvalidDataException("Bad reply magic");
        int id = rd.ReadInt32();
        var status = (ExtractStatus)rd.ReadByte();
        bool truncated = rd.ReadBoolean();
        string extractor = rd.ReadString();
        int version = rd.ReadInt32();
        string error = rd.ReadString();
        var md = new Dictionary<string, string>();
        int n = rd.ReadInt32();
        for (int i = 0; i < n; i++) md[rd.ReadString()] = rd.ReadString();
        int len = rd.ReadInt32();
        if (len < 0 || len > 256 * 1024 * 1024) throw new InvalidDataException("Bad reply length");
        string text = Encoding.UTF8.GetString(rd.ReadBytes(len));
        return (id, new ExtractReply { Status = status, Truncated = truncated, Extractor = extractor, Version = version, Error = error.Length == 0 ? null : error, Metadata = md, Text = text });
    }
}
