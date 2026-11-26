**English** | [Français](DIALOG_NAVIGATION_CHECK.fr.md)

# Seekbeam — "Go to folder": check on real dialogs

How Seekbeam drives Open, Save As and folder dialogs and Explorer windows is described in [TECHNICAL_DECISIONS.md](../TECHNICAL_DECISIONS.md) (TD-20) and in the [user guide](USER_GUIDE.md#dialogs-and-folders-go-to-folder). This page holds the results of `tools/Seekbeam.DialogCheck`, which checks it on **real Windows dialogs** with the very code the launcher ships.

## What the tool does

1. Creates a small test tree in `%TEMP%` and fingerprints it (names, sizes, SHA-256).
2. For each kind of dialog (Open, Save As, folder picker, old folder browser), opens a real dialog, checks that Seekbeam recognises it, drives it (folder chosen, file chosen, folder that no longer exists, focus in the file list as with direct typing), feeds the keyboard router (letter in the file list, letter in the file-name box, Ctrl+G), reads back the dialog's address bar and file-name box, then **closes it with Cancel** and checks the dialog's result: it must be Cancel, never OK.
3. Opens an Explorer window, lists the open folders (Quick Switch), navigates that window and selects a file.
4. With `--app <Seekbeam.exe>`: starts the real application with an isolated profile, presses Ctrl twice over the dialog, types path fragments, then an absolute path, then uses a Save As dialog, each time with Enter.
5. Fingerprints the test tree again: nothing may have changed.

Run it from an interactive session (dialogs appear on screen for a few seconds):

```
dotnet build Seekbeam.sln
tools\Seekbeam.DialogCheck\bin\Debug\net10.0-windows\Seekbeam.DialogCheck.exe report.md --app src\Seekbeam.App\bin\Debug\net10.0-windows\Seekbeam.exe
```

The exit code is 0 only if every check passes.

## Results

Run: 2026-11-26 17:10 · Microsoft Windows NT 10.0.26300.0 · UI culture fr-FR · 17,1 s

**36/36 checks passed.**

| Scenario | Check | Result | Detail |
|---|---|---|---|
| Open dialog | recognised as FileOpen | ✅ pass | FileOpen |
| Open dialog | typing in the file-name box is never taken over | ✅ pass | focus: Edit |
| Open dialog | keyboard router: a letter in the file-name box is left to the dialog | ✅ pass | taken=False |
| Open dialog | keyboard router: a letter in the file list opens Seekbeam, the next one is captured | ✅ pass | first='a', captured='b' |
| Open dialog | keyboard router: Ctrl+G asks for Quick Switch on this dialog | ✅ pass | taken=True |
| Open dialog | file list focused → recognised for direct typing | ✅ pass | focus: DirectUIHWND |
| Open dialog | navigate to a folder | ✅ pass | Done; address: Adresse : %TEMP%\…\Beta\Rapports |
| Open dialog | a file was chosen: its folder, and its name in the box (not validated) | ✅ pass | Done; box: 'notes.md' |
| Open dialog | a folder that no longer exists is refused before touching the dialog | ✅ pass | FolderMissing |
| Open dialog | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| Save As dialog | recognised as FileSave | ✅ pass | FileSave |
| Save As dialog | navigate (focus in the file list), the typed file name is kept | ✅ pass | Done; address: Adresse : %TEMP%\…\Beta\Rapports; box: 'keepme.txt' |
| Save As dialog | a file was chosen: the typed name still wins (no overwrite risk) | ✅ pass | Done; box: 'keepme.txt' |
| Save As dialog | dialog still open (never validated) | ✅ pass |  |
| Save As dialog | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| Folder picker | recognised as FolderPicker | ✅ pass | FolderPicker |
| Folder picker | navigate to a folder (focus in the file list) | ✅ pass | Done; address: Adresse : %TEMP%\…\Beta\Rapports |
| Folder picker | a file was chosen: its folder (no name in the box) | ✅ pass | Done; address: Adresse : %TEMP%\…\Alpha Projets |
| Folder picker | dialog still open (never validated) | ✅ pass |  |
| Folder picker | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| Old folder browser (SHBrowseForFolder) | recognised as LegacyFolderBrowser | ✅ pass | LegacyFolderBrowser |
| Old folder browser (SHBrowseForFolder) | not driven (the launcher copies the path instead) | ✅ pass | Unsupported |
| Old folder browser (SHBrowseForFolder) | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| Explorer window | open Explorer folders are listed (Quick Switch) | ✅ pass | %TEMP%\…\Alpha Projets |
| Explorer window | recognised | ✅ pass | Explorer |
| Explorer window | not taken over for direct typing | ✅ pass |  |
| Explorer window | navigate the same window (and select the file) | ✅ pass | navigated=True; now in %TEMP%\…\Beta\Rapports |
| Explorer window | Quick Switch target = front-most Explorer folder | ✅ pass | %TEMP%\…\Beta\Rapports |
| End to end (Seekbeam.exe) | double Ctrl over the dialog opens the launcher | ✅ pass |  |
| End to end (Seekbeam.exe) | launcher glued under the dialog | ✅ pass |  |
| End to end (Seekbeam.exe) | path fragments + Enter: the dialog goes to the folder | ✅ pass | Adresse : %TEMP%\…\Beta\Rapports |
| End to end (Seekbeam.exe) | absolute path completed from the disk + Enter | ✅ pass | Adresse : %TEMP%\…\Alpha Projets |
| End to end (Seekbeam.exe) | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| End to end (Seekbeam.exe) | Save As: goes to the folder, keeps the file name, not validated | ✅ pass | Adresse : %TEMP%\…\Beta\Rapports; box 'keepme.txt' |
| End to end (Seekbeam.exe) | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| Immutability | test tree unchanged (names, sizes, SHA-256) | ✅ pass | 5 entries before, 5 after |

## Findings while building the feature

- In the lab, a click on OK with a file-name box that had not been recognised **saved** the Save As dialog (the test dialog writes nothing): the design never clicks OK and only ever posts Enter with an existing folder path ending with `\`.
- In a folder picker, OK selects the folder and closes the dialog: Enter navigates instead.
- With the focus in the file list (the direct-typing case), Enter posted to the file-name box was ignored by the Open dialog: Seekbeam now gives the box the focus first (`WM_NEXTDLGCTL`).
- The end-to-end run showed an approximate match on a root's whole path ranked above an exact file: approximate matching no longer applies to root entries, and "folders first" never lifts an approximate folder above an exact file (regression test `ApproximateMatchesNeitherHitRootPathsNorPassExactFiles`).

## Not covered by the tool

- Dialogs of applications running **as administrator**: Windows blocks messages from a normal process (UIPI). Seekbeam detects it and copies the path to the clipboard; checking it needs an elevated test application.
- Direct typing through the real keyboard hook: keys injected by a test are ignored by design (that is how Esc gives the letters back), so the router is fed directly; the hook path is the same as double Ctrl, which is covered end to end.
- Dialogs with a custom template or drawn by toolkits that do not use the Windows common dialogs (some Java, Qt or Electron applications): they are not recognised, and Seekbeam then opens its normal launcher.
