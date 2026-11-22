# CLAUDE.md — SEEKBEAM

> Règles du projet pour les agents de code (Claude Code ou autre) et, plus largement, pour tout contributeur. Le document est en français ; les contributeurs anglophones trouveront l'essentiel dans [CONTRIBUTING.md](CONTRIBUTING.md) et [ARCHITECTURE.md](ARCHITECTURE.md).
>
> Le projet s'appelait initialement **Findex** ; il a été renommé **Seekbeam** le 2026-11-21 (conflit de nom). Le cahier des charges initial, [`docs/project/SPECIFICATIONS.md`](docs/project/SPECIFICATIONS.md), est conservé tel quel et parle encore de « Findex » : il s'agit du même produit.

## 1. Identité du projet

Seekbeam est une application Windows native de recherche rapide, de lancement d'applications et d'indexation documentaire. Il réunit les fonctionnalités essentielles de :

* Listary : launcher, raccourcis globaux, recherche rapide, classement adaptatif ;
* Recoll : recherche plein texte dans les documents ;
* DocFetcher Pro : indexation configurable, périmètres, formats documentaires, indexation incrémentale.

Il doit permettre de retrouver instantanément une application, un fichier, un dossier ou le contenu d'un document. Il est destiné à Windows ; la priorité va à l'intégration native, aux performances, à la fiabilité et à la faible consommation de ressources.

## 2. Contributions assistées par IA

Un agent de code qui contribue à Seekbeam suit les mêmes règles qu'un développeur : changements ciblés, testés, documentés dans les deux langues, sans jamais affaiblir la garantie de non-modification (§29). Il ne committe ni ne pousse sans l'accord explicite du mainteneur.

## 3. Documents de référence

1. Le cahier des charges initial : [`docs/project/SPECIFICATIONS.md`](docs/project/SPECIFICATIONS.md) (document historique).
2. Le présent fichier.
3. [ARCHITECTURE.md](ARCHITECTURE.md) et [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).
4. [TECHNICAL_DECISIONS.md](TECHNICAL_DECISIONS.md).
5. [docs/FILE_IMMUTABILITY_POLICY.md](docs/FILE_IMMUTABILITY_POLICY.md).

En cas de contradiction : les exigences fonctionnelles explicites du cahier des charges priment ; la performance et la sécurité sont prioritaires ; les décisions d'architecture sont documentées ; une ambiguïté se résout par la solution la plus robuste et la plus simple.

## 4. Principes fondamentaux

Non négociables.

### 4.1. Performance

La recherche doit être extrêmement rapide. Le launcher reste réactif, y compris pendant l'indexation de millions de fichiers.

### 4.2. Consommation de ressources

Seekbeam ne doit pas dégrader significativement les performances de la machine. L'indexation est progressive, adaptative, interrompable, reprenable et limitée en CPU, mémoire et I/O.

### 4.3. Priorité de l'indexation des noms

L'indexation des noms et métadonnées est toujours prioritaire sur l'indexation du contenu. La recherche rapide fonctionne dès que les premiers fichiers sont indexés.

### 4.4. Confidentialité

Les données restent locales. Aucune donnée de recherche, aucun contenu, aucun chemin et aucune donnée d'apprentissage n'est transmis à un service externe. Aucune télémétrie par défaut.

### 4.5. Robustesse

Le système reprend son travail après interruption, fermeture, crash, redémarrage, erreur d'indexation, indisponibilité temporaire d'un disque ou interruption réseau.

### 4.6. Simplicité

Pas de complexité inutile. Chaque dépendance, service, processus, bibliothèque et composant doit justifier sa présence.

### 4.7. Non-modification des fichiers sources (Immutabilité)

Seekbeam ne doit **jamais** modifier, déplacer, renommer, supprimer ou altérer les fichiers et dossiers qu'il indexe, ni créer de fichiers temporaires dans les répertoires sources. Cette règle est absolue et prime sur toute fonctionnalité, optimisation ou facilité d'implémentation :

