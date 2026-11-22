// Starts Seekbeam.ExtractorHost.exe, a Seekbeam-owned binary, in a sandbox (SandboxLauncher, TD-17) inside a Job Object
// with kernel-enforced RAM/CPU limits. Source files are opened read-only HERE and handed to the host as read-only handles.
using System.Runtime.InteropServices;
using Seekbeam.Core.Config;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.SourceAccess;

namespace Seekbeam.Core.Content;

/// <summary>
/// Out-of-process extraction: one long-lived child process (Seekbeam.ExtractorHost.exe), sandboxed (AppContainer when
/// possible, otherwise low integrity), placed in a Job Object (process memory limit, CPU hard cap, one process only,
/// no UI/clipboard access, kill-on-close). A timeout or crash kills and restarts the host; the faulty file is reported
/// as failed. Seekbeam itself can never be brought down — or its user's files reached — by a malformed document.
/// </summary>
public sealed partial class ExtractorClient : IExtractionService
{
    private static readonly SandboxLevel[] Strongest = [SandboxLevel.AppContainer, SandboxLevel.LowIntegrity, SandboxLevel.None];
    /// <summary>
    /// Strongest level worth trying: a level that failed is skipped by every host start for <see cref="CeilingRetry"/>
    /// (no repeated start-up cost), then tried again (the failure may have been transient, e.g. a slow boot).
    /// </summary>
    private static SandboxLevel s_ceiling = SandboxLevel.AppContainer;
    private static DateTime s_ceilingSince;
    private static readonly TimeSpan CeilingRetry = TimeSpan.FromMinutes(30);
    private static readonly object s_ceilingLock = new();
    /// <summary>Isolation level of the most recently started extractor host (dashboard).</summary>
    public static SandboxLevel LastLevel { get; private set; } = SandboxLevel.None;
    public static bool AnyHostStarted { get; private set; }
    /// <summary>Why the most recent stronger sandbox level could not be used (diagnostics), or null.</summary>
    public static string? LastFailure { get; private set; }

