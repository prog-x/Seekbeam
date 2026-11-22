[English](PERFORMANCE_PLAN.md) | **Français**

# Seekbeam — Plan de performance

## Objectifs (cibles validées après benchmark initial, puis figées)

| Métrique | Cible | Mesure |
|---|---|---|
| Recherche launcher p50 / p95 / p99 — 1 M entrées | ≤ 5 / 15 / 30 ms | `Seekbeam.Bench search` |
| Recherche launcher p95 — 2 M entrées | ≤ 30 ms | idem |
| Recherche launcher p95 — 5 M entrées | ≤ 60 ms | idem (synthétique in-proc) |
| Affichage du launcher (hotkey → fenêtre visible) | ≤ 50 ms | horodatage hook → `Activated` |
| Démarrage à froid jusqu'à la recherche possible | ≤ 1,5 s | `Seekbeam.Bench startup` |
| RAM privée au repos (2 M entrées indexées) | ≤ 150 Mo | `PrivateMemorySize64` |
| RAM privée en indexation équilibrée | ≤ 300 Mo (Seekbeam) + ≤ 512 Mo (hôte, borné par le Job) | idem |
| Croissance RAM privée 1 M → 2 M | < 15 % | comparaison |
| Scan initial des noms (SSD, cache froid) | ≥ 20 000 entrées/s en Performance | `Seekbeam.Bench index` |
| Taille du Name Index | ≤ 120 octets/entrée (catalogue + segments) | taille des fichiers |
| CPU au repos | ≈ 0 % (aucun polling actif, sondes toutes les 2 s) | compteurs |
| Mise à jour incrémentale (événement → recherchable) | ≤ 3 s | test watcher |

## Datasets (`tools/Seekbeam.DatasetGen`, graine fixe ⇒ reproductibles)

- `synthetic N` : noms générés en mémoire (vocabulaire FR/EN, dates, accents, Unicode, doublons, extensions variées, profondeur 1–15) injectés directement dans le catalogue, pour 100 k, 500 k, 1 M, 2 M et 5 M entrées.
- `disk N` : arborescence réelle sur disque, avec des fichiers vides et une fraction de fichiers texte, DOCX, PDF et ZIP, des chemins profonds, des noms Unicode, des doublons, des junctions (cycles), des dossiers exclus et des fichiers malformés. Tailles : 100 k, 1 M, 2 M (selon l'espace disque).

## Scénarios

1. Requêtes launcher : 40 requêtes représentatives (1 à 4 tokens, courtes et longues, avec et sans résultats, accents, fautes), 20 répétitions, p50/p95/p99.
2. Frappe simulée : préfixes successifs d'une requête (annulation et réutilisation).
3. Recherche pendant l'indexation : latence pendant un scan Performance.
4. Indexation initiale disque : débit, CPU, RAM pic, I/O.
5. Reprise : arrêt forcé à 50 % du scan, redémarrage, temps de reprise, absence de doublons.
6. Rafale : 10 000 créations ou suppressions en 5 s.
7. Contenu : débit d'extraction par format, RAM de l'hôte, respect du cap CPU.
8. Stabilité : 30 min d'indexation + recherches en boucle ; RAM stable.

## Critères de réussite

Toutes les cibles ci-dessus sont atteintes, ou l'écart est documenté avec sa cause et un plan. Les résultats datés sont consignés dans `docs/BENCHMARKS.md`.
