// Starts Seekbeam.ExtractorHost.exe (a Seekbeam-owned binary) in the most restrictive sandbox available (TD-17):
//   1. AppContainer without any capability — no network, no access to user files, user registry or other processes;
//   2. Low integrity restricted token     — cannot write anywhere the user can, no privileges (can still read and use the network);
//   3. none (normal token)                — only if both failed; logged as a warning and shown in the dashboard.
// "Less privileged" AppContainers (LPAC) were evaluated and rejected: .NET cannot initialise TimeZoneInfo there, which
// breaks every ZIP-based format, while the probe tests show no difference in what is blocked (TD-17).
// In every case: created suspended, placed in the Job Object before its first instruction, no child processes,
// only the two stdio pipe handles inherited, exploit mitigations on. Source files are never opened by the sandbox:
// Seekbeam opens them read-only and duplicates a read-only handle into it (SourceFileAccess.DuplicateReadHandleInto).
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Seekbeam.Core.Content;

/// <summary>Isolation level actually obtained by the extractor host (reported by the host itself after start).</summary>
public enum SandboxLevel : byte { None = 0, LowIntegrity = 1, AppContainer = 2 }


/// <summary>A started (suspended, then resumed) sandboxed child process with its stdio pipes.</summary>
internal sealed class SandboxedProcess : IDisposable
{
    public required SafeProcessHandle Process { get; init; }
    public required int ProcessId { get; init; }
    public required Stream StdIn { get; init; }
    public required Stream StdOut { get; init; }
    public required SandboxLevel RequestedLevel { get; init; }

    public bool HasExited => SandboxLauncher.WaitForSingleObject(Process, 0) == 0;

    /// <summary>Exit code once exited (e.g. 0xC0000135 = a DLL could not be loaded, 0xC0000022 = access denied), else null.</summary>
    public uint? ExitCode => HasExited && SandboxLauncher.GetExitCodeProcess(Process, out uint c) ? c : null;

    public void Kill()
    {
        if (!Process.IsInvalid && !Process.IsClosed && !HasExited)
        {
            SandboxLauncher.TerminateProcess(Process, 1);
            SandboxLauncher.WaitForSingleObject(Process, 2000);
        }
    }

    public long PrivateBytes
    {
        get
        {
            var c = new SandboxLauncher.PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<SandboxLauncher.PROCESS_MEMORY_COUNTERS_EX>() };
            return SandboxLauncher.K32GetProcessMemoryInfo(Process, ref c, c.cb) ? (long)c.PrivateUsage : 0;
        }
    }

    public void Dispose()
    {
        try { StdIn.Dispose(); } catch (Exception) { }
        try { StdOut.Dispose(); } catch (Exception) { }
        Process.Dispose();
    }
}

internal static partial class SandboxLauncher
{
    /// <summary>AppContainer name; its SID is derived from it. The profile is created once by SandboxProvisioning.</summary>
    public const string AppContainerName = Storage.SandboxProvisioning.AppContainerName;

