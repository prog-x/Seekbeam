using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using Seekbeam.App.Localization;
using Seekbeam.Core.Apps;
using Seekbeam.Core.Content;
using Seekbeam.Core.Indexing;
using Seekbeam.Core.Search;

namespace Seekbeam.App.Windows;

/// <summary>Display model of one result row.</summary>
public sealed class ResultItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    public SearchResult Result { get; }
    public ResultItem(SearchResult r) { Result = r; }

    public string Name => Result.Name;
    public IReadOnlyList<Highlight> Highlights => Result.Highlights;
    public string? Snippet => Result.Snippet;
    public IReadOnlyList<Highlight> SnippetHighlights => Result.SnippetHighlights;
    public bool HasSnippet => !string.IsNullOrEmpty(Result.Snippet);

    public string SubText => Result.Kind switch
    {
        ResultKind.App when Result.App?.Source == AppSource.Alias =>
            Result.App.Target + (AppEntry.JoinArguments(Result.App.LaunchArguments, Result.Arguments) is { } a ? " " + a : ""),
        ResultKind.App => Result.App?.Target is { Length: > 0 } t && !t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? t : L.T("Kind_Application"),
        _ => Result.Directory ?? Result.FullPath,
    };

    public string Detail => Result.Kind switch
    {
        ResultKind.App => Result.App switch
        {
            { AppUserModelId: not null } => L.T("Kind_StoreApp"),
            { Source: AppSource.RunCommand } => L.T("Kind_Command"),
            { Source: AppSource.Alias, RunAsAdmin: true } => L.T("Kind_AliasAdmin"),
            { Source: AppSource.Alias } => L.T("Kind_Alias"),
            _ => L.T("Kind_Application"),
        },
        ResultKind.Folder => L.T("Kind_Folder"),
        _ => FormatSize(Result.Size) + (Result.ModifiedUtc > DateTime.MinValue ? " · " + Result.ModifiedUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : ""),
    };

    public string FuzzyMark => Result.Fuzzy ? "≈" : "";

    // ------------------------------------------------------------ content-index dot (files only, filled in after display)

    private ContentStateInfo? _content;

    /// <summary>Content-index state; null while unknown and for folders and applications (no dot shown).</summary>
    public ContentStateInfo? Content
    {
        get => _content;
        set
        {
            _content = value;
            foreach (var p in new[] { nameof(Content), nameof(ContentGlyph), nameof(ContentBrushKey), nameof(ContentTip), nameof(HasContent) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }
    }

    public bool HasContent => _content != null;

    /// <summary>● indexed · ◌ pending · ○ not indexed / no text · ⚠ failed.</summary>
    public string ContentGlyph => _content?.State switch
    {
        ContentState.Indexed => "●",
        ContentState.Pending => "◌",
        ContentState.Failed => "⚠",
        ContentState.NoText or ContentState.NotIndexed => "○",
        _ => "",
    };

    /// <summary>Theme brush resource key of the dot.</summary>
    public string ContentBrushKey => _content?.State switch
    {
        ContentState.Indexed => "OkBrush",
        ContentState.Failed => "WarnBrush",
        ContentState.Pending => "AccentBrush",
        _ => "SubFgBrush",
    };

    public string? ContentTip => _content is not { } c ? null : c.State switch
    {
        ContentState.Indexed => L.T("Content_Indexed"),
        ContentState.NoText => L.T("Content_NoText"),
        ContentState.Pending => L.T("Content_Pending"),
        ContentState.Failed => L.T("Content_Failed"),
        _ => L.F("Content_NotIndexed", L.T("ContentWhy_" + ReasonKey(c))),
    };

    private static string ReasonKey(ContentStateInfo c) => c.DocStatus switch
    {
        ContentStatus.Encrypted => "Encrypted",
        ContentStatus.TooLarge => nameof(ContentIneligibility.TooLarge),
        ContentStatus.Skipped => nameof(ContentIneligibility.UnsupportedType),
        _ => c.Reason.ToString(),
    };

    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => L.F("Size_B", bytes),
        < 1024 * 1024 => L.F("Size_KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => L.F("Size_MB", bytes / 1048576.0),
        _ => L.F("Size_GB", bytes / 1073741824.0),
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}
