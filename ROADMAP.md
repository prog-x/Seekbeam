**English** | [Français](ROADMAP.fr.md)

# Seekbeam — Roadmap

Planned work after version 1.0.0. Items are not scheduled; order within a section reflects priority.

## 1. User interface localization — done (unreleased)

**Status**: implemented. The UI ships in English, French, German and Italian; translator guide in [docs/TRANSLATING.md](docs/TRANSLATING.md). Remaining: optional Weblate connection and screenshots of each window per language. The original design is kept below for reference.

**Goal**: ship the UI in **French, English, German and Italian**, and let an external contributor add or fix a language without touching C# or XAML.

Planned design:
- **Resources**: move every UI string (XAML windows, tray menu, dialogs, dashboard, error messages shown to the user) out of the code into `.resx` files: `Strings.resx` (English, the neutral fallback), `Strings.fr.resx`, `Strings.de.resx`, `Strings.it.resx`. `.resx` is the standard .NET format: it is compiled into satellite assemblies, needs no extra dependency, and is understood by translation platforms (Weblate, Crowdin) and by editors such as ResXResourceManager.
- **XAML binding**: a small markup extension (`{l:Tr Key}`) so XAML stays readable. A key that is missing in a language falls back to English, and never to an empty string.
- **Language selection**: by default the Windows display language (`CurrentUICulture`), falling back to English. A setting in Settings › General lets the user override it. On save, the launcher and the tray menu are rebuilt immediately; other windows use the new language when they are reopened.
- **Formats**: dates, sizes and numbers go through `CultureInfo`, never hand-written separators.
- **Contributor workflow**:
  - `docs/TRANSLATING.md` (EN + FR) explains how to add a language (copy `Strings.resx` to `Strings.xx.resx`, translate the values, build, check).
  - A build test fails if a translation contains a key the neutral file does not have, or a malformed placeholder (`{0}`). It also reports missing keys per language, as a warning rather than an error.
  - Optional: connect the repository to Weblate, which offers free hosting for open-source projects, so contributors can translate in a browser.
- **Out of scope**: search behavior is already language-neutral (case, accent and Unicode normalization; the FTS5 `unicode61` tokenizer). Known limit: German compound words are not split, so `Rechnung` does not match inside `Stromrechnung` in full-text search. Name search is substring-based and does match it.

Acceptance: all four languages complete, no hard-coded user-visible string left (checked by a test that scans XAML for literal `Text=`/`Content=`/`Header=` values), and screenshots of each window in each language.

## 2. Publication on GitHub (preparation checklist)

**Repository content**
- [x] Choose the license and add `LICENSE` at the root (see §3): MIT.
- [x] `THIRD-PARTY-NOTICES.md` listing the bundled dependencies and their licenses. These are redistributed inside the MSI and zip, and Apache-2.0 requires including their license text and notices.
- [x] `CONTRIBUTING.md` (EN + FR) covering build, tests, the immutability rule (any new file access goes through `SourceFileAccess`), the bilingual documentation rule, and the commit style.
- [x] `SECURITY.md`: how to report a vulnerability privately (GitHub private vulnerability reporting). This matters because Seekbeam makes a non-modification claim.
- [x] `CODE_OF_CONDUCT.md` (Contributor Covenant).
- [x] `.github/ISSUE_TEMPLATE/` (bug report asking for version, Windows build and the log excerpt; feature request) and a pull request template with an "updated EN and FR docs" checkbox.
- [x] Project input documents (2026-11-22): `CLAUDE.md` is published as a shorter file that holds the project rules only. The agent's private working instructions moved to `CLAUDE.local.md`, which git ignores. The original specification moved to `docs/project/SPECIFICATIONS.md` as a historical document. The original launch prompt (`PROMPT_LAUNCH.md`) stays out of the repository.
- [x] Review the docs for machine-specific paths and personal data. The first commit is the whole history, so nothing private may be in it. (2026-11-22: none found.)
- [ ] Screenshots and a short GIF of the launcher in `README.md`.
- [x] Name check (2026-11-21): the project was first called "Findex", but a Windows local file search app with that name already exists (Product Hunt), so it was renamed **Seekbeam** before publication. Checked for Seekbeam: no software, company or trademark found; no GitHub repository or account; NuGet, npm, PyPI and crates names free; seekbeam.com for sale, seekbeam.app registered but unused. Rejected along the way: Flashseek (a Windows file search repository with the same name), Dowse (an open-source Windows search tool), Keyseek, Reperio (US trademark), Seekdex and others. TMview check (2026-11-22): no SEEKBEAM trademark found.

