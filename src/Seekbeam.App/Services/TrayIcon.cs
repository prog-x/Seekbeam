using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Seekbeam.App.Native;

namespace Seekbeam.App.Services;

/// <summary>Notification-area icon (Shell_NotifyIcon) with a WPF context menu. No WinForms dependency.</summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 77;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_LBUTTONDBLCLK = 0x0203;
    private const int NIN_BALLOONTIMEOUT = 0x0404, NIN_BALLOONUSERCLICK = 0x0405;
    private Action? _balloonAction;
    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;
    private Win32.NOTIFYICONDATA _data;
    private readonly IntPtr _icon;
    private bool _added;
    public ContextMenu Menu { get; }
    public event Action? Clicked;

    public TrayIcon(string tooltip, ContextMenu menu)
    {
        Menu = menu;
        _window = new HwndSource(new HwndSourceParameters("Seekbeam.Tray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        _taskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");
        Win32.ExtractIconExW(Environment.ProcessPath ?? "", 0, out _, out _icon, 1);
        _data = new Win32.NOTIFYICONDATA
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATA>(), hWnd = _window.Handle, uID = 1,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP, uCallbackMessage = CallbackMessage, hIcon = _icon,
            szTip = tooltip, szInfo = "", szInfoTitle = "",
        };
        Add();
    }

    private void Add() { _added = Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref _data); }

    public void SetTooltip(string text)
    {
        _data.szTip = text.Length > 120 ? text[..120] : text;
        _data.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP;
        if (_added) Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref _data);
    }

    /// <param name="onClick">Optional action run when the user clicks the notification.</param>
    public void ShowBalloon(string title, string text, Action? onClick = null)
    {
        _balloonAction = onClick;
        var d = _data;
        d.uFlags = Win32.NIF_INFO;
        d.szInfoTitle = title.Length > 60 ? title[..60] : title;
        d.szInfo = text.Length > 250 ? text[..250] : text;
        d.dwInfoFlags = 1; // NIIF_INFO
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref d);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            int ev = (int)lParam & 0xFFFF;
            if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK) Clicked?.Invoke();
            else if (ev == NIN_BALLOONUSERCLICK) { var a = _balloonAction; _balloonAction = null; a?.Invoke(); }
            else if (ev == NIN_BALLOONTIMEOUT) _balloonAction = null;
            else if (ev == WM_RBUTTONUP)
            {
                Win32.SetForegroundWindow(_window.Handle);
                Menu.IsOpen = true;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated) Add(); // Explorer restarted
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added) Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref _data);
        if (_icon != IntPtr.Zero) Win32.DestroyIcon(_icon);
        _window.Dispose();
    }
}
