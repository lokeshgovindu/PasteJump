using System.Diagnostics.CodeAnalysis;

namespace PasteJump.Core.Settings;

/// <summary>
/// A system-wide hotkey, parsed from and rendered to a string like <c>Ctrl+Shift+H</c>.
/// <para>
/// Parsing lives in <c>Core</c> rather than beside <c>RegisterHotKey</c> so it can be tested. The
/// interesting cases are all textual - a hand-edited settings file, a chord with no modifier, a name
/// spelled <c>Control</c> instead of <c>Ctrl</c> - and none of them need Win32 to exercise.
/// </para>
/// </summary>
/// <param name="Control">Ctrl is part of the chord.</param>
/// <param name="Alt">Alt is part of the chord.</param>
/// <param name="Shift">Shift is part of the chord.</param>
/// <param name="Windows">The Windows key is part of the chord.</param>
/// <param name="VirtualKey">Virtual-key code of the non-modifier key.</param>
public readonly record struct HotkeySpec(bool Control, bool Alt, bool Shift, bool Windows, int VirtualKey)
{
    /// <summary>
    /// Keys offered for a hotkey, as name/virtual-key pairs.
    /// <para>
    /// A closed list rather than anything the layout can produce. A global hotkey takes the chord away
    /// from every other application on the desktop, so the set is restricted to keys a user would
    /// deliberately reserve - letters, digits and the function keys - and excludes the ones that would be
    /// actively hostile to steal, such as Escape, Tab and the modifiers themselves.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Name, int VirtualKey)> AvailableKeys { get; } = BuildAvailableKeys();

    /// <summary>True when this actually names a key. The default value means "no hotkey".</summary>
    public bool IsSet => VirtualKey != 0;

    /// <summary>
    /// True when at least one modifier is present.
    /// <para>
    /// A modifierless global hotkey is a trap: registering bare <c>H</c> makes that letter untypeable in
    /// every application on the desktop, and the user's only route back is editing the settings file with
    /// a keyboard that can no longer type the letter H.
    /// </para>
    /// </summary>
    public bool HasModifier => Control || Alt || Shift || Windows;

    public bool IsValid => IsSet && HasModifier;

    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var control = false;
        var alt = false;
        var shift = false;
        var windows = false;
        var virtualKey = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                // Several spellings each, because this file is documented as hand-editable and "Control"
                // and "Win" are what people type.
                case "CTRL" or "CONTROL":
                    control = true;
                    continue;

                case "ALT":
                    alt = true;
                    continue;

                case "SHIFT":
                    shift = true;
                    continue;

                case "WIN" or "WINDOWS" or "LWIN":
                    windows = true;
                    continue;
            }

            // Two keys in one chord is not a typo we can guess at, so it is rejected rather than having
            // the last one silently win.
            if (virtualKey != 0)
            {
                return false;
            }

            var match = AvailableKeys.FirstOrDefault(k =>
                string.Equals(k.Name, raw, StringComparison.OrdinalIgnoreCase));

            if (match.VirtualKey == 0)
            {
                return false;
            }

            virtualKey = match.VirtualKey;
        }

        spec = new HotkeySpec(control, alt, shift, windows, virtualKey);
        return spec.IsValid;
    }

    /// <summary>Parses, or returns the unset value for anything unusable.</summary>
    public static HotkeySpec ParseOrNone(string? text) => TryParse(text, out var spec) ? spec : default;

    /// <summary>
    /// Renders in the canonical order Ctrl, Alt, Shift, Win, then the key - so a round-trip is stable and
    /// two specs that mean the same thing produce the same string.
    /// </summary>
    public override string ToString()
    {
        if (!IsSet)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);

        if (Control)
        {
            parts.Add("Ctrl");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Windows)
        {
            parts.Add("Win");
        }

        parts.Add(NameFor(VirtualKey) ?? $"0x{VirtualKey:X2}");

        return string.Join("+", parts);
    }

    public static string? NameFor(int virtualKey) => AvailableKeys
        .Where(k => k.VirtualKey == virtualKey)
        .Select(static k => k.Name)
        .FirstOrDefault();

    [SuppressMessage(
        "Performance",
        "CA1861:Avoid constant arrays as arguments",
        Justification = "Runs once, building a static list.")]
    private static List<(string Name, int VirtualKey)> BuildAvailableKeys()
    {
        var keys = new List<(string, int)>();

        for (var c = 'A'; c <= 'Z'; c++)
        {
            // Virtual-key codes for A-Z are the ASCII codes of the uppercase letters, which is why this
            // needs no lookup table.
            keys.Add((c.ToString(), c));
        }

        for (var d = '0'; d <= '9'; d++)
        {
            keys.Add((d.ToString(), d));
        }

        for (var f = 1; f <= 12; f++)
        {
            // VK_F1 is 0x70 and the rest follow consecutively.
            keys.Add(($"F{f}", 0x70 + f - 1));
        }

        keys.Add(("Insert", 0x2D));
        keys.Add(("Delete", 0x2E));
        keys.Add(("Home", 0x24));
        keys.Add(("End", 0x23));
        keys.Add(("PageUp", 0x21));
        keys.Add(("PageDown", 0x22));

        return keys;
    }
}
