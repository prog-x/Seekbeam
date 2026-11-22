## What and why

<!-- Short description of the change and the problem it solves. Link the issue: Fixes #123 -->

## Checklist

- [ ] `dotnet build Seekbeam.sln -c Release` succeeds (no RS0030 banned-API error).
- [ ] `dotnet test Seekbeam.sln` passes (Debug build).
- [ ] **File immutability**: no new code writes, moves, renames or deletes outside `SeekbeamStorage`; source files are only read through `SourceFileAccess`. If file access changed, the change is explained below.
- [ ] **UI text**: every new or changed visible string is in the four `Strings*.resx` files (en, fr, de, it).
- [ ] **Docs**: both `X.md` (English) and `X.fr.md` (French) are updated when documentation changed.
- [ ] Performance-sensitive code (search, scanning, indexing): measured, numbers below.

## Notes for the reviewer
