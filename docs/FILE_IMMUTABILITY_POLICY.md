**English** | [Français](FILE_IMMUTABILITY_POLICY.fr.md)

# Seekbeam — Source File Non-Modification Policy

Version 1.0 — 2026-11-21. Status: **normative**. This requirement takes precedence over any feature, optimization or implementation convenience.

## 1. Requirement

Seekbeam never modifies, moves, renames, deletes or alters the files and folders it indexes. It creates no file in them, temporary files included.

## 2. Protected scope

The following are **protected sources**:
1. any file or folder located under a configured root, subfolders included, **exclusions included** (an excluded folder is not indexed but remains protected);
2. any opportunistically discovered file, and the user's "Recent Items" folder;
3. any file opened or analyzed by an extractor, and any file contained in a processed archive;
4. the shortcuts and executables read by the application index (Start menu, desktop, configured folders);
5. the targets reached through a symbolic link, a junction, a mount point or an alternate path (8.3, `\\?\`, UNC, another drive letter): the protection applies to the object reached, whatever the path;
6. the folders walked by the **folder index** (every fixed drive, the folders learned from use) and the folders listed by **path completion** (TD-21): only their listings are read, and they are protected exactly like the sources above.

**Only exception**: the Seekbeam data directory (`%LOCALAPPDATA%\Seekbeam`, or `<exe>\Data` in portable mode). It is automatically excluded from indexing and is therefore never a source. Seekbeam's own **program folder** is likewise always kept out of the folder index (TD-21), because it receives the extractor sandbox's read permission (audit W7); the locations are still checked as before (`TouchesScope`).

## 3. Operations allowed on sources

| Operation | API | Rights requested |
|---|---|---|
| Metadata read | `GetFileAttributesExW` | no handle |
| Folder enumeration | `CreateFileW` + `NtQueryDirectoryFile` | `FILE_LIST_DIRECTORY` + `SYNCHRONIZE`, R/W/D sharing |
| Identity (volume, FileId) | `CreateFileW` + `GetFileInformationByHandle` | `FILE_READ_ATTRIBUTES`, R/W/D sharing |
| Content read | `CreateFileW` | `GENERIC_READ`, R/W/D sharing, `OPEN_EXISTING`, `FILE_FLAG_SEQUENTIAL_SCAN`, `FILE_FLAG_OPEN_NO_RECALL` |
| Watching | `ReadDirectoryChangesW` (FileSystemWatcher) | `FILE_LIST_DIRECTORY` |
| Opening by the user | `ShellExecuteEx` (`open`, `runas`, `openas`, `properties`), `SHOpenFolderAndSelectItems` | delegated to the shell (§8) |
| Folder index, path completion | same folder enumeration as above; files are never opened there | `FILE_LIST_DIRECTORY` |
| Folder index watching | `ReadDirectoryChangesW` with `FILE_NOTIFY_CHANGE_DIR_NAME` only | `FILE_LIST_DIRECTORY` |
| Shares of a typed server (`\\server\`) | `NetShareEnum` level 1 | no handle (names only) |
| "Go to folder" in a dialog of another application (TD-20) | `WM_SETTEXT` / `WM_GETTEXT` / `WM_NEXTDLGCTL` to its file-name box, Enter **posted**; Explorer: `IShellWindows` → `Navigate2` | no file access by Seekbeam; the dialog only navigates and is **never validated** (§8) |

`OPEN_EXISTING` makes any creation impossible by construction. R/W/D sharing guarantees that Seekbeam never blocks another application.

## 4. Forbidden operations

Writing or truncating content, creating, deleting, renaming, moving, copying to a source, changing attributes, timestamps, ACLs, owner, ADS or EA, NTFS compression or encryption, creating temporary files or archive extractions in a source, "repairing" files, resolving shortcuts with an update (`IShellLink::Resolve`), deliberately hydrating cloud placeholders, exclusive locking.

**Features removed from the scope** (although common in Listary or DocFetcher): rename, move, delete, send to the Recycle Bin, change attributes or metadata, "repair" a link. Specification §19 also explicitly excludes them. Only non-destructive actions are offered.

## 5. Protection architecture

1. **`Seekbeam.Core.SourceAccess.SourceFileAccess`**: the only code allowed to open a source. Its public API contains only read operations. The returned stream (`ReadOnlySourceStream`) throws on `Write`, `SetLength` and `WriteByte`, and `CanWrite` is `false`.
2. **`Seekbeam.Core.Storage.SeekbeamStorage`**: the only code allowed to write to disk. Every path is canonicalized (`GetFullPath`, then the resolved final path of the DataRoot), must be under the DataRoot, and must not be under any root unless the DataRoot is excluded from it. Otherwise an `ImmutabilityViolationException` is thrown and logged. SQLite files and segments receive their paths exclusively from `SeekbeamStorage`. `SQLITE_TMPDIR` and `temp_store=MEMORY` prevent temporary files outside the DataRoot.
3. **Extraction**: in memory or in streams; never an intermediate file. Archives are read entry by entry in memory.
4. **Extraction host process** (TD-17): runs in an AppContainer sandbox and never receives a path. Seekbeam opens the source through `SourceFileAccess` and duplicates into the host a handle restricted to `FILE_GENERIC_READ` (`SourceFileAccess.DuplicateReadHandleInto`). The host checks the granted access of every handle it receives and refuses any write, delete, attribute or ACL right (`SourceFileAccess.FromReceivedHandle`). It returns only text and has no file write API (verified by IL analysis). Even compromised by a malicious document, it cannot write: the AppContainer token is denied every write by Windows, and the handle itself cannot write.
5. **Configuration validation**: a root containing the DataRoot gets an automatic exclusion; a root located *inside* the DataRoot is refused.

## 6. Allowed write locations

`<DataRoot>\config.json`, `<DataRoot>\db\`, `<DataRoot>\index\`, `<DataRoot>\logs\`, `<DataRoot>\tmp\`, `<DataRoot>\cache\`. Outside the file system, there are also the registry key `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Seekbeam` (automatic startup) and the clipboard.

One-time sandbox set-up (`Seekbeam.Core.Storage.SandboxProvisioning`, TD-17), the only writes outside the DataRoot:
- the AppContainer profile `Seekbeam.ExtractorHost`, created by Windows (`CreateAppContainerProfile`): folder `%LOCALAPPDATA%\Packages\seekbeam.extractorhost` and a mapping under `HKCU\Software\Classes\Local Settings`; deleted by `--purge-data`;
- one read+execute ACE for *ALL APPLICATION PACKAGES* (`S-1-15-2-1`) on Seekbeam's own program folder, so that the sandbox can load its own code.

Both are refused when the location is inside the indexing perimeter or contains an indexed root (`ScopeRules.TouchesScope`); the extractor then runs at low integrity instead. In portable mode the DataRoot (`Data\`) lies inside the program folder: before any grant, inheritance is blocked at the DataRoot and any AppContainer ACE removed, so that no AppContainer can ever read the index (test `PortableDataDirectoryIsNeverReadableByAppContainers`).

## 7. Audit and verification methodology

| Level | Tool | What it proves |
|---|---|---|
| Build | `Microsoft.CodeAnalysis.BannedApiAnalyzers` + `BannedSymbols.txt` (error RS0030) | no direct call to a write API outside the explicitly allowed files |
| IL analysis | `ImmutabilityStaticAnalysisTests`: decoding of every method body of every Seekbeam assembly (`System.Reflection.Metadata`), inventory of `call`/`callvirt`/`newobj`/`ldftn` targeting write APIs (System.IO, MemoryMappedFiles, Compression, Process), of every declared P/Invoke and of their entry points | every potential write is located in `Seekbeam.Core.Storage`; every P/Invoke is inventoried and classified |
| Dependency review | IL analysis of PdfPig and Microsoft.Data.Sqlite; review of native SQLite calls | the write paths of the dependencies are not reachable with our usage (read-only streams, paths from SeekbeamStorage) |
| Manual review | complete review of every source file (list in the AUDIT) | intent and context of every operation |
| Integrity tests | `Seekbeam.Immutability.Tests`: before/after fingerprint of every file and folder (SHA-256, size, attributes, creation, write, access, SDDL, ADS, tree) | no difference after each scenario |
| Dynamic | `Seekbeam.FsAudit`: independent process, `ReadDirectoryChangesW` with **all** filters during the real execution of Seekbeam.exe; ETW script (administrator) with per-process attribution | real behavior observed by the kernel |

### Integrity test scenarios
Initial scan; name indexing; extraction of every format; archives (normal, nested, bomb, malicious paths, encrypted); incremental indexing; watching (changes made **by the test** in a third-party area, excluded from the comparison); opportunistic discovery; reconciliation; pause and resume; normal shutdown; forced shutdown of the host and of the engine; recovery after a crash; malformed files; access denied (deny ACL); locked files (opened exclusively by the test); junctions and cycles; large files; read-only and hidden files; Unicode names and long paths; folder index (a whole tree walked as a drive, a learned folder, a full re-walk) and path completion over every folder; real Open / Save As / folder dialogs and an Explorer window driven by `tools/Seekbeam.DialogCheck` (every dialog closed by Cancel, test tree unchanged).

## 8. Limits of the guarantee (acknowledged and documented)

1. **Last access time**: on this machine, NTFS updates the last access time (`DisableLastAccess = 2`, system-managed, enabled). Reading a file or listing a folder may therefore change `LastAccessTime`, with a granularity of about 1 hour. This is an **OS effect**, not a Seekbeam write. The only documented way to avoid it (`SetFileTime(…, 0xFFFFFFFF)`) requires `FILE_WRITE_ATTRIBUTES` on the source, which the policy forbids. The tests isolate this field and report it without mistaking it for a modification.
2. **External applications**: Seekbeam asks Windows to open a file, or to start a program (Run commands, user aliases, possibly with arguments and elevated). The application that opens it may modify it, which Seekbeam cannot prevent. The built-in `hosts` alias opens the hosts file in Notepad as administrator on purpose: saving it is the user's action in Notepad. The Windows "Properties" dialog lets the user change attributes: that is a user action in the shell. The UI and the guide point this out.
3. **Shell components**: icon extraction (`SHGetFileInfo`, `IShellItemImageFactory`) and `shell:AppsFolder` enumeration cause files to be read by Windows components, whose contract is read-only but whose code is not under our control. For files, the icon is obtained by extension (`SHGFI_USEFILEATTRIBUTES`, no disk access), except for `.exe`, `.lnk` and `.ico`.
4. **Cloud files**: `FILE_FLAG_OPEN_NO_RECALL` and attribute filtering prevent hydration. A non-compliant third-party cloud provider could nevertheless react to enumeration.
5. **Antivirus and third-party filters**: they may react to our reads. This is outside the scope of Seekbeam.
6. **Dynamic audit**: `ReadDirectoryChangesW` observes effects but does not attribute them to a process. Attribution requires ETW or Procmon with administrator rights; the script is provided but was not run in the non-elevated development session.
7. **Dialogs of other applications** (TD-20): Seekbeam puts a folder path in their file-name box and posts Enter; the dialog only navigates. A Save As dialog keeps the name the user typed and is never validated by Seekbeam (the path ends with a separator and the folder is checked to exist just before, so the dialog cannot take it for a file name). Validating the dialog, and what the application then writes, is the user's action in that application, as with "Open" (§8.2).

## 9. Acceptance criteria

The 14 criteria of CLAUDE.md §29.13 apply. In practice: BannedApiAnalyzers is active on every project and the build has no violation; the IL analysis test is green; every integrity scenario is green (zero differences apart from the documented `LastAccessTime`); FsAudit detects no name, size, write, attribute or security event on the protected set; the report `docs/FILE_IMMUTABILITY_AUDIT.md` is published with its real results.