    /// <summary>
    /// Starts <paramref name="exePath"/> at <paramref name="level"/>. The process is assigned to <paramref name="job"/>
    /// while suspended, then resumed. Throws <see cref="System.ComponentModel.Win32Exception"/> on failure.
    /// </summary>
    public static SandboxedProcess Start(string exePath, SandboxLevel level, IntPtr job)
    {
        var toChild = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromChild = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        IntPtr attrList = IntPtr.Zero, handleArray = IntPtr.Zero, sid = IntPtr.Zero, secCaps = IntPtr.Zero, values = IntPtr.Zero;
        IntPtr token = IntPtr.Zero, envBlock = IntPtr.Zero;
        bool ok = false;
        try
        {
            IntPtr childIn = toChild.ClientSafePipeHandle.DangerousGetHandle();
            IntPtr childOut = fromChild.ClientSafePipeHandle.DangerousGetHandle();

            int attrCount = 3 + (level == SandboxLevel.AppContainer ? 1 : 0);
            nuint size = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, attrCount, 0, ref size);
            attrList = Marshal.AllocHGlobal((nint)size);
            if (!InitializeProcThreadAttributeList(attrList, attrCount, 0, ref size)) throw Win32("InitializeProcThreadAttributeList");

            // Values must stay alive until DeleteProcThreadAttributeList: one unmanaged block holds them.
            values = Marshal.AllocHGlobal(64);
            // Only the two pipe ends are inherited — never any other inheritable handle of Seekbeam.
            handleArray = Marshal.AllocHGlobal(2 * IntPtr.Size);
            Marshal.WriteIntPtr(handleArray, 0, childIn);
            Marshal.WriteIntPtr(handleArray, IntPtr.Size, childOut);
            Update(attrList, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleArray, 2 * IntPtr.Size);
            // No child process may ever be created by the host.
            Marshal.WriteInt32(values, 0, PROCESS_CREATION_CHILD_PROCESS_RESTRICTED);
            Update(attrList, PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY, values, 4);
            // Exploit mitigations (all supported by Windows 10 1709+, compatible with the .NET runtime).
            Marshal.WriteInt64(values, 8, (long)Mitigations);
            Update(attrList, PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY, values + 8, 8);

            if (level == SandboxLevel.AppContainer)
            {
                int hr = DeriveAppContainerSidFromAppContainerName(AppContainerName, out sid);
                if (hr != 0) throw new System.ComponentModel.Win32Exception(hr, "DeriveAppContainerSidFromAppContainerName");
                secCaps = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
                // No capabilities at all: no network (internetClient…), no libraries, no devices.
                Marshal.StructureToPtr(new SECURITY_CAPABILITIES { AppContainerSid = sid }, secCaps, false);
                Update(attrList, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, secCaps, Marshal.SizeOf<SECURITY_CAPABILITIES>());
            }

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = childIn;
            si.StartupInfo.hStdOutput = childOut;
            si.StartupInfo.hStdError = IntPtr.Zero;
            si.lpAttributeList = attrList;

            envBlock = BuildEnvironment();
            uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | DETACHED_PROCESS | CREATE_UNICODE_ENVIRONMENT | BELOW_NORMAL_PRIORITY_CLASS;
            string cmd = "\"" + exePath + "\"";
            string? cwd = Path.GetDirectoryName(exePath);
            PROCESS_INFORMATION pi;
            bool created;
            if (level == SandboxLevel.LowIntegrity)
            {
                token = CreateLowIntegrityToken();
                created = CreateProcessAsUserW(token, exePath, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, true, flags, envBlock, cwd, ref si, out pi);
            }
            else created = CreateProcessW(exePath, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, true, flags, envBlock, cwd, ref si, out pi);
            if (!created) throw Win32("CreateProcess");

            var process = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            try
            {
                if (job != IntPtr.Zero && !AssignProcessToJobObject(job, pi.hProcess)) throw Win32("AssignProcessToJobObject");
                if (ResumeThread(pi.hThread) == uint.MaxValue) throw Win32("ResumeThread");
            }
            catch
            {
                TerminateProcess(process, 1);
                process.Dispose();
                throw;
            }
            finally { CloseHandle(pi.hThread); }

            toChild.DisposeLocalCopyOfClientHandle();
            fromChild.DisposeLocalCopyOfClientHandle();
            ok = true;
            return new SandboxedProcess { Process = process, ProcessId = (int)pi.dwProcessId, StdIn = toChild, StdOut = fromChild, RequestedLevel = level };
        }
        finally
        {
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (values != IntPtr.Zero) Marshal.FreeHGlobal(values);
            if (secCaps != IntPtr.Zero) Marshal.FreeHGlobal(secCaps);
            if (sid != IntPtr.Zero) FreeSid(sid);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
            if (!ok) { toChild.Dispose(); fromChild.Dispose(); }
        }
    }

    private const ulong Mitigations =
        (1UL << 12)    // HEAP_TERMINATE_ALWAYS_ON
        | (1UL << 16)  // BOTTOM_UP_ASLR_ALWAYS_ON
        | (1UL << 20)  // HIGH_ENTROPY_ASLR_ALWAYS_ON
        | (1UL << 32)  // EXTENSION_POINT_DISABLE_ALWAYS_ON (no AppInit DLLs, IMEs, hooks injected)
        | (1UL << 52)  // IMAGE_LOAD_NO_REMOTE_ALWAYS_ON (no DLL from network shares)
        | (1UL << 56); // IMAGE_LOAD_NO_LOW_LABEL_ALWAYS_ON (no DLL written by a low-integrity process)

    /// <summary>Restricted copy of the current token: all privileges removed, integrity lowered to Low (S-1-16-4096).</summary>
    private static IntPtr CreateLowIntegrityToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT, out var self))
            throw Win32("OpenProcessToken");
        IntPtr restricted = IntPtr.Zero, lowSid = IntPtr.Zero, label = IntPtr.Zero;
        try
        {
            if (!CreateRestrictedToken(self, DISABLE_MAX_PRIVILEGE | LUA_TOKEN, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted))
                throw Win32("CreateRestrictedToken");
            if (!ConvertStringSidToSidW("S-1-16-4096", out lowSid)) throw Win32("ConvertStringSidToSid");
            int labelSize = Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + GetLengthSid(lowSid);
            label = Marshal.AllocHGlobal(labelSize);
            Marshal.StructureToPtr(new TOKEN_MANDATORY_LABEL { Sid = lowSid, Attributes = SE_GROUP_INTEGRITY }, label, false);
            if (!SetTokenInformation(restricted, TokenIntegrityLevel, label, labelSize)) throw Win32("SetTokenInformation(IntegrityLevel)");
            var r = restricted;
            restricted = IntPtr.Zero;
            return r;
        }
        finally
        {
            CloseHandle(self);
            if (restricted != IntPtr.Zero) CloseHandle(restricted);
            if (lowSid != IntPtr.Zero) LocalFree(lowSid);
            if (label != IntPtr.Zero) Marshal.FreeHGlobal(label);
        }
    }

    /// <summary>Current environment plus DOTNET_EnableDiagnostics=0 (no diagnostic IPC endpoint in the sandbox).</summary>
    private static IntPtr BuildEnvironment()
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v && k.Length > 0) vars[k] = v;
        vars["DOTNET_EnableDiagnostics"] = "0";
        var sb = new StringBuilder();
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private static void Update(IntPtr list, nint attribute, IntPtr value, int size)
    {
        if (!UpdateProcThreadAttribute(list, 0, attribute, value, (nuint)size, IntPtr.Zero, IntPtr.Zero))
            throw Win32("UpdateProcThreadAttribute(0x" + attribute.ToString("X") + ")");
    }

    private static System.ComponentModel.Win32Exception Win32(string what)
    {
        int err = Marshal.GetLastPInvokeError();
        return new System.ComponentModel.Win32Exception(err, what + " failed (error " + err + ")");
    }

    // ---------------------------------------------------------------- interop
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_SUSPENDED = 0x4, DETACHED_PROCESS = 0x8,
        CREATE_UNICODE_ENVIRONMENT = 0x400, BELOW_NORMAL_PRIORITY_CLASS = 0x4000;
    private const int STARTF_USESTDHANDLES = 0x100;
    private const nint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002, PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY = 0x20007,
        PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = 0x20009, PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY = 0x2000E;
    private const int PROCESS_CREATION_CHILD_PROCESS_RESTRICTED = 1;
    private const uint TOKEN_DUPLICATE = 0x2, TOKEN_ASSIGN_PRIMARY = 0x1, TOKEN_QUERY = 0x8, TOKEN_ADJUST_DEFAULT = 0x80;
    private const uint DISABLE_MAX_PRIVILEGE = 0x1, LUA_TOKEN = 0x4, SE_GROUP_INTEGRITY = 0x20;
    private const int TokenIntegrityLevel = 25;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public uint dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_CAPABILITIES { public IntPtr AppContainerSid; public IntPtr Capabilities; public uint CapabilityCount; public uint Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL { public IntPtr Sid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage,
            QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(IntPtr list, uint flags, nint attribute, IntPtr value, nuint size, IntPtr prev, IntPtr retSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd,
        ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env,
        string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(IntPtr thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WaitForSingleObject(SafeProcessHandle handle, uint ms);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool K32GetProcessMemoryInfo(SafeProcessHandle process, ref PROCESS_MEMORY_COUNTERS_EX counters, uint cb);

    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);

    [LibraryImport("advapi32.dll")]
    private static partial IntPtr FreeSid(IntPtr sid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateRestrictedToken(IntPtr token, uint flags, uint disableSidCount, IntPtr sidsToDisable, uint deletePrivilegeCount,
        IntPtr privilegesToDelete, uint restrictedSidCount, IntPtr sidsToRestrict, out IntPtr newToken);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSidToSidW(string sid, out IntPtr psid);

    [LibraryImport("advapi32.dll")]
    private static partial int GetLengthSid(IntPtr sid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length);
}
