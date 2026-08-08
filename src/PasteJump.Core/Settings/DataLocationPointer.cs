using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasteJump.Core.Settings;

/// <summary>
/// The one setting that cannot live in <c>settings.json</c>, because <c>settings.json</c> lives inside
/// the directory this selects. So it goes in a tiny file beside the executable instead, read before
/// anything else.
/// <para>
/// <see cref="MigrateFrom"/> is why this carries more than just the choice. Moving the data has to happen
/// with the database closed, which means it happens at startup rather than at the moment the user clicks
/// OK - so the dialog records where the data is coming from, and the next startup acts on it and clears
/// the field. Without that recorded intent the alternative is inferring "there is a database over there,
/// so adopt it", which silently swallows an unrelated history the first time someone unzips a fresh
/// portable copy on a machine that already has one.
/// </para>
/// </summary>
public sealed class DataLocationPointer
{
    /// <summary>Sits beside the executable, never inside the data directory.</summary>
    public const string FileName = "data-location.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,

        // Names, not integers, for the same reason settings.json does it: this file is meant to be
        // readable and hand-editable, and "location": 1 tells the reader nothing.
        Converters = { new JsonStringEnumConverter() },
    };

    public DataLocation Location { get; set; } = DataLocation.ApplicationFolder;

    /// <summary>
    /// Root directory whose <c>data</c> folder should be adopted on the next start, then cleared. Null
    /// when there is nothing pending.
    /// </summary>
    public string? MigrateFrom { get; set; }

    public static string PathFor(string applicationDirectory)
        => Path.Combine(applicationDirectory, FileName);

    /// <summary>
    /// Reads the pointer, falling back to the default for anything unreadable.
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

            if (!Enum.IsDefined(pointer.Location))
            {
                pointer.Location = DataLocation.ApplicationFolder;
            }

            if (string.IsNullOrWhiteSpace(pointer.MigrateFrom))
            {
                pointer.MigrateFrom = null;
            }

            return pointer;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new DataLocationPointer();
        }
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
            // No file at all is the canonical way to say "default, nothing pending", so a portable
            // folder that has never been reconfigured stays clean.
            if (Location == DataLocation.ApplicationFolder && MigrateFrom is null)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
