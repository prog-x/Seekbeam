**English** | [Français](ARCHITECTURE.fr.md)

# Seekbeam — Technical architecture

Version 1.0 — 2026-11-21. Detailed decisions and their alternatives are in [TECHNICAL_DECISIONS.md](TECHNICAL_DECISIONS.md).

## 1. Overview

```
┌────────────────────────────── Seekbeam.exe (WPF, user process, not elevated) ───────────────────────────────┐
│ UI: Launcher · DeepSearch · Settings · Dashboard · Tray        Hotkeys (WH_KEYBOARD_LL + RegisterHotKey)   │
│        │  (ISearchService / IIndexControl / IStatsProvider)                                                │
│ ───────┼──────────────────────────────────────── Seekbeam.Core ───────────────────────────────────────────── │
│ SearchEngine ─► NameIndex (segments mmap + delta)  ─┐      ContentSearch ─► content.db (FTS5)             │
│      │       └► AppIndex (in memory, small)        │                                                      │
│ RankingEngine ◄ UsageStore (local history)          │                                                      │
│ IndexingCoordinator ─► NameQueue ─► DirectoryScanner ─► Catalog (catalog.db, SQLite WAL)                  │
│      │                 ContentQueue ─► ContentIndexer ─► ExtractorClient ══ pipe ══╗                       │
│ FileWatcher · Reconciler · OpportunisticDiscovery · ResourceManager · Logging      ║                       │
│ SourceFileAccess (READ-ONLY)              SeekbeamStorage (WRITES, only under %LOCALAPPDATA%\Seekbeam)       │
└────────────────────────────────────────────────────────────────────────────────────╫──────────────────────┘
                                   Seekbeam.ExtractorHost.exe (AppContainer sandbox + Job Object: bounded RAM/CPU, low priority)
                                   Extractors: text, HTML/XML, OOXML, ODF, RTF, PDF, DOC/XLS/PPT, ZIP
```

A single resident process (UI + engine) and one child extraction process. No Windows service, no driver, no administrator privilege required.

## 2. Projects

| Project | Type | Role |
|---|---|---|
| `src/Seekbeam.Core` | `net10.0-windows` library | complete engine, no UI dependency |
| `src/Seekbeam.App` | WPF `WinExe` | launcher, windows, tray, hotkeys, shell integration |
| `src/Seekbeam.ExtractorHost` | console (windowless) | isolated text extraction: AppContainer sandbox, bounded by a Job Object |
| `tests/Seekbeam.Core.Tests` | xUnit | unit + integration |
| `tests/Seekbeam.Immutability.Tests` | xUnit | SHA-256/metadata integrity, static IL analysis |
| `tools/Seekbeam.DatasetGen` | console | reproducible dataset generator |
| `tools/Seekbeam.Bench` | console | benchmarks (search, indexing, RAM, CPU, I/O) |
| `tools/Seekbeam.FsAudit` | console | independent monitor of file-system changes (dynamic audit) |
| `installer/` | WiX v6 | per-user MSI + portable archive |

## 3. Seekbeam.Core modules

