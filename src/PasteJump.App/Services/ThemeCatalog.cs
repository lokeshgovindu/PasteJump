using System.Windows;
using System.Windows.Media;
using PasteJump.Core;
using PasteJump.Core.Theming;

namespace PasteJump.App.Services;

/// <summary>
/// Every theme the application can offer: the two built-in palettes, the shipped extras, and whatever the user has
/// put in their themes folder.
/// <para>
/// Discovery happens on demand rather than being watched for changes. A theme is chosen from a dialog, so
/// re-reading the folder when that dialog opens is enough - and a <c>FileSystemWatcher</c> on a folder the user
/// edits by hand would fire mid-save, on a half-written file.
/// </para>
/// </summary>
public sealed class ThemeCatalog
{
    private readonly AppPaths _paths;

    /// <summary>Problems found while reading theme files, newest scan only. Surfaced by the settings dialog.</summary>
    private readonly List<string> _problems = [];

    public ThemeCatalog(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

        Themes = BuiltInThemes.All;
    }

    /// <summary>
    /// Themes beyond Light and Dark, shipped and user-authored together. Ordered with the shipped ones first, then
    /// the user's alphabetically - so a folder full of files does not push the built-ins off the end of a combo.
    /// </summary>
    public IReadOnlyList<ThemeDefinition> Themes { get; private set; }

    /// <summary>What went wrong in the last <see cref="Refresh"/>, one line per file. Empty when all was well.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>Where a user's own theme files live. Created on demand, never assumed to exist.</summary>
    public string Folder => Path.Combine(_paths.SettingsDirectory, "themes");

    /// <summary>
    /// Re-reads the themes folder.
    /// <para>
    /// A file that will not parse is <em>reported and skipped</em>, not fatal: one bad file must not cost the user
    /// their other themes, and this runs at start-up where there is nothing to report into yet. The message is kept
    /// for the settings dialog to show, which is where someone editing a theme will be looking.
    /// </para>
    /// <para>
    /// A user theme whose name collides with a shipped one replaces it. That is the useful way round - it is how
    /// someone tweaks Midnight without having to invent a new name - and it cannot hide a built-in palette, since
    /// Light, Dark and System are refused as names by the parser.
    /// </para>
    /// </summary>
    public void Refresh()
    {
        _problems.Clear();

        var byName = new Dictionary<string, ThemeDefinition>(StringComparer.OrdinalIgnoreCase);
        var shipped = new List<ThemeDefinition>(BuiltInThemes.All);
        var mine = new List<ThemeDefinition>();

        foreach (var theme in shipped)
        {
            byName[theme.Name] = theme;
        }

        try
        {
            if (!Directory.Exists(Folder))
            {
                Themes = shipped;
                return;
            }

            foreach (var file in Directory.EnumerateFiles(Folder, "*.json").OrderBy(static f => f, StringComparer.OrdinalIgnoreCase))
            {
                string json;

                try
                {
                    json = File.ReadAllText(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _problems.Add($"{Path.GetFileName(file)}: could not be read ({exception.Message})");
                    continue;
                }

                if (!ThemeDefinition.TryParse(json, out var theme, out var error))
                {
                    _problems.Add($"{Path.GetFileName(file)}: {error}");
                    continue;
                }

                // Replacing a shipped theme of the same name rather than showing two identically-named entries,
                // which would leave the user unable to tell which one the combo was about to apply.
                if (byName.TryGetValue(theme.Name, out var existing) && shipped.Remove(existing))
                {
                    mine.Add(theme);
                }
                else if (byName.ContainsKey(theme.Name))
                {
                    _problems.Add($"{Path.GetFileName(file)}: another theme is already called \"{theme.Name}\".");
                    continue;
                }
                else
                {
                    mine.Add(theme);
                }

                byName[theme.Name] = theme;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _problems.Add($"The themes folder could not be read: {exception.Message}");
        }

        Themes = [.. shipped, .. mine.OrderBy(static t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The theme of that name, or null for a built-in name or one that no longer exists.</summary>
    public ThemeDefinition? Find(string? name)
        => name is null
            ? null
            : Themes.FirstOrDefault(theme => string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes the current palette out as a theme file and returns its path, so someone can start from what they are
    /// already looking at rather than from a blank file.
    /// <para>
    /// Generated from <see cref="PaletteKeys.All"/> and the live resources rather than from a checked-in sample, so
    /// it cannot fall behind the contract: a key added to the palette appears here on the next export, with the
    /// colour actually in force. Every key is written out, which is more than a theme needs - but a starting point
    /// that shows the whole surface is more useful than one that hides most of it.
    /// </para>
    /// </summary>
    public string WriteStartingPoint(string name, bool dark, ResourceDictionary palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        Directory.CreateDirectory(Folder);

        var builder = new System.Text.StringBuilder();

        builder.AppendLine("{");
        builder.AppendLine($"    \"name\": {Quote(name)},");
        builder.AppendLine($"    \"basedOn\": \"{(dark ? "dark" : "light")}\",");
        builder.AppendLine();
        builder.AppendLine("    // Every key is listed here with the colour currently in force. Delete any line you");
        builder.AppendLine("    // do not want to change - anything absent is inherited from the theme named above.");
        builder.AppendLine("    \"colors\": {");

        var keys = PaletteKeys.All;

        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            var comma = i == keys.Count - 1 ? string.Empty : ",";

            builder.AppendLine($"        // {key.Description}");
            builder.AppendLine($"        \"{key.Name}\": {Describe(palette, key)}{comma}");

            if (i != keys.Count - 1)
            {
                builder.AppendLine();
            }
        }

        builder.AppendLine("    }");
        builder.AppendLine("}");

        // A name that is not a legal file name is the user's, so it is sanitised rather than refused - they typed a
        // theme name, not a path.
        var fileName = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var path = Path.Combine(Folder, $"{(fileName.Length == 0 ? "Theme" : fileName)}.json");

        File.WriteAllText(path, builder.ToString());

        return path;
    }

    /// <summary>
    /// One palette entry as a theme file would write it. Reads the live resource, so the exported file starts from
    /// what is on screen.
    /// </summary>
    private static string Describe(ResourceDictionary palette, PaletteKey key)
    {
        var value = palette[key.Name];

        return value switch
        {
            SolidColorBrush brush => Quote(Hex(brush.Color)),
            Color color => Quote(Hex(color)),

            // Two stops become the array form. More than two cannot be expressed, so the first and last are used
            // and the middle is lost - which is why the palette is asserted to hold exactly one gradient.
            LinearGradientBrush gradient when gradient.GradientStops.Count >= 2 =>
                $"[{Quote(Hex(gradient.GradientStops[0].Color))}, {Quote(Hex(gradient.GradientStops[^1].Color))}]",

            LinearGradientBrush gradient when gradient.GradientStops.Count == 1 => Quote(Hex(gradient.GradientStops[0].Color)),

            // Should not happen - the smoke harness asserts every key resolves to something this understands - so
            // a visible placeholder beats a crash while someone is exporting a file.
            _ => "\"#FF00FF\"",
        };
    }

    private static string Hex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string Quote(string text) => $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
