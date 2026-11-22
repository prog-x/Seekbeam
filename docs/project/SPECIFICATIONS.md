# Findex — Cahier des charges fonctionnel et technique

> **Document historique.** Cahier des charges initial du projet, rédigé quand il s'appelait encore **Findex** (renommé **Seekbeam** le 2026-11-21), conservé tel quel. L'état réel est décrit dans [ARCHITECTURE.md](../../ARCHITECTURE.md) et [PROJECT_STATUS.md](../../PROJECT_STATUS.md). Les fonctionnalités écartées pour garantir la non-modification des fichiers sources sont listées dans [docs/FILE_IMMUTABILITY_POLICY.md](../FILE_IMMUTABILITY_POLICY.md).

**Version :** 1.0  
**Statut :** Référence initiale du projet  
**Plateforme cible :** Windows (Windows 10/11, à confirmer selon les choix techniques et les API retenues)  
**Priorités :** rapidité, pertinence, faible consommation de ressources, confidentialité, robustesse et capacité à indexer plusieurs millions de fichiers.

---

## 1. Présentation du projet

Findex est une application Windows native de recherche locale et de lancement rapide, combinant les principales qualités de :

- **Listary** : lanceur et recherche de fichiers rapide, intégration au flux de travail Windows ;
- **Recoll** : recherche en texte intégral dans le contenu des documents ;
- **DocFetcher Pro** : indexation documentaire et recherche avancée.

Findex doit fournir une expérience de recherche immédiate, intuitive et clavier-first, tout en maintenant une indexation locale capable de gérer de très grands volumes de fichiers.

Le projet est Windows-only dans sa première version. Il ne doit pas être conçu autour d'une abstraction multiplateforme qui pénaliserait les performances ou l'intégration Windows.

## 2. Objectifs

### 2.1 Objectifs principaux

1. Rechercher et ouvrir rapidement fichiers, dossiers, applications et raccourcis.
2. Fournir un index de noms et chemins extrêmement rapide.
3. Proposer, en complément, une recherche plein texte dans les documents et fichiers compatibles.
4. Gérer des volumes de plusieurs millions de fichiers sans consommation excessive de mémoire.
5. Indexer les emplacements configurés de manière incrémentale et résiliente.
6. Offrir une interface simple, fluide, discrète et utilisable entièrement au clavier.
7. Fonctionner localement, sans dépendance à un service cloud.
8. Permettre à l'utilisateur de maîtriser les ressources utilisées et les emplacements indexés.

### 2.2 Non-objectifs de la première version

- Support multiplateforme.
- Service de recherche cloud.
- Synchronisation des index entre appareils.
- OCR activé par défaut.
- Indexation de contenus distants non explicitement configurés.
- Remplacement de l'Explorateur Windows.
- Gestion documentaire ou DAM complète.

Ces éléments pourront être envisagés ultérieurement si leur valeur est démontrée et s'ils ne compromettent pas les objectifs fondamentaux.

## 3. Principes de conception

- **Local-first et privacy-first** : index, requêtes, historique et apprentissage restent sur l'appareil.
- **Name Index prioritaire** : les noms et chemins doivent être disponibles avant le contenu.
- **Recherche indépendante de l'indexation** : la recherche ne doit jamais être bloquée par un scan ou une extraction.
- **Incremental-first** : éviter les scans complets inutiles.
- **Memory-conscious** : aucun chargement massif de fichiers ou de records en mémoire.
- **Streaming et batch** : traitements par lots bornés et flux.
- **Resilient** : queues persistantes, reprise après interruption et réconciliation.
- **Configurable** : périmètres, exclusions, types, ressources et comportement.
- **Modulaire** : moteurs d'indexation, extracteurs, recherche et intégration Windows séparés.
- **Measurable** : performances et consommation doivent être mesurées, non supposées.

## 4. Plateforme et intégration Windows

### 4.1 Cible