| Namespace | Component | Responsibility |
|---|---|---|
| `SourceAccess` | `SourceFileAccess`, `DirectoryEnumerator`, `NativeFile` | **only** access point to indexed files: read-only open, metadata, enumeration (`NtQueryDirectoryFile`), volume/FileId identity |
| `Storage` | `SeekbeamStorage`, `DataPaths`, `MappedSegmentFile` | **only** write point: DB, segments, config, logs, temp; every path is validated |
| `Config` | `SeekbeamConfig`, `ConfigStore` | validated, versioned, migrated JSON; atomic write |
| `Text` | `Normalizer`, `QueryParser` | case/accent folding → UTF-8, tokens, operators |
| `Catalog` | `CatalogDb`, `EntryStore`, `QueueStore` | SQLite: entries, roots, persistent queues, tombstones, history |
| `NameIndex` | `Segment`, `DeltaBuffer`, `NameIndexManager`, `SegmentMerger` | name search index derived from the catalog (2-level LSM) |
| `Search` | `SearchEngine`, `NameMatcher`, `FuzzyMatcher`, `Ranking` | interactive search, cancellation, top-K |
| `Indexing` | `IndexingCoordinator`, `DirectoryScanner`, `ScopeRules`, `LinkPolicy` | streaming traversal, per-directory diff, priorities |
| `Watching` | `RootWatcher`, `ChangeCoalescer`, `Reconciler` | ReadDirectoryChangesW, coalescing, reconciliation |
| `Discovery` | `OpportunisticDiscovery`, `RecentItemsMonitor` | discovery through "Recent Items" and files opened from Seekbeam |
| `Content` | `ContentDb`, `ContentIndexer`, `ContentSearch`, `Snippets` | FTS5, content queue, snippets |
| `Extraction` | `IContentExtractor` + extractors, `ExtractorClient`, `ExtractorProtocol` | formats, limits, isolation |
| `Apps` | `AppIndex`, `LnkParser`, `AppsFolderEnumerator` | Start menu, desktop, UWP/MSIX, configured .exe files |
| `Resources` | `ResourceManager`, `SystemProbe`, `Throttle` | modes, adaptation, pause, throughput |
| `Diagnostics` | `Log`, `Stats`, `ErrorJournal` | rotating local logs, counters, bounded error journal |

The UI only knows the `SeekbeamEngine` facades (`Search`, `Index`, `Stats`, `Config`).

## 4. Storage

Location: `%LOCALAPPDATA%\Seekbeam\` (or `.\Data\` in portable mode, if `portable.txt` sits next to the executable).

```
config.json            configuration (atomic write: tmp + replace)
db\catalog.db          SQLite WAL: roots, entries, name_queue, content_queue, tombstones, apps, usage, errors, meta
db\content.db          SQLite WAL: content_fts (FTS5 contentless), docs (compressed text for snippets)
index\names-*.seg      immutable Name Index segments (read-only mmap)
index\manifest.json    list of segments + covered revision
logs\seekbeam-*.log      rotating logs (5 × 5 MB)
tmp\                   temporary files (SQLITE_TMPDIR), purged at startup
```

This directory is **automatically excluded** from every root (it is never a protected source).

### 4.1 Catalog (`catalog.db`)

```sql
entries(id INTEGER PRIMARY KEY, parent_id INT, root_id INT, name TEXT, kind INT, size INT, mtime INT,
        ctime INT, attrs INT, file_id INT, name_rev INT, flags INT)
  UNIQUE(parent_id, name)          -- lookup by path, per-directory diff
  INDEX(name_rev)                  -- rebuilding the Name Index delta
