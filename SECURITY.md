**English** | [Français](SECURITY.fr.md)

# Security policy

## Supported versions

Only the latest released version receives fixes. Please check that the problem still occurs with it.

## Reporting a vulnerability

**Do not open a public issue.** Report it privately through GitHub: on the repository page, open **Security › Report a vulnerability** ([direct link](https://github.com/prog-x/Seekbeam/security/advisories/new)). Reports may be written in English or French.

Please include:
- the Seekbeam version and edition (MSI, portable, built from source) and the Windows version;
- what an attacker can do, and the steps to reproduce (a minimal file or folder structure when possible);
- whether the problem is already public.

What to expect: an acknowledgement within 7 days, an assessment within 30 days, and a fix released as soon as possible depending on severity. You will be credited in the release notes unless you prefer otherwise. Seekbeam is maintained by volunteers, so these are goals, not contractual commitments.

## What we treat as a security issue

Seekbeam makes one central promise: **it never modifies, moves, renames or deletes the files and folders it indexes** ([docs/FILE_IMMUTABILITY_POLICY.md](docs/FILE_IMMUTABILITY_POLICY.md)). These are handled as security issues:

- **Any change to an indexed file or folder caused by Seekbeam**: content, timestamps (other than the last-access time updated by Windows itself), attributes, permissions, or a file created in an indexed folder. If you can reproduce it, the snapshot tool helps: `dotnet run --project tools/Seekbeam.DatasetGen -- snapshot <folder> before.json`, reproduce, take `after.json` the same way, then `compare before.json after.json`.
- A crafted document or archive that crashes the extraction host in an exploitable way, escapes its limits (memory, CPU, time, ZIP bomb protection), or writes to disk.
- A way out of the extractor sandbox: a document that makes `Seekbeam.ExtractorHost.exe` read a file other than the one being extracted, reach the network, write, or start a process.
- Seekbeam writing outside its data folder (`%LOCALAPPDATA%\Seekbeam`, or `Data\` next to `Seekbeam.exe` in portable mode), for example through a junction or a crafted path. The only exception is the one-time sandbox set-up (an AppContainer profile and one read permission on Seekbeam's own program folder, TD-17), which is never done inside an indexed folder.
- Index content, file paths or search history sent off the computer. Seekbeam has no telemetry and makes no network requests.
- Privilege escalation, or reading files that the current Windows user cannot read.

## How Seekbeam limits the impact of a malicious document

Indexing content means parsing documents automatically, including files you never opened. Seekbeam assumes that a document can be hostile:

- **Downloads is indexed by name only.** Files from the Internet and e-mail attachments land there, so by default their content is never read. You can change this in **Settings › Content**; Seekbeam explains the risk and asks for confirmation first.
- **Extraction runs in a sandbox.** Text is extracted by a separate process, `Seekbeam.ExtractorHost.exe`, started in a Windows **AppContainer** with no capabilities: it cannot open your files, write anywhere, use the network, read the user registry or start another process. Seekbeam opens each document itself, read-only, and hands the sandbox a handle that can only read that one file. The process is also bounded by a Job Object (memory, CPU, time) and runs with exploit mitigations. If the AppContainer cannot be used, it falls back to a low-integrity process, which cannot write but can still read and use the network. The level in use is shown in the dashboard.
- **Limits per document**: maximum size, characters, archive depth, entry count and compression ratio (ZIP bombs), and a timeout after which the extraction process is killed and restarted.

Details and test results are in [TECHNICAL_DECISIONS.md](TECHNICAL_DECISIONS.md) (TD-17, TD-18).

## Keeping dependencies up to date (maintainers)

Document parsers are the most exposed code, so their updates must not wait.

1. In the repository settings (**Settings › Code security**), enable **Dependency graph**, **Dependabot alerts** and **Dependabot security updates**, and **Private vulnerability reporting**. Security updates are opened as soon as an advisory is published.
2. [`.github/dependabot.yml`](.github/dependabot.yml) also opens grouped version updates every Monday: NuGet packages (parsers in their own `document-parsers` group), the .NET SDK, which provides the runtime bundled in releases, and GitHub Actions (monthly).
3. Every restore runs **NuGet audit** on direct and transitive packages. CI treats a known vulnerability as an error, so a vulnerable dependency cannot be merged or released. Locally, `dotnet list package --vulnerable --include-transitive` gives the same report.
4. Merge a dependency pull request only when CI is green. The test suite includes the extraction tests on every format, the sandbox confinement tests and the file-immutability tests. Then publish a release, because users only get the fix through a new version.

## Out of scope

- **Changes made by another application** that Seekbeam asked Windows to open (Word, an editor, the Properties dialog). Opening a file hands it to the associated application, which Seekbeam does not control.
- The last-access time that NTFS may update when a file is read (an operating system behavior, see the audit report).
- Information that is visible to the user who owns the index: the index stores names, paths and extracted text of files that this user can already read.

See [docs/FILE_IMMUTABILITY_AUDIT.md](docs/FILE_IMMUTABILITY_AUDIT.md) for how the non-modification guarantee is verified and what its limits are.
