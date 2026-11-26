**English** | [Français](BENCHMARKS.fr.md)

# Seekbeam — Benchmarks

**Machine**: Hyper-V VM, Intel i9-14900K (16 vCPU), **6.5 GB of RAM**, NTFS virtual SSD, Windows 11 Pro, .NET 10.0.15, Release build (without ReadyToRun for the benchmark tools). Measured on 2026-11-21. VMs are noisy (±30% on the high percentiles).

Reproduction:
```powershell
dotnet run -c Release --project tools/Seekbeam.Bench -- synthetic <dataDir> <N>                  # synthetic catalog + search
dotnet run -c Release --project tools/Seekbeam.DatasetGen -- tree C:\SeekbeamBench\disk1m 1000000  # real tree (seed 42)
dotnet run -c Release --project tools/Seekbeam.Bench -- index <dataDir> C:\SeekbeamBench\disk1m Performance [content]
dotnet run -c Release --project tools/Seekbeam.Bench -- search <dataDir>
dotnet run -c Release --project tools/Seekbeam.Bench -- coverage <dataDir> 1200000          # content coverage census (dashboard)
```
Raw logs: `artifacts/bench/*.log`.

## 1. Engine choice (spike)
See [2026-11-21_spike_name_engine.md](benchmarks/2026-11-21_spike_name_engine.md): the vectorized scan is 2 to 10 times faster than FTS5 trigram in the worst case, 4.4 times more compact, and handles short substrings and fuzzy matching.

## 2. Real indexing — 1,000,000 files on disk
Dataset: 1,000,000 entries (64,302 folders, depth ≤ 12, accented and Unicode FR/EN names, 48 MB of text content). The cache was warm (freshly created dataset).

| Measure | Names only (Performance) | Names + content (Performance) |
|---|---|---|
| Total duration | **36.8 s** (27,159 entries/s) | 64.8 s (6,768 text documents extracted) |
| CPU (Seekbeam process) | 19.5 s, i.e. **0.53 core** (3.3% of the machine) | 31.6 s (0.49 core) + capped extraction hosts |
| Private RAM peak / end | **199 MB / 109 MB** | 185 MB / 102 MB |
| Working set peak | 266 MB | 257 MB |
| Process I/O (read / written) | 535 MB / 654 MB (writes: data folder only) | 1,181 MB / 1,306 MB |
| Search during indexing | **p50 3.8 ms, p95 15.5 ms**, max 30.9 ms | p50 5.3 ms, p95 10.6 ms |
| Catalog + segments size | 162.6 + 38.1 MB = 210 bytes/entry | 214 bytes/entry; content 52 MB |

Fix that came out of this benchmark: the first "names + content" run took 290 s and read 139 GB, because of a `COUNT(*)` on the queue in the hot path and because empty files were queued. After the fix: 64.8 s and 1.2 GB.

### Resource modes (subtree of 119,074 entries, names only)
| Mode | Throughput | CPU |
|---|---|---|
| Economy | **2,002 entries/s** (2,000 cap respected) | 0.05 core |
| Balanced | 8,207 entries/s* | 0.17 core |
| Performance | 6,990 entries/s* | 0.15 core |

\* measured while another benchmark was saturating the disk: throughput is limited by I/O, not by the mode cap.

## 3. Launcher search (top 8, 40 queries × 20 repetitions, including typos, filters and Unicode)
| Index | Load | p50 | p95 | p99 | Successive typing p95 | Private RAM afterwards |
|---|---|---|---|---|---|---|
| 1 M **real** (disk) | 70 ms | **2.7 ms** | **7.6 ms** | 25.8 ms | 8.0 ms | 107 MB |
| 1 M synthetic | 74 ms | 3.6 ms | 10.4 ms | 14.4 ms | 20.1 ms | 38–110 MB |
| 2 M synthetic | 68 ms | 6.2 ms | 20.0 ms | 30.4 ms | 46 ms | 65 MB |
| 5 M synthetic | 21 ms | 10.7 ms | **51.3 ms** | 57.6 ms | 139 ms | 195 MB (working set 423 MB, including mapped pages) |

Details at 5 M (ms): `ant 202611 cahier` 10.5 · `rapport` 5.7 · `doc` (490k matches) 7.4 · `a` (1.1 M) 18 · `facture 2024` 38.9 (worst case: ancestor lookups). Catalog 711 MB + segments 206 MB; populated at 10,457 entries/s (48 merges).

**Observed limit**: at 5 M, the first very short prefixes of a typing sequence ("c", "ca") cost up to 140 ms, because the full scan is repeated on every keystroke. Possible fix: incremental refinement (reuse the candidates of the previous query when the new one extends it).

Details on 1 M real (ms): `ant 202611 cahier` 4.8 · `rapport` 2.5 · `doc` (97k matches) 6.7 · `a` (220k) 6.8 · `facture 2024` 25.5 (the token `2024` appears in thousands of folder names, hence ancestor lookups) · `surveillance data` 7.2 · `rapprot`, `facutre`, `survellance`, `presentaton` (typos, fuzzy pass) 2–3 · `ext:pdf rapport` 1.6 · `kind:folder projets` 0.5.

### Optimization history (1 M synthetic)
| Step | `a` | Overall p95 | Overall p99 |
|---|---|---|---|
| Initial version | 88 ms | 14.7 ms | 87.6 ms |
| + lock-free favorites, hoisted spans, 1 character at word start | 5.7 ms | 9.2 ms | 18.4 ms |
| + folder sidecar (path token feasibility) | 6–12 ms | 9.5–10.4 ms | 14.4–20.6 ms |
| `ant 202611 cahier`: 18 ms → **3.5–4.8 ms** thanks to the sidecar | | | |

