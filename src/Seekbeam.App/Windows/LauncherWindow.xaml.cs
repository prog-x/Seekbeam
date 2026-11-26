using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Seekbeam.App.Native;
using Seekbeam.App.Localization;
using Seekbeam.App.Services;
using Seekbeam.Core.Config;
using Seekbeam.Core.Engine;
using Seekbeam.Core.Input;
using Seekbeam.Core.Search;

namespace Seekbeam.App.Windows;

/// <summary>
/// Floating launcher. Pre-created and hidden at startup so that showing it is instantaneous. Each keystroke cancels the
/// previous search; searches run off the UI thread and only the latest result set is displayed.
/// </summary>
public partial class LauncherWindow : Window
{
    private readonly SeekbeamEngine _engine;
    private readonly IconCache _icons;
    private readonly ObservableCollection<ResultItem> _items = [];
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _shown;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;
    public event Action<string>? DeepSearchRequested;
    public event Action? SettingsRequested;

    public LauncherWindow(SeekbeamEngine engine, IconCache icons)
    {
        InitializeComponent();
        _engine = engine;
        _icons = icons;
        Results.ItemsSource = _items;
        // Click elsewhere = Escape. A context menu open on a result is part of the launcher: it does not count as leaving it.
        // (Results.ContextMenu is null until the first right-click: "!menu?.IsOpen == true" was then always false.)
        Deactivated += (_, _) => { if (_engine.Config.Current.Launcher.CloseOnFocusLoss && Results.ContextMenu?.IsOpen != true) HideLauncher(); };
        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateIdleStatus();
        SourceInitialized += (_, _) => Theme.StyleWindow(this, Theme.IsDark(_engine.Config.Current.Launcher.Theme));
    }

    public bool IsShown => _shown;

    /// <summary>Raised with a short message for the user when a navigation could not be done (shown as a notification).</summary>
    public event Action<string>? NoticeRequested;

    /// <summary>Keys typed in dialogs before the launcher had the focus (direct typing).</summary>
    internal DialogKeyRouter? Router { get; set; }

    // "Go to folder" mode (TD-20): the dialog or Explorer window the launcher drives, or null for the normal launcher.
    private NavTarget? _target;
    private bool _openedByTyping;
    private (double Bottom, bool Above)? _anchor;

    private bool Navigating => _target != null;

    public void Toggle()
    {
        if (_shown && IsActive) HideLauncher(); else ShowLauncher();
    }

    public void ShowLauncher(string? text = null)
    {
        SetMode(null, false);
        var cfg = _engine.Config.Current.Launcher;
        Width = cfg.Width;
        Position(cfg.Position);
        ShowCore(text, selectAll: true);
    }

    /// <summary>
    /// Opens the launcher in "go to folder" mode for an Open/Save/folder dialog or an Explorer window, glued under it.
    /// <paramref name="typed"/>: opened by direct typing in the dialog's file list (<paramref name="text"/> is that character).
    /// </summary>
    internal void ShowForTarget(NavTarget target, string? text, bool typed)
    {
        SetMode(target, typed);
        PositionNear(target.Hwnd);
        ShowCore(text, selectAll: !typed);
        if (typed)
        {
            // Characters typed while the launcher was appearing were captured by the keyboard hook: append them.
            string more = Router?.EndCapture() ?? "";
            QueryBox.Text = (text ?? "") + more;
            QueryBox.CaretIndex = QueryBox.Text.Length;
        }
        if (QueryBox.Text.Length == 0) ShowLocations(++_generation);
    }

    private void SetMode(NavTarget? target, bool typed)
    {
        bool changed = (_target == null) != (target == null) || _target?.Kind != target?.Kind;
        _target = target;
        _openedByTyping = typed;
        _anchor = null;
        Hint.Text = target == null ? L.T("Launcher_Hint") : L.T(target.IsDialog ? "Launcher_HintDialog" : "Launcher_HintExplorer");
        KeysHint.Text = target == null ? L.T("Launcher_Keys")
            : target.IsDialog && _engine.Config.Current.Navigation.QuickSwitch ? L.T("Launcher_KeysDialog") : L.T("Launcher_KeysNavigate");
        if (changed) { _items.Clear(); QueryBox.Text = ""; }
    }

