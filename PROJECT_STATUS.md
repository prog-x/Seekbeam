**English** | [Français](PROJECT_STATUS.fr.md)

# Seekbeam — Project status

_Updated: 2026-11-21_

## Current phase
Version 1.1.0 complete: functional, tested, measured, documented and installable. See the final report in `docs/FINAL_REPORT.md`.

## Done (every IMPLEMENTATION_PLAN phase)
| Phase | Status | Evidence |
|---|---|---|
| 0 Analysis and design | ✅ | ARCHITECTURE, TECHNICAL_DECISIONS, plans, RISKS, POLICY, spike |
| 1 Core foundation | ✅ | Storage/SourceAccess/Config/Log/Normalizer, BannedApiAnalyzers |
| 2 Catalog and Name Index | ✅ | NameIndexTests (crash, corrupted manifest, concurrency) |
| 3 Search and ranking | ✅ | SearchTests, TextTests |
| 4 Name Index benchmark | ✅ | docs/BENCHMARKS.md (1 M, 2 M, 5 M synthetic) |
| 5–6 Scanner, queues, watching, reconciliation | ✅ | IndexingIntegrationTests (real file system) |
| 7 Applications | ✅ | AppIndexTests, LnkParserTests |
| 8 WPF application | ✅ | UI Automation smoke test (real double Ctrl, search, Escape, no false trigger) |
| 9–10 Content and advanced search | ✅ | ContentEndToEndTests, 101 extractor tests |
| 11 Opportunistic discovery | ✅ | DiscoverCommand, RecentItemsMonitor, IntegrityTests |
| 12 Resources | ✅ | ResourceDecisionTests; Economy measured at 2,002 entries/s |
| 13 Dashboard and settings | ✅ | WPF windows |
| 14 Immutability | ✅ | docs/FILE_IMMUTABILITY_AUDIT.md (IL, integrity, FsAudit) |
| 15 Installation | ✅ | per-user MSI installed and uninstalled silently; portable zip |
| 16 Validation and performance | ✅ | 1 M disk dataset, benchmarks |
| 17 Finalization | ✅ | README, guides, release notes, final report |

## Tests
307 automated tests green: Core 196, Extraction 101, Immutability 10 (`dotnet test Seekbeam.sln`), plus 36/36 checks on real Windows dialogs (`tools/Seekbeam.DialogCheck`, [docs/DIALOG_NAVIGATION_CHECK.md](docs/DIALOG_NAVIGATION_CHECK.md)).

## Bugs fixed during validation (with a test or a measurement)
- Packing of Win32 structures (wrong metadata size and dates), found by the extractors agent.
- SQLite reader left open while `CommandText` was changed (idle indexer).
- `COUNT(*)` on the content queue in the hot path (139 GB read for 1 M files, brought down to 1.2 GB).
- Empty files queued for content (338k useless writes).
- Wrong "queue empty" detection with several workers and deferred retries.
- AppsFolder enumeration (E_INVALIDARG) replaced by Shell.Application.
- `File.Replace` of the manifest failing on a transient lock (antivirus) at 3.6 M entries → retries with backoff.
- Unstable instance kernel object names (random `string.GetHashCode`), replaced by SHA-256.
- Typo tolerance on numbers, one-character tokens, per-candidate lock (88 ms → 6 ms).

## Known limitations and debt
- Catalog size of about 165 bytes/entry (target 120): secondary SQLite indexes. Possible fix: drop `ix_entries_root`, compact `mtime`/`ctime`.
- At 5 M entries, typing one- or two-letter prefixes reaches 139 ms at p95 (incremental refinement to implement).
- The stored depth of a moved folder's subtree is only updated at the next rescan.
- No OCR; ZIP archives only; no MFT/USN (elevation required).
- ETW audit (administrator) provided but not run.
- "Go to folder": dialogs of elevated applications (UIPI) and the old `SHBrowseForFolder` tree are not driven (the path is copied); dialogs not built on the Windows common dialogs are not recognised; direct typing through the real hook is not covered by automated tests (injected keys are ignored by design).
- Folder index: changing the "ranked lower" rules updates existing folders at the next walk; learned folders come from Seekbeam, recent items and typed paths only (no hook into other applications).
- Automatic re-extraction after an extractor version change is not implemented (use "Reindex content").

## Next possible improvements
USN/MFT mode for elevated installations, optional Windows.Media.Ocr OCR, 7z/rar, jump lists for discovery, binary signing, rich preview (images, PDF).

Since 1.0 (unreleased): "go to folder" in Open/Save/folder dialogs and Explorer with direct typing and Quick Switch (TD-20), folder index outside the locations and path fragments / typed paths (TD-21), UI in English, French, German and Italian ([docs/TRANSLATING.md](docs/TRANSLATING.md)), MIT license, default depth 10 with reporting of unexplored folders, shutdown fix. Planned work, including the rest of the GitHub publication checklist, is tracked in [ROADMAP.md](ROADMAP.md).
