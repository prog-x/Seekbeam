using Seekbeam.App.Native;
using Seekbeam.Core.Diagnostics;
using Seekbeam.Core.SourceAccess;

namespace Seekbeam.App.Services;

/// <summary>Window the "go to folder" mode drives (TD-20).</summary>
internal enum NavTargetKind
{
    /// <summary>Open dialog (common item dialog or Explorer-style common dialog).</summary>
    FileOpen,
    /// <summary>Save As dialog: navigating keeps the name typed, nothing is ever validated.</summary>
    FileSave,
    /// <summary>Folder picker (common item dialog with FOS_PICKFOLDERS).</summary>
    FolderPicker,
    /// <summary>Old SHBrowseForFolder tree: cannot be driven from outside without writing into its process; the path is copied.</summary>
    LegacyFolderBrowser,
    /// <summary>Explorer window: navigated through the shell (IShellWindows).</summary>
    Explorer,
}

internal sealed record NavTarget(IntPtr Hwnd, NavTargetKind Kind)
{
    public bool IsDialog => Kind != NavTargetKind.Explorer;
    public bool CanNavigate => Kind != NavTargetKind.LegacyFolderBrowser;
}

internal enum NavOutcome { Done, FolderMissing, TargetGone, Blocked, Unsupported, Timeout }

/// <summary>
/// Recognises Open/Save/folder dialogs and Explorer windows, and sends a dialog to a folder.
/// <para>
/// Recognition uses only window classes and control ids (no message is sent, so it is safe on the keyboard-hook thread):
/// a common item dialog is a "#32770" with a "SHELLDLL_DefView" view; its file-name box is the ComboBoxEx32 0x47C edit
/// (Open), an edit inside a "FloatNotifySink" (Save As), or the plain Edit 0x480 without file types (folder picker).
/// </para>
/// <para>
/// Navigation, as validated on the real dialogs (docs/DIALOG_NAVIGATION_CHECK.md): the folder path, ending with "\", is put in
/// the file-name box and Enter is posted to it. With an existing folder ending with a separator the dialog always
/// navigates (Open, Save As and folder picker alike) and never validates; a folder that vanished meanwhile only shows
/// the dialog's own error message. The box then gets back what the user had typed (Save As) or the chosen file name
/// (Open). Enter is POSTED, so a modal message box of the target cannot block Seekbeam.
/// </para>
/// </summary>
internal static class NavigationTargets
{
    private const int IdFileNameCombo = 0x47C, IdFileNameEdit = 0x480, IdFileTypes = 0x470, IdOk = 1;

    public static NavTarget? Detect(IntPtr hwnd, bool includeExplorer)
    {
        if (hwnd == IntPtr.Zero) return null;
        IntPtr top = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (top == IntPtr.Zero) top = hwnd;
        string cls = Win32.ClassName(top);
        if (cls is "CabinetWClass" or "ExploreWClass") return includeExplorer ? new NavTarget(top, NavTargetKind.Explorer) : null;
        if (cls != "#32770") return null;
        var s = Inspect(top);
        if (s.ShellView && s.FileNameEdit != IntPtr.Zero)
        {
            if (s.EditInFloatSink) return new NavTarget(top, NavTargetKind.FileSave);
            if (s.EditIsPlain480 && !s.HasFileTypes && !s.HasCombo47C) return new NavTarget(top, NavTargetKind.FolderPicker);
            return new NavTarget(top, NavTargetKind.FileOpen); // Save is refined by IsSaveDialog when it matters
        }
        if (s.LegacyTree) return new NavTarget(top, NavTargetKind.LegacyFolderBrowser);
        return null;
    }

    private sealed class Structure
    {
        public bool ShellView, LegacyTree, HasFileTypes, HasCombo47C, EditInFloatSink, EditIsPlain480;
        public IntPtr FileNameEdit;
    }

    private static Structure Inspect(IntPtr top)
    {
        var s = new Structure();
        IntPtr fallback = IntPtr.Zero;
        foreach (var h in Win32.Children(top))
        {
            string c = Win32.ClassName(h);
            int id = Win32.GetDlgCtrlID(h);
            switch (c)
            {
                case "SHELLDLL_DefView": s.ShellView = true; break;
                case "SHBrowseForFolder ShellNameSpace Control": s.LegacyTree = true; break;
                case "ComboBox" when id == IdFileTypes: s.HasFileTypes = true; break;
                case "ComboBoxEx32" when id == IdFileNameCombo: s.HasCombo47C = true; break;
                case "Edit":
                    if (s.FileNameEdit != IntPtr.Zero || !Win32.IsWindowVisible(h)) break;
                    var (inCombo, inFloat, excluded) = Ancestry(h, top);
                    if (excluded) break;
                    if (id == IdFileNameEdit && Win32.GetAncestor(h, Win32.GA_PARENT) == top) { s.FileNameEdit = h; s.EditIsPlain480 = true; }
                    else if (inCombo && inFloat) { s.FileNameEdit = h; s.EditInFloatSink = true; }
                    else if (inCombo && fallback == IntPtr.Zero) fallback = h;
                    break;
            }
        }
        if (s.FileNameEdit == IntPtr.Zero) s.FileNameEdit = fallback;
        return s;
    }

