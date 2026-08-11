using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasteJump.Core.Settings;

/// <summary>
/// Reads and writes a settings file the user chose, for moving a configuration between machines or keeping a copy
/// before experimenting.
/// <para>
/// Separate from <see cref="SettingsStore"/>, which owns the one file the app actually runs from. Sharing the
/// serializer options with it is deliberate - an exported file is the same shape as <c>PasteJump.json</c>, so it
/// can be dropped into place by hand and read back by either route.
/// </para>
/// </summary>
public static class SettingsTransfer
{
    /// <summary>Matches <see cref="SettingsStore"/> exactly, so exported and live files are interchangeable.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The suggested file name, so two exports from different days do not overwrite each other.</summary>
    public static string SuggestFileName(DateTimeOffset now)
        => $"PasteJump-settings-{now:yyyy-MM-dd}.json";

    /// <summary>
    /// Renders settings for export.
    /// <para>
    /// <b>What is deliberately not in here:</b> where the clips and the settings live. Those are in
    /// <c>data-location.json</c> rather than this class, and they are machine-specific paths - carrying
    /// <c>D:\Clips</c> onto a laptop that has no D: drive would be worse than useless. An import therefore never
    /// moves anyone's data, which also makes it a safe thing to try.
    /// </para>
    /// </summary>
    public static string Export(PasteJumpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return JsonSerializer.Serialize(settings, SerializerOptions);
    }

    /// <summary>
    /// Reads an exported file. Returns null and sets <paramref name="error"/> when it cannot be used.
    /// <para>
    /// Refused rather than partially applied, unlike <see cref="SettingsStore.Load"/>, which degrades to defaults
    /// on a broken file because failing there would leave the user with no clipboard manager and no explanation.
    /// Here there is a person watching who chose the file, so saying which file is wrong beats silently loading
    /// defaults over the settings they already had.
    /// </para>
    /// </summary>
    /// <param name="carryForward">
    /// The settings currently in force. Two values are taken from these rather than from the file: the one-time
    /// legacy-import flag, because importing someone else's "already done" would silently suppress the Clipjump
    /// import prompt on a machine that has never run it; and nothing else - listed explicitly so the exception
    /// stays visible rather than growing quietly.
    /// </param>
    public static PasteJumpSettings? TryImport(string? json, PasteJumpSettings carryForward, out string error)
    {
        ArgumentNullException.ThrowIfNull(carryForward);

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "That file is empty.";
            return null;
        }

        // Shape checked BEFORE deserialising, so each kind of bad file gets the message that helps most. Done the
        // other way round, "[]" and "42" are valid JSON that the deserialiser rejects, so they came back as a
        // parser complaint about a type conversion - true, and no use at all to someone who picked the wrong file.
        if (!DescribesSettings(json, out error))
        {
            return null;
        }

        PasteJumpSettings? imported;

        try
        {
            imported = JsonSerializer.Deserialize<PasteJumpSettings>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            // Reached when the document is shaped right but a value is not - a string where a number belongs. The
            // parser names the line and position, which is genuinely useful for a hand-edited file.
            error = $"That file is not valid JSON. {ex.Message}";
            return null;
        }

        if (imported is null)
        {
            error = "That file holds no settings.";
            return null;
        }

        imported.LegacyImportCompleted = carryForward.LegacyImportCompleted;
        imported.Normalise();

        error = string.Empty;
        return imported;
    }

    /// <summary>
    /// Whether the document is an object naming at least one setting this version knows.
    /// <para>
    /// Deliberately not a version stamp or a schema check. A file exported by a newer build, or edited by hand to
    /// hold only the three settings someone cared about, is a perfectly good import - anything absent simply keeps
    /// its default. All this rejects is a file that is not about PasteJump at all, which would otherwise
    /// deserialise to an object full of defaults and read as a successful import that reset everything.
    /// </para>
    /// </summary>
    private static bool DescribesSettings(string json, out string error)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            error = $"That file is not valid JSON. {ex.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "That file does not look like exported PasteJump settings.";
                return false;
            }

            var known = typeof(PasteJumpSettings)
                .GetProperties()
                .Select(static p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (known.Contains(property.Name))
                {
                    error = string.Empty;
                    return true;
                }
            }

            error = "That file does not look like exported PasteJump settings.";
            return false;
        }
    }
}
