using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasteJump.Core.Settings;

/// <summary>
/// Loads and saves <see cref="PasteJumpSettings"/> as JSON.
/// <para>
/// A corrupt or unreadable file yields defaults rather than an exception. That is a deliberate
/// choice for a tray app that starts at logon: a settings file mangled by a crash or a bad hand
/// edit must not leave the user with no clipboard manager and no visible reason why.
/// </para>
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,

        // Enums as names, not integers. This file is documented as hand-editable, and "theme": 1
        // tells the reader nothing while also breaking silently if the enum is ever reordered.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.SettingsFile;
    }

    /// <summary>Set when the last <see cref="Load"/> fell back to defaults because the file was unusable.</summary>
    public string? LastLoadError { get; private set; }

    public PasteJumpSettings Load()
    {
        LastLoadError = null;

        try
        {
            if (!File.Exists(_path))
            {
                return new PasteJumpSettings();
            }

            var json = File.ReadAllText(_path);

            if (string.IsNullOrWhiteSpace(json))
            {
                return new PasteJumpSettings();
            }

            var settings = JsonSerializer.Deserialize<PasteJumpSettings>(json, SerializerOptions)
                ?? new PasteJumpSettings();

            settings.Normalise();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LastLoadError = ex.Message;
            return new PasteJumpSettings();
        }
    }

    public void Save(PasteJumpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Normalise();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Write-then-replace: a power loss mid-save leaves the previous good file intact rather
        // than a truncated one.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, SerializerOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
