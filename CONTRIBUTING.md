**English** | [Français](CONTRIBUTING.fr.md)

# Contributing to Seekbeam

Thanks for your interest! Seekbeam is maintained by [prog-x](https://github.com/prog-x). Bug reports, translations, documentation and code are all welcome. Issues and pull requests may be written in English or French.

## Before you start

- **Bugs and ideas**: open an issue with the matching template. Check [ROADMAP.md](ROADMAP.md) for what is already planned.
- **Security problems, or a case where Seekbeam changed a file**: do not open a public issue. Follow [SECURITY.md](SECURITY.md).
- **Large changes** (new index format, new dependency, new extractor, change to file access): open an issue first to agree on the approach.
- **Translations**: see [docs/TRANSLATING.md](docs/TRANSLATING.md). No C# knowledge is needed.

## Development setup

Requirements: Windows 10 or 11 x64, the .NET 10 SDK (version pinned in `global.json`), and optionally Visual Studio 2026, Rider or VS Code.

```powershell
git clone https://github.com/prog-x/Seekbeam.git
cd Seekbeam
dotnet build Seekbeam.sln              # Debug build
dotnet test Seekbeam.sln               # all tests (they use the Debug binaries)
dotnet run --project src/Seekbeam.App -- --data "$env:TEMP\seekbeam-dev"   # isolated profile, does not touch your real index
pwsh build/Build-Release.ps1         # MSI + portable zip in artifacts/installer
```

Use `--data <folder>` while developing: it keeps a separate index, configuration and single-instance lock, so you can run a development build next to an installed Seekbeam. The code layout and the benchmark commands are in [docs/DEVELOPER_GUIDE.md](docs/DEVELOPER_GUIDE.md).

## Rules that pull requests must follow

### 1. Seekbeam never modifies the files it indexes
This rule overrides every feature and optimization ([docs/FILE_IMMUTABILITY_POLICY.md](docs/FILE_IMMUTABILITY_POLICY.md)).
- Source files and folders are read **only** through `SourceFileAccess`, and Seekbeam writes **only** through `SeekbeamStorage`, inside its own data folder.
- File-write APIs are banned by the build (`src/BannedSymbols.txt`, error RS0030). Do not suppress the error outside `Storage/` and `SourceAccess/`.
- `StaticAnalysisTests` analyze the compiled IL. A new P/Invoke must be added to the allow-list, with a justification that is reviewed.
- Features that rename, move, delete or edit user files will not be accepted.
- A change that touches file access needs an integrity test (`tests/Seekbeam.Immutability.Tests`).

### 2. Performance and resources
Search must stay interactive with millions of files, and indexing must stay discreet. For changes to search, scanning or indexing, include measurements (`tools/Seekbeam.Bench`, see [docs/BENCHMARKS.md](docs/BENCHMARKS.md)) before and after.

### 3. User interface text in four languages
No hard-coded visible text: XAML uses `{l:Tr Key}` and C# uses `L.T("Key")` / `L.F("Key", …)`. Add each new key to the four `src/Seekbeam.App/Localization/Strings*.resx` files (en, fr, de, it). If you cannot translate a language, copy the English text and say so in the pull request. `LocalizationTests` check this.

### 4. Documentation in English and French
Each document exists as `X.md` (English) and `X.fr.md` (French). When you change one, update the other in the same pull request. If you only write one language, say so and a maintainer will complete the other.

### 5. Code style
- C# with nullable enabled and file-scoped namespaces. Follow the style of the surrounding code.
- Comments explain *why*. Errors are handled explicitly, and long operations take a `CancellationToken`.
- No new dependency without discussing its license, maintenance, size and transitive dependencies. Shipped dependencies must allow MIT redistribution, and must be added to `THIRD-PARTY-NOTICES.md`.
- Every important change comes with tests.

## Pull requests

1. Fork the repository and create a branch from `main` (`fix/…`, `feat/…`, `docs/…`, `i18n/…`).
2. Keep each pull request focused on one subject. Write commit messages in the imperative mood ("Fix depth notice when a root is removed").
3. Make sure `dotnet build Seekbeam.sln -c Release` and `dotnet test Seekbeam.sln` pass locally. CI runs both on Windows.
4. Fill in the pull request checklist.

## Releases (maintainers)

1. Update `<Version>` in `Directory.Build.props` and the `Unreleased` section of `docs/RELEASE_NOTES.md` / `.fr.md`.
2. Tag and push: `git tag v1.1.0 && git push origin v1.1.0`.
3. The `Release` workflow builds, tests and packages Seekbeam, then creates a **draft** GitHub release with the MSI, the portable zip and `SHA256SUMS.txt`. Review it and publish it.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE) of the project.