- Application Windows native.
- Windows 10/11 : compatibilité exacte à déterminer après recherche technique.
- Utilisation des API Windows lorsque cela améliore la performance, la fiabilité ou l'intégration.
- Installation classique et, si faisable sans compromis majeur, version portable.

### 4.2 Architecture technique

Claude Code doit rechercher, comparer et benchmarker les technologies adaptées. Les candidats peuvent inclure C#/.NET, WPF, WinUI 3/Windows App SDK, C++ ou Rust. Electron ne doit pas être retenu par défaut.

Les choix doivent être documentés dans `TECHNICAL_DECISIONS.md`, avec justification portant notamment sur :

- intégration Windows et APIs système ;
- performance et latence ;
- consommation mémoire ;
- indexation de très grands volumes ;
- maintenance et disponibilité des bibliothèques ;
- facilité de tests et de packaging ;
- stabilité et maturité ;
- coût de complexité.

L'architecture doit être modulaire, avec des frontières explicites entre interface, recherche, indexation, extraction, intégration Windows et infrastructure.

## 5. Lanceur et fenêtre de recherche

### 5.1 Activation globale

- Activation par double pression sur Ctrl, par défaut.
- Paramétrage du raccourci : Ctrl gauche, Ctrl droit, les deux, timing et raccourci alternatif.
- Le raccourci global doit être fiable et ne pas interférer inutilement avec les applications.
- Gestion des conflits et option de désactivation.

### 5.2 Fenêtre

- Fenêtre flottante, légère et rapidement affichée.
- Position par défaut centrée ou près du curseur ; choix configurable.
- Taille et comportement adaptés à une recherche rapide.
- Affichage du champ de recherche immédiatement prêt à recevoir du texte.
- Navigation clavier complète.
- Fermeture par Escape.
- Option de rester au premier plan, selon le comportement retenu.
- Ne pas imposer de fenêtre principale lourde pour lancer une recherche.

### 5.3 Résultats

- Nombre de résultats affichés configurable, valeur par défaut raisonnable (par exemple 8).
- Affichage compact : nom, type, chemin et éléments utiles.
- Mise en évidence des termes correspondants.
- Icône/type de fichier.
- Résultats actualisés à mesure que l'utilisateur saisit sa requête.
- Réactivité de l'interface indépendante des tâches d'indexation.

## 6. Recherche de fichiers et dossiers — Name Index

### 6.1 Périmètre

Indexation des noms et chemins de fichiers et dossiers dans les emplacements définis par l'utilisateur. L'indexation est récursive par défaut, avec options pour limiter la profondeur.

Les emplacements peuvent comprendre :

- dossiers locaux ;
- volumes locaux ;
- dossiers synchronisés explicitement configurés ;
- emplacements réseau explicitement configurés, si pris en charge et activés.

### 6.2 Fonctionnalités

- Recherche de fichiers et dossiers par nom.
- Recherche sur le nom et, selon le mode, sur le chemin.
- Recherche de tokens dans n'importe quel ordre.
- Correspondance partielle de tokens.
- Recherche insensible à la casse et aux accents.
- Fuzzy matching configurable.
- Tolérance configurable aux fautes de frappe.
- Requêtes composées.

Exemple de fichier :

`20261121_cahier_des_charges_videosurveillance_datacenter_Anthropic_Lucens.docx`

La requête `Ant 202611 cahier` doit pouvoir trouver ce fichier.

### 6.3 Performance

Le Name Index est la priorité absolue du projet.

Il doit être optimisé pour :

- millions de fichiers ;
- requêtes fréquentes ;
- faible latence ;
- indexation en arrière-plan ;
- mises à jour incrémentales ;
- faible utilisation RAM ;
- résultats partiels rapides, suivis d'un enrichissement éventuel.

L'architecture et le moteur doivent être validés par benchmark avant de bâtir toutes les fonctionnalités autour d'une hypothèse non testée.

