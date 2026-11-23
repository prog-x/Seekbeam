**English** | [Français](TECHNICAL_DECISIONS.fr.md)

# Seekbeam — Technical decisions

Format: context · alternatives · decision · rationale · consequences. Update on every structural decision.

---

## TD-01 — Language and runtime: C# / .NET 10

**Context.** We need a native Windows application, many Win32/COM APIs (hooks, shell, Job Objects, NtQueryDirectoryFile), high scan performance, and test and packaging tooling available on the machine. The .NET 10.0.404 SDK with WindowsDesktop is installed. Rust is present, but without the MSVC toolchain or Visual Studio C++.

| Option | Pros | Cons |
|---|---|---|
| C# / .NET 10 | simple P/Invoke and COM, `Span<T>` and SIMD (vectorized `IndexOf`), controllable GC, xUnit, Roslyn analyzers, mature WPF | runtime ~ 40–70 MB base RAM, JIT (mitigated by ReadyToRun) |
| C++ / Win32 | minimal footprint | no compiler on the machine, low productivity and memory safety for parsers of hostile formats |
| Rust | performance, memory safety | no MSVC linker available; immature native Windows UI (no mature native UI framework); verbose COM |
| Electron | — | excluded by CLAUDE.md (memory, startup) |

**Decision.** C# 14 / .NET 10, `net10.0-windows`, *self-contained* ReadyToRun publishing.
**Rationale.** Best trade-off between Windows integration, measured performance (§TD-03) and maintainability. The critical code (scan) reaches ~13 GB/s per core thanks to the vectorized `IndexOf`. Parsers of untrusted formats benefit from memory safety.

## TD-02 — UI: WPF

| Option | Pros | Cons |
|---|---|---|
| WPF | mature, GPU rendering, virtualized list, no packaging dependency, testable | less "Fluent" look without extra work (fixed through DWM: rounded corners, shadow) |
| WinUI 3 / Windows App SDK | native Fluent | Windows App SDK runtime to deploy, higher startup time and RAM, borderless windows trickier, tooling more fragile outside VS |
| WinForms | lightweight | GDI rendering, rich formatting (highlighting) laborious |

**Decision.** WPF. The launcher window is pre-created and hidden, which makes showing it almost instantaneous. `AllowsTransparency` is not used (it forces slow software rendering); `DwmSetWindowAttribute` is used instead.

## TD-03 — Name Index engine: mmap segments + vectorized scan (not FTS5)

**Context.** The Name Index is the top priority: substrings, free order, accents, typos, several million entries, controlled RAM.
**Measurements** ([docs/benchmarks/2026-11-21_spike_name_engine.md](docs/benchmarks/2026-11-21_spike_name_engine.md), 2 M names): 8-thread scan 1–5 ms (15 ms for the query `a` with 1.7 M results), 67 MB; FTS5 trigram 2–33 ms (151 ms for `a`), 298 MB, 10 s build time, no substrings < 3 characters and no fuzzy matching; `LIKE` 150–220 ms.

| Option | Pros | Cons |
|---|---|---|
| Scan of an mmap blob (Everything style) | fastest, compact, predictable cost, any substring, bitap fuzzy matching on the same blob | derived structure to maintain (LSM) |
| SQLite FTS5 trigram | natively incremental | 4.4× larger, slower in the worst case, no fuzzy matching, ≥ 3 characters |
| In-house inverted index (n-grams) | very fast selective queries | size, update complexity, no better for short queries |
| Everything in memory (managed arrays) | simple | linear private RAM (criterion 15 of the specifications) |

**Decision.** SQLite = source of truth. Name Index = immutable memory-mapped segments (OS page cache, reclaimable, not private) + bounded in-memory delta + persistent tombstones, 2-level LSM merge, consistency derived from `name_rev` (see ARCHITECTURE §4.2).
**Consequence.** Private RAM per entry is limited to 1 staleness bit and the bounded delta. Mapped pages count towards the working set, but the OS can reclaim them at any time.

## TD-04 — Database: SQLite (Microsoft.Data.Sqlite + SQLitePCLRaw e_sqlite3)

