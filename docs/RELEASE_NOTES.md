**English** | [Français](RELEASE_NOTES.fr.md)

# Release notes

## Unreleased

- **New: Run commands and aliases.** The launcher understands what `Win+R` understands: programs in the PATH (`mstsc`, `regedit`), App Paths registrations (`excel`), consoles (`services.msc`, or just `services`) and Control Panel applets (`appwiz.cpl`), optionally followed by arguments (`mstsc /v:server`). To keep results clean, commands appear only for their exact name or a prefix of at least 3 letters. **Aliases** (Settings → new **Applications** tab) map your own keywords to a program, file, folder or address, with arguments and optional administrator rights. A `hosts` alias is included: it opens the hosts file in Notepad as administrator. Configuration schema 3 (`Apps.RunCommands`, `Apps.Aliases`).

- **Fix: the quick search window stayed open when clicking elsewhere.** The "Close when the window loses focus" setting (on by default) only worked after a context menu had been opened on a result. Clicking outside the launcher now closes it, as `Esc` does; a context menu dismissed by a click outside Seekbeam closes it too.

- **Fix: files of a disconnected network drive were never indexed by content.** When content indexing reached a file whose drive or share was not connected, the file was dropped from the queue: it stayed "pending" with an empty queue, and only a change to the file, or a full content reindex, brought it back. Its files now stay in the queue and are tried again every 5 minutes until the location is back; they are no longer counted as failures, and the dashboard shows them as "postponed". Files already lost this way are queued again automatically by the content census (at most once an hour). A location reported "Unavailable" is shown as OK again once it can be read.

- **Fix: content results in the launcher could stay out of sight.** They were appended below the name results, so when names filled the list they ended up under the last row, or past the list's visible height. They now fill the free rows of the list, keep its last two rows when names already fill it (default list of 8), and come before approximate name matches (≈). The list never grows past its configured size. Errors in the launcher's result display are now logged instead of being silently lost.

## 1.0.0 — 2026-11-22

First public release. Its features are described further down; this first part lists what changed since the internal build of 2026-11-21.

### Changes since the internal build of 2026-11-21
- **Indexing state visible in the launcher.** Each file shown carries a dot giving the state of its content: ● indexed, ◌ pending, ○ not indexed (the reason appears on hover), ⚠ failed. It is loaded after the results are displayed, so search is not slowed down. While names are being indexed, a banner warns that some files may not be found yet.
- **The launcher also searches document contents.** Up to 4 documents whose text contains the typed words are added below the name results, with the matching passage. They are computed after the names are displayed and dropped if typing continues. Previously the launcher searched names and paths only: a PDF whose text alone contained the typed words gave 0 results.
- **The advanced search no longer returns 0 results when a word is missing or differs.** When no document contains every word exactly, it tries every word as a word beginning (`code` → `codes`), then documents containing some of the words, and the status line says so. Exclusions (`-word`) and quoted phrases stay strict.
- **The dashboard explains why some files have no indexed content.** The Content index panel now breaks down every file (indexed, without text, failed, pending, not eligible), and the counters add up to the number of files in the name index. The details of the files that are not eligible give each reason and, for unsupported formats, the most frequent extensions. The census is read-only, uses constant memory, runs in the background and yields to searches (about 1.7 s and a few MB for 1.1 million files, see [BENCHMARKS.md](BENCHMARKS.md)). "Skipped (unchanged, not eligible)" becomes "Set aside by the extractor (unchanged or no longer eligible)", and an idle extractor is "on standby" instead of "stopped".
- **Shortcuts captured by pressing them.** In Settings › General you press the combination instead of typing it. Seekbeam checks at once that it is available (not already taken by Windows or another application, not a duplicate) and refuses combinations that would block typing (`Shift` alone + a letter). Newly accepted keys: arrows, numeric keypad, `Pause`.
- **Version and "About".** New Settings › About tab (version, link to the project, MIT licence without warranty, third-party components, copy version information); the version also appears in the tray icon tooltip and menu.
- **Fix: premature "idle" state.** Right after start-up or a settings change, name indexing could report "idle, empty queue" before it had even processed the pending request. The launcher banner, the dashboard and the content-coverage refresh relied on it. A pending request now counts as work in progress.
- **Fix: excluding a folder from content indexing.** Excluding again a folder whose content had been indexed sometimes left its stored text in place, because the new list was applied after the files had been re-checked. The re-check now waits until the new rules are in place.
- **Extraction runs in a sandbox** (TD-17). `Seekbeam.ExtractorHost.exe` now starts in an AppContainer with no capabilities: no access to your files, no network, no writes, no registry, no child process. Seekbeam opens each document read-only and hands it a handle that can only read that file. It falls back to a low-integrity process when the AppContainer is unavailable; the dashboard shows the level in use. One-time set-up at start-up: an AppContainer profile and one read permission on Seekbeam's program folder, never inside an indexed location; `--purge-data` removes the profile.
- **Downloads is indexed by name only** (TD-18). Its content is no longer read by default, because files from the Internet and e-mail would otherwise be parsed automatically. New setting `Content.ExcludedFolders` (Settings › Content › "Folders indexed by name only"); removing Downloads shows the risk and asks for confirmation. Existing configurations are migrated (config schema 2) and text already stored for Downloads is deleted.
- **Dependencies**: Dependabot also updates the .NET SDK (the runtime bundled in releases) and groups document parsers separately. NuGet audit runs on direct and transitive packages, and CI fails on a known vulnerability ([SECURITY.md](../SECURITY.md)).
- **Renamed from Findex to Seekbeam** before the first public release: another Windows local file search app already uses the name Findex. Executables, namespaces, installer (new product code), data folder (`%LOCALAPPDATA%\Seekbeam`) and documentation use the new name.
- **Default maximum depth is now 10** for new locations (it was unlimited). Existing locations keep their saved value. Set `-1` for unlimited.
- **Folders cut off by the depth limit are reported**:
  - in the dashboard, a column and a warning panel list the affected folders (double-click to open one, button to change the limit);
  - a one-time tray notification appears after indexing (click opens the dashboard);
  - a warning is shown in Settings › Locations;
  - the launcher mentions it when a search finds nothing.