    private void ShowCore(string? text, bool selectAll)
    {
        Topmost = _engine.Config.Current.Launcher.Topmost || Navigating;
        if (text != null) { QueryBox.Text = text; }
        Show();
        Activate();
        ForceForeground();
        QueryBox.Focus();
        Keyboard.Focus(QueryBox);
        if (selectAll) QueryBox.SelectAll();
        _shown = true;
        _statusTimer.Start();
        UpdateIdleStatus();
    }

    public void HideLauncher()
    {
        if (!_shown) return;
        _shown = false;
        _statusTimer.Stop();
        _cts?.Cancel();
        Hide();
    }

    /// <summary>
    /// Glued under the bottom edge of the target, aligned on its left edge and as wide as it (within bounds); above it
    /// when there is no room below. Kept anchored while the list grows (see <see cref="OnRenderSizeChanged"/>).
    /// </summary>
    private void PositionNear(IntPtr hwnd)
    {
        if (NavigationTargets.Bounds(hwnd) is not { } r) { Position(_engine.Config.Current.Launcher.Position); return; }
        var center = new Win32.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        IntPtr mon = Win32.MonitorFromPoint(center, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfoW(mon, ref mi) || mi.rcWork.Right <= mi.rcWork.Left) { Position(_engine.Config.Current.Launcher.Position); return; }
        double scale = Win32.GetDpiForMonitor(mon, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : 1;
        double waLeft = mi.rcWork.Left / scale, waTop = mi.rcWork.Top / scale, waRight = mi.rcWork.Right / scale, waBottom = mi.rcWork.Bottom / scale;
        double avail = waRight - waLeft, minWidth = Math.Min(520, avail);
        Width = Math.Clamp((r.Right - r.Left) / scale, minWidth, Math.Max(minWidth, Math.Min(1400, avail))); // narrow screens: never min > max
        Left = Math.Clamp(r.Left / scale, waLeft, Math.Max(waLeft, waRight - Width));
        double below = r.Bottom / scale, above = r.Top / scale;
        const double expected = 360; // room for a few results
        if (below + expected <= waBottom) { Top = below; _anchor = (below, false); }
        else if (above - expected >= waTop) { _anchor = (above, true); Top = above - Math.Max(ActualHeight, 120); }
        else { Top = Math.Max(waTop, waBottom - expected); _anchor = null; }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_anchor is { Above: true } a && sizeInfo.HeightChanged) Top = a.Bottom - ActualHeight;
    }

    private void Position(LauncherPosition mode)
    {
        Win32.GetCursorPos(out var pt);
        IntPtr mon = Win32.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfoW(mon, ref mi);
        double scale = 1;
        if (Win32.GetDpiForMonitor(mon, 0, out uint dpiX, out _) == 0) scale = dpiX / 96.0;
        var wa = new WindowPlacement.RectD(mi.rcWork.Left / scale, mi.rcWork.Top / scale, (mi.rcWork.Right - mi.rcWork.Left) / scale, (mi.rcWork.Bottom - mi.rcWork.Top) / scale);
        double h = Math.Max(ActualHeight, 120);
        var (l, t) = WindowPlacement.Compute(wa, pt.X / scale, pt.Y / scale, Width, h, mode);
        Left = l; Top = t;
    }