* ouverture systématique en lecture seule explicite ;
* séparation architecturale stricte entre lecture des sources (`SourceFileAccess`) et stockage interne (`SeekbeamStorage`) ;
* interdiction de toute fonctionnalité modifiant le système de fichiers utilisateur ;
* vérification par analyse statique, tests d'intégrité (SHA-256, métadonnées) et audit dynamique (voir §29).

## 5. Décisions techniques

Les choix secondaires se font sans cérémonie ; les décisions importantes sont comparées, justifiées et consignées dans [TECHNICAL_DECISIONS.md](TECHNICAL_DECISIONS.md).

## 6. Recherche technique préalable

Avant une implémentation majeure : rechercher et comparer les solutions (API Windows, NTFS, surveillance, moteurs d'index, bases de données, extraction, archives, limitation de ressources, installation), en privilégiant les documentations officielles, et identifier les limitations des API et bibliothèques retenues. Ne pas choisir une technologie seulement parce qu'elle est familière.

## 7. Architecture Windows native

La portabilité vers macOS ou Linux n'est pas un objectif de la première version. La technologie se choisit selon les performances, la mémoire, l'intégration Windows (fenêtres, raccourcis globaux, surveillance des fichiers, Explorer, processus), l'extraction documentaire, la maintenabilité et l'outillage de test. Electron n'est pas choisi par défaut ; toute architecture hybride doit être justifiée.

## 8. Architecture modulaire

Responsabilités clairement séparées : Application Windows, Launcher, Raccourcis, Search Engine, Name Index, Content Index, Ranking Engine, Indexation des applications, Directory Scanner, Indexing Queue, Content Extractors, File Watcher, Opportunistic Discovery, Resource Manager, SourceFileAccess, SeekbeamStorage, Database, Configuration, History, Dashboard, Logging, Installer.

L'interface ne dépend pas des détails internes des moteurs d'indexation. Les composants communiquent par des interfaces clairement définies.

## 9. Architecture des index

Deux index indépendants, aux cycles de vie distincts ; l'indexation des noms a toujours priorité.

* **Name Index** : noms, chemins, répertoires, extensions, tailles, dates, attributs, données de classement. C'est le moteur de la recherche rapide.
* **Content Index** : recherche plein texte, phrases, termes, opérateurs, extraits, filtres, classement.

## 10. Recherche

Instantanée, insensible à la casse et aux accents, multi-tokens, indépendante de l'ordre des tokens, sur mots partiels et chemins, tolérante aux fautes de frappe, avec suggestions. Optimisée pour l'interactif : les recherches obsolètes sont annulées, les résultats classés par pertinence.

## 11. Ranking

Modulaire, documenté et testé sur des jeux de données représentatifs. Critères possibles : correspondance exacte, nom, préfixe, token, partielle, chemin, proximité des tokens, extension, type, fréquence et date d'ouverture, sélection après recherche, historique. Les correspondances exactes gardent une priorité importante. L'apprentissage reste local.

## 12. Launcher

Accessible par double Ctrl et par raccourcis personnalisés ; fonctionne en arrière-plan ; fenêtre flottante, mode centré ou près du curseur ; configurable, réactif, entièrement navigable au clavier ; lance des applications et ouvre des fichiers. Interface minimaliste, qui ne doit pas devenir un environnement de travail complexe.

## 13. Indexation

Par files persistantes, au minimum une file des noms et une file du contenu. Les tâches sont persistantes, dédupliquées, interrompables, reprenables, priorisables et observables. Parcours en streaming (jamais tout un périmètre en mémoire), petits lots, transactions courtes.

## 14. Ressources

Le Resource Manager contrôle CPU, RAM, I/O, workers, taille des lots, débit et priorité. Modes Économie, Équilibré, Performance et Automatique (par défaut). Le mode Automatique s'adapte à l'activité système, au CPU, à la RAM, au disque, à l'activité utilisateur, aux recherches en cours, à l'alimentation et à l'application au premier plan. La recherche a toujours priorité ; l'indexation ralentit ou se suspend si nécessaire.

## 15. Gestion de millions de fichiers

Conçu pour plusieurs millions de fichiers. À éviter : listes massives ou collections globales en RAM, chargements complets, reconstructions inutiles, requêtes inefficaces, traitements O(n²), transactions longues, accès disque redondants. Benchmarks sur au moins un million de fichiers, et plusieurs millions quand c'est possible.

## 16. Découverte opportuniste

Quand un fichier est ouvert : identifier son chemin, vérifier s'il est indexé, vérifier le périmètre et les exclusions, examiner le répertoire parent ; s'il est inconnu, le mettre en file, explorer ses sous-répertoires, indexer les noms et mettre les fichiers éligibles dans la file de contenu. La découverte respecte les périmètres et ne provoque jamais d'indexation massive non contrôlée.

## 17. Liens et cycles

Chemins normalisés, déduplication, identifiants de fichiers et de volumes, détection des liens et des jonctions, profondeur maximale, limites de parcours. Aucun cycle ne doit provoquer de boucle infinie.

## 18. Surveillance des fichiers

Détecte création, modification, suppression, déplacement et renommage, et met à jour les index de manière incrémentale. Une réconciliation périodique corrige les événements manqués. Consommation de ressources maîtrisée.

## 19. Extraction documentaire

Extracteurs modulaires : identification du format, extraction du texte et des métadonnées, streaming si possible, respect des limites de ressources, gestion des erreurs, annulation.

Formats prioritaires : PDF, DOC, DOCX, RTF, TXT, Markdown, ODT, XLS, XLSX, CSV, ODS, PPT, PPTX, ODP, HTML, XML, JSON, YAML, SQL, code source, fichiers de configuration, logs.

## 20. Archives

ZIP est pris en charge ; d'autres formats selon les bibliothèques retenues. Limites de profondeur, de taille, de nombre de fichiers et de décompression ; protection contre les archives bombes ; extraction à la volée ; gestion des archives chiffrées.

## 21. Sécurité

Fonctionnement local, respect des permissions Windows, aucune transmission de données, pas de télémétrie par défaut, fichiers de configuration protégés, pas de données sensibles dans les journaux, fichiers inaccessibles gérés sans interrompre l'indexation.

## 22. Tests

Chaque fonctionnalité importante a des tests : unitaires, intégration, fonctionnels, performance, stabilité, reprise, sécurité lorsque pertinent. Ils sont exécutés régulièrement ; les défauts sont corrigés avant de construire des fonctionnalités qui en dépendent.

## 23. Benchmarks

Reproductibles et documentés ([docs/BENCHMARKS.md](docs/BENCHMARKS.md)) : démarrage, ouverture du launcher, temps de recherche, latence d'affichage, pertinence, mémoire, CPU, I/O, vitesse d'indexation, taille des index.

## 24. Qualité du code

Lisible, modulaire, testé, maintenable ; documenté lorsque nécessaire ; sans abstractions inutiles ni duplications. Erreurs traitées explicitement, opérations longues annulables, interfaces clairement définies, dépendances minimisées.

## 25. Documentation

Documents maintenus : README, architecture, choix technologiques, décisions techniques, guide de développement, guide utilisateur, installation, configuration, troubleshooting, benchmarks, release notes, `FILE_IMMUTABILITY_POLICY.md`, `FILE_IMMUTABILITY_AUDIT.md`.

### 25.1. Documentation bilingue (anglais + français)

* `X.md` : version anglaise (affichée par défaut sur GitHub) ; `X.fr.md` : version française, dans le même dossier ;
* la première ligne contient le sélecteur de langue (`**English** | [Français](X.fr.md)` / `[English](X.md) | **Français**`) ;
* les liens d'un document anglais pointent vers les versions anglaises, ceux d'un document français vers les versions `.fr.md`.

Toute création ou modification de documentation met à jour **les deux langues dans le même changement**. Un document n'est pas à jour si l'une des deux versions est en retard.

Exceptions (français uniquement) : `CLAUDE.md` et `docs/project/SPECIFICATIONS.md`. Textes juridiques en anglais uniquement : `LICENSE`, `THIRD-PARTY-NOTICES.md`.

### 25.2. Interface en quatre langues (anglais, français, allemand, italien)

L'interface est localisée (`src/Seekbeam.App/Localization/Strings*.resx`, anglais = langue neutre ; voir `docs/TRANSLATING.md`) :

* aucun texte visible écrit en dur : XAML `{l:Tr Cle}`, C# `L.T("Cle")` / `L.F("Cle", …)` ;
* toute clé ajoutée ou modifiée l'est **dans les quatre fichiers `.resx` dans le même changement** ;
* `Seekbeam.Core` renvoie des codes, jamais des phrases destinées à l'utilisateur ;
* les tests `LocalizationTests` doivent passer.

## 26. Cycle de travail

Analyser, concevoir, implémenter, tester, corriger, optimiser, documenter, construire, vérifier. Un plan, une architecture ou un prototype ne sont pas des livrables : une fonctionnalité est livrée quand elle fonctionne et qu'elle est testée.

## 27. Définition de « terminé »

Un changement est terminé lorsque : le build est sans erreur ni violation d'API interdite ; tous les tests passent (y compris les tests d'immutabilité) ; la documentation est à jour dans les deux langues ; les textes d'interface existent dans les quatre langues ; les benchmarks sont relancés si le changement touche un chemin critique.