    private readonly string _hostPath;
    private readonly SandboxMode _mode;
    private readonly object _lock = new();
    private SandboxedProcess? _proc;
    private BinaryWriter? _w;
    private BinaryReader? _r;
    private IntPtr _job;
    private int _nextId;
    private int _memoryMB = 512, _cpuPercent = 25;
    private DateTime _lastUse = DateTime.UtcNow;
    private readonly Timer _idleTimer;
    public TimeSpan IdleShutdown { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int Restarts { get; private set; }
    public long HostPrivateBytes { get { try { var p = _proc; return p == null || p.HasExited ? 0 : p.PrivateBytes; } catch (Exception) { return 0; } } }
    public bool HostRunning { get { var p = _proc; return p != null && !p.HasExited; } }
    /// <summary>Isolation of the running host (verified from inside the host at start-up).</summary>
    public SandboxLevel Level { get; private set; }

    public ExtractorClient(string hostPath, SandboxMode mode = SandboxMode.Auto)
    {
        _hostPath = hostPath;
        _mode = mode;
        _idleTimer = new Timer(_ => { lock (_lock) { if (_proc != null && DateTime.UtcNow - _lastUse > IdleShutdown) StopHost(); } }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void SetLimits(int memoryMB, int cpuPercent)
    {
        lock (_lock)
        {
            if (memoryMB == _memoryMB && cpuPercent == _cpuPercent) return;
            _memoryMB = memoryMB; _cpuPercent = cpuPercent;
            if (_job != IntPtr.Zero) ApplyJobLimits(_job, _memoryMB, _cpuPercent);
        }
    }

    public ExtractReply Extract(ExtractRequest request, TimeSpan timeout, CancellationToken ct)
    {
        lock (_lock)
        {
            _lastUse = DateTime.UtcNow;
            try { EnsureHost(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ExtractReply { Status = ExtractStatus.HostCrashed, Error = "extractor host unavailable: " + ex.Message };
            }
            int id = ++_nextId;
            var proc = _proc!;
            ExtractRequest wire;
            // Seekbeam opens the source (read-only, SourceFileAccess) and hands the host a FILE_GENERIC_READ duplicate:
            // the sandboxed host cannot open any file by itself.
            using (var source = OpenSource(request.Path, out var failure))
            {
                if (source == null) return failure!;
                long remote;
                try { remote = SourceFileAccess.DuplicateReadHandleInto(source, proc.Process); }
                catch (SourceAccessException ex) { StopHost(); return new ExtractReply { Status = ExtractStatus.Error, Error = ex.Message }; }
                wire = new ExtractRequest
                {
                    SourceHandle = remote, Extension = Path.GetExtension(request.Path).TrimStart('.').ToLowerInvariant(),
                    MaxChars = request.MaxChars, MaxFileBytes = request.MaxFileBytes, ArchiveMaxDepth = request.ArchiveMaxDepth,
                    ArchiveMaxEntries = request.ArchiveMaxEntries, ArchiveMaxEntryBytes = request.ArchiveMaxEntryBytes,
                    ArchiveMaxTotalBytes = request.ArchiveMaxTotalBytes, ArchiveMaxRatio = request.ArchiveMaxRatio,
                    ArchivesEnabled = request.ArchivesEnabled, DisabledExtractors = request.DisabledExtractors, ExtraTextExtensions = request.ExtraTextExtensions,
                };
            }
            var task = Task.Run(() =>
            {
                ExtractorProtocol.WriteRequest(_w!, id, wire);
                return ExtractorProtocol.ReadReply(_r!);
            });
            bool done;
            try { done = task.Wait(timeout, ct); }
            catch (OperationCanceledException) { StopHost(); throw; }
            catch (AggregateException ex)
            {
                // Pipe broken: the host died (crash, out-of-memory kill by the Job Object…).
                StopHost();
                Restarts++;
                return new ExtractReply { Status = ExtractStatus.HostCrashed, Error = ex.InnerException?.GetType().Name ?? "host crashed" };
            }
            if (!done)
            {
                StopHost();
                Restarts++;
                return new ExtractReply { Status = ExtractStatus.Timeout, Error = $"timeout after {timeout.TotalSeconds:F0}s" };
            }
            var (rid, reply) = task.Result;
            if (rid != id) { StopHost(); return new ExtractReply { Status = ExtractStatus.Error, Error = "protocol desync" }; }
            return reply;
        }
    }

    private static ReadOnlySourceStream? OpenSource(string path, out ExtractReply? failure)
    {
        failure = null;
        try { return SourceFileAccess.OpenRead(path); }
        catch (SourceAccessException ex)
        {
            failure = new ExtractReply { Status = ExtractStatus.Error, Error = ex.IsNotFound ? "not found" : ex.Message };
            return null;
        }
    }

    private IEnumerable<SandboxLevel> Candidates()
    {
        SandboxLevel ceiling;
        lock (s_ceilingLock)
        {
            if (s_ceiling < SandboxLevel.AppContainer && DateTime.UtcNow - s_ceilingSince > CeilingRetry) s_ceiling = SandboxLevel.AppContainer;
            ceiling = s_ceiling;
        }
        return _mode switch
        {
            SandboxMode.Off => [SandboxLevel.None],
            SandboxMode.LowIntegrity => [SandboxLevel.LowIntegrity, SandboxLevel.None],
            SandboxMode.AppContainer => [SandboxLevel.AppContainer, SandboxLevel.LowIntegrity, SandboxLevel.None],
            _ => Strongest.Where(l => l <= ceiling),
        };
    }

    private void EnsureHost()
    {
        if (_proc != null && !_proc.HasExited) return;
        StopHost();
        Exception? last = null;
        foreach (var level in Candidates())
        {
            try
            {
                StartAt(level);
                bool reduced = (_mode is SandboxMode.Auto or SandboxMode.AppContainer && level < SandboxLevel.AppContainer)
                               || (_mode == SandboxMode.LowIntegrity && level == SandboxLevel.None);
                if (reduced)
                    Log.Warn("Extractor", $"Extractor host running with reduced isolation: {level} (stronger levels failed: {last?.Message})");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                StopHost();
                LastFailure = $"{level}: {ex.Message}";
                Log.Info("Extractor", $"Sandbox level {level} unavailable: {ex.Message}");
                if (_mode == SandboxMode.Auto || _mode == SandboxMode.AppContainer)
                    lock (s_ceilingLock) if (s_ceiling >= level && level != SandboxLevel.None) { s_ceiling = level - 1; s_ceilingSince = DateTime.UtcNow; }
            }
        }
        throw last ?? new InvalidOperationException("no sandbox level");
    }

    private void StartAt(SandboxLevel level)
    {
        _job = CreateConfiguredJob(_memoryMB, _cpuPercent);
        if (_job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject");
        _proc = SandboxLauncher.Start(_hostPath, level, _job);
        _w = new BinaryWriter(_proc.StdIn);
        _r = new BinaryReader(_proc.StdOut);
        // The host reports, from its own token, the isolation it actually runs under: trust but verify.
        var hello = Task.Run(() => ExtractorProtocol.ReadHello(_r));
        SandboxLevel observed;
        try
        {
            if (!hello.Wait(StartTimeout)) throw new TimeoutException("extractor host did not start");
            observed = hello.Result;
        }
        catch (AggregateException)
        {
            // The host died while starting (e.g. its files are not readable from the sandbox).
            SandboxLauncher.WaitForSingleObject(_proc.Process, 2000);
            throw new InvalidOperationException($"extractor host exited at start-up (code 0x{_proc.ExitCode ?? 0:X8})");
        }
        bool consistent = level switch
        {
            SandboxLevel.AppContainer => observed == SandboxLevel.AppContainer,
            SandboxLevel.LowIntegrity => observed == SandboxLevel.LowIntegrity,
            _ => true,
        };
        if (!consistent) throw new InvalidOperationException($"host reports {observed}, expected {level}");
        Level = level;
        LastLevel = level;
        AnyHostStarted = true;
        Log.Info("Extractor", $"Extractor host started (pid {_proc.ProcessId}, isolation {level}, {_memoryMB} MB, {_cpuPercent}% CPU)");
    }

    private void StopHost()
    {
        try { _w?.Dispose(); } catch (Exception) { }
        try { _proc?.Kill(); } catch (Exception) { }
        _proc?.Dispose();
        _proc = null; _w = null; _r = null;
        if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }
    }

    /// <summary>Job Object used for the extractor host (also used by the sandbox confinement tests).</summary>
    internal static IntPtr CreateConfiguredJob(int memoryMB, int cpuPercent)
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job != IntPtr.Zero) ApplyJobLimits(job, memoryMB, cpuPercent);
        return job;
    }

    internal static void CloseJob(IntPtr job) { if (job != IntPtr.Zero) CloseHandle(job); }

    private static void ApplyJobLimits(IntPtr job, int memoryMB, int cpuPercent)
    {
        var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        ext.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION
                                               | JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
        ext.BasicLimitInformation.ActiveProcessLimit = 1; // the host can never start another process
        ext.ProcessMemoryLimit = (UIntPtr)((ulong)Math.Max(64, memoryMB) * 1024 * 1024);
        if (!SetInformationJobObject(job, 9 /*JobObjectExtendedLimitInformation*/, ref ext, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            Log.Warn("Extractor", "Job memory limit failed: " + Marshal.GetLastPInvokeError());
        var cpu = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
            CpuRate = (uint)Math.Clamp(cpuPercent * 100, 100, 10000),
        };
        if (!SetInformationJobObjectCpu(job, 15 /*JobObjectCpuRateControlInformation*/, ref cpu, Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>()))
            Log.Warn("Extractor", "Job CPU cap failed: " + Marshal.GetLastPInvokeError());
        // No clipboard, no desktop switching, no global atoms, no system settings, no USER handles from outside the job.
        uint ui = 0xFF;
        if (!SetInformationJobObjectUi(job, 4 /*JobObjectBasicUIRestrictions*/, ref ui, 4))
            Log.Warn("Extractor", "Job UI restrictions failed: " + Marshal.GetLastPInvokeError());
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        lock (_lock) StopHost();
    }

    // ---------------------------------------------------------------- Job Object interop
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION { public uint ControlFlags; public uint CpuRate; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(IntPtr attrs, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObjectCpu(IntPtr job, int infoClass, ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION info, int size);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObjectUi(IntPtr job, int infoClass, ref uint info, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr h);
}
