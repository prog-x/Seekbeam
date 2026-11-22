using System.Diagnostics;
using Seekbeam.Core.Config;
using Seekbeam.Core.Content;
using Seekbeam.Core.Engine;
using Seekbeam.Core.Storage;

namespace Seekbeam.Core.Tests;

/// <summary>Content pipeline end to end: scan → content queue → out-of-process extraction (real host + Job Object) → FTS5 → deep search.</summary>
public sealed class ContentEndToEndTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "seekbeam-content", Guid.NewGuid().ToString("N"));
    private SeekbeamEngine? _engine;

    public static string HostPath
    {
        get
        {
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
            {
                string c = Path.Combine(dir, "src", "Seekbeam.ExtractorHost", "bin", "Debug", "net10.0-windows", "Seekbeam.ExtractorHost.exe");
                if (File.Exists(c))
                {
                    // As Seekbeam.exe does before any extraction: make the AppContainer sandbox usable (TD-17).
                    if (Interlocked.Exchange(ref s_provisioned, 1) == 0)
                        Assert.Equal(Storage.SandboxProvisioningStatus.Ready, Storage.SandboxProvisioning.Ensure(Path.GetDirectoryName(c)!, _ => false));
                    return c;
                }
            }
            throw new FileNotFoundException("Seekbeam.ExtractorHost.exe not built");
        }
    }

    private static int s_provisioned;

    private SeekbeamEngine Start(string src, Action<SeekbeamConfig>? configure = null)
    {
        var storage = new SeekbeamStorage(Path.Combine(_base, "data"));
        new ConfigStore(storage).Update(c => { c.Roots = [new RootConfig { Path = src }]; c.General.FirstRunCompleted = true; c.GlobalExcludes = []; configure?.Invoke(c); });
        _engine = new SeekbeamEngine(storage, new EngineOptions
        {
            Unthrottled = true, EnableApps = false, EnableRecentItems = false,
            SupportedExtensions = () => ["txt", "md", "html", "rtf", "csv", "json"],
            ExtractionFactory = () => new ExtractorClient(HostPath),
        });
        _engine.Start();
        return _engine;
    }

    private static bool WaitFor(Func<bool> cond, int ms = 30_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(100); }
        return cond();
    }

    private int Count(string q, DeepScope scope = DeepScope.ContentOnly, string[]? ext = null) =>
        _engine!.DeepSearch.Search(new DeepSearchRequest { Query = q, Scope = scope, Extensions = ext ?? [] }, default).Results.Count;

    [Fact]
    public void ContentIsExtractedIndexedAndSearchable()
    {
        string src = Path.Combine(_base, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "memo.txt"), "Réunion du comité : le budget prévisionnel de vidéosurveillance est validé à Lucens.");
        File.WriteAllText(Path.Combine(src, "page.html"), "<html><head><title>Charte</title><script>var hidden='zzsecretzz';</script></head><body><p>Politique de s&eacute;curit&eacute; informatique</p></body></html>");
        File.WriteAllText(Path.Combine(src, "notes.rtf"), @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0 Proc\'e9dure de sauvegarde quotidienne\par}");
        var e = Start(src);
        Assert.True(WaitFor(() => e.ContentDb.Counts().Docs >= 3), "documents indexed: " + e.ContentDb.Counts().Docs);

        var r = e.DeepSearch.Search(new DeepSearchRequest { Query = "videosurveillance budget", Scope = DeepScope.ContentOnly }, default);
        var hit = Assert.Single(r.Results);
        Assert.EndsWith("memo.txt", hit.FullPath);
        Assert.Contains("vidéosurveillance", hit.Snippet);
        Assert.NotEmpty(hit.SnippetHighlights);

        Assert.Equal(1, Count("\"politique de sécurité\""));
        Assert.Equal(0, Count("zzsecretzz"));             // <script> content is not indexed
        Assert.Equal(1, Count("procedure sauvegarde"));   // RTF \'e9 decoded, accent-insensitive
        Assert.Equal(0, Count("budget -lucens"));
        Assert.Equal(1, Count("budget OR charte", ext: ["html"]));

        // Modification → re-extraction; deletion → removal from the content index.
        File.WriteAllText(Path.Combine(src, "memo.txt"), "Nouveau contenu : inventaire des serveurs.");
        Assert.True(WaitFor(() => Count("inventaire") == 1), "re-extraction");
        Assert.Equal(0, Count("vidéosurveillance"));
        File.Delete(Path.Combine(src, "page.html"));
        Assert.True(WaitFor(() => Count("politique") == 0), "deletion");
    }

    [Fact]
    public void ExcludedContentFolderIsIndexedByNameOnlyAndExclusionPurgesStoredText()
    {
        string src = Path.Combine(_base, "src"), downloads = Path.Combine(src, "Downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads, "facture_suspecte.txt"), "contenu zzdownloadzz jamais lu");
        File.WriteAllText(Path.Combine(src, "rapport.txt"), "contenu zzrapportzz lu");
        var e = Start(src, c => c.Content.ExcludedFolders = [downloads]);
        Assert.True(WaitFor(() => Count("zzrapportzz") == 1), "normal content indexed");
        Assert.True(WaitFor(() => e.Search.Search(new Search.SearchRequest { Query = "facture_suspecte" }, default).Results.Count == 1), "name indexed");
        Assert.Equal(0, Count("zzdownloadzz"));                // the content of the excluded folder was never read

        // Re-allowing the folder extracts it; excluding it again drops the stored text.
        e.Config.Update(c => c.Content.ExcludedFolders = []);
        Assert.True(WaitFor(() => Count("zzdownloadzz") == 1), "content indexed once allowed");
        e.Config.Update(c => c.Content.ExcludedFolders = [downloads]);
        Assert.True(WaitFor(() => Count("zzdownloadzz") == 0), "content purged once excluded again");
        Assert.Equal(1, Count("zzrapportzz"));
    }

    [Fact]
    public void ContentStateOfResultsReflectsIndexingQueueAndRules()
    {
        string src = Path.Combine(_base, "src");
        Directory.CreateDirectory(src);
        string memo = Path.Combine(src, "memo.txt");
        File.WriteAllText(memo, "Contenu du mémo indexé.");
        File.WriteAllBytes(Path.Combine(src, "photo.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        File.WriteAllText(Path.Combine(src, "vide.txt"), "");
        var e = Start(src);
        Assert.True(WaitFor(() => e.ContentDb.Counts().Docs >= 1 && !e.ContentIndexer!.Stats().Busy), "content not indexed");

        long Id(string q) => Assert.Single(e.Search.Search(new Search.SearchRequest { Query = q, IncludeApps = false }, default).Results).EntryId;
        Assert.Equal(ContentState.Indexed, e.ContentState(Id("memo.txt")).State);
        var jpg = e.ContentState(Id("photo.jpg"));
        Assert.Equal(ContentState.NotIndexed, jpg.State);
        Assert.Equal(Indexing.ContentIneligibility.UnsupportedType, jpg.Reason);
        var empty = e.ContentState(Id("vide.txt"));
        Assert.Equal(ContentState.NotIndexed, empty.State);
        Assert.Equal(Indexing.ContentIneligibility.EmptyFile, empty.Reason);

        // Modified file: pending until re-extracted, then indexed again.
        long memoId = Id("memo.txt");
        File.WriteAllText(memo, "Contenu modifié, nettement plus long qu'avant.");
        File.SetLastWriteTimeUtc(memo, DateTime.UtcNow.AddMinutes(1));
        e.ReindexPath(memo, content: true);
        Assert.Equal(ContentState.Pending, e.ContentState(memoId).State);
        Assert.True(WaitFor(() => e.ContentState(memoId).State == ContentState.Indexed), "re-indexed");
    }

    /// <summary>
    /// Regression (a PDF whose name has none of the words): content words must be found by the advanced search and by
    /// the launcher; a strict query that finds nothing is relaxed (prefixes, then — advanced search only — some words).
    /// </summary>
    [Fact]
    public void ContentWordsAreFoundByBothSearchesWithRelaxationWhenStrictFindsNothing()
    {
        string src = Path.Combine(_base, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "Procès-verbal 20261111 - séance_v2.txt"),
            "Alice a présenté le budget à Bob. Consensus sur le code généré. Transfert par codes-barres. Pour le comité de l'association Lumen.");
        File.WriteAllText(Path.Combine(src, "autre.txt"), "Rien à voir : recette de cuisine.");
        var e = Start(src);
        Assert.True(WaitFor(() => e.ContentDb.Counts().Docs >= 2), "content indexed");
        DeepSearchRequest Req(string q, DeepScope scope = DeepScope.NamesAndContent) => new() { Query = q, Scope = scope };

        // Advanced search: every word present → strict match.
        var r = e.DeepSearch.Search(Req("Alice Code LUMEN"), default);
        Assert.StartsWith("Procès-verbal", Assert.Single(r.Results).Name);
        Assert.Equal(Search.ContentMatchMode.AllWords, r.ContentMatch);
        Assert.Contains("Alice", r.Results[0].Snippet);
        // "barre" and "consens" only exist as word beginnings ("barres", "Consensus") → prefixes.
        r = e.DeepSearch.Search(Req("consens barre", DeepScope.ContentOnly), default);
        Assert.Single(r.Results);
        Assert.Equal(Search.ContentMatchMode.AllWordsAsPrefixes, r.ContentMatch);
        // "Python" is in no document → partial results rather than nothing, flagged as such.
        r = e.DeepSearch.Search(Req("Alice Python", DeepScope.ContentOnly), default);
        Assert.StartsWith("Procès-verbal", Assert.Single(r.Results).Name);
        Assert.Equal(Search.ContentMatchMode.SomeWords, r.ContentMatch);
        // Strict mode is still available (no relaxation).
        Assert.Empty(e.DeepSearch.Search(new DeepSearchRequest { Query = "Alice Python", Relaxation = ContentRelaxation.None }, default).Results);

        // Launcher: names find nothing, the content results do.
        Assert.Empty(e.Search.Search(new Search.SearchRequest { Query = "Alice Code LUMEN", IncludeApps = false }, default).Results);
        var c = e.SearchContentForLauncher("Alice Code LUMEN", 4, default)!;
        Assert.StartsWith("Procès-verbal", Assert.Single(c.Results).Name);
        Assert.Single(e.SearchContentForLauncher("consens barre", 4, default)!.Results);
        Assert.Empty(e.SearchContentForLauncher("Alice Python", 4, default)!.Results); // no "some words" in the launcher
        Assert.Empty(e.SearchContentForLauncher("ext:md Alice", 4, default)!.Results);  // ext: honoured
        Assert.Null(e.SearchContentForLauncher("kind:folder Alice", 4, default));       // not a content query
        Assert.Null(e.SearchContentForLauncher("cl", 4, default));                       // too short
    }

    /// <summary>
    /// Regression ("Claude Code CHDEV": three words found only in a PV's text while name matches fill the list): the
    /// content result must take a visible row of the launcher, not be appended below the name results.
    /// </summary>
    [Fact]
    public void ContentResultKeepsAVisibleRowWhenNamesFillTheLauncherList()
    {
        string src = Path.Combine(_base, "src"), dev = Path.Combine(src, "dev");
        Directory.CreateDirectory(dev);
        for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(dev, $"claude-code-chdev-{i}.md"), "rien");
        File.WriteAllText(Path.Combine(src, "Séance comité 20261111 - PV détaillé_v2.txt"),
            "Marc travaille avec Claude. Consensus sur le code généré. Comment continuer CHDev avec l'IA.");
        var e = Start(src);
        Assert.True(WaitFor(() => e.ContentDb.Counts().Docs >= 13 && !e.ContentIndexer!.Stats().Busy), "content indexed");

        const int max = 8;
        var names = e.Search.Search(new Search.SearchRequest { Query = "Claude Code CHDEV", MaxResults = max, IncludeApps = false }, default).Results;
        Assert.Equal(max, names.Count); // names alone fill the list
        Assert.DoesNotContain(names, r => r.Name.StartsWith("Séance"));
        int shown = names.Count(r => r.EntryId > 0);
        var content = e.SearchContentForLauncher("Claude Code CHDEV", Search.LauncherMerge.ContentRows(names.Count, max) + shown, default)!;
        var merged = Search.LauncherMerge.Merge(names, content.Results, max, out int added);
        Assert.Equal(1, added);
        Assert.Equal(max, merged.Count);
        Assert.Contains(merged, r => r.Name.StartsWith("Séance") && r.Snippet != null);
    }

    /// <summary>A set with one file per state/reason; failure, encryption and "still queued" are injected after the real extraction.</summary>
    private SeekbeamEngine StartCoverageSet()
    {
        string src = Path.Combine(_base, "src"), downloads = Path.Combine(src, "Downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(src, "memo.txt"), "contenu du mémo");
        File.WriteAllText(Path.Combine(src, "blank.txt"), "   \r\n   ");
        File.WriteAllText(Path.Combine(src, "vide.txt"), "");
        File.WriteAllBytes(Path.Combine(src, "gros.txt"), new byte[2 * 1024 * 1024]);
        File.WriteAllBytes(Path.Combine(src, "photo.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);
        File.WriteAllText(Path.Combine(src, "data.csv"), "a;b");
        File.WriteAllText(Path.Combine(downloads, "dl.txt"), "jamais lu");
        foreach (var n in new[] { "alpha.md", "bravo.md", "charlie.md" }) File.WriteAllText(Path.Combine(src, n), "# titre " + n);
        var e = Start(src, c => { c.Content.ExcludedFolders = [downloads]; c.Content.ExcludedExtensions = ["csv"]; c.Content.MaxFileSizeMB = 1; });
        Assert.True(WaitFor(() => e.ContentDb.Counts().Docs >= 5 && e.ContentIndexer!.Stats() is { Busy: false, QueueLength: 0 }), "content indexed");
        e.ContentIndexer!.Dispose(); // freeze the queue so that the injected states stay as they are

        long Id(string name) => Assert.Single(e.Search.Search(new Search.SearchRequest { Query = name, IncludeApps = false }, default).Results).EntryId;
        void Inject(string name, ContentStatus st)
        {
            var info = e.Entries.Get(Id(name))!;
            e.ContentDb.Upsert(info.Id, info.MTime, info.Size, "test", 1, st, "", st.ToString());
        }
        Inject("alpha.md", ContentStatus.Failed);
        Inject("bravo.md", ContentStatus.Encrypted);
        lock (e.Catalog.WriteLock)
        {
            using var cmd = e.Catalog.Writer.CreateCommand();
            cmd.CommandText = "INSERT INTO content_queue(entry_id,priority,op,seq,attempts,not_before) VALUES($id,0,0,0,0,0)";
            cmd.Parameters.AddWithValue("$id", Id("charlie.md"));
            cmd.ExecuteNonQuery();
        }
        return e;
    }

    [Fact]
    public void ContentCoverageCountsEveryFileOnceWithItsReason()
    {
        var e = StartCoverageSet();
        var r = e.RefreshContentCoverage().GetAwaiter().GetResult()!;
        Assert.NotNull(r);
        Assert.Equal(10, r.Files);
        Assert.Equal(e.Entries.Counts().Files, r.Files); // same total as the name index
        Assert.Equal(r.Files, r.Indexed + r.NoText + r.Pending + r.Failed + r.NotIndexed);
        Assert.Equal(1, r.Indexed);   // memo.txt
        Assert.Equal(1, r.NoText);    // blank.txt
        Assert.Equal(1, r.Pending);   // charlie.md (queued)
        Assert.Equal(1, r.Failed);    // alpha.md
        Assert.Equal(6, r.NotIndexed);
        var reasons = r.ByReason.ToDictionary(x => x.Reason, x => x.Count);
        Assert.Equal(1, reasons[Indexing.ContentIneligibility.EmptyFile]);
        Assert.Equal(1, reasons[Indexing.ContentIneligibility.TooLarge]);
        Assert.Equal(1, reasons[Indexing.ContentIneligibility.UnsupportedType]);
        Assert.Equal(1, reasons[Indexing.ContentIneligibility.ExcludedType]);
        Assert.Equal(1, reasons[Indexing.ContentIneligibility.ExcludedFolder]);
        Assert.Equal(5, reasons.Values.Sum());
        Assert.Equal((ContentStatus.Encrypted, 1L), Assert.Single(r.ByExtractorStatus));
        Assert.Equal(("jpg", 1L), Assert.Single(r.TopUnsupportedExtensions));
        Assert.Same(r, e.LastContentCoverage);
    }

    [Fact]
    public void ContentCoverageAgreesWithThePerFileLookupWhateverTheBatchSize()
    {
        var e = StartCoverageSet();
        // Per-file truth: the launcher's lookup, file by file.
        var perFile = new Dictionary<string, long>();
        var con = e.Catalog.RentReader();
        var ids = new List<long>();
        try
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT id FROM entries WHERE kind=0";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) ids.Add(rd.GetInt64(0));
        }
        finally { e.Catalog.ReturnReader(con); }
        foreach (long id in ids)
        {
            var s = e.ContentState(id);
            string key = s.State + "/" + (s.DocStatus?.ToString() ?? s.Reason.ToString());
            perFile[key] = perFile.GetValueOrDefault(key) + 1;
        }
        // Census, with batches small enough to cross several batch boundaries (pagination and merge join).
        foreach (int batch in new[] { 1, 3, ContentCoverage.DefaultBatchSize })
        {
            var r = ContentCoverage.Compute(e.Catalog, e.ContentDb, e.Entries, e.Indexer, true, Resources.UnthrottledGate.Instance, default, batch);
            var census = new Dictionary<string, long>();
            void Put(string k, long n) { if (n > 0) census[k] = census.GetValueOrDefault(k) + n; }
            Put("Indexed/None", r.Indexed); Put("NoText/None", r.NoText); Put("Pending/None", r.Pending); Put("Failed/Failed", r.Failed);
            foreach (var (reason, n) in r.ByReason) Put("NotIndexed/" + reason, n);
            foreach (var (st, n) in r.ByExtractorStatus) Put("NotIndexed/" + st, n);
            Assert.Equal(perFile.OrderBy(kv => kv.Key), census.OrderBy(kv => kv.Key));
        }
    }

    [Fact]
    public void ContentCoverageStopsCleanlyWhenCancelled()
    {
        var e = StartCoverageSet();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ContentCoverage.Compute(e.Catalog, e.ContentDb, e.Entries, e.Indexer, true, Resources.UnthrottledGate.Instance, cts.Token, 1));
        Assert.Null(e.RefreshContentCoverage(cts.Token).GetAwaiter().GetResult());
        Assert.False(e.ContentCoverageRunning);
        Assert.Null(e.LastContentCoverage);
        // Cancellation in the middle of the census: a gate that cancels after the second batch.
        using var mid = new CancellationTokenSource();
        var gate = new CancelAfterGate(mid, 2);
        Assert.Throws<OperationCanceledException>(() => ContentCoverage.Compute(e.Catalog, e.ContentDb, e.Entries, e.Indexer, true, gate, mid.Token, 1));
        Assert.Equal(2, gate.Batches);
        // A normal run afterwards still works.
        Assert.Equal(10, e.RefreshContentCoverage().GetAwaiter().GetResult()!.Files);
    }

    private sealed class CancelAfterGate(CancellationTokenSource cts, int batches) : Resources.IWorkGate
    {
        public int Batches;
        public void WaitTurn(Resources.WorkClass cls, CancellationToken ct) { if (Batches == batches) cts.Cancel(); ct.ThrowIfCancellationRequested(); }
        public void Report(Resources.WorkClass cls, int items, long bytes) => Batches++;
        public int BatchSize(Resources.WorkClass cls) => 1;
    }

    [Fact]
    public void ExtractorHostCrashIsIsolatedAndRecovered()
    {
        Directory.CreateDirectory(_base);
        string f1 = Path.Combine(_base, "a.txt"), f2 = Path.Combine(_base, "b.txt");
        File.WriteAllText(f1, "premier fichier");
        File.WriteAllText(f2, "second fichier");
        using var client = new ExtractorClient(HostPath);
        Assert.Equal(ExtractStatus.Ok, client.Extract(new ExtractRequest { Path = f1 }, TimeSpan.FromSeconds(20), default).Status);
        foreach (var p in Process.GetProcessesByName("Seekbeam.ExtractorHost")) { try { p.Kill(); p.WaitForExit(2000); } catch { } }
        var r = client.Extract(new ExtractRequest { Path = f2 }, TimeSpan.FromSeconds(20), default);
        if (r.Status == ExtractStatus.HostCrashed) r = client.Extract(new ExtractRequest { Path = f2 }, TimeSpan.FromSeconds(20), default);
        Assert.Equal(ExtractStatus.Ok, r.Status);
        Assert.Contains("second", r.Text);
    }

    public void Dispose()
    {
        _engine?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_base, true); } catch { }
    }
}