## 28. Priorité absolue

Seekbeam doit être rapide, intelligent, fiable et discret, et aider l'utilisateur sans jamais devenir une charge pour son ordinateur. La pertinence, la performance, la robustesse et la faible consommation de ressources guident toutes les décisions.

## 29. Audit et garantie de non-modification des fichiers sources

La politique complète est dans [docs/FILE_IMMUTABILITY_POLICY.md](docs/FILE_IMMUTABILITY_POLICY.md) et le rapport de preuve dans [docs/FILE_IMMUTABILITY_AUDIT.md](docs/FILE_IMMUTABILITY_AUDIT.md). Les règles ci-dessous s'appliquent à tout le développement.

### 29.1. Objectif fondamental

Seekbeam ne modifie jamais les fichiers ou dossiers qu'il indexe. Cette exigence prime sur toute fonctionnalité, optimisation ou facilité d'implémentation, et doit être démontrée, pas seulement affirmée.

### 29.2. Périmètre de protection

Sont protégés : les fichiers des racines indexées et de leurs sous-dossiers (sauf exclusions), les fichiers découverts opportunistiquement, les fichiers dont le contenu est extrait ou analysé, et les fichiers contenus dans les archives. La protection tient compte des liens symboliques, jonctions, points de montage, chemins alternatifs et volumes différents ; ses limites sont documentées.

