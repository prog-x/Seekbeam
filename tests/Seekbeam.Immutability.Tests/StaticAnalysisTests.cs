using System.Text;
using Xunit.Abstractions;

namespace Seekbeam.Immutability.Tests;

/// <summary>
/// FILE_IMMUTABILITY_POLICY §7 — static verification on the compiled IL of every shipped Seekbeam assembly.
/// Rules: (1) file-system write APIs are only called from Seekbeam.Core.Storage (+ documented, reviewed exceptions);
/// (2) every P/Invoke is inventoried and must be in the reviewed allow-list; write-capable Win32/NT entry points are
/// forbidden; CreateFileW is declared only in SourceAccess; (3) no SQL that writes other files (ATTACH / VACUUM INTO).
/// The full inventory is written to artifacts/audit/il-inventory.md for the audit report.
/// </summary>
public sealed class StaticAnalysisTests(ITestOutputHelper output)
{
    public static string RepoRoot
    {
        get
        {
            string? d = AppContext.BaseDirectory;
            while (d != null && !File.Exists(Path.Combine(d, "Seekbeam.sln"))) d = Path.GetDirectoryName(d);
            return d ?? throw new DirectoryNotFoundException("repo root");
        }
    }

    public static IReadOnlyList<string> ShippedAssemblies()
    {
        string root = RepoRoot;
        string Find(string project, string file)
        {
            foreach (var cfg in new[] { "Debug", "Release" })
            {
                string p = Path.Combine(root, "src", project, "bin", cfg, "net10.0-windows", file);
                if (File.Exists(p)) return p;
            }
            throw new FileNotFoundException(file);
        }
        return
        [
            Find("Seekbeam.Core", "Seekbeam.Core.dll"),
            Find("Seekbeam.Extraction", "Seekbeam.Extraction.dll"),
            Find("Seekbeam.ExtractorHost", "Seekbeam.ExtractorHost.dll"),
            Find("Seekbeam.App", "Seekbeam.dll"),
        ];
    }

    private static readonly Dictionary<string, Func<string, string, bool>> WriteApis = new()
    {
        ["System.IO.File"] = (m, _) => m is "Create" or "CreateText" or "AppendText" or "Delete" or "Move" or "Copy" or "Replace" or "Open" or "OpenWrite"
            or "OpenHandle" or "SetAttributes" or "Encrypt" or "Decrypt" or "SetUnixFileMode" or "CreateSymbolicLink" or "CreateHardLink"
            || m.StartsWith("WriteAll") || m.StartsWith("AppendAll") || m.StartsWith("SetCreationTime") || m.StartsWith("SetLastAccessTime") || m.StartsWith("SetLastWriteTime"),
        ["System.IO.FileInfo"] = (m, _) => IsInfoWrite(m),
        ["System.IO.DirectoryInfo"] = (m, _) => IsInfoWrite(m),
        ["System.IO.FileSystemInfo"] = (m, _) => IsInfoWrite(m),
        ["System.IO.Directory"] = (m, _) => m is "CreateDirectory" or "Delete" or "Move" or "CreateSymbolicLink" or "CreateTempSubdirectory"
            || m.StartsWith("SetCreationTime") || m.StartsWith("SetLastAccessTime") || m.StartsWith("SetLastWriteTime"),
        ["System.IO.FileStream"] = (m, _) => m == ".ctor",
        ["System.IO.RandomAccess"] = (m, _) => m is "Write" or "WriteAsync" or "SetLength",
        ["System.IO.MemoryMappedFiles.MemoryMappedFile"] = (m, _) => m is "CreateFromFile" or "CreateNew" or "CreateOrOpen",
        ["System.IO.Compression.ZipFile"] = (_, _) => true,
        ["System.IO.Compression.ZipFileExtensions"] = (_, _) => true,
        ["System.IO.Compression.ZipArchiveEntry"] = (m, _) => m.StartsWith("ExtractToFile"),
        ["System.IO.Path"] = (m, _) => m == "GetTempFileName",
        ["System.IO.StreamWriter"] = (m, s) => m == ".ctor" && s.StartsWith("System.String"),
        ["System.IO.FileSystemAclExtensions"] = (m, _) => m is "SetAccessControl" or "Create" or "CreateDirectory",
        ["Microsoft.Data.Sqlite.SqliteConnection"] = (m, s) => (m == ".ctor" && s.StartsWith("System.String")) || m == "set_ConnectionString" || m == "BackupDatabase",
        ["System.Diagnostics.Process"] = (m, _) => m == "Start",
        ["Microsoft.VisualBasic.FileIO.FileSystem"] = (_, _) => true,
    };

