using PasteJump.Core.Settings;

namespace PasteJump.Core;

/// <summary>
/// Every filesystem location the app uses, resolved in exactly one place.
/// <para>
/// Path resolution deliberately goes through <see cref="Environment.ProcessPath"/> rather than
/// <c>Assembly.Location</c>. Under a single-file publish <c>Assembly.Location</c> returns an
/// empty string, so code that uses it works in a folder deployment and silently breaks the day
/// you flip <c>PublishSingleFile</c> on. Funnelling it here means that switch stays a two-line
/// csproj change instead of a bug hunt.
/// </para>
/// </summary>
public sealed class AppPaths
{
    private AppPaths(
        string clipsRoot,
        string settingsRoot,
        DataLocation clipsLocation,
        DataLocation settingsLocation)
    {
        ClipsRoot = clipsRoot;
        SettingsRoot = settingsRoot;
        ClipsLocation = clipsLocation;
        SettingsLocation = settingsLocation;
    }

    /// <summary>Directory the clips' <c>data</c> folder sits beneath.</summary>
    public string ClipsRoot { get; }

    /// <summary>Directory the settings' <c>data</c> folder sits beneath. Independent of <see cref="ClipsRoot"/>.</summary>
    public string SettingsRoot { get; }

    /// <summary>Which of the supported locations <see cref="ClipsRoot"/> came from.</summary>
    public DataLocation ClipsLocation { get; }

    /// <summary>Which of the supported locations <see cref="SettingsRoot"/> came from.</summary>
    public DataLocation SettingsLocation { get; }

    /// <summary>Holds the database, its write-ahead log sidecars and the blob store.</summary>
    public string ClipsDirectory => Path.Combine(ClipsRoot, "data");

    /// <summary>Holds <c>settings.json</c>, and nothing else.</summary>
    public string SettingsDirectory => Path.Combine(SettingsRoot, "data");

    public string DatabaseFile => Path.Combine(ClipsDirectory, "pastejump.db");

    public string BlobsDirectory => Path.Combine(ClipsDirectory, "blobs");

    /// <summary>
    /// Name of the settings file. Named after the application rather than the generic <c>settings.json</c>,
    /// because it does not always sit in a folder that belongs only to PasteJump - with the settings stored
    /// in the user profile it shares a tree with other software, and a file called <c>settings.json</c> there
    /// says nothing about whose settings it holds.
    /// </summary>
    public const string SettingsFileName = "PasteJump.json";

    /// <summary>The name used before the file was renamed after the application.</summary>
    private const string LegacySettingsFileName = "settings.json";

    public string SettingsFile => Path.Combine(SettingsDirectory, SettingsFileName);

    // There is deliberately no LogDirectory. The app has no logger, and the property existed only to have
    // EnsureCreated make an empty data\logs folder on every start. Reintroduce both together or neither.

    /// <summary>
    /// Assets shipped with the executable, such as the notification-area icons.
    /// <para>
    /// The only place in the app that may fall back to <see cref="AppContext.BaseDirectory"/>, and the
    /// exception is deliberate. Everywhere else that property is a trap, because under a single-file publish
    /// it returns the *extraction* directory rather than the folder holding the exe - which is exactly wrong
    /// for the user's data, since it would move the clip database into a temp folder and break portability.
    /// </para>
    /// <para>
    /// For assets it is exactly right, because the extraction directory is where the bundled
    /// <c>Assets</c> folder actually is. Written as a probe rather than a check on whether we are running
    /// single-file: it simply uses whichever location has the folder, so it needs no knowledge of how the
    /// app was published and degrades to the executable's own icon if neither does.
    /// </para>
    /// </summary>
    public string AssetsDirectory
    {
        get
        {
            var besideExecutable = Path.Combine(ApplicationDirectory, "Assets");

            return Directory.Exists(besideExecutable)
                ? besideExecutable
                : Path.Combine(AppContext.BaseDirectory, "Assets");
        }
    }

    /// <summary>Directory holding the executable. Also where the data-location pointer file lives.</summary>
    public static string ApplicationDirectory
    {
        get
        {
            var exePath = Environment.ProcessPath;

            return string.IsNullOrEmpty(exePath)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        }
    }

    /// <summary><c>%LOCALAPPDATA%\PasteJump</c>. See <see cref="DataLocation.UserProfile"/>.</summary>
    public static string UserProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PasteJump");

    /// <summary>
    /// Root directory for a location, without reading or writing anything.
    /// <para>
    /// <paramref name="customPath"/> is required for <see cref="DataLocation.CustomFolder"/> and ignored
    /// otherwise. A custom location with nothing usable in it falls back to the application folder rather than
    /// throwing: this is called during start-up before there is a window to report anything in, and the
    /// recoverable outcome is running from the default, not failing to start.
    /// </para>
    /// </summary>
    public static string RootFor(DataLocation location, string? customPath = null) => location switch
    {
        DataLocation.UserProfile => UserProfileDirectory,

        // One canonicalisation for the whole application - see CustomDataFolder.TryCanonicalise for why the
        // trailing separator matters. Two implementations of this would eventually disagree about whether
        // D:\Clips and D:\Clips\ are the same folder, and that decides whether a database gets copied.
        DataLocation.CustomFolder when CustomDataFolder.TryCanonicalise(customPath, out var full) => full,

        _ => ApplicationDirectory,
    };

