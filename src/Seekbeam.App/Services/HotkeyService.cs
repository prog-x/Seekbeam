using System.Runtime.InteropServices;
using System.Windows.Interop;
using Seekbeam.App.Native;
using Seekbeam.Core.Config;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Input;

namespace Seekbeam.App.Services;

/// <summary>
/// Global activation. Double Ctrl via a low-level keyboard hook running on its own thread and message loop (the UI
/// thread can never delay it, so Windows never unhooks it), events are never swallowed. Custom combinations via
/// RegisterHotKey on a message-only window. The hook health is checked and the hook reinstalled if Windows removed it.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly DoubleTapDetector _detector = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hook;
    private Win32.LowLevelProc? _proc; // keep delegate alive
    private HwndSource? _msgWindow;
    private long _lastHookEventTick;
    private Timer? _health;
    private volatile bool _doubleCtrlEnabled;
    private readonly List<int> _registered = [];
    private HotkeyConfig? _cfg;
    private volatile bool _suspended;

    public event Action? LauncherRequested;
    public event Action? DeepSearchRequested;
    /// <summary>Raised on the UI thread when an alternative hotkey could not be registered (conflict).</summary>
    public event Action<string>? RegistrationFailed;

    public void Configure(HotkeyConfig cfg)
    {
        _cfg = cfg;
        if (_suspended) return; // applied by Resume()
        _detector.DoubleTapMs = cfg.DoubleTapMs;
        _detector.Side = cfg.CtrlSide;
        _doubleCtrlEnabled = cfg.DoubleCtrlEnabled;
        if (cfg.DoubleCtrlEnabled) StartHook(); else StopHook();
        _msgWindow ??= new HwndSource(new HwndSourceParameters("Seekbeam.Hotkeys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _msgWindow.RemoveHook(WndProc);
        _msgWindow.AddHook(WndProc);
        foreach (int id in _registered) Win32.UnregisterHotKey(_msgWindow.Handle, id);
        _registered.Clear();
        Register(1, cfg.AlternativeHotkey);
        Register(2, cfg.DeepSearchHotkey);
    }

    /// <summary>
    /// Releases every global activation while the user captures a new combination in the settings, so that pressing
    /// the current one is captured instead of opening the launcher, and so that <see cref="IsAvailable"/> is not fooled
    /// by Seekbeam's own registrations.
    /// </summary>
    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        if (_msgWindow != null) { foreach (int id in _registered) Win32.UnregisterHotKey(_msgWindow.Handle, id); }
        _registered.Clear();
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        if (_cfg != null) Configure(_cfg);
    }

    /// <summary>True if no other application (nor Windows) holds this combination. Exact only while suspended.</summary>
    public static bool IsAvailable(uint modifiers, uint vk)
    {
        const int probeId = 0xBF00;
        if (!Win32.RegisterHotKey(IntPtr.Zero, probeId, modifiers | HotkeyParser.MOD_NOREPEAT, vk)) return false;
        Win32.UnregisterHotKey(IntPtr.Zero, probeId);
        return true;
    }

    private void Register(int id, string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return;
        if (!HotkeyParser.TryParse(hotkey, out uint mods, out uint vk)) { RegistrationFailed?.Invoke(Localization.L.F("Hotkey_Invalid", hotkey)); return; }
        if (Win32.RegisterHotKey(_msgWindow!.Handle, id, mods | HotkeyParser.MOD_NOREPEAT, vk)) _registered.Add(id);
        else
        {
            Log.Warn("Hotkey", $"RegisterHotKey failed for {hotkey} (error {Marshal.GetLastPInvokeError()})");
            RegistrationFailed?.Invoke(Localization.L.F("Hotkey_InUse", hotkey));
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            if (wParam == 1) LauncherRequested?.Invoke();
            else if (wParam == 2) DeepSearchRequested?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void StartHook()
    {
        if (_hookThread != null) return;
        var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            _hookThreadId = Win32.GetCurrentThreadId();
            _proc = HookProc;
            _hook = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandleW(null), 0);
            if (_hook == IntPtr.Zero) Log.Error("Hotkey", "SetWindowsHookEx failed: " + Marshal.GetLastPInvokeError());
            ready.Set();
            while (Win32.GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) { }
            if (_hook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { Name = "Seekbeam.KeyboardHook", IsBackground = true, Priority = ThreadPriority.Highest };
        _hookThread.Start();
        ready.Wait(2000);
        _lastHookEventTick = Environment.TickCount64;
        _health ??= new Timer(_ => CheckHealth(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void StopHook()
    {
        if (_hookThread == null) return;
        Win32.PostThreadMessageW(_hookThreadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(2000);
        _hookThread = null;
    }

    /// <summary>Windows silently removes slow LL hooks; if keyboard input happened but our hook saw none, reinstall it.</summary>
    private void CheckHealth()
    {
        if (!_doubleCtrlEnabled || _hookThread == null) return;
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return;
        long sinceInput = (uint)Environment.TickCount - lii.dwTime;
        long sinceHook = Environment.TickCount64 - Interlocked.Read(ref _lastHookEventTick);
        if (sinceInput < 5_000 && sinceHook > 120_000)
        {
            Log.Warn("Hotkey", "Keyboard hook appears inactive, reinstalling");
            StopHook();
            StartHook();
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            Interlocked.Exchange(ref _lastHookEventTick, Environment.TickCount64);
            int msg = (int)wParam;
            bool down = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
            bool up = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
            if ((down || up) && !_suspended && _detector.OnKey((int)k.vkCode, down, k.time))
                ThreadPool.UnsafeQueueUserWorkItem(_ => LauncherRequested?.Invoke(), null); // never block the hook
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public void Dispose()
    {
        _health?.Dispose();
        StopHook();
        if (_msgWindow != null) { foreach (int id in _registered) Win32.UnregisterHotKey(_msgWindow.Handle, id); _msgWindow.Dispose(); }
    }
}
