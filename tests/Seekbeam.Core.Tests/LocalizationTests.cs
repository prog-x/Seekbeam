using System.Text.RegularExpressions;
using System.Xml.Linq;
using Seekbeam.Core.Content;
using Xunit;

namespace Seekbeam.Core.Tests;

/// <summary>
/// Checks the UI translations (src/Seekbeam.App/Localization/Strings*.resx) and that the UI code uses them:
/// same keys and placeholders in every language, every key used by the code exists, no hard-coded user-visible text.
/// </summary>
public sealed class LocalizationTests
{
    private static readonly string Repo = FindRepo();
    private static readonly string AppDir = Path.Combine(Repo, "src", "Seekbeam.App");
    private static readonly string LocDir = Path.Combine(AppDir, "Localization");
    public static readonly TheoryData<string> Languages = new() { "fr", "de", "it" };

    private static string FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Seekbeam.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Seekbeam.sln not found");
    }

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(LocDir, file)).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

    private static List<string> Placeholders(string s) =>
        Regex.Matches(s, @"\{\d+(:[^}]*)?\}").Select(m => m.Value).Order(StringComparer.Ordinal).ToList();

    [Theory, MemberData(nameof(Languages))]
    public void TranslationHasSameKeysAndPlaceholdersAsEnglish(string lang)
    {
        var en = Load("Strings.resx");
        var tr = Load($"Strings.{lang}.resx");
        Assert.Empty(en.Keys.Except(tr.Keys));   // missing translations
        Assert.Empty(tr.Keys.Except(en.Keys));   // keys unknown to the neutral file
        foreach (var (key, value) in tr)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"{lang}: empty value for {key}");
            Assert.True(Placeholders(en[key]).SequenceEqual(Placeholders(value)), $"{lang}: placeholders differ for {key}");
            // Format strings must be valid (string.Format throws on malformed braces).
            string.Format(System.Globalization.CultureInfo.InvariantCulture, value, Enumerable.Repeat((object)1, 5).ToArray());
        }
    }

    [Fact]
    public void EveryKeyUsedByTheUiExists()
    {
        var en = Load("Strings.resx");
        var used = new HashSet<string>();
        foreach (var f in Directory.EnumerateFiles(AppDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            string text = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(text, @"L\.[TF]\(""([A-Za-z0-9_]+)""[,)]|\{l:Tr ([A-Za-z0-9_]+)\}"))
                used.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        }
        // Keys built at run time.
        foreach (var s in Enum.GetNames<ContentStatus>()) used.Add("ContentStatus_" + s);
        // Launcher content dot: reasons why a file's content is not indexed (ResultItem.ReasonKey).
        foreach (var s in Enum.GetNames<Core.Indexing.ContentIneligibility>().Where(n => n != "None")) used.Add("ContentWhy_" + s);
        used.Add("ContentWhy_Encrypted");
        string rm = File.ReadAllText(Path.Combine(Repo, "src", "Seekbeam.Core", "Resources", "ResourceManager.cs"));
        foreach (Match m in Regex.Matches(rm, @"new\(""(\w+)""|Name = ""(\w+)""")) used.Add("Profile_" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
        foreach (Match m in Regex.Matches(rm, @"Reason = ""(\w+)""")) used.Add("Reason_" + m.Groups[1].Value);
        Assert.True(used.Count > 200, "key scan found too few keys: " + used.Count);
        Assert.Empty(used.Except(en.Keys));
    }

    [Fact]
    public void XamlHasNoHardCodedText()
    {
        var offenders = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(AppDir, "Windows"), "*.xaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\b(Text|Content|Header|ToolTip|Title|AutomationProperties\.Name)=""([^""{]*\p{L}{2,}[^""]*)"""))
            {
                string v = m.Groups[2].Value;
                // Allowed: product name, icon glyphs (&#x…;), and the extractor ids tooltip (identifiers the user types).
                if (v == "Seekbeam" || v.StartsWith("&#x") || v.StartsWith("text, markup, ooxml")) continue;
                offenders.Add($"{Path.GetFileName(f)}: {m.Value}");
            }
        Assert.Empty(offenders);
    }

    [Fact]
    public void CodeHasNoHardCodedFrenchText()
    {
        var offenders = new List<string>();
        foreach (var f in Directory.EnumerateFiles(AppDir, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.EndsWith("L.cs")))
            foreach (var (line, i) in File.ReadAllLines(f).Select((l, i) => (l, i)))
                if (!line.TrimStart().StartsWith("//") && Regex.IsMatch(line, @"""[^""]*[àâçéèêëîïôûùüÿœÀÉÈ][^""]*"""))
                    offenders.Add($"{Path.GetFileName(f)}:{i + 1}: {line.Trim()}");
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("", "fr-FR", "fr")]
    [InlineData("", "de-CH", "de")]
    [InlineData("", "es-ES", "en")]
    [InlineData("it", "fr-FR", "it")]
    [InlineData("xx", "it-IT", "it")]
    public void LanguageResolution(string configured, string windows, string expected) =>
        Assert.Equal(expected, Core.Config.UiLanguages.Resolve(configured, new System.Globalization.CultureInfo(windows)));

    [Fact]
    public void EveryShippedLanguageHasAResourceFile()
    {
        foreach (var (code, _) in Core.Config.UiLanguages.All)
            Assert.True(File.Exists(Path.Combine(LocDir, code == "en" ? "Strings.resx" : $"Strings.{code}.resx")), code);
    }
}
