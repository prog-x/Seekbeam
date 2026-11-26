using Seekbeam.Core.Config;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Search;

namespace Seekbeam.Core.Catalog;

/// <summary>
/// Local learning: launches/selections per item key (full path, or "app:AUMID"), favorites and query history.
/// Stored only in catalog.db; can be disabled and reset. Never transmitted anywhere.
/// </summary>
public sealed class UsageStore
{
    private readonly CatalogDb _db;
    private readonly object _lock = new();
    private readonly Dictionary<string, (int Launches, DateTime Last)> _usage = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, int> _idBoost = new();
    private HashSet<int> _favoriteIds = new();
    private volatile UsageBoosts _boosts = UsageBoosts.None;
    public RankingWeights Weights { get; set; } = new();
    public bool LearningEnabled { get; set; } = true;
    public bool QueryHistoryEnabled { get; set; } = true;
    public int MaxQueryHistory { get; set; } = 500;
    public Func<string, long?>? Resolver { get; set; }

    public UsageStore(CatalogDb db)
    {
        _db = db;
        var con = db.RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT key, launches, last_used FROM usage ORDER BY last_used DESC LIMIT 20000";
            using (var r = cmd.ExecuteReader())
                while (r.Read()) _usage[r.GetString(0)] = (r.GetInt32(1), new DateTime(r.GetInt64(2), DateTimeKind.Utc));
            cmd.CommandText = "SELECT key FROM favorites";
            using var r2 = cmd.ExecuteReader();
            while (r2.Read()) _favorites.Add(r2.GetString(0));
        }
        finally { db.ReturnReader(con); }
    }

    public UsageBoosts Boosts => _boosts;

    /// <summary>Keys (paths, "app:…") by decreasing last use.</summary>
    public List<string> RecentKeys(int max)
    {
        lock (_lock) return _usage.OrderByDescending(u => u.Value.Last).Take(max).Select(u => u.Key).ToList();
    }

    /// <summary>Favourite keys (paths, "app:…").</summary>
    public List<string> Favorites()
    {
        lock (_lock) return [.. _favorites.Order(StringComparer.OrdinalIgnoreCase)];
    }
    public bool IsFavoriteId(int id) { lock (_lock) return _favoriteIds.Contains(id); }
    public bool IsFavorite(string key) { lock (_lock) return _favorites.Contains(key); }

    public int BoostForKey(string key)
    {
        int b = 0;
        lock (_lock)
        {
            if (_usage.TryGetValue(key, out var u)) b += Ranking.UsageBoost(u.Launches, u.Last, Weights, DateTime.UtcNow);
            if (_favorites.Contains(key)) b += Weights.Favorite;
        }
        return b;
    }

    /// <summary>Resolves stored path keys to entry ids (startup / after reindex). Bounded by history size.</summary>
    public void RebuildIdMap()
    {
        if (Resolver == null) return;
        var map = new Dictionary<int, int>();
        var fav = new HashSet<int>();
        List<KeyValuePair<string, (int, DateTime)>> items;
        List<string> favs;
        lock (_lock) { items = [.. _usage]; favs = [.. _favorites]; }
        var now = DateTime.UtcNow;
        foreach (var (key, (launches, last)) in items)
        {
            if (key.StartsWith("app:", StringComparison.Ordinal)) continue;
            long? id = Resolver(key);
            if (id is long v) map[(int)v] = Ranking.UsageBoost(launches, last, Weights, now);
        }
        foreach (var key in favs)
            if (!key.StartsWith("app:", StringComparison.Ordinal) && Resolver(key) is long v) fav.Add((int)v);
        lock (_lock) { _idBoost = map; _favoriteIds = fav; PublishBoosts(); }
    }

    /// <summary>Publishes an immutable id-to-boost snapshot (usage + favorites) read lock-free by searches. Caller holds _lock.</summary>
    private void PublishBoosts()
    {
        var merged = new Dictionary<int, int>(_idBoost);
        foreach (int id in _favoriteIds) merged[id] = merged.GetValueOrDefault(id) + Weights.Favorite;
        _boosts = UsageBoosts.Create(merged);
    }

    public void RecordLaunch(string key, long? entryId)
    {
        if (!LearningEnabled) return;
        DateTime now = DateTime.UtcNow;
        int launches;
        lock (_lock)
        {
            launches = (_usage.TryGetValue(key, out var u) ? u.Launches : 0) + 1;
            _usage[key] = (launches, now);
            if (entryId is long id)
            {
                var copy = new Dictionary<int, int>(_idBoost) { [(int)id] = Ranking.UsageBoost(launches, now, Weights, now) };
                _idBoost = copy;
                PublishBoosts();
            }
        }
        try
        {
            lock (_db.WriteLock)
            {
                using var cmd = _db.Writer.CreateCommand();
                cmd.CommandText = "INSERT INTO usage(key,launches,last_used) VALUES($k,1,$t) ON CONFLICT(key) DO UPDATE SET launches=launches+1, last_used=$t";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$t", now.Ticks);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { Log.Warn("Usage", "RecordLaunch failed: " + ex.Message); }
    }

    public void SetFavorite(string key, long? entryId, bool favorite)
    {
        lock (_lock)
        {
            if (favorite) { _favorites.Add(key); if (entryId is long id) _favoriteIds.Add((int)id); }
            else { _favorites.Remove(key); if (entryId is long id) _favoriteIds.Remove((int)id); }
            PublishBoosts();
        }
        lock (_db.WriteLock)
        {
            using var cmd = _db.Writer.CreateCommand();
            cmd.CommandText = favorite ? "INSERT OR IGNORE INTO favorites(key,added) VALUES($k,$t)" : "DELETE FROM favorites WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();
        }
    }

    public void RecordQuery(string query)
    {
        if (!QueryHistoryEnabled || string.IsNullOrWhiteSpace(query) || query.Length > 256) return;
        try
        {
            lock (_db.WriteLock)
            {
                using var cmd = _db.Writer.CreateCommand();
                cmd.CommandText = "INSERT INTO query_history(query,count,last_used) VALUES($q,1,$t) ON CONFLICT(query) DO UPDATE SET count=count+1,last_used=$t; " +
                                  "DELETE FROM query_history WHERE query IN (SELECT query FROM query_history ORDER BY last_used DESC LIMIT -1 OFFSET $max);";
                cmd.Parameters.AddWithValue("$q", query.Trim());
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.Ticks);
                cmd.Parameters.AddWithValue("$max", MaxQueryHistory);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex) { Log.Warn("Usage", "RecordQuery failed: " + ex.Message); }
    }

    /// <summary>Most frequent/recent past queries starting with <paramref name="prefix"/> (suggestions).</summary>
    public List<string> SuggestQueries(string prefix, int max = 5)
    {
        var res = new List<string>();
        if (!QueryHistoryEnabled) return res;
        var con = _db.RentReader();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT query FROM query_history WHERE query LIKE $p ESCAPE '\\' ORDER BY count DESC, last_used DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$p", prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            cmd.Parameters.AddWithValue("$n", max);
            using var r = cmd.ExecuteReader();
            while (r.Read()) res.Add(r.GetString(0));
        }
        finally { _db.ReturnReader(con); }
        return res;
    }

    public void ClearHistory()
    {
        lock (_db.WriteLock) CatalogDb.Exec(_db.Writer, "DELETE FROM query_history;");
    }

    public void ResetLearning()
    {
        lock (_lock) { _usage.Clear(); _idBoost = new(); PublishBoosts(); }
        lock (_db.WriteLock) CatalogDb.Exec(_db.Writer, "DELETE FROM usage;");
    }
}
