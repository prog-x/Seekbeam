[English](ROADMAP.md) | **Français**

# Seekbeam — Feuille de route

Travaux prévus après la version 1.1.0. Les éléments ne sont pas planifiés ; dans chaque section, l'ordre reflète la priorité.

## 1. Localisation de l'interface — réalisée (non publiée)

**État** : implémentée. L'interface est livrée en anglais, français, allemand et italien ; guide des traducteurs dans [docs/TRANSLATING.fr.md](docs/TRANSLATING.fr.md). Reste : connexion optionnelle à Weblate et captures de chaque fenêtre dans chaque langue. La conception d'origine est conservée ci-dessous pour référence.

**Objectif** : livrer l'interface en **français, anglais, allemand et italien**, et permettre à un contributeur externe d'ajouter ou de corriger une langue sans toucher au C# ni au XAML.

Conception prévue :
- **Ressources** : sortir du code toutes les chaînes de l'interface (fenêtres XAML, menu du tray, dialogues, tableau de bord, messages d'erreur affichés) vers des fichiers `.resx` : `Strings.resx` (anglais, langue neutre de repli), `Strings.fr.resx`, `Strings.de.resx`, `Strings.it.resx`. Le `.resx` est le format standard de .NET : il est compilé en assemblies satellites, n'exige aucune dépendance supplémentaire et est compris par les plateformes de traduction (Weblate, Crowdin) et par des éditeurs comme ResXResourceManager.
- **Liaison XAML** : une petite extension de balisage (`{l:Tr Clé}`) pour que le XAML reste lisible. Une clé absente dans une langue retombe sur l'anglais, jamais sur une chaîne vide.
- **Choix de la langue** : par défaut, la langue d'affichage de Windows (`CurrentUICulture`), avec repli sur l'anglais. Un réglage dans Paramètres › Général permet de la forcer. À l'enregistrement, le launcher et le menu de la zone de notification sont reconstruits immédiatement ; les autres fenêtres prennent la nouvelle langue à leur réouverture.
- **Formats** : dates, tailles et nombres passent par `CultureInfo`, jamais par des séparateurs écrits à la main.
- **Parcours du contributeur** :
  - `docs/TRANSLATING.md` (EN + FR) explique comment ajouter une langue (copier `Strings.resx` en `Strings.xx.resx`, traduire les valeurs, compiler, vérifier).
  - Un test de build échoue si une traduction contient une clé absente du fichier neutre ou un paramètre (`{0}`) mal formé. Il signale aussi les clés manquantes par langue, en avertissement plutôt qu'en erreur.
  - Optionnel : relier le dépôt à Weblate, qui héberge gratuitement les projets open source, pour que les contributeurs traduisent dans le navigateur.
