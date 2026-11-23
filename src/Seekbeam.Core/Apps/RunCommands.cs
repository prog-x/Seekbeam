using Microsoft.Win32;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.SourceAccess;

namespace Seekbeam.Core.Apps;

/// <summary>
/// Discovers the commands the Windows Run dialog (Win+R) understands, so that typing "mstsc", "regedit",
/// "services.msc" or "appwiz.cpl" in the launcher works the same way:
/// <list type="bullet">
/// <item>files of the PATH folders (machine then user PATH, read from the registry so that a change is seen without
/// restarting Seekbeam), not recursive, with the extensions of <see cref="Extensions"/>;</item>
/// <item>App Paths registrations (HKCU then HKLM <c>SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths</c>).</item>
/// </list>
/// Name resolution follows cmd.exe: PATH order first, then extension order (.com, .exe, .bat, .cmd) within a folder.
/// .com/.exe/.bat/.cmd are named without their extension ("mstsc"), .msc/.cpl keep it ("services.msc"), as typed in
/// Win+R. Read-only: directory listing through <see cref="SourceFileAccess"/>, registry opened read-only.
/// </summary>
public static class RunCommands
{
    /// <summary>Extensions picked up, in resolution order.</summary>
    public static readonly string[] Extensions = [".com", ".exe", ".bat", ".cmd", ".msc", ".cpl"];
    /// <summary>Extensions that can be omitted when typing the command.</summary>
    public static readonly string[] ImplicitExtensions = [".com", ".exe", ".bat", ".cmd"];
    /// <summary>Shortest prefix that lists commands (a single letter would match hundreds of them).</summary>
    public const int MinPrefixLength = 3;
    /// <summary>Safety cap per folder (System32 holds about 700 matching files).</summary>
    private const int MaxPerFolder = 5000;

    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    /// <summary>Command name of a file: without extension for .com/.exe/.bat/.cmd, unchanged otherwise. Null if not a command.</summary>
    public static string? CommandName(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (!Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;
        return ImplicitExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(fileName) : fileName;
    }

    /// <summary>The PATH folders, machine first then user, expanded, de-duplicated, existing only.</summary>
    public static List<string> PathFolders()
    {
        string? machine = ReadPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        string? user = ReadPath(Registry.CurrentUser, "Environment");
        string raw = machine == null && user == null ? Environment.GetEnvironmentVariable("PATH") ?? "" : machine + ";" + user;
        return ParsePathList(raw).Where(SourceFileAccess.DirectoryExists).ToList();
    }

    /// <summary>Splits a PATH value: expands variables, removes quotes, empty and relative entries and duplicates.</summary>
    public static List<string> ParsePathList(string raw)
    {
        var res = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string p = Environment.ExpandEnvironmentVariables(part).Trim().Trim('"');
            if (p.Length == 0 || p.Contains('%') || !Path.IsPathFullyQualified(p)) continue;
            try { p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)); if (p.EndsWith(':')) p += "\\"; }
            catch (Exception) { continue; }
            if (seen.Add(p)) res.Add(p);
        }
        return res;
    }

    /// <summary>All run commands: PATH folders, then App Paths for names not already resolved.</summary>
    public static List<AppEntry> Scan(CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var res = new List<AppEntry>();
        foreach (var dir in PathFolders())
        {
            ct.ThrowIfCancellationRequested();
            AddFolder(dir, res, seen);
        }
        foreach (var (name, path) in AppPaths())
            if (seen.Add(name)) res.Add(AppEntry.Create(name, path, AppSource.RunCommand, path));
        return res;
    }

    /// <summary>Adds the commands of one folder (not recursive); names already in <paramref name="seen"/> are skipped.</summary>
    public static void AddFolder(string dir, List<AppEntry> into, HashSet<string> seen)
    {
        var files = new List<string>();
        using (var reader = SourceFileAccess.CreateDirectoryReader())
            reader.Read(dir, (in RawEntry e) =>
            {
                if (!e.IsDirectory && files.Count < MaxPerFolder && Extensions.Contains(Path.GetExtension(e.Name.ToString()), StringComparer.OrdinalIgnoreCase))
                    files.Add(e.Name.ToString());
            }, out _);
        // Within a folder, cmd.exe tries .com, .exe, .bat, .cmd in turn: "foo.exe" wins over "foo.cmd".
        foreach (var f in files.OrderBy(f => Array.FindIndex(Extensions, x => x.Equals(Path.GetExtension(f), StringComparison.OrdinalIgnoreCase))))
        {
            string? name = CommandName(f);
            if (name == null || !seen.Add(name)) continue;
            string full = Path.Combine(dir, f);
            into.Add(AppEntry.Create(name, full, AppSource.RunCommand, full));
        }
    }

    /// <summary>App Paths registrations (name without .exe, expanded executable path), user before machine.</summary>
    public static IEnumerable<(string Name, string Path)> AppPaths()
    {
        var res = new List<(string, string)>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var root = hive.OpenSubKey(AppPathsKey, writable: false);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub, writable: false);
                    if (k?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string v) continue;
                    string path = Environment.ExpandEnvironmentVariables(v).Trim().Trim('"');
                    if (!Path.IsPathFullyQualified(path) || !SourceFileAccess.FileExists(path)) continue;
                    string name = sub.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? sub[..^4] : sub;
                    if (name.Length > 0) res.Add((name, path));
                }
            }
            catch (Exception ex) { Log.Debug("Apps", "App Paths not readable: " + ex.Message); }
        }
        return res;
    }

    private static string? ReadPath(RegistryKey hive, string key)
    {
        try
        {
            using var k = hive.OpenSubKey(key, writable: false);
            return k?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception ex) { Log.Debug("Apps", "PATH not readable: " + ex.Message); return null; }
    }
}
