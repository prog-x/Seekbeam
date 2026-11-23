[English](ARCHITECTURE.md) | **Français**

# Seekbeam — Architecture technique

Version 1.0 — 2026-11-21. Les décisions détaillées et leurs alternatives sont dans [TECHNICAL_DECISIONS.md](TECHNICAL_DECISIONS.fr.md).

## 1. Vue d'ensemble

```
┌──────────────────────────── Seekbeam.exe (WPF, processus utilisateur, non élevé) ────────────────────────────┐
│ UI : Launcher · DeepSearch · Settings · Dashboard · Tray        Hotkeys (WH_KEYBOARD_LL + RegisterHotKey)   │
│        │  (ISearchService / IIndexControl / IStatsProvider)                                                │
│ ───────┼──────────────────────────────────────── Seekbeam.Core ───────────────────────────────────────────── │
│ SearchEngine ─► NameIndex (segments mmap + delta)  ─┐      ContentSearch ─► content.db (FTS5)             │
│      │       └► AppIndex (mémoire, petit)          │                                                      │
│ RankingEngine ◄ UsageStore (historique local)       │                                                      │
│ IndexingCoordinator ─► NameQueue ─► DirectoryScanner ─► Catalog (catalog.db, SQLite WAL)                  │
│      │                 ContentQueue ─► ContentIndexer ─► ExtractorClient ══ pipe ══╗                       │
│ FileWatcher · Reconciler · OpportunisticDiscovery · ResourceManager · Logging      ║                       │
│ SourceFileAccess (LECTURE SEULE)          SeekbeamStorage (ÉCRITURE, %LOCALAPPDATA%\Seekbeam uniquement)     │
└────────────────────────────────────────────────────────────────────────────────────╫──────────────────────┘
                                   Seekbeam.ExtractorHost.exe (bac à sable AppContainer + Job Object : RAM/CPU bornés, priorité basse)
                                   Extracteurs : texte, HTML/XML, OOXML, ODF, RTF, PDF, DOC/XLS/PPT, ZIP
```

Un seul processus résident (UI + moteur) et un processus enfant d'extraction. Aucun service Windows, aucun pilote, aucun privilège administrateur requis.

## 2. Projets

| Projet | Type | Rôle |
|---|---|---|
| `src/Seekbeam.Core` | bibliothèque `net10.0-windows` | moteur complet, sans dépendance UI |
| `src/Seekbeam.App` | WPF `WinExe` | launcher, fenêtres, tray, raccourcis, intégration shell |
| `src/Seekbeam.ExtractorHost` | console (sans fenêtre) | extraction de texte isolée : bac à sable AppContainer, bornée par Job Object |
| `tests/Seekbeam.Core.Tests` | xUnit | unitaires + intégration |
| `tests/Seekbeam.Immutability.Tests` | xUnit | intégrité SHA-256/métadonnées, analyse IL statique |
| `tools/Seekbeam.DatasetGen` | console | générateur de jeux de données reproductibles |
| `tools/Seekbeam.Bench` | console | benchmarks (recherche, indexation, RAM, CPU, I/O) |
| `tools/Seekbeam.FsAudit` | console | moniteur indépendant des changements filesystem (audit dynamique) |
| `installer/` | WiX v6 | MSI par utilisateur + archive portable |

## 3. Modules de Seekbeam.Core

