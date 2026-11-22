using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Seekbeam.App.Native;
using Seekbeam.Core.Search;

namespace Seekbeam.App.Services;

/// <summary>
/// Icons for results, loaded on a dedicated STA thread and cached. Regular files use the per-extension icon obtained
/// with SHGFI_USEFILEATTRIBUTES (no disk access at all); only .exe/.lnk/.ico/.url read the item through the shell.
/// </summary>
public sealed class IconCache : IDisposable
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<(string Key, Func<ImageSource?> Load, TaskCompletionSource<ImageSource?> Tcs)> _queue = new(512);
    private readonly Thread _thread;
    private static readonly HashSet<string> PerFile = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".cpl", ".scr" };

    public IconCache()
    {
        _thread = new Thread(() =>
        {
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                ImageSource? img = null;
                try { img = job.Load(); } catch (Exception) { }
                _cache[job.Key] = img;
                job.Tcs.TrySetResult(img);
            }
        }) { IsBackground = true, Name = "Seekbeam.Icons" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public ImageSource? TryGetCached(SearchResult r) => _cache.TryGetValue(KeyOf(r), out var img) ? img : null;

    public Task<ImageSource?> GetAsync(SearchResult r)
    {
        string key = KeyOf(r);
        if (_cache.TryGetValue(key, out var img)) return Task.FromResult(img);
        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<ImageSource?> load = r.Kind switch
        {
            ResultKind.Folder => () => FromShellInfo("folder", 0x10, true),
            ResultKind.App when r.App?.AppUserModelId != null => () => FromShellItem(r.App.LaunchPath),
            ResultKind.App => () => FromShellInfo(r.App?.LaunchPath ?? r.FullPath, 0, false),
            _ when PerFile.Contains(System.IO.Path.GetExtension(r.FullPath)) => () => FromShellInfo(r.FullPath, 0, false),
            _ => () => FromShellInfo(System.IO.Path.GetExtension(r.FullPath) is { Length: > 0 } e ? e : ".file", 0x80, true),
        };
        if (!_queue.TryAdd((key, load, tcs))) tcs.TrySetResult(null);
        return tcs.Task;
    }

    private static string KeyOf(SearchResult r) => r.Kind switch
    {
        ResultKind.Folder => "<folder>",
        ResultKind.App => "app:" + (r.App?.AppUserModelId ?? r.App?.LaunchPath ?? r.FullPath),
        _ when PerFile.Contains(System.IO.Path.GetExtension(r.FullPath)) => r.FullPath,
        _ => "ext:" + System.IO.Path.GetExtension(r.FullPath),
    };

    private static ImageSource? FromShellInfo(string path, uint attrs, bool useAttributes)
    {
        var info = new Win32.SHFILEINFO();
        uint flags = Win32.SHGFI_ICON | Win32.SHGFI_LARGEICON | (useAttributes ? Win32.SHGFI_USEFILEATTRIBUTES : 0);
        Win32.SHGetFileInfoW(path, attrs, ref info, (uint)Marshal.SizeOf<Win32.SHFILEINFO>(), flags);
        if (info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { Win32.DestroyIcon(info.hIcon); }
    }

    private static ImageSource? FromShellItem(string parsingName)
    {
        var iid = typeof(Win32.IShellItemImageFactory).GUID;
        if (Win32.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out object obj) != 0) return null;
        var factory = (Win32.IShellItemImageFactory)obj;
        try
        {
            if (factory.GetImage(new Win32.SIZE { cx = 32, cy = 32 }, 0x0 /* SIIGBF_RESIZETOFIT */, out IntPtr hbm) != 0 || hbm == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { Win32.DeleteObject(hbm); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    public void Dispose() => _queue.CompleteAdding();
}