name_tombstones(id INT, rev INT)   -- deletions not yet merged into segments
name_queue(id PK, path UNIQUE, root_id, priority, mode, depth, attempts, not_before)
content_queue(entry_id PK, priority, attempts, not_before, reason)
depth_limited(dir_id PK, root_id, hidden)  -- folders at the root's MaxDepth whose content is not explored (schema v2)
roots, apps, usage(path PK, launches, last_used, score), favorites, query_history, errors, meta
```

The path is not stored per file: it is rebuilt through `parent_id` (LRU cache of directory paths). Saving: ~60 % of the size.

`name_rev` is a global monotonic counter assigned on every insertion or change of name/parent/flags. It makes it possible to derive the Name Index state deterministically after a crash.

### 4.2 Name Index (segments + delta)

- **Segment**: immutable file, sorted by `id`: `ids[] int32`, `parents[] int32`, `flags[] byte`, `offsets[] int32`, `text` (normalized UTF-8 names separated by `\n`). Opened as a read-only memory map: the memory used is shareable page cache that the OS can reclaim, not private memory.
- **Delta**: recent records (`name_rev > covered revision`), in memory, in immutable blocks of 4,096 entries published atomically; capped (≤ 256 k entries), then flushed into a segment.
- **Staleness**: one bitset per segment (1 bit/record) marking copies that were superseded (id present in a newer level) or deleted (tombstone with a later revision).
- **Merge**: levels L0 (base) + L1; the delta is merged into L1; L1 is merged into the base when L1 > base/4 or tombstones > base/8. K-way merge by id, streaming, sequential writes, atomic manifest switch, deferred deletion (reader reference counting).
- **Recovery**: at startup, manifest → segments; delta = `SELECT … WHERE name_rev > R`; tombstones = table. No volatile state is needed for consistency.

### 4.3 Content Index (`content.db`)

- `content_fts`: FTS5 `contentless` (`content=''`, `contentless_delete=1`), tokenizer `unicode61 remove_diacritics 2`, `rowid = entry_id`.
- `docs(entry_id PK, mtime, size, extractor, version, status, chars, text BLOB deflate)`: compressed text (capped, 1 M characters by default) for snippets and preview, without rereading the source.
- Lifecycle independent of the catalog: it can be deleted or rebuilt without touching the Name Index.

## 5. Search

### 5.1 Normalization
`Normalizer`: invariant lowercase, NFD decomposition followed by removal of diacritics, ligatures (ß→ss, æ→ae, œ→oe, ø→o, ł→l…), UTF-8 encoding. The same function is used for the index and for queries, which makes search case- and accent-insensitive by construction. UTF-8 is self-synchronizing, so a byte search cannot produce false positives straddling two characters.

### 5.2 Launcher query
Space-separated tokens, in any order, matched as substrings. Prefixes: `ext:pdf,docx`, `kind:file|folder|app`, `-excluded`, `"exact phrase"`, `in:path`.

### 5.3 Algorithm (per segment, in parallel over blocks of 64 k records)
1. For each token, vectorized scan (`Span<byte>.IndexOf`) of the block text → bit mask per record.
2. Full mask → match on the name. Partial mask → if a missing token does not appear in any folder name (pre-check against the sidecars), the record is rejected immediately; otherwise the missing tokens are searched for in the **names of ancestor directories** (mask memoized per directory, walking up through `parent`). At least one token must match the name.
3. Filters (`ext`, `kind`, exclusions, hidden), then score (§6) and a top-K heap per thread.
4. Heap merge, then hydration of the K results from SQLite (original name, path, size, date).
5. If the number of results is below the threshold: **fuzzy** pass (Wu-Manber bitap extended to transpositions, 1 error for tokens ≥ 4 characters, 2 for ≥ 8; pigeonhole prefilter; never on a token containing digits), with a penalized score.
6. A single-character token only matches at the start of a word.

Each query carries a `CancellationToken` checked between blocks. Query N+1 cancels query N. Indexing workers yield during a search (`ResourceManager.SearchActive`).

### 5.4 Deep search
Merges `NameIndex` (names and paths) with `ContentSearch` (FTS5 MATCH, BM25), with filters on type, root, date and size. Syntax: `AND`/`OR`/`NOT`/`-`, `"phrase"`, `prefix*`, `NEAR`. Snippets come from `docs.text`. Text preview with highlighting.

## 6. Ranking (`Search/Ranking.cs`)

Documented and tested additive score (`RankingTests`, query → expected result sets):

| Signal | Weight (default) |
|---|---:|
| exact name (without extension) = query | +1000 |
| name starts with the 1st token | +300 |
| token at a word start (after `_ - . space`, case/digit transition) | +120 / token |
| token as a plain substring | +40 / token |
| all tokens in the name (vs in the path) | +200 |
| tokens in query order | +60 |
| name coverage (token length / name length) | 0..+150 |
| application (shortcut, UWP) | +250 |
| folder | +20 |
| usage: `min(400, 80·log2(1+launches))` × (0.35 + 0.65 × recency decay, 14-day half-life) | 0..+400 |
| favorite | +400 |
| fuzzy | −250 / error |
| path depth | −5 / level |

Usage is capped below the gap between an exact match and a partial match: history can never override a clearly better textual match.

## 7. Indexing

### 7.1 Persistent queues
- `name_queue`: directories to list (priorities: 0 watcher event, 1 opportunistic discovery, 2 initial scan, 3 reconciliation). Deduplicated by path (`UNIQUE`). FIFO order within a priority, which yields a breadth-first traversal: upper levels are indexed first.
- `content_queue`: entries to extract, deduplicated by `entry_id`, `attempts` + `not_before` (exponential backoff, at most 3 attempts, then a `failed` status visible in the dashboard).

### 7.2 DirectoryScanner (per-directory diff)
For one directory: streaming enumeration (`NtQueryDirectoryFile`, `FileIdFullDirectoryInformation`, 64 KB buffer) → comparison with the known children (`SELECT … WHERE parent_id=?`) → insertions, updates, deletions (deleted subtrees in batches of 5,000), queuing of subdirectories and of files eligible for content. Several small directories are grouped into one short transaction (≤ 2,000 rows, ≤ 100 ms). The same mechanism serves the initial scan, event-driven rescans and reconciliation.

### 7.3 Scope and links
`ScopeRules`: roots, exclusions (glob patterns on name/path, extensions), maximum depth, hidden/system files, Seekbeam data directory always excluded.
`LinkPolicy`: reparse points of type symlink, junction or mount point **are not followed by default**; when they are (per-root option), (VolumeSerial, FileId) is checked against a visited set, together with a maximum depth (64) and a traversal budget. Cloud directories (OneDrive, `IO_REPARSE_TAG_CLOUD*`) are traversed normally: they are real directories.

### 7.4 Watching
`RootWatcher`: one recursive `ReadDirectoryChangesW` per root (through `FileSystemWatcher`, 64 KB buffer). Events go through `ChangeCoalescer` (1.5 s window, deduplication by directory, burst detection > 2,000 events/s that falls back to a deferred rescan). A known rename updates the entry in place (id preserved). Other events trigger a shallow rescan of the parent directory. An overflow triggers a reconciliation of the root.
`Reconciler`: at startup (events missed while not running), then periodically (24 h by default), at low priority.

### 7.5 Opportunistic discovery
Non-intrusive sources: (a) the user's "Recent Items" folder (`%APPDATA%\Microsoft\Windows\Recent`, `.lnk` files created by the shell on every open through Explorer or the standard dialog), watched and parsed by our read-only `.lnk` parser; (b) files opened through Seekbeam. Algorithm: path → already indexed? → inside a root? → excluded? → parent directory known? If not, the directory is queued (priority 1), its subdirectories are discovered according to the rules and eligible files are queued for content. The discovery budget is bounded: 1,000 directories per event and per hour.
Not adopted: API hooks, kernel ETW or minifilter driver, considered intrusive or requiring administrator rights (see TD-11).

## 8. Extraction (isolated process)

`ExtractorClient` starts `Seekbeam.ExtractorHost.exe` inside a **Job Object** (`JOB_OBJECT_LIMIT_PROCESS_MEMORY`, `CPU_RATE_CONTROL` as a hard cap, `KILL_ON_JOB_CLOSE`), at *Below Normal* priority and in background I/O mode. The protocol is binary, length-prefixed, over stdin/stdout. A per-file timeout kills the host, restarts it and marks the file as failed. An extractor crash (stack overflow, OOM) therefore cannot affect Seekbeam.

**Sandbox (TD-17).** `SandboxLauncher` creates the host suspended, in an **AppContainer** without capabilities (otherwise a low-integrity restricted token), assigns it to the Job Object (which also allows a single process and no clipboard or desktop access), then resumes it. Only the two stdio pipes are inherited, and exploit mitigations are set at creation. The host never receives a path: `ExtractorClient` opens the source through `SourceFileAccess` and duplicates a `FILE_GENERIC_READ` handle into the host, which verifies its granted access before reading. At start-up the host sends a hello message with the isolation it observes on its own token; a mismatch makes `ExtractorClient` try the next level. `SandboxProvisioning` (Storage) creates the AppContainer profile and the read ACE on the program folder once, never inside the indexing perimeter.

Extractors (`IContentExtractor`: `Id`, `Version`, `Extensions`, `Extract(Stream, TextSink, Limits, CancellationToken)`):

| Extractor | Formats | Implementation |
|---|---|---|
| PlainText | txt md csv tsv log ini cfg conf toml yaml yml json sql css js ts py cs java c cpp h go rs ps1 sh bat… | BOM/UTF-8/Windows-1252 detection, bounded read |
| Markup | html htm xhtml xml svg | in-house tokenizer, entities, script/style ignored, no DTD |
| OOXML | docx xlsx pptx (+ m/x) | `ZipArchive` + `XmlReader` (DtdProcessing.Prohibit) |
| ODF | odt ods odp | `content.xml` + `meta.xml` |
| RTF | rtf | in-house parser (groups, `\uN`, `\'hh`, ignored destinations) |
| PDF | pdf | PdfPig (Apache-2.0), bounded page count |
| Legacy Office | doc xls ppt | in-house CFB reader + Word piece table / BIFF8 SST / PPT atoms |
| Zip | zip | entries processed in memory, bounded (see §8.1) |

