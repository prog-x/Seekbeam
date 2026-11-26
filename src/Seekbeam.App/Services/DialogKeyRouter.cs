using System.Text;
using Seekbeam.App.Native;

namespace Seekbeam.App.Services;

/// <summary>
/// Keys typed in Open/Save/folder dialogs (TD-20), seen by the low-level keyboard hook:
/// <list type="bullet">
/// <item>a letter or digit typed while the dialog's FILE LIST has the focus opens the launcher in "go to folder" mode
/// with that character (never in a text box: file name, address, search, rename);</item>
/// <item>the characters typed right after it, before the launcher has the focus, are captured and handed to it, so fast
/// typing loses nothing and does not trigger the list's own type-ahead;</item>
/// <item>Ctrl+G jumps to the folder of the front-most Explorer window (Quick Switch).</item>
/// </list>
/// Runs on the hook thread: only window classes and ids are looked at (no message is sent), the dialog structure is
/// cached per window, and events are raised for the UI thread to handle asynchronously. Injected keys are never taken
/// (that is how Esc gives the letters back to the list).
/// </summary>
internal sealed class DialogKeyRouter
{
    private readonly object _lock = new();
    private readonly StringBuilder _buffer = new();
    private bool _capturing;
    private long _captureUntil;
    private IntPtr _captureWindow;
    private (IntPtr Hwnd, NavTarget? Target, long At) _cache;

    public volatile bool DirectTyping;
    public volatile bool QuickSwitch;
    public IntPtr LauncherHwnd { get; set; }
    public bool Active => DirectTyping || QuickSwitch;

    /// <summary>Raised on the hook thread: the user started typing in a dialog's file list.</summary>
    public event Action<NavTarget, string>? TypingStarted;
    /// <summary>Raised on the hook thread: Ctrl+G in a dialog.</summary>
    public event Action<NavTarget>? QuickSwitchRequested;

    /// <summary>Called for every key press that was not injected. True = the key is swallowed.</summary>
    public bool OnKeyDown(uint vk, uint scan)
    {
        if (!Active) return false;
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        bool ctrl = Win32.IsDown(Win32.VK_CONTROL), alt = Win32.IsDown(Win32.VK_MENU), win = Win32.IsDown(Win32.VK_LWIN) || Win32.IsDown(Win32.VK_RWIN);
        lock (_lock)
        {
            if (_capturing)
            {
                if (Environment.TickCount64 > _captureUntil || fg == LauncherHwnd || fg != _captureWindow) _capturing = false;
                else if (!ctrl && !alt && !win)
                {
                    if (vk == Win32.VK_BACK) { if (_buffer.Length > 0) _buffer.Length--; return true; }
                    if (Win32.KeyChar(vk, scan, fg) is char c && !char.IsControl(c)) { _buffer.Append(c); return true; }
                }
            }
        }
        if (fg == LauncherHwnd || win || (ctrl && alt)) return false; // Ctrl+Alt = AltGr: a character of the target
        bool quick = QuickSwitch && ctrl && !alt && vk == Win32.VK_G && !Win32.IsDown(Win32.VK_SHIFT);
        bool typing = DirectTyping && !ctrl && !alt && IsCharacterKey(vk);
        if (!quick && !typing) return false;
        var target = TargetOf(fg);
        if (target == null || !target.IsDialog || !target.CanNavigate) return false;
        if (quick) { QuickSwitchRequested?.Invoke(target); return true; }
        if (!NavigationTargets.IsFileListFocused(target.Hwnd)) return false;
        if (Win32.KeyChar(vk, scan, fg) is not char ch || !char.IsLetterOrDigit(ch)) return false;
        lock (_lock)
        {
            _capturing = true;
            _captureUntil = Environment.TickCount64 + 1500;
            _captureWindow = fg;
            _buffer.Clear();
        }
        TypingStarted?.Invoke(target, ch.ToString());
        return true;
    }

    /// <summary>Ends the capture (the launcher now has the focus) and returns what was typed meanwhile.</summary>
    public string EndCapture()
    {
        lock (_lock)
        {
            _capturing = false;
            string s = _buffer.ToString();
            _buffer.Clear();
            return s;
        }
    }

    /// <summary>Letters, digits, numeric keypad and the OEM keys (accented letters of AZERTY, QWERTZ… layouts).</summary>
    private static bool IsCharacterKey(uint vk) =>
        vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x69 or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

    private NavTarget? TargetOf(IntPtr fg)
    {
        var c = _cache;
        long now = Environment.TickCount64;
        if (c.Hwnd == fg && now - c.At < 2000) return c.Target;
        var t = NavigationTargets.Detect(fg, includeExplorer: false);
        _cache = (fg, t, now);
        return t;
    }
}
