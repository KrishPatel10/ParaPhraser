using System.Windows.Input;

namespace ParaPhraser.Desktop.Services;

/// <summary>
/// Converts shortcuts to and from the text stored in settings, such as "Ctrl+Alt+G".
/// </summary>
public static class HotkeyText
{
    /// <summary>
    /// Returns null for a combination that would interfere with typing.
    /// Shift alone is refused, because Shift+G is just a capital letter.
    /// </summary>
    public static string? Format(ModifierKeys modifiers, Key key)
    {
        var hasChordKey = (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
        if (!hasChordKey || IsModifierKey(key) || key is Key.None or Key.Escape or Key.Tab)
        {
            return null;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        // Key.D1 is the 1 key, and people expect to read "1".
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        return string.Join('+', parts);
    }

    public static bool TryParse(string? text, out HotkeyModifiers modifiers, out uint virtualKey)
    {
        modifiers = HotkeyModifiers.None;
        virtualKey = 0;

        var parts = text?.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        // A lone digit is that number key. Any other number would parse as a
        // raw enum value, so "Ctrl+Alt+1" would mean Key.Cancel.
        var keyText = parts.Length > 0 && parts[^1] is [>= '0' and <= '9'] ? "D" + parts[^1] : parts.LastOrDefault();
        if (parts.Length < 2 || keyText!.All(char.IsDigit) || !Enum.TryParse<Key>(keyText, ignoreCase: true, out var key))
        {
            return false;
        }

        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToLowerInvariant() switch
            {
                "ctrl" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.NoRepeat
            };
        }

        if (modifiers.HasFlag(HotkeyModifiers.NoRepeat)
            || (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) == 0
            || IsModifierKey(key))
        {
            modifiers = HotkeyModifiers.None;
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System;
}