### 8.1 Archives
Never extracted to disk. Limits: 2 nesting levels, 10,000 entries, 50 MB decompressed per entry, 200 MB per archive, compression ratio ≤ 100, normalized entry names (`..` traversal rejected), encrypted entries skipped and reported.

## 9. Resource management

`ResourceManager` publishes a `ResourceBudget` every 2 s:

| Mode | Name scan | Content workers | Host CPU (Job) | Host RAM | Content read throughput |
|---|---|---|---|---|---|
| Economy | 2,000 entries/s | 1 | 10 % | 256 MB | 4 MB/s |
| Balanced | 20,000 entries/s | 1 | 25 % | 512 MB | 20 MB/s |
| Performance | unlimited | 2–4 | 60 % | 1 GB | unlimited |
| Automatic (default) | adjusted | adjusted | adjusted | 512 MB | adjusted |

Auto mode signals: system CPU (`GetSystemTimes`), memory (`GlobalMemoryStatusEx`), user idle time (`GetLastInputInfo`), battery (`GetSystemPowerStatus`), full-screen or presentation application (`SHQueryUserNotificationState`), search in progress. Indexing threads run in `THREAD_MODE_BACKGROUND_BEGIN` (very low CPU, I/O and memory priority, managed by the Windows scheduler). Pause and resume are global or per queue; time windows are optional.