**Continuous integration (GitHub Actions, `windows-latest`)**
- [x] Workflow `ci.yml` on push and PR: `dotnet build -c Release` (BannedApiAnalyzers runs as an error), `dotnet test Seekbeam.sln` (including the IL immutability analysis and the integrity tests).
- [x] Workflow `release.yml` on a `v*` tag: `pwsh build/Build-Release.ps1`, then publish the MSI and the portable zip on a GitHub Release with their SHA-256 checksums.
- [ ] Optional: a periodic job running `tools/audit/Run-DynamicAudit.ps1` and attaching the report.
- [x] Dependabot for NuGet packages.

**Distribution**
- [ ] Code signing. Unsigned binaries trigger SmartScreen warnings. The SignPath Foundation offers free signing for open-source projects; otherwise a commercial certificate is required.
- [x] WiX Toolset licensing (checked 2026-11-21): WiX is MS-RL, which does not apply to the generated MSI. WiX v6 ships the *Open Source Maintenance Fee* EULA v1.0: "The Fee applies only to Users that generate revenue by the Software. Non-revenue-generating use of the Software is exempt." Seekbeam is free and non-commercial, so no fee applies, and v6 does not enforce acceptance. WiX v7 (EULA v1.1, fee above US$10,000 of annual revenue) blocks builds until the EULA is accepted: Seekbeam stays on 6.0.x, and Dependabot ignores WiX major updates. Re-check if Seekbeam ever generates revenue.
- [ ] Optional: a `winget` manifest once releases are signed.

## 3. License — decided: MIT

`LICENSE` (MIT, "prog-x and Seekbeam contributors") is at the repository root, and the release script copies it with `THIRD-PARTY-NOTICES.md` into the MSI and the portable zip. The analysis that led to the choice is kept below.

| Dependency (shipped) | License |
|---|---|
| SQLite (e_sqlite3) | Public domain |
| Microsoft.Data.Sqlite | MIT |
| SQLitePCLRaw | Apache-2.0 |
| PdfPig | Apache-2.0 |

Build and test only: xUnit (Apache-2.0), Microsoft.CodeAnalysis.BannedApiAnalyzers (MIT), WiX Toolset (MS-RL + OSMF).

All shipped dependencies are permissive, so any of the following works:
- **MIT**: the shortest and most widely understood license, and maximizes reuse. Anyone may build a closed-source product from Seekbeam.
- **Apache-2.0**: like MIT, plus an explicit patent grant and a NOTICE mechanism. Slightly heavier.
- **GPL-3.0**: modified versions that are redistributed must stay open source (this is the Recoll model). It may deter some contributors and companies.

Recommendation: **MIT** for a personal utility meant to be widely used. Choose **GPL-3.0** if preventing closed-source forks matters more than adoption.

## 4. Other improvements (from the 1.0 status)
- Incremental refinement when typing: reuse the previous query's candidates when the new query extends it. Target: one- or two-letter prefixes at 5 M entries under 60 ms p95 (currently 139 ms).
- More compact catalog (target 120 bytes/entry, currently 210): drop `ix_entries_root`, pack `mtime`/`ctime`.
- Update the stored depth of a moved folder's subtree immediately.
- Optional USN/MFT mode for elevated installations.
- Optional OCR (Windows.Media.Ocr), 7z/rar archives, jump lists for discovery.
- Automatic re-extraction when an extractor version changes.
- Rich preview (images, PDF).
- Run the ETW audit (`tools/audit/Run-EtwAudit.ps1`, administrator) and add its results to `docs/FILE_IMMUTABILITY_AUDIT.md`.
