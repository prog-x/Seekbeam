using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Seekbeam.Core.Catalog;

/// <summary>
/// catalog.db: source of truth for names/metadata, persistent queues, tombstones, apps, usage, errors.
/// One write connection (serialised by <see cref="WriteLock"/>) + pooled read-only connections (WAL readers).
/// </summary>
public sealed class CatalogDb : IDisposable
{
    public const string FileName = "catalog.db";
    public const int SchemaVersion = 2;
    private readonly SeekbeamStorage _storage;
    private readonly SqliteConnection _writer;
    private readonly System.Collections.Concurrent.ConcurrentBag<SqliteConnection> _readers = new();
    private long _rev;
    private bool _disposed;

    public object WriteLock { get; } = new();
    public SqliteConnection Writer => _writer;
    public SeekbeamStorage Storage => _storage;
    /// <summary>True if the previous session did not shut down cleanly.</summary>
    public bool RecoveredFromUncleanShutdown { get; }

    public CatalogDb(SeekbeamStorage storage)
    {
        _storage = storage;
        _writer = storage.OpenDatabase(FileName);
        Exec(_writer, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-8000; " +
                      "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=OFF; PRAGMA wal_autocheckpoint=2000; PRAGMA journal_size_limit=67108864;");
        CreateSchema();
        RecoveredFromUncleanShutdown = GetMeta("clean_shutdown") == "0";
        if (RecoveredFromUncleanShutdown)
        {
            string ok = Scalar<string>(_writer, "PRAGMA quick_check;") ?? "?";
            if (ok != "ok") Log.Error("Catalog", "quick_check reported: " + ok);
            else Log.Warn("Catalog", "Previous session ended abnormally; catalog quick_check ok.");
        }
        SetMeta("clean_shutdown", "0");
        _rev = Math.Max(Scalar<long>(_writer, "SELECT IFNULL(MAX(name_rev),0) FROM entries"),
                        Scalar<long>(_writer, "SELECT IFNULL(MAX(rev),0) FROM name_tombstones"));
    }

    private void CreateSchema()
    {
        long v = Scalar<long>(_writer, "PRAGMA user_version;");
        if (v == SchemaVersion) return;
        if (v > SchemaVersion) throw new InvalidOperationException($"catalog.db schema {v} is newer than this Seekbeam version ({SchemaVersion}).");
        using var tx = _writer.BeginTransaction();
        Exec(_writer, """
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS roots(
              id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT NOT NULL UNIQUE COLLATE NOCASE, entry_id INTEGER NOT NULL DEFAULT 0,
              state INTEGER NOT NULL DEFAULT 0, last_full_scan INTEGER NOT NULL DEFAULT 0, last_reconcile INTEGER NOT NULL DEFAULT 0,
              last_error TEXT);
            CREATE TABLE IF NOT EXISTS entries(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              parent_id INTEGER NOT NULL, root_id INTEGER NOT NULL, name TEXT NOT NULL, kind INTEGER NOT NULL,
              size INTEGER NOT NULL DEFAULT 0, mtime INTEGER NOT NULL DEFAULT 0, ctime INTEGER NOT NULL DEFAULT 0,
              attrs INTEGER NOT NULL DEFAULT 0, file_id INTEGER NOT NULL DEFAULT 0, depth INTEGER NOT NULL DEFAULT 0,
              name_rev INTEGER NOT NULL, scanned INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_entries_parent_name ON entries(parent_id, name);
            CREATE INDEX IF NOT EXISTS ix_entries_rev ON entries(name_rev);
            CREATE INDEX IF NOT EXISTS ix_entries_root ON entries(root_id);
            CREATE TABLE IF NOT EXISTS name_tombstones(id INTEGER NOT NULL, rev INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_tomb_rev ON name_tombstones(rev);
            CREATE TABLE IF NOT EXISTS name_queue(
              dir_id INTEGER PRIMARY KEY, priority INTEGER NOT NULL, mode INTEGER NOT NULL, seq INTEGER NOT NULL,
              attempts INTEGER NOT NULL DEFAULT 0, not_before INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_nq_order ON name_queue(priority, seq);
            CREATE TABLE IF NOT EXISTS content_queue(
              entry_id INTEGER PRIMARY KEY, priority INTEGER NOT NULL, op INTEGER NOT NULL, seq INTEGER NOT NULL,
              attempts INTEGER NOT NULL DEFAULT 0, not_before INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_cq_order ON content_queue(priority, seq);
            CREATE TABLE IF NOT EXISTS apps(
              id INTEGER PRIMARY KEY, name TEXT NOT NULL, target TEXT, args TEXT, workdir TEXT, icon TEXT,
              source INTEGER NOT NULL, app_id TEXT, key TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS usage(key TEXT PRIMARY KEY COLLATE NOCASE, launches INTEGER NOT NULL, last_used INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS favorites(key TEXT PRIMARY KEY COLLATE NOCASE, added INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS query_history(query TEXT PRIMARY KEY, count INTEGER NOT NULL, last_used INTEGER NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS errors(id INTEGER PRIMARY KEY AUTOINCREMENT, time INTEGER NOT NULL, component TEXT NOT NULL,
              path TEXT, message TEXT NOT NULL);
            -- v2: directories at their root's MaxDepth whose children were not explored (hidden = non-excluded children on disk).
            CREATE TABLE IF NOT EXISTS depth_limited(dir_id INTEGER PRIMARY KEY, root_id INTEGER NOT NULL, hidden INTEGER NOT NULL);
            """);
        Exec(_writer, $"PRAGMA user_version={SchemaVersion};");
        tx.Commit();
    }