| Namespace | Composant | Responsabilité |
|---|---|---|
| `SourceAccess` | `SourceFileAccess`, `DirectoryEnumerator`, `NativeFile` | **seul** point d'accès aux fichiers indexés : ouverture lecture seule, métadonnées, énumération (`NtQueryDirectoryFile`), identité volume/FileId |
| `Storage` | `SeekbeamStorage`, `DataPaths`, `MappedSegmentFile` | **seul** point d'écriture : DB, segments, config, logs, temp ; validation de chaque chemin |
| `Config` | `SeekbeamConfig`, `ConfigStore` | JSON validé, versionné, migré ; écriture atomique |
| `Text` | `Normalizer`, `QueryParser` | pliage casse/accents → UTF-8, tokens, opérateurs |
| `Catalog` | `CatalogDb`, `EntryStore`, `QueueStore` | SQLite : entrées, racines, files persistantes, tombstones, historique |
| `NameIndex` | `Segment`, `DeltaBuffer`, `NameIndexManager`, `SegmentMerger` | index de recherche de noms dérivé du catalogue (LSM 2 niveaux) |
| `Search` | `SearchEngine`, `NameMatcher`, `FuzzyMatcher`, `Ranking` | recherche interactive, annulation, top-K |
| `Indexing` | `IndexingCoordinator`, `DirectoryScanner`, `ScopeRules`, `LinkPolicy` | parcours en streaming, diff par répertoire, priorités |
| `Watching` | `RootWatcher`, `ChangeCoalescer`, `Reconciler` | ReadDirectoryChangesW, coalescence, réconciliation |
| `Discovery` | `OpportunisticDiscovery`, `RecentItemsMonitor` | découverte via « Éléments récents » et ouvertures Seekbeam |
| `Content` | `ContentDb`, `ContentIndexer`, `ContentSearch`, `Snippets` | FTS5, file de contenu, extraits |
| `Extraction` | `IContentExtractor` + extracteurs, `ExtractorClient`, `ExtractorProtocol` | formats, limites, isolation |
| `Apps` | `AppIndex`, `LnkParser`, `AppsFolderEnumerator`, `RunCommands` | menu Démarrer, bureau, UWP/MSIX, .exe configurés, commandes Win+R, alias utilisateur |
| `Resources` | `ResourceManager`, `SystemProbe`, `Throttle` | modes, adaptation, pause, débit |
| `Diagnostics` | `Log`, `Stats`, `ErrorJournal` | logs locaux tournants, compteurs, erreurs bornées |

L'UI ne connaît que les façades `SeekbeamEngine` (`Search`, `Index`, `Stats`, `Config`).

## 4. Stockage

Emplacement : `%LOCALAPPDATA%\Seekbeam\` (ou `.\Data\` en mode portable si `portable.txt` est à côté de l'exécutable).

```
config.json            configuration (écriture atomique tmp + replace)
db\catalog.db          SQLite WAL : roots, entries, name_queue, content_queue, tombstones, apps, usage, errors, meta
db\content.db          SQLite WAL : content_fts (FTS5 contentless), docs (texte compressé pour extraits)
index\names-*.seg      segments immuables du Name Index (mmap lecture seule)
index\manifest.json    liste des segments + révision couverte
logs\seekbeam-*.log      logs tournants (5 × 5 Mo)
tmp\                   temporaires (SQLITE_TMPDIR), purgés au démarrage
```

Ce répertoire est **automatiquement exclu** de toutes les racines (il n'est jamais une source protégée).

### 4.1 Catalogue (`catalog.db`)

```sql
entries(id INTEGER PRIMARY KEY, parent_id INT, root_id INT, name TEXT, kind INT, size INT, mtime INT,
        ctime INT, attrs INT, file_id INT, name_rev INT, flags INT)
  UNIQUE(parent_id, name)          -- recherche par chemin, diff par répertoire
  INDEX(name_rev)                  -- reconstruction du delta du Name Index
