[English](IMPLEMENTATION_PLAN.md) | **Français**

# Seekbeam — Plan d'implémentation

Chaque phase se termine par : build sans erreur, tests verts, mise à jour de `PROJECT_STATUS.md`. Les phases sont réordonnées par rapport aux phases recommandées du cahier des charges initial ([docs/project/SPECIFICATIONS.md](docs/project/SPECIFICATIONS.md) §31) pour valider très tôt le moteur du Name Index, comme le demande le cahier des charges §6.3.

| # | Phase | Objectifs / tâches | Dépend de | Tests | Critères d'acceptation | Livrables |
|---|---|---|---|---|---|---|
| 0 | Analyse & conception | environnement, spike moteur, documents | — | spike reproductible | décisions documentées | ARCHITECTURE, TD, plans, RISKS, POLICY |
| 1 | Socle Core | `Normalizer`, `SourceFileAccess`, `SeekbeamStorage`, `DataPaths`, config JSON validée, logs tournants, banned APIs | 0 | unitaires normalisation, garde de chemins, config round-trip | aucune écriture hors DataRoot possible par l'API | Seekbeam.Core |
| 2 | Catalogue & Name Index | schéma SQLite, `EntryStore`, segments mmap, delta, tombstones, fusion, reprise après crash | 1 | segments (écriture/lecture/fusion), cohérence après « crash » simulé | recherche cohérente après crash | NameIndex |
| 3 | Recherche & ranking | parser de requêtes, matcher multi-tokens, ancêtres, filtres, fuzzy bitap, ranking, top-K, annulation | 2 | jeux requête → attendu, `Ant 202611 cahier`, accents, fautes | p95 < 30 ms sur 2 M (synthétique) | SearchEngine |
| 4 | Benchmark Name Index | générateur synthétique, bench 1 M / 2 M / 5 M (in-proc), RAM | 3 | bench reproductible | cibles PERFORMANCE_PLAN | docs/benchmarks |
| 5 | Scanner & files | `name_queue`, énumération NtQueryDirectoryFile, diff par répertoire, périmètres, exclusions, profondeur, liens/cycles | 2 | arborescences temp, junctions, cycles, permissions refusées | aucune boucle, règles respectées | Indexing |
| 6 | Surveillance & réconciliation | watcher, coalescence, renommage en place, débordement, réconciliation | 5 | création/modif/suppr/renommage/déplacement en direct | index à jour < 3 s | Watching |
| 7 | Applications | parseur `.lnk`, menu Démarrer, bureau, AppsFolder (UWP), exe configurés, icônes, lancement | 1 | parseur `.lnk` sur raccourcis réels | apps trouvables et lançables | Apps |
| 8 | Application WPF | instance unique, tray, hook double Ctrl, RegisterHotKey, launcher, navigation, actions, positionnement | 3, 7 | machine à états hotkey, positionnement, VM du launcher | double Ctrl fiable, Esc, Enter | Seekbeam.exe |
| 9 | Content Index | extracteurs, hôte isolé + Job Object, `content_queue`, FTS5, extraits | 5 | chaque extracteur, fichiers malformés, timeout, crash de l'hôte | échecs isolés | Content |
| 10 | Recherche approfondie | fenêtre dédiée, syntaxe, filtres, extraits, aperçu, actions | 9 | parser FTS, filtres | — | DeepSearch |
| 11 | Découverte opportuniste | moniteur Recent, règles, budget | 5 | fichier ouvert hors zone connue → indexé | périmètres respectés | Discovery |
| 12 | Ressources | modes, Auto, Job limits, background mode, pause/reprise, plages | 5, 9 | budgets par mode, pause effective | modes mesurables | Resources |
| 13 | Dashboard & paramètres | statistiques, files, erreurs, racines, pause, réindexation, configuration complète | 8–12 | VM | — | UI |
| 14 | Immutabilité | tests d'intégrité tous scénarios, analyse IL, FsAudit, script ETW, audit | tous | suite Immutability | 14 critères CLAUDE §29.13 | AUDIT.md |
| 15 | Installation | publish self-contained R2R, MSI WiX, portable, autostart, désinstallation | 8 | install/désinstall silencieuse | installation fonctionnelle | MSI, zip |
| 16 | Validation & perf | datasets disque 1 M / 2 M, bench complet, stabilité, reprise, optimisations | tous | bench + stabilité | critères §30 | BENCHMARKS.md |
| 17 | Finalisation | README, guides utilisateur/dev, config, troubleshooting, release notes, rapport final | tous | — | — | docs |
