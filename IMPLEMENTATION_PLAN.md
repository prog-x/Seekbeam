**English** | [Français](IMPLEMENTATION_PLAN.fr.md)

# Seekbeam — Implementation plan

Each phase ends with: an error-free build, green tests, and an update of `PROJECT_STATUS.md`. The phases are reordered compared with the recommended phases of the original specification ([docs/project/SPECIFICATIONS.md](docs/project/SPECIFICATIONS.md) §31) so that the Name Index engine is validated very early, as required by §6.3 of the specifications.

| # | Phase | Goals / tasks | Depends on | Tests | Acceptance criteria | Deliverables |
|---|---|---|---|---|---|---|
| 0 | Analysis & design | environment, engine spike, documents | — | reproducible spike | documented decisions | ARCHITECTURE, TD, plans, RISKS, POLICY |
| 1 | Core foundation | `Normalizer`, `SourceFileAccess`, `SeekbeamStorage`, `DataPaths`, validated JSON config, rotating logs, banned APIs | 0 | normalization unit tests, path guard, config round-trip | no write outside DataRoot possible through the API | Seekbeam.Core |
| 2 | Catalog & Name Index | SQLite schema, `EntryStore`, mmap segments, delta, tombstones, merge, crash recovery | 1 | segments (write/read/merge), consistency after a simulated "crash" | consistent search after a crash | NameIndex |
| 3 | Search & ranking | query parser, multi-token matcher, ancestors, filters, bitap fuzzy matching, ranking, top-K, cancellation | 2 | query → expected sets, `Ant 202611 cahier`, accents, typos | p95 < 30 ms on 2 M (synthetic) | SearchEngine |
| 4 | Name Index benchmark | synthetic generator, 1 M / 2 M / 5 M benchmark (in-proc), RAM | 3 | reproducible benchmark | PERFORMANCE_PLAN targets | docs/benchmarks |
| 5 | Scanner & queues | `name_queue`, NtQueryDirectoryFile enumeration, per-directory diff, scopes, exclusions, depth, links/cycles | 2 | temp trees, junctions, cycles, denied permissions | no loops, rules respected | Indexing |
| 6 | Watching & reconciliation | watcher, coalescing, in-place rename, overflow, reconciliation | 5 | live create/modify/delete/rename/move | index up to date < 3 s | Watching |
| 7 | Applications | `.lnk` parser, Start menu, desktop, AppsFolder (UWP), configured exe files, icons, launching | 1 | `.lnk` parser on real shortcuts | apps findable and launchable | Apps |
| 8 | WPF application | single instance, tray, double-Ctrl hook, RegisterHotKey, launcher, navigation, actions, positioning | 3, 7 | hotkey state machine, positioning, launcher VM | reliable double Ctrl, Esc, Enter | Seekbeam.exe |
| 9 | Content Index | extractors, isolated host + Job Object, `content_queue`, FTS5, snippets | 5 | each extractor, malformed files, timeout, host crash | isolated failures | Content |
| 10 | Deep search | dedicated window, syntax, filters, snippets, preview, actions | 9 | FTS parser, filters | — | DeepSearch |
| 11 | Opportunistic discovery | Recent monitor, rules, budget | 5 | file opened outside known area → indexed | scopes respected | Discovery |
| 12 | Resources | modes, Auto, Job limits, background mode, pause/resume, time windows | 5, 9 | budgets per mode, effective pause | measurable modes | Resources |
| 13 | Dashboard & settings | statistics, queues, errors, roots, pause, reindexing, full configuration | 8–12 | VM | — | UI |
| 14 | Immutability | integrity tests for all scenarios, IL analysis, FsAudit, ETW script, audit | all | Immutability suite | 14 criteria of CLAUDE §29.13 | AUDIT.md |
| 15 | Installation | self-contained R2R publish, WiX MSI, portable, autostart, uninstall | 8 | silent install/uninstall | working installation | MSI, zip |
| 16 | Validation & perf | 1 M / 2 M disk datasets, full benchmark, stability, recovery, optimizations | all | benchmark + stability | §30 criteria | BENCHMARKS.md |
| 17 | Finalization | README, user/developer guides, config, troubleshooting, release notes, final report | all | — | — | docs |
