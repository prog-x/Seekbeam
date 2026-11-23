using System.Windows;
using Seekbeam.App.Localization;
using Seekbeam.App.Services;
using Seekbeam.Core.Engine;

namespace Seekbeam.App.Windows;

/// <summary>Live diagnostics (CDC §27): progress, queues, sizes, resources, roots, errors, and control commands.</summary>
public partial class DashboardWindow : Window
{
    private readonly SeekbeamEngine _engine;
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    public DashboardWindow(SeekbeamEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await Refresh();
        Loaded += async (_, _) =>
        {
            // The census is not recomputed every second: on opening if older than 10 minutes, on "Refresh",
            // and by the engine itself at the end of indexing.
            if (_engine.LastContentCoverage is not { } last || DateTime.UtcNow - last.ComputedUtc > TimeSpan.FromMinutes(10))
                _ = _engine.RefreshContentCoverage();
            await Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
        SourceInitialized += (_, _) => Theme.StyleWindow(this, Theme.IsDark(engine.Config.Current.Launcher.Theme));
    }

    private static string Mb(long b) => L.F("Size_MBValue", b / 1048576.0);
    private static string When(DateTime? d) => d?.ToLocalTime().ToString("g") ?? "—";
    private static string Lines(params string?[] lines) => string.Join("\n", lines.Where(l => !string.IsNullOrEmpty(l)));

    private async Task Refresh()
    {
        var s = await Task.Run(() => _engine.Stats());
        var n = s.Names;
        NamesText.Text = Lines(
            L.F("Dash_NamesEntries", s.IndexedEntries, s.Files, s.Directories),
            L.F("Dash_NamesApps", s.Apps),
            L.F("Dash_NamesIndex", Mb(s.NameIndexBytes), Mb(s.CatalogBytes)),
            L.F("Dash_NamesDelta", s.DeltaRecords),
            L.F("Dash_NamesQueue", n.QueueLength, n.Busy ? L.F("Dash_Rate", n.EntriesPerSecond) : L.T("Dash_Idle")),
            L.F("Dash_NamesCounts", n.Added, n.Updated, n.Deleted, n.Errors),
            L.F("Dash_Watch", s.Watch.Watchers, s.Watch.EventsReceived, s.Watch.Overflows),
            L.F("Dash_Discovery", s.DiscoveryEvents),
            n.CurrentDirectory is { } cd ? L.F("Dash_Current", cd) : null);
        var c = s.Content;
        ContentText.Text = Lines(
            L.F("Dash_ContentDocs", s.ContentDocs, s.ContentFailed),
            L.F("Dash_ContentSize", Mb(s.ContentBytes)),
            c.Deferred > 0 ? L.F("Dash_ContentQueueDeferred", c.QueueLength, c.Deferred) : L.F("Dash_ContentQueue", c.QueueLength),
            L.F("Dash_ContentRead", c.Busy ? L.F("Dash_ContentRate", c.FilesPerSecond) : L.T("Dash_Idle"), Mb(c.BytesRead)),
            L.F("Dash_ContentSkipped", c.Skipped),
            L.F("Dash_Extractor", c.HostPrivateBytes > 0 ? Mb(c.HostPrivateBytes)
                : c.QueueLength == 0 ? L.T("Dash_ExtractorIdle") : L.T("Dash_ExtractorStopped"), c.HostRestarts),
            c.HostIsolation is { } iso ? L.F("Dash_Isolation", L.T("Sandbox_" + iso)) : null,
            c.CurrentFile is { } cf ? L.F("Dash_Current", cf) : null);
        ShowCoverage();
        var sys = s.System;
        string State(bool on) => on ? L.T("Dash_Active") : L.T("Dash_Paused");
        ResourcesText.Text = Lines(
            L.F("Dash_Profile", L.T("Profile_" + s.Profile.Name)) + (string.IsNullOrEmpty(s.Profile.Reason) ? "" : L.F("Dash_ProfileReason", L.T("Reason_" + s.Profile.Reason))),
            L.F("Dash_Activity", State(s.Profile.NamesAllowed && !s.NamesPaused), State(s.Profile.ContentAllowed && !s.ContentPaused)),
            L.F("Dash_Process", Mb(s.ProcessPrivateBytes), Mb(s.ProcessWorkingSet), s.ProcessCpuPercent),
            sys != null ? L.F("Dash_System", sys.CpuPercent, sys.MemoryLoadPercent, sys.UserIdleSeconds)
                          + (sys.OnBattery ? L.T("Dash_Battery") : "") + (sys.FullscreenOrBusy ? L.T("Dash_Fullscreen") : "") : null,
            L.F("Dash_HostCaps", s.Profile.HostMemoryMB, s.Profile.HostCpuPercent));
        PauseBtn.Content = s.NamesPaused ? L.T("Dash_Resume") : L.T("Dash_Pause");
        RootsView.ItemsSource = s.Roots.Select(r => new
        {
            r.Path, StateText = r.State == 1 ? L.T("State_Unavailable") : L.T("State_Ok"), DepthText = DepthSummary(r),
            FullScan = When(r.LastFullScanUtc), Reconcile = When(r.LastReconcileUtc),
        }).ToList();
        await RefreshDepthPanel(s.Roots);
        ErrorsView.ItemsSource = s.RecentErrors.Select(e => new { Time = e.TimeUtc.ToLocalTime().ToString("g"), e.Component, e.Path, e.Message }).ToList();
    }

    private Core.Content.ContentCoverageReport? _coverageShown;

    /// <summary>Content coverage block: formatting only (the census itself runs in the engine, in the background).</summary>
    private void ShowCoverage()
    {
        var r = _engine.LastContentCoverage;
        bool running = _engine.ContentCoverageRunning;
        CoverageStatus.Text = running ? L.T("Dash_CoverageComputing")
            : r == null ? L.T("Dash_CoverageNone")
            : L.F("Dash_CoverageAt", r.ComputedUtc.ToLocalTime().ToString("t"), r.Elapsed.TotalSeconds);
        if (r == null) { CoverageText.Text = ""; CoverageDetails.Visibility = Visibility.Collapsed; return; }
        if (ReferenceEquals(r, _coverageShown)) return; // unchanged: keep the expander state and avoid re-layout every second
        _coverageShown = r;
        CoverageText.Text = Lines(
            L.F("Dash_CoverageFiles", r.Files),
            L.F("Dash_CoverageStates", r.Indexed, r.NoText, r.Failed, r.Pending, r.NotIndexed));
        // Reasons decided by the rules and by the extractor are merged under the same label (e.g. "file too large").
        var lines = new Dictionary<string, long>();
        foreach (var (reason, n) in r.ByReason) lines[reason.ToString()] = lines.GetValueOrDefault(reason.ToString()) + n;
        foreach (var (status, n) in r.ByExtractorStatus)
        {
            string key = status switch
            {
                Core.Content.ContentStatus.Encrypted => "Encrypted",
                Core.Content.ContentStatus.TooLarge => nameof(Core.Indexing.ContentIneligibility.TooLarge),
                _ => nameof(Core.Indexing.ContentIneligibility.UnsupportedType),
            };
            lines[key] = lines.GetValueOrDefault(key) + n;
        }
        CoverageDetails.Visibility = r.NotIndexed > 0 ? Visibility.Visible : Visibility.Collapsed;
        CoverageDetails.Header = L.F("Dash_CoverageNotEligible", r.NotIndexed);
        CoverageDetailText.Text = L.T("Dash_CoverageNameOnly") + "\n" + string.Join("\n", lines.OrderByDescending(kv => kv.Value).Select(kv =>
        {
            string line = L.F("Dash_CoverageItem", L.T("ContentWhy_" + kv.Key), kv.Value);
            if (kv.Key == nameof(Core.Indexing.ContentIneligibility.UnsupportedType) && r.TopUnsupportedExtensions.Count > 0)
                line += L.F("Dash_CoverageExts", string.Join(", ", r.TopUnsupportedExtensions.Select(e =>
                    L.F("Dash_CoverageExt", e.Extension.Length == 0 ? L.T("Dash_CoverageNoExt") : e.Extension, e.Count))));
            return line;
        }));
    }

    private void CoverageRefresh_Click(object sender, RoutedEventArgs e)
    {
        _ = _engine.RefreshContentCoverage();
        ShowCoverage();
    }

    private static string DepthSummary(RootStatus r) =>
        r.MaxDepth < 0 ? L.T("Depth_Unlimited")
        : r.DepthLimitedDirs == 0 ? L.F("Depth_Levels", r.MaxDepth)
        : L.F("Depth_LevelsLimited", r.MaxDepth, r.DepthLimitedDirs);

    private long _depthShown = -1;

    private async Task RefreshDepthPanel(IReadOnlyList<RootStatus> roots)
    {
        long dirs = roots.Sum(r => r.DepthLimitedDirs), hidden = roots.Sum(r => r.DepthHiddenEntries);
        if (dirs == 0) { DepthPanel.Visibility = Visibility.Collapsed; _depthShown = 0; return; }
        DepthPanel.Visibility = Visibility.Visible;
        if (dirs == _depthShown) return; // list unchanged: avoid resetting the selection every second
        _depthShown = dirs;
        DepthText.Text = L.F("Dash_DepthText", dirs, hidden);
        var list = await Task.Run(() => _engine.DepthLimitedDirectories(null, 50));
        DepthList.ItemsSource = list.Select(d => L.F("Dash_DepthItem", d.Path, d.Hidden)).ToList();
        DepthList.Tag = list;
    }

    private void DepthList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DepthList.Tag is IReadOnlyList<(string Path, int Hidden)> list && DepthList.SelectedIndex is >= 0 and var i && i < list.Count)
            ShellActions.Open(new Core.Search.SearchResult { Kind = Core.Search.ResultKind.Folder, FullPath = list[i].Path });
    }

    private void DepthSettings_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowSettings();

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.Resources == null) return;
        if (_engine.Resources.IsPaused(Core.Resources.WorkClass.Names)) _engine.Resources.Resume(); else _engine.Resources.Pause();
    }
    private void Reconcile_Click(object sender, RoutedEventArgs e) => _engine.Reconcile();
    private void ReindexNames_Click(object sender, RoutedEventArgs e) => _engine.ReindexNames();
    private void ReindexContent_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(L.T("Dash_ReindexContentConfirm"), "Seekbeam", MessageBoxButton.OKCancel) == MessageBoxResult.OK) _engine.ReindexContent();
    }
    private void Maintenance_Click(object sender, RoutedEventArgs e) => Task.Run(() => { _engine.NameIndex.MaybeMerge(force: true); _engine.ContentDb.Optimize(); _engine.Catalog.Checkpoint(); });
    private void ClearErrors_Click(object sender, RoutedEventArgs e) => _engine.ClearErrors();
    private void Logs_Click(object sender, RoutedEventArgs e) =>
        ShellActions.Open(new Core.Search.SearchResult { Kind = Core.Search.ResultKind.Folder, FullPath = _engine.Storage.LogDir });
}
