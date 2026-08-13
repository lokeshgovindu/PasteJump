using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;

namespace PasteJump.Core.Settings;

/// <summary>One setting, as shown on the Advanced page.</summary>
/// <param name="Name">Display name. For a setting this is the property name, matching the key in the JSON file.</param>
/// <param name="TypeName">Friendly type name, e.g. <c>Boolean</c> or <c>Integer</c>.</param>
/// <param name="Value">Current value, formatted for display.</param>
/// <param name="Default">The value a fresh install would have.</param>
/// <param name="IsModified">Whether <paramref name="Value"/> differs from <paramref name="Default"/>.</param>
/// <param name="Key">
/// What this row identifies, for acting on it. The property name for a setting; for the two data locations,
/// which are not settings, the name without the file suffix that <paramref name="Name"/> carries. Separate from
/// <paramref name="Name"/> so a change to how rows are labelled cannot break Reset to Default.
/// </param>
public sealed record SettingRow(
    string Name,
    string TypeName,
    string Value,
    string Default,
    bool IsModified,
    string Key)
{
    /// <summary>
    /// Which tab holds the control for this setting, for the page that cannot change it.
    /// <para>
    /// Filled in by the dialog rather than here, because it is a fact about that dialog's layout and
    /// <c>Core</c> has no business knowing there are tabs at all. Empty until someone sets it - a row with no
    /// answer is a setting whose control nobody has recorded, which the UI smoke harness treats as a failure.
    /// </para>
    /// </summary>
    public string Where { get; init; } = string.Empty;

    /// <summary>
    /// Whether this row can be put back to its default on its own.
    /// <para>
    /// False for the parts of a composite setting - one paste-mode key binding, one per-application delay, one
    /// excluded program. Those are shown so the page can answer what is actually in force, but resetting one would
    /// mean rewriting part of a string, and the tab that owns the setting already does that properly.
    /// </para>
    /// </summary>
    public bool CanReset { get; init; } = true;
}

/// <summary>
/// Enumerates every setting with its current and default value.
/// <para>
/// Built by reflection over <see cref="PasteJumpSettings"/> rather than from a hand-written list, so a
/// new setting appears here the moment it is added to the class. A hand-maintained table would drift
/// the first time someone forgot to update it, and a settings inventory that is silently incomplete is
/// worse than none - it invites the reader to conclude a setting does not exist.
/// </para>
/// </summary>
public static class SettingsInspector
{
    /// <summary>
    /// All settings, ordered by name. Defaults come from a freshly constructed
    /// <see cref="PasteJumpSettings"/>, which is the single definition of "default" in the app.
    /// </summary>
    /// <param name="clipsLocation">
    /// Where clips are stored. Passed in rather than reflected, because it does not live in
    /// <see cref="PasteJumpSettings"/> - it is in <c>data-location.json</c>, since one of the two decides
    /// where <c>settings.json</c> itself is. Without these two arguments the Advanced page would be
    /// silently incomplete, which is worse than showing nothing: it invites the reader to conclude the
    /// setting does not exist.
    /// </param>
    /// <param name="settingsLocation">Where <c>settings.json</c> is stored. See <paramref name="clipsLocation"/>.</param>
    public static IReadOnlyList<SettingRow> Describe(
        PasteJumpSettings settings,
        DataLocation clipsLocation = DataLocation.ApplicationFolder,
        DataLocation settingsLocation = DataLocation.ApplicationFolder)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var defaults = new PasteJumpSettings();

        var rows = new List<SettingRow>
        {
            DescribeLocation("ClipsLocation", clipsLocation),
            DescribeLocation("SettingsLocation", settingsLocation),
        };