## 7. Index des applications et raccourcis

Findex doit indexer et permettre de lancer :

- raccourcis du menu Démarrer ;
- raccourcis du bureau utilisateur et public ;
- applications enregistrées dans Windows ;
- applications UWP/MSIX, si leur lancement est pris en charge ;
- fichiers `.exe` dans des dossiers configurés ;
- autres emplacements d'applications configurés par l'utilisateur.

Les raccourcis `.lnk` doivent être résolus pour obtenir, autant que possible :

- nom affiché ;
- cible ;
- arguments ;
- répertoire de travail ;
- icône ;
- source ;
- informations utiles au lancement.

Les actions de lancement doivent respecter les mécanismes Windows, les permissions et UAC. Findex ne doit pas contourner les protections du système.

## 8. Classement et pertinence

Le classement doit privilégier les résultats les plus utiles.

Signaux possibles :

1. correspondance exacte ;
2. correspondance du début du nom ;
3. correspondance de tous les tokens ;
4. proximité et ordre des tokens ;
5. correspondance partielle ;
6. correspondance dans le chemin ;
7. type de fichier ;
8. fréquence d'ouverture ou de sélection ;
9. récence d'utilisation ;
10. favoris et préférences explicites.

La pondération doit être configurable ou ajustable par expérimentation. L'apprentissage local doit être désactivable et réinitialisable.

Le moteur ne doit pas laisser l'historique dominer une correspondance textuelle nettement meilleure. Le ranking doit être testable avec des jeux de requêtes et résultats attendus.

## 9. Configuration des emplacements

L'utilisateur doit pouvoir définir :

- racines à indexer ;
- inclusion/exclusion de sous-dossiers ;
- exclusions par nom, motif ou extension ;
- profondeur maximale ;
- types de fichiers ;
- emplacements réseau, si pris en charge ;
- dossiers synchronisés ;
- règles d'indexation du contenu ;
- paramètres de ressources.

La configuration doit être persistante et facilement modifiable. Les changements doivent être appliqués sans nécessiter une reconstruction complète de l'index lorsque cela est possible.

## 10. Deux index distincts

Findex doit séparer clairement :

### 10.1 Name Index

Index des noms, chemins et métadonnées de base nécessaires à la recherche rapide.

- Priorité de construction.
- Taille et mémoire maîtrisées.
- Mise à jour rapide.
- Recherche disponible avant l'indexation du contenu.
- Adapté aux millions de fichiers.

### 10.2 Content Index

Index secondaire de texte extrait des fichiers compatibles.

- Construction asynchrone.
- Gestion de priorité.
- Extraction modulaire.
- Indexation limitée par les paramètres de ressources.
- Possibilité de suspendre, reprendre ou exclure.
- Le contenu ne doit pas être nécessaire à la recherche de noms.

Les deux index doivent pouvoir être reconstruits séparément.

## 11. Indexation initiale et incrémentale

### 11.1 Initialisation

- Scan des racines configurées.
- Parcours récursif, contrôlé.
- Enregistrement des noms, chemins et métadonnées nécessaires.
- Détection des fichiers éligibles au Content Index.
- Mise en queue des extractions.
- Progression et statistiques.

### 11.2 Incremental

Détecter et gérer :

- création ;
- modification ;
- suppression ;
- renommage ;
- déplacement ;
- changements de configuration ;
- changements de racines.

Utiliser les mécanismes Windows appropriés (par exemple notifications de fichiers) lorsqu'ils sont fiables, complétés par des scans de réconciliation.

### 11.3 Résilience

- Queues persistantes.
- Reprise après fermeture/crash/redémarrage.
- Éviter la perte de tâches en cours.
- Détection des tâches bloquées.
- Retry avec limites et backoff.
- Erreurs visibles et diagnostiquables.
- Transactions courtes.
- Reconciliation périodique pour corriger les divergences.

