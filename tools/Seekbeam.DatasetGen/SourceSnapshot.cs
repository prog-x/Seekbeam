using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;

namespace Seekbeam.Tools;

/// <summary>State of one file-system object for immutability verification.</summary>
public sealed record SnapshotEntry(
    string Path, bool IsDirectory, FileAttributes Attributes, long Size, long CreationTime, long LastWriteTime, long LastAccessTime,
    string? Sha256, string? Sddl, string Streams, string? ReparseTarget);

public sealed record SnapshotDiff(string Path, string Field, string? Before, string? After)
{
    /// <summary>Last-access time updates are an OS side effect of reading (FILE_IMMUTABILITY_POLICY §8.1), not a Seekbeam write.</summary>
    public bool IsAccessTimeOnly => Field == "LastAccessTime";
    public override string ToString() => $"{Path}: {Field} {Before} -> {After}";
}

/// <summary>
/// Reference snapshot of a directory tree: SHA-256 of every file, size, attributes, creation/write/access times,
/// security descriptor (SDDL), alternate data streams and reparse targets. Reparse points are recorded, not followed.
/// </summary>
public static class SourceSnapshot
{
    public static Dictionary<string, SnapshotEntry> Take(string root)
    {
        var res = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(root);
        res[""] = Entry(root, "", true);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateFileSystemEntries(LongPath(dir)).ToList(); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var raw in children)
            {
                string full = raw.StartsWith(@"\\?\") ? raw[4..] : raw;
                string rel = Path.GetRelativePath(root, full);
                var attrs = File.GetAttributes(LongPath(full));
                bool isDir = (attrs & FileAttributes.Directory) != 0;
                res[rel] = Entry(full, rel, isDir);
                if (isDir && (attrs & FileAttributes.ReparsePoint) == 0) stack.Push(full);
            }
        }
        return res;
    }

    public static string LongPath(string p) => p.Length >= 240 && !p.StartsWith(@"\\?\") ? @"\\?\" + p : p;

    private static SnapshotEntry Entry(string full, string rel, bool isDir)
    {
        string lp = LongPath(full);
        string? hash = null, sddl = null;
        if (!isDir)
        {
            try
            {
                using var s = new FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                hash = Convert.ToHexString(SHA256.HashData(s));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { hash = "<unreadable:" + ex.GetType().Name + ">"; }
        }
        try
        {
            var sec = isDir ? (FileSystemSecurity)new DirectoryInfo(lp).GetAccessControl() : new FileInfo(lp).GetAccessControl();
            sddl = sec.GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException) { sddl = "<unreadable>"; }
        // Metadata is read after hashing so that the snapshot tool's own read is already reflected in LastAccessTime.
        FileSystemInfo fi = isDir ? new DirectoryInfo(lp) : new FileInfo(lp);
        fi.Refresh();
        return new SnapshotEntry(rel, isDir, fi.Attributes, isDir ? 0 : ((FileInfo)fi).Length,
            fi.CreationTimeUtc.ToFileTimeUtc(), fi.LastWriteTimeUtc.ToFileTimeUtc(), fi.LastAccessTimeUtc.ToFileTimeUtc(),
            hash, sddl, isDir ? "" : Streams(lp), fi.LinkTarget);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_STREAM_DATA { public long StreamSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string cStreamName; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string path, int level, out WIN32_FIND_STREAM_DATA data, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextStreamW(IntPtr h, out WIN32_FIND_STREAM_DATA data);
    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr h);

    /// <summary>Alternate data streams (name:size), e.g. Zone.Identifier — any change would be a modification.</summary>
    private static string Streams(string path)
    {
        var list = new List<string>();
        IntPtr h = FindFirstStreamW(path, 0, out var d, 0);
        if (h == new IntPtr(-1)) return "";
        try { do list.Add(d.cStreamName + "=" + d.StreamSize); while (FindNextStreamW(h, out d)); }
        finally { FindClose(h); }
        return string.Join(";", list);
    }

    public static List<SnapshotDiff> Compare(Dictionary<string, SnapshotEntry> before, Dictionary<string, SnapshotEntry> after, ISet<string>? ignorePaths = null)
    {
        var diffs = new List<SnapshotDiff>();
        foreach (var (path, b) in before)
        {
            if (ignorePaths?.Contains(path) == true) continue;
            if (!after.TryGetValue(path, out var a)) { diffs.Add(new SnapshotDiff(path, "Deleted", "exists", null)); continue; }
            void Cmp(string f, object? x, object? y) { if (!Equals(x, y)) diffs.Add(new SnapshotDiff(path, f, x?.ToString(), y?.ToString())); }
            Cmp("IsDirectory", b.IsDirectory, a.IsDirectory);
            Cmp("Attributes", b.Attributes, a.Attributes);
            Cmp("Size", b.Size, a.Size);
            Cmp("CreationTime", b.CreationTime, a.CreationTime);
            Cmp("LastWriteTime", b.LastWriteTime, a.LastWriteTime);
            Cmp("Sha256", b.Sha256, a.Sha256);
            Cmp("Sddl", b.Sddl, a.Sddl);
            Cmp("Streams", b.Streams, a.Streams);
            Cmp("ReparseTarget", b.ReparseTarget, a.ReparseTarget);
            Cmp("LastAccessTime", b.LastAccessTime, a.LastAccessTime);
        }
        foreach (var path in after.Keys)
            if (!before.ContainsKey(path) && ignorePaths?.Contains(path) != true) diffs.Add(new SnapshotDiff(path, "Created", null, "exists"));
        return diffs;
    }

    public static void Save(Dictionary<string, SnapshotEntry> s, string file) =>
        File.WriteAllText(file, JsonSerializer.Serialize(s.Values.OrderBy(e => e.Path).ToList(), new JsonSerializerOptions { WriteIndented = false }));

    public static Dictionary<string, SnapshotEntry> Load(string file) =>
        JsonSerializer.Deserialize<List<SnapshotEntry>>(File.ReadAllText(file))!.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
}
