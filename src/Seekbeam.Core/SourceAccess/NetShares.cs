using System.Runtime.InteropServices;

namespace Seekbeam.Core.SourceAccess;

/// <summary>
/// Disk shares of a server ("\\server\" typed in the launcher). Read-only query (NetShareEnum, level 1): it lists names,
/// nothing is opened. Results are cached a minute per server, and an unreachable server is not asked again for that
/// minute, because the call can block for several seconds.
/// </summary>
public static partial class NetShares
{
    private static readonly Dictionary<string, (DateTime At, List<string> Shares)> s_cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    public static List<string> List(string server)
    {
        server = server.Trim('\\');
        if (server.Length == 0 || server.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0) return [];
        lock (s_cache)
            if (s_cache.TryGetValue(server, out var c) && DateTime.UtcNow - c.At < CacheTtl) return c.Shares;
        var list = Query(server);
        lock (s_cache)
        {
            if (s_cache.Count > 64) s_cache.Clear();
            s_cache[server] = (DateTime.UtcNow, list);
        }
        return list;
    }

    private static unsafe List<string> Query(string server)
    {
        var list = new List<string>();
        int resume = 0;
        int rc = NetShareEnum(@"\\" + server, 1, out IntPtr buf, -1 /* MAX_PREFERRED_LENGTH */, out int read, out _, ref resume);
        try
        {
            if (rc != 0 && rc != 234 /* ERROR_MORE_DATA */) return list;
            var p = (ShareInfo1*)buf;
            for (int i = 0; i < read; i++)
            {
                uint type = p[i].Type;
                string? name = Marshal.PtrToStringUni(p[i].NetName);
                // Disk shares only (STYPE_DISKTREE = 0), not the hidden administrative ones (special bit, or "C$").
                if (name == null || (type & 0xFF) != 0 || (type & 0x80000000) != 0 || name.EndsWith('$')) continue;
                list.Add(name);
            }
        }
        finally { if (buf != IntPtr.Zero) NetApiBufferFree(buf); }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShareInfo1 { public IntPtr NetName; public uint Type; public IntPtr Remark; }

    [LibraryImport("netapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NetShareEnum(string serverName, int level, out IntPtr bufPtr, int prefMaxLen, out int entriesRead, out int totalEntries, ref int resumeHandle);

    [LibraryImport("netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);
}
