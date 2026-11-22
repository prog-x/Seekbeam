using System.Globalization;

namespace Seekbeam.Core.Config;

/// <summary>User-interface languages shipped with Seekbeam and how the configured one is resolved.</summary>
public static class UiLanguages
{
    /// <summary>Language code and native name (never translated). English is the neutral fallback.</summary>
    public static readonly IReadOnlyList<(string Code, string NativeName)> All =
        [("en", "English"), ("fr", "Français"), ("de", "Deutsch"), ("it", "Italiano")];

    /// <summary>The configured code if shipped; otherwise ("" or unknown) the Windows display language if shipped; else English.</summary>
    public static string Resolve(string? configured, CultureInfo windowsUi)
    {
        if (!string.IsNullOrWhiteSpace(configured) && All.Any(l => l.Code.Equals(configured, StringComparison.OrdinalIgnoreCase)))
            return configured.ToLowerInvariant();
        string sys = windowsUi.TwoLetterISOLanguageName;
        return All.Any(l => l.Code == sys) ? sys : "en";
    }
}
