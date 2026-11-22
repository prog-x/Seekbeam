[English](FINAL_REPORT.md) | **Français**

# Seekbeam 1.0 — Rapport final

## 1. Fonctionnalités réalisées
| Domaine | Réalisé |
|---|---|
| Launcher | double Ctrl (gauche, droite ou les deux, délai), raccourcis personnalisés, fenêtre centrée ou près du curseur, navigation clavier, actions, icônes, surlignage, statut d'indexation, thème clair ou sombre |
| Recherche de noms | multi-tokens dans n'importe quel ordre, sous-chaînes, casse et accents (NFD compris), chemins (dossiers parents), `ext:`, `kind:`, `in:`, exclusions, phrases, fautes de frappe (bitap avec transpositions), suggestions approximatives marquées ≈, annulation des requêtes obsolètes |
| Ranking | modèle additif documenté (exact, préfixe, débuts de mots, couverture, ordre, apps, dossiers, profondeur), apprentissage local plafonné, favoris |
| Applications | menu Démarrer, bureau, UWP/MSIX, exécutables de dossiers configurés, lancement via le shell (UAC respecté) |
| Indexation | NtQueryDirectoryFile, diff par répertoire, files persistantes priorisées et dédupliquées, transactions courtes, recherche disponible dès les premiers fichiers, exclusions, profondeur, liens et cycles, permissions respectées, racines indisponibles |
| Surveillance | ReadDirectoryChangesW (découpage des racines à exclusions), coalescence, renommages en place, rafales, débordements, réconciliation au démarrage et périodique |
| Content Index | FTS5 (BM25, phrases, OR, NOT, préfixes), extraits, aperçu, filtres de type, emplacement, date et taille ; 8 extracteurs (texte et code, HTML/XML, OOXML, ODF, RTF, PDF, DOC/XLS/PPT, ZIP) dans un processus isolé sous Job Object |
| Découverte opportuniste | Éléments récents de Windows et ouvertures depuis Seekbeam, budget horaire, périmètre respecté |
| Ressources | Économie, Équilibré, Performance, Auto (CPU, RAM, inactivité, batterie, plein écran), plages horaires, pause et reprise, priorité de la recherche, threads en mode arrière-plan |
| Diagnostic | tableau de bord (progression, files, tailles, ressources, racines, erreurs), journaux locaux tournants |
| Installation | MSI par utilisateur sans élévation, zip portable, démarrage automatique, `--exit`, `--data`, `--purge-data` |
| Immutabilité | séparation SourceFileAccess / SeekbeamStorage, BannedApiAnalyzers, analyse IL, tests SHA-256, FsAudit, script ETW |

**Fonctionnalités volontairement exclues** : renommer, déplacer, supprimer, modifier attributs ou métadonnées (politique de non-modification).

## 2. Architecture et technologies
- **Technologies** : C# 14 / .NET 10, WPF, SQLite (WAL) via Microsoft.Data.Sqlite, PdfPig. Il n'y a que deux dépendances de production.
- **Name Index** : un LSM de segments mmap balayés par `IndexOf` vectorisé, dérivé du catalogue SQLite.
- **Content Index** : FTS5 contentless.
- **Extraction** : hors processus.

Voir [ARCHITECTURE.fr.md](../ARCHITECTURE.fr.md) et [TECHNICAL_DECISIONS.fr.md](../TECHNICAL_DECISIONS.fr.md) (16 décisions documentées avec alternatives et mesures).

## 3. Tests
185 tests automatisés, tous verts :
- **Core (75)** :
  - normalisation, parseur, bitap ;
  - Name Index : fusion, crash, manifeste corrompu, concurrence ;
  - recherche et ranking, dont l'exemple du cahier des charges ;
  - intégration sur vrai filesystem : exclusions, profondeur, watcher, renommages et déplacements, réconciliation hors ligne, jonction cyclique, ACL refusée, retrait de racine ;
  - apps, `.lnk`, ressources ;
  - contenu de bout en bout avec le vrai hôte, crash de l'hôte.
- **Extraction (101)** : tous formats, encodages, malformés, bombes ZIP, XXE, fuzzing, intégrité des fichiers lus.
- **Immutabilité (9)** : analyse IL et auto-test ; intégrité SHA-256 et métadonnées sur trois scénarios, dont le vrai `Seekbeam.exe` tué puis relancé.
- **Manuels / automatisés hors suite** :
  - smoke test UI Automation (double Ctrl réel, focus, recherche, Échap, absence de faux déclenchement) ;
  - audit dynamique FsAudit avec contrôle positif ;
  - installation et désinstallation MSI silencieuses.

## 4. Benchmarks (détails : [BENCHMARKS.fr.md](BENCHMARKS.fr.md))
- **Indexation réelle de 1 000 000 de fichiers** : 36,8 s (27 159 entrées/s), 0,53 cœur, pic de 199 Mo de RAM privée.
- **Recherche sur ces 1 M entrées réelles** : p50 2,7 ms, p95 7,6 ms, p99 25,8 ms ; pendant l'indexation, p95 15,5 ms.
- **2 M entrées** : p95 20 ms.
- **5 M entrées** : p95 51 ms (cible 60 ms), index chargé en 21 ms ; la frappe de préfixes d'une lettre est plus lente à 5 M (139 ms p95).
- **Application au repos** : environ 63 à 69 Mo de RAM privée ; démarrage avec index de 1 M chargé en environ 70 ms.
- **Mode Économie** : plafond respecté (2 002 entrées/s, 0,05 cœur).

## 5. Limitations
Voir [RELEASE_NOTES.fr.md](RELEASE_NOTES.fr.md) et [FILE_IMMUTABILITY_AUDIT.fr.md](FILE_IMMUTABILITY_AUDIT.fr.md) §8 :
- heure d'accès NTFS (effet de l'OS) ;
- applications externes ;
- interface en français uniquement ;
- OCR absent ;
- ZIP seul ;
- pas de MFT/USN ;
- catalogue d'environ 165 octets/entrée ;
- ETW non exécuté faute de droits administrateur ;
- binaires non signés.

## 6. Installation
`Seekbeam-1.0.0-x64.msi` (par utilisateur, sans élévation) ou zip portable, construits par `pwsh build/Build-Release.ps1` dans `artifacts/installer/`. Voir [INSTALLATION.fr.md](INSTALLATION.fr.md).

## 7. Utilisation
Double Ctrl → taper → Entrée. `Ctrl+D` pour le plein texte, Tab pour les actions. Voir [USER_GUIDE.fr.md](USER_GUIDE.fr.md).

## 8. Évolutions possibles
- **Localisation de l'interface** : français, anglais, allemand, italien, avec des fichiers de traduction faciles à compléter par des contributeurs externes.
- **Publication** : sur GitHub (licence, CI, fichiers de contribution).
- **Indexation** : USN/MFT pour les installations élevées.
- **Contenu** : OCR (Windows.Media.Ocr), 7z/rar, réextraction par version d'extracteur.
- **Découverte** : jump lists.
- **Stockage** : catalogue plus compact.
- **Distribution** : signature de code.
- **Aperçu** : images et PDF.

Voir [ROADMAP.fr.md](../ROADMAP.fr.md).
