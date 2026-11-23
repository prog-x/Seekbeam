[English](FILE_IMMUTABILITY_AUDIT.md) | **Français**

# Seekbeam — Rapport d'audit de non-modification des fichiers sources

Version 1.0 — 2026-11-21. Politique de référence : [FILE_IMMUTABILITY_POLICY.fr.md](FILE_IMMUTABILITY_POLICY.fr.md).
Environnement : Windows 11 Pro (VM Hyper-V), NTFS, utilisateur **non administrateur**, `DisableLastAccess = 2` (mises à jour de l'heure d'accès activées), .NET 10.0.15.

## 1. Synthèse

| Contrôle | Outil | Résultat |
|---|---|---|
| Compilation : API d'écriture interdites | BannedApiAnalyzers (RS0030 en erreur), `src/BannedSymbols.txt` | **0 violation** (build vert ; suppressions limitées à `Storage/` et `SourceAccess/`, justifiées) |
| Analyse IL de 4 assemblies livrés | `StaticAnalysisTests` (System.Reflection.Metadata) | **0 appel d'écriture hors `Seekbeam.Core.Storage`** (2 exceptions revues), **0 P/Invoke non revu**, 0 SQL `ATTACH` / `VACUUM INTO` |
| Auto-test de l'analyseur | `AnalyzerSelfTests` | détecte les écritures directes, dans un lambda, dans une machine à états async, dans un constructeur et dans un setter |
| Intégrité SHA-256 et métadonnées (in-process, tous formats, liens, verrous, ACL, pause, découverte, réconciliation) | `IntegrityTests` | **0 modification** sur 62 entrées et sur la cible de jonction externe |
| Intégrité avec changements faits par l'utilisateur pendant la surveillance | `IntegrityTests` | **0 modification** hors fichiers touchés par le test ; ceux-ci sont exactement dans l'état écrit par le test |
| Vrai `Seekbeam.exe` tué pendant l'indexation puis relancé, arrêt par `--exit` | `IntegrityTests` + `Run-DynamicAudit.ps1` | **0 modification** ; crash détecté et réparé ; arrêt propre |
| Audit dynamique indépendant (notifications noyau) | `Seekbeam.FsAudit` | **0 événement de modification** sur 30 063 entrées pendant l'exécution réelle (crash compris) |
| Contrôle positif de FsAudit | écriture, renommage, attribut, horodatage, ACL volontaires | 5/5 détectés (+ 1 événement d'accès) |
| Extracteurs : intégrité des fichiers lus | `Seekbeam.Extraction.Tests` (`ExtractFileLeavesTheSourceUntouched`, traversal) | inchangés (SHA-256, taille, attributs, dates de création et d'écriture, listing) |
| ETW attribué au processus | `tools/audit/Run-EtwAudit.ps1` | **non exécuté** (droits administrateur requis), voir §7 |

**Conclusion** (§10) : niveau de confiance élevé que Seekbeam n'effectue aucune opération de modification sur les fichiers sources, dans les limites explicitées en §8.

## 2. Méthodologie

1. **Revue architecturale** : identifier tous les canaux possibles d'écriture (API .NET System.IO, P/Invoke Win32/NT, COM shell, SQLite natif, processus lancés, dépendances tierces, presse-papiers, registre).
2. **Revue manuelle** de l'intégralité des fichiers sources (§3), en cherchant les opérations d'écriture directes et indirectes et les chemins d'exécution moins évidents (nettoyage, temporaires, archives, raccourcis, icônes, réparation).
3. **Contrôles statiques automatisés** à la compilation (BannedApiAnalyzers) et sur l'IL compilé (lambdas, async, code d'interop généré compris).
4. **Tests d'intégrité** : snapshot de référence → scénario → snapshot → comparaison champ par champ.
5. **Vérification dynamique indépendante** : un processus tiers observe les notifications du noyau pendant l'exécution réelle de l'application, avec un contrôle positif.
6. **Confrontation** : comparer les résultats dynamiques à l'inventaire statique.

## 3. Fichiers et modules examinés

Revue complète des 67 fichiers C# livrés (≈ 10 800 lignes) :

| Module | Fichiers | Écritures fichier ? | Constat |
|---|---|---|---|
| `Seekbeam.Core/Storage` | SeekbeamStorage.cs | **oui, uniquement sous DataRoot** | chemin validé (`Resolve`) : sous DataRoot, pas de point de reparse intermédiaire, DataRoot lui-même non reparse |
| `Seekbeam.Core/Storage` | SandboxProvisioning.cs | **oui, hors DataRoot, une fois** | profil AppContainer (géré par Windows) et une ACE de lecture sur le dossier du programme Seekbeam (W7, W8) ; refusé si l'emplacement touche le périmètre d'indexation |
| `Seekbeam.Core/SourceAccess` | NativeMethods.cs, SourceFileAccess.cs | non | un seul `CreateFileW`, masques `GENERIC_READ` / `FILE_LIST_DIRECTORY` / `FILE_READ_ATTRIBUTES`, `OPEN_EXISTING`, partage R/W/D, `FILE_FLAG_OPEN_NO_RECALL` ; flux `ReadOnlySourceStream` (toute écriture lève une exception) |
| `Seekbeam.Core/Catalog`, `NameIndex`, `Content/ContentDb` | CatalogDb, CatalogWriter, EntryStore, UsageStore, NameIndexManager, SegmentWriter, RecordSource, ContentDb | via SeekbeamStorage seulement | bases et segments ouverts par `SeekbeamStorage.OpenDatabase` / `CreateNew` / `MapReadOnly` |
| `Seekbeam.Core/Indexing`, `Watching`, `Discovery`, `Apps` | NameIndexer, DirectoryScanner, ScopeRules, LinkPolicy, RootWatcher, RecentItemsMonitor, AppScanner, LnkParser, AppEntry, RunCommands | non | lecture par SourceFileAccess ; `FileSystemWatcher` ouvre les dossiers en `FILE_LIST_DIRECTORY` ; `.lnk` analysés sans `IShellLink::Resolve` ; dossiers du PATH énumérés (sans récursion) par SourceFileAccess, PATH et App Paths lus dans le registre par `OpenSubKey(writable: false)` |
| `Seekbeam.Core/Content` | ContentIndexer, ExtractorClient, ExtractionService, SandboxLauncher, DeepSearch, ContentStateLookup, ContentCoverage | non | lance `Seekbeam.ExtractorHost.exe` (binaire Seekbeam) dans un AppContainer (TD-17) ; sources ouvertes ici, handles en lecture seule dupliqués dans l'hôte ; texte renvoyé par pipe. ContentStateLookup et ContentCoverage ne lisent que catalog.db et content.db (aucun accès aux sources, pas d'ATTACH) |
| `Seekbeam.Core/Search`, `Text`, `Config`, `Resources`, `Input`, `Diagnostics`, `Engine` | — | config et logs via SeekbeamStorage | — |
| `Seekbeam.Extraction` (25 fichiers, 8 extracteurs, lecteur CFB) | — | **aucune** (IL : 0 appel, 0 P/Invoke) | lecture du seul `Stream` fourni ; ZIP en `ZipArchiveMode.Read`, entrées en mémoire, jamais `ExtractToFile` ; XML `DtdProcessing.Prohibit`, `XmlResolver = null` |
| `Seekbeam.ExtractorHost` | Program.cs | **aucune** | stdin/stdout ; `PROCESS_MODE_BACKGROUND_BEGIN` ; ne reçoit que des handles, refuse tout handle portant un droit d'écriture ; indique l'isolation de son propre jeton |
| `Seekbeam.App` (WPF) | 18 fichiers | **aucune** (IL : 0 appel) | actions shell déléguées à Windows (§5), registre `HKCU\…\Run`, presse-papiers ; liens « À propos » ouverts par `ShellActions.OpenLink`, qui n'accepte que des pages sous l'URL du projet (jamais un chemin local) ; la pastille d'état du contenu du launcher est lue dans les bases (lecture seule) |

