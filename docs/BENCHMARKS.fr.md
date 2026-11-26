[English](BENCHMARKS.md) | **Français**

# Seekbeam — Benchmarks

**Machine** : VM Hyper-V, Intel i9-14900K (16 vCPU), **6,5 Go de RAM**, SSD virtuel NTFS, Windows 11 Pro, .NET 10.0.15, build Release (sans ReadyToRun pour les outils de bench). Mesures du 2026-11-21. Les VM présentent du bruit (±30 % sur les percentiles élevés).

Reproduction :
```powershell
dotnet run -c Release --project tools/Seekbeam.Bench -- synthetic <dataDir> <N>                  # catalogue synthétique + recherche
dotnet run -c Release --project tools/Seekbeam.DatasetGen -- tree C:\SeekbeamBench\disk1m 1000000  # arborescence réelle (graine 42)
dotnet run -c Release --project tools/Seekbeam.Bench -- index <dataDir> C:\SeekbeamBench\disk1m Performance [content]
dotnet run -c Release --project tools/Seekbeam.Bench -- search <dataDir>
dotnet run -c Release --project tools/Seekbeam.Bench -- coverage <dataDir> 1200000          # répartition du contenu (tableau de bord)
```
Logs bruts : `artifacts/bench/*.log`.

## 1. Choix du moteur (spike)
Voir [2026-11-21_spike_name_engine.md](benchmarks/2026-11-21_spike_name_engine.fr.md) : le scan vectorisé est 2 à 10 fois plus rapide que FTS5 trigram dans le pire cas, 4,4 fois plus compact, et gère sous-chaînes courtes et fuzzy.

## 2. Indexation réelle — 1 000 000 de fichiers sur disque
Dataset : 1 000 000 d'entrées (64 302 dossiers, profondeur ≤ 12, noms FR/EN accentués et Unicode, 48 Mo de contenu texte). Le cache était chaud (dataset fraîchement créé).

| Mesure | Noms seuls (Performance) | Noms + contenu (Performance) |
|---|---|---|
| Durée totale | **36,8 s** (27 159 entrées/s) | 64,8 s (6 768 documents texte extraits) |
| CPU (processus Seekbeam) | 19,5 s, soit **0,53 cœur** (3,3 % de la machine) | 31,6 s (0,49 cœur) + hôtes d'extraction plafonnés |
| RAM privée pic / fin | **199 Mo / 109 Mo** | 185 Mo / 102 Mo |
| Working set pic | 266 Mo | 257 Mo |
| I/O processus (lu / écrit) | 535 Mo / 654 Mo (écritures : dossier de données uniquement) | 1 181 Mo / 1 306 Mo |
| Recherche pendant l'indexation | **p50 3,8 ms, p95 15,5 ms**, max 30,9 ms | p50 5,3 ms, p95 10,6 ms |
| Taille du catalogue + segments | 162,6 + 38,1 Mo = 210 octets/entrée | 214 octets/entrée ; contenu 52 Mo |

Correctif issu de ce benchmark : la première mesure « noms + contenu » prenait 290 s et lisait 139 Go, à cause d'un `COUNT(*)` de la file dans le chemin chaud et de la mise en file des fichiers vides. Après correction : 64,8 s et 1,2 Go.

### Modes de ressources (sous-arbre de 119 074 entrées, noms seuls)
| Mode | Débit | CPU |
|---|---|---|
| Économie | **2 002 entrées/s** (plafond 2 000 respecté) | 0,05 cœur |
| Équilibré | 8 207 entrées/s* | 0,17 cœur |
| Performance | 6 990 entrées/s* | 0,15 cœur |

\* mesuré pendant qu'un autre benchmark saturait le disque : le débit est limité par les I/O, pas par le plafond du mode.

## 3. Recherche launcher (top 8, 40 requêtes × 20 répétitions, dont fautes, filtres et Unicode)
| Index | Chargement | p50 | p95 | p99 | Frappe successive p95 | RAM privée après |
|---|---|---|---|---|---|---|
| 1 M **réel** (disque) | 70 ms | **2,7 ms** | **7,6 ms** | 25,8 ms | 8,0 ms | 107 Mo |
| 1 M synthétique | 74 ms | 3,6 ms | 10,4 ms | 14,4 ms | 20,1 ms | 38–110 Mo |
| 2 M synthétique | 68 ms | 6,2 ms | 20,0 ms | 30,4 ms | 46 ms | 65 Mo |
| 5 M synthétique | 21 ms | 10,7 ms | **51,3 ms** | 57,6 ms | 139 ms | 195 Mo (working set 423 Mo, dont pages mappées) |