        foreach (var property in typeof(PasteJumpSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // Skip computed views over other settings - PasteModeOptions is one - since they are not
            // stored and showing them would imply they can be set independently.
            if (property.GetMethod is null
                || property.SetMethod is null
                || property.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
            {
                continue;
            }

            var current = Format(property.GetValue(settings));
            var original = Format(property.GetValue(defaults));

            rows.Add(new SettingRow(
                property.Name,
                FriendlyTypeName(property.PropertyType),
                current,
                original,
                !string.Equals(current, original, StringComparison.Ordinal),
                property.Name));
        }

        // Ordered first, then the parts of each composite setting are slotted in beneath their parent - so a child
        // sits with the row it belongs to rather than wherever its name happens to sort.
        var ordered = rows.OrderBy(static r => r.Name, StringComparer.Ordinal).ToList();
        var expanded = new List<SettingRow>(ordered.Count);

        foreach (var row in ordered)
        {
            expanded.Add(row);
            expanded.AddRange(Expand(row.Key, settings, defaults));
        }

        return expanded;
    }

    /// <summary>
    /// The parts of a setting that holds several values in one string or list, as rows of their own.
    /// <para>
    /// Three settings are like that, and each was a single opaque row: every paste-mode key binding lived in
    /// <c>PasteModeKeys</c>, every per-application delay in <c>PasteSettleDelayPerApp</c>, and every excluded
    /// program in <c>IgnoredProcesses</c>. An inventory whose job is to answer "what is PasteJump actually doing"
    /// cannot answer it with <c>back=C;newest=A;search=F;pin=P;join=J;…</c> in a column three inches wide.
    /// </para>
    /// <para>
    /// The children are <b>read-only detail</b> - <see cref="SettingRow.CanReset"/> is false. Resetting one would
    /// mean rewriting part of a string, and the tab that owns the setting already offers that per row. They still
    /// carry a default and a modified flag, because "which of my key bindings have I actually changed" is exactly
    /// what someone comes to this page to find out.
    /// </para>
    /// </summary>
    private static IEnumerable<SettingRow> Expand(string key, PasteJumpSettings settings, PasteJumpSettings defaults)
        => key switch
        {
            nameof(PasteJumpSettings.PasteModeKeys) => ExpandKeyMap(settings),
            nameof(PasteJumpSettings.PasteSettleDelayPerApp) => ExpandPerAppDelays(settings),
            nameof(PasteJumpSettings.IgnoredProcesses) => ExpandList(settings.IgnoredProcesses, nameof(PasteJumpSettings.IgnoredProcesses)),
            _ => [],
        };

    /// <summary>One row per paste-mode action, showing the letter in force and the letter it ships with.</summary>
    private static IEnumerable<SettingRow> ExpandKeyMap(PasteJumpSettings settings)
    {
        var map = PasteMode.PasteKeyMap.Parse(settings.PasteModeKeys);

        foreach (var entry in PasteMode.PasteKeyMap.Entries)
        {
            var letter = map.LetterFor(entry.Name);

            // "(off)" rather than blank: an action with no letter is a deliberate state, and an empty cell reads as
            // a rendering fault. Same word the Keys tab uses in its combo.
            var current = letter?.ToString() ?? "(off)";

            // Same word for a default of "no letter", which is what "mark to join" ships as: the Default column has
            // to be able to say "off" too, or an action that is off and has always been off would read as modified.
            var original = entry.DefaultLetter?.ToString() ?? "(off)";

            yield return new SettingRow(
                $"    {entry.Name} — {entry.Description}",
                "Key",
                current,
                original,
                !string.Equals(current, original, StringComparison.Ordinal),
                string.Empty)
            {
                CanReset = false,
            };
        }
    }

    /// <summary>One row per application with its own paste delay. None by default, so usually nothing.</summary>
    private static IEnumerable<SettingRow> ExpandPerAppDelays(PasteJumpSettings settings)
    {
        foreach (var entry in Paste.PerAppSettleDelays.Parse(settings.PasteSettleDelayPerApp).Entries)
        {
            yield return new SettingRow(
                $"    {entry.Process}",
                "Integer",
                entry.Milliseconds.ToString(System.Globalization.CultureInfo.CurrentCulture),
                $"{settings.PasteSettleDelayMs} (the global delay)",
                true,
                string.Empty)
            {
                CanReset = false,
            };
        }
    }

    /// <summary>One row per list entry, numbered so the order is visible.</summary>
    private static IEnumerable<SettingRow> ExpandList(IReadOnlyList<string> values, string name)
    {
        for (var i = 0; i < values.Count; i++)
        {
            yield return new SettingRow(
                $"    [{i}] {values[i]}",
                "String",
                values[i],
                "(none)",
                true,
                string.Empty)
            {
                CanReset = false,
            };
        }
    }

    /// <summary>
    /// A data-location row, formatted like the reflected ones so it does not read as a different kind of
    /// thing. Marked with its file, since it is not in <c>settings.json</c> and someone looking for it there
    /// would not find it.
    /// </summary>
    private static SettingRow DescribeLocation(string name, DataLocation value) => new(
        $"{name} ({DataLocationPointer.FileName})",
        string.Join(" | ", Enum.GetNames<DataLocation>()),
        value.ToString(),
        DataLocation.ApplicationFolder.ToString(),
        value != DataLocation.ApplicationFolder,
        name);

    private static string FriendlyTypeName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsEnum)
        {
            // More useful than "Enum": it tells the reader what the legal values are.
            return string.Join(" | ", Enum.GetNames(underlying));
        }

        if (underlying == typeof(bool))
        {
            return "Boolean";
        }

        if (underlying == typeof(int) || underlying == typeof(long))
        {
            return "Integer";
        }

        if (underlying == typeof(string))
        {
            return "String";
        }

        return underlying is { IsGenericType: true } && typeof(IEnumerable).IsAssignableFrom(underlying)
            ? "List"
            : underlying.Name;
    }

    private static string Format(object? value) => value switch
    {
        null => "(none)",
        bool flag => flag ? "True" : "False",
        string text => text.Length == 0 ? "(empty)" : text,

        // Lists render as a single line: the grid is one row per setting, and a multi-line cell would
        // break that. Empty is called out explicitly rather than shown as blank, which reads as a bug.
        IEnumerable<string> items => items.Any() ? string.Join(", ", items) : "(empty)",

        Enum enumValue => enumValue.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