name_tombstones(id INT, rev INT)   -- suppressions non encore fusionnées dans les segments
name_queue(id PK, path UNIQUE, root_id, priority, mode, depth, attempts, not_before)
content_queue(entry_id PK, priority, attempts, not_before, reason)
depth_limited(dir_id PK, root_id, hidden)  -- dossiers à la profondeur maximale de leur racine dont le contenu n'est pas exploré (schéma v2)
roots, apps, usage(path PK, launches, last_used, score), favorites, query_history, errors, meta
```

Le chemin n'est pas stocké par fichier : il est reconstruit via `parent_id` (cache LRU des chemins de répertoires). Gain : ~60 % de taille.

`name_rev` est un compteur global monotone attribué à chaque insertion ou changement de nom/parent/drapeaux. Il permet de dériver de façon déterministe l'état du Name Index après un crash.

### 4.2 Name Index (segments + delta)

- **Segment** : fichier immuable, trié par `id` : `ids[] int32`, `parents[] int32`, `flags[] byte`, `offsets[] int32`, `text` (noms normalisés UTF-8 séparés par `\n`). Ouvert en mmap lecture seule : la mémoire utilisée est du cache de pages partageable et récupérable par l'OS, pas de la mémoire privée.
- **Delta** : enregistrements récents (`name_rev > révision couverte`), en mémoire, par blocs immuables de 4 096 entrées publiés atomiquement ; plafonné (≤ 256 k entrées), puis vidé dans un segment.
- **Staleness** : bitset par segment (1 bit/enregistrement) marquant les copies remplacées (id présent dans un niveau plus récent) ou supprimées (tombstone de révision postérieure).
- **Fusion** : niveaux L0 (base) + L1 ; le delta est fusionné dans L1 ; L1 est fusionné dans la base quand L1 > base/4 ou tombstones > base/8. Fusion en k-voies par id, en streaming, écriture séquentielle, bascule atomique du manifeste, suppression différée (comptage de références des lecteurs).
- **Reprise** : au démarrage, manifeste → segments ; delta = `SELECT … WHERE name_rev > R` ; tombstones = table. Aucun état volatile n'est nécessaire à la cohérence.

### 4.3 Content Index (`content.db`)

- `content_fts` : FTS5 `contentless` (`content=''`, `contentless_delete=1`), tokenizer `unicode61 remove_diacritics 2`, `rowid = entry_id`.
- `docs(entry_id PK, mtime, size, extractor, version, status, chars, text BLOB deflate)` : texte compressé (plafonné, 1 Mo de caractères par défaut) pour les extraits et l'aperçu, sans relire la source.
- Cycle de vie indépendant du catalogue : suppression ou reconstruction possibles sans toucher au Name Index.

## 5. Recherche

### 5.1 Normalisation
`Normalizer` : minuscules invariantes, décomposition NFD puis suppression des diacritiques, ligatures (ß→ss, æ→ae, œ→oe, ø→o, ł→l…), encodage UTF-8. La même fonction sert à l'index et aux requêtes, ce qui rend la recherche insensible à la casse et aux accents par construction. UTF-8 est auto-synchronisant, donc une recherche d'octets ne produit pas de faux positifs à cheval sur deux caractères.

### 5.2 Requête launcher
Tokens séparés par des espaces, dans n'importe quel ordre, en sous-chaîne. Préfixes : `ext:pdf,docx`, `kind:file|folder|app`, `-exclu`, `"phrase exacte"`, `in:chemin`.

### 5.3 Algorithme (par segment, en parallèle par blocs de 64 k enregistrements)
1. Pour chaque token, balayage vectorisé (`Span<byte>.IndexOf`) du texte du bloc → masque de bits par enregistrement.
2. Masque complet → correspondance sur le nom. Masque partiel → si un token manquant n'apparaît dans aucun nom de dossier (test préalable sur les sidecars), l'enregistrement est rejeté immédiatement ; sinon les tokens manquants sont cherchés dans les **noms des répertoires ancêtres** (masque mémoïsé par répertoire, remontée via `parent`). Au moins un token doit correspondre au nom.
3. Filtres (`ext`, `kind`, exclusions, cachés) puis score (§6) et tas top-K par thread.
4. Fusion des tas, puis hydratation des K résultats depuis SQLite (nom original, chemin, taille, date).
5. Si le nombre de résultats est inférieur au seuil : passe **fuzzy** (bitap de Wu-Manber étendu aux transpositions, 1 erreur pour les tokens ≥ 4 caractères, 2 pour ≥ 8 ; présélection par pigeonhole ; jamais sur un token contenant des chiffres), avec un score pénalisé.
6. Un token d'un seul caractère ne correspond qu'en début de mot.

Chaque requête porte un `CancellationToken` vérifié entre les blocs. La requête N+1 annule la requête N. Les workers d'indexation cèdent pendant une recherche (`ResourceManager.SearchActive`).

### 5.4 Recherche approfondie
Fusion de `NameIndex` (noms et chemins) et de `ContentSearch` (FTS5 MATCH, BM25), avec filtres de type, racine, date et taille. Syntaxe : `AND`/`OR`/`NOT`/`-`, `"phrase"`, `préfixe*`, `NEAR`. Extraits depuis `docs.text`. Aperçu texte avec surlignage.

## 6. Ranking (`Search/Ranking.cs`)

Score additif documenté et testé (`RankingTests`, jeux requête → résultat attendu) :

| Signal | Poids (défaut) |
|---|---:|
| nom exact (sans extension) = requête | +1000 |
| nom commence par le 1er token | +300 |
| token en début de mot (après `_ - . espace`, transition casse/chiffre) | +120 / token |
| token en sous-chaîne simple | +40 / token |
| tous les tokens dans le nom (vs dans le chemin) | +200 |
| tokens dans l'ordre de la requête | +60 |
| couverture du nom (longueur des tokens / longueur du nom) | 0..+150 |
| application (raccourci, UWP) | +250 |
| dossier | +20 |
| usage : `min(400, 80·log2(1+lancements))` × (0,35 + 0,65 × décroissance de récence, demi-vie 14 j) | 0..+400 |
| favori | +400 |
| fuzzy | −250 / erreur |
| profondeur du chemin | −5 / niveau |

L'usage est plafonné en dessous de l'écart entre une correspondance exacte et une correspondance partielle : l'historique ne peut pas écraser une correspondance textuelle nettement meilleure.

## 7. Indexation

### 7.1 Files persistantes
- `name_queue` : répertoires à lister (priorités : 0 événement watcher, 1 découverte opportuniste, 2 scan initial, 3 réconciliation). Déduplication par chemin (`UNIQUE`). Ordre FIFO par priorité, ce qui donne un parcours en largeur : les niveaux supérieurs sont indexés d'abord.
- `content_queue` : entrées à extraire, déduplication par `entry_id`, `attempts` + `not_before` (backoff exponentiel, 3 tentatives max, puis statut `failed` visible au tableau de bord). Quand l'emplacement d'un fichier est inaccessible (lecteur réseau ou disque déconnecté), tous les fichiers en file de cet emplacement sont reportés de 5 minutes en une seule requête, sans compter de tentative, au lieu d'être abandonnés. Le recensement du contenu remet en file les fichiers en attente qu'il trouve hors de la file (au plus une fois par heure), pour que rien ne reste en attente indéfiniment.

### 7.2 DirectoryScanner (diff par répertoire)
Pour un répertoire : énumération en streaming (`NtQueryDirectoryFile`, `FileIdFullDirectoryInformation`, tampon 64 Ko) → comparaison avec les enfants connus (`SELECT … WHERE parent_id=?`) → insertions, mises à jour, suppressions (sous-arbres supprimés par lots de 5 000), mise en file des sous-répertoires et des fichiers éligibles au contenu. Plusieurs petits répertoires sont regroupés dans une transaction courte (≤ 2 000 lignes, ≤ 100 ms). Le même mécanisme sert au scan initial, au rescan sur événement et à la réconciliation.

### 7.3 Périmètre et liens
`ScopeRules` : racines, exclusions (motifs glob sur nom/chemin, extensions), profondeur maximale, cachés/système, répertoire de données Seekbeam toujours exclu.
`LinkPolicy` : les points de reparse de type symlink, junction ou mount point **ne sont pas suivis par défaut** ; s'ils le sont (option par racine), on vérifie (VolumeSerial, FileId) dans un ensemble de visites, ainsi qu'une profondeur maximale (64) et un budget de traversée. Les répertoires cloud (OneDrive, `IO_REPARSE_TAG_CLOUD*`) sont parcourus normalement : ce sont de vrais répertoires.

### 7.4 Surveillance
`RootWatcher` : un `ReadDirectoryChangesW` récursif par racine (via `FileSystemWatcher`, tampon 64 Ko). Les événements passent par `ChangeCoalescer` (fenêtre de 1,5 s, déduplication par répertoire, détection de rafale > 2 000 événements/s qui bascule sur un rescan différé). Un renommage connu met à jour l'entrée en place (id conservé). Les autres événements provoquent un rescan superficiel du répertoire parent. Un débordement déclenche une réconciliation de la racine.
`Reconciler` : au démarrage (événements manqués hors exécution), puis périodiquement (24 h par défaut), en priorité basse.

### 7.5 Découverte opportuniste
Sources non intrusives : (a) le dossier « Éléments récents » de l'utilisateur (`%APPDATA%\Microsoft\Windows\Recent`, `.lnk` créés par le shell à chaque ouverture via l'Explorateur ou la boîte de dialogue standard), surveillé et analysé par notre parseur `.lnk` en lecture seule ; (b) les fichiers ouverts via Seekbeam. Algorithme : chemin → déjà indexé ? → dans une racine ? → exclu ? → répertoire parent connu ? Sinon, le répertoire est mis en file (priorité 1), ses sous-répertoires sont découverts selon les règles et les fichiers éligibles sont mis en file de contenu. Budget de découverte borné : 1 000 répertoires par événement et par heure.
Non retenu : hooks d'API, ETW noyau ou pilote minifiltre, jugés intrusifs ou demandant des droits administrateur (voir TD-11).

## 8. Extraction (processus isolé)

`ExtractorClient` lance `Seekbeam.ExtractorHost.exe` dans un **Job Object** (`JOB_OBJECT_LIMIT_PROCESS_MEMORY`, `CPU_RATE_CONTROL` en hard cap, `KILL_ON_JOB_CLOSE`), en priorité *Below Normal* et en mode arrière-plan I/O. Le protocole est binaire, longueur préfixée, sur stdin/stdout. Un délai d'expiration par fichier entraîne la mise à mort de l'hôte, son redémarrage et le marquage du fichier comme échoué. Un crash d'extracteur (stack overflow, OOM) ne peut donc pas affecter Seekbeam.

**Bac à sable (TD-17).** `SandboxLauncher` crée l'hôte suspendu, dans un **AppContainer** sans capacité (sinon avec un jeton restreint en intégrité faible), l'affecte au Job Object (qui n'autorise en outre qu'un seul processus, sans presse-papiers ni bureau), puis le relance. Seuls les deux tubes stdio sont hérités, et les protections contre l'exploitation sont fixées à la création. L'hôte ne reçoit jamais de chemin : `ExtractorClient` ouvre la source via `SourceFileAccess` et duplique dans l'hôte un handle `FILE_GENERIC_READ`, dont l'hôte vérifie les droits avant de lire. Au démarrage, l'hôte envoie un message d'accueil avec l'isolation qu'il constate sur son propre jeton ; en cas d'écart, `ExtractorClient` essaie le niveau suivant. `SandboxProvisioning` (Storage) crée une fois le profil AppContainer et l'ACE de lecture sur le dossier du programme, jamais dans le périmètre d'indexation.

Extracteurs (`IContentExtractor` : `Id`, `Version`, `Extensions`, `Extract(Stream, TextSink, Limits, CancellationToken)`) :

| Extracteur | Formats | Implémentation |
|---|---|---|
| PlainText | txt md csv tsv log ini cfg conf toml yaml yml json sql css js ts py cs java c cpp h go rs ps1 sh bat… | détection BOM/UTF-8/Windows-1252, lecture bornée |
| Markup | html htm xhtml xml svg | tokenizer maison, entités, script/style ignorés, sans DTD |
| OOXML | docx xlsx pptx (+ m/x) | `ZipArchive` + `XmlReader` (DtdProcessing.Prohibit) |
| ODF | odt ods odp | `content.xml` + `meta.xml` |
| RTF | rtf | parseur maison (groupes, `\uN`, `\'hh`, destinations ignorées) |
| PDF | pdf | PdfPig (Apache-2.0), pages bornées |
| Legacy Office | doc xls ppt | lecteur CFB maison + piece table Word / BIFF8 SST / atomes PPT |
| Zip | zip | entrées traitées en mémoire, bornées (voir §8.1) |

