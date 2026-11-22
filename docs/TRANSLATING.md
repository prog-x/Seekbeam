**English** | [Français](TRANSLATING.fr.md)

# Translating Seekbeam

The Seekbeam user interface ships in **English, French, German and Italian**. All visible text lives in standard .NET resource files, so translating needs no C# or XAML knowledge.

## Where the strings are

| File | Language |
|---|---|
| `src/Seekbeam.App/Localization/Strings.resx` | English: the neutral language, used as fallback |
| `src/Seekbeam.App/Localization/Strings.fr.resx` | French |
| `src/Seekbeam.App/Localization/Strings.de.resx` | German |
| `src/Seekbeam.App/Localization/Strings.it.resx` | Italian |

Each entry looks like this:

```xml
<data name="Launcher_NoResult" xml:space="preserve"><value>No results · {0:N0} items indexed · {1:F0} ms</value></data>
```

- `name` is the key used by the code. **Never change it.**
- `value` is the text to translate.

Any text editor works. A dedicated tool is more comfortable: ResXResourceManager (Visual Studio extension or standalone), or the resource editor of Visual Studio or Rider.

## Rules

1. **Keep every placeholder** exactly as it is: `{0}`, `{1:N0}`, `{2:F0}`, `{0:0.#}`. Their order in the sentence may change. Numbers and dates are formatted with the user's Windows regional settings.
2. **Keep the leading spaces and separators** of values that start with ` · ` or ` (`: they are appended to another text.
3. **Keep the symbols** `⚠`, `↵`, `›`, `…`.
4. **Text the user types stays in English**: hotkey examples (`Alt+Space`, `Ctrl+Shift+F`) and search syntax (`OR`, `ext:pdf`, `in:`). Translate only the words around them.
5. **Displayed keyboard keys** follow the conventions of the language, for example `Strg` and `Umschalt` in German, `Maiusc` and `Invio` in Italian, `Maj` and `Entrée` in French.
6. Follow the vocabulary of Windows 11 in your language ("Settings", "Properties", "Run as administrator"…), and keep "Seekbeam" untranslated.

## Fixing or completing a translation

1. Edit the values in `Strings.<lang>.resx`.
2. Run `dotnet test tests/Seekbeam.Core.Tests --filter Localization`. The tests check that:
   - the translation has exactly the same keys as `Strings.resx` (no missing or unknown key);
   - every value keeps the same placeholders and is a valid format string;
   - every key used by the code exists, and no visible text is hard-coded in the UI.
3. Run Seekbeam, then choose the language in **Settings › General › Language**. The change applies to the launcher immediately, and to the other windows the next time they are opened.

## Adding a new language

1. Copy `Strings.resx` to `Strings.<code>.resx`, where `<code>` is the two-letter ISO code (`es`, `pt`, `nl`…), and translate every value.
2. Add the language to `UiLanguages.All` in `src/Seekbeam.Core/Config/UiLanguages.cs` with its native name (`("es", "Español")`).
3. Add the code to `<SatelliteResourceLanguages>` in `Directory.Build.props`, so the build and the installer include it.
4. Add the code to `Languages` in `tests/Seekbeam.Core.Tests/LocalizationTests.cs`, then run the tests.

## For developers: adding a visible string

- **XAML**: `Text="{l:Tr My_Key}"`, with `xmlns:l="clr-namespace:Seekbeam.App.Localization"` on the window.
- **C#**: `L.T("My_Key")` for plain text, `L.F("My_Key", arg0, arg1)` for a format string.
- Add the key to **all four** `.resx` files in the same change. If you cannot translate a language, put the English text in it and say so in the pull request; the tests fail if a key is missing from one file.
- Search behavior is language-neutral (case, accents, Unicode), so no search code depends on the UI language.
