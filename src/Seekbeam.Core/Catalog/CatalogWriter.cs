using Seekbeam.Core.NameIndex;
using Microsoft.Data.Sqlite;

namespace Seekbeam.Core.Catalog;

public enum QueuePriority { Event = 0, Discovery = 1, InitialScan = 2, Reconcile = 3, Maintenance = 4 }
public enum ScanMode { Shallow = 0, Recursive = 1 }
public enum ContentOp { Index = 0, Delete = 1 }

/// <summary>
/// Short write transactions on the catalog. Every name-affecting change gets a new global revision and is returned
/// as a <see cref="NameChange"/> to be applied to the Name Index after commit (crash-consistent: the index can always
/// be re-derived from entries.name_rev / name_tombstones).
/// </summary>
public sealed class CatalogWriter
{
    private readonly CatalogDb _db;
    private long _queueSeq;
    private readonly SqliteCommand _insert, _updateMeta, _rename, _deleteOne, _tomb, _children, _enqueueDir, _enqueueContent, _subtree, _setScanned,
        _setDepthLimited, _clearDepthLimited;

    public CatalogDb Db => _db;

    public CatalogWriter(CatalogDb db)
    {
        _db = db;
        var w = db.Writer;
        _queueSeq = Math.Max(CatalogDb.Scalar<long>(w, "SELECT IFNULL(MAX(seq),0) FROM name_queue"),
                             CatalogDb.Scalar<long>(w, "SELECT IFNULL(MAX(seq),0) FROM content_queue"));
        _insert = Cmd("INSERT INTO entries(parent_id,root_id,name,kind,size,mtime,ctime,attrs,file_id,depth,name_rev,scanned) " +
                      "VALUES($p,$r,$n,$k,$s,$m,$c,$a,$f,$d,$rev,$sc) RETURNING id",
                      "$p", "$r", "$n", "$k", "$s", "$m", "$c", "$a", "$f", "$d", "$rev", "$sc");
        _updateMeta = Cmd("UPDATE entries SET size=$s, mtime=$m, ctime=$c, attrs=$a, file_id=$f, name_rev=CASE WHEN $rev>0 THEN $rev ELSE name_rev END WHERE id=$id",
                          "$s", "$m", "$c", "$a", "$f", "$rev", "$id");
        _rename = Cmd("UPDATE entries SET parent_id=$p, name=$n, depth=$d, name_rev=$rev WHERE id=$id", "$p", "$n", "$d", "$rev", "$id");
        _deleteOne = Cmd("DELETE FROM entries WHERE id=$id", "$id");
        _tomb = Cmd("INSERT INTO name_tombstones(id,rev) VALUES($id,$rev)", "$id", "$rev");
        _children = Cmd("SELECT id,name,kind,size,mtime,ctime,attrs,file_id,scanned FROM entries WHERE parent_id=$p", "$p");
        _enqueueDir = Cmd("INSERT INTO name_queue(dir_id,priority,mode,seq,attempts,not_before) VALUES($id,$pr,$m,$seq,0,$nb) " +
                          "ON CONFLICT(dir_id) DO UPDATE SET priority=MIN(priority,excluded.priority), mode=MAX(mode,excluded.mode), not_before=MIN(not_before,excluded.not_before)",
                          "$id", "$pr", "$m", "$seq", "$nb");
        _enqueueContent = Cmd("INSERT INTO content_queue(entry_id,priority,op,seq,attempts,not_before) VALUES($id,$pr,$op,$seq,0,0) " +
                              "ON CONFLICT(entry_id) DO UPDATE SET priority=MIN(priority,excluded.priority), op=excluded.op, attempts=0, not_before=0",
                              "$id", "$pr", "$op", "$seq");
        _subtree = Cmd("WITH RECURSIVE sub(id,kind) AS (SELECT id,kind FROM entries WHERE parent_id=$id " +
                       "UNION ALL SELECT e.id,e.kind FROM entries e JOIN sub ON e.parent_id=sub.id WHERE sub.kind=1) SELECT id,kind FROM sub LIMIT 50000", "$id");
        _setDepthLimited = Cmd("INSERT INTO depth_limited(dir_id,root_id,hidden) VALUES($id,$r,$h) ON CONFLICT(dir_id) DO UPDATE SET hidden=excluded.hidden",
                               "$id", "$r", "$h");
        _clearDepthLimited = Cmd("DELETE FROM depth_limited WHERE dir_id=$id", "$id");
        _setScanned = Cmd("UPDATE entries SET scanned=$t, mtime=CASE WHEN $m<>0 THEN $m ELSE mtime END WHERE id=$id", "$t", "$m", "$id");
    }

    private SqliteCommand Cmd(string sql, params string[] ps)
    {
        var c = _db.Writer.CreateCommand();
        c.CommandText = sql;
        foreach (var p in ps) c.Parameters.Add(new SqliteParameter(p, null));
        c.Prepare();
        return c;
    }

    /// <summary>A write transaction. Dispose without Commit rolls back. Holds the catalog write lock.</summary>
    public sealed class Batch : IDisposable
    {
        private readonly CatalogWriter _w;
        internal readonly SqliteTransaction Tx;
        internal readonly List<NameChange> Changes = new();
        public int Operations { get; internal set; }
        private bool _done;

        internal Batch(CatalogWriter w)
        {
            _w = w;
            Monitor.Enter(w._db.WriteLock);
            Tx = w._db.Writer.BeginTransaction(deferred: false);
        }

        /// <summary>Commits and returns name changes to apply to the index (in revision order).</summary>
        public List<NameChange> Commit()
        {
            Tx.Commit();
            _done = true;
            return Changes;
        }