## 12. Découverte opportuniste

Lorsqu'un utilisateur ouvre un fichier situé dans un périmètre configuré mais dans un dossier non encore connu/indexé :

1. détecter l'ouverture lorsque cela est possible et autorisé ;
2. ajouter le fichier au Name Index ;
3. découvrir son dossier parent ;
4. lancer la découverte récursive de ses sous-dossiers selon les règles ;
5. mettre les fichiers éligibles dans la queue du Content Index ;
6. ne pas perturber l'ouverture ni l'application source.

La découverte opportuniste doit respecter les racines, exclusions, limites de profondeur et paramètres de ressources. Elle ne doit pas devenir une méthode de surveillance invasive.

## 13. Gestion des liens et cycles

La traversée doit protéger contre les boucles et doublons causés par :

- symlinks ;
- junctions ;
- points de montage ;
- liens vers des parents ;
- chemins alternatifs ;
- volumes différents.

Mesures à évaluer et appliquer :

- normalisation des chemins ;
- identification des fichiers/dossiers par identifiants système et volume lorsque possible ;
- détection de visites ;
- limites de profondeur ;
- limites de traversée ;
- exclusions de liens configurable ;
- comportement explicite concernant les liens vers l'extérieur des racines.

Le parcours ne doit jamais se bloquer indéfiniment ni indexer des zones non autorisées par configuration.

## 14. Surveillance et réconciliation

- Surveillance des changements en arrière-plan.
- Éviter une dépendance exclusive aux watchers, qui peuvent perdre des événements.
- Scans de réconciliation configurables.
- Coalescence des événements rapprochés.
- Déduplication.
- Protection contre les rafales de changements.
- Traitement des événements avec priorité adaptée.
- Diagnostic des watchers et des scans.

## 15. Recherche plein texte — Content Index

### 15.1 Formats initiaux

Support à évaluer et implémenter par extracteurs modulaires pour les formats courants :

- PDF ;
- DOC/DOCX ;
- RTF ;
- TXT ;
- Markdown ;
- ODT ;
- XLS/XLSX ;
- CSV ;
- ODS ;
- PPT/PPTX ;
- ODP ;
- HTML/XML ;
- JSON/YAML ;
- CSS, JS, TS, SQL ;
- fichiers source et configuration ;
- logs.

L'extraction doit être robuste face aux fichiers malformés, encodages variés et contenus volumineux.

### 15.2 Recherche

- recherche de termes dans le contenu ;
- phrases exactes ;
- AND/OR/NOT, selon le moteur retenu ;
- filtres de type, emplacement, date, taille ;
- snippets/contextes ;
- classement des résultats ;
- recherche combinée nom + contenu ;
- possibilité de privilégier le Name Index.

### 15.3 Extraction modulaire

Chaque extracteur doit être isolé et pouvoir être :

- activé/désactivé ;
- versionné ;
- testé ;
- remplacé ;
- limité en temps, mémoire et taille de contenu.

Un échec d'extraction ne doit pas interrompre l'indexation globale.

## 16. Archives

Le support des archives est optionnel et doit être évalué selon son coût.

ZIP est prioritaire. Autres formats possibles selon les bibliothèques et résultats de benchmark.

Protection contre :

- archive bombs ;
- compression excessive ;
- profondeur d'archives imbriquées ;
- nombre de fichiers ;
- fichiers trop volumineux ;
- chemins malveillants ;
- erreurs d'extraction.

L'indexation des archives doit être configurable et limitée. Ne pas extraire systématiquement toutes les archives sur disque.

## 17. OCR

- OCR facultatif.
- Désactivé par défaut.
- Configurable par type, taille, emplacement ou demande explicite.
- Ressources et temps limités.
- Résultats identifiés comme provenant de l'OCR.
- Ne pas faire de l'OCR une dépendance du fonctionnement de base.

## 18. Prévisualisation