### 29.3. Politique stricte de lecture seule

Interdits sur les sources : écriture de contenu, suppression, renommage, déplacement, modification d'attribut, de timestamp, d'ACL ou de permissions, création de fichier (y compris temporaire), extraction d'archive dans les répertoires sources, modification indirecte, réparation ou transformation. Les sources s'ouvrent explicitement en lecture seule, avec les options Windows les plus restrictives adaptées. Une convention de programmation ou un réglage « read-only » dans l'interface ne suffit jamais comme protection.

### 29.4. Séparation architecturale

* **`SourceFileAccess`** : seul point d'accès aux sources ; ouverture en lecture seule, lecture de contenu et de métadonnées, énumération de dossiers. Aucune API de modification.
* **`SeekbeamStorage`** : seul point d'écriture, limité aux emplacements de Seekbeam (index, configuration, cache, logs, fichiers temporaires, données d'apprentissage, diagnostic), documentés et distincts des racines indexées.

Tout nouvel accès fichier passe par l'un de ces deux services.

### 29.5. Revue de code

Toute opération susceptible d'écrire, supprimer, déplacer, renommer ou modifier un fichier ou ses métadonnées, y compris dans les dépendances, wrappers, extracteurs, archives, caches, nettoyages, scripts et processus lancés, est identifiée et justifiée : emplacement, finalité, cible, possibilité d'atteindre une racine indexée, protections, risques.

