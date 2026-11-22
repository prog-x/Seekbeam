using System.Reflection;

namespace Seekbeam.App.Services;

/// <summary>Product identity shown in "About", the tray tooltip and the logs. Version comes from Directory.Build.props.</summary>
public static class AppInfo
{
    public const string ProjectUrl = "https://github.com/prog-x/seekbeam";

    private static readonly Assembly Asm = typeof(AppInfo).Assembly;

    /// <summary>"1.0.0" (from &lt;Version&gt;).</summary>
    public static string Version { get; } = Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } iv
        ? iv.Split('+')[0]
        : Asm.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Short commit id the build was made from (the SDK appends it to the informational version), or null.</summary>
    public static string? Commit { get; } = Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } iv
        && iv.IndexOf('+') is int i and >= 0 && iv.Length > i + 1
        ? iv[(i + 1)..Math.Min(iv.Length, i + 8)]
        : null;

    public static string Copyright { get; } = Asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    /// <summary>"1.0.0" or "1.0.0 (abc1234)".</summary>
    public static string DisplayVersion => Commit is null ? Version : $"{Version} ({Commit})";

    /// <summary>A file of the repository (LICENSE, notices). A copy of both is also shipped next to Seekbeam.exe.</summary>
    public static string RepoFileUrl(string fileName) => $"{ProjectUrl}/blob/main/{fileName}";

    /// <summary>Documentation page in the repository, in the UI language when a translation exists (X.fr.md).</summary>
    public static string DocUrl(string docPathWithoutExtension, string uiLanguage) =>
        $"{ProjectUrl}/blob/main/{docPathWithoutExtension}{(uiLanguage == "fr" ? ".fr" : "")}.md";
}
