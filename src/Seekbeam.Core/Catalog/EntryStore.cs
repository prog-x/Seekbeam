using Seekbeam.Core.NameIndex;
using Microsoft.Data.Sqlite;

namespace Seekbeam.Core.Catalog;

public enum EntryKind { File = 0, Directory = 1 }

public sealed record EntryInfo(long Id, long ParentId, long RootId, string Name, EntryKind Kind, long Size, long MTime, long CTime, long Attributes, int Depth)
{
    public DateTime ModifiedUtc => MTime > 0 ? DateTime.FromFileTimeUtc(MTime) : DateTime.MinValue;
    public bool IsDirectory => Kind == EntryKind.Directory;
}

/// <summary>Read-side access to catalog entries: display hydration, path building (with an LRU of directory paths), path resolution.</summary>
public sealed class EntryStore
{
    private readonly CatalogDb _db;
    private readonly object _cacheLock = new();
    private readonly Dictionary<long, LinkedListNode<(long Id, string Path)>> _dirPaths = new();
    private readonly LinkedList<(long Id, string Path)> _lru = new();
    private const int CacheCapacity = 8192;

    public EntryStore(CatalogDb db) { _db = db; }

    public void InvalidatePathCache()
    {
        lock (_cacheLock) { _dirPaths.Clear(); _lru.Clear(); }
    }

    private bool TryCache(long id, out string path)
    {
        lock (_cacheLock)
        {
            if (_dirPaths.TryGetValue(id, out var node))
            {
                _lru.Remove(node); _lru.AddFirst(node);
                path = node.Value.Path;
                return true;
            }
        }
        path = "";
        return false;
    }

    private void PutCache(long id, string path)
    {
        lock (_cacheLock)
        {
            if (_dirPaths.ContainsKey(id)) return;
            var node = _lru.AddFirst((id, path));
            _dirPaths[id] = node;
            if (_dirPaths.Count > CacheCapacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _dirPaths.Remove(last.Value.Id);
            }
        }
    }

    public EntryInfo? Get(long id)
    {
        var con = _db.RentReader();
        try { return Get(con, id); }
        finally { _db.ReturnReader(con); }
    }

    public static EntryInfo? Get(SqliteConnection con, long id)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id,parent_id,root_id,name,kind,size,mtime,ctime,attrs,depth FROM entries WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    internal static EntryInfo Read(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), (EntryKind)r.GetInt32(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt32(9));

    /// <summary>Hydrates several entries with their full paths using one connection.</summary>
    public List<(EntryInfo Info, string FullPath)> GetWithPaths(IEnumerable<long> ids)
    {
        var res = new List<(EntryInfo, string)>();
        var con = _db.RentReader();
        try
        {
            foreach (long id in ids)
            {
                var e = Get(con, id);
                if (e == null) continue;
                string? p = BuildPath(con, e);
                if (p != null) res.Add((e, p));
            }
        }
        finally { _db.ReturnReader(con); }
        return res;
    }

    public string? GetPath(long id)
    {
        var con = _db.RentReader();
        try
        {
            var e = Get(con, id);
            return e == null ? null : BuildPath(con, e);
        }
        finally { _db.ReturnReader(con); }
    }

    private string? BuildPath(SqliteConnection con, EntryInfo e)
    {
        if (e.ParentId == 0) return e.Name; // root entry: name is the full root path
        string? parent = DirectoryPath(con, e.ParentId);
        return parent == null ? null : Combine(parent, e.Name);
    }

    public static string Combine(string dir, string name) => dir.EndsWith('\\') ? dir + name : dir + "\\" + name;

    public string? DirectoryPath(SqliteConnection con, long dirId)
    {
        if (TryCache(dirId, out var cached)) return cached;
        // Walk up to the first cached ancestor or the root.
        var chain = new List<(long Id, string Name)>();
        long cur = dirId;
        string? basePath = null;
        for (int guard = 0; guard < 4096; guard++)
        {
            if (TryCache(cur, out var c)) { basePath = c; break; }
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT parent_id, name FROM entries WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", cur);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            long parent = r.GetInt64(0);
            string name = r.GetString(1);
            if (parent == 0) { basePath = name; PutCache(cur, name); break; }
            chain.Add((cur, name));
            cur = parent;
        }
        if (basePath == null) return null;
        string path = basePath;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            path = Combine(path, chain[i].Name);
            PutCache(chain[i].Id, path);
        }
        return path;
    }

    /// <summary>Resolves an absolute path to an entry id, or null if not indexed.</summary>
    public long? Resolve(string fullPath, IReadOnlyList<(long RootId, string RootPath, long RootEntryId)> roots)
    {
        var con = _db.RentReader();
        try { return Resolve(con, fullPath, roots); }
        finally { _db.ReturnReader(con); }
    }

    public static long? Resolve(SqliteConnection con, string fullPath, IReadOnlyList<(long RootId, string RootPath, long RootEntryId)> roots)
    {
        string p = Path.TrimEndingDirectorySeparator(fullPath);
        (long RootId, string RootPath, long RootEntryId)? best = null;
        foreach (var r in roots)
        {
            string rp = Path.TrimEndingDirectorySeparator(r.RootPath);
            if (p.Equals(rp, StringComparison.OrdinalIgnoreCase)) return r.RootEntryId;
            if (p.Length > rp.Length && p.StartsWith(rp, StringComparison.OrdinalIgnoreCase) && (p[rp.Length] == '\\' || rp.EndsWith('\\'))
                && (best == null || rp.Length > best.Value.RootPath.Length)) best = r;
        }
        if (best == null) return null;
        string rel = p[Path.TrimEndingDirectorySeparator(best.Value.RootPath).Length..].TrimStart('\\');
        long cur = best.Value.RootEntryId;
        foreach (var part in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            long? next = FindChild(con, cur, part);
            if (next == null) return null;
            cur = next.Value;
        }
        return cur;
    }

    public static long? FindChild(SqliteConnection con, long parentId, string name)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id FROM entries WHERE parent_id=$p AND name=$n";
        cmd.Parameters.AddWithValue("$p", parentId);
        cmd.Parameters.AddWithValue("$n", name);
        if (cmd.ExecuteScalar() is long id) return id;
        cmd.CommandText = "SELECT id FROM entries WHERE parent_id=$p AND name=$n COLLATE NOCASE LIMIT 1";
        return cmd.ExecuteScalar() is long id2 ? id2 : null;
    }

    public (long Files, long Dirs) Counts()
    {
        var con = _db.RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT kind, COUNT(*) FROM entries GROUP BY kind";
            using var r = cmd.ExecuteReader();
            long f = 0, d = 0;
            while (r.Read()) { if (r.GetInt32(0) == 1) d = r.GetInt64(1); else f = r.GetInt64(1); }
            return (f, d);
        }
        finally { _db.ReturnReader(con); }
    }

    public static EntryFlags FlagsOf(EntryInfo e) => NameIndexManager.ToFlags((int)e.Kind, e.Attributes, e.ParentId == 0);
}
