[English](CONFIGURATION.md) | **Français**

# Configuration

Fichier : `%LOCALAPPDATA%\Seekbeam\config.json` (ou `Data\config.json` en portable). Il est modifiable depuis la fenêtre Paramètres ; l'édition manuelle reste possible, Seekbeam fermé. Les valeurs sont validées et bornées au chargement. Un fichier invalide est sauvegardé sous `config.invalid.json` et remplacé par les valeurs par défaut. L'écriture est atomique (fichier temporaire puis remplacement).

| Section | Clé | Défaut | Description |
|---|---|---|---|
| General | Language | "" | langue de l'interface : `en`, `fr`, `de`, `it` ; vide = langue d'affichage de Windows (anglais si elle n'est pas prise en charge) |
| | StartWithWindows | true | démarrage automatique (HKCU Run ; ignoré en portable et avec `--data`) |
| | ShowTrayIcon | true | icône de notification |
| Hotkey | DoubleCtrlEnabled | true | double Ctrl |
| | CtrlSide | Both | Both / Left / Right |
| | DoubleTapMs | 400 | délai maximal entre les deux appuis (150–1000) |
| | AlternativeHotkey | "" | ex. `Alt+Space`, `Ctrl+Shift+F`, `Win+K` ; saisi par capture dans les Paramètres |
| | DeepSearchHotkey | "" | raccourci de la recherche avancée (même syntaxe) |
| Launcher | Position | Center | Center / NearCursor |
| | MaxResults | 8 | 1–50 |
| | Width | 720 | largeur (DIP) |
| | Topmost, CloseOnFocusLoss | true | comportement de la fenêtre |
| | Theme | System | System / Light / Dark |
| Search | FuzzyEnabled | true | tolérance aux fautes |
| | FuzzyMinTokenLength | 4 | longueur minimale d'un mot pour la tolérance |
| | MatchInPath | true | les mots peuvent correspondre aux dossiers parents |
| | IncludeHidden / IncludeSystem | false | afficher les fichiers cachés / système |
| | MaxSearchThreads | 0 | 0 = auto (cœurs/2, max 8) |
| | Weights.* | voir ARCHITECTURE §6 | pondérations du ranking |
| Roots[] | Path | profil utilisateur | racine indexée (dossier ou volume, ex. `D:\`) |
| | Enabled, IndexContent, IncludeHidden, Watch | true | |
| | FollowLinks | false | suivre symlinks et jonctions (protégé contre les cycles) |
| | MaxDepth | 10 | niveaux explorés sous la racine (ses enfants directs sont au niveau 1 ; -1 = illimitée). Les dossiers dont le contenu est coupé par cette limite sont signalés (tableau de bord, notification, paramètres) |
| | Excludes | AppData, .git, node_modules… | noms, motifs (`*.tmp`, `~$*`) ou chemins relatifs (`build\out`, `**\cache`) |
| GlobalExcludes | | `$Recycle.Bin`, `System Volume Information`, `*.tmp`, `~$*`, `Thumbs.db` | exclusions de toutes les racines |
| Content | Enabled | true | indexation du contenu |
| | MaxFileSizeMB | 50 | fichiers plus gros ignorés |
| | MaxCharsPerDocument | 1 000 000 | texte conservé par document |
| | StoreTextForSnippets | true | texte compressé pour les extraits et l'aperçu |
| | TimeoutSeconds | 30 | délai d'extraction par fichier |
| | DisabledExtractors | [] | `text`, `markup`, `ooxml`, `odf`, `rtf`, `pdf`, `legacy-office`, `zip` |
| | ExtraTextExtensions / ExcludedExtensions | [] | |
| | ExcludedFolders | [Téléchargements] | dossiers (et sous-dossiers) indexés **par nom uniquement** : leur contenu n'est jamais lu. Contient par défaut le dossier Téléchargements, par sécurité ([SECURITY.fr.md](../SECURITY.fr.md)) ; Paramètres › Contenu demande une confirmation avant de le retirer |
| | Sandbox | Auto | isolation de l'extracteur : `Auto` (AppContainer, sinon intégrité faible), `AppContainer`, `LowIntegrity`, `Off`. À ne modifier que pour un dépannage ; s'applique au redémarrage de l'hôte d'extraction |
| Content.Archives | Enabled, MaxDepth, MaxEntries, MaxEntrySizeMB, MaxTotalSizeMB, MaxCompressionRatio | true, 2, 10000, 50, 200, 100 | limites anti-bombe |
| Content.Ocr | Enabled | false | réservé (non inclus en 1.0) |
| Resources | Mode | Auto | Auto / Economy / Balanced / Performance |
| | PauseInFullscreen | true | pause en jeu, vidéo plein écran, présentation |
| | EconomyOnBattery / PauseContentOnBattery | true / false | |
| | UserIdleSeconds | 120 | inactivité avant accélération (Auto) |
| | ContentWorkers / ExtractorMemoryMB | 0 | 0 = selon le mode |
| | ContentTimeWindows | [] | ex. `["22:00-07:00"]` |
| Apps | StartMenu, Desktop, PackagedApps | true | sources d'applications |
| | ExecutableFolders | [] | dossiers d'exécutables supplémentaires |
| | RunCommands | true | commandes Win+R : dossiers du PATH, App Paths, .msc, .cpl |
| | Aliases | `hosts` | mots-clés de l'utilisateur (voir ci-dessous) |
| Privacy | QueryHistory, Learning | true | historique et apprentissage locaux |
| Watch | Enabled, ReconcileAtStartup | true | surveillance et réconciliation au démarrage |
| | ReconcileIntervalHours | 24 | réconciliation périodique |
| Discovery | Enabled, WatchRecentItems | true | découverte opportuniste (Éléments récents) |
| | MaxDirectoriesPerHour | 2000 | budget |
| Logging | Level, RedactPaths, MaxFiles | Info, false, 5 | journaux locaux tournants (5 × 5 Mo) |

### Alias
Chaque alias a un `Keyword` (mot-clé sans espace, unique), une `Target` (programme, fichier, dossier ou URI), et en option des `Arguments`, un `WorkingDirectory` et `RunAsAdmin`. Les variables d'environnement sont développées au lancement. Les entrées invalides (mot-clé ou cible vide, mot-clé contenant une espace, mot-clé en double) sont retirées au chargement de la configuration, avec un avertissement dans le journal.

```json
"Aliases": [
  { "Keyword": "hosts", "Target": "%SystemRoot%\\System32\\notepad.exe",
    "Arguments": "%SystemRoot%\\System32\\drivers\\etc\\hosts", "WorkingDirectory": "", "RunAsAdmin": true },
  { "Keyword": "rdp-labo", "Target": "mstsc.exe", "Arguments": "/v:labo01", "WorkingDirectory": "", "RunAsAdmin": false },
  { "Keyword": "affichage", "Target": "ms-settings:display", "Arguments": "", "WorkingDirectory": "", "RunAsAdmin": false }
]
```

Le schéma 3 ajoute une fois l'alias `hosts` aux configurations existantes ; si vous le supprimez, il ne revient pas.

## Profils de ressources
| Mode | Noms | Contenu | CPU de l'hôte (plafond noyau) | RAM de l'hôte |
|---|---|---|---|---|
| Économie | 2 000 entrées/s | 4 Mo/s, 1 worker | 10 % | 256 Mo |
| Équilibré | 20 000 entrées/s | 20 Mo/s, 1 worker | 25 % | 512 Mo |
| Performance | illimité | illimité, jusqu'à 4 workers | 60 % | 1 Go |
| Automatique | choisi selon CPU, RAM, inactivité, batterie et plein écran | | | |
