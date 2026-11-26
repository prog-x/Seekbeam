using Seekbeam.Core.Apps;
using Seekbeam.Core.Catalog;
using Seekbeam.Core.Config;
using Seekbeam.Core.NameIndex;
using Seekbeam.Core.Search;
using Seekbeam.Core.Storage;

namespace Seekbeam.Core.Tests;

/// <summary>Isolated Seekbeam data root + catalog + name index in a temp directory.</summary>
public sealed class TestEnv : IDisposable
{
    public string Root { get; }
    public SeekbeamStorage Storage { get; }
    public CatalogDb Db { get; private set; }
    public CatalogWriter Writer { get; private set; }
    public NameIndexManager Index { get; private set; }
    public EntryStore Entries { get; private set; }
    public AppCatalog Apps { get; } = new();
    public UsageStore Usage { get; private set; }
    public SearchConfig SearchConfig { get; } = new();
    public SearchEngine Engine { get; private set; }

    public TestEnv(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "seekbeam-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Storage = new SeekbeamStorage(Path.Combine(Root, "data"));
        Open();
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(Db), nameof(Writer), nameof(Index), nameof(Entries), nameof(Usage), nameof(Engine))]
    private void Open()
    {
        Db = new CatalogDb(Storage);
        Writer = new CatalogWriter(Db);
        Index = new NameIndexManager(Storage, Db);
        Index.Load();
        Entries = new EntryStore(Db);
        Usage = new UsageStore(Db);
        Engine = new SearchEngine(Index, Entries, Apps, Usage, () => SearchConfig);
    }

    /// <summary>Simulates a restart. If <paramref name="crash"/>, the index is NOT flushed (as after a process kill).</summary>
    public void Reopen(bool crash)
    {
        if (!crash) { Index.Dispose(); Db.Dispose(); }
        else
        {
            // Abandon in-memory state without flush; release file handles so the "new process" can open them.
            typeof(NameIndexManager).GetField("_published", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(Index, null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Db.Writer.Close();
        }
        Open();
    }

    public long AddRoot(string path, long xflags = 0)
    {
        using var b = Writer.Begin();
        long id = Writer.Insert(b, 0, 1, path, EntryKind.Directory, 0, 0, 0, (long)FileAttributes.Directory, 0, 0, xflags);
        Index.Apply(b.Commit());
        return id;
    }

    public long Add(long parent, string name, bool dir = false, long attrs = 0, long xflags = 0)
    {
        using var b = Writer.Begin();
        long id = Writer.Insert(b, parent, 1, name, dir ? EntryKind.Directory : EntryKind.File, 10, DateTime.UtcNow.ToFileTimeUtc(), 0,
            attrs | (dir ? (long)FileAttributes.Directory : 0), 0, 1, xflags);
        Index.Apply(b.Commit());
        return id;
    }

    public List<long> AddMany(long parent, IEnumerable<string> names)
    {
        var ids = new List<long>();
        using var b = Writer.Begin();
        foreach (var n in names) ids.Add(Writer.Insert(b, parent, 1, n, EntryKind.File, 1, 0, 0, 0, 0, 1));
        Index.Apply(b.Commit());
        return ids;
    }

    public void Delete(long id, bool dir = false)
    {
        using var b = Writer.Begin();
        Writer.Delete(b, id, dir ? EntryKind.Directory : EntryKind.File);
        Index.Apply(b.Commit());
    }

    public void Rename(long id, long parent, string name, bool dir = false)
    {
        using var b = Writer.Begin();
        Writer.Rename(b, id, parent, name, 1, dir ? EntryKind.Directory : EntryKind.File, 0);
        Index.Apply(b.Commit());
        Entries.InvalidatePathCache();
    }

    public SearchResultSet Search(string q, int max = 20, SearchPurpose purpose = SearchPurpose.Launch) =>
        Engine.Search(new SearchRequest { Query = q, MaxResults = max, Purpose = purpose }, CancellationToken.None);
    public List<string> Names(string q, int max = 20) => Search(q, max).Results.Select(r => r.Name).ToList();

    public void Dispose()
    {
        try { Index.Dispose(); } catch { }
        try { Db.Dispose(); } catch { }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch { }
    }
}
