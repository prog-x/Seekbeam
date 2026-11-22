[English](RISKS.md) | **Français**

# Seekbeam — Registre des risques

| # | Catégorie | Risque | Probabilité / Impact | Mitigation |
|---|---|---|---|---|
| R1 | Immutabilité | Une API ou une dépendance écrit dans un fichier source | faible / critique | `SourceFileAccess` seul accès, BannedApiAnalyzers, analyse IL, tests SHA-256, FsAudit |
| R2 | Immutabilité | Hydratation de placeholders cloud lors de la lecture | moyenne / élevé | contenu ignoré pour les attributs RECALL/OFFLINE, `FILE_FLAG_OPEN_NO_RECALL` |
| R3 | Immutabilité | Mise à jour du *last access time* par NTFS lors des lectures (activée sur cette machine) | certaine / faible | effet de l'OS documenté, distingué dans les tests ; aucune écriture de Seekbeam |
| R4 | Immutabilité | Une application externe ouverte depuis Seekbeam modifie un fichier | possible / moyen | hors du contrôle de Seekbeam, signalé dans l'UI et la documentation |
| R5 | Performance | Requêtes très courtes (1 caractère) sur des millions d'entrées | moyenne / moyen | parallélisme, top-K, annulation, préférence préfixe |
| R6 | Performance | Fusion de segments coûteuse en I/O pendant le scan initial | moyenne / moyen | LSM 2 niveaux, seuils proportionnels, I/O basse priorité |
| R7 | Mémoire | Répertoires gigantesques (100 k+ enfants) en mémoire pendant le diff | faible / moyen | diff par lots triés, plafond, streaming |
| R8 | Mémoire | PDF malveillant ou énorme | moyenne / moyen | hôte isolé, Job Object RAM, limites de taille et de pages, timeout |
| R9 | Stabilité | Hook clavier désinstallé par Windows (timeout) | moyenne / élevé | thread dédié, traitement O(1), contrôle de santé et réinstallation |
| R10 | Stabilité | Corruption SQLite après coupure | faible / élevé | WAL, `synchronous=NORMAL`, `integrity_check` au démarrage après arrêt anormal, reconstruction possible |
| R11 | Indexation | Débordement du tampon `ReadDirectoryChangesW` | moyenne / moyen | détection, réconciliation ciblée |
| R12 | Indexation | Cycles via junctions ou symlinks | moyenne / élevé | non-suivi par défaut, FileId+volume, profondeur, budget |
| R13 | Indexation | Accès refusé, fichiers verrouillés | élevée / faible | erreurs isolées, journal borné, retry avec backoff |
| R14 | Indexation | Partages réseau lents ou indisponibles | moyenne / moyen | racine marquée indisponible, retry espacé, pas de watcher si non supporté |
| R15 | Sécurité | XXE ou zip bomb dans les extracteurs | moyenne / élevé | `DtdProcessing.Prohibit`, limites de ratio, de taille et de profondeur, extraction en mémoire |
| R16 | Sécurité | Fuite de données sensibles dans les logs | moyenne / moyen | aucune requête ni contenu journalisé, chemins seulement dans les erreurs, purge |
| R17 | Dépendances | PdfPig en version 0.x | moyenne / faible | version épinglée, isolé dans l'hôte, remplaçable (interface) |
| R18 | Installation | WiX ou SDK indisponibles hors ligne | faible / moyen | archive portable en alternative |
| R19 | Plateforme | Activation au premier plan refusée (`SetForegroundWindow`) | moyenne / moyen | technique `AttachThreadInput` ou ALT simulé, tests manuels |
| R20 | Environnement | Pas de droits administrateur (ETW/Procmon impossibles en développement) | certaine / moyen | FsAudit sans administrateur, script ETW fourni pour validation élevée |
| R21 | Environnement | VM à 6,5 Go de RAM et 46 Go libres pour les datasets disque | certaine / faible | 2 M fichiers sur disque maximum, 5 M en synthétique |