    private static bool IsInfoWrite(string m) => m is "Create" or "CreateText" or "AppendText" or "Delete" or "MoveTo" or "CopyTo" or "Open" or "OpenWrite"
        or "Replace" or "Encrypt" or "Decrypt" or "CreateSubdirectory" or "CreateAsSymbolicLink" or "SetAccessControl" or "set_Attributes" or "set_UnixFileMode"
        || m.StartsWith("set_CreationTime") || m.StartsWith("set_LastAccessTime") || m.StartsWith("set_LastWriteTime");

    /// <summary>Reviewed exceptions outside Seekbeam.Core.Storage (justified in FILE_IMMUTABILITY_AUDIT.md).</summary>
    // (The extractor host is no longer started with Process.Start but by SandboxLauncher.CreateProcess*, checked below.)
    private static bool IsAllowedException(CallSite c) =>
        // Wraps a handle opened by SourceNative.Open with GENERIC_READ only, or received by the extractor host as a
        // FILE_GENERIC_READ duplicate whose granted access is verified (kernel-enforced read-only).
        (c.CalleeType == "System.IO.FileStream" && c.CallerType.StartsWith("Seekbeam.Core.SourceAccess.SourceFileAccess")
            && c.CalleeSignature.StartsWith("Microsoft.Win32.SafeHandles.SafeFileHandle,System.IO.FileAccess"));

    public static bool IsWriteApi(CallSite c) => WriteApis.TryGetValue(c.CalleeType, out var pred) && pred(c.CalleeMember, c.CalleeSignature);

