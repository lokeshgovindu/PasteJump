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

        return [.. rows.OrderBy(static r => r.Name, StringComparer.Ordinal)];
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
