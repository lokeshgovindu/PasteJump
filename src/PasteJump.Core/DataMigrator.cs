namespace PasteJump.Core;

/// <summary>Outcome of a data-directory move. <see cref="Adopted"/> is false when there was nothing to do.</summary>
/// <param name="Adopted">True when at least one file was copied.</param>
/// <param name="FilesCopied">Files successfully copied.</param>
/// <param name="Error">Populated when the copy was abandoned part-way.</param>
public readonly record struct DataMigrationReport(bool Adopted, int FilesCopied, string? Error)
{
    public static DataMigrationReport NothingToDo => new(false, 0, null);
}

/// <summary>
/// Moves the <c>data</c> folder between the two supported locations when the user changes the setting.
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
    /// Copies <paramref name="fromRoot"/>'s data folder into <paramref name="toRoot"/>.
    /// <para>
    /// Declines rather than merges when the destination already holds a database. Two histories cannot be
    /// combined by copying files over each other - the blobs are addressed by content but the database
    /// rows are not - and overwriting the destination would discard whichever history the user had been
    /// using most recently.
    /// </para>
    /// </summary>
    public static DataMigrationReport Adopt(string fromRoot, string toRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(toRoot);

        var source = AppPaths.At(fromRoot);
        var destination = AppPaths.At(toRoot);

        if (string.Equals(source.RootDirectory, destination.RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return DataMigrationReport.NothingToDo;
        }

        if (!Directory.Exists(source.DataDirectory) || File.Exists(destination.DatabaseFile))
        {
            return DataMigrationReport.NothingToDo;
        }

        var copied = 0;

        try
        {
            Directory.CreateDirectory(destination.DataDirectory);

            foreach (var file in Directory.EnumerateFiles(source.DataDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source.DataDirectory, file);
                var target = Path.Combine(destination.DataDirectory, relative);

                // Leftover temp files from an interrupted settings save or a previous migration. Copying
                // them would resurrect rubbish at the new location.
                if (target.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // overwrite: false - the destination had no database, so anything already there was put
                // there deliberately and is not ours to replace.
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