Alternatives: LiteDB (less efficient, no FTS), RocksDB (heavy native dependency, no FTS), ESENT (native to Windows, painful API, no FTS), custom files (corruption risk).
**Decision.** SQLite 3.4x in WAL mode, `synchronous=NORMAL`, short transactions, bounded `cache_size` (8 MB catalog, 4 MB content), `temp_store=MEMORY`, `SQLITE_TMPDIR` pointed at Seekbeam's temporary directory. Licenses: public domain, MIT and Apache-2.0. Two separate databases (`catalog.db`, `content.db`) give independent lifecycles and limit write contention.

## TD-05 — Content Index: contentless SQLite FTS5 + compressed text

Alternatives: Lucene.NET (4.8 still in beta for years, heavy), Tantivy (Rust, no toolchain), in-house inverted index (development cost, risk). FTS5 offers BM25, phrases, prefixes, `NEAR` and Boolean operators, and it is proven.
**Decision.** Contentless `content_fts` with `contentless_delete=1`. Extracted text is stored compressed (Deflate) and capped at 1 M characters per document, solely for snippets and preview. This avoids rereading the source, which would be slower and less safe. The `content.storeText=false` option disables this storage: snippets are then unavailable.

## TD-06 — Enumeration: NtQueryDirectoryFile (FileIdFullDirectoryInformation)

Alternatives: `Directory.EnumerateFiles`/`FileInfo` (no FileId, allocations), `FindFirstFileExW` (no FileId), reading the MFT or the USN journal (`FSCTL_ENUM_USN_DATA`, very fast but **requires administrator rights**, NTFS only).
**Decision.** `NtQueryDirectoryFile` on a handle opened with `FILE_LIST_DIRECTORY` only, with a reused 64 KB buffer. A single call returns the name, size, dates, attributes, FileId and reparse tag (`EaSize` field), with no per-entry allocation. An MFT/USN mode is documented as a possible evolution for elevated installations.

## TD-07 — Watching: ReadDirectoryChangesW + reconciliation

The USN journal would be more reliable, but it requires administrator rights. `ReadDirectoryChangesW` (through `FileSystemWatcher`, `FILE_LIST_DIRECTORY` handle) works without elevation. Its limits (buffer overflow, network shares) are offset by coalescing, overflow detection followed by reconciliation, and reconciliation at startup and periodically. Processing by directory diff is idempotent.

## TD-08 — Global hotkey: WH_KEYBOARD_LL (double Ctrl) + RegisterHotKey (combinations)

`RegisterHotKey` cannot detect a double press of a lone modifier, so the low-level hook is required. The hook runs on a dedicated thread (a slow message loop would make Windows silently uninstall it after `LowLevelHooksTimeout`), returns immediately and never blocks keys. The hook's health is checked periodically and it is reinstalled if needed. Classic combinations (e.g. `Alt+Space`) go through `RegisterHotKey`, which is more robust and detects conflicts.

## TD-09 — Extractor isolation: host process in a Job Object

In-process execution with a timeout protects neither against a `StackOverflowException` nor against an OOM, either of which would kill Seekbeam. The host process is therefore bounded by the kernel (RAM, CPU hard cap, dies with its parent). The cost, one more process (~25 MB), is deemed acceptable, and the host is stopped after 5 min of inactivity. Since TD-17, the host also runs in a sandbox (AppContainer).

## TD-10 — Extraction: in-house parsers + PdfPig

| Format | Choice | Rejected alternatives |
|---|---|---|
| OOXML / ODF | `System.IO.Compression` + `XmlReader` (included in .NET) | DocumentFormat.OpenXml (5 MB, DOM), NPOI |
| PDF | **PdfPig 0.1.x** (Apache-2.0, pure managed, maintained) | PDFium (native, third-party binary), iText (AGPL), Docnet |
| DOC/XLS/PPT | CFB reader + in-house text extraction (piece table, BIFF8 SST, PPT text atoms) | NPOI (≈ 10 MB of dependencies), system IFilter (offfilt opens the file itself, open flags not controllable) |
| RTF / HTML / text | in-house | — |
| ZIP | `System.IO.Compression` in memory | SharpCompress (7z/rar): postponed |

Dependencies are minimized: only two production dependencies (SQLite, PdfPig).

## TD-11 — Detecting opened files: "Recent Items" + files opened from Seekbeam