    /// <summary>
    /// The layout the app actually runs with: resolved from the pointer file beside the executable,
    /// defaulting to the portable layout when there is no pointer.
    /// </summary>
    public static AppPaths Resolve()
    {
        var pointer = DataLocationPointer.Read(ApplicationDirectory);

        return new AppPaths(
            RootFor(pointer.Clips, pointer.ClipsPath),
            RootFor(pointer.Settings, pointer.SettingsPath),
            pointer.Clips,
            pointer.Settings);
    }

    /// <summary>Standard portable layout: everything next to the executable, ignoring any pointer.</summary>
    public static AppPaths Portable()
        => new(
            ApplicationDirectory,
            ApplicationDirectory,
            DataLocation.ApplicationFolder,
            DataLocation.ApplicationFolder);

    /// <summary>
    /// One explicit root for both halves, for tests and for the importer's dry-run mode.
    /// </summary>
    public static AppPaths At(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var full = Path.GetFullPath(rootDirectory);

        return new AppPaths(full, full, DataLocation.ApplicationFolder, DataLocation.ApplicationFolder);
    }

    /// <summary>Separate roots, for tests that exercise the two halves living apart.</summary>
    public static AppPaths At(string clipsRoot, string settingsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsRoot);

        return new AppPaths(
            Path.GetFullPath(clipsRoot),
            Path.GetFullPath(settingsRoot),
            DataLocation.ApplicationFolder,
            DataLocation.ApplicationFolder);
    }

    /// <summary>
    /// Whether both data directories can actually be created and written to. Reports each half
    /// separately, because they can now be in different places and only one of them may be the problem.
    /// <para>
    /// Worth checking rather than discovering it from a failed database open. Unzipping the portable
    /// folder under <c>C:\Program Files</c> is an obvious thing to do and leaves the app unable to write
    /// beside its own executable - which without this check surfaces as an opaque SQLite error at
    /// startup rather than as advice to switch the location.
    /// </para>
    /// </summary>
    public (bool Clips, bool Settings) CheckWritable()
        => (IsWritable(ClipsDirectory), IsWritable(SettingsDirectory));

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // A real create-and-delete, because directory ACLs are not the only thing that can refuse:
            // read-only media and some redirected folders both accept CreateDirectory and then reject
            // the write.
            var probe = Path.Combine(directory, $".write-probe-{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ClipsDirectory);
        Directory.CreateDirectory(BlobsDirectory);

        // Separate call rather than folded in above: when the two halves are in the same place this is a
        // no-op, and when they are not it is the only thing that creates the settings side.
        Directory.CreateDirectory(SettingsDirectory);
    }

    /// <summary>
    /// Renames a <c>settings.json</c> left by an earlier version, and reports whether it did.
    /// <para>
    /// Without this, renaming the file would silently reset every setting to its default - the old file still
    /// on disk under a name nothing looks for. Cheap insurance against exactly the failure
    /// <see cref="TryMigrateLegacyDatabase"/> exists to prevent, and it moves rather than copies so the user
    /// is not left wondering which of two files is live.
    /// </para>
    /// <para>
    /// Only ever moves into an unoccupied name. If both exist the current one wins and the old file is left
    /// alone, because overwriting live settings to tidy up a filename would be worse than a stray file.
    /// </para>
    /// </summary>
    public bool TryMigrateLegacySettings()
    {
        var legacy = Path.Combine(SettingsDirectory, LegacySettingsFileName);

        if (!File.Exists(legacy) || File.Exists(SettingsFile))
        {
            return false;
        }

        try
        {
            File.Move(legacy, SettingsFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting with defaults is recoverable; refusing to start is not. Reported rather than thrown.
            return false;
        }
    }

    /// <summary>Database name used before the app was renamed from Clipjog to PasteJump.</summary>
    private const string LegacyDatabaseName = "clipjog.db";

    /// <summary>
    /// Renames a database left behind by the pre-rename build, and reports whether it did.
    /// <para>
    /// The rename changed <see cref="DatabaseFile"/>, which would otherwise make an existing install
    /// start against an empty database and appear to have lost every clip - the data still on disk but
    /// under a name nothing looks for. Silent data loss is a bad failure even in a pre-release app, and
    /// this is cheap insurance.
    /// </para>
    /// <para>
    /// The <c>-wal</c> and <c>-shm</c> sidecars move too. SQLite derives their names from the database
    /// filename, so leaving them behind would discard whatever the write-ahead log had not yet
    /// checkpointed; renaming all three together keeps the set consistent.
    /// </para>
    /// <para>
    /// Only ever moves <em>into</em> an unoccupied name. If both exist, the current one wins and the
    /// legacy file is left untouched for the user to deal with - overwriting real data to tidy up a
    /// filename would be far worse than leaving a stray file.
    /// </para>
    /// </summary>
    public bool TryMigrateLegacyDatabase()
    {
        var legacy = Path.Combine(ClipsDirectory, LegacyDatabaseName);

        if (!File.Exists(legacy) || File.Exists(DatabaseFile))
        {
            return false;
        }

        try
        {
            File.Move(legacy, DatabaseFile);

            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var from = legacy + suffix;
                var to = DatabaseFile + suffix;

                if (File.Exists(from) && !File.Exists(to))
                {
                    File.Move(from, to);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file means another copy is running, which is already handled by the
            // single-instance mutex. Starting with an empty store is recoverable; refusing to start is
            // not, so this is reported rather than thrown.
            return false;
        }
    }
}
