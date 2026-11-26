**English** | [Français](FILE_IMMUTABILITY_AUDIT.fr.md)

# Seekbeam — Source File Non-Modification Audit Report

Version 1.0 — 2026-11-21. Reference policy: [FILE_IMMUTABILITY_POLICY.md](FILE_IMMUTABILITY_POLICY.md).
Environment: Windows 11 Pro (Hyper-V VM), NTFS, **non-administrator** user, `DisableLastAccess = 2` (last-access time updates enabled), .NET 10.0.15.

## 1. Summary

| Check | Tool | Result |
|---|---|---|
| Build: banned write APIs | BannedApiAnalyzers (RS0030 as error), `src/BannedSymbols.txt` | **0 violations** (green build; suppressions limited to `Storage/` and `SourceAccess/`, justified) |
| IL analysis of the 4 shipped assemblies | `StaticAnalysisTests` (System.Reflection.Metadata) | **0 write calls outside `Seekbeam.Core.Storage`** (2 reviewed exceptions), **0 unreviewed P/Invokes**, 0 SQL `ATTACH` / `VACUUM INTO` |
| Analyzer self-test | `AnalyzerSelfTests` | detects direct writes, writes inside a lambda, inside an async state machine, inside a constructor and inside a setter |
| SHA-256 and metadata integrity (in-process, all formats, links, locks, ACLs, pause, discovery, reconciliation) | `IntegrityTests` | **0 modifications** on 62 entries and on the external junction target |
| Integrity with user changes made while watching | `IntegrityTests` | **0 modifications** outside the files touched by the test; those are exactly in the state written by the test |
| Real `Seekbeam.exe` killed during indexing then restarted, shutdown via `--exit` | `IntegrityTests` + `Run-DynamicAudit.ps1` | **0 modifications**; crash detected and repaired; clean shutdown |
| Independent dynamic audit (kernel notifications) | `Seekbeam.FsAudit` | **0 modification events** on 30,063 entries during the real run (crash included) |
| FsAudit positive control | deliberate write, rename, attribute, timestamp and ACL changes | 5/5 detected (+ 1 access event) |
| Extractors: integrity of the files read | `Seekbeam.Extraction.Tests` (`ExtractFileLeavesTheSourceUntouched`, traversal) | unchanged (SHA-256, size, attributes, creation and write times, listing) |
| ETW with per-process attribution | `tools/audit/Run-EtwAudit.ps1` | **not run** (administrator rights required), see §7 |
| Folder index + path completion (2026-11-26, TD-21): a whole tree walked as a fixed drive, a learned folder, a full re-walk, completion over every folder | `IntegrityTests.FolderIndexAndPathCompletionModifyNothing` | **0 modifications** (65 + 5 + 2 entries) |
| "Go to folder" on real dialogs (2026-11-26, TD-20): Open, Save As, folder picker, old folder browser, Explorer, and the real `Seekbeam.exe` driven by double Ctrl | `tools/Seekbeam.DialogCheck` | **36/36 checks**: every dialog closed by **Cancel** (never validated), Save As kept its typed name, test tree unchanged (SHA-256) — §7.4 |

**Conclusion** (§10): high confidence that Seekbeam performs no modifying operation on source files, within the limits set out in §8.

## 2. Methodology

1. **Architecture review**: identify every possible write channel (.NET System.IO APIs, Win32/NT P/Invoke, shell COM, native SQLite, launched processes, third-party dependencies, clipboard, registry).
2. **Manual review** of every source file (§3), looking for direct and indirect write operations and less obvious execution paths (cleanup, temporary files, archives, shortcuts, icons, repair).
3. **Automated static checks** at build time (BannedApiAnalyzers) and on the compiled IL (lambdas, async code and generated interop code included).
4. **Integrity tests**: reference snapshot → scenario → snapshot → field-by-field comparison.
5. **Independent dynamic verification**: a third-party process observes kernel notifications while the real application runs, with a positive control.
6. **Cross-check**: compare the dynamic results with the static inventory.

