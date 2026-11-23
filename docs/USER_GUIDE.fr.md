[English](USER_GUIDE.md) | **Français**

# Seekbeam — Guide utilisateur

## Premiers pas
Après l'installation, Seekbeam démarre en arrière-plan : une icône apparaît dans la zone de notification. Par défaut, il indexe votre dossier personnel (`C:\Users\<vous>`), sauf `AppData`, `.git`, `node_modules` et quelques caches. La recherche fonctionne **immédiatement**, et les résultats s'enrichissent à mesure que l'indexation progresse. Le contenu des documents est indexé ensuite, plus lentement et sans gêner votre travail.

**Appuyez deux fois rapidement sur Ctrl** pour ouvrir la recherche.

## Rechercher
- Tapez quelques lettres de n'importe quelle partie du nom, dans n'importe quel ordre : `fact 2024 client`.
- Majuscules et accents n'ont pas d'importance : `ete` trouve `Été`, et `previsionnel` trouve `prévisionnel`.
- Les noms des dossiers parents comptent aussi : `projets alpha notes` trouve `…\Projets\Alpha\notes.txt`.
- Les fautes de frappe sont tolérées (`raport`, `rapprot`) lorsque aucun résultat exact n'existe. Les résultats approximatifs sont marqués d'un `≈`. Les nombres ne sont jamais approximés.
- Une seule lettre cherche les noms qui commencent par cette lettre (ou dont un mot commence par elle).
- **Le contenu des documents aussi** : le launcher affiche aussi les documents dont le contenu indexé contient tous les mots (ou des mots qui commencent ainsi), avec le passage où ils apparaissent. Ils occupent les lignes libres de la liste ; si les résultats par nom la remplissent déjà, ils en prennent quand même les deux dernières lignes (avec les 8 lignes par défaut), et ils passent avant les correspondances approximatives de nom (≈). Un PDF dont le nom ne contient aucun des mots tapés est donc trouvé si son texte les contient. Ces résultats arrivent une fraction de seconde après les résultats par nom, qui ne sont jamais retardés. Pas pour les requêtes de moins de 3 caractères ni celles limitées par `kind:` / `in:` ; `ext:` est respecté. Pour « une partie des mots » et les autres filtres : recherche avancée (`Ctrl+D`).

Opérateurs : `"phrase exacte"`, `-motexclu`, `ext:pdf,docx`, `kind:file`, `kind:folder`, `kind:app`, `in:dossier`.

Le classement privilégie la correspondance exacte, le début du nom, les débuts de mots, les applications et vos éléments les plus utilisés. L'historique ne peut jamais dépasser une correspondance nettement meilleure.

**Indexé ou pas encore ?** Un fichier affiché dans les résultats a toujours son nom indexé. Une pastille à droite de chaque fichier indique l'état de son **contenu** (survolez-la pour le détail) :

| Pastille | Signification |
|---|---|
| ● (vert) | contenu indexé : son texte est trouvable par la recherche avancée (`Ctrl+D`) |
| ◌ (bleu) | en attente : pas encore extrait, ou modifié depuis la dernière extraction |
| ○ (gris) | non indexé, avec la raison : format non pris en charge, fichier vide ou trop volumineux, fichier OneDrive « en ligne uniquement », dossier indexé par nom uniquement, type exclu… ; ou document sans texte (PDF scanné) |
| ⚠ (orange) | échec de l'extraction (détail dans le tableau de bord) |

Les dossiers et les applications n'ont pas de pastille. Tant que l'indexation des noms est en cours, un bandeau en bas du launcher prévient que certains fichiers ne sont peut-être pas encore trouvables.

## Actions
`Entrée` ouvre l'élément ou lance l'application. `Tab` ou un clic droit affichent toutes les actions : ouvrir le dossier, copier le chemin ou le nom, copier le contenu indexé, propriétés, exécuter en tant qu'administrateur, ouvrir avec…, favori, exclure de l'index, réindexer.

> Seekbeam ne renomme, ne déplace, ne supprime et ne modifie jamais un fichier. Quand vous ouvrez un fichier, Windows le confie à l'application associée (Word, un éditeur…), qui peut le modifier comme d'habitude : cela ne dépend plus de Seekbeam.

