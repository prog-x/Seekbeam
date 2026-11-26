// The dialog check tool (tools/Seekbeam.DialogCheck) drives real Open/Save/folder dialogs with the very code the launcher
// uses (NavigationTargets, ExplorerShell), so that docs/DIALOG_NAVIGATION_CHECK.md reports on the shipped implementation.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Seekbeam.DialogCheck")]
