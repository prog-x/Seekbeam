[English](INSTALLATION.md) | **Français**

# Installation

## Prérequis
Windows 10 (1809+) ou Windows 11, x64. Aucun runtime à installer : les paquets sont *self-contained* (.NET 10 inclus).

## MSI (recommandé)
- `Seekbeam-1.0.0-x64.msi` installe Seekbeam **pour l'utilisateur courant, sans élévation**, dans `%LOCALAPPDATA%\Programs\Seekbeam`.
- Il crée deux raccourcis dans le menu Démarrer (Seekbeam, Seekbeam — Recherche avancée) et le démarrage automatique (`HKCU\…\Run\Seekbeam`).
- En mode interactif, Seekbeam est lancé en fin d'installation.
- Silencieux : `msiexec /i Seekbeam-1.0.0-x64.msi /qn`. Désinstallation : `msiexec /x Seekbeam-1.0.0-x64.msi /qn`.
- Mise à jour : installer la nouvelle version (MajorUpgrade). La configuration et les index sont conservés, et le schéma est migré automatiquement.
- La désinstallation retire les binaires, les raccourcis et le démarrage automatique. Les données (`%LOCALAPPDATA%\Seekbeam`) et le profil du bac à sable de l'extracteur sont conservés volontairement. Pour les supprimer : `Seekbeam.exe --purge-data`, avant de désinstaller.

## Portable
Décompresser `Seekbeam-1.0.0-x64-portable.zip` n'importe où et lancer `Seekbeam.exe`. Le fichier `portable.txt` active le mode portable : les données vont dans `Data\` à côté de l'exécutable, et il n'y a pas de démarrage automatique. La seule chose que Seekbeam laisse hors de son dossier est le profil du bac à sable de l'extracteur (TD-17) : Windows crée `%LOCALAPPDATA%\Packages\seekbeam.extractorhost` et une entrée de registre sous `HKCU\Software\Classes\Local Settings`. `Seekbeam.exe --purge-data` les supprime avec les données.

Décompressez-le **hors des dossiers indexés par Seekbeam** (par exemple `C:\Outils\Seekbeam`, pas dans Documents ni sur le Bureau). Seekbeam ne modifie jamais les permissions d'un dossier indexé : depuis un tel emplacement, l'extracteur ne peut pas utiliser son bac à sable AppContainer et s'exécute en intégrité faible (voir [TROUBLESHOOTING.fr.md](TROUBLESHOOTING.fr.md)).

## Ligne de commande
| Option | Effet |
|---|---|
| `--background` | démarre sans afficher le launcher |
| `--deep "requête"` | ouvre la recherche avancée |
| `--settings`, `--dashboard` | ouvre la fenêtre correspondante |
| `--exit` | arrête proprement l'instance en cours |
| `--data <dossier>` | profil isolé (données dans ce dossier, sans démarrage automatique) |
| `--purge-data` | supprime index, historique, journaux, configuration et profil du bac à sable de l'extracteur (jamais vos fichiers) |

Lancer `Seekbeam.exe` alors qu'il tourne déjà affiche le launcher de l'instance existante.

## Signature
Les binaires de cette version ne sont pas signés. SmartScreen peut afficher un avertissement au premier lancement. Pour une diffusion, signer `Seekbeam.exe`, `Seekbeam.ExtractorHost.exe` et le MSI avec `signtool`.

## Construire les paquets
`pwsh build/Build-Release.ps1` (build, tests, publication ReadyToRun self-contained, MSI WiX v6 via `dotnet build`, zip portable) → `artifacts/installer/`.
