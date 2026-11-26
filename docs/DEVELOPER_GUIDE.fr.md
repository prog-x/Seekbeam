[English](DEVELOPER_GUIDE.md) | **Français**

# Guide développeur

## Arborescence
```
src/Seekbeam.Core            moteur (sans UI) : Storage, SourceAccess, Catalog, NameIndex, Search, Indexing, Watching,
                           Content, Apps, Discovery, Resources, Engine, Config, Text, Input, Diagnostics
src/Seekbeam.Extraction      extracteurs (texte, markup, OOXML, ODF, RTF, PDF/PdfPig, DOC/XLS/PPT via CFB, ZIP)
src/Seekbeam.ExtractorHost   processus isolé d'extraction (stdin/stdout, Job Object)
src/Seekbeam.App             WPF : launcher, recherche avancée, paramètres, tableau de bord, tray, hotkeys
tests/Seekbeam.Core.Tests          unitaires + intégration (filesystem réel, watcher, ACL, jonctions, contenu de bout en bout)
tests/Seekbeam.Extraction.Tests    extracteurs (fixtures générées, fichiers malformés, bombes, fuzzing)
tests/Seekbeam.Immutability.Tests  analyse IL, tests d'intégrité SHA-256 (dont le vrai Seekbeam.exe tué puis relancé)
tests/Seekbeam.SandboxProbe        exe de test uniquement, joue un extracteur compromis (SandboxTests prouve ce que bloque chaque niveau du bac à sable)
tools/Seekbeam.Bench               benchmarks (synthetic, search, index, folderindex)
tools/Seekbeam.DatasetGen          générateur de datasets, snapshots et comparaison d'intégrité
tools/Seekbeam.FsAudit             moniteur indépendant des changements filesystem
tools/Seekbeam.DialogCheck         pilote de vraies boîtes Ouvrir/Enregistrer/choix de dossier et l'Explorateur avec le code livré d'« aller au dossier » (rapport : docs/DIALOG_NAVIGATION_CHECK.fr.md)
tools/audit/                     scripts d'audit dynamique
installer/                       MSI WiX v6
build/Build-Release.ps1          pipeline de release
```

## Commandes
```powershell
dotnet build Seekbeam.sln
dotnet test Seekbeam.sln
dotnet run -c Release --project tools/Seekbeam.Bench -- synthetic artifacts/bench/syn1m 1000000
dotnet run -c Release --project tools/Seekbeam.DatasetGen -- tree C:\SeekbeamBench\disk1m 1000000
dotnet run -c Release --project tools/Seekbeam.Bench -- index artifacts/bench/disk1m-data C:\SeekbeamBench\disk1m Performance
pwsh tools/audit/Run-DynamicAudit.ps1
dotnet run --project tools/Seekbeam.DialogCheck -- report.md --app src/Seekbeam.App/bin/Debug/net10.0-windows/Seekbeam.exe
pwsh build/Build-Release.ps1
```

## Intégration continue (GitHub Actions)
- **`.github/workflows/ci.yml`** (push sur `main`, pull requests) : compile en Release, puis compile en Debug et lance `dotnet test` sur `windows-latest`. Les résultats de tests sont joints quand un test échoue. Sur `main`, un second job exécute `build/Build-Release.ps1 -SkipTests` et conserve le MSI et le zip comme artefacts du workflow pendant 14 jours.
- **`.github/workflows/release.yml`** (tag `v*`) : vérifie que le tag correspond à `<Version>` dans `Directory.Build.props`, exécute toute la chaîne de release avec les tests, et crée une release GitHub en **brouillon** avec le MSI, le zip portable et `SHA256SUMS.txt`.
- **Version** : `<Version>` dans `Directory.Build.props` est le seul endroit à modifier. Elle nomme le MSI et le zip, et fixe la version du MSI.
- **`.github/dependabot.yml`** : mises à jour NuGet chaque semaine, GitHub Actions chaque mois.

## Règles non négociables (immutabilité)
1. On ne lit une source que par `SourceFileAccess`, et on n'écrit que par `SeekbeamStorage`.
2. `src/BannedSymbols.txt` interdit les API d'écriture (erreur de compilation RS0030). Une suppression de cet avertissement n'est permise que dans `Storage/` et `SourceAccess/`, avec justification.
3. `StaticAnalysisTests` décode l'IL. Tout nouvel appel d'écriture hors `Seekbeam.Core.Storage`, ou tout P/Invoke non revu, fait échouer les tests. Ajouter un P/Invoke impose de l'ajouter à l'allow-list **après revue**.
4. Un extracteur ne lit que le `Stream` reçu : aucun fichier temporaire, aucune extraction sur disque.
5. Toute nouvelle fonctionnalité qui touche les sources doit être couverte par `IntegrityTests`.

