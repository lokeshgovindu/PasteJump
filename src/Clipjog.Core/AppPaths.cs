namespace Clipjog.Core;

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

    public string DatabaseFile => Path.Combine(DataDirectory, "clipjog.db");

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
}