Lorsque faisable, afficher :

- snippet du texte ;
- contexte autour des termes ;
- informations du fichier ;
- aperçu simple des formats compatibles.

La prévisualisation doit être rapide, sécurisée et isolée des tâches lourdes. Elle ne doit pas ralentir le launcher.

## 19. Actions sur les résultats

Actions contextuelles et raccourcis :

- ouvrir ;
- ouvrir le dossier contenant le fichier ;
- copier le chemin complet ;
- copier le nom ;
- copier le contenu, lorsque pertinent ;
- afficher les propriétés ;
- exécuter ;
- exécuter en tant qu'administrateur, via mécanismes Windows ;
- ajouter/retirer des favoris ;
- exclure de l'index ;
- réindexer ;
- ouvrir avec une autre application.

Les actions destructives et de modifications ne sont pas autorisées via ce logiciel.

## 20. Historique et apprentissage

- Historique des requêtes optionnel.
- Historique local.
- Apprentissage des sélections/ouvertures, si activé.
- Favoris.
- Possibilité de désactiver l'apprentissage.
- Effacement de l'historique.
- Réinitialisation du profil d'apprentissage.
- Aucun envoi à un serveur.

## 21. Gestion des ressources

La gestion des ressources est un objectif fonctionnel majeur.

### 21.1 Modes

Prévoir au minimum :

- **Économie** : très faible activité, priorité à l'interactivité ;
- **Équilibré** : comportement par défaut, indexation progressive ;
- **Performance** : indexation plus rapide, consommation accrue autorisée.

Un mode **Auto** adaptatif peut être proposé : ajuster workers, débit et priorité selon la charge, l'activité utilisateur, la batterie et les ressources disponibles.

### 21.2 Paramètres

- nombre de workers ;
- priorité CPU ;
- limites de CPU ou débit lorsque techniquement réalisable ;
- limite RAM ;
- débit disque ;
- pauses pendant l'activité utilisateur ;
- comportement sur batterie ;
- fenêtres horaires ;
- suspension/reprise ;
- priorité des queues ;
- limites de taille de fichier ;
- timeout d'extraction.

### 21.3 Priorités de traitement

1. recherche interactive ;
2. mise à jour des noms et métadonnées ;
3. événements incrémentaux ;
4. découverte opportuniste ;
5. extraction de contenu ;
6. tâches de maintenance.

## 22. RAM et performance

Findex doit gérer plusieurs millions de fichiers sans chargement massif en mémoire.

Exigences :

- streaming ;
- pagination/cursor ;
- batchs bornés ;
- transactions courtes ;
- queues persistantes ;
- cache limité et configurable ;
- index disque ;
- éviter les collections géantes en mémoire ;
- éviter de charger le contenu intégral de fichiers ;
- limiter les buffers et les tâches concurrentes ;
- surveiller fuites et fragmentation ;
- benchmarks de consommation.

Les objectifs mémoire doivent être mesurés et documentés. Cibles indicatives à valider :

- application au repos : idéalement environ 100–150 MB ou moins ;
- indexation équilibrée : viser environ 300–500 MB ou moins, hors cas particuliers ;
- aucun comportement qui augmente sans limite avec le nombre de fichiers ;
- pas de consommation multi-gigaoctets dans les scénarios normaux.

Ces cibles sont des objectifs de benchmark, non des garanties absolues avant validation.

## 23. Stockage et index

- Stockage local.
- Taille de l'index mesurée et affichée.
- Index de noms et index de contenu séparables.
- Base et index protégés contre corruption.
- Sauvegarde ou reconstruction possible.
- Migration de schéma.
- Compactage/maintenance contrôlés.
- Éviter de dupliquer inutilement le contenu des documents.
- Documenter le coût de stockage par volume de fichiers.

Les technologies candidates (SQLite/FTS, index inversé spécialisé, moteur hybride, etc.) doivent être comparées et validées par benchmark.

