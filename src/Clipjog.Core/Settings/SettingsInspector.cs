using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Clipjog.Core.Settings;

/// <summary>One setting, as shown on the Advanced page.</summary>
/// <param name="Name">Property name, matching the key in <c>settings.json</c>.</param>
/// <param name="TypeName">Friendly type name, e.g. <c>Boolean</c> or <c>Integer</c>.</param>
/// <param name="Value">Current value, formatted for display.</param>
/// <param name="Default">The value a fresh install would have.</param>
/// <param name="IsModified">Whether <paramref name="Value"/> differs from <paramref name="Default"/>.</param>
public sealed record SettingRow(
    string Name,
    string TypeName,
    string Value,
    string Default,
    bool IsModified);

/// <summary>
/// Enumerates every setting with its current and default value.
/// <para>
/// Built by reflection over <see cref="ClipjogSettings"/> rather than from a hand-written list, so a
/// new setting appears here the moment it is added to the class. A hand-maintained table would drift
/// the first time someone forgot to update it, and a settings inventory that is silently incomplete is
/// worse than none - it invites the reader to conclude a setting does not exist.
/// </para>
/// </summary>
public static class SettingsInspector
{
    /// <summary>
    /// All settings, ordered by name. Defaults come from a freshly constructed
    /// <see cref="ClipjogSettings"/>, which is the single definition of "default" in the app.
    /// </summary>
    public static IReadOnlyList<SettingRow> Describe(ClipjogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var defaults = new ClipjogSettings();
        var rows = new List<SettingRow>();

        foreach (var property in typeof(ClipjogSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
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
                !string.Equals(current, original, StringComparison.Ordinal)));
        }

        return [.. rows.OrderBy(static r => r.Name, StringComparer.Ordinal)];
    }

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
