using System.Runtime.InteropServices;
using Seekbeam.App.Native;
using Seekbeam.Core.Diagnostics;

namespace Seekbeam.App.Services;

/// <summary>An open Explorer folder: its top-level window, its tab (Windows 11 tabs) and its path.</summary>
internal sealed record ExplorerFolder(IntPtr Window, IntPtr Tab, string Path, bool ActiveTab);

/// <summary>
/// Explorer windows through the shell's documented automation (Shell.Application → IShellWindows): the folders they show
/// (Quick Switch) and navigation of one window. Read and navigate only. Must be called on an STA thread (the UI thread).
/// The tab of a window is found with IServiceProvider → IShellBrowser → GetWindow: the visible one is the active tab.
/// </summary>
internal static class ExplorerShell
{
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr ppv); }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser { [PreserveSig] int GetWindow(out IntPtr hwnd); }

    private static dynamic? Shell()
    {
        var t = Type.GetTypeFromProgID("Shell.Application");
        return t == null ? null : Activator.CreateInstance(t);
    }

    /// <summary>Open Explorer folders, front-most window first (z-order), the active tab of each window first.</summary>
    public static List<ExplorerFolder> OpenFolders()
    {
        var res = new List<ExplorerFolder>();
        dynamic? shell = null;
        try
        {
            shell = Shell();
            if (shell == null) return res;
            var items = new List<(IntPtr Window, IntPtr Tab, string Path)>();
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    IntPtr top = new((long)w.HWND);
                    if (Win32.ClassName(top) is not ("CabinetWClass" or "ExploreWClass")) continue;
                    string? path = FolderPath(w);
                    // Real folders only: Home, This PC, libraries, zip folders… report "::{GUID}" or shell names.
                    if (string.IsNullOrEmpty(path) || path.StartsWith("::", StringComparison.Ordinal) || !System.IO.Path.IsPathFullyQualified(path)) continue;
                    items.Add((top, TabOf(w), path));
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException) { }
                finally { Release(w); }
            }
            var z = Win32.TopLevelWindows();
            foreach (var (window, tab, path) in items.OrderBy(i => { int k = z.IndexOf(i.Window); return k < 0 ? int.MaxValue : k; })
                         .ThenByDescending(i => i.Tab != IntPtr.Zero && Win32.IsWindowVisible(i.Tab)))
                res.Add(new ExplorerFolder(window, tab, path, tab == IntPtr.Zero || Win32.IsWindowVisible(tab)));
        }
        catch (Exception ex) { Log.Debug("Explorer", "Shell windows: " + ex.Message); }
        finally { Release(shell); }
        return res;
    }

    /// <summary>Folder shown by the front-most Explorer window (its active tab), or null.</summary>
    public static string? FrontFolder(IntPtr except = default) =>
        OpenFolders().FirstOrDefault(f => f.ActiveTab && f.Window != except)?.Path;

    /// <summary>
    /// Navigates the active tab of the Explorer window <paramref name="window"/> to <paramref name="folder"/>, then, if given,
    /// selects <paramref name="selectName"/> in it. Returns false if the window is gone.
    /// </summary>
    public static async Task<bool> NavigateAsync(IntPtr window, string folder, string? selectName)
    {
        dynamic? shell = null, target = null;
        try
        {
            shell = Shell();
            if (shell == null) return false;
            foreach (dynamic w in shell.Windows())
            {
                bool keep = false;
                try
                {
                    if (new IntPtr((long)w.HWND) != window) continue;
                    IntPtr tab = TabOf(w);
                    if (target == null || tab == IntPtr.Zero || Win32.IsWindowVisible(tab))
                    {
                        if (target != null) Release(target);
                        target = w; keep = true;
                        if (tab == IntPtr.Zero || Win32.IsWindowVisible(tab)) break;
                    }
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                finally { if (!keep) Release(w); }
            }
            if (target == null) return false;
            target.Navigate2(folder);
            if (selectName == null) return true;
            // The view is replaced asynchronously: wait for the new folder, then select the item.
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(50);
                try
                {
                    if (!string.Equals(FolderPath(target), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(FolderPath(target), folder, StringComparison.OrdinalIgnoreCase)) continue;
                    dynamic doc = target.Document;
                    dynamic item = doc.Folder.ParseName(selectName);
                    if (item != null) doc.SelectItem(item, 1 | 4 | 8 | 16); // select, deselect others, ensure visible, focus
                    break;
                }
                catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            }
            return true;
        }
        catch (Exception ex) { Log.Warn("Explorer", "Navigation failed: " + ex.Message); return false; }
        finally { Release(target); Release(shell); }
    }

    private static string? FolderPath(dynamic w)
    {
        try { return (string?)w.Document?.Folder?.Self?.Path; }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
    }

    private static IntPtr TabOf(object w)
    {
        try
        {
            if (w is not IServiceProvider sp) return IntPtr.Zero;
            Guid sid = SID_STopLevelBrowser, iid = IID_IShellBrowser;
            if (sp.QueryService(ref sid, ref iid, out IntPtr p) != 0 || p == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(p);
                IntPtr hwnd = browser.GetWindow(out IntPtr h) == 0 ? h : IntPtr.Zero;
                Marshal.ReleaseComObject(browser);
                return hwnd;
            }
            finally { Marshal.Release(p); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return IntPtr.Zero; }
    }

    private static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }
}
