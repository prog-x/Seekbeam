using System.Runtime.InteropServices;
using System.Windows;
using Seekbeam.App.Native;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Search;

namespace Seekbeam.App.Services;

/// <summary>
/// Result actions. FILE_IMMUTABILITY_POLICY §4/§8: none of them modifies a file. "Open", "Run as administrator",
/// "Open with" and "Properties" delegate to the Windows shell; the application that then opens the file may modify it,
/// which Seekbeam cannot control (documented limitation). Rename/move/delete are intentionally not offered.
/// </summary>
public static class ShellActions
{
    public static bool Open(SearchResult r) => r.Kind == ResultKind.App && r.App != null
        ? Execute(null, r.App.LaunchPath, null, r.App.WorkingDirectory)
        : Execute(null, r.FullPath, null, r.Directory);

    public static bool RunAsAdministrator(SearchResult r) =>
        Execute("runas", r.Kind == ResultKind.App && r.App != null ? r.App.LaunchPath : r.FullPath, null, r.Directory);

    public static bool OpenWith(SearchResult r) => Execute("openas", r.FullPath, null, null, invokeIdList: true);

    public static bool Properties(SearchResult r) =>
        Execute("properties", r.Kind == ResultKind.App && r.App?.Target is { } t ? t : r.FullPath, null, null, invokeIdList: true);

    /// <summary>Opens the containing folder in Explorer with the item selected.</summary>
    public static bool OpenContainingFolder(SearchResult r)
    {
        string? path = r.Kind == ResultKind.App ? r.App?.Target ?? r.App?.LaunchPath : r.FullPath;
        if (string.IsNullOrEmpty(path) || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return false;
        IntPtr pidl = Win32.ILCreateFromPathW(path);
        if (pidl == IntPtr.Zero) return Execute(null, System.IO.Path.GetDirectoryName(path) ?? path, null, null);
        try { return Win32.SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0) == 0; }
        finally { Win32.ILFree(pidl); }
    }

    /// <summary>
    /// Opens a page of the project site in the default browser ("About"). Anything else is refused, so this can
    /// never be pointed at an indexed file.
    /// </summary>
    public static bool OpenLink(string url)
    {
        if (!url.StartsWith(AppInfo.ProjectUrl + "/", StringComparison.OrdinalIgnoreCase) && url != AppInfo.ProjectUrl)
        {
            Log.Warn("Shell", "Refused link outside the project site");
            return false;
        }
        return Execute(null, url, null, null);
    }

    public static void CopyText(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(text, true); return; }
            catch (COMException) { Thread.Sleep(50); } // clipboard busy
        }
    }

    private static bool Execute(string? verb, string file, string? args, string? dir, bool invokeIdList = false)
    {
        var info = new Win32.SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<Win32.SHELLEXECUTEINFO>(), lpVerb = verb, lpFile = file, lpParameters = args,
            lpDirectory = string.IsNullOrEmpty(dir) || dir.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? null : dir,
            nShow = 1, fMask = (invokeIdList ? Win32.SEE_MASK_INVOKEIDLIST : 0) | Win32.SEE_MASK_NOASYNC,
        };
        bool ok = Win32.ShellExecuteExW(ref info);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 1223) Log.Warn("Shell", $"ShellExecuteEx({verb ?? "open"}) failed: {err}"); // 1223 = cancelled by user (UAC)
        }
        return ok;
    }
}
