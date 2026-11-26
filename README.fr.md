[English](README.md) | **Français**

# Seekbeam

Recherche locale ultra-rapide, lanceur d'applications et indexation documentaire pour Windows : l'essentiel de Listary, Recoll et DocFetcher, en natif, 100 % local, **sans jamais modifier vos fichiers**.

> **Langues de l'interface** : anglais, français, allemand et italien. Seekbeam suit la langue d'affichage de Windows (l'anglais si ce n'est pas l'une de ces langues), et on peut la changer dans Paramètres › Général › Langue. Les traductions sont de simples fichiers `.resx` : voir [docs/TRANSLATING.fr.md](docs/TRANSLATING.fr.md) pour en corriger une ou ajouter une langue.

## Démarrage rapide

1. **Installer** `Seekbeam-1.1.0-x64.msi` (par utilisateur, sans droits administrateur), ou décompresser `Seekbeam-1.1.0-x64-portable.zip` et lancer `Seekbeam.exe`.
2. **Appuyer deux fois sur Ctrl** pour ouvrir la recherche.
3. **Taper** quelques lettres de n'importe quelle partie du nom, dans n'importe quel ordre.
4. **Entrée** ouvre le fichier ou lance l'application.
5. **Plein texte** : `Ctrl+D` dans le launcher pour chercher dans le contenu des documents.

## Fonctionnalités

- **Double Ctrl** : la fenêtre de recherche s'ouvre instantanément (centrée ou près du curseur).
- **Noms et chemins** : mots dans n'importe quel ordre, morceaux de mots, insensible à la casse et aux accents, tolérant aux fautes de frappe. `Ant 202611 cahier` trouve `20261121_cahier_des_charges_…_Anthropic_Lucens.docx`.
- **Applications** : menu Démarrer, bureau, applications du Store (UWP/MSIX), exécutables de dossiers choisis.
- **Commandes et alias** : commandes de Win+R (`mstsc`, `regedit`, `services.msc`, avec arguments : `mstsc /v:serveur`) et vos propres mots-clés (`hosts` ouvre le fichier hosts dans le Bloc-notes en administrateur).
- **Aller au dossier dans n'importe quelle boîte Ouvrir / Enregistrer** : double Ctrl au-dessus de la boîte (ou tapez simplement dans sa liste de fichiers), tapez des bribes du chemin (`cli dup fact`) ou un chemin absolu, Entrée : la boîte affiche ce dossier, comme avec Listary. Fonctionne aussi dans l'Explorateur, avec Quick Switch (`Ctrl+G` : dossier de la dernière fenêtre de l'Explorateur). Seekbeam se contente de naviguer, il ne valide jamais la boîte. Les noms des dossiers de chaque disque fixe sont indexés pour cela (jamais les fichiers ni le contenu). Voir le [guide utilisateur](docs/USER_GUIDE.fr.md#boîtes-de-dialogue-et-dossiers-aller-au-dossier).
- **Plein texte** :
  - formats : PDF, Word (DOC/DOCX), Excel (XLS/XLSX), PowerPoint (PPT/PPTX), OpenDocument, RTF, HTML/XML, JSON/YAML/CSV, code source, logs, archives ZIP ;
  - requêtes : phrases exactes, `OR`, exclusions ;
  - filtres de type, d'emplacement, de date et de taille, avec extraits et aperçu.
- **Des millions de fichiers** : sur 1 M d'entrées, une requête se mesure en ms au p95 et l'application consomme environ 40 à 110 Mo de RAM privée (voir [docs/BENCHMARKS.fr.md](docs/BENCHMARKS.fr.md)).
- **Discret** :
  - indexation adaptative en quatre modes (Économie, Équilibré, Performance, Auto) ;
  - pause en plein écran, sur batterie et pendant une recherche ;
  - l'extraction tourne dans un processus séparé dont la RAM et le CPU sont plafonnés par le noyau.
- **Lecture seule prouvée** : analyse statique de l'IL, tests SHA-256 et métadonnées, audit dynamique indépendant (voir [docs/FILE_IMMUTABILITY_AUDIT.fr.md](docs/FILE_IMMUTABILITY_AUDIT.fr.md)).
- **Confidentialité** : aucune télémétrie, aucune connexion réseau. Index, historique et apprentissage restent sur la machine.

## Installation

- **MSI** (par utilisateur, sans droits administrateur) : `Seekbeam-1.1.0-x64.msi`.
- **Portable** : décompresser `Seekbeam-1.1.0-x64-portable.zip` et lancer `Seekbeam.exe` (les données sont stockées dans `Data\` à côté de l'exécutable).

Détails : [docs/INSTALLATION.fr.md](docs/INSTALLATION.fr.md). Guide utilisateur : [docs/USER_GUIDE.fr.md](docs/USER_GUIDE.fr.md).

## Raccourcis du launcher

| Touche | Action |
|---|---|
| Ctrl, Ctrl | ouvrir / fermer |
| ↑ ↓ PgUp PgDn | naviguer |
| Entrée | ouvrir / lancer |
| Ctrl+Entrée | ouvrir le dossier contenant |
| Maj+Entrée | exécuter en tant qu'administrateur |
| Alt+Entrée | propriétés |
| Tab / clic droit | menu d'actions |
| Ctrl+C / Ctrl+Maj+C | copier le chemin / le nom |
| Ctrl+F | favori |
| Ctrl+D | recherche avancée (plein texte) |
| Ctrl+, | paramètres |
| Échap | fermer |
| Ctrl, Ctrl au-dessus d'une boîte Ouvrir / Enregistrer | aller au dossier dans cette boîte (Entrée : y aller ; Ctrl+G : dernier dossier de l'Explorateur ; Échap : retour) |

**Syntaxe** :
- `mot1 mot2` : tous les mots, en sous-chaîne ;
- `"phrase exacte"` : les mots doivent apparaître ensemble ;
- `-exclu` : écarte les noms contenant ce mot ;
- `ext:pdf,docx` : filtre par extension ;
- `kind:file|folder|app` : filtre par type ;
- `in:nomdedossier` : limite la recherche à un dossier.

## Construire depuis les sources

Prérequis : Windows 10/11 x64 et le SDK .NET 10 (10.0.4xx). Aucune autre installation.

```powershell
dotnet build Seekbeam.sln -c Release
dotnet test Seekbeam.sln                     # 180+ tests (unitaires, intégration, extracteurs, immutabilité)
pwsh build/Build-Release.ps1               # publish self-contained + MSI + zip portable dans artifacts/installer
pwsh tools/audit/Run-DynamicAudit.ps1      # audit dynamique d'immutabilité (sans droits administrateur)
```

## Documentation

| Document | Contenu |
|---|---|
| [ARCHITECTURE.fr.md](ARCHITECTURE.fr.md) | architecture, index, algorithmes, ranking |
| [TECHNICAL_DECISIONS.fr.md](TECHNICAL_DECISIONS.fr.md) | décisions et alternatives |
| [IMPLEMENTATION_PLAN.fr.md](IMPLEMENTATION_PLAN.fr.md) · [PROJECT_STATUS.fr.md](PROJECT_STATUS.fr.md) | plan et état |
| [ROADMAP.fr.md](ROADMAP.fr.md) | évolutions prévues et préparation de la publication GitHub |
| [PERFORMANCE_PLAN.fr.md](PERFORMANCE_PLAN.fr.md) · [docs/BENCHMARKS.fr.md](docs/BENCHMARKS.fr.md) · [spike](docs/benchmarks/2026-11-21_spike_name_engine.fr.md) | objectifs et mesures |
| [RISKS.fr.md](RISKS.fr.md) | risques et mitigations |
| [docs/FILE_IMMUTABILITY_POLICY.fr.md](docs/FILE_IMMUTABILITY_POLICY.fr.md) · [docs/FILE_IMMUTABILITY_AUDIT.fr.md](docs/FILE_IMMUTABILITY_AUDIT.fr.md) | garantie de non-modification |
| [docs/USER_GUIDE.fr.md](docs/USER_GUIDE.fr.md) · [docs/CONFIGURATION.fr.md](docs/CONFIGURATION.fr.md) · [docs/TROUBLESHOOTING.fr.md](docs/TROUBLESHOOTING.fr.md) | utilisation |
| [docs/TRANSLATING.fr.md](docs/TRANSLATING.fr.md) | traduire l'interface |
| [docs/DEVELOPER_GUIDE.fr.md](docs/DEVELOPER_GUIDE.fr.md) · [docs/INSTALLATION.fr.md](docs/INSTALLATION.fr.md) · [docs/RELEASE_NOTES.fr.md](docs/RELEASE_NOTES.fr.md) · [docs/FINAL_REPORT.fr.md](docs/FINAL_REPORT.fr.md) | développement et livraison |

## Contribuer et langues de la documentation

La documentation est maintenue **en anglais et en français**. Chaque document existe en `X.md` (anglais) et `X.fr.md` (français), et la ligne 1 de chaque fichier renvoie vers l'autre langue.

**Toute modification doit mettre à jour les deux versions dans le même changement.**

Les contributions sont bienvenues : lisez [CONTRIBUTING.fr.md](CONTRIBUTING.fr.md) (environnement, règles, pull requests) et le [code de conduite](CODE_OF_CONDUCT.fr.md). Pour signaler une vulnérabilité, ou un cas où Seekbeam a modifié un fichier, suivez [SECURITY.fr.md](SECURITY.fr.md) au lieu d'ouvrir une issue publique.

## Auteurs

Créé et maintenu par [prog-x](https://github.com/prog-x). Les contributeurs sont listés sur la [page des contributeurs GitHub](https://github.com/prog-x/Seekbeam/graphs/contributors).

## Licence

Seekbeam est distribué sous [licence MIT](LICENSE), **tel quel, sans aucune garantie** (voir la licence). Les composants qu'il redistribue gardent leur propre licence : voir [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) (en anglais).
