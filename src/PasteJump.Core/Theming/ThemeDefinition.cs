using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace PasteJump.Core.Theming;

/// <summary>A colour as four bytes. No WPF here, so <c>Core</c> stays free of it; the App turns these into brushes.</summary>
public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    /// <summary>Back to <c>#AARRGGBB</c>, which is what a theme file writes and what WPF parses.</summary>
    public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// What a theme says for one palette key: one colour, or two for a gradient.
/// </summary>
/// <param name="Top">The colour, or the top of a gradient.</param>
/// <param name="Bottom">The bottom of a gradient, or null for a flat colour.</param>
public readonly record struct ThemeValue(ThemeColor Top, ThemeColor? Bottom);

/// <summary>Which built-in palette a theme starts from - and, separately, how Windows draws its title bars.</summary>
public enum ThemeBase
{
    Light,
    Dark,
}

/// <summary>
/// A user-authored theme: a name, a base palette, and colours for as many keys as it cares to name.
/// <para>
/// <b>Partial by design, and that is the answer to this feature's central trap.</b> Every palette key is reached
/// through <c>DynamicResource</c>, so a key a theme fails to define resolves to nothing and the control renders
/// unstyled with no error - which would make "you must supply all 25 keys" the only safe rule, and writing a theme
/// a chore. Instead a theme <em>inherits</em> from Light or Dark and overrides what it names, so a three-line file
/// that recolours the accent is legal and complete.
/// </para>
/// <para>
/// <c>basedOn</c> does double duty and both halves matter: it fills the keys the file omits, and it decides whether
/// Windows draws dark title bars and window borders. Those are drawn by DWM from an API call rather than from the
/// palette, so a dark theme declaring itself light would render correct content inside a white title bar.
/// </para>
/// </summary>
public sealed class ThemeDefinition
{
    /// <summary>Longest a theme name may be. Long enough for a real name, short enough for the settings combo.</summary>
    public const int MaxNameLength = 40;

    private ThemeDefinition(string name, ThemeBase basedOn, IReadOnlyDictionary<string, ThemeValue> colors)
    {
        Name = name;
        BasedOn = basedOn;
        Colors = colors;
    }

    /// <summary>What the settings dialog shows. Also how the theme is stored in settings, so it must be stable.</summary>
    public string Name { get; }

    public ThemeBase BasedOn { get; }

    /// <summary>The keys this theme overrides. Anything absent comes from <see cref="BasedOn"/>.</summary>
    public IReadOnlyDictionary<string, ThemeValue> Colors { get; }

    /// <summary>
    /// Reads a theme from JSON, or explains why it cannot.
    /// <para>
    /// <b>Refuses rather than degrading</b>, unlike the settings parsers that fall back to defaults: a person chose
    /// this file, so naming the problem beats silently loading something else. Every rejection names the key or the
    /// value at fault, because "invalid theme" tells the author nothing.
    /// </para>
    /// <para>
    /// An <b>unknown key is an error</b>, not something to ignore. A typo like <c>SurfceBrush</c> would otherwise
    /// produce a theme that loaded cleanly and did nothing, and there is no way for the author to tell that from a
    /// colour that simply looks wrong. The keys are case-sensitive for the same reason - see
    /// <see cref="PaletteKeys.Find"/>.
    /// </para>
    /// </summary>
    public static bool TryParse(
        string? json,
        [NotNullWhen(true)] out ThemeDefinition? theme,
        [NotNullWhen(false)] out string? error)
    {
        theme = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The file is empty.";
            return false;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException exception)
        {
            // The parser's own message names the line and position, which is the most useful thing to pass on.
            error = $"This is not valid JSON: {exception.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "A theme file must be a JSON object with \"name\" and \"colors\".";
                return false;
            }

            if (!TryReadName(root, out var name, out error))
            {
                return false;
            }

            if (!TryReadBase(root, out var basedOn, out error))
            {
                return false;
            }

            if (!TryReadColors(root, out var colors, out error))
            {
                return false;
            }