## 24. Réseau et cloud

- Aucun service cloud requis.
- Aucun contenu, nom de fichier, chemin, requête ou historique transmis à des tiers.
- Les dossiers réseau ou synchronisés ne sont indexés que si l'utilisateur les configure explicitement.
- Comportement configurable lorsque la source est indisponible.
- Ne pas maintenir des connexions réseau inutiles.

## 25. Confidentialité et sécurité

- Local-first.
- Télémetrie désactivée par défaut ; idéalement aucune télémétrie dans la première version.
- Aucun envoi de données de recherche.
- Contrôle des racines et exclusions.
- Protection des index et données de profil selon les permissions Windows.
- Respect des permissions des fichiers.
- Ne pas contourner ACL, UAC ou restrictions d'accès.
- Traitement prudent des extracteurs et formats non fiables.
- Limites anti-DoS sur les fichiers et archives.
- Gestion des erreurs sans exposer de données sensibles dans les logs.
- Logs locaux configurables et purge possible.

## 26. Paramètres utilisateur

Une interface de configuration doit permettre de régler :

- raccourci global ;
- activation au démarrage ;
- position de la fenêtre ;
- thème et apparence, si disponibles ;
- nombre de résultats ;
- fuzzy matching ;
- ranking ;
- racines d'indexation ;
- exclusions ;
- types de fichiers ;
- indexation de contenu ;
- extracteurs ;
- archives ;
- OCR ;
- ressources ;
- historique et apprentissage ;
- surveillance et réconciliation ;
- logs ;
- comportement de l'application.

Les réglages doivent être persistants, validés et documentés.

## 27. Dashboard et diagnostics

Afficher au minimum :

- état de l'indexation ;
- fichiers/dossiers indexés ;
- taille du Name Index ;
- taille du Content Index ;
- tâches en attente ;
- vitesse d'indexation ;
- progression ;
- erreurs ;
- CPU/RAM/disque consommés ;
- workers actifs ;
- dernière réconciliation ;
- état des racines ;
- commandes pause, reprise, réindexation et maintenance.

Les diagnostics doivent aider à comprendre les problèmes sans exposer inutilement le contenu des fichiers.

## 28. Installation, démarrage et mise à jour

- Installateur Windows.
- Désinstallation propre.
- Option de lancement au démarrage.
- Gestion des mises à jour à définir.
- Préservation ou suppression configurable des index lors de la désinstallation.
- Version portable si faisable.
- Signature des binaires à considérer.
- Configuration et index placés dans des emplacements Windows appropriés.

## 29. Tests et validation

### 29.1 Tests automatisés

- unitaires ;
- intégration ;
- recherche et ranking ;
- indexation ;
- extracteurs ;
- gestion des erreurs ;
- changements de fichiers ;
- queues et reprise ;
- configuration ;
- sécurité et limites.

### 29.2 Dataset

Créer un générateur de données de test comprenant :

- 100 000 fichiers ;
- 500 000 fichiers ;
- 1 million ;
- 2 millions et davantage si matériel disponible.

Variété :

- noms courts/longs ;
- tokens multiples ;
- accents ;
- caractères Unicode ;
- extensions ;
- chemins profonds ;
- doublons ;
- collisions de noms ;
- fichiers de tailles variées ;
- fichiers malformés ;
- liens et cycles ;
- dossiers exclus ;
- archives ;
- changements pendant l'indexation.

### 29.3 Benchmarks

Mesurer et documenter :

- latence de recherche (p50, p95, p99) ;
- pertinence ;
- démarrage ;
- temps de scan ;
- vitesse d'indexation ;
- débit de mise à jour ;
- RAM au repos et en charge ;
- CPU ;
- I/O ;
- taille des index ;
- reprise après interruption ;
- stabilité sur scans prolongés ;
- comportement lors de rafales d'événements.

## 30. Critères d'acceptation