## Commandes et alias
**Commandes Exécuter.** Tout ce que comprend la boîte Exécuter de Windows (`Win+R`) fonctionne aussi dans le launcher : programmes des dossiers du PATH (`mstsc`, `regedit`, `cmd`, `winver`…), applications enregistrées dans Windows (App Paths : `excel`, `chrome`…), consoles (`services.msc`, `devmgmt.msc` ; taper `services` suffit) et modules du Panneau de configuration (`appwiz.cpl`, `ncpa.cpl`).
- Une commande apparaît quand vous tapez son nom exact, ou ses 3 premières lettres au moins : `ms` n'affiche rien, `mst` affiche déjà `mstsc`. Les commandes ne tolèrent pas les fautes de frappe, pour que les centaines de noms système abrégés n'envahissent jamais les résultats.
- **Arguments** : tapez-les après la commande, comme dans `Win+R` : `mstsc /v:serveur01`, `ping 1.1.1.1`. La première ligne lance alors la commande avec ces arguments.
- La liste suit les changements du PATH et des programmes installés en 15 minutes au plus. Elle se désactive dans **Paramètres → Applications**.

Les **alias** sont vos propres mots-clés (**Paramètres → Applications → Alias**). Un alias lance un programme, un fichier, un dossier ou une adresse (`ms-settings:display`, `https://…`), avec des arguments fixes, un dossier de travail et, si besoin, les droits d'administrateur. Les variables d'environnement (`%SystemRoot%`, `%USERPROFILE%`…) sont acceptées. Le texte tapé après le mot-clé est ajouté aux arguments. Un alias dont le mot-clé est tapé exactement passe en premier, même avant une commande du même nom.

Seekbeam est livré avec un alias : **`hosts`** ouvre `%SystemRoot%\System32\drivers\etc\hosts` dans le Bloc-notes **en tant qu'administrateur**, car l'enregistrement de ce fichier l'exige ; Windows demande confirmation (UAC). Vous pouvez le modifier ou le supprimer comme tout autre alias.

> Comme pour « Ouvrir », Seekbeam demande seulement à Windows de lancer le programme. Ce que fait ensuite ce programme (le Bloc-notes qui enregistre le fichier hosts, par exemple) est votre action dans ce programme, pas celle de Seekbeam.

## Recherche avancée (plein texte)
`Ctrl+D` depuis le launcher, ou l'entrée « Recherche avancée » du menu Démarrer ou de l'icône.
- Le contenu de PDF, Word, Excel, PowerPoint, OpenDocument, RTF, HTML, texte, code et archives ZIP est cherché.
- Syntaxe : mots (tous requis), `"phrase exacte"`, `facture OR devis`, `-brouillon`, `préfixe*`.
- **Quand aucun document ne contient exactement tous les mots**, la recherche s'assouplit au lieu de ne rien renvoyer : d'abord chaque mot comme début de mot (`code` trouve aussi `codes`), puis les documents qui ne contiennent qu'une partie des mots, classés par pertinence. La ligne d'état indique l'étape utilisée (« mots trouvés comme débuts de mots », « résultats partiels »).
- Filtres : portée (noms et/ou contenu), types, emplacement, date de modification, taille.
- Le panneau de droite affiche un aperçu du texte indexé avec les termes surlignés. L'aperçu vient de l'index : le fichier n'est pas rouvert.

## Tableau de bord
Menu de l'icône → Tableau de bord : nombre d'éléments, files d'attente, vitesse, taille des index, ressources, état des emplacements, isolation de l'extracteur (« AppContainer » quand le bac à sable est pleinement actif), erreurs (fichiers inaccessibles, extractions échouées). Boutons : suspendre ou reprendre, réconcilier, réindexer les noms, réindexer le contenu, maintenance.

**Pourquoi tous les fichiers n'ont-ils pas leur contenu indexé ?** Le panneau « Index du contenu » répartit tous les fichiers de l'index des noms ; la somme est égale au nombre de fichiers :

