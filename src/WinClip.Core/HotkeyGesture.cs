namespace WinClip.Core;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>A global hotkey such as "Ctrl+Alt+V". <see cref="Key"/> is a WPF <c>Key</c> name.</summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public static bool TryParse(string? value, out HotkeyGesture gesture)
    {
        gesture = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
                return false;
            modifiers |= modifier;
        }

        // A global hotkey without a modifier would swallow that key system-wide.
        if (modifiers == HotkeyModifiers.None || parts.Length < 2)
            return false;

        gesture = new HotkeyGesture(modifiers, parts[^1]);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }
}
