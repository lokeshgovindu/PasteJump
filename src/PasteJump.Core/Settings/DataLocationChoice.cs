namespace PasteJump.Core.Settings;

/// <summary>
/// Where one half of the data should live: the choice, plus the folder when the choice needs one.
/// <para>
/// The two travel together because neither is sufficient alone - <see cref="DataLocation.CustomFolder"/> means
/// nothing without a path, and a path means nothing for the other two. Passing them as separate arguments is
/// how they come to disagree.
/// </para>
/// </summary>
/// <param name="Location">The kind of location.</param>
/// <param name="Path">The folder, when <paramref name="Location"/> is a custom one. Null otherwise.</param>
public readonly record struct DataLocationChoice(DataLocation Location, string? Path = null)
{
    /// <summary>The root directory this choice resolves to.</summary>
    public string Root => AppPaths.RootFor(Location, Path);

    /// <summary>Whether this resolves to the same directory as <paramref name="other"/>.</summary>
    public bool SameRootAs(string other)
        => string.Equals(Root, other, StringComparison.OrdinalIgnoreCase);
}
