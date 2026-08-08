namespace PasteJump.Core;

/// <summary>Outcome of a move. <see cref="Adopted"/> is false when there was nothing to do.</summary>
/// <param name="Adopted">True when at least one file was copied.</param>
/// <param name="FilesCopied">Files successfully copied.</param>
/// <param name="Error">Populated when the copy was abandoned part-way.</param>
public readonly record struct DataMigrationReport(bool Adopted, int FilesCopied, string? Error)
{
    public static DataMigrationReport NothingToDo => new(false, 0, null);
}

/// <summary>
/// Moves clips and settings between locations when the user changes either setting.
/// <para>
/// Runs at startup, before the store is opened, because the database cannot be copied while it is open.
/// </para>
/// <para>
/// The source is never deleted. A clipboard history is the one thing in this app that cannot be
/// regenerated, and a half-finished copy followed by a delete is how a tidy-up turns into data loss - so
/// the old folder is left behind for the user to remove once they are satisfied.
/// </para>
/// </summary>
public static class DataMigrator
{
    /// <summary>
    /// Files belonging to the settings half. Everything else in <c>data</c> is clips.
    /// <para>
    /// The superseded <c>settings.json</c> is listed too, so a move performed before the rename has caught up
    /// carries the old file across rather than stranding it - the rename then happens at the destination on
    /// the next start.
    /// </para>
    /// </summary>
    private static readonly string[] SettingsFileNames = [AppPaths.SettingsFileName, "settings.json"];

    private static bool IsSettingsFile(string relativePath) => SettingsFileNames
        .Any(name => relativePath.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Copies the database, blobs and logs from <paramref name="fromRoot"/> to <paramref name="toRoot"/>.
    /// <para>
    /// Declines rather than merges when the destination already holds a database. Two histories cannot be
    /// combined by copying files over each other - the blobs are addressed by content but the database
    /// rows are not - and overwriting the destination would discard whichever history the user had been
    /// using most recently.
    /// </para>
    /// </summary>
    public static DataMigrationReport AdoptClips(string fromRoot, string toRoot)
    {
        var source = Resolve(fromRoot, toRoot);

        if (source is not var (from, to))
        {
            return DataMigrationReport.NothingToDo;
        }

        if (File.Exists(to.DatabaseFile))
        {
            return DataMigrationReport.NothingToDo;
        }

        return Copy(from.ClipsDirectory, to.ClipsDirectory, include: static name => !IsSettingsFile(name));
    }

    /// <summary>
    /// Copies <c>settings.json</c> from <paramref name="fromRoot"/> to <paramref name="toRoot"/>.
    /// <para>
    /// Declines when the destination already has one, on the same reasoning as the clips: a settings file
    /// already at the destination was put there deliberately.
    /// </para>
    /// </summary>
    public static DataMigrationReport AdoptSettings(string fromRoot, string toRoot)
    {
        var source = Resolve(fromRoot, toRoot);

        if (source is not var (from, to))
        {
            return DataMigrationReport.NothingToDo;
        }

        if (File.Exists(to.SettingsFile))
        {
            return DataMigrationReport.NothingToDo;
        }

        return Copy(from.SettingsDirectory, to.SettingsDirectory, include: static name => IsSettingsFile(name));
    }

    /// <summary>
    /// Null when the two roots are the same or the source does not exist, which is the "nothing to do"
    /// case for both halves.
    /// </summary>
    private static (AppPaths From, AppPaths To)? Resolve(string fromRoot, string toRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(toRoot);

        var from = AppPaths.At(fromRoot);
        var to = AppPaths.At(toRoot);

        if (string.Equals(from.ClipsRoot, to.ClipsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Directory.Exists(from.ClipsDirectory) ? (from, to) : null;
    }

    private static DataMigrationReport Copy(
        string sourceDirectory,
        string destinationDirectory,
        Func<string, bool> include)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return DataMigrationReport.NothingToDo;
        }

        var copied = 0;

        try
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDirectory, file);

                // Matched on the leaf name, so settings.json is recognised wherever it sits while a blob
                // that happens to share the name inside blobs\ is not.
                if (!include(relative))
                {
                    continue;
                }

                // Leftover temp files from an interrupted settings save or a previous migration. Copying
                // them would resurrect rubbish at the new location.
                if (relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = Path.Combine(destinationDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // overwrite: false - the caller already established the destination was unoccupied, so
                // anything here was put there deliberately and is not ours to replace.
                File.Copy(file, target, overwrite: false);
                copied++;
            }

            return new DataMigrationReport(copied > 0, copied, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported, not thrown. Whatever was copied is still at the destination and the source is
            // untouched, so the user can retry or switch the location back.
            return new DataMigrationReport(copied > 0, copied, ex.Message);
        }
    }
}
