**English** | [Français](INSTALLATION.fr.md)

# Installation

## Prerequisites
Windows 10 (1809+) or Windows 11, x64. You don't need to install a runtime: the packages are *self-contained* (.NET 10 included).

## MSI (recommended)
- **Install location**: `Seekbeam-1.0.0-x64.msi` installs Seekbeam **for the current user, without elevation**, in `%LOCALAPPDATA%\Programs\Seekbeam`.
- **What it adds**: two Start menu shortcuts (Seekbeam, and Seekbeam — Advanced search), plus automatic startup (`HKCU\…\Run\Seekbeam`).
- **After installing**: in interactive mode, Seekbeam starts at the end of the installation.
- **Silent install**: `msiexec /i Seekbeam-1.0.0-x64.msi /qn`. Uninstall: `msiexec /x Seekbeam-1.0.0-x64.msi /qn`.
- **Update**: install the new version (MajorUpgrade). The configuration and indexes are kept, and the schema is migrated automatically.
- **Uninstall**: removes the binaries, shortcuts and automatic startup. Your data (`%LOCALAPPDATA%\Seekbeam`) and the extractor sandbox profile are deliberately kept. To delete them, run `Seekbeam.exe --purge-data` before uninstalling.

## Portable
Unzip `Seekbeam-1.0.0-x64-portable.zip` anywhere and run `Seekbeam.exe`.

The `portable.txt` file enables portable mode: data goes to `Data\` next to the executable, and there is no automatic startup. The only thing Seekbeam leaves outside its folder is the extractor sandbox profile (TD-17): Windows creates `%LOCALAPPDATA%\Packages\seekbeam.extractorhost` and a registry entry under `HKCU\Software\Classes\Local Settings`. `Seekbeam.exe --purge-data` removes them along with the data.

Unzip it **outside the folders Seekbeam indexes** (for example `C:\Tools\Seekbeam`, not Documents or the Desktop). Seekbeam never changes the permissions of an indexed folder, so from there the extractor cannot use its AppContainer sandbox and runs at low integrity instead (see [TROUBLESHOOTING.md](TROUBLESHOOTING.md)).

## Command line
| Option | Effect |
|---|---|
| `--background` | starts without showing the launcher |
| `--deep "query"` | opens advanced search |
| `--settings`, `--dashboard` | opens the corresponding window |
| `--exit` | cleanly stops the running instance |
| `--data <folder>` | isolated profile (data in that folder, no automatic startup) |
| `--purge-data` | deletes indexes, history, logs, configuration and the extractor sandbox profile (never your files) |

Running `Seekbeam.exe` while it is already running shows the launcher of the existing instance.

## Signing
This version's binaries are not signed, so SmartScreen may show a warning on first launch. For distribution, sign `Seekbeam.exe`, `Seekbeam.ExtractorHost.exe` and the MSI with `signtool`.

## Building the packages
Run `pwsh build/Build-Release.ps1`. It writes the packages to `artifacts/installer/` in these steps:
1. build;
2. tests;
3. self-contained ReadyToRun publish;
4. WiX v6 MSI via `dotnet build`;
5. portable zip.
