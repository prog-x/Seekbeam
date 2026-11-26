**English** | [Français](README.fr.md)

# Seekbeam

Seekbeam is a fast local search tool, application launcher and document indexer for Windows. It brings together the core features of Listary, Recoll and DocFetcher in one native app. Everything stays on your machine, and Seekbeam **never modifies your files**.

> **UI languages:** English, French, German and Italian. Seekbeam follows the Windows display language (English if it is not one of these) and can be switched in Settings › General › Language. Translations are plain `.resx` files: see [docs/TRANSLATING.md](docs/TRANSLATING.md) to fix one or add a language.

## Quick start

1. **Install** with `Seekbeam-1.1.0-x64.msi` (per user, no administrator rights), or unzip `Seekbeam-1.1.0-x64-portable.zip` and run `Seekbeam.exe`.
2. **Press Ctrl twice** to open the search window.
3. **Type** a few letters from any part of the name, in any order.
4. **Press Enter** to open the file or launch the app.
5. **Full-text search:** press `Ctrl+D` in the launcher to search document contents.

## Features

- **Double Ctrl**: the search window opens instantly (centered or near the cursor).
- **Names and paths**: words in any order, partial words, case- and accent-insensitive, typo-tolerant. `Ant 202611 cahier` finds `20261121_cahier_des_charges_…_Anthropic_Lucens.docx`.
- **Applications**: Start menu, desktop, Store apps (UWP/MSIX), and executables from folders you choose.
- **Commands and aliases**: Win+R commands (`mstsc`, `regedit`, `services.msc`, with arguments: `mstsc /v:server`) and your own keywords (`hosts` opens the hosts file in Notepad as administrator).
- **Go to folder in any Open / Save dialog**: Ctrl twice over the dialog (or just type in its file list), type bits of the path (`cli dup fact`) or an absolute path, Enter: the dialog shows that folder, like Listary. Works in Explorer too, with Quick Switch (`Ctrl+G`: folder of the last Explorer window). Seekbeam only navigates, it never validates the dialog. Folder names of every fixed drive are indexed for this (never files nor content). See the [user guide](docs/USER_GUIDE.md#dialogs-and-folders-go-to-folder).
- **Full text**:
  - formats: PDF, Word (DOC/DOCX), Excel (XLS/XLSX), PowerPoint (PPT/PPTX), OpenDocument, RTF, HTML/XML, JSON/YAML/CSV, source code, logs and ZIP archives;
  - queries: exact phrases, `OR` and exclusions;
  - filters on type, location, date and size, with snippets and a preview.
- **Millions of files**: on 1M entries a query takes a few milliseconds at p95, and the app uses about 40 to 110 MB of private RAM (see [docs/BENCHMARKS.md](docs/BENCHMARKS.md)).
- **Unobtrusive**:
  - indexing adapts to the machine through four modes (Economy, Balanced, Performance, Auto);
  - it pauses for full-screen apps, on battery and while you search;
  - extraction runs in a separate process whose RAM and CPU are capped by the kernel.
- **Read-only, proven**: static IL analysis, SHA-256 and metadata tests, and an independent dynamic audit (see [docs/FILE_IMMUTABILITY_AUDIT.md](docs/FILE_IMMUTABILITY_AUDIT.md)).
- **Privacy**: no telemetry and no network connection. The index, history and learning data stay on your machine.

## Installation

- **MSI** (per user, no administrator rights): `Seekbeam-1.1.0-x64.msi`.
- **Portable**: unzip `Seekbeam-1.1.0-x64-portable.zip` and run `Seekbeam.exe`. Data is stored in `Data\` next to the executable.

Details: [docs/INSTALLATION.md](docs/INSTALLATION.md). User guide: [docs/USER_GUIDE.md](docs/USER_GUIDE.md).

## Launcher shortcuts

| Key | Action |
|---|---|
| Ctrl, Ctrl | open / close |
| ↑ ↓ PgUp PgDn | navigate |
| Enter | open / launch |
| Ctrl+Enter | open containing folder |
| Shift+Enter | run as administrator |
| Alt+Enter | properties |
| Tab / right-click | actions menu |
| Ctrl+C / Ctrl+Shift+C | copy path / name |
| Ctrl+F | favorite |
| Ctrl+D | advanced search (full text) |
| Ctrl+, | settings |
| Esc | close |
| Ctrl, Ctrl over an Open / Save dialog | go to folder in that dialog (Enter: go; Ctrl+G: last Explorer folder; Esc: back) |

**Syntax:**
- `word1 word2`: all words must match, as substrings;
- `"exact phrase"`: the words must appear together;
- `-excluded`: leaves out names containing this word;
- `ext:pdf,docx`: filters by extension;
- `kind:file|folder|app`: filters by type;
- `in:foldername`: limits the search to a folder.

## Build from source

Prerequisites: Windows 10/11 x64 and the .NET 10 SDK (10.0.4xx). Nothing else is needed.

```powershell
dotnet build Seekbeam.sln -c Release
dotnet test Seekbeam.sln                     # 180+ tests (unit, integration, extractors, immutability)
pwsh build/Build-Release.ps1               # self-contained publish + MSI + portable zip in artifacts/installer
pwsh tools/audit/Run-DynamicAudit.ps1      # dynamic immutability audit (no administrator rights)
```

## Documentation

| Document | Contents |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | architecture, indexes, algorithms, ranking |
| [TECHNICAL_DECISIONS.md](TECHNICAL_DECISIONS.md) | decisions and alternatives |
| [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) · [PROJECT_STATUS.md](PROJECT_STATUS.md) | plan and status |
| [ROADMAP.md](ROADMAP.md) | planned features and GitHub publication checklist |
| [PERFORMANCE_PLAN.md](PERFORMANCE_PLAN.md) · [docs/BENCHMARKS.md](docs/BENCHMARKS.md) · [spike](docs/benchmarks/2026-11-21_spike_name_engine.md) | targets and measurements |
| [RISKS.md](RISKS.md) | risks and mitigations |
| [docs/FILE_IMMUTABILITY_POLICY.md](docs/FILE_IMMUTABILITY_POLICY.md) · [docs/FILE_IMMUTABILITY_AUDIT.md](docs/FILE_IMMUTABILITY_AUDIT.md) | no-modification guarantee |
| [docs/USER_GUIDE.md](docs/USER_GUIDE.md) · [docs/CONFIGURATION.md](docs/CONFIGURATION.md) · [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | usage |
| [docs/TRANSLATING.md](docs/TRANSLATING.md) | translating the user interface |
| [docs/DEVELOPER_GUIDE.md](docs/DEVELOPER_GUIDE.md) · [docs/INSTALLATION.md](docs/INSTALLATION.md) · [docs/RELEASE_NOTES.md](docs/RELEASE_NOTES.md) · [docs/FINAL_REPORT.md](docs/FINAL_REPORT.md) | development and delivery |

## Contributing and documentation languages

The documentation is maintained in **English and French**. Each document exists as `X.md` (English) and `X.fr.md` (French), and line 1 of each file links to the other language.

**Every change must update both versions in the same change.**

Contributions are welcome: read [CONTRIBUTING.md](CONTRIBUTING.md) (setup, rules, pull requests) and the [code of conduct](CODE_OF_CONDUCT.md). To report a vulnerability, or a case where Seekbeam modified a file, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Authors

Created and maintained by [prog-x](https://github.com/prog-x). Contributors are listed on the [GitHub contributors page](https://github.com/prog-x/Seekbeam/graphs/contributors).

## License

Seekbeam is released under the [MIT License](LICENSE), **as is, without warranty of any kind** (see the licence). The components it redistributes keep their own licenses: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