## 3. Files and modules reviewed

Full review of the 67 shipped C# files (≈ 10,800 lines):

| Module | Files | File writes? | Finding |
|---|---|---|---|
| `Seekbeam.Core/Storage` | SeekbeamStorage.cs | **yes, only under DataRoot** | path validated (`Resolve`): under DataRoot, no intermediate reparse point, DataRoot itself not a reparse point |
| `Seekbeam.Core/Storage` | SandboxProvisioning.cs | **yes, outside DataRoot, once** | AppContainer profile (Windows-managed) and one read ACE on Seekbeam's program folder (W7, W8); refused when the location touches the indexing perimeter |
| `Seekbeam.Core/SourceAccess` | NativeMethods.cs, SourceFileAccess.cs | no | a single `CreateFileW`, `GENERIC_READ` / `FILE_LIST_DIRECTORY` / `FILE_READ_ATTRIBUTES` masks, `OPEN_EXISTING`, R/W/D sharing, `FILE_FLAG_OPEN_NO_RECALL`; `ReadOnlySourceStream` (every write throws) |
| `Seekbeam.Core/Catalog`, `NameIndex`, `Content/ContentDb` | CatalogDb, CatalogWriter, EntryStore, UsageStore, NameIndexManager, SegmentWriter, RecordSource, ContentDb | through SeekbeamStorage only | databases and segments opened by `SeekbeamStorage.OpenDatabase` / `CreateNew` / `MapReadOnly` |
| `Seekbeam.Core/Indexing`, `Watching`, `Discovery`, `Apps` | NameIndexer, DirectoryScanner, ScopeRules, LinkPolicy, RootWatcher, RecentItemsMonitor, AppScanner, LnkParser, AppEntry, RunCommands | no | reads through SourceFileAccess; `FileSystemWatcher` opens folders with `FILE_LIST_DIRECTORY`; `.lnk` files parsed without `IShellLink::Resolve`; PATH folders listed (not recursive) through SourceFileAccess, PATH and App Paths read from the registry with `OpenSubKey(writable: false)` |
| `Seekbeam.Core/Content` | ContentIndexer, ExtractorClient, ExtractionService, SandboxLauncher, DeepSearch, ContentStateLookup, ContentCoverage | no | starts `Seekbeam.ExtractorHost.exe` (a Seekbeam binary) in an AppContainer (TD-17); sources opened here, read-only handles duplicated into the host; text returned over a pipe. ContentStateLookup and ContentCoverage only read catalog.db and content.db (no source access, no ATTACH) |
| `Seekbeam.Core/Search`, `Text`, `Config`, `Resources`, `Input`, `Diagnostics`, `Engine` | — | config and logs through SeekbeamStorage | — |
| `Seekbeam.Extraction` (25 files, 8 extractors, CFB reader) | — | **none** (IL: 0 calls, 0 P/Invokes) | reads only the `Stream` it is given; ZIP in `ZipArchiveMode.Read`, entries in memory, never `ExtractToFile`; XML with `DtdProcessing.Prohibit`, `XmlResolver = null` |
| `Seekbeam.ExtractorHost` | Program.cs | **none** | stdin/stdout; `PROCESS_MODE_BACKGROUND_BEGIN`; receives handles only, refuses any handle with a write right; reports its own token isolation |
| `Seekbeam.App` (WPF) | 18 files | **none** (IL: 0 calls) | shell actions delegated to Windows (§5), `HKCU\…\Run` registry key, clipboard; "About" links opened by `ShellActions.OpenLink`, which only accepts pages under the project URL (never a local path); the launcher's content-state dot is read from the databases (read-only) |
| `Seekbeam.Core/Indexing` (2026-11-26) | FolderIndexPlanner; folder-index branch of NameIndexer, DirectoryScanner, ScopeRules, RootWatcher | no | plans roots only; the walk reads folder listings through SourceFileAccess (files skipped, never opened); watching with `FILE_NOTIFY_CHANGE_DIR_NAME`; `DriveInfo` (`Name`, `DriveType`, `IsReady`) only |
| `Seekbeam.Core/Search`, `SourceAccess` (2026-11-26) | PathCompleter, NetShares | no | typed absolute path listed through `SourceFileAccess.CreateDirectoryReader` (≤ 20,000 entries); `NetShareEnum` level 1 returns share names, no handle |
| `Seekbeam.App` (2026-11-26) | NavigationTargets, ExplorerShell, DialogKeyRouter, Win32.Windows, LauncherWindow | **none** (IL: 0 calls) | messages to another process's dialog: `WM_GETTEXT`/`WM_SETTEXT` (with timeout), `WM_NEXTDLGCTL`, Enter **posted** to the file-name box with a folder path ending with `\` whose existence is checked just before; `SendInput` of Unicode characters (Esc gives typed letters back to the list); Explorer navigated by `IShellWindows.Navigate2`. Never a click on OK, never a file name of a non-existing path: the dialog navigates, it is never validated |