### 8.1 Archives
Jamais d'extraction sur disque. Limites : 2 niveaux d'imbrication, 10 000 entrées, 50 Mo décompressés par entrée, 200 Mo par archive, ratio de compression ≤ 100, noms d'entrée normalisés (traversée `..` rejetée), entrées chiffrées ignorées et signalées.

## 9. Gestion des ressources

`ResourceManager` publie un `ResourceBudget` toutes les 2 s :

| Mode | Scan noms | Workers contenu | CPU hôte (Job) | RAM hôte | Débit lecture contenu |
|---|---|---|---|---|---|
| Économie | 2 000 entrées/s | 1 | 10 % | 256 Mo | 4 Mo/s |
| Équilibré | 20 000 entrées/s | 1 | 25 % | 512 Mo | 20 Mo/s |
| Performance | illimité | 2–4 | 60 % | 1 Go | illimité |
| Automatique (défaut) | ajusté | ajusté | ajusté | 512 Mo | ajusté |

Signaux du mode Auto : CPU système (`GetSystemTimes`), mémoire (`GlobalMemoryStatusEx`), inactivité utilisateur (`GetLastInputInfo`), batterie (`GetSystemPowerStatus`), application plein écran ou présentation (`SHQueryUserNotificationState`), recherche en cours. Les threads d'indexation sont en `THREAD_MODE_BACKGROUND_BEGIN` (priorité CPU, I/O et mémoire très basses, gérées par l'ordonnanceur Windows). La pause et la reprise sont globales ou par file ; des plages horaires sont optionnelles.