- **Hors périmètre** : la recherche est déjà indépendante de la langue (normalisation de la casse, des accents et de l'Unicode ; tokenizer FTS5 `unicode61`). Limite connue : les mots composés allemands ne sont pas découpés, donc `Rechnung` ne trouve pas `Stromrechnung` en plein texte. La recherche de noms, qui fonctionne par sous-chaînes, le trouve.

Critères d'acceptation : les quatre langues complètes, aucune chaîne visible codée en dur (vérifié par un test qui recherche les valeurs littérales `Text=`/`Content=`/`Header=` dans le XAML), et des captures de chaque fenêtre dans chaque langue.

## 2. Publication sur GitHub (liste de préparation)

**Contenu du dépôt**
- [x] Choisir la licence et ajouter `LICENSE` à la racine (voir §3) : MIT.
- [x] `THIRD-PARTY-NOTICES.md` listant les dépendances embarquées et leurs licences. Elles sont redistribuées dans le MSI et le zip, et l'Apache-2.0 impose d'inclure son texte et ses notices.
- [x] `CONTRIBUTING.md` (EN + FR) : build, tests, règle d'immutabilité (tout nouvel accès fichier passe par `SourceFileAccess`), règle de documentation bilingue, style des commits.
- [x] `SECURITY.md` : comment signaler une vulnérabilité en privé (signalement privé de GitHub). C'est important parce que Seekbeam affirme ne pas modifier les fichiers.
- [x] `CODE_OF_CONDUCT.md` (Contributor Covenant).
- [x] `.github/ISSUE_TEMPLATE/` (rapport de bug demandant la version, le build Windows et l'extrait de log ; demande de fonctionnalité) et un modèle de pull request avec une case « docs EN et FR mises à jour ».
- [x] Documents d'entrée du projet (2026-11-22) : `CLAUDE.md` est publié sous une forme plus courte, limitée aux règles du projet. Les instructions de travail privées de l'agent sont passées dans `CLAUDE.local.md`, que git ignore. Le cahier des charges initial est déplacé dans `docs/project/SPECIFICATIONS.md` comme document historique. Le prompt de lancement initial (`PROMPT_LAUNCH.md`) reste hors du dépôt.
- [x] Relire la documentation pour les chemins propres à la machine et les données personnelles. Le premier commit constitue tout l'historique : rien de privé ne doit y figurer. (2026-11-22 : rien trouvé.)
- [ ] Captures d'écran et court GIF du launcher dans le `README.md`.
- [x] Vérification du nom (2026-11-21) : le projet s'appelait d'abord « Findex », mais une application Windows de recherche locale de fichiers porte déjà ce nom (Product Hunt) ; il a donc été renommé **Seekbeam** avant publication. Vérifié pour Seekbeam : aucun logiciel, aucune entreprise ni marque trouvés ; aucun dépôt ni compte GitHub ; noms NuGet, npm, PyPI et crates libres ; seekbeam.com à vendre, seekbeam.app enregistré mais inutilisé. Écartés en chemin : Flashseek (un dépôt de recherche de fichiers Windows du même nom), Dowse (un outil open source de recherche pour Windows), Keyseek, Reperio (marque américaine), Seekdex, entre autres. Vérification TMview (2026-11-22) : aucune marque SEEKBEAM trouvée.

**Intégration continue (GitHub Actions, `windows-latest`)**
- [x] Workflow `ci.yml` sur push et PR : `dotnet build -c Release` (BannedApiAnalyzers bloque en erreur), `dotnet test Seekbeam.sln` (dont l'analyse IL d'immutabilité et les tests d'intégrité).
- [x] Workflow `release.yml` sur un tag `v*` : `pwsh build/Build-Release.ps1`, puis publier le MSI et le zip portable dans une GitHub Release, avec leurs sommes SHA-256.
- [ ] Optionnel : une tâche périodique qui exécute `tools/audit/Run-DynamicAudit.ps1` et joint le rapport.
- [x] Dependabot pour les paquets NuGet.

**Distribution**
- [ ] Signature de code. Des binaires non signés déclenchent les avertissements SmartScreen. La SignPath Foundation signe gratuitement les projets open source ; sinon, il faut un certificat commercial.
- [x] Licence de WiX Toolset (vérifiée le 2026-11-21) : WiX est sous MS-RL, qui ne s'applique pas au MSI produit. WiX v6 inclut le contrat *Open Source Maintenance Fee* v1.0 : « The Fee applies only to Users that generate revenue by the Software. Non-revenue-generating use of the Software is exempt. » Seekbeam est gratuit et non commercial : aucune contribution n'est due, et la v6 n'impose pas d'accepter le contrat. WiX v7 (contrat v1.1, contribution au-delà de 10 000 US$ de revenus annuels) bloque le build tant que le contrat n'est pas accepté : Seekbeam reste en 6.0.x, et Dependabot ignore les mises à jour majeures de WiX. À revérifier si Seekbeam génère un jour des revenus.
- [ ] Optionnel : un manifeste `winget` une fois les versions signées.

## 3. Licence — choisie : MIT

`LICENSE` (MIT, « prog-x and Seekbeam contributors ») est à la racine du dépôt, et le script de release le copie avec `THIRD-PARTY-NOTICES.md` dans le MSI et le zip portable. L'analyse qui a conduit à ce choix est conservée ci-dessous.

| Dépendance (livrée) | Licence |
|---|---|
| SQLite (e_sqlite3) | Domaine public |
| Microsoft.Data.Sqlite | MIT |
| SQLitePCLRaw | Apache-2.0 |
| PdfPig | Apache-2.0 |

Build et tests uniquement : xUnit (Apache-2.0), Microsoft.CodeAnalysis.BannedApiAnalyzers (MIT), WiX Toolset (MS-RL + OSMF).

Toutes les dépendances livrées sont permissives, donc chacune des licences suivantes convient :
- **MIT** : la licence la plus courte et la plus connue, qui maximise la réutilisation. N'importe qui peut construire un produit fermé à partir de Seekbeam.
- **Apache-2.0** : comme MIT, avec en plus une cession explicite des brevets et un mécanisme de NOTICE. Un peu plus lourde.
- **GPL-3.0** : les versions modifiées redistribuées doivent rester open source (c'est le modèle de Recoll). Elle peut freiner certains contributeurs et certaines entreprises.

Recommandation : **MIT** pour un utilitaire personnel destiné à être largement utilisé. Choisir **GPL-3.0** si empêcher les forks fermés compte davantage que l'adoption.

## 4. Autres améliorations (issues de l'état 1.0)
- Affinage incrémental pendant la frappe : réutiliser les candidats de la requête précédente quand la nouvelle la prolonge. Cible : préfixes d'une ou deux lettres à 5 M entrées sous 60 ms au p95 (actuellement 139 ms).
- Catalogue plus compact (cible 120 octets/entrée, actuellement 210) : supprimer `ix_entries_root`, compacter `mtime`/`ctime`.
- Mise à jour immédiate de la profondeur stockée du sous-arbre d'un dossier déplacé.
- Mode USN/MFT optionnel pour les installations élevées.
- OCR optionnel (Windows.Media.Ocr), archives 7z/rar, jump lists pour la découverte.
- Réextraction automatique au changement de version d'un extracteur.
- Aperçu riche (images, PDF).
- Exécuter l'audit ETW (`tools/audit/Run-EtwAudit.ps1`, administrateur) et ajouter ses résultats à `docs/FILE_IMMUTABILITY_AUDIT.md`.
