using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Seekbeam.Core.Input;

namespace Seekbeam.App.Controls;

/// <summary>
/// Captures a global hotkey by pressing it (no typing): shows the modifiers while they are held, then the canonical
/// combination ("Ctrl+Alt+F"). Backspace or Delete alone clears it; Tab and Escape alone keep their usual meaning.
/// The text is always in the <see cref="HotkeyParser"/> syntax stored in the configuration.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    private string _hotkey = "";

    /// <summary>Raised when a combination is pressed: its canonical text, modifiers, virtual key and validation result.</summary>
    public event Action<HotkeyBox, string, uint, uint, HotkeyValidation>? Captured;
    /// <summary>Raised when the user clears the hotkey.</summary>
    public event Action<HotkeyBox>? Cleared;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ContextMenu = null;
        Cursor = Cursors.Arrow;
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    /// <summary>The stored hotkey ("" = none).</summary>
    public string Hotkey
    {
        get => _hotkey;
        set { _hotkey = value ?? ""; Text = _hotkey; }
    }

    private static uint Modifiers()
    {
        var m = Keyboard.Modifiers;
        uint r = 0;
        if (m.HasFlag(ModifierKeys.Control)) r |= HotkeyParser.MOD_CONTROL;
        if (m.HasFlag(ModifierKeys.Alt)) r |= HotkeyParser.MOD_ALT;
        if (m.HasFlag(ModifierKeys.Shift)) r |= HotkeyParser.MOD_SHIFT;
        if (m.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) r |= HotkeyParser.MOD_WIN;
        return r;
    }

    private static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        Key key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        uint mods = Modifiers();
        if (mods == 0 && key is Key.Tab or Key.Escape) { base.OnPreviewKeyDown(e); return; } // navigation, dialog cancel
        e.Handled = true;
        if (mods == 0 && key is Key.Back or Key.Delete)
        {
            Hotkey = "";
            Cleared?.Invoke(this);
            return;
        }
        if (IsModifier(key))
        {
            // Live preview of the held modifiers ("Ctrl+Alt+...").
            Text = HotkeyParser.Format(mods, 0).Replace("?", "…");
            return;
        }
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        var validation = HotkeyParser.Validate(mods, vk);
        string text = HotkeyParser.Format(mods, vk);
        if (validation == HotkeyValidation.Ok) Hotkey = text; else Text = _hotkey;
        Captured?.Invoke(this, text, mods, vk, validation);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // Released modifiers without a key: restore the stored value.
        if (Modifiers() == 0 && Text != _hotkey) Text = _hotkey;
        base.OnPreviewKeyUp(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        Text = _hotkey;
        base.OnLostKeyboardFocus(e);
    }
}