Détail à 5 M (ms) : `ant 202611 cahier` 10,5 · `rapport` 5,7 · `doc` (490k correspondances) 7,4 · `a` (1,1 M) 18 · `facture 2024` 38,9 (pire cas : remontées d'ancêtres). Catalogue 711 Mo + segments 206 Mo ; peuplement à 10 457 entrées/s (48 fusions).

**Limite observée** : à 5 M, les premiers préfixes très courts d'une frappe (« c », « ca ») coûtent jusqu'à 140 ms, car le scan complet est répété à chaque frappe. Piste : affinage incrémental (réutiliser les candidats de la requête précédente quand la nouvelle la prolonge).

Détail sur 1 M réel (ms) : `ant 202611 cahier` 4,8 · `rapport` 2,5 · `doc` (97k correspondances) 6,7 · `a` (220k) 6,8 · `facture 2024` 25,5 (le token `2024` apparaît dans des milliers de noms de dossiers, d'où des remontées d'ancêtres) · `surveillance data` 7,2 · `rapprot`, `facutre`, `survellance`, `presentaton` (fautes, passe fuzzy) 2–3 · `ext:pdf rapport` 1,6 · `kind:folder projets` 0,5.

### Historique d'optimisation (1 M synthétique)
| Étape | `a` | p95 global | p99 global |
|---|---|---|---|
| Version initiale | 88 ms | 14,7 ms | 87,6 ms |
| + favoris sans verrou, spans hissés, 1 caractère en début de mot | 5,7 ms | 9,2 ms | 18,4 ms |
| + sidecar des dossiers (faisabilité des tokens de chemin) | 6–12 ms | 9,5–10,4 ms | 14,4–20,6 ms |
| `ant 202611 cahier` : 18 ms → **3,5–4,8 ms** grâce au sidecar | | | |

## 4. Application réelle (Seekbeam.exe, index de 1 M chargé)
| Mesure | Valeur |
|---|---|
| Lancement → index prêt | **~0,21 s** (Name Index chargé en 17 ms) |
| RAM privée au repos (sans index, profil vide) | 63–69 Mo (working set ~140 Mo) |
| RAM privée 60 s après démarrage (1 M entrées, réconciliation en cours) | **66 Mo** (working set 143 Mo) |
| Recherche affichée dans le launcher, pendant la réconciliation de démarrage du million | 15–62 ms (build non R2R, première requête avec JIT) |
| Double Ctrl → fenêtre visible (mesure UIA incluant 2 appuis simulés et le polling) | 369 ms (borne haute ; la fenêtre est pré-créée) |

### Répartition du contenu (tableau de bord, `ContentCoverage`)
Recensement en lecture seule de l'état du contenu de chaque fichier (jointure par fusion catalogue / file / content.db, lots de 10 000 paginés par clé). Catalogue synthétique de 1,2 M entrées (**1 121 068 fichiers**), content.db avec 708 348 documents (indexés, sans texte, échecs), un dossier de premier niveau exclu du contenu. 3 exécutions consécutives, log : `artifacts/bench/coverage1m.log`.

| Mesure | Valeur |
|---|---|
| Durée (médiane de 3) | **1,63–1,68 s** (≈ 670 000 fichiers/s) |
| RAM privée en plus pendant le calcul | **+4 à +12 Mo** à chaud ; +34 Mo au premier passage (JIT, cache SQLite) |
| Lecture de fichiers sources | aucune (catalogue et content.db uniquement) |

La mémoire ne dépend pas du nombre de fichiers : seulement d'un lot de 10 000 lignes, du cache des dossiers parents (uniquement s'il y a des dossiers exclus) et de l'histogramme des extensions (plafonné à 4 096 entrées). En production, le calcul s'exécute en priorité basse et cède le pas aux recherches entre deux lots.

