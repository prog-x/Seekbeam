using System.Collections.ObjectModel;
using System.Windows;
using Seekbeam.App.Localization;
using Seekbeam.App.Services;
using Seekbeam.Core.Config;
using ThemeMode = Seekbeam.Core.Config.ThemeMode;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Engine;

namespace Seekbeam.App.Windows;

/// <summary>Settings editor. Works on a copy of the configuration; Save validates and writes it atomically.</summary>
public partial class SettingsWindow : Window
{
    private readonly SeekbeamEngine _engine;
    private readonly SeekbeamConfig _cfg;
    private readonly ObservableCollection<RootConfig> _roots;
    private readonly ObservableCollection<string> _contentFolders;
    private RootConfig? _editing;
    private readonly HotkeyService? _hotkeys;

    public SettingsWindow(SeekbeamEngine engine, HotkeyService? hotkeys = null)
    {
        InitializeComponent();
        _engine = engine;
        _hotkeys = hotkeys;
        _cfg = ConfigStore.Clone(engine.Config.Current);
        _roots = new ObservableCollection<RootConfig>(_cfg.Roots);
        RootsList.ItemsSource = _roots;
        _contentFolders = new ObservableCollection<string>(_cfg.Content.ExcludedFolders);
        ContentFoldersList.ItemsSource = _contentFolders;
        Load();
        WireHotkeyCapture();
        LoadAbout();
        SourceInitialized += (_, _) => Theme.StyleWindow(this, Theme.IsDark(_cfg.Launcher.Theme));
    }