## 10. Intégration Windows (Seekbeam.App)

- **Raccourci** : hook clavier bas niveau `WH_KEYBOARD_LL` sur un thread dédié avec sa propre boucle de messages (aucune latence UI possible). Machine à états double Ctrl : deux appuis-relâchés de Ctrl seul, ≤ 400 ms, sans autre touche intercalée, Ctrl gauche, droit ou les deux. Les combinaisons personnalisées passent par `RegisterHotKey`. Le hook ne consomme jamais l'événement (`CallNextHookEx` systématique).
- **Fenêtre** : WPF sans bordure, coins arrondis DWM, pré-créée et masquée au démarrage (affichage < 50 ms), positionnée au centre de l'écran actif ou près du curseur.
- **Actions** : `ShellExecuteEx` (open, runas, openas, properties), `SHOpenFolderAndSelectItems`, presse-papiers. Aucune action ne modifie un fichier.
- **Applications** : menu Démarrer et bureau (`.lnk` analysés par notre parseur), `shell:AppsFolder` (IShellItem, AUMID UWP/MSIX), `.exe` des dossiers configurés.
- **Commandes Exécuter et alias** (TD-19) : fichiers des dossiers du PATH (.com/.exe/.bat/.cmd/.msc/.cpl, sans récursion, PATH lu dans le registre) et enregistrements App Paths, réexaminés toutes les 15 minutes (≈ 900 entrées, 10 à 30 ms) ; alias de l'utilisateur issus de la configuration. Les commandes ne correspondent que par leur nom exact ou un préfixe d'au moins 3 lettres. « commande arguments » propose la commande avec ces arguments.
- **Tray** : `NotifyIcon` (menu : rechercher, recherche avancée, tableau de bord, pause, paramètres, quitter).
- **Démarrage auto** : `HKCU\…\Run`. Instance unique : mutex nommé + événement « show ».

