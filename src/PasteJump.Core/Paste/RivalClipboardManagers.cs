namespace PasteJump.Core.Paste;

/// <summary>
/// Recognises other clipboard managers by process name, so the app can say why pasting has stopped
/// working instead of appearing to do nothing.
/// <para>
/// A pure function over a list of names, with the enumeration left to the caller. Reaching for
/// <c>Process.GetProcesses</c> in here would put a machine-wide query inside <c>Core</c> and make the
/// interesting part - which names count, and what the user is told - untestable.
/// </para>
/// </summary>
public static class RivalClipboardManagers
{
    /// <summary>
    /// Process names that indicate a manager likely to hold a suppressing hotkey on Ctrl+V, mapped to the
    /// name to show the user. Compared without the <c>.exe</c> extension and case-insensitively.
    /// <para>
    /// Kept deliberately short. A false positive tells the user to change a setting they did not need to
    /// change, which is worse than missing one - so this lists only managers whose default binding is
    /// known to be plain Ctrl+V. PowerToys is a specific omission: its Advanced Paste is Ctrl+Shift+V and
    /// does not collide.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // Both spellings ship: Clipjump.exe from the 32-bit build and Clipjump_x64.exe from the 64-bit
        // one. Matching only the former is how this check quietly fails on a 64-bit install.
        ["Clipjump"] = "Clipjump",
        ["Clipjump_x64"] = "Clipjump",
        ["Ditto"] = "Ditto",
        ["CopyQ"] = "CopyQ",
        ["ArsClip"] = "ArsClip",
        ["ClipboardFusion"] = "ClipboardFusion",
        ["Clipdiary"] = "Clipdiary",
    };

    /// <summary>
    /// Display names of recognised managers among <paramref name="runningProcessNames"/>, in a stable
    /// order and without duplicates.
    /// </summary>
    public static IReadOnlyList<string> Detect(IEnumerable<string?> runningProcessNames)
    {
        ArgumentNullException.ThrowIfNull(runningProcessNames);

        var found = new List<string>();

        foreach (var raw in runningProcessNames)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // Accepts "Clipjump", "Clipjump.exe" and a full path, because callers differ: Process.ProcessName
            // omits the extension while anything read from a command line or a window title may not.
            var name = Path.GetFileNameWithoutExtension(raw.Trim());

            if (Known.TryGetValue(name, out var display) && !found.Contains(display, StringComparer.Ordinal))
            {
                found.Add(display);
            }
        }

        return found;
    }

    /// <summary>
    /// The hint shown when a rival is detected. Built here rather than in the UI so the wording is covered by
    /// a test and cannot drift from the detection.
    /// <para>
    /// Deliberately conditional - "if pasting stops working" rather than "pasting does nothing". Detection is
    /// by process name, and that cannot tell whether the other manager's paste hotkey is actually enabled:
    /// Clipjump has its own disable toggle and stays running while switched off, so asserting that pasting is
    /// broken is wrong exactly as often as someone has it running but disabled. An earlier version of this
    /// text made that assertion and was reported as a false alarm.
    /// </para>
    /// </summary>
    public static string DescribeConflict(IReadOnlyList<string> rivals)
    {
        ArgumentNullException.ThrowIfNull(rivals);

        var names = rivals.Count switch
        {
            0 => "Another clipboard manager",
            1 => rivals[0],
            _ => string.Join(" and ", rivals),
        };

        var verb = rivals.Count > 1 ? "are" : "is";

        return
            $"{names} {verb} also running. If pasting stops working, that is why - two managers cannot " +
            "share Ctrl+V. Settings, Paste mode has the fix.";
    }
}
