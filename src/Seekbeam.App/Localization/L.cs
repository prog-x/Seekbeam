using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace Seekbeam.App.Localization;

/// <summary>
/// User-interface strings (Localization/Strings*.resx). English is the neutral language; fr, de and it are satellite
/// translations. Text follows the chosen UI language; numbers and dates keep the user's Windows regional format.
/// A key missing from a translation falls back to English, never to an empty string.
/// </summary>
public static class L
{
    /// <summary>Languages shipped with Seekbeam: code and native name (never translated).</summary>
    public static IReadOnlyList<(string Code, string NativeName)> Languages => Core.Config.UiLanguages.All;

    private static readonly ResourceManager s_rm = new("Seekbeam.App.Localization.Strings", typeof(L).Assembly);
    private static readonly HashSet<string> s_missingLogged = [];

    /// <summary>Culture used for UI text.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    /// <summary>Resolves the configured language ("" = Windows display language) to a supported culture and applies it.</summary>
    public static void Apply(string? configured)
    {
        string code = Core.Config.UiLanguages.Resolve(configured, s_windowsUi);
        Culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }

    /// <summary>Windows display language captured before Seekbeam overrides the UI culture.</summary>
    private static readonly CultureInfo s_windowsUi = CultureInfo.CurrentUICulture;

    /// <summary>Translated text for <paramref name="key"/>.</summary>
    public static string T(string key)
    {
        string? s = s_rm.GetString(key, Culture);
        if (s != null) return s;
        lock (s_missingLogged)
            if (s_missingLogged.Add(key)) Core.Diagnostics.Log.Warn("L10n", "Missing string: " + key);
        return "[" + key + "]";
    }

    /// <summary>Translated format string filled with <paramref name="args"/> (numbers use the regional format).</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
}

/// <summary>XAML markup extension: <c>Text="{l:Tr Set_Save}"</c>. The value is read when the window is created.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) { Key = key; }
    [ConstructorArgument("key")] public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