### 29.6. Fonctionnalités incompatibles

Aucune fonctionnalité ne renomme, déplace, supprime ou modifie les attributs ou métadonnées des sources, même si elle figure dans le cahier des charges initial ; les fonctionnalités retirées sont documentées. Demander à Windows d'ouvrir un fichier dans une application externe est une action distincte : Seekbeam ne peut pas garantir ce que fait cette application, et cette limite est présentée clairement à l'utilisateur.

### 29.7. Vérification statique automatisée

Les API d'écriture sont interdites par analyse statique (BannedApiAnalyzers) et par une analyse IL des assemblys livrés ; les services d'écriture ne peuvent pas recevoir arbitrairement des chemins sources. Ces contrôles font partie du build et de la CI.

### 29.8. Tests de non-modification

Avant chaque scénario, un état de référence est établi (SHA-256, taille, attributs, timestamps, structure, existence, noms et chemins). Après le scénario (scan, indexation des noms et du contenu, archives, incrémental, surveillance, découverte opportuniste, réconciliation, suspension et reprise, arrêt normal ou forcé, reprise après crash, fichiers malformés, erreurs d'accès, liens et jonctions, gros fichiers), toute différence inattendue fait échouer le test.

### 29.9. Vérification dynamique indépendante

Le comportement réel est observé pendant l'exécution (outil `Seekbeam.FsAudit`, ETW, Process Monitor si besoin) et confronté à l'analyse du code. Outils, procédures et résultats sont documentés.

### 29.10. Archives et extracteurs

Les extracteurs ne modifient pas les originaux. Toute extraction ou transformation se fait en mémoire ou dans un répertoire temporaire de Seekbeam, nettoyé sans jamais toucher aux sources. Limites : taille, profondeur, nombre de fichiers, temps, mémoire, archives malformées, archives bombes, chemins malveillants.

### 29.11. Timestamps et effets du système

Distinguer les modifications faites par Seekbeam, les effets de bord du système (par exemple `LastAccessTime`) et les modifications par une application externe. Ne pas prétendre garantir ce que Windows ou le système de fichiers ne permet pas de contrôler.

### 29.12. Rapport de preuve

`docs/FILE_IMMUTABILITY_POLICY.md` décrit l'exigence, le périmètre, les opérations autorisées et interdites, l'architecture, les répertoires d'écriture, les méthodologies d'audit et de test, les outils, les limites et les critères d'acceptation. `docs/FILE_IMMUTABILITY_AUDIT.md` présente les modules examinés, les API d'écriture identifiées, les risques, les corrections, les résultats statiques, de test et dynamiques, les exceptions, les limites et une conclusion argumentée sur le niveau de confiance atteint, sans garantie absolue que la méthodologie ne permet pas de démontrer. Les deux sont mis à jour quand un changement touche aux accès fichiers.

### 29.13. Critères d'acceptation

L'exigence n'est satisfaite que si :

1. une politique de non-modification est documentée ;
2. l'architecture sépare les accès aux sources et les écritures propres à Seekbeam ;
3. les fichiers sources sont ouverts en lecture seule ;
4. aucune fonctionnalité Seekbeam ne modifie volontairement les sources ;
5. les fichiers temporaires sont créés hors des racines sources ;
6. les extracteurs ne modifient pas les originaux ;
7. les opérations incompatibles ont été supprimées ou désactivées ;
8. une revue de code complète a été effectuée ;
9. les contrôles statiques automatisés sont opérationnels ;
10. les tests de non-modification sont automatisés ;
11. les tests couvrent les scénarios principaux et les cas d'erreur ;
12. les résultats sont documentés ;
13. les limites sont explicitement reconnues ;
14. le rapport final est disponible.

### 29.14. Pour toute évolution

Un changement qui touche aux accès fichiers, aux extracteurs ou aux archives met à jour les contrôles statiques, les tests d'intégrité et le rapport d'audit dans le même changement. La protection des fichiers sources reste une exigence fondamentale pendant toute la suite du développement.