| Mechanism | Verdict |
|---|---|
| Recent folder (`.lnk` files created by `SHAddToRecentDocs`) | **adopted**: non-intrusive, no rights needed, covers Explorer, common dialogs and most applications |
| Files opened through Seekbeam | **adopted** |
| Jump lists (`AutomaticDestinations`, CFB format) | possible evolution |
| Kernel FileIO ETW / minifilter / injection | **rejected**: administrator or driver required, intrusive, contrary to CLAUDE.md §16 |
| Enumerating other processes' handles | **rejected**: intrusive, costly, unreliable |

Documented limitation: files opened by applications that do not call `SHAddToRecentDocs` are not detected.

## TD-12 — `.lnk` parsing: in-house MS-SHLLINK parser

`IShellLink::Resolve` can **modify** the shortcut (updating its target), and `IPersistFile::Load` opens the file with flags we do not control. The in-house parser reads the data through `SourceFileAccess` (`LinkInfo`, `StringData`, `EnvironmentVariableDataBlock`). The COM fallback is limited to enumerating `shell:AppsFolder`, which does not read `.lnk` files from the indexed roots on our behalf.

## TD-13 — Cloud files (OneDrive, placeholders)

Reading a dehydrated placeholder triggers its download and changes its attributes: that is an indirect modification. Decision: names are indexed, but content extraction skips `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`, `RECALL_ON_OPEN` and `OFFLINE`. In addition, every source open uses `FILE_FLAG_OPEN_NO_RECALL`.

## TD-14 — Installation: per-user WiX v6 MSI + portable archive

Alternatives: Inno Setup (not available on the machine), MSIX (mandatory signing, file-system sandbox awkward for an indexer), custom installer. WiX v6 builds with `dotnet build` through `WixToolset.Sdk`, with no prior installation. WiX v7 was rejected because it requires accepting the OSMF EULA. Installation is per user (`%LOCALAPPDATA%\Programs\Seekbeam`), without elevation. The portable archive stores its data next to the executable.

## TD-15 — Tests: xUnit 2.9; static analysis: BannedApiAnalyzers + IL analysis

xUnit is the .NET standard and well supported by `dotnet test`. For immutability, two complementary checks are in place: (1) `Microsoft.CodeAnalysis.BannedApiAnalyzers` for compile-time errors, (2) a test that decodes the IL of every Seekbeam assembly (`System.Reflection.Metadata`) and verifies that every call to a file-writing API or a writing P/Invoke lies within `Seekbeam.Core.Storage`. Check (2) also catches indirect calls and P/Invokes, which method names alone do not reveal.

## TD-16 — Dynamic audit without administrator rights

Process Monitor and kernel ETW require administrator rights, and the development session does not have them. Decision: `Seekbeam.FsAudit`, an independent process that watches the test dataset with **all** `ReadDirectoryChangesW` filters (name, attributes, size, write, **access**, creation, **security**) while the real Seekbeam.exe runs. It is complemented by a before/after fingerprint comparison. An ETW script (`tools/audit/Run-EtwAudit.ps1`, TraceEvent) is provided for an elevated verification attributed to the process.

## TD-17 — Extractor sandbox: AppContainer, low-integrity fallback, brokered read-only handles

**Context.** Content extraction parses untrusted documents automatically, including files the user never opened. A flaw in a parser (PdfPig, or our own OOXML/ODF/CFB/RTF/ZIP code) must not give a malicious document access to the user's files, the network or the rest of the system. The Job Object of TD-09 bounds resources but not access rights.

**Options compared** (verified by a probe that plays a compromised extractor, `SandboxTests.SandboxConfinesACompromisedExtractor`):

| | Read user files | Write anywhere | Network | Child process | HKCU registry | Requirements |
|---|---|---|---|---|---|---|
| Normal token + Job Object (before) | yes | yes | yes | blocked (job) | yes | none |
| **Low integrity** restricted token | yes | **blocked** | yes | blocked | **blocked** | none |
| **AppContainer**, no capabilities | **blocked** | **blocked** | **blocked** | blocked | **blocked** | AppContainer profile; read ACE on the program folder |
| Less-privileged AppContainer (LPAC) | blocked | blocked | blocked | blocked | blocked | same; **.NET cannot initialise `TimeZoneInfo`**, so every ZIP-based format fails |