    /// <summary>Windows restricts SetForegroundWindow for background processes: attach to the foreground thread input.</summary>
    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == hwnd) return;
        uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
        uint me = Win32.GetCurrentThreadId();
        if (fgThread != me) Win32.AttachThreadInput(me, fgThread, true);
        Win32.BringWindowToTop(hwnd);
        Win32.SetForegroundWindow(hwnd);
        if (fgThread != me) Win32.AttachThreadInput(me, fgThread, false);
    }

    private void QueryBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        Hint.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RunSearch(QueryBox.Text);
    }

    private void RunSearch(string query)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        int gen = ++_generation;
        if (string.IsNullOrWhiteSpace(query))
        {
            _items.Clear();
            UpdateIdleStatus();
            if (Navigating && _shown) ShowLocations(gen);
            return;
        }
        int max = _engine.Config.Current.Launcher.MaxResults;
        var nav = _engine.Config.Current.Navigation;
        var request = new SearchRequest
        {
            Query = query, MaxResults = max, Purpose = Navigating ? SearchPurpose.Navigate : SearchPurpose.Launch,
            IncludeApps = !Navigating, PathCompletion = nav.PathCompletion,
        };
        Task.Run(() =>
        {
            try { return _engine.Search.Search(request, cts.Token); }
            catch (OperationCanceledException) { return null; }
        }).ContinueWith(t =>
        {
            if (gen != _generation) return; // stale: a newer query was typed
            if (t.Exception is { } ex) { Core.Diagnostics.Log.Warn("Launcher", "Name search: " + ex.GetBaseException().Message); return; }
            if (t.Result == null) return;
            Show(t.Result);
            // Content results: not for "go to folder", nor for a typed path (completed from the disk).
            if (!Navigating && !(nav.PathCompletion && PathCompleter.IsAbsolutePathQuery(query))) SearchContent(query, gen, max, cts.Token);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Empty query in "go to folder" mode: the folders of the open Explorer windows (Quick Switch), then favourite and
    /// recent folders. Each part has its own switch in the settings. Loaded after the window is shown.
    /// </summary>
    private void ShowLocations(int gen)
    {
        var nav = _engine.Config.Current.Navigation;
        var target = _target;
        if (target == null || (!nav.QuickSwitch && !nav.RecentFolders)) return;
        int max = Math.Max(_engine.Config.Current.Launcher.MaxResults, 8);
        Dispatcher.BeginInvoke(async () =>
        {
            if (gen != _generation || !_shown) return;
            var list = new List<SearchResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (nav.QuickSwitch)
            {
                foreach (var f in ExplorerShell.OpenFolders())
                {
                    if (!f.ActiveTab || f.Window == target.Hwnd || !seen.Add(f.Path) || list.Count >= max) continue;
                    list.Add(new SearchResult
                    {
                        Kind = ResultKind.Folder, Origin = ResultOrigin.Explorer, FullPath = f.Path, Directory = System.IO.Path.GetDirectoryName(f.Path),
                        Name = System.IO.Path.GetFileName(f.Path) is { Length: > 0 } n ? n : f.Path,
                    });
                }
            }
            if (nav.RecentFolders)
            {
                var recent = await Task.Run(() =>
                {
                    try { return _engine.RecentLocations(max * 2); }
                    catch (Exception ex) { Core.Diagnostics.Log.Warn("Launcher", "Recent folders: " + ex.Message); return []; }
                });
                foreach (var r in recent) if (list.Count < max && seen.Add(r.FullPath)) list.Add(r);
            }
            if (gen != _generation || !_shown || QueryBox.Text.Length > 0) return;
            Show(new SearchResultSet { Results = list, TotalMatches = list.Count });
            Status.Text = list.Count == 0 ? L.T("Launcher_NavNoLocation") : L.T("Launcher_NavLocations");
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Documents whose content contains the words, placed among the name results by <see cref="LauncherMerge"/> (free
    /// rows, a reserved share of a full list, above approximate name matches). Runs after the name results are on
    /// screen and after a short pause in typing, so it never delays them; dropped if a newer query was typed.
    /// </summary>
    private void SearchContent(string query, int gen, int max, CancellationToken ct)
    {
        var names = _items.Select(i => i.Result).ToList();
        int shownFiles = names.Count(r => r.EntryId > 0);
        int want = LauncherMerge.ContentRows(names.Count, max);
        if (want == 0) return;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, ct); // typing pause
                if (gen != Volatile.Read(ref _generation)) return null;
                return _engine.SearchContentForLauncher(query, want + shownFiles, ct);
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) { Core.Diagnostics.Log.Warn("Launcher", "Content search: " + ex.Message); return null; }
        }).ContinueWith(t =>
        {
            if (gen != _generation || t.Result is not { } set) return;
            try { ShowContent(names, set, max); }
            catch (Exception ex) { Core.Diagnostics.Log.Warn("Launcher", "Content results: " + ex.Message); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowContent(List<SearchResult> names, SearchResultSet set, int max)
    {
        var merged = LauncherMerge.Merge(names, set.Results, max, out int added);
        if (added == 0) return;
        var byResult = _items.ToDictionary(i => i.Result); // SearchResult has reference equality
        var selected = Selected;
        bool atTop = Results.SelectedIndex <= 0;
        _items.Clear();
        foreach (var r in merged)
        {
            if (byResult.TryGetValue(r, out var existing)) { _items.Add(existing); continue; }
            // Found in the content index: its content is indexed by definition.
            var item = new ResultItem(r) { Icon = _icons.TryGetCached(r), Content = new(Core.Content.ContentState.Indexed) };
            _items.Add(item);
            if (item.Icon == null) _icons.GetAsync(r).ContinueWith(x => item.Icon = x.Result, TaskScheduler.FromCurrentSynchronizationContext());
        }
        // Keep the user's selection if they already moved in the list; otherwise the first row.
        Results.SelectedIndex = !atTop && selected != null && _items.Contains(selected) ? _items.IndexOf(selected) : 0;
        string partial = set.ContentMatch == ContentMatchMode.AllWordsAsPrefixes ? L.T("Launcher_ContentPrefix") : "";
        Status.Text = names.Count == 0
            ? L.F("Launcher_ContentOnly", added) + partial
            : Status.Text + L.F("Launcher_ContentMore", added) + partial;
    }

    private void Show(SearchResultSet set)
    {
        _items.Clear();
        foreach (var r in set.Results)
        {
            var item = new ResultItem(r) { Icon = _icons.TryGetCached(r) };
            _items.Add(item);
            if (item.Icon == null) _icons.GetAsync(r).ContinueWith(t => item.Icon = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
        }
        if (_items.Count > 0) Results.SelectedIndex = 0;
        LoadContentStates(_generation);
        Status.Text = set.Results.Count == 0
            ? L.F("Launcher_NoResult", set.IndexedEntries, set.Elapsed.TotalMilliseconds) +
              (_engine.DepthLimitedCount() > 0 ? L.T("Launcher_DepthHint") : "")
            : L.F("Launcher_Results", set.TotalMatches, set.UsedFuzzy ? L.T("Launcher_ApproxSuffix") : "", set.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Fills the content-index dot of the file results, off the UI thread, after the results are displayed: the search
    /// itself is never delayed. A few primary-key reads per result; skipped if a newer search has replaced the list.
    /// </summary>
    private void LoadContentStates(int gen)
    {
        if (Navigating) return; // "go to folder": the content dot is not useful there
        var files = _items.Where(i => i.Result.Kind == ResultKind.File && i.Result.EntryId > 0).ToList();
        if (files.Count == 0) return;
        Task.Run(() =>
        {
            var states = new List<(ResultItem, Core.Content.ContentStateInfo)>(files.Count);
            foreach (var item in files)
            {
                if (gen != Volatile.Read(ref _generation)) return null;
                try { states.Add((item, _engine.ContentState(item.Result.EntryId))); }
                catch (Exception ex) { Core.Diagnostics.Log.Debug("Launcher", "Content state: " + ex.Message); }
            }
            return states;
        }).ContinueWith(t =>
        {
            if (gen != _generation || t.Result == null) return;
            foreach (var (item, state) in t.Result) item.Content = state;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private int _busyTicks;

    private void UpdateIdleStatus()
    {
        var s = _engine.Indexer.Stats();
        bool busy = s.Busy || (_engine.ContentIndexer?.Stats().Busy ?? false);
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        // Name indexing in progress: results may be incomplete. Shown after 2 s so that a short watcher event
        // (one file changed) does not make the banner flicker.
        _busyTicks = s.Busy ? _busyTicks + 1 : 0;
        bool banner = _busyTicks >= 2;
        IndexingBanner.Visibility = banner ? Visibility.Visible : Visibility.Collapsed;
        if (banner) IndexingBannerText.Text = L.F("Launcher_IndexingBanner", Math.Max(s.QueueLength, 1));
        if (QueryBox.Text.Length > 0) return;
        if (_target is { } t) { if (_items.Count == 0) Status.Text = L.T("NavTarget_" + t.Kind); } // which window is driven
        else Status.Text = L.F("Launcher_Idle", _engine.NameIndex.LiveCount, _engine.Apps.Count);
    }

    private ResultItem? Selected => Results.SelectedItem as ResultItem;

    private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Escape:
                if (QueryBox.Text.Length > 0 && mods == ModifierKeys.Shift) QueryBox.Clear();
                else if (Navigating) BackToTarget(giveBackText: _openedByTyping);
                else HideLauncher();
                e.Handled = true; break;
            case Key.Enter or Key.Return when Navigating && mods == ModifierKeys.None:
                if (Selected is { } target) _ = NavigateTo(target.Result);
                e.Handled = true; break;
            case Key.G when Navigating && mods == ModifierKeys.Control && _target!.IsDialog && _engine.Config.Current.Navigation.QuickSwitch:
                _ = QuickSwitch(_target!);
                e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.PageDown: Move(5); e.Handled = true; break;
            case Key.PageUp: Move(-5); e.Handled = true; break;
            case Key.Enter or Key.Return:
                if (Selected is { } item)
                {
                    var r = item.Result;
                    if (mods == ModifierKeys.Control) ShellActions.OpenContainingFolder(r);
                    else if (mods == ModifierKeys.Shift) { ShellActions.RunAsAdministrator(r); _engine.RecordOpen(r); }
                    else if (mods == ModifierKeys.Alt) ShellActions.Properties(r);
                    else ResultActions.Open(r, _engine);
                    _engine.Usage.RecordQuery(QueryBox.Text);
                    HideLauncher();
                }
                e.Handled = true; break;
            case Key.System when e.SystemKey == Key.Enter && Selected is { } si:
                ShellActions.Properties(si.Result); HideLauncher(); e.Handled = true; break;
            case Key.Tab or Key.Apps:
                if (Selected != null) OpenMenu();
                e.Handled = true; break;
            case Key.C when mods.HasFlag(ModifierKeys.Control) && QueryBox.SelectionLength == 0 && Selected is { } ci:
                ShellActions.CopyText(mods.HasFlag(ModifierKeys.Shift) ? ci.Result.Name : ci.Result.FullPath);
                Status.Text = L.T("Launcher_Copied");
                e.Handled = true; break;
            case Key.F when mods == ModifierKeys.Control && Selected is { } fi:
                string key = fi.Result.Kind == ResultKind.App ? fi.Result.App!.Key : fi.Result.FullPath;
                bool fav = !_engine.Usage.IsFavorite(key);
                _engine.Usage.SetFavorite(key, fi.Result.EntryId > 0 ? fi.Result.EntryId : null, fav);
                Status.Text = fav ? L.T("Launcher_FavAdded") : L.T("Launcher_FavRemoved");
                e.Handled = true; break;
            case Key.D when mods == ModifierKeys.Control:
                string q = QueryBox.Text;
                HideLauncher();
                DeepSearchRequested?.Invoke(q);
                e.Handled = true; break;
            case Key.OemComma when mods == ModifierKeys.Control:
                HideLauncher(); SettingsRequested?.Invoke(); e.Handled = true; break;
        }
    }

    private void Move(int delta)
    {
        if (_items.Count == 0) return;
        int i = Math.Clamp(Results.SelectedIndex + delta, 0, _items.Count - 1);
        Results.SelectedIndex = i;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void OpenMenu()
    {
        if (Selected is not { } item) return;
        var menu = ResultActions.BuildMenu(item.Result, _engine, HideLauncher);
        var container = Results.ItemContainerGenerator.ContainerFromItem(item) as UIElement;
        menu.PlacementTarget = container ?? Results;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        // Menu dismissed by a click outside Seekbeam: the launcher lost the focus meanwhile, close it as well.
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_shown) return;
            if (!IsActive && _engine.Config.Current.Launcher.CloseOnFocusLoss) HideLauncher(); else QueryBox.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
        Results.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not { } item) return;
        if (Navigating) { _ = NavigateTo(item.Result); return; }
        ResultActions.Open(item.Result, _engine);
        HideLauncher();
    }

    // ------------------------------------------------------------------ "go to folder" (TD-20)

    /// <summary>Closes the launcher and gives the focus back to the dialog; with direct typing, Esc also gives it the letters.</summary>
    private void BackToTarget(bool giveBackText)
    {
        string typed = QueryBox.Text;
        var t = _target;
        HideLauncher();
        if (t == null || !Win32.IsWindow(t.Hwnd)) return;
        Win32.SetForegroundWindow(t.Hwnd);
        if (giveBackText && typed.Length is > 0 and <= 64)
            Dispatcher.BeginInvoke(async () => { await Task.Delay(80); if (Win32.GetForegroundWindow() == t.Hwnd) Win32.TypeText(typed); });
    }

    /// <summary>
    /// Sends the target to the chosen result: a folder, or the folder of a file (whose name then goes in the file-name box
    /// of an Open dialog, or is selected in Explorer). Seekbeam only navigates: the user validates the dialog.
    /// </summary>
    private async Task NavigateTo(SearchResult r)
    {
        var t = _target;
        if (t == null || r.Kind == ResultKind.App) return;
        string? folder = r.Kind == ResultKind.Folder ? r.FullPath : System.IO.Path.GetDirectoryName(r.FullPath);
        string? file = r.Kind == ResultKind.File ? System.IO.Path.GetFileName(r.FullPath) : null;
        if (string.IsNullOrEmpty(folder)) return;
        _engine.Usage.RecordQuery(QueryBox.Text);
        HideLauncher();
        await NavigateTarget(t, folder, file, r.Kind == ResultKind.Folder ? r.EntryId : 0);
    }

    /// <summary>Ctrl+G: the target goes to the folder of the front-most Explorer window.</summary>
    internal async Task QuickSwitch(NavTarget t)
    {
        string? folder = ExplorerShell.FrontFolder(except: t.Hwnd);
        if (folder == null) { NoticeRequested?.Invoke(L.T("Nav_NoExplorer")); return; }
        if (_shown) HideLauncher();
        await NavigateTarget(t, folder, null, 0);
    }

    private async Task NavigateTarget(NavTarget t, string folder, string? file, long entryId)
    {
        if (!Win32.IsWindow(t.Hwnd)) { NoticeRequested?.Invoke(L.T("Nav_TargetGone")); return; }
        Win32.SetForegroundWindow(t.Hwnd);
        NavOutcome outcome;
        if (t.Kind == NavTargetKind.Explorer)
            outcome = await ExplorerShell.NavigateAsync(t.Hwnd, folder, file) ? NavOutcome.Done : NavOutcome.TargetGone;
        else if (!t.CanNavigate) outcome = NavOutcome.Unsupported;
        else outcome = await Task.Run(() => NavigationTargets.NavigateDialog(t, folder, file));
        switch (outcome)
        {
            case NavOutcome.Done:
                _engine.RecordNavigation(folder, entryId);
                break;
            case NavOutcome.FolderMissing:
                NoticeRequested?.Invoke(L.F("Nav_FolderMissing", folder));
                // Only the stale entry must go: a shallow rescan of its parent, at low priority (the parent may be a drive).
                if (System.IO.Path.GetDirectoryName(folder) is { } parent)
                    _engine.Indexer.Post(new Core.Indexing.RescanPathCommand(parent, Core.Catalog.QueuePriority.Reconcile, Core.Catalog.ScanMode.Shallow));
                break;
            case NavOutcome.TargetGone:
                NoticeRequested?.Invoke(L.T("Nav_TargetGone"));
                break;
            case NavOutcome.Timeout:
                // The dialog is drivable but did not move in time (slow network folder…): its text was restored.
                ShellActions.CopyText(folder);
                NoticeRequested?.Invoke(L.F("Nav_TimeoutCopied", folder));
                break;
            default:
                // Cannot drive this window (old folder browser, elevated application…): the path is ready to paste.
                // Not recorded as a navigation: the dialog did not go there.
                ShellActions.CopyText(folder);
                NoticeRequested?.Invoke(L.F(outcome == NavOutcome.Blocked ? "Nav_BlockedCopied" : "Nav_UnsupportedCopied", folder));
                break;
        }
    }

    private void Results_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && System.Windows.Media.VisualTreeHelper.GetParent(d) != null)
        {
            var lbi = ItemsControlFromElement(d);
            if (lbi != null) Results.SelectedItem = lbi;
        }
        OpenMenu();
        e.Handled = true;
    }

    private static object? ItemsControlFromElement(DependencyObject d)
    {
        while (d != null && d is not System.Windows.Controls.ListBoxItem) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return (d as System.Windows.Controls.ListBoxItem)?.DataContext;
    }

    private bool _destroying;

    /// <summary>Really closes the window (used when the UI language changes and the launcher is rebuilt).</summary>
    public void Destroy()
    {
        HideLauncher();
        _destroying = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The launcher lives for the whole session: closing only hides it.
        if (!App.IsShuttingDown && !_destroying) { e.Cancel = true; HideLauncher(); }
        base.OnClosing(e);
    }
}
