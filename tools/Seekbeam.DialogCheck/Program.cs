using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using Seekbeam.App.Native;
using Seekbeam.App.Services;
using Forms = System.Windows.Forms;

namespace Seekbeam.DialogCheck;

/// <summary>
/// Drives REAL Open / Save As / folder-picker / old folder-browser dialogs and an Explorer window with the code the
/// launcher ships (NavigationTargets, ExplorerShell, DialogKeyRouter), and checks the result: recognised kind, folder
/// reached, file name kept or set, dialog never validated, nothing written. Writes a Markdown report.
/// Usage: Seekbeam.DialogCheck [report.md]. Needs an interactive desktop session.
/// </summary>
internal static class Program
{
    private sealed record Check(string Scenario, string Step, bool Ok, string Detail);
    private static readonly List<Check> Checks = [];
    private static string _root = "";

    [STAThread]
    private static int Main(string[] args)
    {
        _root = Path.Combine(Path.GetTempPath(), "seekbeam-dialogcheck", Guid.NewGuid().ToString("N"));
        string alpha = Path.Combine(_root, "Alpha Projets"), beta = Path.Combine(_root, "Beta", "Rapports");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);
        File.WriteAllText(Path.Combine(beta, "rapport.txt"), "report");
        File.WriteAllText(Path.Combine(alpha, "notes.md"), "notes");
        var before = Snapshot(_root);
        var sw = Stopwatch.StartNew();
        string? app = args.SkipWhile(a => a != "--app").Skip(1).FirstOrDefault();
        _screenshot = args.SkipWhile(a => a != "--screenshot").Skip(1).FirstOrDefault();
        args = args.Where((a, i) => a != "--screenshot" && (i == 0 || args[i - 1] != "--screenshot")).ToArray();
        args = args.Where((a, i) => a != "--app" && (i == 0 || args[i - 1] != "--app")).ToArray();
        try
        {
            OpenDialog(alpha, beta);
            SaveDialog(alpha, beta);
            FolderPicker(alpha, beta);
            LegacyFolderBrowser(alpha);
            Explorer(alpha, beta);
            if (app != null) EndToEnd(app, alpha, beta);
        }
        catch (Exception ex) { Add("Run", "unexpected error", false, ex.ToString()); }
        var after = Snapshot(_root);
        Add("Immutability", "test tree unchanged (names, sizes, SHA-256)", before.SequenceEqual(after),
            $"{before.Count} entries before, {after.Count} after");
        string report = Report(sw.Elapsed);
        Console.WriteLine(report);
        if (args.Length > 0) File.WriteAllText(args[0], report, new UTF8Encoding(false));
        try { Directory.Delete(_root, true); } catch { }
        return Checks.All(c => c.Ok) ? 0 : 1;
    }

    // ------------------------------------------------------------------ scenarios

    private static void OpenDialog(string alpha, string beta)
    {
        const string S = "Open dialog";
        RunDialog(S, () => new Forms.OpenFileDialog { Title = "SB-CHECK-OPEN", InitialDirectory = alpha, Filter = "All|*.*|Text|*.txt" }, top =>
        {
            var t = Expect(S, top, NavTargetKind.FileOpen);
            if (t == null) return;
            IntPtr edit = NavigationTargets.FileNameEdit(top);
            Add(S, "typing in the file-name box is never taken over", !NavigationTargets.IsFileListFocused(top), "focus: " + Win32.ClassName(Win32.FocusedControl(top)));
            RouterChecks(S, top, edit);
            FocusFileList(top);
            Add(S, "file list focused → recognised for direct typing", NavigationTargets.IsFileListFocused(top), "focus: " + Win32.ClassName(Win32.FocusedControl(top)));
            var outcome = NavigationTargets.NavigateDialog(t, beta, null);
            Add(S, "navigate to a folder", outcome == NavOutcome.Done && WaitAddress(top, beta), $"{outcome}; address: {Address(top)}");
            outcome = NavigationTargets.NavigateDialog(t, alpha, "notes.md");
            Add(S, "a file was chosen: its folder, and its name in the box (not validated)",
                outcome == NavOutcome.Done && WaitAddress(top, alpha) && Win32.GetText(edit) == "notes.md", $"{outcome}; box: '{Win32.GetText(edit)}'");
            outcome = NavigationTargets.NavigateDialog(t, Path.Combine(_root, "does-not-exist"), null);
            Add(S, "a folder that no longer exists is refused before touching the dialog", outcome == NavOutcome.FolderMissing, outcome.ToString());
        });
    }

    private static void SaveDialog(string alpha, string beta)
    {
        const string S = "Save As dialog";
        RunDialog(S, () => new Forms.SaveFileDialog { Title = "SB-CHECK-SAVE", InitialDirectory = alpha, FileName = "keepme.txt", Filter = "Text|*.txt" }, top =>
        {
            var t = Expect(S, top, NavTargetKind.FileSave);
            if (t == null) return;
            IntPtr edit = NavigationTargets.FileNameEdit(top);
            FocusFileList(top); // as after direct typing
            var outcome = NavigationTargets.NavigateDialog(t, beta, null);
            Add(S, "navigate (focus in the file list), the typed file name is kept", outcome == NavOutcome.Done && WaitAddress(top, beta) && Win32.GetText(edit) == "keepme.txt",
                $"{outcome}; address: {Address(top)}; box: '{Win32.GetText(edit)}'");
            outcome = NavigationTargets.NavigateDialog(t, alpha, "rapport.txt");
            Add(S, "a file was chosen: the typed name still wins (no overwrite risk)", outcome == NavOutcome.Done && WaitAddress(top, alpha) && Win32.GetText(edit) == "keepme.txt",
                $"{outcome}; box: '{Win32.GetText(edit)}'");
            Add(S, "dialog still open (never validated)", Win32.IsWindow(top) && Win32.IsWindowVisible(top), "");
        });
    }

    private static void FolderPicker(string alpha, string beta)
    {
        const string S = "Folder picker";
        RunDialog(S, () => new Forms.FolderBrowserDialog { Description = "SB-CHECK-FOLDER", UseDescriptionForTitle = true, InitialDirectory = alpha }, top =>
        {
            var t = Expect(S, top, NavTargetKind.FolderPicker);
            if (t == null) return;
            FocusFileList(top);
            var outcome = NavigationTargets.NavigateDialog(t, beta, null);
            Add(S, "navigate to a folder (focus in the file list)", outcome == NavOutcome.Done && WaitAddress(top, beta), $"{outcome}; address: {Address(top)}");
            outcome = NavigationTargets.NavigateDialog(t, alpha, "notes.md");
            Add(S, "a file was chosen: its folder (no name in the box)", outcome == NavOutcome.Done && WaitAddress(top, alpha), $"{outcome}; address: {Address(top)}");
            Add(S, "dialog still open (never validated)", Win32.IsWindow(top) && Win32.IsWindowVisible(top), "");
        });
    }

    private static void LegacyFolderBrowser(string alpha)
    {
        const string S = "Old folder browser (SHBrowseForFolder)";
        RunDialog(S, () => new Forms.FolderBrowserDialog { Description = "SB-CHECK-LEGACY", AutoUpgradeEnabled = false }, top =>
        {
            var t = Expect(S, top, NavTargetKind.LegacyFolderBrowser);
            if (t == null) return;
            var outcome = NavigationTargets.NavigateDialog(t, alpha, null);
            Add(S, "not driven (the launcher copies the path instead)", outcome == NavOutcome.Unsupported, outcome.ToString());
        }, findByOwnProcess: true);
    }

    private static void Explorer(string alpha, string beta)
    {
        const string S = "Explorer window";
        var d = Dispatcher.CurrentDispatcher;
        d.InvokeAsync(async () =>
        {
            try
            {
                var before = Win32.TopLevelWindows().ToHashSet();
                using (Process.Start(new ProcessStartInfo("explorer.exe", "\"" + alpha + "\"") { UseShellExecute = true })) { }
                ExplorerFolder? win = null;
                for (int i = 0; i < 100 && win == null; i++)
                {
                    await Task.Delay(100);
                    win = ExplorerShell.OpenFolders().FirstOrDefault(f => Same(f.Path, alpha));
                }
                Add(S, "open Explorer folders are listed (Quick Switch)", win != null, win?.Path ?? "not found");
                if (win == null) return;
                var t = NavigationTargets.Detect(win.Window, includeExplorer: true);
                Add(S, "recognised", t?.Kind == NavTargetKind.Explorer, t?.Kind.ToString() ?? "none");
                Add(S, "not taken over for direct typing", NavigationTargets.Detect(win.Window, includeExplorer: false) == null, "");
                bool ok = await ExplorerShell.NavigateAsync(win.Window, beta, "rapport.txt");
                bool there = false;
                for (int i = 0; i < 40 && !there; i++) { await Task.Delay(100); there = ExplorerShell.OpenFolders().Any(f => f.Window == win.Window && Same(f.Path, beta)); }
                Add(S, "navigate the same window (and select the file)", ok && there, $"navigated={ok}; now in {(there ? beta : "?")}");
                Add(S, "Quick Switch target = front-most Explorer folder", Same(ExplorerShell.FrontFolder() ?? "", beta), ExplorerShell.FrontFolder() ?? "none");
                if (!before.Contains(win.Window)) Win32.PostMessageW(win.Window, 0x0010 /* WM_CLOSE */, 0, 0);
            }
            catch (Exception ex) { Add(S, "error", false, ex.Message); }
            finally { d.InvokeShutdown(); }
        });
        Dispatcher.Run();
    }

    /// <summary>
    /// The real application, with an isolated profile (--data): double Ctrl over an Open dialog opens the launcher in
    /// "go to folder" mode glued to the dialog; typed path fragments find the folder; Enter sends the dialog there. Then an
    /// absolute path typed in the launcher (live completion) and a Save As dialog keeping its file name.
    /// </summary>
    private static void EndToEnd(string app, string alpha, string beta)
    {
        const string S = "End to end (Seekbeam.exe)";
        string data = Path.Combine(_root, "..", Path.GetFileName(_root) + "-data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "config.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            General = new { FirstRunCompleted = true, StartWithWindows = false, ShowTrayIcon = false },
            Roots = new[] { new { Path = _root } },
            FolderIndex = new { FixedDrives = false },
        }));
        using var p = Process.Start(new ProcessStartInfo(app, $"--data \"{data}\" --background") { UseShellExecute = false })!;
        try
        {
            Thread.Sleep(4000); // start + index the small test tree
            RunDialog(S, () => new Forms.OpenFileDialog { Title = "SB-E2E-OPEN", InitialDirectory = alpha }, top =>
            {
                Foreground(top);
                DoubleCtrl();
                IntPtr launcher = WaitLauncher((uint)p.Id);
                Add(S, "double Ctrl over the dialog opens the launcher", launcher != IntPtr.Zero, "");
                if (launcher == IntPtr.Zero) return;
                bool glued = NavigationTargets.Bounds(top) is { } d && NavigationTargets.Bounds(launcher) is { } l && Math.Abs(l.Top - d.Bottom) <= 2 && Math.Abs(l.Left - d.Left) <= 2;
                Add(S, "launcher glued under the dialog", glued, "");
                Win32.TypeText("beta rapp");
                Thread.Sleep(900);
                if (_screenshot != null) Capture(top, launcher, _screenshot);
                Key(Win32.VK_RETURN);
                Add(S, "path fragments + Enter: the dialog goes to the folder", WaitAddress(top, beta, 4000), Address(top));
                Foreground(top);
                DoubleCtrl();
                launcher = WaitLauncher((uint)p.Id);
                Win32.TypeText(Path.GetDirectoryName(alpha) + "\\Alpha Pro");
                Thread.Sleep(900);
                Key(Win32.VK_RETURN);
                Add(S, "absolute path completed from the disk + Enter", WaitAddress(top, alpha, 4000), Address(top));
            });
            RunDialog(S, () => new Forms.SaveFileDialog { Title = "SB-E2E-SAVE", InitialDirectory = alpha, FileName = "keepme.txt" }, top =>
            {
                Foreground(top);
                DoubleCtrl();
                if (WaitLauncher((uint)p.Id) == IntPtr.Zero) { Add(S, "Save As: launcher", false, "not shown"); return; }
                Win32.TypeText("rapports");
                Thread.Sleep(900);
                Key(Win32.VK_RETURN);
                IntPtr edit = NavigationTargets.FileNameEdit(top);
                Add(S, "Save As: goes to the folder, keeps the file name, not validated",
                    WaitAddress(top, beta, 4000) && Win32.GetText(edit) == "keepme.txt" && Win32.IsWindowVisible(top), $"{Address(top)}; box '{Win32.GetText(edit)}'");
            });
        }
        finally
        {
            using (var exit = Process.Start(new ProcessStartInfo(app, $"--data \"{data}\" --exit") { UseShellExecute = false })) exit?.WaitForExit(10_000);
            if (!p.WaitForExit(15_000)) p.Kill();
            try { Directory.Delete(data, true); } catch { }
        }
    }

    private static string? _screenshot;

    /// <summary>Screenshot of the dialog and the launcher glued to it (documentation, visual check).</summary>
    private static void Capture(IntPtr dialog, IntPtr launcher, string png)
    {
        if (NavigationTargets.Bounds(dialog) is not { } d || NavigationTargets.Bounds(launcher) is not { } l) return;
        int x = Math.Min(d.Left, l.Left), y = Math.Min(d.Top, l.Top), w = Math.Max(d.Right, l.Right) - x, h = Math.Max(d.Bottom, l.Bottom) - y;
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void Foreground(IntPtr top)
    {
        uint target = Win32.GetWindowThreadProcessId(top, out _), me = Win32.GetCurrentThreadId();
        Win32.AttachThreadInput(me, target, true);
        Win32.SetForegroundWindow(top);
        Win32.AttachThreadInput(me, target, false);
        Thread.Sleep(300);
    }

    private static void DoubleCtrl()
    {
        for (int i = 0; i < 2; i++)
        {
            keybd_event(0xA2 /* VK_LCONTROL */, 0, 0, 0);
            Thread.Sleep(30);
            keybd_event(0xA2, 0, 2, 0);
            Thread.Sleep(90);
        }
    }

    private static void Key(int vk)
    {
        keybd_event((byte)vk, 0, 0, 0);
        Thread.Sleep(30);
        keybd_event((byte)vk, 0, 2, 0);
    }

    private static IntPtr WaitLauncher(uint pid)
    {
        for (int i = 0; i < 40; i++)
        {
            Thread.Sleep(100);
            IntPtr fg = Win32.GetForegroundWindow();
            if (Win32.GetWindowThreadProcessId(fg, out uint p) != 0 && p == pid && Win32.IsWindowVisible(fg)) return fg;
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ helpers

    private static NavTarget? Expect(string scenario, IntPtr top, NavTargetKind kind)
    {
        var t = NavigationTargets.Detect(top, includeExplorer: true);
        Add(scenario, $"recognised as {kind}", t?.Kind == kind, t?.Kind.ToString() ?? "not recognised");
        return t?.Kind == kind ? t : null;
    }

    /// <summary>Shows a dialog on its own STA thread, runs <paramref name="test"/> against it, then cancels it.</summary>
    private static void RunDialog(string scenario, Func<Forms.CommonDialog> create, Action<IntPtr> test, bool findByOwnProcess = false)
    {
        Forms.DialogResult result = Forms.DialogResult.None;
        string? title = null;
        var thread = new Thread(() =>
        {
            Forms.Application.EnableVisualStyles();
            using var dlg = create();
            title = dlg switch { Forms.FileDialog f => f.Title, Forms.FolderBrowserDialog b => b.Description, _ => null };
            result = dlg.ShowDialog();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        IntPtr top = IntPtr.Zero;
        uint pid = (uint)Environment.ProcessId;
        for (int i = 0; i < 100 && top == IntPtr.Zero; i++)
        {
            Thread.Sleep(100);
            top = Win32.TopLevelWindows().FirstOrDefault(h => Win32.ClassName(h) == "#32770" && Win32.GetWindowThreadProcessId(h, out uint p) != 0 && p == pid
                && (findByOwnProcess || (Win32.GetText(h) ?? "") == title));
        }
        if (top == IntPtr.Zero) { Add(scenario, "dialog shown", false, "window not found"); thread.Join(5000); return; }
        Thread.Sleep(600); // let the view load
        try { test(top); }
        catch (Exception ex) { Add(scenario, "error", false, ex.Message); }
        Win32.PostMessageW(top, 0x0010 /* WM_CLOSE = Cancel */, 0, 0);
        thread.Join(10_000);
        Add(scenario, "closed by Cancel: the dialog was never validated", result == Forms.DialogResult.Cancel, result.ToString());
    }

    private static bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static string Address(IntPtr top) =>
        Win32.Children(top).Where(h => Win32.ClassName(h) == "ToolbarWindow32" && Win32.GetDlgCtrlID(h) == 0x3E9).Select(h => Win32.GetText(h) ?? "").FirstOrDefault() ?? "";

    /// <summary>The breadcrumb reads "Address: C:\…" (localised): the folder path must appear in it.</summary>
    private static bool WaitAddress(IntPtr top, string folder, int timeoutMs = 2000)
    {
        for (int i = 0; i < timeoutMs / 50; i++)
        {
            if (Address(top).EndsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);

    /// <summary>
    /// The keyboard router of the hook (direct typing, Ctrl+G) on the real dialog brought to the foreground: a letter in the
    /// file list is taken (and the next ones captured), a letter in the file-name box is not, Ctrl+G asks for Quick Switch.
    /// The keys are fed to the router directly (no real key press, so nothing reaches the dialog).
    /// </summary>
    private static void RouterChecks(string scenario, IntPtr top, IntPtr edit)
    {
        uint target = Win32.GetWindowThreadProcessId(top, out _), me = Win32.GetCurrentThreadId();
        Win32.AttachThreadInput(me, target, true);
        Win32.SetForegroundWindow(top);
        Win32.AttachThreadInput(me, target, false);
        Thread.Sleep(200);
        if (Win32.GetForegroundWindow() != top) { Add(scenario, "keyboard router (dialog in the foreground)", false, "could not bring the dialog to the foreground"); return; }
        var router = new DialogKeyRouter { DirectTyping = true, QuickSwitch = true };
        string? started = null; NavTarget? quick = null;
        router.TypingStarted += (_, ch) => started = ch;
        router.QuickSwitchRequested += t => quick = t;
        uint scanA = Win32.MapVirtualKeyW(0x41, 0), scanB = Win32.MapVirtualKeyW(0x42, 0);
        bool takenInBox = router.OnKeyDown(0x41, scanA);
        Add(scenario, "keyboard router: a letter in the file-name box is left to the dialog", !takenInBox && started == null, $"taken={takenInBox}");
        FocusFileList(top);
        bool taken = router.OnKeyDown(0x41, scanA);
        bool next = router.OnKeyDown(0x42, scanB);
        string captured = router.EndCapture();
        Add(scenario, "keyboard router: a letter in the file list opens Seekbeam, the next one is captured",
            taken && next && started?.ToLowerInvariant() == "a" && captured.ToLowerInvariant() == "b", $"first='{started}', captured='{captured}'");
        Win32.FocusDialogControl(top, edit);
        Thread.Sleep(100);
        // Ctrl+G: Ctrl really held (alone it does nothing in the dialog), G handed to the router only.
        keybd_event((byte)Win32.VK_CONTROL, 0, 0, 0);
        Thread.Sleep(50);
        bool g = router.OnKeyDown((uint)Win32.VK_G, Win32.MapVirtualKeyW((uint)Win32.VK_G, 0));
        keybd_event((byte)Win32.VK_CONTROL, 0, 2 /* KEYEVENTF_KEYUP */, 0);
        Add(scenario, "keyboard router: Ctrl+G asks for Quick Switch on this dialog", g && quick?.Hwnd == top, $"taken={g}");
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    private static void FocusFileList(IntPtr top)
    {
        IntPtr view = Win32.Children(top).FirstOrDefault(h => Win32.ClassName(h) == "SHELLDLL_DefView");
        IntPtr list = view == IntPtr.Zero ? IntPtr.Zero : Win32.Children(view).FirstOrDefault(h => Win32.ClassName(h) is "DirectUIHWND" or "SysListView32");
        if (list == IntPtr.Zero) return;
        uint target = Win32.GetWindowThreadProcessId(top, out _), me = Win32.GetCurrentThreadId();
        Win32.AttachThreadInput(me, target, true);
        SetFocus(list);
        Win32.AttachThreadInput(me, target, false);
        Thread.Sleep(150);
    }

    private static void Add(string scenario, string step, bool ok, string detail)
    {
        Checks.Add(new Check(scenario, step, ok, detail));
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {scenario}: {step}  {detail}");
    }

    private static List<string> Snapshot(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)
            .Select(p => File.Exists(p) ? $"{p}|{new FileInfo(p).Length}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}" : p + "|dir").ToList();

    private static string Report(TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!-- Generated by tools/Seekbeam.DialogCheck: do not edit by hand. -->");
        sb.AppendLine($"Run: {DateTime.Now:yyyy-MM-dd HH:mm} · {Environment.OSVersion.VersionString} · UI culture {System.Globalization.CultureInfo.CurrentUICulture.Name} · {elapsed.TotalSeconds:F1} s");
        sb.AppendLine();
        sb.AppendLine($"**{Checks.Count(c => c.Ok)}/{Checks.Count} checks passed.**");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Check | Result | Detail |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var c in Checks)
            sb.AppendLine($"| {c.Scenario} | {c.Step} | {(c.Ok ? "✅ pass" : "❌ FAIL")} | {c.Detail.Replace("|", "\\|").Replace(_root, "%TEMP%\\…")} |");
        return sb.ToString();
    }
}
