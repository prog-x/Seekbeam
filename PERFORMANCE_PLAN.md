**English** | [Français](PERFORMANCE_PLAN.fr.md)

# Seekbeam — Performance plan

## Targets (validated after an initial benchmark, then frozen)

| Metric | Target | Measurement |
|---|---|---|
| Launcher search p50 / p95 / p99 — 1 M entries | ≤ 5 / 15 / 30 ms | `Seekbeam.Bench search` |
| Launcher search p95 — 2 M entries | ≤ 30 ms | same |
| Launcher search p95 — 5 M entries | ≤ 60 ms | same (synthetic in-proc) |
| Launcher display (hotkey → window visible) | ≤ 50 ms | hook timestamp → `Activated` |
| Cold start until search is possible | ≤ 1.5 s | `Seekbeam.Bench startup` |
| Idle private RAM (2 M entries indexed) | ≤ 150 MB | `PrivateMemorySize64` |
| Private RAM during Balanced indexing | ≤ 300 MB (Seekbeam) + ≤ 512 MB (host, bounded by the Job) | same |
| Private RAM growth 1 M → 2 M | < 15 % | comparison |
| Initial name scan (SSD, cold cache) | ≥ 20,000 entries/s in Performance | `Seekbeam.Bench index` |
| Name Index size | ≤ 120 bytes/entry (catalog + segments) | file sizes |
| Idle CPU | ≈ 0 % (no active polling, probes every 2 s) | counters |
| Incremental update (event → searchable) | ≤ 3 s | watcher test |

## Datasets (`tools/Seekbeam.DatasetGen`, fixed seed ⇒ reproducible)

- `synthetic N`: names generated in memory (FR/EN vocabulary, dates, accents, Unicode, duplicates, varied extensions, depth 1–15) injected directly into the catalog, for 100 k, 500 k, 1 M, 2 M and 5 M entries.
- `disk N`: real tree on disk, with empty files and a fraction of text, DOCX, PDF and ZIP files, deep paths, Unicode names, duplicates, junctions (cycles), excluded folders and malformed files. Sizes: 100 k, 1 M, 2 M (depending on disk space).

## Scenarios

1. Launcher queries: 40 representative queries (1 to 4 tokens, short and long, with and without results, accents, typos), 20 repetitions, p50/p95/p99.
2. Simulated typing: successive prefixes of a query (cancellation and reuse).
3. Search during indexing: latency during a Performance scan.
4. Initial disk indexing: throughput, CPU, peak RAM, I/O.
5. Recovery: forced stop at 50 % of the scan, restart, recovery time, no duplicates.
6. Burst: 10,000 creations or deletions in 5 s.
7. Content: extraction throughput per format, host RAM, CPU cap respected.
8. Stability: 30 min of indexing + searches in a loop; stable RAM.

## Success criteria

All targets above are met, or the gap is documented with its cause and a plan. Dated results are recorded in `docs/BENCHMARKS.md`.
