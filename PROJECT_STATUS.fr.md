[English](PROJECT_STATUS.md) | **Français**

# Seekbeam — État du projet

_Mis à jour : 2026-11-21_

## Phase actuelle
Version 1.0.0 terminée : fonctionnelle, testée, mesurée, documentée et installable. Voir le rapport final dans `docs/FINAL_REPORT.md`.

## Terminé (toutes les phases d'IMPLEMENTATION_PLAN)
| Phase | Statut | Preuve |
|---|---|---|
| 0 Analyse et conception | ✅ | ARCHITECTURE, TECHNICAL_DECISIONS, plans, RISKS, POLICY, spike |
| 1 Socle Core | ✅ | Storage/SourceAccess/Config/Log/Normalizer, BannedApiAnalyzers |
| 2 Catalogue et Name Index | ✅ | NameIndexTests (crash, manifeste corrompu, concurrence) |
| 3 Recherche et ranking | ✅ | SearchTests, TextTests |
| 4 Bench Name Index | ✅ | docs/BENCHMARKS.md (1 M, 2 M, 5 M synthétiques) |
| 5–6 Scanner, files, surveillance, réconciliation | ✅ | IndexingIntegrationTests (filesystem réel) |
| 7 Applications | ✅ | AppIndexTests, LnkParserTests |
| 8 Application WPF | ✅ | smoke test UI Automation (double Ctrl réel, recherche, Échap, pas de faux déclenchement) |
| 9–10 Contenu et recherche avancée | ✅ | ContentEndToEndTests, 101 tests d'extracteurs |
| 11 Découverte opportuniste | ✅ | DiscoverCommand, RecentItemsMonitor, IntegrityTests |
| 12 Ressources | ✅ | ResourceDecisionTests ; Économie mesurée à 2 002 entrées/s |
| 13 Dashboard et paramètres | ✅ | fenêtres WPF |
| 14 Immutabilité | ✅ | docs/FILE_IMMUTABILITY_AUDIT.md (IL, intégrité, FsAudit) |
| 15 Installation | ✅ | MSI par utilisateur installé et désinstallé en silencieux ; zip portable |
| 16 Validation et performance | ✅ | dataset disque 1 M, benchmarks |
| 17 Finalisation | ✅ | README, guides, release notes, rapport final |

## Tests
307 tests automatisés verts : Core 196, Extraction 101, Immutabilité 10 (`dotnet test Seekbeam.sln`), plus 36/36 vérifications sur de vraies boîtes de dialogue Windows (`tools/Seekbeam.DialogCheck`, [docs/DIALOG_NAVIGATION_CHECK.fr.md](docs/DIALOG_NAVIGATION_CHECK.fr.md)).

## Bugs corrigés pendant la validation (avec test ou mesure)
- Packing des structures Win32 (taille et dates de métadonnées fausses), trouvé par l'agent extracteurs.
- Lecteur SQLite resté ouvert pendant le changement de `CommandText` (indexeur inactif).
- `COUNT(*)` de la file de contenu dans le chemin chaud (139 Go lus pour 1 M fichiers, ramenés à 1,2 Go).
- Fichiers vides mis en file de contenu (338 k écritures inutiles).
- Détection « file vide » erronée avec plusieurs workers et retries différés.
- Énumération AppsFolder (E_INVALIDARG) remplacée par Shell.Application.
- `File.Replace` du manifeste en échec sur verrou transitoire (antivirus) à 3,6 M entrées → retries avec backoff.
- Noms d'objets noyau d'instance non stables (`string.GetHashCode` aléatoire), remplacés par SHA-256.
- Tolérance aux fautes sur les nombres, tokens d'un caractère, verrou par candidat (88 ms → 6 ms).

## Limitations et dette connues
- Taille du catalogue d'environ 165 octets/entrée (cible 120) : index SQLite secondaires. Piste : supprimer `ix_entries_root`, compacter `mtime`/`ctime`.
- À 5 M entrées, la frappe de préfixes d'une ou deux lettres atteint 139 ms au p95 (affinage incrémental à implémenter).
- La profondeur stockée du sous-arbre d'un dossier déplacé n'est mise à jour qu'au rescan suivant.
- OCR non inclus ; archives ZIP seules ; pas de MFT/USN (élévation requise).
- Audit ETW (administrateur) fourni mais non exécuté.
- « Aller au dossier » : les boîtes des applications élevées (UIPI) et l'ancienne arborescence `SHBrowseForFolder` ne sont pas pilotées (le chemin est copié) ; les boîtes qui ne reposent pas sur les boîtes communes de Windows ne sont pas reconnues ; la saisie directe par le vrai hook n'est pas couverte par les tests automatisés (les touches injectées sont ignorées à dessein).
- Index des dossiers : un changement des règles « classé plus bas » met à jour les dossiers existants au prochain parcours ; les dossiers appris viennent seulement de Seekbeam, des éléments récents et des chemins tapés (aucun hook dans les autres applications).
- La réextraction automatique après changement de version d'un extracteur n'est pas implémentée (utiliser « Réindexer le contenu »).

## Prochaines évolutions possibles
Mode USN/MFT pour les installations élevées, OCR Windows.Media.Ocr optionnel, 7z/rar, jump lists pour la découverte, signature des binaires, aperçu riche (images, PDF).

Depuis la 1.0 (non publié) : « aller au dossier » dans les boîtes Ouvrir/Enregistrer/choix de dossier et l'Explorateur, avec saisie directe et Quick Switch (TD-20), index des dossiers hors des emplacements et bribes de chemin / chemins tapés (TD-21), interface en anglais, français, allemand et italien ([docs/TRANSLATING.fr.md](docs/TRANSLATING.fr.md)), licence MIT, profondeur par défaut de 10 avec signalement des dossiers non explorés, correctif de l'arrêt. Les travaux prévus, dont le reste de la préparation de la publication GitHub, sont suivis dans [ROADMAP.fr.md](ROADMAP.fr.md).
