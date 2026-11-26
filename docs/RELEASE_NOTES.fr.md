[English](RELEASE_NOTES.md) | **Français**

# Notes de version

## Non publié

- **Nouveau : « aller au dossier » dans les boîtes Ouvrir / Enregistrer sous / choix de dossier et dans l'Explorateur** (TD-20). Appuyez deux fois sur Ctrl au-dessus de la boîte de dialogue de n'importe quelle application, ou commencez simplement à taper dans sa liste de fichiers : Seekbeam s'ouvre collé sous la boîte, vous tapez des bribes du chemin (`cli dup fact`) ou un chemin absolu, `Entrée`, et la boîte affiche ce dossier. Choisir un fichier ouvre son dossier et, dans une boîte Ouvrir, inscrit son nom dans la zone. Une boîte Enregistrer sous garde le nom que vous avez tapé. Seekbeam **ne valide jamais** la boîte : c'est vous qui confirmez. Quand rien n'est tapé, les dossiers ouverts dans l'Explorateur, puis les dossiers favoris et récents, sont proposés ; `Ctrl+G` saute au dossier de la fenêtre de l'Explorateur la plus en avant (Quick Switch). Un double Ctrl au-dessus d'une fenêtre de l'Explorateur pilote cette fenêtre. Pour les boîtes des applications lancées en administrateur et l'ancienne arborescence de choix de dossier, le chemin est copié dans le presse-papiers. Vérifié sur les vraies boîtes de Windows ([DIALOG_NAVIGATION_CHECK.fr.md](DIALOG_NAVIGATION_CHECK.fr.md)). Chaque partie se désactive dans le nouvel onglet de paramètres **Dialogues et dossiers**.

- **Nouveau : les dossiers hors de vos emplacements** (TD-21). Seekbeam indexe aussi les **noms des dossiers** de chaque disque fixe (jamais leurs fichiers ni leur contenu), ainsi que les dossiers que vous utilisez hors de l'index (partages réseau, clés USB), pour que vous puissiez les atteindre depuis n'importe quelle boîte de dialogue. Windows, les caches et les dossiers de build sont écartés ; Program Files, ProgramData et AppData sont classés plus bas. Parcours à basse priorité après vos emplacements (environ 1 minute et 0,3 cœur pour 94 000 dossiers en mode par défaut) ; les nouveaux dossiers sont suivis dès leur création. Dans le launcher normal, ils ne remplissent que les lignes libres (ou apparaissent avec `kind:folder`). Schéma du catalogue v3 (mis à niveau automatiquement).

- **Nouveau : bribes de chemin et chemins tapés.** Les mots tapés dans l'ordre du chemin sont mieux classés (`sou per see` → `C:\source\perso\Seekbeam`), le dernier mot désigne la cible, `src\seek` impose la hiérarchie, et un chemin absolu (`C:\Win`, `\\serveur\`, `%APPDATA%\`) est complété en direct depuis le disque.

- **Correction : plantage rare quand le moteur était fermé juste après son démarrage.** Une tâche différée en arrière-plan pouvait atteindre le catalogue fermé et terminer le processus ; elle vérifie désormais que le moteur est encore ouvert.

- **Nouveau : commandes Exécuter et alias.** Le launcher comprend ce que comprend `Win+R` : programmes du PATH (`mstsc`, `regedit`), applications enregistrées dans App Paths (`excel`), consoles (`services.msc`, ou simplement `services`) et modules du Panneau de configuration (`appwiz.cpl`), éventuellement suivis d'arguments (`mstsc /v:serveur`). Pour garder des résultats lisibles, les commandes n'apparaissent que pour leur nom exact ou un préfixe d'au moins 3 lettres. Les **alias** (Paramètres → nouvel onglet **Applications**) associent vos propres mots-clés à un programme, un fichier, un dossier ou une adresse, avec des arguments et, si besoin, les droits d'administrateur. Un alias `hosts` est fourni : il ouvre le fichier hosts dans le Bloc-notes en administrateur. Schéma de configuration 3 (`Apps.RunCommands`, `Apps.Aliases`).

- **Correctif : la fenêtre de recherche rapide restait ouverte après un clic ailleurs.** Le réglage « Fermer quand la fenêtre perd le focus » (actif par défaut) ne fonctionnait qu'après l'ouverture d'un menu contextuel sur un résultat. Un clic hors du launcher le ferme désormais, comme `Échap` ; un menu contextuel refermé par un clic hors de Seekbeam le ferme aussi.

