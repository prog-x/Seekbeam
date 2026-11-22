**English** | [Français](FINAL_REPORT.fr.md)

# Seekbeam 1.0 — Final report

## 1. Features delivered
| Area | Delivered |
|---|---|
| Launcher | double Ctrl (left, right or both, delay), custom shortcuts, window centered or near the cursor, keyboard navigation, actions, icons, highlighting, indexing status, light or dark theme |
| Name search | multiple tokens in any order, substrings, case and accents (including NFD), paths (parent folders), `ext:`, `kind:`, `in:`, exclusions, phrases, typos (bitap with transpositions), approximate suggestions marked ≈, cancellation of stale queries |
| Ranking | documented additive model (exact, prefix, word starts, coverage, order, apps, folders, depth), capped local learning, favorites |
| Applications | Start menu, desktop, UWP/MSIX, executables from configured folders, launch through the shell (UAC respected) |
| Indexing | NtQueryDirectoryFile, per-directory diff, persistent prioritized and deduplicated queues, short transactions, search available from the first files, exclusions, depth, links and cycles, permissions respected, unavailable roots |
| Watching | ReadDirectoryChangesW (splitting of roots with exclusions), coalescing, in-place renames, bursts, overflows, reconciliation at startup and periodically |
| Content Index | FTS5 (BM25, phrases, OR, NOT, prefixes), snippets, preview, filters on type, location, date and size; 8 extractors (text and code, HTML/XML, OOXML, ODF, RTF, PDF, DOC/XLS/PPT, ZIP) in an isolated process under a Job Object |
| Opportunistic discovery | Windows Recent Items and files opened from Seekbeam, hourly budget, scope respected |
| Resources | Economy, Balanced, Performance, Auto (CPU, RAM, idle time, battery, full screen), time windows, pause and resume, search priority, threads in background mode |
| Diagnostics | dashboard (progress, queues, sizes, resources, roots, errors), rotating local logs |
| Installation | per-user MSI without elevation, portable zip, automatic startup, `--exit`, `--data`, `--purge-data` |
| Immutability | SourceFileAccess / SeekbeamStorage separation, BannedApiAnalyzers, IL analysis, SHA-256 tests, FsAudit, ETW script |

**Deliberately excluded features**: renaming, moving, deleting, and changing attributes or metadata (no-modification policy).

## 2. Architecture and technologies
- **Stack**: C# 14 / .NET 10, WPF, SQLite (WAL) via Microsoft.Data.Sqlite, PdfPig. There are only two production dependencies.
- **Name Index**: an LSM of mmap segments scanned with vectorized `IndexOf`, derived from the SQLite catalog.
- **Content Index**: FTS5 contentless.
- **Extraction**: runs out of process.

See [ARCHITECTURE.md](../ARCHITECTURE.md) and [TECHNICAL_DECISIONS.md](../TECHNICAL_DECISIONS.md) (16 documented decisions with alternatives and measurements).

## 3. Tests
185 automated tests, all green:
- **Core (75)**:
  - normalization, parser, bitap;
  - Name Index: merge, crash, corrupted manifest, concurrency;
  - search and ranking, including the example from the specification;
  - integration on a real filesystem: exclusions, depth, watcher, renames and moves, offline reconciliation, cyclic junction, denied ACL, root removal;
  - apps, `.lnk`, resources;
  - end-to-end content with the real host, and host crash.
- **Extraction (101)**: all formats, encodings, malformed files, ZIP bombs, XXE, fuzzing, integrity of the files read.
- **Immutability (9)**: IL analysis and self-test; SHA-256 and metadata integrity in three scenarios, including the real `Seekbeam.exe` killed then restarted.
- **Manual or automated outside the suite**:
  - UI Automation smoke test (real double Ctrl, focus, search, Esc, no false trigger);
  - FsAudit dynamic audit with a positive control;
  - silent MSI install and uninstall.

## 4. Benchmarks (details: [BENCHMARKS.md](BENCHMARKS.md))
- **Real indexing of 1,000,000 files**: 36.8 s (27,159 entries/s), 0.53 core, 199 MB peak private RAM.
- **Search on those 1M real entries**: p50 2.7 ms, p95 7.6 ms, p99 25.8 ms; p95 15.5 ms during indexing.
- **2M entries**: p95 20 ms.
- **5M entries**: p95 51 ms (target 60 ms), index loaded in 21 ms. Typing one-letter prefixes is slower at 5M (139 ms p95).
- **Application at rest**: about 63 to 69 MB of private RAM; startup with a 1M index loaded in about 70 ms.
- **Economy mode**: cap respected (2,002 entries/s, 0.05 core).

## 5. Limitations
See [RELEASE_NOTES.md](RELEASE_NOTES.md) and [FILE_IMMUTABILITY_AUDIT.md](FILE_IMMUTABILITY_AUDIT.md) §8:
- NTFS access time (an OS effect);
- external applications;
- French-only UI;
- no OCR;
- ZIP only;
- no MFT/USN;
- catalog of about 165 bytes/entry;
- ETW not run (no administrator rights);
- unsigned binaries.

## 6. Installation
`Seekbeam-1.0.0-x64.msi` (per user, without elevation) or the portable zip, built by `pwsh build/Build-Release.ps1` into `artifacts/installer/`. See [INSTALLATION.md](INSTALLATION.md).

## 7. Usage
Double Ctrl → type → Enter. `Ctrl+D` for full text, Tab for actions. See [USER_GUIDE.md](USER_GUIDE.md).

## 8. Possible next steps
- **UI localization**: French, English, German, Italian, with translation files that external contributors can edit easily.
- **Publication**: on GitHub (license, CI, contribution files).
- **Indexing**: USN/MFT for elevated installations.
- **Content**: OCR (Windows.Media.Ocr), 7z/rar, re-extraction by extractor version.
- **Discovery**: jump lists.
- **Storage**: a more compact catalog.
- **Distribution**: code signing.
- **Preview**: images and PDF.

See [ROADMAP.md](../ROADMAP.md).