            theme = new ThemeDefinition(name, basedOn, colors);
            return true;
        }
    }

    private static bool TryReadName(
        JsonElement root,
        [NotNullWhen(true)] out string? name,
        [NotNullWhen(false)] out string? error)
    {
        name = null;
        error = null;

        if (!root.TryGetProperty("name", out var element) || element.ValueKind != JsonValueKind.String)
        {
            error = "The theme needs a \"name\", which is what the settings dialog will show.";
            return false;
        }

        var value = element.GetString()?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            error = "The theme's \"name\" is empty.";
            return false;
        }

        if (value.Length > MaxNameLength)
        {
            error = $"The theme's \"name\" is longer than {MaxNameLength} characters.";
            return false;
        }

        // Refused because the name is how the theme is stored in settings and shown in a combo beside the built-ins.
        // A theme called "Dark" could not be chosen - the reserved name would win - so it would look like the file
        // was being ignored.
        if (IsReservedName(value))
        {
            error = $"\"{value}\" is a built-in theme name. Choose another.";
            return false;
        }

        name = value;
        return true;
    }

    /// <summary>
    /// Whether a name belongs to the built-in choices, which a file may not take. Held here rather than in the App
    /// so the check and the refusal message cannot disagree.
    /// </summary>
    public static bool IsReservedName(string? name)
        => string.Equals(name, "Light", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Dark", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "System", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadBase(JsonElement root, out ThemeBase basedOn, [NotNullWhen(false)] out string? error)
    {
        // Light is the default, matching the application's own default theme, so a file that omits basedOn behaves
        // like the palette everything else starts from.
        basedOn = ThemeBase.Light;
        error = null;

        if (!root.TryGetProperty("basedOn", out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = "\"basedOn\" must be \"light\" or \"dark\".";
            return false;
        }

        var value = element.GetString();

        if (string.Equals(value, "light", StringComparison.OrdinalIgnoreCase))
        {
            basedOn = ThemeBase.Light;
            return true;
        }

        if (string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase))
        {
            basedOn = ThemeBase.Dark;
            return true;
        }

        error = $"\"basedOn\" must be \"light\" or \"dark\", not \"{value}\".";
        return false;
    }

    private static bool TryReadColors(
        JsonElement root,
        [NotNullWhen(true)] out IReadOnlyDictionary<string, ThemeValue>? colors,
        [NotNullWhen(false)] out string? error)
    {
        colors = null;
        error = null;

        if (!root.TryGetProperty("colors", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            error = "The theme needs a \"colors\" object, even if it only sets one key.";
            return false;
        }

        var parsed = new Dictionary<string, ThemeValue>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (PaletteKeys.Find(property.Name) is not { } key)
            {
                error = $"\"{property.Name}\" is not a palette key. See the sample theme for the full list.";
                return false;
            }

            if (!TryReadValue(key, property.Value, out var value, out error))
            {
                return false;
            }

            parsed[key.Name] = value;
        }

        colors = parsed;
        return true;
    }

    private static bool TryReadValue(
        PaletteKey key,
        JsonElement element,
        out ThemeValue value,
        [NotNullWhen(false)] out string? error)
    {
        value = default;
        error = null;

        if (element.ValueKind == JsonValueKind.String)
        {
            if (!TryParseColor(element.GetString(), out var flat, out var reason))
            {
                error = $"\"{key.Name}\": {reason}";
                return false;
            }

            value = new ThemeValue(flat, null);
            return true;
        }

        // An array is the gradient form, and only the one gradient key accepts it. Rejected elsewhere rather than
        // quietly taking the first entry, which would hide a misunderstanding about which keys are gradients.
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (key.Kind != PaletteEntryKind.Gradient)
            {
                error = $"\"{key.Name}\" is a single colour, not a gradient.";
                return false;
            }

            var stops = element.EnumerateArray().ToList();

            if (stops.Count != 2)
            {
                error = $"\"{key.Name}\" as a gradient needs exactly two colours, the top and the bottom.";
                return false;
            }

            if (!TryParseColor(stops[0].ValueKind == JsonValueKind.String ? stops[0].GetString() : null, out var top, out var topReason))
            {
                error = $"\"{key.Name}\" first colour: {topReason}";
                return false;
            }

            if (!TryParseColor(stops[1].ValueKind == JsonValueKind.String ? stops[1].GetString() : null, out var bottom, out var bottomReason))
            {
                error = $"\"{key.Name}\" second colour: {bottomReason}";
                return false;
            }

            value = new ThemeValue(top, bottom);
            return true;
        }

        error = $"\"{key.Name}\" must be a colour such as \"#2563EB\".";
        return false;
    }

    /// <summary>
    /// Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c>, and says what is wrong otherwise.
    /// <para>
    /// Hex only. WPF would also accept the 140-odd named colours, and supporting them here would mean carrying that
    /// table in <c>Core</c> - so they are refused with a message that says what to write instead, rather than
    /// accepted in the App and rejected here.
    /// </para>
    /// </summary>
    public static bool TryParseColor(string? text, out ThemeColor color, [NotNullWhen(false)] out string? error)
    {
        color = default;
        error = null;

        var value = text?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            error = "no colour was given.";
            return false;
        }

        if (value[0] != '#')
        {
            error = $"\"{value}\" must start with # - colour names are not supported, so write #RRGGBB.";
            return false;
        }

        var digits = value[1..];

        if (!digits.All(char.IsAsciiHexDigit))
        {
            error = $"\"{value}\" has something in it that is not a hex digit.";
            return false;
        }

        switch (digits.Length)
        {
            case 3:
                // #RGB, each digit doubled, exactly as CSS and WPF read it.
                color = new ThemeColor(
                    255,
                    (byte)(Nibble(digits[0]) * 17),
                    (byte)(Nibble(digits[1]) * 17),
                    (byte)(Nibble(digits[2]) * 17));
                return true;

            case 6:
                color = new ThemeColor(255, Byte(digits, 0), Byte(digits, 2), Byte(digits, 4));
                return true;

            case 8:
                color = new ThemeColor(Byte(digits, 0), Byte(digits, 2), Byte(digits, 4), Byte(digits, 6));
                return true;

            default:
                error = $"\"{value}\" is not #RGB, #RRGGBB or #AARRGGBB.";
                return false;
        }

        static int Nibble(char c) => Convert.ToInt32(c.ToString(), 16);

        static byte Byte(string text, int at) => Convert.ToByte(text.Substring(at, 2), 16);
    }
}
