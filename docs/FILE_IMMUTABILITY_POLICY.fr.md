[English](FILE_IMMUTABILITY_POLICY.md) | **Français**

# Seekbeam — Politique de non-modification des fichiers sources

Version 1.0 — 2026-11-21. Statut : **normatif**. Cette exigence prime sur toute fonctionnalité, optimisation ou facilité d'implémentation.

## 1. Exigence

Seekbeam ne modifie, ne déplace, ne renomme, ne supprime ni n'altère jamais les fichiers et dossiers qu'il indexe. Il n'y crée aucun fichier, y compris temporaire.

## 2. Périmètre protégé

Sont des **sources protégées** :
1. tout fichier ou dossier situé sous une racine configurée, sous-dossiers compris, **exclusions comprises** (un dossier exclu n'est pas indexé mais reste protégé) ;
2. tout fichier découvert de manière opportuniste, et le dossier « Éléments récents » de l'utilisateur ;
3. tout fichier ouvert ou analysé par un extracteur, et tout fichier contenu dans une archive traitée ;
4. les raccourcis et exécutables lus par l'index des applications (menu Démarrer, bureau, dossiers configurés) ;
5. les cibles atteintes par un lien symbolique, une junction, un point de montage ou un chemin alternatif (8.3, `\\?\`, UNC, autre lettre de lecteur) : la protection s'applique à l'objet atteint, quel que soit le chemin.

**Seule exception** : le répertoire de données de Seekbeam (`%LOCALAPPDATA%\Seekbeam` ou `<exe>\Data` en mode portable). Il est automatiquement exclu de l'indexation et n'est donc jamais une source.

## 3. Opérations autorisées sur les sources

| Opération | API | Droits demandés |
|---|---|---|
| Lecture de métadonnées | `GetFileAttributesExW` | aucun handle |
| Énumération de dossier | `CreateFileW` + `NtQueryDirectoryFile` | `FILE_LIST_DIRECTORY` + `SYNCHRONIZE`, partage R/W/D |
| Identité (volume, FileId) | `CreateFileW` + `GetFileInformationByHandle` | `FILE_READ_ATTRIBUTES`, partage R/W/D |
| Lecture de contenu | `CreateFileW` | `GENERIC_READ`, partage R/W/D, `OPEN_EXISTING`, `FILE_FLAG_SEQUENTIAL_SCAN`, `FILE_FLAG_OPEN_NO_RECALL` |
| Surveillance | `ReadDirectoryChangesW` (FileSystemWatcher) | `FILE_LIST_DIRECTORY` |
| Ouverture par l'utilisateur | `ShellExecuteEx` (`open`, `runas`, `openas`, `properties`), `SHOpenFolderAndSelectItems` | délégué au shell (§8) |

`OPEN_EXISTING` empêche par construction toute création. Le partage R/W/D garantit que Seekbeam ne bloque jamais une autre application.

## 4. Opérations interdites

Écriture ou troncature de contenu, création, suppression, renommage, déplacement, copie vers une source, modification d'attributs, de timestamps, d'ACL, de propriétaire, d'ADS ou d'EA, compression ou chiffrement NTFS, création de fichiers temporaires ou d'extractions d'archive dans une source, « réparation » de fichiers, résolution de raccourcis avec mise à jour (`IShellLink::Resolve`), hydratation volontaire de placeholders cloud, verrouillage exclusif.

**Fonctionnalités retirées du périmètre** (bien que courantes dans Listary ou DocFetcher) : renommer, déplacer, supprimer, envoyer à la corbeille, modifier les attributs ou les métadonnées, « réparer » un lien. Le cahier des charges §19 les exclut aussi explicitement. Seules des actions non destructives sont proposées.

## 5. Architecture de protection

1. **`Seekbeam.Core.SourceAccess.SourceFileAccess`** : l'unique code autorisé à ouvrir une source. Son API publique ne contient que des opérations de lecture. Le flux retourné (`ReadOnlySourceStream`) lève une exception sur `Write`, `SetLength` et `WriteByte`, et `CanWrite` vaut `false`.
2. **`Seekbeam.Core.Storage.SeekbeamStorage`** : l'unique code autorisé à écrire sur disque. Chaque chemin est canonicalisé (`GetFullPath`, puis chemin final du DataRoot résolu), doit être sous le DataRoot, et ne doit être sous aucune racine sauf si le DataRoot y est exclu. Sinon une `ImmutabilityViolationException` est levée et journalisée. Les fichiers SQLite et les segments reçoivent leur chemin exclusivement de `SeekbeamStorage`. `SQLITE_TMPDIR` et `temp_store=MEMORY` évitent les temporaires hors DataRoot.
3. **Extraction** : en mémoire ou dans des flux ; jamais de fichier intermédiaire. Les archives sont lues entrée par entrée en mémoire.
4. **Processus hôte d'extraction** (TD-17) : s'exécute dans un bac à sable AppContainer et ne reçoit jamais de chemin. Seekbeam ouvre la source via `SourceFileAccess` et duplique dans l'hôte un handle restreint à `FILE_GENERIC_READ` (`SourceFileAccess.DuplicateReadHandleInto`). L'hôte vérifie les droits accordés à chaque handle reçu et refuse tout droit d'écriture, de suppression, d'attribut ou d'ACL (`SourceFileAccess.FromReceivedHandle`). Il ne renvoie que du texte et n'a aucune API d'écriture fichier (vérifié par l'analyse IL). Même compromis par un document malveillant, il ne peut pas écrire : Windows refuse toute écriture au jeton AppContainer, et le handle lui-même ne permet pas d'écrire.
5. **Validation de configuration** : une racine contenant le DataRoot reçoit une exclusion automatique ; une racine située *dans* le DataRoot est refusée.

## 6. Répertoires d'écriture autorisés

`<DataRoot>\config.json`, `<DataRoot>\db\`, `<DataRoot>\index\`, `<DataRoot>\logs\`, `<DataRoot>\tmp\`, `<DataRoot>\cache\`. S'y ajoutent hors système de fichiers : la clé de registre `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Seekbeam` (démarrage automatique) et le presse-papiers.

Mise en place unique du bac à sable (`Seekbeam.Core.Storage.SandboxProvisioning`, TD-17), seules écritures hors du DataRoot :
- le profil AppContainer `Seekbeam.ExtractorHost`, créé par Windows (`CreateAppContainerProfile`) : dossier `%LOCALAPPDATA%\Packages\seekbeam.extractorhost` et une correspondance sous `HKCU\Software\Classes\Local Settings` ; supprimé par `--purge-data` ;
- une ACE lecture + exécution pour *TOUS LES PACKAGES D'APPLICATION* (`S-1-15-2-1`) sur le dossier du programme Seekbeam, pour que le bac à sable puisse charger son propre code.

Les deux sont refusées si l'emplacement est dans le périmètre d'indexation ou contient une racine indexée (`ScopeRules.TouchesScope`) ; l'extracteur s'exécute alors en intégrité faible. En mode portable, le DataRoot (`Data\`) se trouve dans le dossier du programme : avant toute autorisation, l'héritage est coupé au niveau du DataRoot et toute ACE AppContainer retirée, pour qu'aucun AppContainer ne puisse jamais lire l'index (test `PortableDataDirectoryIsNeverReadableByAppContainers`).

## 7. Méthodologie d'audit et de vérification

| Niveau | Outil | Ce qui est prouvé |
|---|---|---|
| Compilation | `Microsoft.CodeAnalysis.BannedApiAnalyzers` + `BannedSymbols.txt` (erreur RS0030) | aucun appel direct d'API d'écriture hors des fichiers explicitement autorisés |
| Analyse IL | `ImmutabilityStaticAnalysisTests` : décodage de chaque corps de méthode de chaque assembly Seekbeam (`System.Reflection.Metadata`), inventaire des `call`/`callvirt`/`newobj`/`ldftn` vers les API d'écriture (System.IO, MemoryMappedFiles, Compression, Process), de tous les P/Invoke déclarés et de leurs points d'entrée | toute écriture potentielle est localisée dans `Seekbeam.Core.Storage`, chaque P/Invoke est inventorié et classé |
| Revue des dépendances | analyse IL de PdfPig et Microsoft.Data.Sqlite ; revue des appels natifs SQLite | les chemins d'écriture des dépendances ne sont pas atteignables avec notre usage (flux en lecture seule, chemins issus de SeekbeamStorage) |
| Revue manuelle | revue complète de chaque fichier source (liste dans l'AUDIT) | intention et contexte de chaque opération |
| Tests d'intégrité | `Seekbeam.Immutability.Tests` : empreinte avant/après de chaque fichier et dossier (SHA-256, taille, attributs, création, écriture, accès, SDDL, ADS, arborescence) | aucune différence après chaque scénario |
| Dynamique | `Seekbeam.FsAudit` : processus indépendant, `ReadDirectoryChangesW` avec **tous** les filtres pendant l'exécution réelle de Seekbeam.exe ; script ETW (administrateur) attribué au processus | comportement réel observé par le noyau |

### Scénarios de test d'intégrité
Scan initial ; indexation des noms ; extraction de tous les formats ; archives (normales, imbriquées, bombe, chemins malveillants, chiffrées) ; indexation incrémentale ; surveillance (changements effectués **par le test** dans une zone tierce, exclus de la comparaison) ; découverte opportuniste ; réconciliation ; pause et reprise ; arrêt normal ; arrêt forcé de l'hôte et du moteur ; reprise après crash ; fichiers malformés ; accès refusé (ACL deny) ; fichiers verrouillés (ouverts en exclusif par le test) ; junctions et cycles ; fichiers volumineux ; lecture seule et cachés ; noms Unicode et chemins longs.

## 8. Limites de la garantie (assumées et documentées)

1. **Last access time** : sur cette machine, NTFS met à jour l'heure du dernier accès (`DisableLastAccess = 2`, géré par le système, activé). Lire un fichier ou lister un dossier peut donc faire évoluer `LastAccessTime`, avec une granularité d'environ 1 h. C'est un **effet de l'OS**, pas une écriture de Seekbeam. Le seul moyen documenté de l'éviter (`SetFileTime(…, 0xFFFFFFFF)`) exige `FILE_WRITE_ATTRIBUTES` sur la source, ce que la politique interdit. Les tests isolent ce champ et le signalent sans le confondre avec une modification.
2. **Applications externes** : Seekbeam demande à Windows d'ouvrir un fichier. L'application qui l'ouvre peut le modifier, ce que Seekbeam ne peut pas garantir. La boîte « Propriétés » de Windows permet à l'utilisateur de modifier des attributs : c'est une action de l'utilisateur dans le shell. L'UI et le guide le signalent.
3. **Composants du shell** : l'extraction d'icônes (`SHGetFileInfo`, `IShellItemImageFactory`) et l'énumération `shell:AppsFolder` font lire des fichiers par des composants Windows, dont le contrat est en lecture seule mais dont le code n'est pas sous notre contrôle. Pour les fichiers, l'icône est obtenue par extension (`SHGFI_USEFILEATTRIBUTES`, sans accès disque), sauf pour `.exe`, `.lnk` et `.ico`.
4. **Fichiers cloud** : `FILE_FLAG_OPEN_NO_RECALL` et le filtrage par attributs empêchent l'hydratation. Un fournisseur cloud tiers non conforme pourrait toutefois réagir à l'énumération.
5. **Antivirus et filtres tiers** : ils peuvent réagir à nos lectures. C'est hors du périmètre de Seekbeam.
6. **Audit dynamique** : `ReadDirectoryChangesW` observe les effets mais ne les attribue pas à un processus. L'attribution exige ETW ou Procmon avec droits administrateur, et ce script est fourni mais pas exécuté dans la session de développement non élevée.

## 9. Critères d'acceptation

Les 14 critères de CLAUDE.md §29.13 s'appliquent. Concrètement : BannedApiAnalyzers est actif sur tous les projets et le build n'a aucune violation ; le test d'analyse IL est vert ; tous les scénarios d'intégrité sont verts (zéro différence hors `LastAccessTime` documenté) ; FsAudit ne détecte aucun événement de nom, taille, écriture, attribut ou sécurité sur le jeu protégé ; le rapport `docs/FILE_IMMUTABILITY_AUDIT.md` est publié avec ses résultats réels.
