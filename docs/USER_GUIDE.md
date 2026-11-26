**English** | [Français](USER_GUIDE.fr.md)

# Seekbeam — User guide

> The user interface is currently in French. In this guide, UI labels are given in French as they appear in the app, followed by an English gloss.

## Getting started
After installation, Seekbeam starts in the background and an icon appears in the notification area.

By default it indexes your user folder (`C:\Users\<you>`), except `AppData`, `.git`, `node_modules` and a few caches. Search works **immediately**, and results get richer as indexing progresses. Document contents are indexed afterwards, more slowly and without getting in your way.

**Press Ctrl twice quickly** to open the search.

## Searching
- Type a few letters from any part of the name, in any order: `fact 2024 client`.
- Capitals and accents don't matter: `ete` finds `Été`, and `previsionnel` finds `prévisionnel`.
- Parent folder names count too: `projets alpha notes` finds `…\Projets\Alpha\notes.txt`.
- Typos are tolerated (`raport`, `rapprot`) when no exact result exists. Approximate results are marked with `≈`. Numbers are never matched approximately.
- A single letter finds names that start with that letter, or that contain a word starting with it.
- **Document contents too**: the launcher also lists documents whose indexed content contains every word (or words starting that way), with the passage where they appear. They fill the free rows of the list; when the name results already fill it, they still take its last two rows (with the default 8 rows), and they come before approximate name matches (≈). A PDF whose name contains none of the typed words is therefore found if its text contains them. These results arrive a fraction of a second after the name results, which are never delayed. Not for queries of fewer than 3 characters or restricted with `kind:` / `in:`; `ext:` is honoured. For "some of the words" and the other filters, use the advanced search (`Ctrl+D`).

**Operators:**
- `"exact phrase"`: the words must appear together;
- `-excludedword`: leaves out names containing this word;
- `ext:pdf,docx`: filters by extension;
- `kind:file`, `kind:folder`, `kind:app`: filters by type;
- `in:folder`: limits the search to a folder;
- `src\seek` (or `src/seek`): path fragments in hierarchy order: `seek` must be in a folder (or name) **below** a folder matching `src`;
- `C:\Win`, `\\server\`, `%APPDATA%\Mic`: an **absolute path** is completed live from the disk, like the Explorer address bar (folders first). Typing `\\server\` lists the server's shares.

**Path fragments.** Words typed in the order of the path rank higher: `sou per see` puts `C:\source\perso\Seekbeam` first, and the last word names the target (`seek src` gives the `src` folder, not what is inside it).

**Ranking** favors, in order:
- exact matches;
- the start of the name and the start of words;
- applications;
- your most-used items.

History can never outrank a clearly better match.

**Indexed yet?** A file shown in the results always has its name indexed. A dot on the right of each file shows the state of its **content** (hover it for details):

| Dot | Meaning |
|---|---|
| ● (green) | content indexed: its text can be found by the advanced search (`Ctrl+D`) |
| ◌ (blue) | pending: not extracted yet, or changed since it was last extracted |
| ○ (grey) | not indexed, with the reason: unsupported format, empty or too large file, OneDrive "online-only" file, folder indexed by name only, excluded type…; or a document with no text (scanned PDF) |
| ⚠ (orange) | extraction failed (details in the dashboard) |

Folders and applications have no dot. While names are still being indexed, a banner at the bottom of the launcher warns that some files may not be found yet.

## Actions
`Enter` opens the item or launches the application. `Tab` or a right-click shows every action:
- open the folder;
- copy the path or the name;
- copy the indexed content;
- properties;
- run as administrator;
- open with…;
- favorite;
- exclude from the index;
- reindex.

> Seekbeam never renames, moves, deletes or modifies a file. When you open a file, Windows hands it to the associated application (Word, an editor…), which may modify it as usual: that is no longer under Seekbeam's control.

## Commands and aliases
**Run commands.** Everything the Windows Run dialog (`Win+R`) understands also works in the launcher: programs in the PATH folders (`mstsc`, `regedit`, `cmd`, `winver`…), applications registered in Windows (App Paths: `excel`, `chrome`…), consoles (`services.msc`, `devmgmt.msc`; typing `services` is enough) and Control Panel applets (`appwiz.cpl`, `ncpa.cpl`).
- A command appears when you type its exact name, or the first 3 letters or more: `ms` lists nothing, `mst` already shows `mstsc`. There is no typo tolerance for commands, so that the hundreds of terse system names never flood the results.
- **Arguments**: type them after the command, as in `Win+R`: `mstsc /v:server01`, `ping 1.1.1.1`. The first row then launches the command with these arguments.
- The list follows changes to the PATH and to installed programs within 15 minutes. It can be turned off in **Settings → Applications**.

**Aliases** are your own keywords (**Settings → Applications → Aliases**). An alias starts a program, a file, a folder or an address (`ms-settings:display`, `https://…`), with fixed arguments, a working folder and, optionally, administrator rights. Environment variables (`%SystemRoot%`, `%USERPROFILE%`…) are allowed. Text typed after the keyword is added to the arguments. An alias whose keyword is typed exactly comes first, even before a command of the same name.

