using System.Windows;
using System.Windows.Controls;
using Seekbeam.App.Localization;
using Seekbeam.Core.Engine;
using Seekbeam.Core.Search;

namespace Seekbeam.App.Services;

/// <summary>Context actions on a result (CDC §19). Destructive or modifying actions are deliberately absent.</summary>
public static class ResultActions
{
    public static ContextMenu BuildMenu(SearchResult r, SeekbeamEngine engine, Action? after = null)
    {
        var menu = new ContextMenu();
        void Add(string header, string gesture, Action action, bool enabled = true, string? tip = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled, ToolTip = tip };
            mi.Click += (_, _) => { action(); after?.Invoke(); };
            menu.Items.Add(mi);
        }
        bool isFile = r.Kind == ResultKind.File;
        bool isApp = r.Kind == ResultKind.App;
        Add(isApp ? L.T("Action_Launch") : L.T("Action_Open"), L.T("Key_Enter"), () => Open(r, engine), tip: L.T("Action_OpenTip"));
        Add(L.T("Action_OpenFolder"), L.T("Key_CtrlEnter"), () => ShellActions.OpenContainingFolder(r), !(isApp && r.App?.AppUserModelId != null));
        if (isApp || r.FullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || r.FullPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            || r.FullPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || r.FullPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            Add(L.T("Action_RunAsAdmin"), L.T("Key_ShiftEnter"), () => { ShellActions.RunAsAdministrator(r); engine.RecordOpen(r); });
        if (isFile) Add(L.T("Action_OpenWith"), "", () => ShellActions.OpenWith(r));
        menu.Items.Add(new Separator());
        Add(L.T("Action_CopyPath"), L.T("Key_CtrlC"), () => ShellActions.CopyText(r.FullPath));
        Add(L.T("Action_CopyName"), L.T("Key_CtrlShiftC"), () => ShellActions.CopyText(r.Name));
        if (isFile) Add(L.T("Action_CopyContent"), "", () => { var t = r.EntryId > 0 ? engine.ContentDb.GetText(r.EntryId) : null; if (t != null) ShellActions.CopyText(t); },
            r.EntryId > 0 && engine.ContentDb.GetInfo(r.EntryId) != null);
        Add(L.T("Action_Properties"), L.T("Key_AltEnter"), () => ShellActions.Properties(r), !(isApp && r.App?.AppUserModelId != null));
        menu.Items.Add(new Separator());
        string key = isApp ? r.App!.Key : r.FullPath;
        bool fav = engine.Usage.IsFavorite(key);
        Add(fav ? L.T("Action_FavRemove") : L.T("Action_FavAdd"), L.T("Key_CtrlF"), () => engine.Usage.SetFavorite(key, r.EntryId > 0 ? r.EntryId : null, !fav));
        if (!isApp)
        {
            Add(L.T("Action_Exclude"), "", () =>
            {
                if (MessageBox.Show(L.F("Action_ExcludeConfirm", r.FullPath), "Seekbeam", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    engine.ExcludePath(r.FullPath);
            });
            Add(L.T("Action_Reindex"), "", () => engine.ReindexPath(r.FullPath, content: isFile));
        }
        return menu;
    }

    public static void Open(SearchResult r, SeekbeamEngine engine)
    {
        if (ShellActions.Open(r)) engine.RecordOpen(r);
    }
}
