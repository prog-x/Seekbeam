**English** | [Français](RISKS.fr.md)

# Seekbeam — Risk register

| # | Category | Risk | Probability / Impact | Mitigation |
|---|---|---|---|---|
| R1 | Immutability | An API or a dependency writes to a source file | low / critical | `SourceFileAccess` as the only access, BannedApiAnalyzers, IL analysis, SHA-256 tests, FsAudit |
| R2 | Immutability | Hydration of cloud placeholders when reading | medium / high | content skipped for RECALL/OFFLINE attributes, `FILE_FLAG_OPEN_NO_RECALL` |
| R3 | Immutability | NTFS updates the *last access time* on reads (enabled on this machine) | certain / low | OS effect documented and distinguished in the tests; no write by Seekbeam |
| R4 | Immutability | An external application opened from Seekbeam modifies a file | possible / medium | outside Seekbeam's control, stated in the UI and the documentation |
| R5 | Performance | Very short queries (1 character) over millions of entries | medium / medium | parallelism, top-K, cancellation, prefix preference |
| R6 | Performance | I/O-heavy segment merges during the initial scan | medium / medium | 2-level LSM, proportional thresholds, low-priority I/O |
| R7 | Memory | Huge directories (100 k+ children) held in memory during the diff | low / medium | diff in sorted batches, cap, streaming |
| R8 | Memory | Malicious or huge PDF | medium / medium | isolated host, Job Object RAM limit, size and page limits, timeout |
| R9 | Stability | Keyboard hook uninstalled by Windows (timeout) | medium / high | dedicated thread, O(1) processing, health check and reinstallation |
| R10 | Stability | SQLite corruption after a power loss | low / high | WAL, `synchronous=NORMAL`, `integrity_check` at startup after an abnormal shutdown, rebuild possible |
| R11 | Indexing | `ReadDirectoryChangesW` buffer overflow | medium / medium | detection, targeted reconciliation |
| R12 | Indexing | Cycles through junctions or symlinks | medium / high | not followed by default, FileId+volume, depth, budget |
| R13 | Indexing | Access denied, locked files | high / low | isolated errors, bounded journal, retry with backoff |
| R14 | Indexing | Slow or unavailable network shares | medium / medium | root marked unavailable, spaced retries, no watcher if unsupported |
| R15 | Security | XXE or zip bomb in the extractors | medium / high | `DtdProcessing.Prohibit`, ratio, size and depth limits, in-memory extraction |
| R16 | Security | Sensitive data leaking into logs | medium / medium | no query or content logged, paths only in errors, purge |
| R17 | Dependencies | PdfPig at version 0.x | medium / low | pinned version, isolated in the host, replaceable (interface) |
| R18 | Installation | WiX or SDK unavailable offline | low / medium | portable archive as an alternative |
| R19 | Platform | Foreground activation refused (`SetForegroundWindow`) | medium / medium | `AttachThreadInput` technique or simulated ALT, manual tests |
| R20 | Environment | No administrator rights (ETW/Procmon impossible during development) | certain / medium | FsAudit without administrator rights, ETW script provided for elevated validation |
| R21 | Environment | VM with 6.5 GB of RAM and 46 GB free for disk datasets | certain / low | at most 2 M files on disk, 5 M synthetic |