## 4. Write operations identified (IL inventory, `artifacts/audit/il-inventory.md`)

| # | Location | Purpose | Target | Can it reach a root? | Protection |
|---|---|---|---|---|---|
| W1 | `SeekbeamStorage..ctor`, `CreateNew`, `OpenAppend`, `WriteAllBytesAtomic` → `Directory.CreateDirectory` | data folders | `<DataRoot>\{db,index,logs,tmp,cache}` | no | `Resolve` (under DataRoot, no reparse point) |
| W2 | `SeekbeamStorage.WriteAllBytesAtomic` → `FileStream`, `File.Replace`, `File.Move` | config.json, manifest.json | DataRoot | no | same; temporary file created in `<DataRoot>\tmp` |
| W3 | `SeekbeamStorage.CreateNew` / `MappedFile` → `FileStream`, `MemoryMappedFile.CreateFromFile` | Name Index segments | `<DataRoot>\index` | no | same; mapping with `MemoryMappedFileAccess.Read` |
| W4 | `SeekbeamStorage.OpenAppend` | logs | `<DataRoot>\logs` | no | same |
| W5 | `SeekbeamStorage.TryDelete`, `CleanTemp`, `DeleteAllOwnedData` → `File.Delete` | retired segments, temporary files, purge | DataRoot | no | `Resolve` on every path (including enumerated ones) |
| W6 | `SeekbeamStorage.OpenDatabase` → `SqliteConnection` | catalog.db, content.db (+ -wal/-shm) | `<DataRoot>\db` | no | path from `Resolve`; `temp_store=MEMORY`; `sqlite3_win32_set_directory` → `<DataRoot>\tmp` |
| W7 | `SandboxProvisioning.GrantAppContainerRead` → `FileSystemAclExtensions.SetAccessControl` | let the sandbox load its code | Seekbeam program folder | **refused if it does** | `ScopeRules.TouchesScope` (locations); the program folder is excluded from the folder index (TD-21, found by code review) so it is never inside the perimeter; only adds one read ACE, once |
| W8 | `SandboxProvisioning.Ensure` → `userenv!CreateAppContainerProfile` | AppContainer profile | `%LOCALAPPDATA%\Packages\seekbeam.extractorhost`, HKCU mapping | **refused if it does** | same check; Windows-managed object; removed by `--purge-data` |
| E1 | `SandboxLauncher.Start` → `CreateProcessW` / `CreateProcessAsUserW` | extraction process | — (no file) | — | Seekbeam executable next to Seekbeam.exe; AppContainer or low integrity; suspended, then Job Object; no child process |
| E3 | `SourceFileAccess.DuplicateReadHandleInto` → `DuplicateHandle` | hand a source to the sandbox | source | **read-only** | access mask `FILE_GENERIC_READ`; only caller of `SourceNative.DuplicateHandle` (IL check) |
| E4 | `SourceFileAccess.FromReceivedHandle` → `new FileStream(SafeFileHandle, FileAccess.Read)` | host reads the source | source | **read-only** | granted access checked with `NtQueryObject`; any write right is refused |
| E2 | `SourceFileAccess.OpenRead` → `new FileStream(SafeFileHandle, FileAccess.Read)` | read | source | **read-only** | the handle is opened with `GENERIC_READ`: the kernel refuses writes |
| N1 | `SourceNative.CreateFileW` (P/Invoke) | open source | source | read-only | called only by `SourceNative.Open`, constant read masks |