## Concepts clés
- **Name Index** : segments mmap immuables (`names-*.seg`, format v2 : ids, parents, drapeaux, offsets, texte normalisé, plus un sidecar des noms de dossiers) + delta mémoire + tombstones. Il est entièrement dérivable de `catalog.db` (`name_rev`). Merge : `NameIndexManager.FlushLocked`.
- **Écritures du catalogue** : uniquement via `CatalogWriter.Batch` (transaction courte, verrou d'écriture). Les `NameChange` retournés par `Commit()` doivent être appliqués à `NameIndexManager.Apply` dans l'ordre.
- **Indexation** : un seul thread `NameIndexer` (mode arrière-plan) traite `name_queue` et les commandes (watcher, renommages, découverte, racines). `DirectoryScanner.ApplyDiff` est l'unique algorithme de synchronisation.
- **Recherche** : `NameSearcher` (scan par blocs, masques par token, ancêtres mémoïsés, faisabilité par sidecar, bitap, top-K), `SearchEngine` (apps, hydratation, surlignage), `DeepSearch` (FTS5 + noms + filtres + extraits).
- **Ressources** : `ResourceManager.Decide` est une fonction pure testée. Les workers appellent `IWorkGate.WaitTurn` et `Report`.

## Conventions
C# 14, nullable activé, espaces de noms au niveau fichier, pas de dépendance nouvelle sans revue (licence, maintenance, taille, transitives). Les commentaires expliquent le *pourquoi*. Les erreurs sont traitées explicitement, et les opérations longues prennent un `CancellationToken`.

## Langues de la documentation
La documentation du projet est bilingue :
- **Noms de fichiers** : anglais dans `X.md`, français dans `X.fr.md`, dans le même dossier.
- **Même changement** : les deux versions sont mises à jour ensemble. Une modification dans une langue n'est pas terminée tant que l'autre n'est pas à jour.
- **Sélecteur de langue** : la ligne 1 de chaque fichier renvoie vers l'autre langue : `**English** | [Français](X.fr.md)` en anglais, `[English](X.md) | **Français**` en français.
- **Liens** : les documents anglais pointent vers les documents anglais, les documents français vers les versions `.fr.md`.
- **Non traduits** : `CLAUDE.md` (règles du projet pour les agents de code et les contributeurs) et `docs/project/SPECIFICATIONS.md` (cahier des charges initial, conservé comme document historique) sont en français uniquement. `LICENSE` et `THIRD-PARTY-NOTICES.md` sont des textes juridiques, en anglais uniquement.

## Langues de l'interface
L'interface est livrée en anglais (langue neutre), français, allemand et italien (`src/Seekbeam.App/Localization/Strings*.resx`).
- **Aucun texte visible écrit en dur** : le XAML utilise `{l:Tr Cle}`, le C# `L.T("Cle")` ou `L.F("Cle", args)`.
- **Quatre langues dans le même changement** : une clé nouvelle ou modifiée est ajoutée d'un coup aux quatre fichiers `.resx`.
- **Formats** : le texte suit la langue de l'interface, les nombres et les dates suivent les paramètres régionaux de Windows (`CultureInfo.CurrentCulture`).
- **Textes du moteur** : `Seekbeam.Core` renvoie des codes (noms et motifs des profils de ressources, statut du contenu) que l'interface traduit ; il ne construit jamais de phrase visible.
- **Contrôles** : `LocalizationTests` échoue sur une clé manquante ou inconnue, un paramètre différent, une clé inconnue dans le code, ou un texte écrit en dur dans le XAML ou le C#.

Voir [TRANSLATING.fr.md](TRANSLATING.fr.md) pour la procédure des traducteurs.

## Ajouter un extracteur
Implémenter `IContentExtractor` (`Id`, `Version`, `Extensions`, `Extract(Stream, ext, TextSink, ExtractionContext, ct)`), l'enregistrer dans `BuiltInExtractors.All()`, puis ajouter des tests avec fixtures générées, fichiers malformés et limites. La version est enregistrée par document (`docs.version`) ; après un changement d'extracteur, utiliser « Réindexer le contenu » (la réextraction automatique par version est une évolution prévue).
