**English** | [Français](CONFIGURATION.fr.md)

# Configuration

**File**: `%LOCALAPPDATA%\Seekbeam\config.json` (or `Data\config.json` in portable mode).

**Editing**: use the Settings window. You can also edit the file by hand while Seekbeam is closed.

**Loading**:
- values are validated and clamped when loaded;
- an invalid file is saved as `config.invalid.json` and replaced with the defaults;
- writes are atomic (temporary file, then replace).

| Section | Key | Default | Description |
|---|---|---|---|
| General | Language | "" | UI language: `en`, `fr`, `de`, `it`; empty = Windows display language (English if not supported) |
| | StartWithWindows | true | automatic startup (HKCU Run; ignored in portable mode and with `--data`) |
| | ShowTrayIcon | true | notification area icon |
| Hotkey | DoubleCtrlEnabled | true | double Ctrl |
| | CtrlSide | Both | Both / Left / Right |
| | DoubleTapMs | 400 | maximum delay between the two presses (150–1000) |
| | AlternativeHotkey | "" | e.g. `Alt+Space`, `Ctrl+Shift+F`, `Win+K`; captured by pressing it in the Settings |
| | DeepSearchHotkey | "" | advanced search shortcut (same syntax) |
| Launcher | Position | Center | Center / NearCursor |
| | MaxResults | 8 | 1–50 |
| | Width | 720 | width (DIP) |
| | Topmost, CloseOnFocusLoss | true | window behavior |
| | Theme | System | System / Light / Dark |
| Search | FuzzyEnabled | true | typo tolerance |
| | FuzzyMinTokenLength | 4 | minimum word length for typo tolerance |
| | MatchInPath | true | words may match parent folders |
| | IncludeHidden / IncludeSystem | false | show hidden / system files |
| | MaxSearchThreads | 0 | 0 = auto (cores/2, max 8) |
| | Weights.* | see ARCHITECTURE §6 | ranking weights |
| Roots[] | Path | user profile | indexed root (folder or volume, e.g. `D:\`) |
| | Enabled, IndexContent, IncludeHidden, Watch | true | |
| | FollowLinks | false | follow symlinks and junctions (protected against cycles) |
| | MaxDepth | 10 | levels explored below the root (its direct children are level 1; -1 = unlimited). Folders whose content is cut off by this limit are reported (dashboard, notification, settings) |
| | Excludes | AppData, .git, node_modules… | names, patterns (`*.tmp`, `~$*`) or relative paths (`build\out`, `**\cache`) |
| GlobalExcludes | | `$Recycle.Bin`, `System Volume Information`, `*.tmp`, `~$*`, `Thumbs.db` | exclusions for all roots |
| Content | Enabled | true | content indexing |
| | MaxFileSizeMB | 50 | larger files are skipped |
| | MaxCharsPerDocument | 1,000,000 | text kept per document |
| | StoreTextForSnippets | true | compressed text for snippets and preview |
| | TimeoutSeconds | 30 | extraction timeout per file |
| | DisabledExtractors | [] | `text`, `markup`, `ooxml`, `odf`, `rtf`, `pdf`, `legacy-office`, `zip` |
| | ExtraTextExtensions / ExcludedExtensions | [] | |
| | ExcludedFolders | [Downloads] | folders (and subfolders) indexed **by name only**: their content is never read. Contains the Downloads folder by default, for security ([SECURITY.md](../SECURITY.md)); Settings › Content asks for confirmation before removing it |
| | Sandbox | Auto | extractor isolation: `Auto` (AppContainer, otherwise low integrity), `AppContainer`, `LowIntegrity`, `Off`. Change only for troubleshooting; applies when the extractor host restarts |
| Content.Archives | Enabled, MaxDepth, MaxEntries, MaxEntrySizeMB, MaxTotalSizeMB, MaxCompressionRatio | true, 2, 10000, 50, 200, 100 | anti-bomb limits |
| Content.Ocr | Enabled | false | reserved (not included in 1.0) |
| Resources | Mode | Auto | Auto / Economy / Balanced / Performance |
| | PauseInFullscreen | true | pause during games, full-screen video, presentations |
| | EconomyOnBattery / PauseContentOnBattery | true / false | |
| | UserIdleSeconds | 120 | idle time before speeding up (Auto) |
| | ContentWorkers / ExtractorMemoryMB | 0 | 0 = depends on the mode |
| | ContentTimeWindows | [] | e.g. `["22:00-07:00"]` |
| Apps | StartMenu, Desktop, PackagedApps | true | application sources |
| | ExecutableFolders | [] | extra executable folders |
| | RunCommands | true | Win+R commands: PATH folders, App Paths, .msc, .cpl |
| | Aliases | `hosts` | user keywords (see below) |
| Privacy | QueryHistory, Learning | true | local history and learning |
| Watch | Enabled, ReconcileAtStartup | true | watching and reconciliation at startup |
| | ReconcileIntervalHours | 24 | periodic reconciliation |
| Discovery | Enabled, WatchRecentItems | true | opportunistic discovery (Recent Items) |
| | MaxDirectoriesPerHour | 2000 | budget |
| Logging | Level, RedactPaths, MaxFiles | Info, false, 5 | rotating local logs (5 × 5 MB) |

### Aliases
Each alias has a `Keyword` (no spaces, unique), a `Target` (program, file, folder or URI), and optional `Arguments`, `WorkingDirectory` and `RunAsAdmin`. Environment variables are expanded at launch. Invalid entries (empty keyword or target, keyword with a space, duplicate keyword) are removed when the configuration is loaded, and a warning is logged.

```json
"Aliases": [
  { "Keyword": "hosts", "Target": "%SystemRoot%\\System32\\notepad.exe",
    "Arguments": "%SystemRoot%\\System32\\drivers\\etc\\hosts", "WorkingDirectory": "", "RunAsAdmin": true },
  { "Keyword": "rdp-lab", "Target": "mstsc.exe", "Arguments": "/v:lab01", "WorkingDirectory": "", "RunAsAdmin": false },
  { "Keyword": "display", "Target": "ms-settings:display", "Arguments": "", "WorkingDirectory": "", "RunAsAdmin": false }
]
```

Schema 3 adds the `hosts` alias once to existing configurations; if you delete it, it does not come back.

## Resource profiles
| Mode | Names | Content | Host CPU (kernel cap) | Host RAM |
|---|---|---|---|---|
| Economy | 2,000 entries/s | 4 MB/s, 1 worker | 10% | 256 MB |
| Balanced | 20,000 entries/s | 20 MB/s, 1 worker | 25% | 512 MB |
| Performance | unlimited | unlimited, up to 4 workers | 60% | 1 GB |
| Automatic | chosen based on CPU, RAM, idle time, battery and full screen | | | |
