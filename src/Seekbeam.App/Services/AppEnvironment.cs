using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Seekbeam.App.Native;
using Seekbeam.Core.Config;
using ThemeMode = Seekbeam.Core.Config.ThemeMode;
using Microsoft.Win32;

namespace Seekbeam.App.Services;

/// <summary>Theme (light/dark, follows Windows by default) and DWM window styling.</summary>
public static class Theme
{
    public static bool IsDark(ThemeMode mode)
    {
        if (mode == ThemeMode.Dark) return true;
        if (mode == ThemeMode.Light) return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    public static void Apply(ThemeMode mode)
    {
        bool dark = IsDark(mode);
        var r = Application.Current.Resources;
        void Set(string key, string color) => r[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) { };
        if (dark)
        {
            Set("BgBrush", "#FF202124"); Set("PanelBrush", "#FF2B2C30"); Set("FgBrush", "#FFF1F1F3"); Set("SubFgBrush", "#FF9AA0A6");
            Set("BorderBrush", "#FF3C4043"); Set("SelBrush", "#FF33415C"); Set("AccentBrush", "#FF8AB4F8"); Set("HighlightBrush", "#FFFFD54F");
            Set("InputBrush", "#FF2B2C30"); Set("OkBrush", "#FF5BD27A"); Set("WarnBrush", "#FFFFB74D");
        }
        else
        {
            Set("BgBrush", "#FFFFFFFF"); Set("PanelBrush", "#FFF5F6F8"); Set("FgBrush", "#FF1F2328"); Set("SubFgBrush", "#FF6B7280");
            Set("BorderBrush", "#FFD7DBE0"); Set("SelBrush", "#FFE3ECFD"); Set("AccentBrush", "#FF3B5BDB"); Set("HighlightBrush", "#FFB45309");
            Set("InputBrush", "#FFFFFFFF"); Set("OkBrush", "#FF1E8E3E"); Set("WarnBrush", "#FFC07800");
        }
        r["IsDarkTheme"] = dark;
    }

    /// <summary>Rounded corners (Windows 11) and dark title bar for a window.</summary>
    public static void StyleWindow(Window w, bool dark)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        int round = Win32.DWMWCP_ROUND;
        Win32.DwmSetWindowAttribute(h, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int d = dark ? 1 : 0;
        Win32.DwmSetWindowAttribute(h, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, sizeof(int));
    }
}

/// <summary>"Start with Windows": HKCU Run key (per-user, no elevation, no file written).</summary>
public static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Seekbeam";

    public static void Apply(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true) ?? Registry.CurrentUser.CreateSubKey(Key);
            if (enabled && Environment.ProcessPath is { } exe) k.SetValue(Name, $"\"{exe}\" --background");
            else if (k.GetValue(Name) != null) k.DeleteValue(Name, false);
        }
        catch (Exception ex) { Seekbeam.Core.Diagnostics.Log.Warn("Startup", ex.Message); }
    }
}
