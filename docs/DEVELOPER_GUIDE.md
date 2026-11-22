**English** | [Français](DEVELOPER_GUIDE.fr.md)

# Developer guide

## Layout
```
src/Seekbeam.Core            engine (no UI): Storage, SourceAccess, Catalog, NameIndex, Search, Indexing, Watching,
                           Content, Apps, Discovery, Resources, Engine, Config, Text, Input, Diagnostics
src/Seekbeam.Extraction      extractors (text, markup, OOXML, ODF, RTF, PDF/PdfPig, DOC/XLS/PPT via CFB, ZIP)
src/Seekbeam.ExtractorHost   isolated extraction process (stdin/stdout, Job Object)
src/Seekbeam.App             WPF: launcher, advanced search, settings, dashboard, tray, hotkeys
tests/Seekbeam.Core.Tests          unit + integration (real filesystem, watcher, ACLs, junctions, end-to-end content)
tests/Seekbeam.Extraction.Tests    extractors (generated fixtures, malformed files, bombs, fuzzing)
tests/Seekbeam.Immutability.Tests  IL analysis, SHA-256 integrity tests (including the real Seekbeam.exe killed then restarted)
tests/Seekbeam.SandboxProbe        test-only exe playing a compromised extractor (SandboxTests prove what each sandbox level blocks)
tools/Seekbeam.Bench               benchmarks (synthetic, search, index)
tools/Seekbeam.DatasetGen          dataset generator, snapshots and integrity comparison
tools/Seekbeam.FsAudit             independent filesystem change monitor
tools/audit/                     dynamic audit scripts
installer/                       WiX v6 MSI
build/Build-Release.ps1          release pipeline
```

## Commands
```powershell
dotnet build Seekbeam.sln
dotnet test Seekbeam.sln
dotnet run -c Release --project tools/Seekbeam.Bench -- synthetic artifacts/bench/syn1m 1000000
dotnet run -c Release --project tools/Seekbeam.DatasetGen -- tree C:\SeekbeamBench\disk1m 1000000
dotnet run -c Release --project tools/Seekbeam.Bench -- index artifacts/bench/disk1m-data C:\SeekbeamBench\disk1m Performance
pwsh tools/audit/Run-DynamicAudit.ps1
pwsh build/Build-Release.ps1
```

## Continuous integration (GitHub Actions)
- **`.github/workflows/ci.yml`** (push to `main`, pull requests): builds Release, then builds Debug and runs `dotnet test` on `windows-latest`. Test results are uploaded when a test fails. On `main`, a second job runs `build/Build-Release.ps1 -SkipTests` and keeps the MSI and the zip as workflow artifacts for 14 days.
- **`.github/workflows/release.yml`** (tag `v*`): checks that the tag matches `<Version>` in `Directory.Build.props`, runs the full release pipeline with tests, and creates a **draft** GitHub release with the MSI, the portable zip and `SHA256SUMS.txt`.
- **Version**: `<Version>` in `Directory.Build.props` is the only place to change. It names the MSI and the zip, and sets the MSI version.
- **`.github/dependabot.yml`**: weekly NuGet updates, monthly GitHub Actions updates.

## Non-negotiable rules (immutability)
1. Sources are read only through `SourceFileAccess`, and writes go only through `SeekbeamStorage`.
2. `src/BannedSymbols.txt` bans write APIs (compile error RS0030). Suppressing this warning is allowed only in `Storage/` and `SourceAccess/`, with a justification.
3. `StaticAnalysisTests` decodes the IL. Any new write call outside `Seekbeam.Core.Storage`, or any unreviewed P/Invoke, fails the tests. A new P/Invoke must be added to the allow-list **after review**.
4. An extractor reads only the `Stream` it receives: no temporary files and no extraction to disk.
5. Any new feature that touches sources must be covered by `IntegrityTests`.

## Key concepts
- **Name Index**:
  - immutable mmap segments (`names-*.seg`, format v2: ids, parents, flags, offsets, normalized text, plus a sidecar of folder names);
  - an in-memory delta and tombstones;
  - fully derivable from `catalog.db` (`name_rev`);
  - merges happen in `NameIndexManager.FlushLocked`.
- **Catalog writes**: only through `CatalogWriter.Batch` (short transaction, write lock). The `NameChange`s returned by `Commit()` must be applied to `NameIndexManager.Apply` in order.
- **Indexing**: a single `NameIndexer` thread (background mode) processes `name_queue` and the commands (watcher, renames, discovery, roots). `DirectoryScanner.ApplyDiff` is the only synchronization algorithm.
- **Search**:
  - `NameSearcher`: block scan, per-token masks, memoized ancestors, sidecar feasibility, bitap, top-K;
  - `SearchEngine`: apps, hydration, highlighting;
  - `DeepSearch`: FTS5, names, filters and snippets.
- **Resources**: `ResourceManager.Decide` is a pure, tested function. Workers call `IWorkGate.WaitTurn` and `Report`.

## Conventions
- **Language**: C# 14, nullable enabled, file-scoped namespaces.
- **Dependencies**: no new dependency without a review of its license, maintenance, size and transitive dependencies.
- **Comments**: they explain the *why*.
- **Errors**: handled explicitly, and long-running operations take a `CancellationToken`.

## Documentation languages
Project documentation is bilingual:
- **File names**: English in `X.md`, French in `X.fr.md`, in the same folder.
- **Same change**: both versions are updated together. A change to one language is not complete until the other is updated.
- **Language switch**: line 1 of each file links to the other language: `**English** | [Français](X.fr.md)` in English, `[English](X.md) | **Français**` in French.
- **Links**: English documents link to English documents, French documents to the `.fr.md` versions.
- **Not translated**: `CLAUDE.md` (project rules for coding agents and contributors) and `docs/project/SPECIFICATIONS.md` (the original specification, kept as a historical document) are in French only. `LICENSE` and `THIRD-PARTY-NOTICES.md` are legal texts kept in English only.

## User interface languages
The UI ships in English (neutral), French, German and Italian (`src/Seekbeam.App/Localization/Strings*.resx`).
- **No hard-coded visible text**: XAML uses `{l:Tr Key}`, C# uses `L.T("Key")` or `L.F("Key", args)`.
- **Four languages in the same change**: a new or changed key is added to the four `.resx` files at once.
- **Formats**: text follows the UI language, numbers and dates follow the Windows regional settings (`CultureInfo.CurrentCulture`).
- **Engine strings**: `Seekbeam.Core` returns codes (resource profile names, reasons, content status) that the UI translates; it never builds user-visible sentences.
- **Checks**: `LocalizationTests` fail on a missing or unknown key, a placeholder mismatch, an unknown key in the code, or hard-coded text in XAML or C#.

See [TRANSLATING.md](TRANSLATING.md) for the translator workflow.

## Adding an extractor
1. Implement `IContentExtractor` (`Id`, `Version`, `Extensions`, `Extract(Stream, ext, TextSink, ExtractionContext, ct)`).
2. Register it in `BuiltInExtractors.All()`.
3. Add tests with generated fixtures, malformed files and limits.

The extractor version is stored per document (`docs.version`). After changing an extractor, use **Reindex content** in the dashboard. Automatic re-extraction by version is a planned improvement.