## 5. Bilan par rapport au PERFORMANCE_PLAN
| Cible | Résultat | Statut |
|---|---|---|
| p50 / p95 / p99 ≤ 5 / 15 / 30 ms à 1 M | 2,7 / 7,6 / 25,8 ms (réel) | ✅ |
| p95 ≤ 30 ms à 2 M | 20,0 ms | ✅ |
| p95 ≤ 60 ms à 5 M | 51,3 ms | ✅ (frappe de préfixes très courts : 139 ms, ❌ voir §3) |
| Démarrage ≤ 1,5 s | ~0,21 s | ✅ |
| RAM privée au repos ≤ 150 Mo | 63–69 Mo (66 Mo avec 1 M entrées) | ✅ |
| RAM en indexation ≤ 300 Mo | pic 199 Mo | ✅ |
| Croissance RAM 1 M → 2 M < 15 % | 107 → 65 Mo privés après recherche (pas de croissance linéaire ; 195 Mo à 5 M, surtout du pool GC) | ✅ |
| Scan ≥ 20 000 entrées/s (Performance) | 27 159 entrées/s | ✅ |
| ≤ 120 octets/entrée | 210 octets/entrée | ❌ (catalogue SQLite et index secondaires ; piste dans PROJECT_STATUS) |
| Événement → recherchable ≤ 3 s | coalescence 1,5 s + scan ; vérifié en test (≤ 10 s d'attente, typiquement < 2 s) | ✅ |

## 6. Index des dossiers et « aller au dossier » (2026-11-26, TD-20/TD-21)

Même VM (i9-14900K, 16 vCPU, 9,3 Go de RAM lors de cette mesure), build Release. L'index des dossiers a été mesuré sur le **vrai disque C:** du poste de développement, avec les exclusions par défaut (aucun emplacement indexé, pour que tout passe par l'index des dossiers).

```powershell
dotnet run -c Release --project tools/Seekbeam.Bench -- folderindex <dataDir> [Performance|Auto]
```

| Mesure | Mode Performance | Mode Automatique (par défaut) |
|---|---|---|
| Dossiers parcourus (C:, profondeur 16, Windows/caches/dossiers de build exclus) | **93 816** | 93 816 |
| Durée du parcours | **12,0 s** (7 845 dossiers/s) | 54,9 s (1 708 dossiers/s : le gestionnaire de ressources le régule) |
| CPU | 8,8 s, **0,74 cœur** | 18,9 s, **0,34 cœur** |
| RAM privée pic / fin | 154 / 141 Mo | **93 / 93 Mo** |
| Catalogue + index des noms | **19,8 + 3,6 Mo** (≈ 250 octets/dossier) | idem |
| Recherche « aller au dossier » pendant le parcours | p50 0,7 ms, p95 5,9 ms, max 30,9 ms | p50 0,5 ms, **p95 1,0 ms**, max 25,1 ms |
| Recherche « aller au dossier » ensuite (20 requêtes × 20, top 8) | p50 0,3 ms, **p95 1,0 ms**, p99 1,1 ms | p50 0,3 ms, p95 1,3 ms, p99 1,7 ms |

Exemples sur ce disque : `prog fil` → `C:\Program Files` ; `prog\micro` → `C:\Program Files (x86)\Microsoft` (ce cas a conduit au signal `LeafPrefix` et à une pénalité « classé plus bas » plus douce, voir TD-21).

**Recherche du launcher, aucune régression** (1 M synthétique, graine 42, 40 requêtes × 20, même machine ; la version précédente (HEAD) et la nouvelle, construites et exécutées l'une après l'autre) :

| Version | p50 | p95 | p99 | Frappe successive p95 |
|---|---|---|---|---|
| Avant (HEAD), peuplement + recherche | 3,49 ms | 12,59 ms | 20,61 ms | 18,29 ms |
| Après, peuplement + recherche | 3,30 ms | 12,54 ms | 19,61 ms | 19,65 ms |
| Avant (HEAD), nouvelle recherche | 3,50 ms | 10,57 ms | 18,08 ms | 17,05 ms |
| Après, nouvelle recherche | 3,50 ms | 9,52 ms | 11,12 ms | 18,57 ms |

Les écarts restent dans le bruit de la VM : les contrôles de l'index des dossiers coûtent un test de drapeau par enregistrement, les signaux d'ordre du chemin ne sont calculés que sur la liste courte, et les chaînes `a\b` seulement quand elles sont tapées.

**Vraies boîtes de dialogue** : `tools/Seekbeam.DialogCheck` (36/36, voir [DIALOG_NAVIGATION_CHECK.fr.md](DIALOG_NAVIGATION_CHECK.fr.md)) exécute toute la chaîne, du double Ctrl au-dessus de la boîte jusqu'à la boîte qui affiche le dossier, en 1 s environ par cas, attentes de l'outil comprises ; le launcher apparaît collé sous la boîte.
