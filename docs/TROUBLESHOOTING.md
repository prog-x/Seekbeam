**English** | [Français](TROUBLESHOOTING.fr.md)

# Troubleshooting

> UI labels are given in French as they appear in the app, followed by an English gloss.

| Symptom | Likely cause | Solution |
|---|---|---|
| Double Ctrl does nothing | presses too slow; another key pressed between the two presses; hook disabled | increase `DoubleTapMs`; check the notification area icon; set an alternative shortcut (`Alt+Space`). The hook is checked every 30 s and reinstalled if Windows removed it. |
| Double Ctrl doesn't work in an app running as administrator | UIPI isolation: a non-elevated process doesn't receive keystrokes from elevated windows | Windows behavior; use the shortcut from another window |
| "The shortcut … is already used by another application." | another application has reserved the combination | pick another one |
| A recent file doesn't show up | indexing in progress, folder excluded, hidden or system file, root not watched | dashboard → queues; check the exclusions; **Reconcile now** |
| A document's content isn't found | content not indexed yet, size above `MaxFileSizeMB`, encrypted file, image-only PDF (no OCR), cloud file not downloaded | dashboard → content and errors; actions menu → **Reindex** |
| Some files seem never to get indexed ("documents indexed" far below the file count, queue empty) | these files are not eligible for content indexing: unsupported format (images, executables…), folder indexed by name only (Downloads), empty or too large file, excluded type, cloud file… They remain findable by name | dashboard › Content index: the **Fichiers** (Files) line breaks down **every** file (indexed, without text, failed, pending, not eligible); expand **Non éligibles** (Not eligible) to see each reason and the most frequent extensions, then adjust Settings › Content if needed. In the launcher, a file's ○ dot also gives its reason on hover |
| Many files stay "pending" although the content queue is empty, or the queue shows files "postponed" | their location (network drive, share, external disk) was not reachable when their turn came | reconnect the drive: postponed files are tried again every 5 minutes. Files dropped by an earlier version are queued again by the content census, at most once an hour (dashboard › Content index › **Actualiser**/Refresh starts one) |
| OneDrive "online-only" files have no content | by design, Seekbeam never triggers their download (reading would modify the placeholder) | make them "Always keep on this device" |
| "Access denied" errors | folders protected by Windows permissions | expected: Seekbeam respects ACLs and never bypasses them |
| Indexing paused | Auto mode: full screen, battery, CPU or memory saturated | dashboard → resources; change the mode |
| Corrupted database (power loss) | rare (SQLite WAL) | after an abnormal shutdown, Seekbeam checks the database at startup and rebuilds the Name Index from the catalog. Otherwise, with Seekbeam closed, delete `db\` and `index\` in the data folder and restart (full reindex). |
| High memory | index in the page cache (reclaimable by Windows), extraction of large documents | private RAM stays bounded; the extraction host is capped by a Job Object |
| The content of files in Downloads isn't found | by design: Downloads is indexed by name only ([SECURITY.md](../SECURITY.md)) | Settings › Content › remove Downloads from "Folders indexed by name only" (read the warning first) |
| Dashboard shows "Extractor isolation: low integrity" | the AppContainer sandbox could not be set up: Seekbeam's program folder is inside an indexed location (typical for a portable copy unpacked in Documents), the folder's permissions cannot be changed, or `%LOCALAPPDATA%\Packages` is indexed. The log (`Sandbox`, `Extractor` lines) gives the reason | move the portable folder outside the indexed locations, or exclude it; or grant read access once yourself: `icacls "<Seekbeam folder>" /grant *S-1-15-2-1:(OI)(CI)(RX)`, then restart Seekbeam. Extraction still works at low integrity, with weaker isolation |
| Dashboard shows "Extractor isolation: none" | both sandboxes failed (unusual: security software, policy) | see the log; `Content.Sandbox` can force a level for testing |

## Logs and diagnostics
- **Location**: `%LOCALAPPDATA%\Seekbeam\logs\seekbeam-YYYYMMDD.log`. Open them from the dashboard → **Open logs**.
- **Level**: adjustable (`Logging.Level = Debug`).
- **Content**: logs never contain searches or document content. Paths only appear in warnings and errors, and the `RedactPaths` option masks them.
- **Persistent errors** (last 2,000): dashboard → **Recent errors**.

## Resetting
- **Indexes only**: dashboard → **Reindex names** / **Reindex content**.
- **Everything**: `Seekbeam.exe --purge-data` deletes indexes, history, logs and configuration. Your files are not affected.