- Catalog schema v2 (new `depth_limited` table, upgraded automatically).
- **User interface in English, French, German and Italian.** By default Seekbeam follows the Windows display language (English otherwise); Settings › General › Language overrides it, and the launcher switches immediately. Numbers and dates keep the Windows regional format. Translators: see [TRANSLATING.md](TRANSLATING.md).
- **License**: MIT (`LICENSE`), with `THIRD-PARTY-NOTICES.md` for the redistributed components; both are included in the MSI and the portable zip.
- **Installer**: shortcut names and descriptions are now in English ("Seekbeam — Advanced search").
- **Fix: slow shutdown.** Content indexing workers shared one wake-up event, so on exit all but one slept up to 15 s and Seekbeam took about 11 s to quit. Waits now also end on cancellation: exit takes about 0.2 s.

### Features
- **Launcher**:
  - double Ctrl (left, right or both, adjustable delay) and custom shortcuts (RegisterHotKey);
  - window centered or near the cursor;
  - full keyboard navigation and non-destructive contextual actions;
  - icons and match highlighting.
- **Name Index**:
  - multiple tokens in any order, substrings, parent folders;
  - case- and accent-insensitive (including NFD);
  - filters: `ext:`, `kind:`, `in:`, `-excluded`, `"phrase"`;
  - typo tolerance (bitap: substitution, insertion, deletion, transposition);
  - documented ranking, with capped local learning.
- **Applications**: Start menu, desktop, UWP/MSIX (shell:AppsFolder) and executable folders; read-only `.lnk` parser.
- **Indexing**:
  - NtQueryDirectoryFile with a per-directory diff;
  - persistent, prioritized queues and short transactions;
  - search available from the first files;
  - exclusions, depth, links and cycles, permissions respected.
- **Watching**: ReadDirectoryChangesW, coalescing, in-place renames, burst protection, reconciliation at startup and periodically.
- **Content Index**:
  - FTS5 (BM25, phrases, OR, NOT, prefixes), snippets, preview, filters;
  - extractors for text, HTML/XML, OOXML, ODF, RTF, PDF, DOC/XLS/PPT and ZIP (in memory, anti-bomb);
  - isolated host process under a Job Object.
- **Opportunistic discovery**: Windows Recent Items, and files opened from Seekbeam.
- **Resources**:
  - Economy, Balanced, Performance and Automatic modes;
  - pause in full screen and on battery;
  - time windows;
  - absolute priority for search;
  - threads in Windows background mode.
- **Interface**: dashboard, full settings, light or dark theme, tray icon.
- **Installation**: per-user MSI without elevation, portable edition, `--exit`, `--data`, `--purge-data`.

### Immutability guarantee
- **Architecture**: the SourceFileAccess / SeekbeamStorage separation.
- **Static checks**: BannedApiAnalyzers and IL analysis.
- **Tests**: SHA-256 and metadata integrity tests, including a forced kill and restart of the real executable.
- **Dynamic audit**: FsAudit, plus an ETW script.

See `docs/FILE_IMMUTABILITY_AUDIT.md`.

### Deliberately missing features
Renaming, moving, deleting, and changing attributes or metadata (incompatible with the no-modification policy).

### Known limitations
- **User interface**: French only (localization planned).
- **OCR**: not included (disabled, reserved).
- **Archives**: ZIP only (no 7z or rar). Encrypted entries are skipped.
- **Old formats**: Excel 95 (BIFF5) and Word before 97 are not supported. Image-only PDFs have no text.
- **Initial scan**: no MFT/USN reading, since it would require administrator rights. The initial scan uses directory enumeration.
- **Open detection**: it relies on "Recent Items", so applications that don't register there don't trigger discovery.
- **Typing speed at scale**: at 5M entries, typing one- or two-letter prefixes reaches 139 ms at p95 (incremental refinement still to be implemented).
- **Moved folders**: the stored depth of a moved folder's subtree is only updated at the next rescan.
- **Signing**: binaries are unsigned.

### Next steps
- **UI localization**: French, English, German, Italian, with translation files that external contributors can edit easily.
- **GitHub publication**: preparing the repository.

See [ROADMAP.md](../ROADMAP.md).