    /// <summary>Reviewed allow-list of native entry points (module!entry). None of them writes to the file system,
    /// except the SQLite engine whose database paths come exclusively from SeekbeamStorage.</summary>
    private static readonly HashSet<string> AllowedPInvokes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Source read access (SourceNative)
        "kernel32.dll!CreateFileW", "kernel32.dll!GetFileAttributesExW", "kernel32.dll!GetFileInformationByHandle", "kernel32.dll!GetFinalPathNameByHandleW",
        "ntdll.dll!NtQueryDirectoryFile", "ntdll.dll!RtlNtStatusToDosError",
        // Threads / processes / jobs / system probes
        "kernel32.dll!GetCurrentThread", "kernel32.dll!SetThreadPriority", "kernel32.dll!GetCurrentProcess", "kernel32.dll!SetPriorityClass",
        "kernel32.dll!CreateJobObjectW", "kernel32.dll!SetInformationJobObject", "kernel32.dll!AssignProcessToJobObject", "kernel32.dll!CloseHandle",
        "kernel32.dll!GetSystemTimes", "kernel32.dll!GlobalMemoryStatusEx", "kernel32.dll!GetSystemPowerStatus", "user32.dll!GetLastInputInfo",
        "shell32.dll!SHQueryUserNotificationState", "kernel32.dll!GetCurrentThreadId", "kernel32.dll!GetModuleHandleW",
        // UI: hooks, hotkeys, windows, monitors, DWM, tray, icons
        "user32.dll!SetWindowsHookExW", "user32.dll!UnhookWindowsHookEx", "user32.dll!CallNextHookEx", "user32.dll!GetMessageW", "user32.dll!PostThreadMessageW",
        "user32.dll!RegisterHotKey", "user32.dll!UnregisterHotKey", "user32.dll!GetCursorPos", "user32.dll!GetForegroundWindow", "user32.dll!SetForegroundWindow",
        "user32.dll!GetWindowThreadProcessId", "user32.dll!AttachThreadInput", "user32.dll!BringWindowToTop", "user32.dll!ShowWindow",
        "user32.dll!MonitorFromPoint", "user32.dll!GetMonitorInfoW", "shcore.dll!GetDpiForMonitor", "dwmapi.dll!DwmSetWindowAttribute",
        "shell32.dll!Shell_NotifyIconW", "user32.dll!RegisterWindowMessageW", "shell32.dll!SHGetFileInfoW", "user32.dll!DestroyIcon", "gdi32.dll!DeleteObject",
        "shell32.dll!ExtractIconExW", "shell32.dll!SHCreateItemFromParsingName",
        // Shell actions delegated to Windows (open / runas / openas / properties / reveal in Explorer)
        "shell32.dll!ShellExecuteExW", "shell32.dll!ILCreateFromPathW", "shell32.dll!ILFree", "shell32.dll!SHOpenFolderAndSelectItems",
        // Known folders lookup (Downloads), read-only
        "shell32.dll!SHGetKnownFolderPath", "ole32.dll!CoTaskMemFree",
        // Extractor sandbox (TD-17). Process creation: only SandboxLauncher (checked below). Tokens are only restricted
        // (privileges removed, integrity lowered) or queried; no privilege is ever enabled.
        "kernel32.dll!InitializeProcThreadAttributeList", "kernel32.dll!UpdateProcThreadAttribute", "kernel32.dll!DeleteProcThreadAttributeList",
        "kernel32.dll!CreateProcessW", "advapi32.dll!CreateProcessAsUserW", "kernel32.dll!ResumeThread", "kernel32.dll!TerminateProcess",
        "kernel32.dll!WaitForSingleObject", "kernel32.dll!GetExitCodeProcess", "kernel32.dll!K32GetProcessMemoryInfo", "kernel32.dll!LocalFree",
        "userenv.dll!DeriveAppContainerSidFromAppContainerName", "advapi32.dll!FreeSid", "advapi32.dll!OpenProcessToken",
        "advapi32.dll!CreateRestrictedToken", "advapi32.dll!ConvertStringSidToSidW", "advapi32.dll!GetLengthSid", "advapi32.dll!SetTokenInformation",
        "advapi32.dll!GetTokenInformation",
        // Handing a source to the sandbox: read-only duplicate (SourceNative only, checked below) and verification on reception
        "kernel32.dll!DuplicateHandle", "kernel32.dll!GetFileType", "ntdll.dll!NtQueryObject",
        // AppContainer profile (OS-managed: %LOCALAPPDATA%\Packages + HKCU mapping), SeekbeamStorage only (checked below)
        "userenv.dll!CreateAppContainerProfile", "userenv.dll!DeleteAppContainerProfile",
    };

    /// <summary>Sensitive entry points and the only type allowed to declare each of them.</summary>
    private static readonly Dictionary<string, string> PinnedDeclaringType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CreateFileW"] = "Seekbeam.Core.SourceAccess.SourceNative",
        ["DuplicateHandle"] = "Seekbeam.Core.SourceAccess.SourceNative",
        ["CreateProcessW"] = "Seekbeam.Core.Content.SandboxLauncher",
        ["CreateProcessAsUserW"] = "Seekbeam.Core.Content.SandboxLauncher",
        ["SetTokenInformation"] = "Seekbeam.Core.Content.SandboxLauncher",
        ["CreateAppContainerProfile"] = "Seekbeam.Core.Storage.SandboxProvisioning",
        ["DeleteAppContainerProfile"] = "Seekbeam.Core.Storage.SandboxProvisioning",
    };

    private static readonly string[] ForbiddenNative =
    [
        "WriteFile", "WriteFileEx", "DeleteFile", "MoveFile", "CopyFile", "ReplaceFile", "SetFileAttributes", "SetFileTime", "SetFileInformationByHandle",
        "SetEndOfFile", "CreateDirectory", "RemoveDirectory", "CreateHardLink", "CreateSymbolicLink", "SetFileShortName", "DeviceIoControl",
        "NtWriteFile", "NtSetInformationFile", "NtCreateFile", "NtOpenFile", "NtDeleteFile", "NtFsControlFile", "SetFileSecurity", "SetNamedSecurityInfo",
        "SetSecurityInfo", "EncryptFile", "DecryptFile", "SHFileOperation", "IFileOperation", "MoveFileWithProgress", "CopyFile2",
    ];

    private static List<IlAnalyzer.Result> AnalyzeAll() => ShippedAssemblies().Select(IlAnalyzer.Analyze).ToList();

    [Fact]
    public void WriteApisAreOnlyCalledFromSeekbeamStorage()
    {
        var all = AnalyzeAll();
        var writes = all.SelectMany(r => r.Calls).Where(IsWriteApi).ToList();
        var violations = writes.Where(c => !c.CallerType.StartsWith("Seekbeam.Core.Storage.") && !IsAllowedException(c)).ToList();
        foreach (var v in violations) output.WriteLine("VIOLATION " + v);
        Assert.Empty(violations);
        Assert.NotEmpty(writes); // sanity: the analyzer does see SeekbeamStorage's writes
        Assert.All(writes.Where(w => w.CallerType.StartsWith("Seekbeam.Core.Storage.")), w => Assert.Equal("Seekbeam.Core", w.Assembly));
    }

    [Fact]
    public void EveryPInvokeIsReviewedAndNoneWritesFiles()
    {
        var all = AnalyzeAll();
        var pinvokes = all.SelectMany(r => r.PInvokes).ToList();
        foreach (var p in pinvokes)
        {
            string key = p.Module.ToLowerInvariant().Replace(".dll", "") + ".dll!" + p.EntryPoint;
            Assert.True(AllowedPInvokes.Contains(key), "Unreviewed P/Invoke: " + p);
            Assert.DoesNotContain(ForbiddenNative, f => p.EntryPoint.StartsWith(f, StringComparison.OrdinalIgnoreCase));
            if (PinnedDeclaringType.TryGetValue(p.EntryPoint, out var owner)) Assert.Equal(owner, p.DeclaringType);
        }
        // The sandbox's source handle is only ever duplicated with FILE_GENERIC_READ, from SourceFileAccess.
        var dupCallers = all.SelectMany(r => r.Calls).Where(c => c.CalleeType == "Seekbeam.Core.SourceAccess.SourceNative" && c.CalleeMember == "DuplicateHandle").ToList();
        Assert.NotEmpty(dupCallers);
        Assert.All(dupCallers, c => Assert.Equal("Seekbeam.Core.SourceAccess.SourceFileAccess", c.CallerType));
        // CreateFileW is only reachable through SourceNative.Open (single call site with read-only access masks).
        var createFileCallers = all.SelectMany(r => r.Calls).Where(c => c.CalleeType == "Seekbeam.Core.SourceAccess.SourceNative" && c.CalleeMember == "CreateFileW").ToList();
        Assert.All(createFileCallers, c => Assert.Equal("Open", c.CallerMethod));
    }

    [Fact]
    public void NoSqlWritesOtherDatabaseFiles()
    {
        var all = AnalyzeAll();
        var bad = all.SelectMany(r => r.Strings).Where(s =>
            s.Value.Contains("ATTACH ", StringComparison.OrdinalIgnoreCase) && s.Value.Contains("DATABASE", StringComparison.OrdinalIgnoreCase)
            || s.Value.Contains("VACUUM INTO", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void ExtractorsAndHostContainNoWriteApiAtAll()
    {
        foreach (var path in ShippedAssemblies().Where(p => p.Contains("Extraction") || p.Contains("ExtractorHost")))
        {
            var r = IlAnalyzer.Analyze(path);
            Assert.Empty(r.Calls.Where(IsWriteApi));
            Assert.True(r.Methods > 50 || path.Contains("ExtractorHost"));
        }
    }

    [Fact]
    public void WriteInventoryReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IL inventory (generated by StaticAnalysisTests)").AppendLine();
        sb.AppendLine($"Generated: {DateTime.UtcNow:u}").AppendLine();
        foreach (var path in ShippedAssemblies())
        {
            var r = IlAnalyzer.Analyze(path);
            sb.AppendLine($"## {Path.GetFileName(path)}").AppendLine();
            sb.AppendLine($"- Method bodies decoded: {r.Methods}, call sites: {r.Calls.Count}, string literals: {r.Strings.Count}, P/Invokes: {r.PInvokes.Count}").AppendLine();
            var w = r.Calls.Where(IsWriteApi).ToList();
            sb.AppendLine($"### File-system write API call sites ({w.Count})").AppendLine();
            foreach (var g in w.GroupBy(c => $"{c.CallerType}::{c.CallerMethod} → {c.CalleeType}::{c.CalleeMember}").OrderBy(g => g.Key))
                sb.AppendLine($"- `{g.Key}`" + (g.Count() > 1 ? $" ×{g.Count()}" : "") + (IsAllowedException(g.First()) ? " *(reviewed exception)*" : ""));
            sb.AppendLine().AppendLine($"### P/Invoke declarations ({r.PInvokes.Count})").AppendLine();
            foreach (var p in r.PInvokes.OrderBy(p => p.Module).ThenBy(p => p.EntryPoint)) sb.AppendLine($"- `{p.Module}!{p.EntryPoint}` ({p.DeclaringType})");
            sb.AppendLine();
        }
        // Third-party dependencies: report (not enforce) their write-capable code paths.
        sb.AppendLine("## Third-party dependencies (informative)").AppendLine();
        string bin = AppContext.BaseDirectory;
        foreach (var dep in new[] { "UglyToad.PdfPig.dll", "UglyToad.PdfPig.Core.dll", "UglyToad.PdfPig.Fonts.dll", "UglyToad.PdfPig.Tokenization.dll", "UglyToad.PdfPig.Tokens.dll", "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.batteries_v2.dll", "SQLitePCLRaw.provider.e_sqlite3.dll" })
        {
            string p = Path.Combine(bin, dep);
            if (!File.Exists(p)) continue;
            var r = IlAnalyzer.Analyze(p);
            var w = r.Calls.Where(IsWriteApi).ToList();
            sb.AppendLine($"- **{dep}**: {r.Methods} methods, {w.Count} write-API call sites" + (w.Count > 0 ? ": " + string.Join("; ", w.Select(c => $"{c.CallerType}::{c.CallerMethod} → {c.CalleeType}::{c.CalleeMember}").Distinct().Take(12)) : "")
                + $"; P/Invokes: {r.PInvokes.Count}");
        }
        string outDir = Path.Combine(RepoRoot, "artifacts", "audit");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "il-inventory.md"), sb.ToString());
        output.WriteLine(sb.ToString());
    }
}