- **Correctif : les fichiers d'un lecteur réseau déconnecté n'étaient jamais indexés par le contenu.** Quand l'indexation du contenu arrivait sur un fichier dont le lecteur ou le partage n'était pas connecté, le fichier était retiré de la file : il restait « en attente » avec une file vide, et seule une modification du fichier, ou une réindexation complète du contenu, le faisait revenir. Ses fichiers restent désormais dans la file et sont réessayés toutes les 5 minutes jusqu'au retour de l'emplacement ; ils ne comptent plus comme des échecs et le tableau de bord les affiche comme « reportés ». Les fichiers déjà perdus de cette façon sont remis en file automatiquement par le recensement du contenu (au plus une fois par heure). Un emplacement signalé « Indisponible » repasse à OK dès qu'il peut être lu.

- **Correctif : les résultats par le contenu pouvaient rester hors de vue dans le launcher.** Ils s'ajoutaient sous les résultats par nom : quand les noms remplissaient la liste, ils se retrouvaient après la dernière ligne, voire au-delà de la hauteur visible. Ils occupent désormais les lignes libres de la liste, en gardent les deux dernières lignes quand les noms la remplissent déjà (liste de 8 par défaut) et passent avant les correspondances approximatives de nom (≈). La liste ne dépasse jamais la taille configurée. Les erreurs d'affichage des résultats du launcher sont désormais journalisées au lieu d'être perdues silencieusement.

## 1.0.0 — 2026-11-22

Première version publique. Les fonctionnalités sont décrites plus bas ; cette première partie liste ce qui a changé depuis la version interne du 2026-11-21.

