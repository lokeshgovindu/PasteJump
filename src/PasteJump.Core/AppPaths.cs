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
    private AppPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
    }

    /// <summary>Directory containing the executable. Data lives beneath it, keeping the app portable.</summary>
    public string RootDirectory { get; }

    public string DataDirectory => Path.Combine(RootDirectory, "data");

    public string DatabaseFile => Path.Combine(DataDirectory, "pastejump.db");

    public string BlobsDirectory => Path.Combine(DataDirectory, "blobs");

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Assets shipped alongside the executable, such as the two notification-area icons.</summary>
    public string AssetsDirectory => Path.Combine(RootDirectory, "Assets");

    /// <summary>Standard portable layout: data sits next to the executable.</summary>
    public static AppPaths Portable()
    {
        var exePath = Environment.ProcessPath;
        var root = string.IsNullOrEmpty(exePath)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;

        return new AppPaths(root);
    }

    /// <summary>Explicit root, for tests and for the importer's dry-run mode.</summary>
    public static AppPaths At(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return new AppPaths(Path.GetFullPath(rootDirectory));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BlobsDirectory);
        Directory.CreateDirectory(LogDirectory);
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
        var legacy = Path.Combine(DataDirectory, LegacyDatabaseName);

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