Findex ne sera considéré comme prêt que si :

1. l'application s'installe et se lance proprement sous Windows cible ;
2. le launcher global fonctionne de manière fiable ;
3. la recherche de noms est utilisable avant la fin de l'indexation du contenu ;
4. les requêtes multi-tokens dans n'importe quel ordre sont correctement gérées ;
5. la recherche tolère casse, accents et correspondances partielles ;
6. le ranking est testé sur un dataset représentatif ;
7. les applications et raccourcis Windows sont recherchables et lançables ;
8. les racines, exclusions et règles sont respectées ;
9. les mises à jour incrémentales fonctionnent ;
10. les queues survivent aux interruptions ;
11. les cycles de liens sont détectés ou limités ;
12. les fichiers pris en charge peuvent être recherchés en plein texte ;
13. les extracteurs échouent de façon isolée ;
14. les modes de ressources fonctionnent et sont mesurables ;
15. le volume de RAM ne croît pas linéairement avec le nombre de fichiers indexés ;
16. les benchmarks sont documentés ;
17. les tests automatisés sont exécutables ;
18. les logs et diagnostics permettent d'investiguer les erreurs ;
19. la confidentialité locale est vérifiée ;
20. la documentation utilisateur et développeur est fournie.

Les seuils chiffrés de latence et de débit devront être définis après benchmark initial, puis figés dans les critères de performance du projet.

## 31. Phases de développement recommandées

1. Analyse de l'environnement et recherche technique.
2. Benchmark de technologies et moteurs d'index.
3. Architecture et décisions documentées.
4. Base Windows, application et configuration.
5. Indexation des applications et raccourcis.
6. Prototype et benchmark du Name Index à grande échelle.
7. Name Index complet et recherche interactive.
8. Surveillance et indexation incrémentale.
9. Ressources, queues et résilience.
10. Découverte opportuniste et gestion des liens.
11. Content Index et extracteurs.
12. Recherche avancée, filtres et snippets.
13. Actions et prévisualisation.
14. Dashboard et diagnostics.
15. Installation, packaging et documentation.
16. Tests, benchmarks, optimisation et validation finale.

Les phases peuvent être réorganisées selon les résultats de recherche et les risques, mais le Name Index à grande échelle reste prioritaire.

## 32. Livrables

- Application Findex fonctionnelle.
- Code source.
- Architecture documentée.
- Décisions techniques documentées.
- Plan de développement et état du projet.
- Plan de performance.
- Analyse des risques.
- Tests automatisés.
- Générateur de dataset.
- Résultats de benchmarks.
- Installateur.
- Guide utilisateur.
- Guide développeur.
- Documentation des paramètres et limites.
- Procédure de build, test et release.

## 33. Autonomie de développement

Claude Code est chargé de mener le projet avec un haut niveau d'autonomie :

- inspecter le dépôt et l'environnement ;
- effectuer les recherches nécessaires ;
- comparer les technologies ;
- définir l'architecture ;
- documenter les décisions ;
- implémenter ;
- tester ;
- corriger ;
- benchmarker ;
- optimiser ;
- produire les livrables.

Il doit demander l'intervention humaine uniquement pour les décisions réellement bloquantes, permissions manquantes, actions destructives ou décisions majeures difficilement réversibles. Il doit présenter son plan initial, puis poursuivre sans attendre une validation, conformément à `PROMPT_LAUNCH.md`.

## 34. Directive finale

Findex doit être conçu comme un outil de recherche Windows fiable, rapide et discret, et non comme un simple prototype.

**La priorité absolue est une recherche de noms ultra-rapide, pertinente et capable de gérer plusieurs millions de fichiers avec une consommation maîtrisée.** L'indexation du contenu doit enrichir cette expérience sans la ralentir ni compromettre la stabilité.

Toute décision technique doit être évaluée au regard de ces objectifs, mesurée lorsque c'est possible et documentée.
