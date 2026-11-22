using System.Runtime.InteropServices;
using Seekbeam.Core.Content;
using Seekbeam.Core.SourceAccess;
using Seekbeam.Extraction;

namespace Seekbeam.ExtractorHost;

/// <summary>
/// Isolated text-extraction worker (TD-09, TD-17). Started by Seekbeam in a sandbox (AppContainer or low integrity) inside a
/// Job Object (memory/CPU caps, single process, killed with its parent). It cannot open files by itself: each request carries
/// a read-only handle duplicated by Seekbeam, verified here (<see cref="SourceFileAccess.FromReceivedHandle"/>).
/// Reads requests on stdin, writes replies on stdout. It never writes any file. Exits when stdin closes.
/// </summary>
internal static partial class Program
{
    private static int Main()
    {
        // Background processing mode: low CPU, I/O and memory priority for the whole worker process.
        SetPriorityClass(GetCurrentProcess(), PROCESS_MODE_BACKGROUND_BEGIN);
        var input = new BinaryReader(Console.OpenStandardInput());
        var output = new BinaryWriter(Console.OpenStandardOutput());
        try { ExtractorProtocol.WriteHello(output, ObservedIsolation()); }
        catch (IOException) { return 0; }
        ExtractorRegistry? registry = null;
        string? registryKey = null;
        while (true)
        {
            (int Id, ExtractRequest Request)? msg;
            try { msg = ExtractorProtocol.ReadRequest(input); }
            catch (Exception) { return 2; }
            if (msg == null) return 0; // parent closed the pipe
            var (id, req) = msg.Value;
            string key = string.Join(",", req.DisabledExtractors) + "|" + string.Join(",", req.ExtraTextExtensions) + "|" + req.ArchivesEnabled;
            if (registry == null || registryKey != key)
            {
                var disabled = new List<string>(req.DisabledExtractors);
                if (!req.ArchivesEnabled) disabled.Add("zip");
                registry = ExtractorRegistry.CreateDefault(disabled, req.ExtraTextExtensions);
                registryKey = key;
            }
            ExtractReply reply;
            try
            {
                using var source = SourceFileAccess.FromReceivedHandle(req.SourceHandle);
                var limits = new ExtractionLimits
                {
                    MaxChars = req.MaxChars, MaxFileBytes = req.MaxFileBytes, MaxArchiveDepth = req.ArchiveMaxDepth,
                    MaxArchiveEntries = req.ArchiveMaxEntries, MaxArchiveEntryBytes = req.ArchiveMaxEntryBytes,
                    MaxArchiveTotalBytes = req.ArchiveMaxTotalBytes, MaxCompressionRatio = req.ArchiveMaxRatio,
                };
                var r = registry.ExtractOpened(source, req.Extension, limits, CancellationToken.None);
                reply = new ExtractReply
                {
                    Status = (ExtractStatus)(byte)r.Status, Text = r.Text, Error = r.Error, Extractor = r.ExtractorId ?? "",
                    Version = r.ExtractorVersion, Truncated = r.Truncated, Metadata = new Dictionary<string, string>(r.Metadata),
                };
            }
            catch (Exception ex) // isolation boundary: report, never crash on a single file
            {
                reply = new ExtractReply { Status = ExtractStatus.Error, Error = ex.GetType().Name + ": " + ex.Message };
            }
            try { ExtractorProtocol.WriteReply(output, id, reply); }
            catch (IOException) { return 0; }
            if (registry != null && GC.GetTotalMemory(false) > 128L * 1024 * 1024) GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        }
    }

    /// <summary>Isolation read from this process's own token: AppContainer flag, otherwise the integrity level.</summary>
    private static SandboxLevel ObservedIsolation()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token)) return SandboxLevel.None;
        try
        {
            if (GetTokenInformation(token, TokenIsAppContainer, out uint isAc, 4, out _) && isAc != 0) return SandboxLevel.AppContainer;
            Span<byte> buf = stackalloc byte[64];
            if (GetTokenInformationBuf(token, TokenIntegrityLevel, ref MemoryMarshal.GetReference(buf), buf.Length, out _))
            {
                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes } }: the RID is the last sub-authority.
                IntPtr sid = MemoryMarshal.Read<IntPtr>(buf);
                int count = Marshal.ReadByte(sid, 1);
                uint rid = (uint)Marshal.ReadInt32(sid, 8 + 4 * (count - 1));
                if (rid <= 0x1000) return SandboxLevel.LowIntegrity;
            }
            return SandboxLevel.None;
        }
        finally { CloseHandle(token); }
    }

    private const uint PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000;
    private const uint TOKEN_QUERY = 0x8;
    private const int TokenIntegrityLevel = 25, TokenIsAppContainer = 29;

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(IntPtr process, uint priorityClass);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int infoClass, out uint info, int length, out int returned);

    [LibraryImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformationBuf(IntPtr token, int infoClass, ref byte info, int length, out int returned);
}