    /// <summary>Whether an edit sits in a combo box / a FloatNotifySink, or in a part that is not the file-name box.</summary>
    private static (bool InCombo, bool InFloat, bool Excluded) Ancestry(IntPtr h, IntPtr top)
    {
        bool combo = false, flt = false;
        for (IntPtr p = Win32.GetAncestor(h, Win32.GA_PARENT); p != IntPtr.Zero && p != top; p = Win32.GetAncestor(p, Win32.GA_PARENT))
        {
            string c = Win32.ClassName(p);
            if (c is "ComboBox" or "ComboBoxEx32") combo = true;
            else if (c == "FloatNotifySink") flt = true;
            else if (c is "Address Band Root" or "Search Box" or "UniversalSearchBand" or "ReBarWindow32" or "SHELLDLL_DefView" or "NamespaceTreeControl")
                return (combo, flt, true);
        }
        return (combo, flt, false);
    }

    public static IntPtr FileNameEdit(IntPtr top) => Inspect(top).FileNameEdit;

    /// <summary>True when the keyboard focus of the dialog is in its file list (where direct typing is taken over).</summary>
    public static bool IsFileListFocused(IntPtr top)
    {
        IntPtr f = Win32.FocusedControl(top);
        if (f == IntPtr.Zero || Win32.GetAncestor(f, Win32.GA_ROOT) != top) return false;
        string cls = Win32.ClassName(f);
        if (cls is not ("DirectUIHWND" or "SysListView32")) return false;
        for (IntPtr p = Win32.GetAncestor(f, Win32.GA_PARENT); p != IntPtr.Zero && p != top; p = Win32.GetAncestor(p, Win32.GA_PARENT))
            if (Win32.ClassName(p) == "SHELLDLL_DefView") return true;
        return false;
    }

    private static readonly string[] SaveWords =
        ["save", "enregistr", "speicher", "salva", "guardar", "opslaan", "salvar", "zapisz", "сохран", "uložit", "spara", "gem", "tallenna", "lagre", "kaydet"];

    /// <summary>
    /// Save dialog of the Explorer-style kind (file-name combo 0x47C in both Open and Save): told apart by the caption of the
    /// OK button or of the window. Only used to decide whether a chosen file's name goes in the box.
    /// </summary>
    public static bool IsSaveDialog(NavTarget t)
    {
        if (t.Kind == NavTargetKind.FileSave) return true;
        if (t.Kind != NavTargetKind.FileOpen) return false;
        string text = ((Win32.GetText(t.Hwnd) ?? "") + " " + (OkButton(t.Hwnd) is var ok && ok != IntPtr.Zero ? Win32.GetText(ok) ?? "" : "")).Replace("&", "").ToLowerInvariant();
        return SaveWords.Any(text.Contains);
    }

    private static IntPtr OkButton(IntPtr top) =>
        Win32.Children(top).FirstOrDefault(h => Win32.GetDlgCtrlID(h) == IdOk && Win32.ClassName(h) == "Button");

    /// <summary>
    /// Sends a dialog to <paramref name="folder"/>. With <paramref name="fileName"/> (a file was chosen in an Open dialog) its
    /// name is then put in the file-name box, ready for the user to validate. Never validates anything itself.
    /// </summary>
    public static NavOutcome NavigateDialog(NavTarget t, string folder, string? fileName)
    {
        if (!Win32.IsWindow(t.Hwnd)) return NavOutcome.TargetGone;
        if (!t.CanNavigate) return NavOutcome.Unsupported;
        if (!SourceFileAccess.DirectoryExists(folder)) return NavOutcome.FolderMissing;
        IntPtr edit = FileNameEdit(t.Hwnd);
        if (edit == IntPtr.Zero) return NavOutcome.Unsupported;
        bool save = IsSaveDialog(t);
        string old = Win32.GetText(edit) ?? "";
        // The trailing separator makes the text a folder for the dialog: never a file name it could save or open.
        string path = folder.EndsWith('\\') ? folder : folder + "\\";
        if (!Win32.SetText(edit, path) || Win32.GetText(edit) != path)
        {
            // Refused: most often a dialog of an elevated application (UIPI blocks messages from a normal process).
            Win32.SetText(edit, old);
            return NavOutcome.Blocked;
        }
        // Enter only means "go" to the box when it has the focus (direct typing leaves the focus in the file list).
        if (Win32.FocusedControl(t.Hwnd) != edit) { Win32.FocusDialogControl(t.Hwnd, edit); Thread.Sleep(30); }
        Win32.PostEnter(edit);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? now = path;
        while (sw.ElapsedMilliseconds < 2500)
        {
            Thread.Sleep(40);
            now = Win32.GetText(edit);
            if (now != path) break;
        }
        if (now == path)
        {
            Log.Warn("Navigate", "The dialog did not navigate in time");
            Win32.SetText(edit, old);
            return NavOutcome.Timeout;
        }
        if (t.Kind == NavTargetKind.FolderPicker) return NavOutcome.Done; // the box now names the folder: leave it
        string? want = save ? (old.Length > 0 ? old : null) : fileName;
        if (want != null && Win32.GetText(edit) != want)
        {
            Thread.Sleep(60); // let the dialog finish its own update of the box first
            Win32.SetText(edit, want);
        }
        if (want != null) Win32.SelectAllText(edit);
        return NavOutcome.Done;
    }

    /// <summary>Screen rectangle of a window (physical pixels).</summary>
    public static Win32.RECT? Bounds(IntPtr hwnd) => Win32.GetWindowRect(hwnd, out var r) ? r : null;
}
