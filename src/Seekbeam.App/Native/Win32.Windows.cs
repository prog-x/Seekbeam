using System.Runtime.InteropServices;


namespace Seekbeam.App.Native;

/// <summary>
/// Win32 used to recognise and drive Open/Save/folder dialogs and Explorer windows (TD-20): window enumeration and
/// classes, control text through WM_GETTEXT / WM_SETTEXT (sent with a timeout, never to a hung window), the focused
/// control of another thread, key translation, and the Enter key posted to the file-name box. None of these writes to
/// the file system; the dialog only navigates (FILE_IMMUTABILITY_POLICY §8).
/// </summary>
internal static partial class Win32
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassNameW(IntPtr hwnd, char* buf, int max);
    [LibraryImport("user32.dll")]
    public static partial int GetDlgCtrlID(IntPtr hwnd);
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);
    public const uint GA_PARENT = 1, GA_ROOT = 2;
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static unsafe partial IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    public static partial uint MapVirtualKeyW(uint code, uint mapType);

    public const uint WM_SETTEXT = 0x000C, WM_GETTEXT = 0x000D, WM_GETTEXTLENGTH = 0x000E, EM_SETSEL = 0x00B1, WM_CHAR = 0x0102;
    public const int VK_RETURN = 0x0D, VK_BACK = 0x08, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14,
        VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_G = 0x47;
    private const uint SMTO_ABORTIFHUNG = 0x0002, SMTO_BLOCK = 0x0001;
    public const uint LLKHF_INJECTED = 0x10;

    public static unsafe string ClassName(IntPtr hwnd)
    {
        char* buf = stackalloc char[128];
        int n = GetClassNameW(hwnd, buf, 128);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    /// <summary>Text of a control of another process (WM_GETTEXT, 500 ms at most); null if the window does not answer.</summary>
    public static unsafe string? GetText(IntPtr hwnd, int max = 2048)
    {
        if (SendMessageTimeoutW(hwnd, WM_GETTEXTLENGTH, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 500, out IntPtr len) == 0) return null;
        int n = (int)Math.Min(Math.Max(0, (long)len), max);
        char* buf = stackalloc char[n + 1];
        if (SendMessageTimeoutW(hwnd, WM_GETTEXT, n + 1, (IntPtr)buf, SMTO_ABORTIFHUNG | SMTO_BLOCK, 500, out IntPtr got) == 0) return null;
        return new string(buf, 0, (int)Math.Min((long)got, n));
    }

    /// <summary>Sets the text of a control of another process (WM_SETTEXT, 500 ms at most). False if refused or hung.</summary>
    public static unsafe bool SetText(IntPtr hwnd, string text)
    {
        fixed (char* p = text)
            return SendMessageTimeoutW(hwnd, WM_SETTEXT, 0, (IntPtr)p, SMTO_ABORTIFHUNG | SMTO_BLOCK, 500, out IntPtr ok) != 0 && ok != 0;
    }

    public static void SelectAllText(IntPtr edit) => PostMessageW(edit, EM_SETSEL, 0, -1);

    /// <summary>
    /// Gives the keyboard focus to <paramref name="control"/> of the dialog <paramref name="dialog"/> (WM_NEXTDLGCTL, handled
    /// by the dialog's own thread; 500 ms at most). Enter typed in the file-name box is only taken as "go" when it has the focus.
    /// </summary>
    public static unsafe bool FocusDialogControl(IntPtr dialog, IntPtr control) =>
        SendMessageTimeoutW(dialog, 0x0028 /* WM_NEXTDLGCTL */, control, 1, SMTO_ABORTIFHUNG | SMTO_BLOCK, 500, out _) != 0;

    /// <summary>Posts Enter (key down then up) to a control: asynchronous, so a modal error box of the target cannot block us.</summary>
    public static void PostEnter(IntPtr hwnd)
    {
        uint scan = MapVirtualKeyW(VK_RETURN, 0);
        PostMessageW(hwnd, WM_KEYDOWN, VK_RETURN, (IntPtr)(1 | (scan << 16)));
        PostMessageW(hwnd, WM_KEYUP, VK_RETURN, unchecked((IntPtr)(1 | (scan << 16) | (1u << 30) | (1u << 31))));
    }

    // ---------------------------------------------------------------- focus of another thread

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize; public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    public static IntPtr FocusedControl(IntPtr top)
    {
        uint tid = GetWindowThreadProcessId(top, out _);
        var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(tid, ref gi) ? gi.hwndFocus : IntPtr.Zero;
    }

    // ---------------------------------------------------------------- keys

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vk);
    [LibraryImport("user32.dll")]
    public static partial short GetKeyState(int vk);
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetKeyboardLayout(uint threadId);
    [LibraryImport("user32.dll")]
    private static unsafe partial int ToUnicodeEx(uint vk, uint scan, byte* state, char* buf, int bufLen, uint flags, IntPtr hkl);

    public static bool IsDown(int vk) => GetAsyncKeyState(vk) < 0;

    /// <summary>
    /// Character produced by a key in the layout of the window that has it, without touching the keyboard state (flag 4,
    /// Windows 10 1607+), so dead keys typed in the target are not disturbed. Null for dead keys and non-characters.
    /// </summary>
    public static unsafe char? KeyChar(uint vk, uint scan, IntPtr foreground)
    {
        byte* state = stackalloc byte[256];
        new Span<byte>(state, 256).Clear();
        if (IsDown(VK_SHIFT)) state[VK_SHIFT] = 0x80;
        if ((GetKeyState(VK_CAPITAL) & 1) != 0) state[VK_CAPITAL] = 0x01;
        IntPtr hkl = GetKeyboardLayout(GetWindowThreadProcessId(foreground, out _));
        char* buf = stackalloc char[8];
        int n = ToUnicodeEx(vk, scan, state, buf, 8, 4, hkl);
        return n == 1 ? buf[0] : null;
    }

    // ---------------------------------------------------------------- input replay (Esc gives the typed letters back)

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] private unsafe fixed byte _size[32]; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }
    [LibraryImport("user32.dll")]
    private static unsafe partial uint SendInput(uint count, INPUT* inputs, int size);
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    /// <summary>Types <paramref name="text"/> into the foreground window as Unicode characters (marked injected).</summary>
    public static unsafe void TypeText(string text)
    {
        if (text.Length == 0) return;
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[2 * i].type = INPUT_KEYBOARD;
            inputs[2 * i].u.ki = new KEYBDINPUT { wScan = text[i], dwFlags = KEYEVENTF_UNICODE };
            inputs[2 * i + 1].type = INPUT_KEYBOARD;
            inputs[2 * i + 1].u.ki = new KEYBDINPUT { wScan = text[i], dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
        }
        fixed (INPUT* p = inputs) SendInput((uint)inputs.Length, p, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Visible top-level windows in z-order (topmost first).</summary>
    public static List<IntPtr> TopLevelWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) => { if (IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    public static List<IntPtr> Children(IntPtr parent)
    {
        var list = new List<IntPtr>();
        EnumChildWindows(parent, (h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

}