Seekbeam ships with one alias: **`hosts`** opens `%SystemRoot%\System32\drivers\etc\hosts` in Notepad **as administrator**, because saving that file requires it; Windows asks for confirmation (UAC). You can edit or delete it like any other alias.

> As with "Open", Seekbeam only asks Windows to start the program. What that program then does (Notepad saving the hosts file, for instance) is your action in that program, not Seekbeam's.

## Dialogs and folders (go to folder)
In an **Open**, **Save As** or **folder** dialog of any application, Seekbeam takes you to a folder in a few keystrokes, like Listary.

- **Ctrl twice** over the dialog: the launcher opens **glued under the dialog**, in "go to folder" mode. Type bits of the path (`cli dup fact`), or an absolute path (`D:\Pro`), then `Enter`: the dialog shows that folder.
- **Just type**: when the dialog's **file list** has the focus, typing a letter or a digit opens Seekbeam with it. `Esc` closes Seekbeam and gives the letters back to the list (its own "jump to the name starting with…"). Text boxes (file name, address, search) are never affected.
- **Nothing typed**: the folders open in Explorer (Quick Switch), then your favorite and recent folders.
- **Ctrl+G** in a dialog: it jumps straight to the folder of the front-most Explorer window.
- **Explorer**: Ctrl twice over an Explorer window drives that window (its active tab); typing in Explorer keeps its usual behaviour.
- Folders come first, then files. Choosing a **file** goes to its folder; in an Open dialog its name is put in the "File name" box, ready for you to confirm.
- In a **Save As** dialog the file name you typed is kept.

**Seekbeam never validates a dialog**: it only shows the folder. You confirm (Open, Save, Select folder) yourself, so Seekbeam can neither open nor overwrite a file. Two cases cannot be driven and get the path copied to the clipboard instead (paste it with `Ctrl+V`): the dialogs of applications running as administrator (Windows protects them from normal applications), and the old "Browse for folder" tree.

**Folders outside your locations.** For this to work wherever you save, Seekbeam also indexes the **names of the folders** of every fixed local drive (never their files, never their contents), plus the folders you use outside the index (network shares, USB drives) with a few levels below. `C:\Windows`, recycle bins, caches, `node_modules`, `.git`, `bin`/`obj` are left out; Program Files, ProgramData and AppData are included but ranked lower. In the normal launcher these folders only fill the free rows, after your locations (or with `kind:folder`). Each option can be switched off in **Settings → Dialogs & folders**.

## Advanced search (full text)
Press `Ctrl+D` in the launcher, or use the **Advanced search** entry in the Start menu or the tray icon menu.
- **Formats searched**: PDF, Word, Excel, PowerPoint, OpenDocument, RTF, HTML, text, code and ZIP archives.
- **Syntax**:
  - words (all required);
  - `"exact phrase"`;
  - `facture OR devis`: either word;
  - `-brouillon`: excludes a word;
  - `préfixe*`: matches words starting with this prefix.
- **When nothing matches every word exactly**, the search is relaxed instead of returning nothing: first every word as a word beginning (`code` also finds `codes`), then documents containing only some of the words, ranked by relevance. The status line says which step produced the results ("words matched as word beginnings", "partial results").
- **Filters**: scope (names and/or content), types, location, modification date, size.
- **Preview**: the right-hand pane previews the indexed text with the search terms highlighted. The preview comes from the index, so the file is not reopened.

