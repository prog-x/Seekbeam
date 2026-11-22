using System.Runtime.InteropServices;

namespace Seekbeam.Core.Config;

/// <summary>Windows known folders that <see cref="Environment.SpecialFolder"/> does not cover (read-only lookup).</summary>
public static partial class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's Downloads folder (follows redirection), or %USERPROFILE%\Downloads if Windows cannot tell.</summary>
    public static string Downloads
    {
        get
        {
            if (SHGetKnownFolderPath(in DownloadsId, 0, IntPtr.Zero, out IntPtr p) == 0 && p != IntPtr.Zero)
            {
                try { if (Marshal.PtrToStringUni(p) is { Length: > 0 } s) return s; }
                finally { CoTaskMemFree(p); }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    /// <summary>True if <paramref name="path"/> designates the Downloads folder (case-insensitive, separators ignored).</summary>
    public static bool IsDownloads(string path)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Downloads)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(IntPtr p);
}