### Changements depuis la version interne du 2026-11-21
- **État d'indexation visible dans le launcher.** Chaque fichier affiché porte une pastille indiquant l'état de son contenu : ● indexé, ◌ en attente, ○ non indexé (la raison s'affiche au survol), ⚠ échec. Elle est chargée après l'affichage des résultats, sans ralentir la recherche. Pendant l'indexation des noms, un bandeau prévient que certains fichiers ne sont peut-être pas encore trouvables.
- **Le launcher cherche aussi dans le contenu.** Jusqu'à 4 documents dont le texte contient les mots tapés s'ajoutent sous les résultats par nom, avec l'extrait correspondant. Ils sont calculés après l'affichage des noms et abandonnés si la frappe continue. Auparavant, le launcher ne cherchait que dans les noms et les chemins : un PDF dont seul le texte contenait les mots tapés donnait 0 résultat.
- **La recherche avancée ne renvoie plus 0 résultat quand un mot manque ou diffère.** Si aucun document ne contient exactement tous les mots, elle essaie chaque mot comme début de mot (`code` → `codes`), puis les documents contenant une partie des mots, et la ligne d'état le signale. Les exclusions (`-mot`) et les phrases entre guillemets restent strictes.
- **Le tableau de bord explique pourquoi des fichiers n'ont pas de contenu indexé.** Le panneau « Index du contenu » répartit désormais tous les fichiers (indexés, sans texte, échecs, en attente, non éligibles), la somme étant égale au nombre de fichiers de l'index des noms. Le détail des non éligibles donne chaque raison et, pour les formats non pris en charge, les extensions les plus fréquentes. Le calcul est en lecture seule, à mémoire constante, en arrière-plan et cède le pas aux recherches (environ 1,7 s et quelques Mo pour 1,1 million de fichiers, voir [BENCHMARKS.fr.md](BENCHMARKS.fr.md)). « Ignorés (inchangés, non éligibles) » devient « Écartés par l'extracteur (inchangés ou devenus non éligibles) », et l'extracteur inactif est « en veille » au lieu d'« arrêté ».
- **Raccourcis saisis par capture.** Dans Paramètres › Général, on appuie sur la combinaison au lieu de la taper. Seekbeam vérifie aussitôt qu'elle est disponible (pas déjà prise par Windows ou une autre application, pas en double) et refuse les combinaisons qui bloqueraient la saisie (`Maj` seul + lettre). Nouvelles touches acceptées : flèches, pavé numérique, `Pause`.
- **Version et « À propos ».** Nouvel onglet Paramètres › À propos (version, lien vers le projet, licence MIT sans garantie, composants tiers, copie des informations de version) ; la version apparaît aussi dans l'infobulle et le menu de l'icône.
- **Correctif : état « au repos » prématuré.** Juste après le démarrage ou un changement de réglages, l'indexation des noms pouvait se dire « au repos, file vide » avant même d'avoir traité la demande en attente. Le bandeau du launcher, le tableau de bord et le recalcul de la répartition du contenu s'y fiaient. Une demande en attente compte désormais comme du travail en cours.
- **Correctif : exclusion d'un dossier du contenu.** Ré-exclure un dossier dont le contenu avait été indexé ne supprimait parfois pas le texte stocké : la nouvelle liste était appliquée après la revérification des fichiers. La revérification attend désormais que les nouvelles règles soient en place.
- **L'extraction s'exécute dans un bac à sable** (TD-17). `Seekbeam.ExtractorHost.exe` démarre désormais dans un AppContainer sans aucune capacité : ni accès à vos fichiers, ni réseau, ni écriture, ni registre, ni processus enfant. Seekbeam ouvre chaque document en lecture seule et lui transmet un handle qui ne permet que de lire ce fichier. Il se replie sur un processus en intégrité faible si l'AppContainer est indisponible ; le tableau de bord affiche le niveau utilisé. Mise en place unique au démarrage : un profil AppContainer et une permission de lecture sur le dossier du programme Seekbeam, jamais dans un emplacement indexé ; `--purge-data` supprime le profil.
- **Téléchargements est indexé par nom uniquement** (TD-18). Son contenu n'est plus lu par défaut, car les fichiers venus d'Internet et des e-mails seraient sinon analysés automatiquement. Nouveau réglage `Content.ExcludedFolders` (Paramètres › Contenu › « Dossiers indexés par nom uniquement ») ; retirer Téléchargements affiche le risque et demande une confirmation. Les configurations existantes sont migrées (schéma de configuration 2) et le texte déjà stocké pour Téléchargements est supprimé.
- **Dépendances** : Dependabot met aussi à jour le SDK .NET (le runtime embarqué dans les versions publiées) et regroupe à part les analyseurs de documents. L'audit NuGet porte sur les paquets directs et transitifs, et la CI échoue sur une vulnérabilité connue ([SECURITY.fr.md](../SECURITY.fr.md)).
- **Renommage de Findex en Seekbeam** avant la première publication : une autre application Windows de recherche locale de fichiers porte déjà le nom Findex. Exécutables, espaces de noms, installeur (nouveau code produit), dossier de données (`%LOCALAPPDATA%\Seekbeam`) et documentation utilisent le nouveau nom.
- **La profondeur maximale par défaut passe à 10** pour les nouveaux emplacements (elle était illimitée). Les emplacements existants gardent leur valeur enregistrée. Mettre `-1` pour illimitée.
- **Les dossiers coupés par la limite de profondeur sont signalés** :
  - dans le tableau de bord, une colonne et un encadré d'avertissement listent les dossiers concernés (double-clic pour en ouvrir un, bouton pour changer la limite) ;
  - une notification unique apparaît après l'indexation (un clic ouvre le tableau de bord) ;
  - un avertissement s'affiche dans Paramètres › Emplacements ;
  - le launcher l'indique quand une recherche ne trouve rien.
- Schéma du catalogue v2 (nouvelle table `depth_limited`, mise à niveau automatique).
- **Interface en anglais, français, allemand et italien.** Par défaut, Seekbeam suit la langue d'affichage de Windows (sinon l'anglais) ; Paramètres › Général › Langue permet de la forcer, et le launcher change immédiatement. Les nombres et les dates gardent le format régional de Windows. Traducteurs : voir [TRANSLATING.fr.md](TRANSLATING.fr.md).
- **Licence** : MIT (`LICENSE`), avec `THIRD-PARTY-NOTICES.md` pour les composants redistribués ; les deux sont inclus dans le MSI et le zip portable.
- **Installeur** : les noms et descriptions des raccourcis sont désormais en anglais (« Seekbeam — Advanced search »).
- **Correctif : arrêt lent.** Les workers d'indexation du contenu partageaient un même événement de réveil : à la fermeture, tous sauf un dormaient jusqu'à 15 s, et Seekbeam mettait environ 11 s à quitter. Les attentes se terminent maintenant aussi sur annulation : la fermeture prend environ 0,2 s.