## 4. Opérations d'écriture identifiées (inventaire IL, `artifacts/audit/il-inventory.md`)

| # | Emplacement | Finalité | Cible | Peut atteindre une racine ? | Protection |
|---|---|---|---|---|---|
| W1 | `SeekbeamStorage..ctor`, `CreateNew`, `OpenAppend`, `WriteAllBytesAtomic` → `Directory.CreateDirectory` | dossiers de données | `<DataRoot>\{db,index,logs,tmp,cache}` | non | `Resolve` (sous DataRoot, sans reparse) |
| W2 | `SeekbeamStorage.WriteAllBytesAtomic` → `FileStream`, `File.Replace`, `File.Move` | config.json, manifest.json | DataRoot | non | idem ; temporaire créé dans `<DataRoot>\tmp` |
| W3 | `SeekbeamStorage.CreateNew` / `MappedFile` → `FileStream`, `MemoryMappedFile.CreateFromFile` | segments du Name Index | `<DataRoot>\index` | non | idem ; mapping `MemoryMappedFileAccess.Read` |
| W4 | `SeekbeamStorage.OpenAppend` | journaux | `<DataRoot>\logs` | non | idem |
| W5 | `SeekbeamStorage.TryDelete`, `CleanTemp`, `DeleteAllOwnedData` → `File.Delete` | segments retirés, temporaires, purge | DataRoot | non | `Resolve` sur chaque chemin (y compris ceux énumérés) |
| W6 | `SeekbeamStorage.OpenDatabase` → `SqliteConnection` | catalog.db, content.db (+ -wal/-shm) | `<DataRoot>\db` | non | chemin issu de `Resolve` ; `temp_store=MEMORY` ; `sqlite3_win32_set_directory` → `<DataRoot>\tmp` |
| W7 | `SandboxProvisioning.GrantAppContainerRead` → `FileSystemAclExtensions.SetAccessControl` | permettre au bac à sable de charger son code | dossier du programme Seekbeam | **refusé si c'est le cas** | `ScopeRules.TouchesScope` ; ajoute une seule ACE de lecture, une fois |
| W8 | `SandboxProvisioning.Ensure` → `userenv!CreateAppContainerProfile` | profil AppContainer | `%LOCALAPPDATA%\Packages\seekbeam.extractorhost`, correspondance HKCU | **refusé si c'est le cas** | même contrôle ; objet géré par Windows ; supprimé par `--purge-data` |
| E1 | `SandboxLauncher.Start` → `CreateProcessW` / `CreateProcessAsUserW` | processus d'extraction | — (pas de fichier) | — | exécutable Seekbeam à côté de Seekbeam.exe ; AppContainer ou intégrité faible ; suspendu, puis Job Object ; aucun processus enfant |
| E3 | `SourceFileAccess.DuplicateReadHandleInto` → `DuplicateHandle` | transmettre une source au bac à sable | source | **lecture seule** | masque d'accès `FILE_GENERIC_READ` ; seul appelant de `SourceNative.DuplicateHandle` (contrôle IL) |
| E4 | `SourceFileAccess.FromReceivedHandle` → `new FileStream(SafeFileHandle, FileAccess.Read)` | l'hôte lit la source | source | **lecture seule** | droits accordés vérifiés par `NtQueryObject` ; tout droit d'écriture est refusé |
| E2 | `SourceFileAccess.OpenRead` → `new FileStream(SafeFileHandle, FileAccess.Read)` | lecture | source | **lecture seule** | le handle est ouvert en `GENERIC_READ` : l'écriture est refusée par le noyau |
| N1 | `SourceNative.CreateFileW` (P/Invoke) | ouverture source | source | lecture seule | appelé uniquement par `SourceNative.Open`, masques constants en lecture |

