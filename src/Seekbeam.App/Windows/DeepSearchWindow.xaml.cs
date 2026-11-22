using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Seekbeam.App.Localization;
using Seekbeam.App.Services;
using Seekbeam.Core.Content;
using Seekbeam.Core.Engine;
using Seekbeam.Core.Search;

namespace Seekbeam.App.Windows;

/// <summary>Deep search: full text + names, filters, snippets and preview (CDC §15.2, §18).</summary>
public partial class DeepSearchWindow : Window
{
    private readonly SeekbeamEngine _engine;
    private readonly IconCache _icons;
    private readonly ObservableCollection<ResultItem> _items = [];
    private CancellationTokenSource? _cts;
    private int _gen;
    private readonly System.Windows.Threading.DispatcherTimer _debounce;

    public DeepSearchWindow(SeekbeamEngine engine, IconCache icons)
    {
        InitializeComponent();
        _engine = engine;
        _icons = icons;
        Results.ItemsSource = _items;
        LocationBox.Items.Add(L.T("Deep_AllLocations"));
        foreach (var r in engine.Config.Current.Roots.Where(r => r.Enabled)) LocationBox.Items.Add(r.Path);
        LocationBox.SelectedIndex = 0;
        _debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };
        SourceInitialized += (_, _) => Theme.StyleWindow(this, Theme.IsDark(engine.Config.Current.Launcher.Theme));
        Loaded += (_, _) => QueryBox.Focus();
    }

    public void SetQuery(string q)
    {
        QueryBox.Text = q;
        QueryBox.CaretIndex = q.Length;
        RunSearch();
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) { _debounce.Stop(); _debounce.Start(); }
    private void Filter_Changed(object sender, EventArgs e) { if (IsLoaded) { _debounce.Stop(); _debounce.Start(); } }
    private void Search_Click(object sender, RoutedEventArgs e) => RunSearch();
    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _items.Count > 0) { Results.SelectedIndex = 0; (Results.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus(); }
        if (e.Key == Key.Escape) Close();
    }

    private DeepSearchRequest BuildRequest()
    {
        DateTime now = DateTime.UtcNow;
        (DateTime? after, DateTime? before) = DateBox.SelectedIndex switch
        {
            1 => ((DateTime?)DateTime.Today.ToUniversalTime(), (DateTime?)null),
            2 => (now.AddDays(-7), null),
            3 => (now.AddDays(-30), null),
            4 => (new DateTime(DateTime.Today.Year, 1, 1).ToUniversalTime(), null),
            5 => (null, now.AddYears(-1)),
            _ => (null, null),
        };
        (long? min, long? max) = SizeBox.SelectedIndex switch
        {
            1 => ((long?)null, (long?)(100 * 1024)),
            2 => (100 * 1024, 10L * 1024 * 1024),
            3 => (10L * 1024 * 1024, null),
            _ => (null, null),
        };
        return new DeepSearchRequest
        {
            Query = QueryBox.Text,
            Scope = ScopeBox.SelectedIndex switch { 1 => DeepScope.ContentOnly, 2 => DeepScope.NamesOnly, _ => DeepScope.NamesAndContent },
            Extensions = ExtBox.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries),
            Location = LocationBox.SelectedIndex > 0 ? LocationBox.SelectedItem as string : null,
            ModifiedAfterUtc = after, ModifiedBeforeUtc = before, MinSize = min, MaxSize = max, MaxResults = 200,
        };
    }

    private void RunSearch()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        int gen = ++_gen;
        if (string.IsNullOrWhiteSpace(QueryBox.Text)) { _items.Clear(); Status.Text = ""; return; }
        var req = BuildRequest();
        Status.Text = L.T("Deep_Searching");
        Task.Run(() =>
        {
            try { return _engine.DeepSearch.Search(req, cts.Token); }
            catch (OperationCanceledException) { return null; }
        }).ContinueWith(t =>
        {
            if (gen != _gen || t.Result == null) return;
            _items.Clear();
            foreach (var r in t.Result.Results)
            {
                var item = new ResultItem(r) { Icon = _icons.TryGetCached(r) };
                _items.Add(item);
                if (item.Icon == null) _icons.GetAsync(r).ContinueWith(x => item.Icon = x.Result, TaskScheduler.FromCurrentSynchronizationContext());
            }
            var cs = _engine.ContentIndexer?.Stats();
            Status.Text = L.F("Deep_Status", t.Result.Results.Count, t.Result.TotalMatches, t.Result.Elapsed.TotalMilliseconds) +
                          (cs is { QueueLength: > 0 } ? L.F("Deep_StatusContentQueue", cs.QueueLength) : "") +
                          // The strict query found nothing in the contents: say how it was relaxed.
                          t.Result.ContentMatch switch
                          {
                              Core.Search.ContentMatchMode.AllWordsAsPrefixes => L.T("Deep_MatchPrefix"),
                              Core.Search.ContentMatchMode.SomeWords => L.T("Deep_MatchSome"),
                              _ => "",
                          };
            if (_items.Count > 0) Results.SelectedIndex = 0;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private ResultItem? Selected => Results.SelectedItem as ResultItem;

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } item) return;
        var r = item.Result;
        PreviewTitle.Text = r.Name;
        string info = r.FullPath;
        if (r.Kind == ResultKind.File) info += "\n" + L.F("Deep_InfoModified", ResultItem.FormatSize(r.Size), r.ModifiedUtc.ToLocalTime());
        var doc = r.EntryId > 0 ? _engine.ContentDb.GetInfo(r.EntryId) : null;
        if (doc != null) info += "\n" + L.F("Deep_InfoContent", L.T("ContentStatus_" + doc.Status), doc.Chars, doc.Extractor);
        PreviewInfo.Text = info;
        // Preview from the stored extracted text (never re-reads or alters the source).
        var dq = DeepSearch.Parse(QueryBox.Text);
        string? text = r.EntryId > 0 ? _engine.ContentDb.GetText(r.EntryId) : null;
        if (text == null) { PreviewText.SourceText = r.Kind == ResultKind.File ? L.T("Deep_NoText") : ""; PreviewText.Highlights = []; return; }
        if (text.Length > 20_000)
        {
            var (snip, _) = Snippets.Build(text, dq.PositiveTerms, 19_000);
            text = snip;
        }
        PreviewText.SourceText = text;
        PreviewText.Highlights = AllHighlights(text, dq.PositiveTerms);
    }

    private static IReadOnlyList<Highlight> AllHighlights(string text, List<string> terms)
    {
        var res = new List<Highlight>();
        foreach (var t in terms)
        {
            int i = 0;
            while (i < text.Length && res.Count < 500)
            {
                int p = System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, t, i,
                    System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace);
                if (p < 0) break;
                res.Add(new Highlight(p, t.Length));
                i = p + t.Length;
            }
        }
        return res.OrderBy(h => h.Start).ToList();
    }

    private void Open_Click(object sender, RoutedEventArgs e) { if (Selected is { } i) ResultActions.Open(i.Result, _engine); }
    private void Folder_Click(object sender, RoutedEventArgs e) { if (Selected is { } i) ShellActions.OpenContainingFolder(i.Result); }
    private void Actions_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } i) return;
        var m = ResultActions.BuildMenu(i.Result, _engine);
        m.PlacementTarget = (UIElement)sender;
        m.IsOpen = true;
    }
    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (Selected is { } i) ResultActions.Open(i.Result, _engine); }
    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (Selected is not { } i) return;
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control) ShellActions.OpenContainingFolder(i.Result); else ResultActions.Open(i.Result, _engine);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { ShellActions.CopyText(i.Result.FullPath); e.Handled = true; }
        else if (e.Key is Key.Apps or Key.Tab) { Actions_Click(Results, e); e.Handled = true; }
    }
}