    private static string Lines(IEnumerable<string> l) => string.Join(Environment.NewLine, l);
    private static List<string> FromLines(string s) => s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    private static List<string> FromCsv(string s) => s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.TrimStart('.')).ToList();
    private static int Int(string s, int def) => int.TryParse(s, out int v) ? v : def;

    private void Load()
    {
        var c = _cfg;
        StartWithWindows.IsChecked = c.General.StartWithWindows; ShowTray.IsChecked = c.General.ShowTrayIcon;
        DoubleCtrl.IsChecked = c.Hotkey.DoubleCtrlEnabled; CtrlSide.SelectedIndex = (int)c.Hotkey.CtrlSide; TapMs.Text = c.Hotkey.DoubleTapMs.ToString();
        AltHotkey.Hotkey = c.Hotkey.AlternativeHotkey; DeepHotkey.Hotkey = c.Hotkey.DeepSearchHotkey;
        Position.SelectedIndex = (int)c.Launcher.Position; MaxResults.Text = c.Launcher.MaxResults.ToString(); ThemeBox.SelectedIndex = (int)c.Launcher.Theme;
        TopmostBox.IsChecked = c.Launcher.Topmost; CloseOnFocusLoss.IsChecked = c.Launcher.CloseOnFocusLoss;
        GlobalExcludes.Text = Lines(c.GlobalExcludes);
        ContentEnabled.IsChecked = c.Content.Enabled; StoreText.IsChecked = c.Content.StoreTextForSnippets; MaxFileMB.Text = c.Content.MaxFileSizeMB.ToString();
        MaxChars.Text = c.Content.MaxCharsPerDocument.ToString(); Timeout.Text = c.Content.TimeoutSeconds.ToString();
        ExcludedExt.Text = string.Join(", ", c.Content.ExcludedExtensions); ExtraExt.Text = string.Join(", ", c.Content.ExtraTextExtensions);
        DisabledExtractors.Text = string.Join(", ", c.Content.DisabledExtractors);
        ArchivesEnabled.IsChecked = c.Content.Archives.Enabled; ArchiveDepth.Text = c.Content.Archives.MaxDepth.ToString(); ArchiveEntries.Text = c.Content.Archives.MaxEntries.ToString();
        Mode.SelectedIndex = (int)c.Resources.Mode; PauseFullscreen.IsChecked = c.Resources.PauseInFullscreen; EconomyBattery.IsChecked = c.Resources.EconomyOnBattery;
        PauseContentBattery.IsChecked = c.Resources.PauseContentOnBattery; IdleSec.Text = c.Resources.UserIdleSeconds.ToString();
        Windows.Text = string.Join(", ", c.Resources.ContentTimeWindows); HostMem.Text = c.Resources.ExtractorMemoryMB.ToString();
        Fuzzy.IsChecked = c.Search.FuzzyEnabled; MatchPath.IsChecked = c.Search.MatchInPath; SearchHidden.IsChecked = c.Search.IncludeHidden; SearchSystem.IsChecked = c.Search.IncludeSystem;
        AppsStart.IsChecked = c.Apps.StartMenu; AppsDesktop.IsChecked = c.Apps.Desktop; AppsStore.IsChecked = c.Apps.PackagedApps; ExeFolders.Text = Lines(c.Apps.ExecutableFolders);
        Learning.IsChecked = c.Privacy.Learning; History.IsChecked = c.Privacy.QueryHistory; Discovery.IsChecked = c.Discovery.Enabled; RedactPaths.IsChecked = c.Logging.RedactPaths;
        WatchEnabled.IsChecked = c.Watch.Enabled; ReconcileStartup.IsChecked = c.Watch.ReconcileAtStartup; ReconcileHours.Text = c.Watch.ReconcileIntervalHours.ToString();
        DataDir.Text = _engine.Storage.DataRoot;
        LanguageBox.Items.Add(L.T("Lang_Auto"));
        foreach (var (_, native) in L.Languages) LanguageBox.Items.Add(native);
        int li = L.Languages.ToList().FindIndex(l => l.Code.Equals(c.General.Language, StringComparison.OrdinalIgnoreCase));
        LanguageBox.SelectedIndex = li + 1;
        if (_roots.Count > 0) RootsList.SelectedIndex = 0; else RootPanel.IsEnabled = false;
    }

    private void StoreRoot()
    {
        if (_editing == null) return;
        _editing.Enabled = RootEnabled.IsChecked == true; _editing.IndexContent = RootContent.IsChecked == true; _editing.IncludeHidden = RootHidden.IsChecked == true;
        _editing.Watch = RootWatch.IsChecked == true; _editing.FollowLinks = RootLinks.IsChecked == true; _editing.MaxDepth = Int(RootDepth.Text, RootConfig.DefaultMaxDepth);
        _editing.Excludes = FromLines(RootExcludes.Text);
    }

    private void RootsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        StoreRoot();
        _editing = RootsList.SelectedItem as RootConfig;
        RootPanel.IsEnabled = _editing != null;
        if (_editing == null) return;
        RootEnabled.IsChecked = _editing.Enabled; RootContent.IsChecked = _editing.IndexContent; RootHidden.IsChecked = _editing.IncludeHidden;
        RootWatch.IsChecked = _editing.Watch; RootLinks.IsChecked = _editing.FollowLinks; RootDepth.Text = _editing.MaxDepth.ToString();
        RootExcludes.Text = Lines(_editing.Excludes);
        // Tell the user when the current limit leaves folders of this root unexplored.
        var status = _engine.Roots().FirstOrDefault(r => r.Path.Equals(_editing.Path, StringComparison.OrdinalIgnoreCase));
        RootDepthInfo.Visibility = status is { DepthLimitedDirs: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (status is { DepthLimitedDirs: > 0 })
            RootDepthInfo.Text = L.F("Set_RootDepthInfo", status.MaxDepth, status.DepthLimitedDirs, status.DepthHiddenEntries);
    }

    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Set_AddRootTitle"), Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FolderNames)
        {
            if (_engine.Storage.IsUnderDataRoot(f)) { MessageBox.Show(L.T("Set_DataDirRefused"), "Seekbeam"); continue; }
            if (_roots.Any(r => r.Path.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
            var rc = new RootConfig { Path = f };
            _roots.Add(rc);
            RootsList.SelectedItem = rc;
        }
    }

    private void RemoveRoot_Click(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is RootConfig r) { _editing = null; _roots.Remove(r); }
    }

    private void AddContentFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Set_AddContentFolderTitle"), Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FolderNames)
            if (!_contentFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) _contentFolders.Add(f);
    }

    private void RemoveContentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ContentFoldersList.SelectedItem is not string f) return;
        // Downloads is excluded for security reasons: explain why before letting the user index its content (SECURITY.md).
        if (KnownFolders.IsDownloads(f)
            && MessageBox.Show(this, L.T("Set_DownloadsRisk"), L.T("Set_DownloadsRiskTitle"),
                   MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _contentFolders.Remove(f);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AltHotkey.Hotkey.Length > 0 && AltHotkey.Hotkey.Equals(DeepHotkey.Hotkey, StringComparison.OrdinalIgnoreCase))
        {
            Tabs.SelectedIndex = 0;
            ShowHotkeyFeedback(L.F("Set_HotkeyDuplicate", AltHotkey.Hotkey), warning: true);
            return;
        }
        StoreRoot();
        var c = _cfg;
        c.General.Language = LanguageBox.SelectedIndex > 0 ? L.Languages[LanguageBox.SelectedIndex - 1].Code : "";
        c.General.StartWithWindows = StartWithWindows.IsChecked == true; c.General.ShowTrayIcon = ShowTray.IsChecked == true;
        c.Hotkey.DoubleCtrlEnabled = DoubleCtrl.IsChecked == true; c.Hotkey.CtrlSide = (CtrlSide)Math.Max(0, CtrlSide.SelectedIndex);
        c.Hotkey.DoubleTapMs = Int(TapMs.Text, 400); c.Hotkey.AlternativeHotkey = AltHotkey.Hotkey; c.Hotkey.DeepSearchHotkey = DeepHotkey.Hotkey;
        c.Launcher.Position = (LauncherPosition)Math.Max(0, Position.SelectedIndex); c.Launcher.MaxResults = Int(MaxResults.Text, 8);
        c.Launcher.Theme = (ThemeMode)Math.Max(0, ThemeBox.SelectedIndex); c.Launcher.Topmost = TopmostBox.IsChecked == true; c.Launcher.CloseOnFocusLoss = CloseOnFocusLoss.IsChecked == true;
        c.Roots = [.. _roots]; c.GlobalExcludes = FromLines(GlobalExcludes.Text);
        c.Content.Enabled = ContentEnabled.IsChecked == true; c.Content.StoreTextForSnippets = StoreText.IsChecked == true; c.Content.MaxFileSizeMB = Int(MaxFileMB.Text, 50);
        c.Content.MaxCharsPerDocument = Int(MaxChars.Text, 1_000_000); c.Content.TimeoutSeconds = Int(Timeout.Text, 30);
        c.Content.ExcludedExtensions = FromCsv(ExcludedExt.Text); c.Content.ExtraTextExtensions = FromCsv(ExtraExt.Text); c.Content.DisabledExtractors = FromCsv(DisabledExtractors.Text);
        c.Content.ExcludedFolders = [.. _contentFolders];
        c.Content.Archives.Enabled = ArchivesEnabled.IsChecked == true; c.Content.Archives.MaxDepth = Int(ArchiveDepth.Text, 2); c.Content.Archives.MaxEntries = Int(ArchiveEntries.Text, 10_000);
        c.Resources.Mode = (ResourceMode)Math.Max(0, Mode.SelectedIndex); c.Resources.PauseInFullscreen = PauseFullscreen.IsChecked == true;
        c.Resources.EconomyOnBattery = EconomyBattery.IsChecked == true; c.Resources.PauseContentOnBattery = PauseContentBattery.IsChecked == true;
        c.Resources.UserIdleSeconds = Int(IdleSec.Text, 120); c.Resources.ContentTimeWindows = FromCsv(Windows.Text); c.Resources.ExtractorMemoryMB = Int(HostMem.Text, 0);
        c.Search.FuzzyEnabled = Fuzzy.IsChecked == true; c.Search.MatchInPath = MatchPath.IsChecked == true; c.Search.IncludeHidden = SearchHidden.IsChecked == true; c.Search.IncludeSystem = SearchSystem.IsChecked == true;
        c.Apps.StartMenu = AppsStart.IsChecked == true; c.Apps.Desktop = AppsDesktop.IsChecked == true; c.Apps.PackagedApps = AppsStore.IsChecked == true; c.Apps.ExecutableFolders = FromLines(ExeFolders.Text);
        c.Privacy.Learning = Learning.IsChecked == true; c.Privacy.QueryHistory = History.IsChecked == true; c.Discovery.Enabled = Discovery.IsChecked == true; c.Logging.RedactPaths = RedactPaths.IsChecked == true;
        c.Watch.Enabled = WatchEnabled.IsChecked == true; c.Watch.ReconcileAtStartup = ReconcileStartup.IsChecked == true; c.Watch.ReconcileIntervalHours = Int(ReconcileHours.Text, 24);
        try
        {
            _engine.Config.Save(c);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Settings", "Save failed", ex);
            MessageBox.Show(L.F("Set_SaveFailed", ex.Message), "Seekbeam", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ hotkey capture

    private void WireHotkeyCapture()
    {
        foreach (var box in new[] { AltHotkey, DeepHotkey })
        {
            // While a box has the focus, Seekbeam's own global activations are released: pressing the current
            // combination is captured instead of opening the launcher, and the availability probe is exact.
            box.GotKeyboardFocus += (_, _) => { _hotkeys?.Suspend(); ShowHotkeyFeedback(L.T("Set_HotkeyCaptureHint"), warning: false); };
            box.LostKeyboardFocus += (_, _) => _hotkeys?.Resume();
            box.Captured += OnHotkeyCaptured;
            box.Cleared += _ => ShowHotkeyFeedback(L.T("Set_HotkeyCleared"), warning: false);
        }
        Closed += (_, _) => _hotkeys?.Resume();
    }

    private void OnHotkeyCaptured(Controls.HotkeyBox box, string text, uint mods, uint vk, Core.Input.HotkeyValidation v)
    {
        switch (v)
        {
            case Core.Input.HotkeyValidation.NoModifier: ShowHotkeyFeedback(L.T("Set_HotkeyNoModifier"), true); return;
            case Core.Input.HotkeyValidation.ShiftOnly: ShowHotkeyFeedback(L.F("Set_HotkeyShiftOnly", text), true); return;
            case Core.Input.HotkeyValidation.UnsupportedKey: ShowHotkeyFeedback(L.T("Set_HotkeyUnsupported"), true); return;
        }
        var other = box == AltHotkey ? DeepHotkey : AltHotkey;
        if (other.Hotkey.Equals(text, StringComparison.OrdinalIgnoreCase)) ShowHotkeyFeedback(L.F("Set_HotkeyDuplicate", text), true);
        else if (!HotkeyService.IsAvailable(mods, vk)) ShowHotkeyFeedback(L.F("Set_HotkeyTaken", text), true);
        else ShowHotkeyFeedback(L.F("Set_HotkeyOk", text), false);
    }

    private void ShowHotkeyFeedback(string text, bool warning)
    {
        HotkeyFeedback.Text = text;
        HotkeyFeedback.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, warning ? "WarnBrush" : "SubFgBrush");
    }

    private void ClearAltHotkey_Click(object sender, RoutedEventArgs e) { AltHotkey.Hotkey = ""; ShowHotkeyFeedback(L.T("Set_HotkeyCleared"), false); }
    private void ClearDeepHotkey_Click(object sender, RoutedEventArgs e) { DeepHotkey.Hotkey = ""; ShowHotkeyFeedback(L.T("Set_HotkeyCleared"), false); }

    // ------------------------------------------------------------------ about

    /// <summary>Selects the "About" tab (tray menu entry).</summary>
    public void ShowAbout() => Tabs.SelectedItem = AboutTab;

    private void LoadAbout()
    {
        AboutVersion.Text = L.F("About_Version", AppInfo.DisplayVersion);
        AboutCopyright.Text = AppInfo.Copyright;
        ProjectUrl.Text = AppInfo.ProjectUrl;
        try { AboutIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/seekbeam.ico")); }
        catch (Exception ex) { Log.Warn("Settings", "About icon: " + ex.Message); }
    }

    private void ProjectLink_Click(object sender, RoutedEventArgs e) => ShellActions.OpenLink(AppInfo.ProjectUrl);
    private void LicenseLink_Click(object sender, RoutedEventArgs e) => ShellActions.OpenLink(AppInfo.RepoFileUrl("LICENSE"));
    private void NoticesLink_Click(object sender, RoutedEventArgs e) => ShellActions.OpenLink(AppInfo.RepoFileUrl("THIRD-PARTY-NOTICES.md"));
    private void PolicyLink_Click(object sender, RoutedEventArgs e) =>
        ShellActions.OpenLink(AppInfo.DocUrl("docs/FILE_IMMUTABILITY_POLICY", L.Culture.TwoLetterISOLanguageName));

    private void CopyInfo_Click(object sender, RoutedEventArgs e)
    {
        // Diagnostic block for bug reports: no paths, no personal data.
        string info = $"Seekbeam {AppInfo.DisplayVersion}\n.NET {Environment.Version}\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                      $"({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})\nUI: {L.Culture.Name}";
        ShellActions.CopyText(info);
        MessageBox.Show(this, L.T("About_Copied"), "Seekbeam");
    }
    private void ClearHistory_Click(object sender, RoutedEventArgs e) { _engine.Usage.ClearHistory(); MessageBox.Show(L.T("Set_HistoryCleared"), "Seekbeam"); }
    private void ResetLearning_Click(object sender, RoutedEventArgs e) { _engine.Usage.ResetLearning(); MessageBox.Show(L.T("Set_LearningReset"), "Seekbeam"); }
    private void PurgeLogs_Click(object sender, RoutedEventArgs e) { Log.PurgeLogs(); MessageBox.Show(L.T("Set_LogsPurged"), "Seekbeam"); }
}