**Decision.** The host starts in the strongest level that works: **AppContainer** without any capability, otherwise a **low-integrity** restricted token (all privileges removed), otherwise a normal token (logged as a warning, shown in the dashboard). LPAC was rejected: it breaks DOCX/XLSX/ODT/ZIP and blocks nothing more in the probe tests. In every case the host is created suspended, assigned to its Job Object before its first instruction (memory and CPU caps, a single process, no clipboard or desktop access), inherits only its two stdio pipes, runs with exploit mitigations (no extension points, no remote or low-label images, bottom-up and high-entropy ASLR) and cannot create a child process.

Because an AppContainer cannot open user files, **Seekbeam opens each source itself** (read-only, `SourceFileAccess`) and duplicates into the host a handle restricted to `FILE_GENERIC_READ`. The host verifies the granted access of every handle it receives and refuses any handle with a write, delete, attribute or ACL right. The host therefore never sees a path and can read exactly one file at a time. At start-up it reports the isolation it observes on its own token, and Seekbeam rejects a level that does not match the requested one.

**Set-up** (`SandboxProvisioning`, once): create the AppContainer profile `Seekbeam.ExtractorHost` (Windows creates `%LOCALAPPDATA%\Packages\seekbeam.extractorhost` and a registry mapping), and add a read+execute ACE for *ALL APPLICATION PACKAGES* on Seekbeam's program folder. Neither write happens if the location is inside, or contains, an indexed root: the extractor then falls back to low integrity. In portable mode the data folder is inside the program folder, so inheritance of that ACE is blocked at the data folder first: no AppContainer can read the index. `--purge-data` deletes the profile.

**Cost.** Host start-up is unchanged (~100 ms). Duplicating a handle per document costs a few microseconds.

**Limits.** Low integrity still lets a compromised extractor read the user's files and use the network; this is why AppContainer is preferred and the dashboard shows the level in use. A portable copy unpacked inside an indexed folder stays at low integrity (see Troubleshooting). Kernel or Windows vulnerabilities that escape an AppContainer are out of scope.

## TD-18 — Downloads: names indexed, content never read by default

The Downloads folder receives files from the Internet and e-mail. Content indexing would parse them automatically, without the user ever opening them, which is the classic way to reach a parser vulnerability. Decision: a new setting, `Content.ExcludedFolders` (folders whose files are indexed by name only), contains the Downloads known folder by default (`FOLDERID_Downloads`, which follows redirection). Configurations from schema 1 are migrated once. Removing Downloads from the list in Settings first shows an explanation of the risk and asks for confirmation. Changing the list re-evaluates the content queue: text already stored for a newly excluded folder is deleted.

## TD-19 — Run commands and aliases in the launcher

Users coming from Listary expect `mstsc` or `hosts` to work. Two mechanisms, rather than indexing `C:\Windows` (100,000+ files of noise, and `hosts` has no extension, so "Open" would show the "Open with" dialog):

- **Run commands**: what `Win+R` resolves. Files of the PATH folders (.com/.exe/.bat/.cmd/.msc/.cpl, not recursive) and App Paths registrations. The PATH is read from the registry (machine, then user), not from Seekbeam's environment, so that a change is seen without restarting. Name resolution follows cmd.exe: PATH order, then extension order within a folder. Rescanned every 15 minutes (≈ 900 entries in 10–30 ms on the development machine) instead of watching System32 with `FileSystemWatcher`, which Windows updates would wake constantly.
- **Ranking**: these are hundreds of terse names (msdt, mshta, msra…). They match only on the exact name (or the name without .msc/.cpl) or a prefix of at least 3 letters, never by substring or typo; the application bonus applies to exact matches only. The catalog search stays at about 25 µs per keystroke.
- **Aliases**: user keywords → target, arguments, working folder, run as administrator. Found by keyword only (never by their target), usage learning keyed by the keyword. An exactly typed alias beats a command of the same name.
- **Arguments**: "word rest" where `word` is exactly a command or alias offers that command with `rest` appended to its arguments, scored as the exact command (it tops ordinary matches but does not hide a file whose name is the whole query).
- **Immutability**: directory listing through `SourceFileAccess`, registry opened read-only; launching goes through the existing `ShellExecuteEx` delegation (FILE_IMMUTABILITY_POLICY §8). The `hosts` alias deliberately opens a system file in an editor, elevated: the write, if any, is the user's action in Notepad.
- Not done (yet): a built-in catalog of system shortcuts (`ms-settings:` pages, `shell:` folders, localized keywords).