P/Invokes: Seekbeam.Core 50, Seekbeam.dll 49, ExtractorHost 6 (2026-11-26: +`NetShareEnum`/`NetApiBufferFree` in Core; +17 user32 entry points in the App for window recognition, control text, focus, key translation and input replay, all reviewed and justified in the allow-list). All are on the reviewed allow-list (hooks, hotkeys, windows, DWM, tray, icons, shell, Job Objects, system probes, file reads, sandbox: process creation, token restriction, AppContainer profile, handle duplication). Sensitive entry points are pinned to one declaring type (IL check): `CreateFileW` and `DuplicateHandle` to `SourceNative`, `CreateProcessW` / `CreateProcessAsUserW` / `SetTokenInformation` to `SandboxLauncher`, `CreateAppContainerProfile` / `DeleteAppContainerProfile` to `SandboxProvisioning`. **None** of the banned entry points (`WriteFile`, `DeleteFile`, `MoveFile*`, `SetFile*`, `NtSetInformationFile`, `NtCreateFile`, `DeviceIoControl`, `SHFileOperation`, `Set*Security*`…) is declared.

**Third-party dependencies** (informational, IL analyzed): PdfPig contains a single write site (`PdfMerger.OpenReadShared` → `FileStream`), unreachable because Seekbeam does not merge PDFs and only passes read-only streams. Microsoft.Data.Sqlite writes only to the open database, whose path comes from SeekbeamStorage. SQLitePCLRaw (154 P/Invokes into e_sqlite3) writes only to the open database files and to the configured temporary directory.

## 5. Risks identified and fixes applied