## 10. Windows integration (Seekbeam.App)

- **Hotkey**: low-level keyboard hook `WH_KEYBOARD_LL` on a dedicated thread with its own message loop (no UI latency possible). Double-Ctrl state machine: two press-and-release of Ctrl alone, ≤ 400 ms, with no other key in between, left Ctrl, right Ctrl or both. Custom combinations go through `RegisterHotKey`. The hook never swallows the event (`CallNextHookEx` always called).
- **Window**: borderless WPF, DWM rounded corners, pre-created and hidden at startup (shown in < 50 ms), positioned at the center of the active screen or near the cursor.
- **Actions**: `ShellExecuteEx` (open, runas, openas, properties), `SHOpenFolderAndSelectItems`, clipboard. No action modifies a file.
- **Applications**: Start menu and desktop (`.lnk` files parsed by our parser), `shell:AppsFolder` (IShellItem, UWP/MSIX AUMID), `.exe` files from configured folders.
- **Tray**: `NotifyIcon` (menu: search, advanced search, dashboard, pause, settings, quit).
- **Auto start**: `HKCU\…\Run`. Single instance: named mutex + "show" event.

## 11. Immutability guarantee

See [docs/FILE_IMMUTABILITY_POLICY.md](docs/FILE_IMMUTABILITY_POLICY.md). In short: separation between `SourceFileAccess` (read-only) and `SeekbeamStorage` (confined writes), `BannedApiAnalyzers` at compile time, IL analysis of every assembly in the tests, SHA-256 and metadata integrity tests on every scenario, independent dynamic audit (`Seekbeam.FsAudit` and an optional ETW script for administrators).

## 12. Main interfaces

```csharp
public interface ISearchService {
    Task<SearchResultSet> SearchAsync(SearchRequest request, CancellationToken ct);      // launcher
    Task<DeepSearchResultSet> DeepSearchAsync(DeepSearchRequest request, CancellationToken ct);
}
public interface IIndexControl {
    void Pause(WorkClass? cls = null); void Resume(WorkClass? cls = null);
    void ReindexNames(long? rootId = null); void ReindexContent(long? rootId = null);
    void RecordOpen(string path);  // opportunistic discovery + learning
}
public interface IStatsProvider { EngineStats Snapshot(); }
public interface IContentExtractor {
    string Id { get; } int Version { get; } IReadOnlyCollection<string> Extensions { get; }
    void Extract(Stream source, TextSink sink, ExtractionLimits limits, CancellationToken ct);
}
```
