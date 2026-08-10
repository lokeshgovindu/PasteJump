namespace PasteJump.Core.Settings;

/// <summary>Where PasteJump keeps its database, blobs and settings.</summary>
public enum DataLocation
{
    /// <summary>
    /// A <c>data</c> folder beneath the directory holding the executable. The default, and what makes
    /// the install portable: copy the folder and the history goes with it.
    /// </summary>
    ApplicationFolder,

    /// <summary>
    /// <c>%LOCALAPPDATA%\PasteJump</c>. Always writable, unaffected by replacing the program folder, and
    /// shared by every copy of PasteJump on the machine - which is what makes a Debug build and a
    /// Release build see one history instead of two.
    /// <para>
    /// Local rather than Roaming deliberately. A clipboard history is machine-specific and grows without
    /// bound once images are stored; putting it in Roaming would push it through the roaming profile
    /// sync quota.
    /// </para>
    /// </summary>
    UserProfile,

    /// <summary>
    /// A folder the user names. The path itself lives in <c>data-location.json</c> alongside this choice,
    /// because an enum cannot carry it.
    /// <para>
    /// For the cases neither of the other two covers: a synced folder, a second drive, a network share, or
    /// simply somewhere with room for years of images. Note what the app does <em>not</em> do with it - the
    /// database is SQLite, so a path on a share that goes offline mid-session fails like any other
    /// disconnected file, and two machines pointed at one share will corrupt it. That is the user's call to
    /// make, and the settings dialog says so rather than refusing.
    /// </para>
    /// </summary>
    CustomFolder,
}
