[English](DIALOG_NAVIGATION_CHECK.md) | **Français**

# Seekbeam — « Aller au dossier » : vérification sur de vraies boîtes de dialogue

La façon dont Seekbeam pilote les boîtes Ouvrir, Enregistrer sous et de choix de dossier ainsi que les fenêtres de l'Explorateur est décrite dans [TECHNICAL_DECISIONS.fr.md](../TECHNICAL_DECISIONS.fr.md) (TD-20) et dans le [guide utilisateur](USER_GUIDE.fr.md#boîtes-de-dialogue-et-dossiers-aller-au-dossier). Cette page présente les résultats de `tools/Seekbeam.DialogCheck`, qui la vérifie sur de **vraies boîtes de dialogue Windows** avec le code même que livre le launcher.

## Ce que fait l'outil

1. Il crée une petite arborescence de test dans `%TEMP%` et en prend l'empreinte (noms, tailles, SHA-256).
2. Pour chaque sorte de boîte (Ouvrir, Enregistrer sous, sélecteur de dossier, ancienne arborescence), il ouvre une vraie boîte, vérifie que Seekbeam la reconnaît, la pilote (dossier choisi, fichier choisi, dossier qui n'existe plus, focus dans la liste de fichiers comme avec la saisie directe), alimente le routeur clavier (lettre dans la liste, lettre dans la zone « Nom du fichier », Ctrl+G), relit la barre d'adresse et la zone du nom, puis **ferme la boîte par Annuler** et contrôle son résultat : il doit être Annuler, jamais OK.
3. Il ouvre une fenêtre de l'Explorateur, liste les dossiers ouverts (Quick Switch), fait naviguer cette fenêtre et y sélectionne un fichier.
4. Avec `--app <Seekbeam.exe>` : il démarre la vraie application avec un profil isolé, appuie deux fois sur Ctrl au-dessus de la boîte, tape des bribes de chemin, puis un chemin absolu, puis utilise une boîte Enregistrer sous, chaque fois avec Entrée.
5. Il reprend l'empreinte de l'arborescence de test : rien ne doit avoir changé.

À lancer depuis une session interactive (les boîtes apparaissent quelques secondes à l'écran) :

```
dotnet build Seekbeam.sln
tools\Seekbeam.DialogCheck\bin\Debug\net10.0-windows\Seekbeam.DialogCheck.exe rapport.md --app src\Seekbeam.App\bin\Debug\net10.0-windows\Seekbeam.exe
```

Le code de sortie vaut 0 seulement si toutes les vérifications réussissent.

## Résultats

Tableau généré par l'outil (libellés en anglais ; « Adresse » est le texte de la barre d'adresse de Windows en français).

Run: 2026-11-26 17:10 · Microsoft Windows NT 10.0.26300.0 · UI culture fr-FR · 17,1 s

**36/36 checks passed.**

| Scenario | Check | Result | Detail |
|---|---|---|---|
| Open dialog | recognised as FileOpen | ✅ pass | FileOpen |
| Open dialog | typing in the file-name box is never taken over | ✅ pass | focus: Edit |
| Open dialog | keyboard router: a letter in the file-name box is left to the dialog | ✅ pass | taken=False |
| Open dialog | keyboard router: a letter in the file list opens Seekbeam, the next one is captured | ✅ pass | first='a', captured='b' |
| Open dialog | keyboard router: Ctrl+G asks for Quick Switch on this dialog | ✅ pass | taken=True |
| Open dialog | file list focused → recognised for direct typing | ✅ pass | focus: DirectUIHWND |
| Open dialog | navigate to a folder | ✅ pass | Done; address: Adresse : %TEMP%\…\Beta\Rapports |
| Open dialog | a file was chosen: its folder, and its name in the box (not validated) | ✅ pass | Done; box: 'notes.md' |
| Open dialog | a folder that no longer exists is refused before touching the dialog | ✅ pass | FolderMissing |
| Open dialog | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| Save As dialog | recognised as FileSave | ✅ pass | FileSave |
| Save As dialog | navigate (focus in the file list), the typed file name is kept | ✅ pass | Done; address: Adresse : %TEMP%\…\Beta\Rapports; box: 'keepme.txt' |
| Save As dialog | a file was chosen: the typed name still wins (no overwrite risk) | ✅ pass | Done; box: 'keepme.txt' |
| Save As dialog | dialog still open (never validated) | ✅ pass |  |
| Save As dialog | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| Folder picker | recognised as FolderPicker | ✅ pass | FolderPicker |
| Folder picker | navigate to a folder (focus in the file list) | ✅ pass | Done; address: Adresse : %TEMP%\…\Beta\Rapports |
| Folder picker | a file was chosen: its folder (no name in the box) | ✅ pass | Done; address: Adresse : %TEMP%\…\Alpha Projets |
| Folder picker | dialog still open (never validated) | ✅ pass |  |
| Folder picker | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| Old folder browser (SHBrowseForFolder) | recognised as LegacyFolderBrowser | ✅ pass | LegacyFolderBrowser |
| Old folder browser (SHBrowseForFolder) | not driven (the launcher copies the path instead) | ✅ pass | Unsupported |
| Old folder browser (SHBrowseForFolder) | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| Explorer window | open Explorer folders are listed (Quick Switch) | ✅ pass | %TEMP%\…\Alpha Projets |
| Explorer window | recognised | ✅ pass | Explorer |
| Explorer window | not taken over for direct typing | ✅ pass |  |
| Explorer window | navigate the same window (and select the file) | ✅ pass | navigated=True; now in %TEMP%\…\Beta\Rapports |
| Explorer window | Quick Switch target = front-most Explorer folder | ✅ pass | %TEMP%\…\Beta\Rapports |
| End to end (Seekbeam.exe) | double Ctrl over the dialog opens the launcher | ✅ pass |  |
| End to end (Seekbeam.exe) | launcher glued under the dialog | ✅ pass |  |
| End to end (Seekbeam.exe) | path fragments + Enter: the dialog goes to the folder | ✅ pass | Adresse : %TEMP%\…\Beta\Rapports |
| End to end (Seekbeam.exe) | absolute path completed from the disk + Enter | ✅ pass | Adresse : %TEMP%\…\Alpha Projets |
| End to end (Seekbeam.exe) | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| End to end (Seekbeam.exe) | Save As: goes to the folder, keeps the file name, not validated | ✅ pass | Adresse : %TEMP%\…\Beta\Rapports; box 'keepme.txt' |
| End to end (Seekbeam.exe) | closed by Cancel: the dialog was never validated | ✅ pass | Cancel |
| Immutability | test tree unchanged (names, sizes, SHA-256) | ✅ pass | 5 entries before, 5 after |

## Constats faits pendant la réalisation

- En laboratoire, un clic sur OK alors que la zone « Nom du fichier » n'avait pas été reconnue a **validé** la boîte Enregistrer sous (la boîte de test n'écrit rien) : la conception ne clique donc jamais sur OK et ne poste Entrée qu'avec le chemin d'un dossier existant terminé par `\`.
- Dans un sélecteur de dossier, OK sélectionne le dossier et ferme la boîte : Entrée, au contraire, navigue.
- Focus dans la liste de fichiers (le cas de la saisie directe), Entrée postée à la zone du nom était ignorée par la boîte Ouvrir : Seekbeam donne désormais d'abord le focus à la zone (`WM_NEXTDLGCTL`).
- L'exécution de bout en bout a montré une correspondance approximative sur le chemin entier d'une racine classée devant un fichier exact : la recherche approximative ne s'applique plus aux racines, et « dossiers d'abord » ne fait jamais passer un dossier approximatif devant un fichier exact (test de non-régression `ApproximateMatchesNeitherHitRootPathsNorPassExactFiles`).

## Non couvert par l'outil

- Boîtes d'applications lancées **en administrateur** : Windows bloque les messages d'un processus normal (UIPI). Seekbeam le détecte et copie le chemin dans le presse-papiers ; le vérifier demande une application de test élevée.
- Saisie directe par le vrai hook clavier : les touches injectées par un test sont ignorées à dessein (c'est ainsi qu'Échap rend les lettres), donc le routeur est alimenté directement ; le chemin du hook est le même que pour le double Ctrl, couvert de bout en bout.
- Boîtes à modèle personnalisé ou dessinées par des bibliothèques qui n'utilisent pas les boîtes communes de Windows (certaines applications Java, Qt ou Electron) : elles ne sont pas reconnues, et Seekbeam ouvre alors son launcher normal.