P/Invoke : Seekbeam.Core 48, Seekbeam.dll 32, ExtractorHost 6. Tous sont dans l'allow-list revue (hooks, hotkeys, fenêtres, DWM, tray, icônes, shell, Job Objects, sondes système, lecture fichier, bac à sable : création de processus, restriction de jeton, profil AppContainer, duplication de handle). Les points d'entrée sensibles sont attachés à un seul type déclarant (contrôle IL) : `CreateFileW` et `DuplicateHandle` à `SourceNative`, `CreateProcessW` / `CreateProcessAsUserW` / `SetTokenInformation` à `SandboxLauncher`, `CreateAppContainerProfile` / `DeleteAppContainerProfile` à `SandboxProvisioning`. **Aucun** des points d'entrée interdits (`WriteFile`, `DeleteFile`, `MoveFile*`, `SetFile*`, `NtSetInformationFile`, `NtCreateFile`, `DeviceIoControl`, `SHFileOperation`, `Set*Security*`…) n'est déclaré.

**Dépendances tierces** (informatif, IL analysé) : PdfPig contient un seul site d'écriture (`PdfMerger.OpenReadShared` → `FileStream`), non atteignable puisque Seekbeam ne fusionne pas de PDF et ne passe que des flux en lecture seule. Microsoft.Data.Sqlite n'écrit que dans la base ouverte, dont le chemin vient de SeekbeamStorage. SQLitePCLRaw (154 P/Invoke vers e_sqlite3) n'écrit que dans les fichiers de base ouverts et dans le répertoire temporaire configuré.

