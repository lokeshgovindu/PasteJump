using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasteJump.Core.Settings;

/// <summary>
/// Where the clips live and where the settings live - the two locations that cannot themselves be stored
/// in <c>settings.json</c>, because one of them decides where <c>settings.json</c> is. So they go in a
/// tiny file beside the executable instead, read before anything else.
/// <para>
/// Clips and settings are independent on purpose. They are wanted in different places for different
/// reasons: the clip database is large, machine-specific and the thing you want shared between a Debug
/// and a Release build, whereas settings are small and are what you want to keep beside a portable copy
/// so the folder carries its own configuration.
/// </para>
/// <para>
/// The <c>Migrate*From</c> fields are why this carries more than the two choices. Moving data has to
/// happen with the database closed, which means at startup rather than when the user clicks OK - so the
/// dialog records where each half is coming from and the next startup acts on it and clears the field.
/// Without that recorded intent the alternative is inferring "there is a database over there, so adopt
/// it", which silently swallows an unrelated history the first time someone unzips a fresh portable copy
/// on a machine that already has one.
/// </para>
/// </summary>
public sealed class DataLocationPointer
{
    /// <summary>Sits beside the executable, never inside either data directory.</summary>
    public const string FileName = "data-location.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,

        // Nulls omitted, so a written file never carries the superseded "location" key and never lists a
        // pending move that is not pending.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Names, not integers, for the same reason settings.json does it: this file is meant to be
        // readable and hand-editable, and "clipsLocation": 1 tells the reader nothing.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Where the database, blobs and logs live. Null means "not stated"; see <see cref="Clips"/>.</summary>
    public DataLocation? ClipsLocation { get; set; }

    /// <summary>Where <c>settings.json</c> lives. Null means "not stated"; see <see cref="Settings"/>.</summary>
    public DataLocation? SettingsLocation { get; set; }

    /// <summary>
    /// Single location written by the build that had one setting for both halves.
    /// <para>
    /// Honoured as the value for whichever of the two new fields is absent. Dropping it instead would
    /// send an existing install back to the application folder without saying so, and it would find no
    /// database there and appear to have lost every clip - the same silent failure
    /// <see cref="AppPaths.TryMigrateLegacyDatabase"/> exists to prevent.
    /// </para>
    /// </summary>
    [JsonPropertyName("location")]
    public DataLocation? LegacyLocation { get; set; }

    /// <summary>Root whose clips should be adopted on the next start, then cleared.</summary>
    public string? MigrateClipsFrom { get; set; }

    /// <summary>Root whose settings should be adopted on the next start, then cleared.</summary>
    public string? MigrateSettingsFrom { get; set; }

    /// <summary>Pending move recorded by the single-setting build. Applied to both halves.</summary>
    [JsonPropertyName("migrateFrom")]
    public string? LegacyMigrateFrom { get; set; }

    /// <summary>Resolved clips location, falling back through the legacy field to the default.</summary>
    [JsonIgnore]
    public DataLocation Clips => ClipsLocation ?? LegacyLocation ?? DataLocation.ApplicationFolder;

    /// <summary>Resolved settings location, falling back through the legacy field to the default.</summary>
    [JsonIgnore]
    public DataLocation Settings => SettingsLocation ?? LegacyLocation ?? DataLocation.ApplicationFolder;

    /// <summary>Resolved pending clips move.</summary>
    [JsonIgnore]
    public string? PendingClipsMove => MigrateClipsFrom ?? LegacyMigrateFrom;

    /// <summary>Resolved pending settings move.</summary>
    [JsonIgnore]
    public string? PendingSettingsMove => MigrateSettingsFrom ?? LegacyMigrateFrom;

    /// <summary>True when neither half differs from the default and nothing is pending.</summary>
    [JsonIgnore]
    public bool IsDefault =>
        Clips == DataLocation.ApplicationFolder
        && Settings == DataLocation.ApplicationFolder
        && PendingClipsMove is null
        && PendingSettingsMove is null;

    public static string PathFor(string applicationDirectory)
        => Path.Combine(applicationDirectory, FileName);

    /// <summary>
    /// Reads the pointer, falling back to defaults for anything unreadable.
    /// <para>
    /// Every failure degrades to <see cref="DataLocation.ApplicationFolder"/> rather than throwing. This
    /// runs before the app has a window or a log, so an exception here is a process that dies with no
    /// explanation - and the wrong data directory is recoverable while a silent failure to start is not.
    /// </para>
    /// </summary>
    public static DataLocationPointer Read(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        var path = PathFor(applicationDirectory);

        try
        {
            if (!File.Exists(path))
            {
                return new DataLocationPointer();
            }

            var json = File.ReadAllText(path);

            if (string.IsNullOrWhiteSpace(json))
            {
                return new DataLocationPointer();
            }

            var pointer = JsonSerializer.Deserialize<DataLocationPointer>(json, SerializerOptions)
                ?? new DataLocationPointer();

            pointer.Normalise();
            return pointer;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new DataLocationPointer();
        }
    }

    /// <summary>Discards anything a hand-edited file could have made nonsensical.</summary>
    private void Normalise()
    {
        ClipsLocation = Defined(ClipsLocation);
        SettingsLocation = Defined(SettingsLocation);
        LegacyLocation = Defined(LegacyLocation);

        MigrateClipsFrom = Trimmed(MigrateClipsFrom);
        MigrateSettingsFrom = Trimmed(MigrateSettingsFrom);
        LegacyMigrateFrom = Trimmed(LegacyMigrateFrom);

        static DataLocation? Defined(DataLocation? value)
            => value is { } v && Enum.IsDefined(v) ? v : null;

        static string? Trimmed(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Writes the pointer, or removes it when it holds nothing but defaults. Returns false if the file
    /// could not be written - which is the case worth surfacing, because it means the choice will not
    /// survive a restart.
    /// </summary>
    public bool TryWrite(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        var path = PathFor(applicationDirectory);

        try
        {
            // No file at all is the canonical way to say "defaults, nothing pending", so a portable
            // folder that has never been reconfigured stays clean.
            if (IsDefault)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }

            // Collapsed onto the current fields before writing, so a file that came in with the legacy
            // single "location" key goes out in the two-field form and stops needing the fallback.
            var written = new DataLocationPointer
            {
                ClipsLocation = Clips,
                SettingsLocation = Settings,
                MigrateClipsFrom = PendingClipsMove,
                MigrateSettingsFrom = PendingSettingsMove,
            };

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(written, SerializerOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