    /// <summary>Next value of the global monotonic name revision (caller holds <see cref="WriteLock"/>).</summary>
    public long NextRev() => ++_rev;
    public long CurrentRev => Interlocked.Read(ref _rev);

    public SqliteConnection RentReader()
    {
        if (_readers.TryTake(out var c)) return c;
        var con = _storage.OpenDatabase(FileName, readOnly: true);
        Exec(con, "PRAGMA query_only=1; PRAGMA cache_size=-2000; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;");
        return con;
    }

    public void ReturnReader(SqliteConnection con)
    {
        if (_disposed || _readers.Count >= 8) con.Dispose();
        else _readers.Add(con);
    }

    public string? GetMeta(string key)
    {
        lock (WriteLock)
        {
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (WriteLock)
        {
            using var cmd = _writer.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public void RecordError(string component, string? path, string message)
    {
        try
        {
            lock (WriteLock)
            {
                using var cmd = _writer.CreateCommand();
                cmd.CommandText = "INSERT INTO errors(time,component,path,message) VALUES($t,$c,$p,$m); " +
                                  "DELETE FROM errors WHERE id <= (SELECT MAX(id) FROM errors) - 2000;";
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
                cmd.Parameters.AddWithValue("$c", component);
                cmd.Parameters.AddWithValue("$p", (object?)path ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$m", message);
                cmd.ExecuteNonQuery();
            }
        }
        catch (SqliteException ex) { Log.Warn("Catalog", "RecordError failed: " + ex.Message); }
    }

    public void Checkpoint()
    {
        lock (WriteLock) Exec(_writer, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public long FileSizeBytes() =>
        _storage.FileSize(Path.Combine("db", FileName)) + _storage.FileSize(Path.Combine("db", FileName + "-wal"));

    public static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static T? Scalar<T>(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        object? o = cmd.ExecuteScalar();
        if (o == null || o is DBNull) return default;
        return (T)Convert.ChangeType(o, typeof(T));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            lock (WriteLock)
            {
                Exec(_writer, "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);");
                using var cmd = _writer.CreateCommand();
                cmd.CommandText = "INSERT INTO meta(key,value) VALUES('clean_shutdown','1') ON CONFLICT(key) DO UPDATE SET value='1'";
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { Log.Warn("Catalog", "Shutdown checkpoint failed: " + ex.Message); }
        while (_readers.TryTake(out var r)) r.Dispose();
        _writer.Dispose();
    }
}
