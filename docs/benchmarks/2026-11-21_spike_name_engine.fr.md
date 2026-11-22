[English](2026-11-21_spike_name_engine.md) | **Français**

# Spike — choix du moteur du Name Index (2026-11-21)

Machine : VM Hyper-V, Intel i9-14900K (16 vCPU), 6,5 Go RAM, Windows 11 Pro, .NET 10.0.15, SQLite (SQLitePCLRaw 3.0.5).
Reproduction : `dotnet run -c Release --project bench/Seekbeam.Spike -- 2000000`

Jeu : 2 000 000 noms synthétiques (vocabulaire FR/EN, dates, numéros, extensions), longueur moyenne 34,4 caractères.

| Requête | Hits | Scan 1 thread | Scan 8 threads | FTS5 trigram (count) | LIKE '%x%' |
|---|---:|---:|---:|---:|---:|
| `ant 202611 cahier` | 19 | 8,72 ms | 3,40 ms | 8,58 ms | 216,6 ms |
| `rapport` | 87 829 | 13,38 ms | 4,81 ms | 20,26 ms | 200,9 ms |
| `doc` | 85 749 | 12,42 ms | 3,75 ms | 1,78 ms | 173,3 ms |
| `facture 2024` | 2 422 | 9,92 ms | 3,11 ms | 17,70 ms | 160,6 ms |
| `pdf` | 85 305 | 9,26 ms | 2,47 ms | 1,89 ms | 179,5 ms |
| `a` | 1 686 216 | 78,51 ms | 15,16 ms | 151,29 ms (LIKE, <3 car.) | 159,9 ms |
| `zzqx` | 0 | 4,79 ms | 1,14 ms | 0,06 ms | 149,4 ms |
| `surveillance data` | 7 217 | 13,87 ms | 2,33 ms | 33,07 ms | 200,9 ms |
| `proj 2023 final` | 88 | 9,41 ms | 3,07 ms | 12,14 ms | 184,1 ms |

| | Scan (blob) | FTS5 trigram |
|---|---|---|
| Construction | 249 ms | 1 593 ms insert + 8 745 ms index |
| Taille | 67,5 Mo | 297,8 Mo (table + index) |
| Sous-chaîne < 3 caractères | oui | non (repli LIKE) |
| Fuzzy (fautes de frappe) | oui (bitap sur le même blob) | non |
| Coût prévisible | O(octets), indépendant de la sélectivité | dépend des doclists (pire cas 150 ms) |

Remarque : les chiffres FTS5 ne comptent que les correspondances ; un classement nécessiterait en plus la lecture des noms.

**Conclusion** : Name Index = segments immuables de noms normalisés, mappés en mémoire, balayés par `Span<byte>.IndexOf` vectorisé en parallèle ; SQLite reste la source de vérité (catalogue, files, historique). FTS5 est retenu pour le Content Index (texte long, BM25, phrases, opérateurs), où il est adapté.
