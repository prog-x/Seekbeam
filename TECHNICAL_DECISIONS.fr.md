[English](TECHNICAL_DECISIONS.md) | **Français**

# Seekbeam — Décisions techniques

Format : contexte · alternatives · décision · justification · conséquences. Mettre à jour à chaque décision structurante.

---

## TD-01 — Langage et runtime : C# / .NET 10

**Contexte.** Il faut une application Windows native, des API Win32/COM nombreuses (hooks, shell, Job Objects, NtQueryDirectoryFile), des performances de scan élevées, et un outillage de test et de packaging disponible sur la machine. Le SDK .NET 10.0.404 avec WindowsDesktop est installé. Rust est présent, mais sans toolchain MSVC ni Visual Studio C++.

| Option | Avantages | Inconvénients |
|---|---|---|
| C# / .NET 10 | P/Invoke et COM simples, `Span<T>` et SIMD (`IndexOf` vectorisé), GC maîtrisable, xUnit, analyseurs Roslyn, WPF mature | runtime ~ 40–70 Mo RAM de base, JIT (atténué par ReadyToRun) |
| C++ / Win32 | empreinte minimale | pas de compilateur sur la machine, productivité et sûreté mémoire faibles pour des parseurs de formats hostiles |
| Rust | performance, sûreté mémoire | pas de linker MSVC disponible ; UI Windows native immature (pas de framework UI natif mûr) ; COM verbeux |
| Electron | — | exclu par CLAUDE.md (mémoire, démarrage) |

**Décision.** C# 14 / .NET 10, `net10.0-windows`, publication *self-contained* ReadyToRun.
**Justification.** Meilleur compromis entre intégration Windows, performance mesurée (§TD-03) et maintenabilité. Le code critique (scan) atteint ~13 Go/s par cœur grâce à l'`IndexOf` vectorisé. Les parseurs de formats non fiables bénéficient de la sûreté mémoire.

## TD-02 — UI : WPF

| Option | Avantages | Inconvénients |
|---|---|---|
| WPF | mature, rendu GPU, liste virtualisée, aucune dépendance de packaging, testable | look moins « Fluent » sans travail (corrigé via DWM : coins arrondis, ombre) |
| WinUI 3 / Windows App SDK | Fluent natif | runtime Windows App SDK à déployer, démarrage et RAM supérieurs, fenêtres sans bordure plus délicates, tooling plus fragile hors VS |
| WinForms | léger | rendu GDI, mise en forme riche (surlignage) laborieuse |

**Décision.** WPF. La fenêtre du launcher est pré-créée et masquée, ce qui rend son affichage quasi instantané. `AllowsTransparency` n'est pas utilisé (il force un rendu logiciel lent) ; on emploie `DwmSetWindowAttribute` à la place.

## TD-03 — Moteur du Name Index : segments mmap + scan vectorisé (et non FTS5)

**Contexte.** Le Name Index est la priorité absolue : sous-chaîne, ordre libre, accents, fautes de frappe, plusieurs millions d'entrées, RAM maîtrisée.
**Mesures** ([docs/benchmarks/2026-11-21_spike_name_engine.md](docs/benchmarks/2026-11-21_spike_name_engine.fr.md), 2 M noms) : scan 8 threads 1–5 ms (15 ms pour la requête `a` avec 1,7 M résultats), 67 Mo ; FTS5 trigram 2–33 ms (151 ms pour `a`), 298 Mo, 10 s de construction, pas de sous-chaîne < 3 caractères ni de fuzzy ; `LIKE` 150–220 ms.

| Option | Avantages | Inconvénients |
|---|---|---|
| Scan d'un blob mmap (style Everything) | le plus rapide, compact, coût prévisible, toute sous-chaîne, fuzzy par bitap sur le même blob | structure dérivée à maintenir (LSM) |
| SQLite FTS5 trigram | incrémental natif | 4,4× plus gros, plus lent dans le pire cas, pas de fuzzy, ≥ 3 caractères |
| Index inversé maison (n-grammes) | requêtes sélectives très rapides | taille, complexité de mise à jour, pas meilleur pour les requêtes courtes |
| Tout en mémoire (tableaux managés) | simple | RAM privée linéaire (critère 15 du cahier des charges) |

