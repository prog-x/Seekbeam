using System.IO.Compression;
using System.Text;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Seekbeam.Core.Content;

public enum ContentStatus { Indexed = 0, Empty = 1, Failed = 2, Skipped = 3, Encrypted = 4, TooLarge = 5 }

public sealed record ContentDocInfo(long EntryId, long MTime, long Size, string Extractor, int Version, ContentStatus Status, int Chars, string? Error);

public sealed record ContentHit(long EntryId, double Rank);

/// <summary>
/// content.db: FTS5 contentless index (unicode61, diacritics removed) + compressed extracted text for snippets/preview.
/// Independent lifecycle from the catalog (can be deleted and rebuilt alone).
/// </summary>
public sealed class ContentDb : IDisposable
{
    public const string FileName = "content.db";
    private readonly SeekbeamStorage _storage;
    private readonly SqliteConnection _w;
    private readonly object _lock = new();
    private readonly System.Collections.Concurrent.ConcurrentBag<SqliteConnection> _readers = new();
    public bool StoreText { get; set; } = true;

    public ContentDb(SeekbeamStorage storage)
    {
        _storage = storage;
        _w = storage.OpenDatabase(FileName);
        CatalogDb.Exec(_w, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-4000; PRAGMA busy_timeout=10000;");
        long v = CatalogDb.Scalar<long>(_w, "PRAGMA user_version;");
        if (v == 0)
        {
            CatalogDb.Exec(_w, """
                CREATE VIRTUAL TABLE IF NOT EXISTS content_fts USING fts5(body, content='', contentless_delete=1, tokenize='unicode61 remove_diacritics 2');
                CREATE TABLE IF NOT EXISTS docs(entry_id INTEGER PRIMARY KEY, mtime INTEGER NOT NULL, size INTEGER NOT NULL, extractor TEXT NOT NULL,
                  version INTEGER NOT NULL, status INTEGER NOT NULL, chars INTEGER NOT NULL, error TEXT, indexed_at INTEGER NOT NULL, text BLOB);
                PRAGMA user_version=1;
                """);
        }
    }

    public SqliteConnection RentReader()
    {
        if (_readers.TryTake(out var c)) return c;
        var con = _storage.OpenDatabase(FileName, readOnly: true);
        CatalogDb.Exec(con, "PRAGMA query_only=1; PRAGMA cache_size=-2000; PRAGMA busy_timeout=5000;");
        return con;
    }

    public void ReturnReader(SqliteConnection c) { if (_readers.Count < 4) _readers.Add(c); else c.Dispose(); }

    public ContentDocInfo? GetInfo(long entryId)
    {
        var con = RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT entry_id, mtime, size, extractor, version, status, chars, error FROM docs WHERE entry_id=$id";
            cmd.Parameters.AddWithValue("$id", entryId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new ContentDocInfo(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt32(4), (ContentStatus)r.GetInt32(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7));
        }
        finally { ReturnReader(con); }
    }

    /// <summary>Replaces the document for an entry (short transaction).</summary>
    public void Upsert(long entryId, long mtime, long size, string extractor, int version, ContentStatus status, string text, string? error)
    {
        byte[]? blob = StoreText && text.Length > 0 ? Compress(text) : null;
        lock (_lock)
        {
            using var tx = _w.BeginTransaction();
            using (var del = _w.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM content_fts WHERE rowid=$id";
                del.Parameters.AddWithValue("$id", entryId);
                del.ExecuteNonQuery();
            }
            if (text.Length > 0)
            {
                using var ins = _w.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO content_fts(rowid, body) VALUES($id, $b)";
                ins.Parameters.AddWithValue("$id", entryId);
                ins.Parameters.AddWithValue("$b", text);
                ins.ExecuteNonQuery();
            }
            using (var up = _w.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = "INSERT OR REPLACE INTO docs(entry_id,mtime,size,extractor,version,status,chars,error,indexed_at,text) VALUES($id,$m,$s,$x,$v,$st,$c,$e,$t,$txt)";
                up.Parameters.AddWithValue("$id", entryId);
                up.Parameters.AddWithValue("$m", mtime);
                up.Parameters.AddWithValue("$s", size);
                up.Parameters.AddWithValue("$x", extractor);
                up.Parameters.AddWithValue("$v", version);
                up.Parameters.AddWithValue("$st", (int)status);
                up.Parameters.AddWithValue("$c", text.Length);
                up.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
                up.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
                up.Parameters.AddWithValue("$txt", (object?)blob ?? DBNull.Value);
                up.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Delete(long entryId)
    {
        lock (_lock)
        {
            using var tx = _w.BeginTransaction();
            using var cmd = _w.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM content_fts WHERE rowid=$id; DELETE FROM docs WHERE entry_id=$id;";
            cmd.Parameters.AddWithValue("$id", entryId);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public void DeleteAll()
    {
        lock (_lock) CatalogDb.Exec(_w, "DELETE FROM content_fts; DELETE FROM docs; INSERT INTO content_fts(content_fts) VALUES('optimize');");
    }

    /// <summary>Full-text search (FTS5 MATCH expression), best BM25 first.</summary>
    public List<ContentHit> Search(string ftsExpression, int limit)
    {
        var res = new List<ContentHit>();
        var con = RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT rowid, bm25(content_fts) FROM content_fts WHERE content_fts MATCH $q ORDER BY bm25(content_fts) LIMIT $n";
            cmd.Parameters.AddWithValue("$q", ftsExpression);
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) res.Add(new ContentHit(r.GetInt64(0), r.GetDouble(1)));
        }
        catch (SqliteException ex) { Log.Debug("Content", "FTS query rejected: " + ex.Message); }
        finally { ReturnReader(con); }
        return res;
    }

    public string? GetText(long entryId)
    {
        var con = RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT text FROM docs WHERE entry_id=$id";
            cmd.Parameters.AddWithValue("$id", entryId);
            return cmd.ExecuteScalar() is byte[] b ? Decompress(b) : null;
        }
        finally { ReturnReader(con); }
    }

    public (long Docs, long Failed, long Chars) Counts()
    {
        var con = RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), IFNULL(SUM(status=2),0), IFNULL(SUM(chars),0) FROM docs";
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
        }
        finally { ReturnReader(con); }
    }

    /// <summary>Removes documents whose catalog entry no longer exists (maintenance, batched).</summary>
    public int DeleteOrphans(Func<long, bool> entryExists, int max = 5000)
    {
        var ids = new List<long>();
        var con = RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT entry_id FROM docs";
            using var r = cmd.ExecuteReader();
            while (r.Read() && ids.Count < max) { long id = r.GetInt64(0); if (!entryExists(id)) ids.Add(id); }
        }
        finally { ReturnReader(con); }
        foreach (var id in ids) Delete(id);
        return ids.Count;
    }

    public void Optimize()
    {
        lock (_lock) CatalogDb.Exec(_w, "INSERT INTO content_fts(content_fts) VALUES('optimize'); PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public long FileSizeBytes() => _storage.FileSize(Path.Combine("db", FileName)) + _storage.FileSize(Path.Combine("db", FileName + "-wal"));

    private static byte[] Compress(string text)
    {
        var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            z.Write(bytes);
        }
        return ms.ToArray();
    }

    private static string Decompress(byte[] blob)
    {
        using var z = new DeflateStream(new MemoryStream(blob), CompressionMode.Decompress);
        using var sr = new StreamReader(z, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    public void Dispose()
    {
        try { lock (_lock) CatalogDb.Exec(_w, "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { }
        while (_readers.TryTake(out var r)) r.Dispose();
        _w.Dispose();
    }
}