## 5. Risques identifiés et corrections effectuées

| Risque | Gravité | Correction / décision |
|---|---|---|
| `IShellLink::Resolve` peut réécrire un `.lnk` ; `IPersistFile::Load` ouvre avec des flags non maîtrisés | élevée | parseur MS-SHLLINK maison en lecture seule (TD-12) ; les `.lnk` du menu Démarrer et des Éléments récents ne sont jamais ouverts par COM |
| Lire un placeholder cloud (OneDrive) le télécharge et change ses attributs | élevée | contenu ignoré si `RECALL_ON_DATA_ACCESS`, `RECALL_ON_OPEN` ou `OFFLINE` ; toute ouverture en `FILE_FLAG_OPEN_NO_RECALL` (TD-13) |
| Fichiers temporaires SQLite dans `%TEMP%` ou ailleurs | moyenne | `temp_store=MEMORY` + répertoire temporaire SQLite forcé sous DataRoot |
| Jonction placée dans le dossier de données qui redirigerait une écriture vers une source | moyenne | `SeekbeamStorage.Resolve` refuse tout point de reparse entre DataRoot et la cible, et un DataRoot reparse |
| DataRoot situé dans une racine indexée (profil utilisateur) | moyenne | DataRoot toujours exclu du périmètre (`ScopeRules.IsDataDirectory`) ; une racine *dans* DataRoot est refusée |
| Extraction d'archive sur disque, chemins `..` | élevée | extraction exclusivement en mémoire, bornée ; noms dangereux seulement affichés |
| Fonctionnalités modifiantes (renommer, déplacer, supprimer, attributs) prévues par Listary/DocFetcher | élevée | **non implémentées** (politique §4) ; « Exclure de l'index » ne modifie que la configuration Seekbeam |
| Verrouillage d'un fichier gênant une autre application | faible | partage R/W/D systématique ; les fichiers verrouillés par d'autres sont réessayés plus tard (test : fichier ouvert en `FileShare.None`) |
| Blocage de la suppression d'un dossier surveillé | faible | handles `FileSystemWatcher` en partage complet ; la suppression d'un dossier surveillé reste possible (elle est finalisée à la fermeture du handle) |
| Lecture erronée des métadonnées (taille, dates) par packing incorrect de structures Win32 | moyenne (correction fonctionnelle) | `Pack = 4` sur `WIN32_FILE_ATTRIBUTE_DATA` et `BY_HANDLE_FILE_INFORMATION` + test de régression (bug trouvé pendant l'audit) |
| Biais méthodologique : l'outil de snapshot lisait les dates avant de hacher | faible | métadonnées lues après le hachage (`Refresh`) |
| Un document malveillant exploite une faille d'analyseur : code arbitraire dans l'hôte d'extraction | élevée | bac à sable AppContainer sans capacité, sources transmises en handles lecture seule, Job Object, protections contre l'exploitation (TD-17) ; prouvé par `SandboxTests.SandboxConfinesACompromisedExtractor` (lecture d'un autre fichier, écriture, réseau, processus enfant, registre : tous refusés) |
| Fichiers venus d'Internet et des e-mails analysés sans avoir été ouverts | moyenne | Téléchargements exclu par défaut de l'extraction de contenu, avec une explication avant de le réactiver (TD-18) |
| Mode portable : l'ACE AppContainer du dossier du programme serait héritée par `Data\` (index et texte extrait lisibles par tout AppContainer) | élevée (trouvée avant publication) | héritage coupé au niveau du DataRoot avant toute autorisation, ACE AppContainer retirées, exposition existante réparée au démarrage ; test `PortableDataDirectoryIsNeverReadableByAppContainers` |
| Test d'intégrité limité sans bruit par la profondeur par défaut de 10 : le chemin de plus de 260 caractères n'était plus extrait | faible (couverture de test) | jeu d'intégrité indexé en profondeur illimitée ; de nouveau 20/20 marqueurs |

## 6. Résultats des tests (exécution du 2026-11-22)

- `Seekbeam.Core.Tests` : **104/104**. Intégration sur filesystem réel : exclusions, profondeur, watcher (création, renommage, déplacement de fichier et de dossier, suppression, sous-arbre), réconciliation hors ligne, jonction cyclique, ACL refusée, retrait de racine, pipeline de contenu de bout en bout, crash de l'hôte d'extraction, Téléchargements indexé par nom uniquement, et le bac à sable (`SandboxTests` : chaque niveau d'isolation, extraction par handles transmis, refus d'un handle autorisant l'écriture, et matrice de confinement d'une sonde jouant un extracteur compromis).
- `Seekbeam.Extraction.Tests` : **101/101**. Malformés, bombes, XXE, fuzzing, intégrité des fichiers lus.
- `Seekbeam.Immutability.Tests` : **9/9** :
  - `FullIndexingOfEveryFormatLinksErrorsAndLocksModifiesNothing` : 62 entrées (20 formats, ZIP imbriqué, bombe 40 Mo, traversal `../`, malformés, 60 Mo au-delà de la limite, lecture seule, caché, système, ADS `Zone.Identifier`, Unicode, chemin > 260 caractères, jonction cyclique et jonction externe, dossier en ACL deny, fichier verrouillé), tous extraits dans le bac à sable AppContainer ; 20/20 marqueurs de contenu trouvés ; **0 modification**.
  - `IncrementalWatcherChangesByOthersAreTheOnlyChanges` : **0 modification** hors fichiers modifiés par le test.
  - `RealProcessKilledDuringIndexingThenRecoveredModifiesNothing` : **0 modification**, crash détecté, `--exit` propre.
  - Analyse statique (4 tests) + auto-test de l'analyseur.
- Champs comparés : SHA-256, taille, attributs, création, dernière écriture, SDDL (DACL, propriétaire, groupe), flux alternatifs, cible de reparse, existence (créations, suppressions, renommages). `LastAccessTime` est rapporté séparément (§8.1).

## 7. Vérification dynamique

### 7.1 FsAudit (sans privilège) — `tools/audit/Run-DynamicAudit.ps1`
Scénario : jeu d'audit + 30 000 entrées bulk (10 % avec texte) → snapshot → FsAudit → `Seekbeam.exe --data` (profil isolé, mode Performance, liens suivis) → **kill forcé après 2 s** (pendant l'extraction) → relance → indexation complète (13 123 documents) → `--exit` → snapshot.

| Exécution | Entrées | MODIFY | ACCESS | Différences snapshot | Verdict du script |
|---|---|---|---|---|---|
| 2026-11-21 11:33 | 62 | 0 | 0 | 0 | scénario invalide : le script a détecté la fin trop tôt (ligne de journal d'avant le crash) ; corrigé |
| 2026-11-21 11:40 | 62 | 0 | 0 | 0 | FAIL : critère « contenu terminé » jamais émis (tout le contenu était fait avant le kill) ; jeu bulk ajouté |
| 2026-11-21 11:41 | 30 063 | 0 | 0 | 0 | PASS |
| 2026-11-21 11:43 | 30 063 | 0 | 0 | 0 | PASS (après correction de la détection « drained » multi-workers) |

Aucune exécution n'a jamais observé de modification. Les deux premiers verdicts négatifs viennent de défauts du script d'orchestration, corrigés depuis.

Rapports : `artifacts/audit/dynamic-audit-*.md`, `artifacts/audit/fsaudit-last.txt`.
**Contrôle positif** : appliquées volontairement sur un dossier témoin, l'écriture, le renommage, le passage en lecture seule, la modification d'horodatage et la modification d'ACL produisent 5 événements MODIFY (et 1 ACCESS). Les 0 ci-dessus sont donc significatifs.

### 7.2 ETW — `tools/audit/Run-EtwAudit.ps1`
Il attribue chaque opération (Create, Write, SetInfo, Delete, Rename) au processus Seekbeam via `wpr -start FileIO` et `tracerpt`. **Non exécuté** : la session de développement n'est pas élevée. À exécuter depuis une console administrateur pour compléter l'attribution par processus.

### 7.3 Confrontation statique / dynamique
L'inventaire statique n'autorise des écritures que sous DataRoot. Les observations dynamiques ne montrent aucune écriture sous la racine source, et les écritures du processus mesurées par le benchmark (`GetProcessIoCounters`) correspondent au volume des bases et segments du dossier de données. Les deux approches concordent.

## 8. Limites de la garantie

1. **Heure du dernier accès** : NTFS (`DisableLastAccess = 2`) peut mettre à jour `LastAccessTime` lorsqu'un fichier est lu ou un dossier listé, par incréments d'environ une heure. C'est un effet de l'OS, pas une écriture de Seekbeam. L'éviter exigerait d'ouvrir la source en `FILE_WRITE_ATTRIBUTES` (`SetFileTime(-1)`), ce que la politique interdit. Pendant les audits, FsAudit n'a reçu **aucune** notification d'accès. Les différences d'accès vues par la comparaison de snapshots (57 sur 62 et 6 084 sur 30 063) proviennent des lectures de l'outil de snapshot lui-même (hachage) et du report d'écriture de NTFS. Elles sont classées à part.
2. **Applications externes** : « Ouvrir », « Ouvrir avec », « Exécuter en tant qu'administrateur » et « Propriétés » délèguent à Windows, de même que le lancement d'une commande Exécuter ou d'un alias (avec ses arguments, en administrateur si l'alias le demande). L'alias `hosts` fourni ouvre délibérément un fichier système dans le Bloc-notes en administrateur. L'application ouverte, ou l'utilisateur dans la boîte Propriétés, peut modifier le fichier. Ce n'est pas maîtrisable par Seekbeam ; c'est signalé dans l'UI (infobulle) et dans le guide.
3. **Composants du shell** : icônes (`SHGetFileInfo`, `IShellItemImageFactory`) et énumération `shell:AppsFolder`. Les icônes des fichiers ordinaires sont obtenues par extension, sans accès disque. Celles des `.exe`, `.lnk`, `.ico` et `.url` sont lues par le shell, dont le contrat est en lecture seule mais dont le code n'est pas audité.
4. **Fournisseurs cloud** : `FILE_FLAG_OPEN_NO_RECALL` et le filtrage d'attributs empêchent l'hydratation, mais un fournisseur tiers non conforme pourrait réagir à l'énumération. Non testé avec un vrai compte OneDrive (aucun configuré sur la machine d'audit).
5. **Antivirus et filtres tiers** : ils peuvent réagir aux lectures ; c'est hors du périmètre.
6. **Systèmes de fichiers non NTFS et partages réseau** : la logique de lecture seule est la même, mais ils n'ont pas été audités dynamiquement.
7. **Attribution par processus** : FsAudit observe les effets sans les attribuer. L'audit ETW reste à exécuter avec des droits administrateur.
8. **SQLite natif** : la bibliothèque native n'est pas analysable par l'IL. Ses écritures sont confinées par le chemin fourni, et l'observation dynamique le confirme.

## 9. Éléments restant à vérifier
- Exécuter `Run-EtwAudit.ps1` en session administrateur.
- Audit dynamique sur un dossier OneDrive avec des fichiers « en ligne uniquement ».
- Audit sur un partage réseau SMB et sur un volume exFAT/FAT32.

## 10. Conclusion
La non-modification repose sur quatre niveaux indépendants, tous verts :
1. une **architecture** où une seule classe peut écrire, et uniquement dans son dossier, tandis que les sources ne sont ouvertes qu'avec des masques d'accès en lecture, ce que le noyau fait respecter ;
2. des **contrôles statiques** à la compilation et sur l'IL réel ;
3. des **tests d'intégrité** exhaustifs, y compris sur l'exécutable réel tué en pleine indexation ;
4. une **observation dynamique indépendante**, validée par un contrôle positif.

Le niveau de confiance est **élevé** pour les fichiers NTFS locaux. La garantie ne couvre pas les effets de l'OS (heure d'accès), les applications externes ouvertes par l'utilisateur, ni les composants tiers listés en §8. L'attribution par processus via ETW reste à exécuter en session élevée.