## 4. Real application (Seekbeam.exe, 1 M index loaded)
| Measure | Value |
|---|---|
| Launch → index ready | **~0.21 s** (Name Index loaded in 17 ms) |
| Private RAM at idle (no index, empty profile) | 63–69 MB (working set ~140 MB) |
| Private RAM 60 s after startup (1 M entries, reconciliation running) | **66 MB** (working set 143 MB) |
| Search displayed in the launcher, during the startup reconciliation of the million entries | 15–62 ms (non-R2R build, first query with JIT) |
| Double Ctrl → window visible (UIA measurement including 2 simulated key presses and polling) | 369 ms (upper bound; the window is pre-created) |

### Content coverage (dashboard, `ContentCoverage`)
Read-only census of the content state of every file (merge join of catalog / queue / content.db, key-paginated batches of 10,000). Synthetic catalog of 1.2 M entries (**1,121,068 files**), content.db with 708,348 documents (indexed, without text, failed), one top-level folder excluded from content indexing. 3 consecutive runs, log: `artifacts/bench/coverage1m.log`.

| Measure | Value |
|---|---|
| Duration (median of 3) | **1.63–1.68 s** (≈ 670,000 files/s) |
| Extra private RAM during the census | **+4 to +12 MB** warm; +34 MB on the first pass (JIT, SQLite cache) |
| Source files read | none (catalog and content.db only) |

Memory does not depend on the number of files: only on one batch of 10,000 rows, the parent-folder cache (only when folders are excluded) and the extension histogram (capped at 4,096 entries). In production the census runs at low priority and yields to searches between batches.

## 5. Results against the PERFORMANCE_PLAN
| Target | Result | Status |
|---|---|---|
| p50 / p95 / p99 ≤ 5 / 15 / 30 ms at 1 M | 2.7 / 7.6 / 25.8 ms (real) | ✅ |
| p95 ≤ 30 ms at 2 M | 20.0 ms | ✅ |
| p95 ≤ 60 ms at 5 M | 51.3 ms | ✅ (typing very short prefixes: 139 ms, ❌ see §3) |
| Startup ≤ 1.5 s | ~0.21 s | ✅ |
| Private RAM at idle ≤ 150 MB | 63–69 MB (66 MB with 1 M entries) | ✅ |
| RAM while indexing ≤ 300 MB | peak 199 MB | ✅ |
| RAM growth 1 M → 2 M < 15% | 107 → 65 MB private after search (no linear growth; 195 MB at 5 M, mostly GC pool) | ✅ |
| Scan ≥ 20,000 entries/s (Performance) | 27,159 entries/s | ✅ |
| ≤ 120 bytes/entry | 210 bytes/entry | ❌ (SQLite catalog and secondary indexes; possible fix in PROJECT_STATUS) |
| Event → searchable ≤ 3 s | 1.5 s coalescing + scan; verified in tests (≤ 10 s wait, typically < 2 s) | ✅ |

## 6. Folder index and "go to folder" (2026-11-26, TD-20/TD-21)

Same VM (i9-14900K, 16 vCPU, 9.3 GB of RAM at the time of this run), Release build. The folder index was measured on the **real C: drive** of the development machine, with the default exclusions (no indexed location, so that everything goes through the folder index).

```powershell
dotnet run -c Release --project tools/Seekbeam.Bench -- folderindex <dataDir> [Performance|Auto]
```

| Measure | Performance mode | Automatic mode (default) |
|---|---|---|
| Folders walked (C:, depth 16, Windows/caches/build folders excluded) | **93,816** | 93,816 |
| Walk duration | **12.0 s** (7,845 folders/s) | 54.9 s (1,708 folders/s: the resource manager paces it) |
| CPU | 8.8 s, **0.74 core** | 18.9 s, **0.34 core** |
| Private RAM peak / end | 154 / 141 MB | **93 / 93 MB** |
| Catalog + name index | **19.8 + 3.6 MB** (≈ 250 bytes/folder) | same |
| "Go to folder" search during the walk | p50 0.7 ms, p95 5.9 ms, max 30.9 ms | p50 0.5 ms, **p95 1.0 ms**, max 25.1 ms |
| "Go to folder" search afterwards (20 queries × 20, top 8) | p50 0.3 ms, **p95 1.0 ms**, p99 1.1 ms | p50 0.3 ms, p95 1.3 ms, p99 1.7 ms |

Examples on that drive: `prog fil` → `C:\Program Files`; `prog\micro` → `C:\Program Files (x86)\Microsoft` (this case led to the `LeafPrefix` signal and a milder "ranked lower" penalty, see TD-21).

**Launcher search, no regression** (1 M synthetic, seed 42, 40 queries × 20, same machine, the previous version (HEAD) and the new one built and run back to back):

| Version | p50 | p95 | p99 | Successive typing p95 |
|---|---|---|---|---|
| Before (HEAD), populate + search | 3.49 ms | 12.59 ms | 20.61 ms | 18.29 ms |
| After, populate + search | 3.30 ms | 12.54 ms | 19.61 ms | 19.65 ms |
| Before (HEAD), search again | 3.50 ms | 10.57 ms | 18.08 ms | 17.05 ms |
| After, search again | 3.50 ms | 9.52 ms | 11.12 ms | 18.57 ms |

The differences are within the noise of the VM: the folder-index checks cost one flag test per record, the path-order signals are computed on the short list only, and `a\b` chains only when typed.

**Real dialogs**: `tools/Seekbeam.DialogCheck` (36/36, see [DIALOG_NAVIGATION_CHECK.md](DIALOG_NAVIGATION_CHECK.md)) runs the whole chain, from double Ctrl over the dialog to the dialog showing the folder, in about 1 s per case including its own waits; the launcher appears glued under the dialog.
