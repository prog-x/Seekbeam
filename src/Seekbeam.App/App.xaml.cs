using System.Windows;
using System.Windows.Controls;
using Seekbeam.App.Localization;
using Seekbeam.App.Services;
using Seekbeam.App.Windows;
using Seekbeam.Core.Content;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.Engine;
using Seekbeam.Core.Resources;
using Seekbeam.Core.Storage;
using Seekbeam.Extraction;

namespace Seekbeam.App;

/// <summary>
/// Entry point: single instance, background start, engine, hotkeys, tray, windows.
/// Command line: --background (start hidden), --show, --deep "query", --settings, --dashboard, --purge-data.
/// </summary>
public partial class App : Application
{
    private static string MutexName = "Local\\Seekbeam.SingleInstance";
    private static string ShowEventName = "Local\\Seekbeam.Show";
    private static string ExitEventName = "Local\\Seekbeam.Exit";
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private SeekbeamEngine? _engine;
    private HotkeyService? _hotkeys;
    private TrayIcon? _tray;
    private IconCache? _icons;
    private LauncherWindow? _launcher;
    private DeepSearchWindow? _deep;
    private SettingsWindow? _settings;
    private DashboardWindow? _dashboard;
    public static bool IsShuttingDown { get; private set; }
    public static SeekbeamEngine Engine => ((App)Current)._engine!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();
        // --data <dir>: isolated profile (tests, audits, multiple profiles). Instance objects are scoped to the data dir.
        string? dataArg = ArgValue(e.Args, "--data");
        string dataRoot = dataArg != null ? Path.GetFullPath(dataArg) : SeekbeamStorage.ResolveDefaultDataRoot();
        // Stable across processes (string.GetHashCode is randomised per process).
        string scope = dataArg == null ? "" : "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(dataRoot.ToUpperInvariant())))[..16];
        MutexName += scope; ShowEventName += scope; ExitEventName += scope;
        if (args.Contains("--exit"))
        {
            try { EventWaitHandle.OpenExisting(ExitEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Already running: ask the existing instance to show its launcher.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        var storage = new SeekbeamStorage(dataRoot);
        bool registerStartup = dataArg == null && !storage.IsPortable;
        Log.Initialize(storage, LogLevel.Info);
        Log.Info("App", $"Seekbeam {AppInfo.DisplayVersion} starting (data: {Log.P(storage.DataRoot)})");
        DispatcherUnhandledException += (_, ex) => { Log.Error("App", "Unhandled UI exception", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("App", "Fatal", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Warn("App", "Unobserved task: " + ex.Exception.InnerException?.Message); ex.SetObserved(); };

        if (args.Contains("--purge-data"))
        {
            L.Apply(new Core.Config.ConfigStore(storage).Current.General.Language);
            if (MessageBox.Show(L.T("Purge_Confirm"), "Seekbeam",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            {
                storage.DeleteAllOwnedData(includeConfig: true);
                Core.Storage.SandboxProvisioning.RemoveProfile();
            }
            Shutdown();
            return;
        }

        string hostPath = Path.Combine(AppContext.BaseDirectory, "Seekbeam.ExtractorHost.exe");
        var registry = ExtractorRegistry.CreateDefault();
        _engine = new SeekbeamEngine(storage, new EngineOptions
        {
            SupportedExtensions = () => registry.SupportedExtensions,
            ExtractionFactory = () => new ExtractorClient(hostPath, _engine!.Config.Current.Content.Sandbox),
        });
        // Extractor sandbox (TD-17): AppContainer profile + read ACL on our own program directory, never inside the perimeter.
        _engine.ProvisionExtractorSandbox(AppContext.BaseDirectory);
        var cfg = _engine.Config.Current;
        L.Apply(cfg.General.Language);
        _language = cfg.General.Language;
        Theme.Apply(cfg.Launcher.Theme);
        _engine.Start();
        if (registerStartup) StartupRegistration.Apply(cfg.General.StartWithWindows);

        _icons = new IconCache();
        CreateLauncher();

        _hotkeys = new HotkeyService();
        _hotkeys.LauncherRequested += () => Dispatcher.BeginInvoke(() => _launcher!.Toggle());
        _hotkeys.DeepSearchRequested += () => Dispatcher.BeginInvoke(() => ShowDeep(null));
        _hotkeys.RegistrationFailed += msg => _tray?.ShowBalloon("Seekbeam", msg);
        _hotkeys.Configure(cfg.Hotkey);
        _engine.Config.Changed += c => Dispatcher.BeginInvoke(() =>
        {
            _hotkeys.Configure(c.Hotkey);
            Theme.Apply(c.Launcher.Theme);
            if (c.General.Language != _language) ApplyLanguage(c.General.Language);
            if (registerStartup) StartupRegistration.Apply(c.General.StartWithWindows);
        });

        if (cfg.General.ShowTrayIcon) CreateTray();
        _engine.Indexer.QueueDrained += () => Task.Run(NotifyDepthLimits);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var t = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (IsShuttingDown) return;
                Dispatcher.BeginInvoke(() => _launcher.ShowLauncher());
            }
        }) { IsBackground = true, Name = "Seekbeam.ShowEvent" };
        t.Start();
        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        new Thread(() => { exitEvent.WaitOne(); Dispatcher.BeginInvoke(Quit); }) { IsBackground = true, Name = "Seekbeam.ExitEvent" }.Start();

        if (args.Contains("--settings")) ShowSettings();
        else if (args.Contains("--dashboard")) ShowDashboard();
        else if (args.Contains("--deep")) ShowDeep(e.Args.SkipWhile(a => !a.Equals("--deep", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault());
        else if (!args.Contains("--background")) _launcher.ShowLauncher();
        if (!cfg.General.FirstRunCompleted || args.Contains("--background") == false && _engine.NameIndex.LiveCount <= 1)
            _tray?.ShowBalloon("Seekbeam", L.T("Tray_Welcome"));
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private string _language = "";

    private void CreateLauncher()
    {
        _launcher = new LauncherWindow(_engine!, _icons!);
        _launcher.DeepSearchRequested += q => ShowDeep(q);
        _launcher.SettingsRequested += ShowSettings;
        // Pre-create the window (hidden) so the first activation is instantaneous.
        new System.Windows.Interop.WindowInteropHelper(_launcher).EnsureHandle();
    }

    /// <summary>
    /// Switches the UI language: the launcher and the tray menu are rebuilt now; other windows pick it up when reopened
    /// (the settings window that triggered the change closes on save).
    /// </summary>
    private void ApplyLanguage(string language)
    {
        _language = language;
        L.Apply(language);
        _launcher?.Destroy();
        CreateLauncher();
        if (_tray != null) { _tray.Dispose(); _tray = null; CreateTray(); }
        foreach (Window w in new Window[] { _deep!, _dashboard! }) if (w is { IsLoaded: true }) w.Close();
        Log.Info("App", "UI language: " + L.Culture.Name);
    }

    /// <summary>Tells the user once when a root's MaxDepth leaves folders unexplored (click opens the dashboard).</summary>
    private void NotifyDepthLimits()
    {
        try
        {
            var notices = _engine!.TakeNewDepthLimitNotices();
            if (notices.Count == 0) return;
            string text = notices.Count == 1
                ? L.F("Depth_NoticeOne", notices[0].Path, notices[0].DepthLimitedDirs, notices[0].MaxDepth)
                : L.F("Depth_NoticeMany", notices.Count, notices.Sum(n => n.DepthLimitedDirs));
            Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(L.T("Depth_NoticeTitle"), text + " " + L.T("Depth_NoticeClick"), ShowDashboard));
        }
        catch (Exception ex) { Core.Diagnostics.Log.Warn("App", "Depth notice: " + ex.Message); }
    }

    private void CreateTray()
    {
        var menu = new ContextMenu();
        void Item(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item(L.T("Tray_Search"), () => _launcher!.ShowLauncher());
        Item(L.T("Tray_Deep"), () => ShowDeep(null));
        Item(L.T("Tray_Dashboard"), ShowDashboard);
        menu.Items.Add(new Separator());
        var pause = new MenuItem { Header = L.T("Tray_Pause"), IsCheckable = true, IsChecked = _engine!.Resources?.IsPaused(WorkClass.Names) ?? false };
        pause.Click += (_, _) => { if (pause.IsChecked) _engine!.Resources?.Pause(); else _engine!.Resources?.Resume(); };
        menu.Items.Add(pause);
        Item(L.T("Tray_Settings"), ShowSettings);
        Item(L.F("Tray_About", AppInfo.Version), () => ShowSettings(about: true));
        menu.Items.Add(new Separator());
        Item(L.T("Tray_Quit"), Quit);
        _tray = new TrayIcon("Seekbeam " + AppInfo.Version, menu);
        _tray.Clicked += () => Dispatcher.BeginInvoke(() => _launcher!.ShowLauncher());
    }

    public void ShowDeep(string? query)
    {
        if (_deep == null || !_deep.IsLoaded) { _deep = new DeepSearchWindow(_engine!, _icons!); }
        _deep.Show();
        _deep.Activate();
        if (query != null) _deep.SetQuery(query);
    }

    public void ShowSettings() => ShowSettings(about: false);

    public void ShowSettings(bool about)
    {
        if (_settings == null || !_settings.IsLoaded) _settings = new SettingsWindow(_engine!, _hotkeys);
        if (about) _settings.ShowAbout();
        _settings.Show();
        _settings.Activate();
    }

    public void ShowDashboard()
    {
        if (_dashboard == null || !_dashboard.IsLoaded) _dashboard = new DashboardWindow(_engine!);
        _dashboard.Show();
        _dashboard.Activate();
    }

    public void Quit()
    {
        IsShuttingDown = true;
        _showEvent?.Set();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsShuttingDown = true;
        try
        {
            _hotkeys?.Dispose();
            _tray?.Dispose();
            _icons?.Dispose();
            _engine?.Dispose();
        }
        catch (Exception ex) { Log.Error("App", "Shutdown error", ex); }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