### Fonctionnalités
- **Launcher** :
  - double Ctrl (gauche, droite ou les deux, délai réglable) et raccourcis personnalisés (RegisterHotKey) ;
  - fenêtre centrée ou près du curseur ;
  - navigation clavier complète et actions contextuelles non destructives ;
  - icônes et surlignage des correspondances.
- **Name Index** :
  - multi-tokens dans n'importe quel ordre, sous-chaînes, dossiers parents ;
  - insensible à la casse et aux accents (y compris NFD) ;
  - filtres `ext:`, `kind:`, `in:`, `-exclu`, `"phrase"` ;
  - fautes de frappe (bitap : substitution, insertion, suppression, transposition) ;
  - ranking documenté, avec apprentissage local plafonné.
- **Applications** : menu Démarrer, bureau, UWP/MSIX (shell:AppsFolder), dossiers d'exécutables ; parseur `.lnk` en lecture seule.
- **Indexation** :
  - NtQueryDirectoryFile, diff par répertoire ;
  - files persistantes priorisées, transactions courtes ;
  - recherche disponible dès les premiers fichiers ;
  - exclusions, profondeur, liens et cycles, permissions respectées.
- **Surveillance** : ReadDirectoryChangesW, coalescence, renommages en place, protection contre les rafales, réconciliation au démarrage et périodique.
- **Content Index** :
  - FTS5 (BM25, phrases, OR, NOT, préfixes), extraits, aperçu, filtres ;
  - extracteurs texte, HTML/XML, OOXML, ODF, RTF, PDF, DOC/XLS/PPT, ZIP (en mémoire, anti-bombe) ;
  - processus hôte isolé sous Job Object.
- **Découverte opportuniste** : Éléments récents de Windows et ouvertures depuis Seekbeam.
- **Ressources** :
  - modes Économie, Équilibré, Performance, Automatique ;
  - pause en plein écran et sur batterie ;
  - plages horaires ;
  - priorité absolue à la recherche ;
  - threads en mode arrière-plan Windows.
- **Interface** : tableau de bord, paramètres complets, thème clair ou sombre, tray.
- **Installation** : MSI par utilisateur sans élévation, édition portable, `--exit`, `--data`, `--purge-data`.

### Garantie d'immutabilité
- **Architecture** : séparation SourceFileAccess / SeekbeamStorage.
- **Contrôles statiques** : BannedApiAnalyzers et analyse IL.
- **Tests** : intégrité SHA-256 et métadonnées, dont arrêt forcé et reprise du vrai exécutable.
- **Audit dynamique** : FsAudit, plus un script ETW.

Voir `docs/FILE_IMMUTABILITY_AUDIT.fr.md`.

### Fonctionnalités volontairement absentes
Renommer, déplacer, supprimer et modifier attributs ou métadonnées (incompatibles avec la politique de non-modification).

### Limitations connues
- **Interface** : en français uniquement (localisation prévue).
- **OCR** : non inclus (désactivé, réservé).
- **Archives** : ZIP uniquement (pas de 7z ni de rar). Entrées chiffrées ignorées.
- **Anciens formats** : Excel 95 (BIFF5) et Word antérieur à 97 non supportés. Les PDF image n'ont pas de texte.
- **Scan initial** : pas de lecture MFT/USN (elle exigerait l'administrateur). Le scan initial passe par l'énumération de répertoires.
- **Détection des ouvertures** : elle repose sur les « Éléments récents ». Les applications qui ne s'y inscrivent pas ne déclenchent pas la découverte.
- **Frappe à grande échelle** : à 5 M entrées, la frappe de préfixes d'une ou deux lettres atteint 139 ms au p95 (affinage incrémental à implémenter).
- **Dossiers déplacés** : la profondeur stockée du sous-arbre d'un dossier déplacé n'est mise à jour qu'au rescan suivant.
- **Signature** : binaires non signés.

### Prochaines étapes
- **Localisation de l'interface** : français, anglais, allemand, italien, avec des fichiers de traduction faciles à compléter par des contributeurs externes.
- **Publication sur GitHub** : préparation du dépôt.

Voir [ROADMAP.fr.md](../ROADMAP.fr.md).