```
Fichiers : 21 319
14 482 indexés · 312 sans texte · 84 échecs · 0 en attente · 6 441 non éligibles
▾ Non éligibles : 6 441
    Toujours trouvables par leur nom.
    format non pris en charge (nom seulement) : 5 230 (jpg 2 100, png 1 400, exe 600, …)
    dossier exclu de l'indexation du contenu : 640
    fichier vide : 410 · fichier trop volumineux : 98 · …
```

Les fichiers non éligibles ne sont pas en retard : les règles (format, taille, dossiers indexés par nom uniquement, types exclus, fichiers cloud…) les écartent volontairement, et ils restent trouvables par leur nom. Le calcul se fait en arrière-plan, à basse priorité, à l'ouverture du tableau de bord si le dernier date de plus de 10 minutes, à la fin de chaque indexation, ou avec le bouton « Actualiser ». Son heure s'affiche sous la répartition.

## Paramètres
Menu de l'icône → Paramètres, ou `Ctrl+,` dans le launcher. Voir [CONFIGURATION.fr.md](CONFIGURATION.fr.md) :
- **Général** : langue (anglais, français, allemand, italien, ou langue de Windows), démarrage, double Ctrl (gauche, droite ou les deux, délai), raccourci alternatif et raccourci de la recherche avancée, position, nombre de résultats, thème.
  - **Choisir un raccourci** : cliquez dans le champ, puis appuyez sur la combinaison voulue (ex. `Ctrl+Alt+F`). Seekbeam indique aussitôt si elle est disponible, déjà prise par Windows ou une autre application, ou inutilisable (il faut au moins `Ctrl`, `Alt` ou `Win` avec une touche de caractère, pour ne pas bloquer la saisie ailleurs). `Retour arrière` ou ✕ efface le raccourci. Pendant la saisie, les raccourcis de Seekbeam sont suspendus.
- **À propos** : version, lien vers le projet, licence (MIT, fournie « telle quelle », sans garantie), bouton pour copier les informations de version dans un rapport de bug. Aussi accessible depuis le menu de l'icône.
- **Emplacements** : dossiers et disques à indexer, exclusions, profondeur, contenu, fichiers cachés, liens.
  - **Profondeur maximale** (10 par défaut) : nombre de niveaux de dossiers explorés sous l'emplacement. Si des dossiers contiennent encore des éléments au-delà de cette limite, Seekbeam vous prévient : une notification unique après l'indexation, un avertissement dans le tableau de bord avec la liste des dossiers concernés, et une indication dans le launcher quand une recherche ne trouve rien. Augmentez la limite (ou mettez `-1` pour illimitée), ou ajoutez le dossier profond comme emplacement séparé.
- **Contenu** : limites d'extraction, archives et **dossiers indexés par nom uniquement**. Les fichiers de ces dossiers restent trouvables par leur nom, mais leur contenu n'est jamais lu. Le dossier Téléchargements figure dans cette liste par défaut : les fichiers venus d'Internet et des e-mails y arrivent, et indexer leur contenu reviendrait à les analyser automatiquement, même ceux que vous n'avez jamais ouverts. Si vous le retirez, Seekbeam explique d'abord le risque et demande une confirmation.
- **Applications** : sources d'applications (menu Démarrer, bureau, applications du Store, dossiers d'exécutables supplémentaires), commandes Exécuter et éditeur d'alias (voir [Commandes et alias](#commandes-et-alias)).
- **Ressources**, **Recherche et confidentialité**, **Surveillance**.

## Ressources
Le mode **Automatique** (par défaut) ralentit l'indexation quand vous travaillez, que le système est chargé ou que vous êtes sur batterie. Il la suspend pendant les jeux, les vidéos en plein écran et les présentations, et l'accélère quand l'ordinateur est inactif. Une recherche est toujours prioritaire.

## Confidentialité
Tout reste sur votre ordinateur : aucune télémétrie, aucun envoi. L'historique et l'apprentissage peuvent être désactivés, effacés ou réinitialisés depuis les paramètres. Les journaux ne contiennent jamais vos recherches ni le contenu de vos documents.

## Désinstaller
Paramètres Windows → Applications → Seekbeam → Désinstaller. Vos index sont conservés dans `%LOCALAPPDATA%\Seekbeam`. Pour tout supprimer, lancez `Seekbeam.exe --purge-data` avant de désinstaller, ou supprimez ce dossier.
