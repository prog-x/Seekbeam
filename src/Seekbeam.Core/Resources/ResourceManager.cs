using System.Runtime.InteropServices;
using Seekbeam.Core.Config;
using Seekbeam.Core.Diagnostics;

namespace Seekbeam.Core.Resources;

/// <summary>Effective budget for background work (ARCHITECTURE.md §9).</summary>
public sealed record ResourceProfile(
    string Name, int NamesPerSecond, int NameBatch, long ContentBytesPerSecond, int ContentBatch, int ContentWorkers,
    int HostCpuPercent, int HostMemoryMB, bool NamesAllowed, bool ContentAllowed, string Reason)
{
    public static ResourceProfile Economy => new("Economy", 2_000, 16, 4L << 20, 2, 1, 10, 256, true, true, "");
    public static ResourceProfile Balanced => new("Balanced", 20_000, 64, 20L << 20, 4, 1, 25, 512, true, true, "");
    public static ResourceProfile Performance => new("Performance", 0, 256, 0, 8, Math.Clamp(Environment.ProcessorCount / 4, 1, 4), 60, 1024, true, true, "");
}

public sealed record SystemSample(double CpuPercent, int MemoryLoadPercent, double UserIdleSeconds, bool OnBattery, bool FullscreenOrBusy, DateTime TimeUtc);

/// <summary>
/// Resource manager and work gate. Samples the system every 2 s and derives a profile from the configured mode
/// (Auto by default). Search always wins: while a search runs, background workers yield.
/// </summary>
public sealed partial class ResourceManager : IWorkGate, IDisposable
{
    private readonly Func<ResourceConfig> _config;
    private readonly Func<bool> _searchActive;
    private readonly Timer _timer;
    private volatile ResourceProfile _profile = ResourceProfile.Balanced;
    private volatile SystemSample _sample = new(0, 0, 0, false, false, DateTime.UtcNow);
    private readonly bool[] _userPaused = new bool[3];
    private DateTime? _pausedUntil;
    private readonly double[] _tokens = new double[3];
    private readonly DateTime[] _lastRefill = [DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow];
    private readonly object _lock = new();
    private long _prevIdle, _prevKernel, _prevUser;

    public event Action<ResourceProfile>? ProfileChanged;
    public ResourceProfile Profile => _profile;
    public SystemSample LastSample => _sample;
    public bool IsPaused(WorkClass cls) => _userPaused[(int)cls] || (_pausedUntil is { } u && DateTime.UtcNow < u);

    public ResourceManager(Func<ResourceConfig> config, Func<bool> searchActive)
    {
        _config = config;
        _searchActive = searchActive;
        Sample();
        _timer = new Timer(_ => { try { Sample(); } catch (Exception ex) { Log.Warn("Resources", ex.Message); } }, null, 2000, 2000);
    }

    public void Pause(WorkClass? cls = null, TimeSpan? duration = null)
    {
        if (duration is { } d) { _pausedUntil = DateTime.UtcNow + d; return; }
        if (cls is { } c) _userPaused[(int)c] = true;
        else for (int i = 0; i < 3; i++) _userPaused[i] = true;
    }

    public void Resume(WorkClass? cls = null)
    {
        _pausedUntil = null;
        if (cls is { } c) _userPaused[(int)c] = false;
        else for (int i = 0; i < 3; i++) _userPaused[i] = false;
    }

    public int BatchSize(WorkClass cls) => cls switch { WorkClass.Names => _profile.NameBatch, WorkClass.Content => _profile.ContentBatch, _ => 8 };

