[English](CONTRIBUTING.md) | **Français**

# Contribuer à Seekbeam

Merci de votre intérêt ! Seekbeam est maintenu par [prog-x](https://github.com/prog-x). Les signalements de bugs, les traductions, la documentation et le code sont tous bienvenus. Les issues et pull requests peuvent être rédigées en anglais ou en français.

## Avant de commencer

- **Bugs et idées** : ouvrez une issue avec le modèle correspondant. Consultez [ROADMAP.fr.md](ROADMAP.fr.md) pour ce qui est déjà prévu.
- **Problème de sécurité, ou cas où Seekbeam a modifié un fichier** : n'ouvrez pas d'issue publique. Suivez [SECURITY.fr.md](SECURITY.fr.md).
- **Changements importants** (nouveau format d'index, nouvelle dépendance, nouvel extracteur, modification de l'accès aux fichiers) : ouvrez d'abord une issue pour convenir de l'approche.
- **Traductions** : voir [docs/TRANSLATING.fr.md](docs/TRANSLATING.fr.md). Aucune connaissance de C# n'est nécessaire.

## Environnement de développement

Prérequis : Windows 10 ou 11 x64, le SDK .NET 10 (version fixée dans `global.json`) et, au choix, Visual Studio 2026, Rider ou VS Code.

```powershell
git clone https://github.com/prog-x/Seekbeam.git
cd Seekbeam
dotnet build Seekbeam.sln              # build Debug
dotnet test Seekbeam.sln               # tous les tests (ils utilisent les binaires Debug)
dotnet run --project src/Seekbeam.App -- --data "$env:TEMP\seekbeam-dev"   # profil isolé, ne touche pas votre vrai index
pwsh build/Build-Release.ps1         # MSI + zip portable dans artifacts/installer
```

Utilisez `--data <dossier>` pendant le développement : il garde un index, une configuration et un verrou d'instance séparés, ce qui permet de lancer une version de développement à côté d'un Seekbeam installé. L'organisation du code et les commandes de benchmark sont dans [docs/DEVELOPER_GUIDE.fr.md](docs/DEVELOPER_GUIDE.fr.md).

## Règles que les pull requests doivent respecter

### 1. Seekbeam ne modifie jamais les fichiers qu'il indexe
Cette règle prime sur toute fonctionnalité et toute optimisation ([docs/FILE_IMMUTABILITY_POLICY.fr.md](docs/FILE_IMMUTABILITY_POLICY.fr.md)).
- Les fichiers et dossiers sources sont lus **uniquement** via `SourceFileAccess`, et Seekbeam n'écrit **que** via `SeekbeamStorage`, dans son propre dossier de données.
- Les API d'écriture de fichiers sont interdites par le build (`src/BannedSymbols.txt`, erreur RS0030). Ne supprimez pas cette erreur en dehors de `Storage/` et `SourceAccess/`.
- `StaticAnalysisTests` analyse l'IL compilé. Un nouvel appel P/Invoke doit être ajouté à la liste autorisée, avec une justification relue.
- Les fonctionnalités qui renomment, déplacent, suppriment ou modifient des fichiers de l'utilisateur ne seront pas acceptées.
- Une modification qui touche à l'accès aux fichiers doit être accompagnée d'un test d'intégrité (`tests/Seekbeam.Immutability.Tests`).

### 2. Performances et ressources
La recherche doit rester interactive avec des millions de fichiers, et l'indexation doit rester discrète. Pour une modification de la recherche, du parcours ou de l'indexation, joignez des mesures avant et après (`tools/Seekbeam.Bench`, voir [docs/BENCHMARKS.fr.md](docs/BENCHMARKS.fr.md)).

### 3. Textes de l'interface en quatre langues
Aucun texte visible écrit en dur : le XAML utilise `{l:Tr Cle}` et le C# `L.T("Cle")` / `L.F("Cle", …)`. Ajoutez chaque nouvelle clé aux quatre fichiers `src/Seekbeam.App/Localization/Strings*.resx` (en, fr, de, it). Si vous ne pouvez pas traduire une langue, copiez le texte anglais et signalez-le dans la pull request. `LocalizationTests` le vérifie.

### 4. Documentation en anglais et en français
Chaque document existe en `X.md` (anglais) et `X.fr.md` (français). Quand vous modifiez l'un, mettez l'autre à jour dans la même pull request. Si vous n'écrivez qu'une langue, signalez-le : un mainteneur complétera l'autre.

### 5. Style de code
- C# avec nullable activé et espaces de noms au niveau fichier. Suivez le style du code environnant.
- Les commentaires expliquent le *pourquoi*. Les erreurs sont traitées explicitement, et les opérations longues prennent un `CancellationToken`.
- Pas de nouvelle dépendance sans discussion sur sa licence, sa maintenance, sa taille et ses dépendances transitives. Les dépendances livrées doivent permettre une redistribution sous MIT, et être ajoutées à `THIRD-PARTY-NOTICES.md`.
- Chaque modification importante est accompagnée de tests.

## Pull requests

1. Forkez le dépôt et créez une branche depuis `main` (`fix/…`, `feat/…`, `docs/…`, `i18n/…`).
2. Gardez chaque pull request centrée sur un seul sujet. Rédigez les messages de commit à l'impératif, en anglais de préférence (« Fix depth notice when a root is removed »).
3. Vérifiez que `dotnet build Seekbeam.sln -c Release` et `dotnet test Seekbeam.sln` passent en local. La CI exécute les deux sous Windows.
4. Remplissez la liste de vérification de la pull request.

## Publication d'une version (mainteneurs)

1. Mettez à jour `<Version>` dans `Directory.Build.props` et la section « Non publié » de `docs/RELEASE_NOTES.md` / `.fr.md`.
2. Créez et poussez le tag : `git tag v1.1.0 && git push origin v1.1.0`.
3. Le workflow `Release` compile, teste et empaquette Seekbeam, puis crée une release GitHub en **brouillon** avec le MSI, le zip portable et `SHA256SUMS.txt`. Relisez-la, puis publiez-la.

## Licence

En contribuant, vous acceptez que vos contributions soient distribuées sous la [licence MIT](LICENSE) du projet.