## 11. Garantie d'immutabilité

Voir [docs/FILE_IMMUTABILITY_POLICY.md](docs/FILE_IMMUTABILITY_POLICY.fr.md). En résumé : séparation `SourceFileAccess` (lecture seule) / `SeekbeamStorage` (écritures confinées), `BannedApiAnalyzers` à la compilation, analyse IL de tous les assemblies dans les tests, tests d'intégrité SHA-256 et métadonnées sur tous les scénarios, audit dynamique indépendant (`Seekbeam.FsAudit` et script ETW optionnel pour administrateur).

## 12. Interfaces principales

```csharp
public interface ISearchService {
    Task<SearchResultSet> SearchAsync(SearchRequest request, CancellationToken ct);      // launcher
    Task<DeepSearchResultSet> DeepSearchAsync(DeepSearchRequest request, CancellationToken ct);
}
public interface IIndexControl {
    void Pause(WorkClass? cls = null); void Resume(WorkClass? cls = null);
    void ReindexNames(long? rootId = null); void ReindexContent(long? rootId = null);
    void RecordOpen(string path);  // découverte opportuniste + apprentissage
}
public interface IStatsProvider { EngineStats Snapshot(); }
public interface IContentExtractor {
    string Id { get; } int Version { get; } IReadOnlyCollection<string> Extensions { get; }
    void Extract(Stream source, TextSink sink, ExtractionLimits limits, CancellationToken ct);
}
```
