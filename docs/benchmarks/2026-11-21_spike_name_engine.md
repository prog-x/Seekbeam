**English** | [Français](2026-11-21_spike_name_engine.fr.md)

# Spike — choosing the Name Index engine (2026-11-21)

Machine: Hyper-V VM, Intel i9-14900K (16 vCPU), 6.5 GB RAM, Windows 11 Pro, .NET 10.0.15, SQLite (SQLitePCLRaw 3.0.5).
Reproduction: `dotnet run -c Release --project bench/Seekbeam.Spike -- 2000000`

Dataset: 2,000,000 synthetic names (FR/EN vocabulary, dates, numbers, extensions), average length 34.4 characters.

| Query | Hits | Scan 1 thread | Scan 8 threads | FTS5 trigram (count) | LIKE '%x%' |
|---|---:|---:|---:|---:|---:|
| `ant 202611 cahier` | 19 | 8.72 ms | 3.40 ms | 8.58 ms | 216.6 ms |
| `rapport` | 87,829 | 13.38 ms | 4.81 ms | 20.26 ms | 200.9 ms |
| `doc` | 85,749 | 12.42 ms | 3.75 ms | 1.78 ms | 173.3 ms |
| `facture 2024` | 2,422 | 9.92 ms | 3.11 ms | 17.70 ms | 160.6 ms |
| `pdf` | 85,305 | 9.26 ms | 2.47 ms | 1.89 ms | 179.5 ms |
| `a` | 1,686,216 | 78.51 ms | 15.16 ms | 151.29 ms (LIKE, <3 chars) | 159.9 ms |
| `zzqx` | 0 | 4.79 ms | 1.14 ms | 0.06 ms | 149.4 ms |
| `surveillance data` | 7,217 | 13.87 ms | 2.33 ms | 33.07 ms | 200.9 ms |
| `proj 2023 final` | 88 | 9.41 ms | 3.07 ms | 12.14 ms | 184.1 ms |

| | Scan (blob) | FTS5 trigram |
|---|---|---|
| Build | 249 ms | 1,593 ms insert + 8,745 ms index |
| Size | 67.5 MB | 297.8 MB (table + index) |
| Substring < 3 characters | yes | no (LIKE fallback) |
| Fuzzy (typos) | yes (bitap on the same blob) | no |
| Predictable cost | O(bytes), independent of selectivity | depends on doclists (worst case 150 ms) |

Note: the FTS5 figures only count matches; ranking would additionally require reading the names.

**Conclusion**: Name Index = immutable segments of normalized names, memory-mapped, scanned in parallel with vectorized `Span<byte>.IndexOf`; SQLite remains the source of truth (catalog, queues, history). FTS5 is chosen for the Content Index (long text, BM25, phrases, operators), where it is a good fit.