    public void WaitTurn(WorkClass cls, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var p = _profile;
            bool allowed = !IsPaused(cls) && (cls == WorkClass.Names ? p.NamesAllowed : p.ContentAllowed);
            if (!allowed) { ct.WaitHandle.WaitOne(500); continue; }
            if (_searchActive()) { ct.WaitHandle.WaitOne(30); continue; } // interactive search first
            double wait = 0;
            lock (_lock)
            {
                Refill(cls, p);
                if (_tokens[(int)cls] < 0)
                {
                    double rate = Rate(cls, p);
                    wait = rate <= 0 ? 0 : -_tokens[(int)cls] / rate;
                }
            }
            if (wait <= 0) return;
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(wait, 1)));
        }
    }

    public void Report(WorkClass cls, int items, long bytes)
    {
        lock (_lock) _tokens[(int)cls] -= cls == WorkClass.Content ? bytes : items;
    }

    private static double Rate(WorkClass cls, ResourceProfile p) => cls switch
    {
        WorkClass.Names => p.NamesPerSecond,
        WorkClass.Content => p.ContentBytesPerSecond,
        _ => 0,
    };

    private void Refill(WorkClass cls, ResourceProfile p)
    {
        double rate = Rate(cls, p);
        var now = DateTime.UtcNow;
        if (rate <= 0) { _tokens[(int)cls] = 0; _lastRefill[(int)cls] = now; return; }
        double add = (now - _lastRefill[(int)cls]).TotalSeconds * rate;
        _tokens[(int)cls] = Math.Min(rate, _tokens[(int)cls] + add); // burst ≤ 1 s
        _lastRefill[(int)cls] = now;
    }

    private void Sample()
    {
        var s = Probe();
        _sample = s;
        var cfg = _config();
        var p = Decide(cfg, s, DateTime.Now);
        if (cfg.ContentWorkers > 0) p = p with { ContentWorkers = cfg.ContentWorkers };
        if (cfg.ExtractorMemoryMB > 0) p = p with { HostMemoryMB = cfg.ExtractorMemoryMB };
        var old = _profile;
        _profile = p;
        if (old.Name != p.Name || old.ContentAllowed != p.ContentAllowed || old.NamesAllowed != p.NamesAllowed || old.HostCpuPercent != p.HostCpuPercent || old.HostMemoryMB != p.HostMemoryMB || old.ContentWorkers != p.ContentWorkers)
        {
            Log.Info("Resources", $"Profile {p.Name} (names={(p.NamesAllowed ? "on" : "paused")}, content={(p.ContentAllowed ? "on" : "paused")}) {p.Reason}");
            ProfileChanged?.Invoke(p);
        }
    }

    /// <summary>Pure decision function (unit-tested).</summary>
    public static ResourceProfile Decide(ResourceConfig cfg, SystemSample s, DateTime localNow)
    {
        ResourceProfile p = cfg.Mode switch
        {
            ResourceMode.Economy => ResourceProfile.Economy,
            ResourceMode.Balanced => ResourceProfile.Balanced,
            ResourceMode.Performance => ResourceProfile.Performance,
            _ => ResourceProfile.Balanced with { Name = "Auto" },
        };
        if (cfg.Mode == ResourceMode.Auto)
        {
            if (cfg.PauseInFullscreen && s.FullscreenOrBusy)
                return p with { NamesAllowed = false, ContentAllowed = false, Reason = "fullscreen" };
            if (s.MemoryLoadPercent >= 92) p = ResourceProfile.Economy with { Name = "Auto", ContentAllowed = false, Reason = "memory" };
            else if (s.OnBattery && cfg.EconomyOnBattery) p = ResourceProfile.Economy with { Name = "Auto", ContentAllowed = !cfg.PauseContentOnBattery, Reason = "battery" };
            else if (s.CpuPercent >= 90) p = ResourceProfile.Economy with { Name = "Auto", ContentAllowed = false, Reason = "cpu" };
            else if (s.CpuPercent >= 70 || s.MemoryLoadPercent >= 85) p = ResourceProfile.Economy with { Name = "Auto", Reason = "busy" };
            else if (s.UserIdleSeconds >= cfg.UserIdleSeconds) p = ResourceProfile.Performance with { Name = "Auto", Reason = "idle" };
            else p = p with { Reason = "normal" };
        }
        if (cfg.ContentTimeWindows.Count > 0 && !InAnyWindow(cfg.ContentTimeWindows, localNow))
            p = p with { ContentAllowed = false, Reason = p.Reason + " ; hors plage horaire du contenu" };
        return p;
    }

    public static bool InAnyWindow(IEnumerable<string> windows, DateTime localNow)
    {
        var t = localNow.TimeOfDay;
        foreach (var w in windows)
        {
            var parts = w.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !TimeSpan.TryParse(parts[0], out var a) || !TimeSpan.TryParse(parts[1], out var b)) continue;
            if (a <= b ? t >= a && t < b : t >= a || t < b) return true;
        }
        return false;
    }

    private SystemSample Probe()
    {
        double cpu = 0;
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long di = idle - _prevIdle, dk = kernel - _prevKernel, du = user - _prevUser;
            if (_prevKernel != 0 && dk + du > 0) cpu = Math.Clamp(100.0 * (dk + du - di) / (dk + du), 0, 100);
            _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
        }
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        int memLoad = GlobalMemoryStatusEx(ref mem) ? (int)mem.dwMemoryLoad : 0;
        double idleSec = 0;
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (GetLastInputInfo(ref lii)) idleSec = Math.Max(0, (uint)Environment.TickCount - lii.dwTime) / 1000.0;
        bool battery = GetSystemPowerStatus(out var ps) && ps.ACLineStatus == 0;
        bool busy = SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4; // BUSY, D3D full screen, presentation
        return new SystemSample(cpu, memLoad, idleSec, battery, busy, DateTime.UtcNow);
    }

    public void Dispose() => _timer.Dispose();

    // ------------------------------------------------------------ interop
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO lii);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);
}