**Décision.** SQLite = source de vérité. Name Index = segments immuables mappés en mémoire (cache de pages de l'OS, récupérable, non privé) + delta mémoire borné + tombstones persistants, fusion LSM 2 niveaux, cohérence dérivée de `name_rev` (voir ARCHITECTURE §4.2).
**Conséquence.** La RAM privée par entrée se limite à 1 bit de staleness et au delta borné. Les pages mappées sont comptées dans le working set mais l'OS peut les récupérer à tout moment.

## TD-04 — Base de données : SQLite (Microsoft.Data.Sqlite + SQLitePCLRaw e_sqlite3)

Alternatives : LiteDB (moins performant, pas de FTS), RocksDB (dépendance native lourde, pas de FTS), ESENT (natif Windows, API pénible, pas de FTS), fichiers maison (risque de corruption).
**Décision.** SQLite 3.4x en mode WAL, `synchronous=NORMAL`, transactions courtes, `cache_size` borné (8 Mo catalogue, 4 Mo contenu), `temp_store=MEMORY`, `SQLITE_TMPDIR` pointé sur le répertoire temporaire de Seekbeam. Licences : domaine public, MIT et Apache-2.0. Deux bases séparées (`catalog.db`, `content.db`) donnent des cycles de vie indépendants et limitent la contention d'écriture.

## TD-05 — Content Index : SQLite FTS5 contentless + texte compressé

Alternatives : Lucene.NET (4.8 encore en bêta depuis des années, lourd), Tantivy (Rust, pas de toolchain), index inversé maison (coût de développement, risque). FTS5 offre BM25, phrases, préfixes, `NEAR` et les opérateurs booléens, et il est éprouvé.
**Décision.** `content_fts` contentless avec `contentless_delete=1`. Le texte extrait est stocké compressé (Deflate) et plafonné à 1 M caractères par document, uniquement pour les extraits et l'aperçu. Cela évite de relire la source, ce qui serait plus lent et moins sûr. L'option `content.storeText=false` désactive ce stockage : les extraits deviennent alors absents.

## TD-06 — Énumération : NtQueryDirectoryFile (FileIdFullDirectoryInformation)

Alternatives : `Directory.EnumerateFiles`/`FileInfo` (pas de FileId, allocations), `FindFirstFileExW` (pas de FileId), lecture MFT ou journal USN (`FSCTL_ENUM_USN_DATA`, très rapide mais **requiert l'administrateur**, NTFS uniquement).
**Décision.** `NtQueryDirectoryFile` sur un handle ouvert avec `FILE_LIST_DIRECTORY` seul, tampon 64 Ko réutilisé. On obtient en un appel le nom, la taille, les dates, les attributs, le FileId et le tag de reparse (champ `EaSize`), sans allocation par entrée. Le mode MFT/USN est documenté comme évolution possible pour les installations élevées.

## TD-07 — Surveillance : ReadDirectoryChangesW + réconciliation

Le journal USN serait plus fiable, mais il requiert l'administrateur. `ReadDirectoryChangesW` (via `FileSystemWatcher`, handle `FILE_LIST_DIRECTORY`) fonctionne sans élévation. Ses limites (débordement de tampon, partages réseau) sont compensées par la coalescence, la détection de débordement suivie d'une réconciliation, et la réconciliation au démarrage et périodique. Le traitement par diff de répertoire est idempotent.

## TD-08 — Raccourci global : WH_KEYBOARD_LL (double Ctrl) + RegisterHotKey (combinaisons)

`RegisterHotKey` ne sait pas détecter une double pression d'un modificateur seul : le hook bas niveau est donc nécessaire. Le hook tourne sur un thread dédié (une boucle de messages lente provoquerait sa désinstallation silencieuse par Windows après `LowLevelHooksTimeout`), retourne immédiatement et ne bloque jamais les touches. La santé du hook est vérifiée périodiquement et il est réinstallé si besoin. Les combinaisons classiques (ex. `Alt+Espace`) passent par `RegisterHotKey`, qui est plus robuste et détecte les conflits.

## TD-09 — Isolation des extracteurs : processus hôte dans un Job Object

L'exécution en processus avec timeout ne protège ni d'un `StackOverflowException` ni d'un OOM, qui tueraient Seekbeam. Le processus hôte est donc borné par le noyau (RAM, CPU en hard cap, mort avec le parent). Le coût, un processus de plus (~25 Mo), est jugé acceptable et l'hôte est arrêté après 5 min d'inactivité. Depuis TD-17, l'hôte s'exécute en plus dans un bac à sable (AppContainer).

## TD-10 — Extraction : parseurs maison + PdfPig

| Format | Choix | Alternatives écartées |
|---|---|---|
| OOXML / ODF | `System.IO.Compression` + `XmlReader` (inclus dans .NET) | DocumentFormat.OpenXml (5 Mo, DOM), NPOI |
| PDF | **PdfPig 0.1.x** (Apache-2.0, managé pur, maintenu) | PDFium (natif, binaire tiers), iText (AGPL), Docnet |
| DOC/XLS/PPT | lecteur CFB + extraction texte maison (piece table, SST BIFF8, atomes texte PPT) | NPOI (≈ 10 Mo de dépendances), IFilter système (offfilt ouvre lui-même le fichier, flags d'ouverture non contrôlables) |
| RTF / HTML / texte | maison | — |
| ZIP | `System.IO.Compression` en mémoire | SharpCompress (7z/rar) : reporté |

Les dépendances sont minimisées : seules deux dépendances de production (SQLite, PdfPig).

## TD-11 — Détection des fichiers ouverts : « Éléments récents » + ouvertures Seekbeam

| Mécanisme | Verdict |
|---|---|
| Dossier Recent (`.lnk` créés par `SHAddToRecentDocs`) | **retenu** : non intrusif, sans droits, couvre l'Explorateur, les boîtes de dialogue communes et la plupart des applications |
| Ouvertures via Seekbeam | **retenu** |
| Jump lists (`AutomaticDestinations`, format CFB) | évolution possible |
| ETW noyau FileIO / minifiltre / injection | **rejeté** : administrateur ou pilote requis, intrusif, contraire à CLAUDE.md §16 |
| Énumération des handles des autres processus | **rejeté** : intrusif, coûteux, non fiable |

Limitation documentée : les fichiers ouverts par des applications qui n'appellent pas `SHAddToRecentDocs` ne sont pas détectés.

## TD-12 — Analyse `.lnk` : parseur MS-SHLLINK maison

`IShellLink::Resolve` peut **modifier** le raccourci (mise à jour de la cible), et `IPersistFile::Load` ouvre le fichier avec des flags que nous ne contrôlons pas. Le parseur maison lit les données via `SourceFileAccess` (`LinkInfo`, `StringData`, `EnvironmentVariableDataBlock`). Le repli COM est limité à l'énumération `shell:AppsFolder`, qui ne lit pas les `.lnk` des racines indexées pour notre compte.

## TD-13 — Fichiers cloud (OneDrive, placeholders)

Lire un placeholder déshydraté déclenche son téléchargement et change ses attributs : c'est une modification indirecte. Décision : les noms sont indexés, mais l'extraction de contenu ignore `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`, `RECALL_ON_OPEN` et `OFFLINE`. Toute ouverture source utilise en plus `FILE_FLAG_OPEN_NO_RECALL`.

## TD-14 — Installation : MSI WiX v6 par utilisateur + archive portable

Alternatives : Inno Setup (absent de la machine), MSIX (signature obligatoire, sandbox du système de fichiers gênante pour un indexeur), installateur maison. WiX v6 se compile avec `dotnet build` via `WixToolset.Sdk`, sans installation préalable. WiX v7 a été écarté parce qu'il exige l'acceptation de l'EULA OSMF. L'installation se fait par utilisateur (`%LOCALAPPDATA%\Programs\Seekbeam`), sans élévation. L'archive portable stocke ses données à côté de l'exécutable.

## TD-15 — Tests : xUnit 2.9 ; analyse statique : BannedApiAnalyzers + analyse IL

xUnit est le standard .NET et bien supporté par `dotnet test`. Pour l'immutabilité, deux contrôles complémentaires sont mis en place : (1) `Microsoft.CodeAnalysis.BannedApiAnalyzers` pour les erreurs de compilation, (2) un test qui décode l'IL de tous les assemblies Seekbeam (`System.Reflection.Metadata`) et vérifie que tout appel à une API d'écriture fichier ou à un P/Invoke d'écriture se trouve dans `Seekbeam.Core.Storage`. Le contrôle (2) attrape aussi les appels indirects et les P/Invoke, que les noms de méthodes seuls ne révèlent pas.

## TD-16 — Audit dynamique sans administrateur

Process Monitor et ETW noyau requièrent l'administrateur, et la session de développement ne l'est pas. Décision : `Seekbeam.FsAudit`, un processus indépendant qui surveille le jeu de test avec **tous** les filtres `ReadDirectoryChangesW` (nom, attributs, taille, écriture, **accès**, création, **sécurité**) pendant l'exécution réelle de Seekbeam.exe. Il est complété par une comparaison d'empreintes avant/après. Un script ETW (`tools/audit/Run-EtwAudit.ps1`, TraceEvent) est fourni pour une vérification élevée, attribuée au processus.

## TD-17 — Bac à sable de l'extracteur : AppContainer, repli en intégrité faible, handles en lecture seule transmis

**Contexte.** L'extraction de contenu analyse automatiquement des documents non fiables, y compris des fichiers que l'utilisateur n'a jamais ouverts. Une faille d'un analyseur (PdfPig, ou notre propre code OOXML/ODF/CFB/RTF/ZIP) ne doit pas donner à un document malveillant l'accès aux fichiers de l'utilisateur, au réseau ou au reste du système. Le Job Object de TD-09 borne les ressources, pas les droits d'accès.

**Options comparées** (vérifiées par une sonde qui joue le rôle d'un extracteur compromis, `SandboxTests.SandboxConfinesACompromisedExtractor`) :

| | Lire les fichiers de l'utilisateur | Écrire | Réseau | Processus enfant | Registre HKCU | Prérequis |
|---|---|---|---|---|---|---|
| Jeton normal + Job Object (avant) | oui | oui | oui | bloqué (job) | oui | aucun |
| Jeton restreint en **intégrité faible** | oui | **bloqué** | oui | bloqué | **bloqué** | aucun |
| **AppContainer** sans capacité | **bloqué** | **bloqué** | **bloqué** | bloqué | **bloqué** | profil AppContainer ; ACE de lecture sur le dossier du programme |
| AppContainer « moins privilégié » (LPAC) | bloqué | bloqué | bloqué | bloqué | bloqué | idem ; **.NET ne peut pas initialiser `TimeZoneInfo`**, donc tous les formats à base de ZIP échouent |

**Décision.** L'hôte démarre au niveau le plus fort qui fonctionne : **AppContainer** sans aucune capacité, sinon un jeton restreint en **intégrité faible** (tous les privilèges retirés), sinon un jeton normal (avertissement dans le journal, niveau affiché dans le tableau de bord). LPAC est écarté : il casse DOCX/XLSX/ODT/ZIP et ne bloque rien de plus dans les tests de la sonde. Dans tous les cas, l'hôte est créé suspendu, placé dans son Job Object avant sa première instruction (plafonds mémoire et CPU, un seul processus, ni presse-papiers ni bureau), n'hérite que de ses deux tubes stdio, s'exécute avec des protections contre l'exploitation (pas de points d'extension, pas d'images distantes ou de faible intégrité, ASLR ascendant et à haute entropie) et ne peut créer aucun processus enfant.

Comme un AppContainer ne peut ouvrir aucun fichier de l'utilisateur, **Seekbeam ouvre lui-même chaque source** (en lecture seule, `SourceFileAccess`) et duplique dans l'hôte un handle restreint à `FILE_GENERIC_READ`. L'hôte vérifie les droits accordés à chaque handle reçu et refuse tout handle portant un droit d'écriture, de suppression, d'attribut ou d'ACL. L'hôte ne voit donc jamais de chemin et ne peut lire qu'un seul fichier à la fois. Au démarrage, il indique l'isolation qu'il constate sur son propre jeton, et Seekbeam rejette un niveau qui ne correspond pas à celui demandé.

**Mise en place** (`SandboxProvisioning`, une seule fois) : créer le profil AppContainer `Seekbeam.ExtractorHost` (Windows crée `%LOCALAPPDATA%\Packages\seekbeam.extractorhost` et une correspondance dans le registre), et ajouter une ACE lecture + exécution pour *TOUS LES PACKAGES D'APPLICATION* sur le dossier du programme Seekbeam. Aucune de ces écritures n'a lieu si l'emplacement est dans une racine indexée ou en contient une : l'extracteur se replie alors en intégrité faible. En mode portable, le dossier de données est dans le dossier du programme : l'héritage de cette ACE y est donc d'abord coupé, et aucun AppContainer ne peut lire l'index. `--purge-data` supprime le profil.

**Coût.** Le démarrage de l'hôte est inchangé (~100 ms). Dupliquer un handle par document coûte quelques microsecondes.

**Limites.** En intégrité faible, un extracteur compromis peut encore lire les fichiers de l'utilisateur et utiliser le réseau ; c'est pourquoi l'AppContainer est préféré et le tableau de bord affiche le niveau utilisé. Une copie portable décompressée dans un dossier indexé reste en intégrité faible (voir Dépannage). Les vulnérabilités du noyau ou de Windows permettant de sortir d'un AppContainer sont hors périmètre.

## TD-18 — Téléchargements : noms indexés, contenu jamais lu par défaut

Le dossier Téléchargements reçoit les fichiers venus d'Internet et des e-mails. L'indexation du contenu les analyserait automatiquement, sans que l'utilisateur les ouvre jamais, ce qui est le moyen classique d'atteindre une vulnérabilité d'analyseur. Décision : un nouveau réglage, `Content.ExcludedFolders` (dossiers dont les fichiers sont indexés par leur nom uniquement), contient par défaut le dossier connu Téléchargements (`FOLDERID_Downloads`, qui suit les redirections). Les configurations du schéma 1 sont migrées une fois. Retirer Téléchargements de la liste dans les paramètres affiche d'abord une explication du risque et demande une confirmation. Modifier la liste réévalue la file de contenu : le texte déjà stocké pour un dossier nouvellement exclu est supprimé.

## TD-19 — Commandes Exécuter et alias dans le launcher

Les utilisateurs venant de Listary s'attendent à ce que `mstsc` ou `hosts` fonctionnent. Deux mécanismes, plutôt qu'indexer `C:\Windows` (plus de 100 000 fichiers de bruit, et `hosts` n'a pas d'extension : « Ouvrir » afficherait la boîte « Ouvrir avec ») :

- **Commandes Exécuter** : ce que résout `Win+R`. Fichiers des dossiers du PATH (.com/.exe/.bat/.cmd/.msc/.cpl, sans récursion) et enregistrements App Paths. Le PATH est lu dans le registre (machine, puis utilisateur), pas dans l'environnement de Seekbeam, pour qu'un changement soit vu sans redémarrer. La résolution des noms suit cmd.exe : ordre du PATH, puis ordre des extensions dans un dossier. Réexamen toutes les 15 minutes (≈ 900 entrées en 10 à 30 ms sur la machine de développement) au lieu de surveiller System32 par `FileSystemWatcher`, que les mises à jour Windows réveilleraient sans cesse.
- **Classement** : ce sont des centaines de noms abrégés (msdt, mshta, msra…). Ils ne correspondent que par le nom exact (ou le nom sans .msc/.cpl) ou un préfixe d'au moins 3 lettres, jamais par sous-chaîne ni faute de frappe ; le bonus d'application ne s'applique qu'à la correspondance exacte. La recherche dans le catalogue reste d'environ 25 µs par frappe.
- **Alias** : mots-clés de l'utilisateur → cible, arguments, dossier de travail, exécution en administrateur. Trouvés par leur mot-clé uniquement (jamais par leur cible) ; l'apprentissage est associé au mot-clé. Un alias tapé exactement passe devant une commande du même nom.
- **Arguments** : « mot reste », où `mot` est exactement une commande ou un alias, propose cette commande avec `reste` ajouté à ses arguments, notée comme la commande exacte (elle passe devant les correspondances ordinaires sans masquer un fichier dont le nom est toute la requête).
- **Immutabilité** : énumération des dossiers par `SourceFileAccess`, registre ouvert en lecture seule ; le lancement passe par la délégation `ShellExecuteEx` existante (FILE_IMMUTABILITY_POLICY §8). L'alias `hosts` ouvre délibérément un fichier système dans un éditeur, en administrateur : l'écriture éventuelle est l'action de l'utilisateur dans le Bloc-notes.
- Pas encore fait : un catalogue intégré de raccourcis système (pages `ms-settings:`, dossiers `shell:`, mots-clés traduits).
