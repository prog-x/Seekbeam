[English](TRANSLATING.md) | **Français**

# Traduire Seekbeam

L'interface de Seekbeam est livrée en **anglais, français, allemand et italien**. Tous les textes visibles sont dans des fichiers de ressources .NET standard : traduire ne demande aucune connaissance de C# ni de XAML.

## Où se trouvent les textes

| Fichier | Langue |
|---|---|
| `src/Seekbeam.App/Localization/Strings.resx` | anglais : langue neutre, utilisée en repli |
| `src/Seekbeam.App/Localization/Strings.fr.resx` | français |
| `src/Seekbeam.App/Localization/Strings.de.resx` | allemand |
| `src/Seekbeam.App/Localization/Strings.it.resx` | italien |

Chaque entrée se présente ainsi :

```xml
<data name="Launcher_NoResult" xml:space="preserve"><value>Aucun résultat · {0:N0} éléments indexés · {1:F0} ms</value></data>
```

- `name` est la clé utilisée par le code. **Ne la modifiez jamais.**
- `value` est le texte à traduire.

N'importe quel éditeur de texte convient. Un outil dédié est plus confortable : ResXResourceManager (extension Visual Studio ou version autonome), ou l'éditeur de ressources de Visual Studio ou de Rider.

## Règles

1. **Conservez chaque paramètre** à l'identique : `{0}`, `{1:N0}`, `{2:F0}`, `{0:0.#}`. Leur ordre dans la phrase peut changer. Les nombres et les dates suivent les paramètres régionaux de Windows de l'utilisateur.
2. **Conservez les espaces et séparateurs de début** des valeurs qui commencent par ` · ` ou ` (` : elles sont ajoutées à la suite d'un autre texte.
3. **Conservez les symboles** `⚠`, `↵`, `›`, `…`.
4. **Ce que l'utilisateur tape reste en anglais** : exemples de raccourcis (`Alt+Space`, `Ctrl+Shift+F`) et syntaxe de recherche (`OR`, `ext:pdf`, `in:`). Traduisez seulement les mots autour.
5. **Les touches affichées** suivent les conventions de la langue, par exemple `Strg` et `Umschalt` en allemand, `Maiusc` et `Invio` en italien, `Maj` et `Entrée` en français.
6. Suivez le vocabulaire de Windows 11 dans votre langue (« Paramètres », « Propriétés », « Exécuter en tant qu'administrateur »…), et ne traduisez pas « Seekbeam ».

## Corriger ou compléter une traduction

1. Modifiez les valeurs dans `Strings.<langue>.resx`.
2. Lancez `dotnet test tests/Seekbeam.Core.Tests --filter Localization`. Les tests vérifient que :
   - la traduction a exactement les mêmes clés que `Strings.resx` (aucune clé manquante ou inconnue) ;
   - chaque valeur garde les mêmes paramètres et forme une chaîne de format valide ;
   - chaque clé utilisée par le code existe, et aucun texte visible n'est écrit en dur dans l'interface.
3. Lancez Seekbeam, puis choisissez la langue dans Paramètres › Général › Langue. Le changement s'applique tout de suite au launcher, et aux autres fenêtres à leur prochaine ouverture.

## Ajouter une langue

1. Copiez `Strings.resx` en `Strings.<code>.resx`, où `<code>` est le code ISO à deux lettres (`es`, `pt`, `nl`…), et traduisez chaque valeur.
2. Ajoutez la langue à `UiLanguages.All` dans `src/Seekbeam.Core/Config/UiLanguages.cs`, avec son nom dans la langue elle-même (`("es", "Español")`).
3. Ajoutez le code à `<SatelliteResourceLanguages>` dans `Directory.Build.props`, pour que le build et l'installeur l'incluent.
4. Ajoutez le code à `Languages` dans `tests/Seekbeam.Core.Tests/LocalizationTests.cs`, puis lancez les tests.

## Pour les développeurs : ajouter un texte visible

- **XAML** : `Text="{l:Tr Ma_Cle}"`, avec `xmlns:l="clr-namespace:Seekbeam.App.Localization"` sur la fenêtre.
- **C#** : `L.T("Ma_Cle")` pour un texte simple, `L.F("Ma_Cle", arg0, arg1)` pour une chaîne de format.
- Ajoutez la clé aux **quatre** fichiers `.resx` dans le même changement. Si vous ne pouvez pas traduire une langue, mettez-y le texte anglais et signalez-le dans la pull request ; les tests échouent si une clé manque dans un fichier.
- La recherche est indépendante de la langue (casse, accents, Unicode) : aucun code de recherche ne dépend de la langue de l'interface.
