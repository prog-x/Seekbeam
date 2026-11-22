using Seekbeam.Core.Config;

namespace Seekbeam.Core.Input;

/// <summary>
/// Pure state machine detecting a "double Ctrl" tap from low-level keyboard events: two short presses of Ctrl alone,
/// the second starting within <see cref="DoubleTapMs"/> of the first release, with no other key in between.
/// Holding Ctrl, auto-repeat, Ctrl+key shortcuts and slow taps never trigger.
/// </summary>
public sealed class DoubleTapDetector
{
    public const int VK_CONTROL = 0x11, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    public int DoubleTapMs { get; set; } = 400;
    public int MaxTapMs { get; set; } = 350;
    public CtrlSide Side { get; set; } = CtrlSide.Both;

    private int _stage; // 0 idle, 1 first down, 2 first up, 3 second down
    private long _t;
    private bool _ctrlHeld;

    public void Reset() { _stage = 0; _ctrlHeld = false; }

    private bool IsCtrl(int vk) => Side switch
    {
        CtrlSide.Left => vk == VK_LCONTROL,
        CtrlSide.Right => vk == VK_RCONTROL,
        _ => vk is VK_LCONTROL or VK_RCONTROL or VK_CONTROL,
    };

    private static bool IsAnyCtrl(int vk) => vk is VK_LCONTROL or VK_RCONTROL or VK_CONTROL;

    /// <summary>Feeds one key event; returns true when a double tap completes.</summary>
    public bool OnKey(int vk, bool down, long timeMs)
    {
        if (!IsCtrl(vk))
        {
            // Any other key (or the non-configured Ctrl) cancels the sequence.
            if (down || !IsAnyCtrl(vk)) _stage = 0;
            return false;
        }
        if (down)
        {
            if (_ctrlHeld) return false; // auto-repeat while held
            _ctrlHeld = true;
            if (_stage == 2 && timeMs - _t <= DoubleTapMs) { _stage = 3; _t = timeMs; }
            else { _stage = 1; _t = timeMs; }
            return false;
        }
        _ctrlHeld = false;
        if (_stage == 1)
        {
            if (timeMs - _t <= MaxTapMs) { _stage = 2; _t = timeMs; } else _stage = 0;
            return false;
        }
        if (_stage == 3)
        {
            bool ok = timeMs - _t <= MaxTapMs;
            _stage = 0;
            return ok;
        }
        _stage = 0;
        return false;
    }
}

/// <summary>Launcher placement (pure geometry, device-independent units).</summary>
public static class WindowPlacement
{
    public readonly record struct RectD(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    public static (double Left, double Top) Compute(RectD workArea, double cursorX, double cursorY, double width, double height, LauncherPosition mode)
    {
        double left, top;
        if (mode == LauncherPosition.NearCursor)
        {
            left = cursorX - width / 2;
            top = cursorY + 18;
            if (top + height > workArea.Bottom) top = cursorY - height - 18;
        }
        else
        {
            left = workArea.Left + (workArea.Width - width) / 2;
            top = workArea.Top + workArea.Height * 0.22;
        }
        left = Math.Clamp(left, workArea.Left + 8, Math.Max(workArea.Left + 8, workArea.Right - width - 8));
        top = Math.Clamp(top, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - height - 8));
        return (left, top);
    }
}

/// <summary>Parses hotkey strings like "Alt+Space", "Ctrl+Shift+F", "Win+K" into Win32 modifier flags + virtual key.</summary>
public static class HotkeyParser
{
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    public static bool TryParse(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift" or "maj": modifiers |= MOD_SHIFT; break;
                case "win" or "windows": modifiers |= MOD_WIN; break;
                default:
                    if (vk != 0) return false;
                    vk = KeyCode(raw);
                    if (vk == 0) return false;
                    break;
            }
        }
        return vk != 0 && modifiers != 0;
    }

    private static uint KeyCode(string k)
    {
        k = k.ToUpperInvariant();
        if (k.Length == 1 && k[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') return k[0];
        if (k.StartsWith('F') && int.TryParse(k[1..], out int f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return k switch
        {
            "SPACE" or "ESPACE" => 0x20, "ENTER" or "RETURN" => 0x0D, "TAB" => 0x09, "ESC" or "ESCAPE" => 0x1B,
            "INSERT" or "INS" => 0x2D, "DELETE" or "DEL" => 0x2E, "HOME" => 0x24, "END" => 0x23, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            "`" or "OEM3" => 0xC0, ";" => 0xBA, "," => 0xBC, "." => 0xBE, "/" => 0xBF, "\\" => 0xDC,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28, "PAUSE" => 0x13,
            _ when k.StartsWith("NUM") && k.Length == 4 && char.IsAsciiDigit(k[3]) => (uint)(0x60 + k[3] - '0'),
            _ => 0,
        };
    }

    /// <summary>Canonical name of a virtual key accepted by <see cref="TryParse"/>, or null if the key is not supported.</summary>
    public static string? KeyName(uint vk) => vk switch
    {
        >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1),
        >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
        0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc", 0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End",
        0x21 => "PageUp", 0x22 => "PageDown", 0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x13 => "Pause",
        0xC0 => "OEM3", 0xBA => ";", 0xBC => ",", 0xBE => ".", 0xBF => "/", 0xDC => "\\",
        _ => null,
    };

    /// <summary>Canonical text ("Ctrl+Alt+F") of a combination; round-trips through <see cref="TryParse"/>.</summary>
    public static string Format(uint modifiers, uint vk)
    {
        var parts = new List<string>(5);
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(vk) ?? "?");
        return string.Join("+", parts);
    }

    /// <summary>Checks a captured combination before it is offered as a global hotkey.</summary>
    public static HotkeyValidation Validate(uint modifiers, uint vk)
    {
        if (KeyName(vk) == null) return HotkeyValidation.UnsupportedKey;
        if ((modifiers & (MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_WIN)) == 0) return HotkeyValidation.NoModifier;
        // Shift alone + a character key would steal ordinary typing (capital letters, symbols) in every application.
        bool typingKey = vk is >= 'A' and <= 'Z' or >= '0' and <= '9' or 0x20 or (>= 0xBA and <= 0xC0) or 0xDC or (>= 0x60 and <= 0x69);
        if ((modifiers & (MOD_CONTROL | MOD_ALT | MOD_WIN)) == 0 && typingKey) return HotkeyValidation.ShiftOnly;
        return HotkeyValidation.Ok;
    }
}

public enum HotkeyValidation { Ok, NoModifier, ShiftOnly, UnsupportedKey }