        public void Dispose()
        {
            if (!_done) { try { Tx.Rollback(); } catch (SqliteException) { } Changes.Clear(); }
            Tx.Dispose();
            Monitor.Exit(_w._db.WriteLock);
        }
    }

    public Batch Begin() => new(this);

    private SqliteCommand Bind(Batch b, SqliteCommand c, params object?[] values)
    {
        c.Transaction = b.Tx;
        for (int i = 0; i < values.Length; i++) c.Parameters[i].Value = values[i] ?? DBNull.Value;
        b.Operations++;
        return c;
    }

    public readonly record struct ChildRow(long Id, string Name, EntryKind Kind, long Size, long MTime, long CTime, long Attrs, long FileId, long Scanned);

    public List<ChildRow> GetChildren(Batch b, long parentId)
    {
        var list = new List<ChildRow>();
        using var r = Bind(b, _children, parentId).ExecuteReader();
        while (r.Read())
            list.Add(new ChildRow(r.GetInt64(0), r.GetString(1), (EntryKind)r.GetInt32(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8)));
        return list;
    }

    public long Insert(Batch b, long parentId, long rootId, string name, EntryKind kind, long size, long mtime, long ctime, long attrs, long fileId, int depth)
    {
        long rev = _db.NextRev();
        long id = (long)Bind(b, _insert, parentId, rootId, name, (int)kind, size, mtime, ctime, attrs, fileId, depth, rev, 0L).ExecuteScalar()!;
        if (id >= int.MaxValue) throw new InvalidOperationException("Entry id space exhausted (2^31).");
        b.Changes.Add(new NameChange((int)id, (int)parentId, NameIndexManager.ToFlags((int)kind, attrs, parentId == 0), name, rev, false));
        return id;
    }

    /// <summary>Updates metadata; if search-relevant attributes changed (hidden/system/reparse) the record is re-indexed.</summary>
    public void UpdateMeta(Batch b, ChildRow old, long parentId, long size, long mtime, long ctime, long attrs, long fileId)
    {
        const long relevant = (long)(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint);
        long rev = 0;
        if ((old.Attrs & relevant) != (attrs & relevant))
        {
            rev = _db.NextRev();
            b.Changes.Add(new NameChange((int)old.Id, (int)parentId, NameIndexManager.ToFlags((int)old.Kind, attrs, parentId == 0), old.Name, rev, false));
        }
        Bind(b, _updateMeta, size, mtime, ctime, attrs, fileId, rev, old.Id).ExecuteNonQuery();
    }

    /// <summary>Rename/move keeping the id (children keep their parent pointer).</summary>
    public void Rename(Batch b, long id, long newParentId, string newName, int depth, EntryKind kind, long attrs)
    {
        long rev = _db.NextRev();
        Bind(b, _rename, newParentId, newName, depth, rev, id).ExecuteNonQuery();
        b.Changes.Add(new NameChange((int)id, (int)newParentId, NameIndexManager.ToFlags((int)kind, attrs, newParentId == 0), newName, rev, false));
    }

    /// <summary>Deletes an entry and (for directories) its whole subtree, writing tombstones.</summary>
    public int Delete(Batch b, long id, EntryKind kind, Action<long>? onFileDeleted = null)
    {
        int n = 0;
        if (kind == EntryKind.Directory)
        {
            while (true)
            {
                var sub = new List<(long Id, int Kind)>();
                using (var r = Bind(b, _subtree, id).ExecuteReader())
                    while (r.Read()) sub.Add((r.GetInt64(0), r.GetInt32(1)));
                if (sub.Count == 0) break;
                // Delete leaves first is not required (no FK); delete all collected rows.
                foreach (var (sid, sk) in sub)
                {
                    DeleteRow(b, sid);
                    if (sk == 0) onFileDeleted?.Invoke(sid);
                    n++;
                }
            }
        }
        DeleteRow(b, id);
        if (kind == EntryKind.File) onFileDeleted?.Invoke(id);
        return n + 1;
    }

    private void DeleteRow(Batch b, long id)
    {
        long rev = _db.NextRev();
        Bind(b, _deleteOne, id).ExecuteNonQuery();
        Bind(b, _tomb, id, rev).ExecuteNonQuery();
        Bind(b, _clearDepthLimited, id).ExecuteNonQuery();
        b.Changes.Add(new NameChange((int)id, 0, EntryFlags.None, null, rev, true));
    }

    /// <summary>Records (hidden &gt; 0) or clears the "children not explored because of MaxDepth" mark of a directory.</summary>
    public void SetDepthLimited(Batch b, long dirId, long rootId, int hidden)
    {
        if (hidden > 0) Bind(b, _setDepthLimited, dirId, rootId, hidden).ExecuteNonQuery();
        else Bind(b, _clearDepthLimited, dirId).ExecuteNonQuery();
    }

    public void MarkScanned(Batch b, long dirId, long dirMtime) => Bind(b, _setScanned, DateTime.UtcNow.Ticks, dirMtime, dirId).ExecuteNonQuery();

    public void EnqueueDirectory(Batch b, long dirId, QueuePriority priority, ScanMode mode, long notBeforeTicks = 0) =>
        Bind(b, _enqueueDir, dirId, (int)priority, (int)mode, Interlocked.Increment(ref _queueSeq), notBeforeTicks).ExecuteNonQuery();

    public void EnqueueContent(Batch b, long entryId, QueuePriority priority, ContentOp op) =>
        Bind(b, _enqueueContent, entryId, (int)priority, (int)op, Interlocked.Increment(ref _queueSeq)).ExecuteNonQuery();
}