| Risk | Severity | Fix / decision |
|---|---|---|
| `IShellLink::Resolve` can rewrite a `.lnk`; `IPersistFile::Load` opens with uncontrolled flags | high | in-house read-only MS-SHLLINK parser (TD-12); Start menu and Recent Items `.lnk` files are never opened through COM |
| Reading a cloud placeholder (OneDrive) downloads it and changes its attributes | high | content skipped when `RECALL_ON_DATA_ACCESS`, `RECALL_ON_OPEN` or `OFFLINE` is set; every open uses `FILE_FLAG_OPEN_NO_RECALL` (TD-13) |
| SQLite temporary files in `%TEMP%` or elsewhere | medium | `temp_store=MEMORY` + SQLite temporary directory forced under DataRoot |
| A junction placed in the data folder could redirect a write to a source | medium | `SeekbeamStorage.Resolve` refuses any reparse point between DataRoot and the target, and refuses a DataRoot that is a reparse point |
| DataRoot located inside an indexed root (user profile) | medium | DataRoot always excluded from the scope (`ScopeRules.IsDataDirectory`); a root *inside* DataRoot is refused |
| Archive extraction to disk, `..` paths | high | extraction exclusively in memory, bounded; dangerous names are only displayed |
| Modifying features (rename, move, delete, attributes) found in Listary/DocFetcher | high | **not implemented** (policy §4); "Exclude from index" only changes the Seekbeam configuration |
| Locking a file in a way that disturbs another application | low | R/W/D sharing everywhere; files locked by others are retried later (test: file opened with `FileShare.None`) |
| Preventing the deletion of a watched folder | low | `FileSystemWatcher` handles with full sharing; a watched folder can still be deleted (the deletion completes when the handle is closed) |
| Wrong metadata reads (size, dates) because of incorrect Win32 struct packing | medium (functional fix) | `Pack = 4` on `WIN32_FILE_ATTRIBUTE_DATA` and `BY_HANDLE_FILE_INFORMATION` + regression test (bug found during the audit) |
| Methodological bias: the snapshot tool read the dates before hashing | low | metadata read after hashing (`Refresh`) |
| A malicious document exploits a parser flaw: arbitrary code in the extraction host | high | AppContainer sandbox without capabilities, sources handed as read-only handles, Job Object, mitigations (TD-17); proved by `SandboxTests.SandboxConfinesACompromisedExtractor` (reading another file, writing, network, child process, registry: all denied) |
| Files from the Internet and e-mail parsed without being opened | medium | Downloads excluded from content extraction by default, with an explanation before re-enabling it (TD-18) |
| Portable mode: the AppContainer ACE on the program folder would be inherited by `Data\` (index and extracted text readable by any AppContainer) | high (found before release) | inheritance blocked at the DataRoot before any grant, AppContainer ACEs removed, existing exposure repaired at start-up; test `PortableDataDirectoryIsNeverReadableByAppContainers` |
| Integrity test silently limited by the default depth of 10: the > 260-character path was no longer extracted | low (test coverage) | integrity dataset indexed with unlimited depth; 20/20 markers again |
| "Go to folder" could make a Save As dialog **save** (overwrite) a file | high (TD-20) | never a click on OK; only a folder path ending with `\`, checked to exist just before, in the file-name box, then Enter posted: the dialog navigates. A folder that vanished meanwhile only shows the dialog's own error. Found in the lab (a click with an unrecognised box saved), then verified on the real dialogs: every dialog ends **Cancel** (§7.4) |
| In a folder picker, OK *selects* the folder and closes the dialog | medium (functional) | Enter instead of OK (navigates); the box is left as the dialog sets it |
| With the focus in the file list (direct typing), Enter posted to the box was ignored | medium (functional, found by DialogCheck) | `WM_NEXTDLGCTL` gives the box the focus first; re-verified |
| The folder index lists every fixed drive: many more sources are read | medium | listings only, through `SourceFileAccess` with the same masks; files never recorded nor opened; Windows, caches and build folders excluded; integrity test on a whole tree |
| Keys swallowed in other applications (direct typing) | low (functional) | only in a recognised dialog whose FILE LIST has the focus, never in a text box, never injected keys; switch in Settings |
| Sandbox read ACE on Seekbeam's program folder, which the folder index walks (C:\Program Files) | high (§29.3, found by code review) | the program folder is always excluded from the folder index (`NameIndexer.ExcludedFromFolderIndex`), so the ACE is never set inside the perimeter; test `ProgramFolderIsNeverWalked` |
| Removing the folder index (a whole drive) in one huge transaction | medium (§13, found by code review) | deletion by root, 20,000 rows per transaction, resumable after a crash; test `RemovingTheFolderIndexDeletesInSmallTransactions` |
| A burst of new folders under a drive re-walked the whole drive at high priority | medium (§14, found by code review) | folder-index events are rescanned folder by folder (shallow), at reconciliation priority during a burst; a watcher overflow re-walks at the lowest priority, at most once an hour |
| Links of a location into a folder-index area were no longer followed | medium (functional, found by code review) | link targets are checked against the locations only; test `LinksOfALocationAreStillFollowedIntoTheFolderIndexArea` |

## 6. Test results (run of 2026-11-22)

- `Seekbeam.Core.Tests`: **104/104**. Integration on a real file system: exclusions, depth, watcher (creation, rename, file and folder moves, deletion, subtree), offline reconciliation, cyclic junction, denied ACL, root removal, end-to-end content pipeline, extraction host crash, Downloads indexed by name only, and the sandbox (`SandboxTests`: every isolation level, extraction through brokered handles, a write-capable handle refused, and the confinement matrix of a probe playing a compromised extractor).
- `Seekbeam.Extraction.Tests`: **101/101**. Malformed files, bombs, XXE, fuzzing, integrity of the files read.
- `Seekbeam.Immutability.Tests`: **9/9**:
  - `FullIndexingOfEveryFormatLinksErrorsAndLocksModifiesNothing`: 62 entries (20 formats, nested ZIP, 40 MB bomb, `../` traversal, malformed files, 60 MB above the limit, read-only, hidden, system, `Zone.Identifier` ADS, Unicode, path > 260 characters, cyclic junction and external junction, folder with a deny ACL, locked file), all extracted inside the AppContainer sandbox; 20/20 content markers found; **0 modifications**.
  - `IncrementalWatcherChangesByOthersAreTheOnlyChanges`: **0 modifications** outside the files changed by the test.
  - `RealProcessKilledDuringIndexingThenRecoveredModifiesNothing`: **0 modifications**, crash detected, clean `--exit`.
  - Static analysis (4 tests) + analyzer self-test.
- Fields compared: SHA-256, size, attributes, creation, last write, SDDL (DACL, owner, group), alternate data streams, reparse target, existence (creations, deletions, renames). `LastAccessTime` is reported separately (§8.1).

**Run of 2026-11-26** (folder index and "go to folder", TD-20/TD-21): `Seekbeam.Core.Tests` **196/196** (including the folder index end to end on a real tree: files never recorded, exclusions, AppData reached, directory events, learning, location added/removed, switch-off, catalog v2 → v3 migration), `Seekbeam.Extraction.Tests` **101/101**, `Seekbeam.Immutability.Tests` **10/10**: the new `FolderIndexAndPathCompletionModifyNothing` walked 32 folders, completed 89 paths, learned a folder outside the tree: **0 modifications** on the walked tree (65 entries), the learned folder (5) and the external target (2); the static analysis passes with the 19 new P/Invokes reviewed.

## 7. Dynamic verification

### 7.1 FsAudit (no privileges) — `tools/audit/Run-DynamicAudit.ps1`
Scenario: audit dataset + 30,000 bulk entries (10% with text) → snapshot → FsAudit → `Seekbeam.exe --data` (isolated profile, Performance mode, links followed) → **forced kill after 2 s** (during extraction) → restart → full indexing (13,123 documents) → `--exit` → snapshot.

| Run | Entries | MODIFY | ACCESS | Snapshot differences | Script verdict |
|---|---|---|---|---|---|
| 2026-11-21 11:33 | 62 | 0 | 0 | 0 | invalid scenario: the script detected completion too early (log line from before the crash); fixed |
| 2026-11-21 11:40 | 62 | 0 | 0 | 0 | FAIL: "content finished" criterion never emitted (all content was done before the kill); bulk dataset added |
| 2026-11-21 11:41 | 30,063 | 0 | 0 | 0 | PASS |
| 2026-11-21 11:43 | 30,063 | 0 | 0 | 0 | PASS (after fixing the multi-worker "drained" detection) |

No run has ever observed a modification. The first two negative verdicts came from defects in the orchestration script, which have since been fixed.

Reports: `artifacts/audit/dynamic-audit-*.md`, `artifacts/audit/fsaudit-last.txt`.
**Positive control**: applied deliberately to a control folder, a write, a rename, setting read-only, a timestamp change and an ACL change produce 5 MODIFY events (and 1 ACCESS). The zeros above are therefore meaningful.

### 7.2 ETW — `tools/audit/Run-EtwAudit.ps1`
It attributes each operation (Create, Write, SetInfo, Delete, Rename) to the Seekbeam process using `wpr -start FileIO` and `tracerpt`. **Not run**: the development session is not elevated. Run it from an administrator console to complete the per-process attribution.

### 7.3 Static / dynamic cross-check
The static inventory allows writes only under DataRoot. The dynamic observations show no write under the source root, and the process writes measured by the benchmark (`GetProcessIoCounters`) match the size of the databases and segments in the data folder. Both approaches agree.

### 7.4 Real dialogs — `tools/Seekbeam.DialogCheck` (2026-11-26)
The tool opens real Windows dialogs (Open, Save As, folder picker, old folder browser) and an Explorer window, drives them with the very code the launcher ships, then closes every dialog with **Cancel** and checks its result. With `--app`, it also drives the real `Seekbeam.exe` (isolated profile) with a simulated double Ctrl and typed queries. Every dialog returned **Cancel**: none was ever validated; the Save As dialog kept the typed name in every case; the test tree (files and folders, SHA-256) is unchanged. Full results: [DIALOG_NAVIGATION_CHECK.md](DIALOG_NAVIGATION_CHECK.md).

## 8. Limits of the guarantee

1. **Last access time**: NTFS (`DisableLastAccess = 2`) may update `LastAccessTime` when a file is read or a folder is listed, in increments of about one hour. This is an OS effect, not a Seekbeam write. Avoiding it would require opening the source with `FILE_WRITE_ATTRIBUTES` (`SetFileTime(-1)`), which the policy forbids. During the audits, FsAudit received **no** access notification. The access differences seen by the snapshot comparison (57 of 62 and 6,084 of 30,063) come from reads by the snapshot tool itself (hashing) and from NTFS deferred writes. They are classified separately.
2. **External applications**: "Open", "Open with", "Run as administrator" and "Properties" delegate to Windows, and so does launching a Run command or a user alias (with its arguments, elevated if the alias asks for it). The built-in `hosts` alias opens a system file in Notepad as administrator on purpose. The opened application, or the user in the Properties dialog, may modify the file. Seekbeam cannot control this; it is pointed out in the UI (tooltip) and in the guide.
3. **Shell components**: icons (`SHGetFileInfo`, `IShellItemImageFactory`) and `shell:AppsFolder` enumeration. Icons for ordinary files are obtained by extension, without disk access. Those for `.exe`, `.lnk`, `.ico` and `.url` files are read by the shell, whose contract is read-only but whose code has not been audited.
4. **Cloud providers**: `FILE_FLAG_OPEN_NO_RECALL` and attribute filtering prevent hydration, but a non-compliant third-party provider could react to enumeration. Not tested with a real OneDrive account (none configured on the audit machine).
5. **Antivirus and third-party filters**: they may react to reads; this is out of scope.
6. **Non-NTFS file systems and network shares**: the read-only logic is the same, but they have not been audited dynamically.
7. **Per-process attribution**: FsAudit observes effects without attributing them. The ETW audit still has to be run with administrator rights.
8. **Native SQLite**: the native library cannot be analyzed through IL. Its writes are confined by the path it is given, and dynamic observation confirms this.

## 9. Items still to verify
- Run `Run-EtwAudit.ps1` in an administrator session.
- Dynamic audit on a OneDrive folder with "online-only" files.
- Audit on an SMB network share and on an exFAT/FAT32 volume.

## 10. Conclusion
Non-modification rests on four independent layers, all green:
1. an **architecture** in which a single class can write, and only into its own folder, while sources are opened only with read access masks, which the kernel enforces;
2. **static checks** at build time and on the real IL;
3. exhaustive **integrity tests**, including on the real executable killed in the middle of indexing;
4. an **independent dynamic observation**, validated by a positive control.

The confidence level is **high** for local NTFS files. The guarantee does not cover OS effects (access time), external applications opened by the user, or the third-party components listed in §8. Per-process attribution through ETW still has to be run in an elevated session.