## Dashboard
Tray icon menu → **Dashboard**.

It shows:
- number of items;
- queues;
- speed;
- index size;
- resources;
- status of each location;
- the isolation of the extractor ("AppContainer" when the sandbox is fully active);
- errors (inaccessible files, failed extractions).

Buttons: pause or resume, reconcile, reindex names, reindex content, maintenance.

**Why doesn't every file have its content indexed?** The **Content index** panel breaks down every file of the name index; the counters add up to the number of files:

```
Files: 21,319
14,482 indexed · 312 without text · 84 failed · 0 pending · 6,441 not eligible
▾ Not eligible: 6,441
    Still findable by name.
    format not supported (name only): 5,230 (jpg 2,100, png 1,400, exe 600, …)
    folder excluded from content indexing: 640
    empty file: 410 · file too large: 98 · …
```

Files that are not eligible are not lagging behind: the rules (format, size, folders indexed by name only, excluded types, cloud files…) set them aside on purpose, and they remain findable by name. The breakdown is computed in the background at low priority: when the dashboard opens if the last one is more than 10 minutes old, at the end of each indexing run, or with **Refresh**. Its time is shown below it.

## Settings
Tray icon menu → **Settings**, or `Ctrl+,` in the launcher. See [CONFIGURATION.md](CONFIGURATION.md).
- **General**: language (English, French, German, Italian, or the Windows language), startup, double Ctrl (left, right or both, delay), alternative shortcut and advanced-search shortcut, position, number of results, theme.
  - **Choosing a shortcut**: click the field, then press the combination you want (e.g. `Ctrl+Alt+F`). Seekbeam tells you at once whether it is available, already taken by Windows or another application, or unusable (a character key needs at least `Ctrl`, `Alt` or `Win`, so that typing elsewhere is not blocked). `Backspace` or ✕ clears the shortcut. Seekbeam's own shortcuts are suspended while you choose.
- **About**: version, link to the project, licence (MIT, provided "as is", without warranty), and a button that copies the version information for a bug report. Also available from the tray icon menu.
- **Locations**: folders and drives to index, exclusions, depth, content, hidden files, links.
  - **Maximum depth** (default 10): how many folder levels below the location are explored. When folders still contain items beyond this limit, Seekbeam tells you: a one-time notification after indexing, a warning in the dashboard listing the affected folders, and a hint in the launcher when a search finds nothing. Raise the limit (or set `-1` for unlimited), or add the deep folder as a separate location.
- **Content**: extraction limits, archives, and **folders indexed by name only**. Files in these folders are found by name, but their content is never read. The Downloads folder is in this list by default: files from the Internet and e-mail land there, and indexing their content would mean parsing them automatically, even ones you never opened. If you remove it, Seekbeam first explains the risk and asks for confirmation.
- **Applications**: application sources (Start menu, desktop, Store apps, extra executable folders), Run commands, and the alias editor (see [Commands and aliases](#commands-and-aliases)).
- **Dialogs & folders**: one switch per feature (dialogs, direct typing, Quick Switch, Explorer, recent folders, path completion) and the folder index outside the locations (drives, learned folders, depth, exclusions, folders ranked lower, "forget the learned folders"). See [Dialogs and folders](#dialogs-and-folders-go-to-folder).
- **Resources**, **Search & privacy**, **Watching**.

## Resources
**Automatic** mode is the default:
- it slows indexing down while you work, when the system is busy or when you are on battery;
- it pauses indexing during games, full-screen videos and presentations;
- it speeds indexing up when the computer is idle.

A search always takes priority.

## Privacy
Everything stays on your computer: no telemetry, nothing is sent anywhere. History and learning can be disabled, cleared or reset in the settings. Logs never contain your searches or the content of your documents.

## Uninstalling
Windows Settings → Apps → Seekbeam → Uninstall.

Your indexes are kept in `%LOCALAPPDATA%\Seekbeam`. To remove everything, run `Seekbeam.exe --purge-data` before uninstalling, or delete that folder.
