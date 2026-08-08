namespace PasteJump.Core.PasteMode;

/// <summary>
/// Which letter, held with Ctrl, opens paste mode - Clipjump's <c>paste_k</c>.
/// <para>
/// Configurable for the same reason <c>PasteKeystroke</c> is, but for the other half of the problem. That
/// setting changes the chord we <em>send</em>, so another clipboard manager's hook cannot swallow our
/// paste. This one changes the chord we <em>listen for</em>, so the two applications stop competing for
/// Ctrl+V in the first place: whichever hook runs first no longer matters, because they are watching
/// different keys.
/// </para>
/// <para>
/// The letter does double duty. It opens the session and, once open, steps to an older clip - so it
/// cannot be a letter already bound to another paste-mode action, or that action would become
/// unreachable. <see cref="IsAvailable"/> is the rule, and it is enforced rather than resolved silently:
/// quietly dropping a binding the user cannot see is worse than refusing the choice.
/// </para>
/// </summary>
public static class TriggerKey
{
    /// <summary>The original's key, and ours.</summary>
    public const char Default = 'V';

    /// <summary>
    /// Letters bound to other paste-mode actions, which the trigger therefore cannot use. Mirrors the map
    /// in <c>VirtualKeyTranslator</c>; if a binding is added there, add it here too or the two disagree
    /// and the new action becomes silently stealable.
    /// </summary>
    private static readonly Dictionary<char, string> Reserved = new()
    {
        ['C'] = "step to a newer clip",
        ['X'] = "cycle what release does",
        ['A'] = "jump to the newest clip",
        ['Q'] = "move the clip to the front",
        ['F'] = "open search",
        ['Z'] = "cycle the paste format",
        ['T'] = "edit tags",
        ['S'] = "put the clip on the clipboard",
        ['H'] = "open the clip in an editor",
        ['E'] = "export the clip",
    };

    /// <summary>Letters the trigger may use, in alphabetical order.</summary>
    public static IReadOnlyList<char> Available { get; } =
        [.. Enumerable.Range('A', 26).Select(static c => (char)c).Where(IsAvailable)];

    /// <summary>True when <paramref name="key"/> is a letter not already bound to another action.</summary>
    public static bool IsAvailable(char key)
    {
        var upper = char.ToUpperInvariant(key);

        return upper is >= 'A' and <= 'Z' && !Reserved.ContainsKey(upper);
    }

    /// <summary>What <paramref name="key"/> is already used for, or null when it is free.</summary>
    public static string? ReservedFor(char key)
        => Reserved.TryGetValue(char.ToUpperInvariant(key), out var use) ? use : null;

    /// <summary>
    /// Coerces a stored value to a usable trigger letter, falling back to <see cref="Default"/>.
    /// Accepts a single letter, with or without surrounding whitespace and in either case.
    /// </summary>
    public static char Normalise(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return Default;
        }

        var trimmed = stored.Trim();

        if (trimmed.Length != 1)
        {
            return Default;
        }

        var upper = char.ToUpperInvariant(trimmed[0]);

        return IsAvailable(upper) ? upper : Default;
    }

    /// <summary>
    /// Virtual-key code for a trigger letter. A-Z virtual keys are the ASCII codes of the uppercase
    /// letters, which is why no lookup table is needed.
    /// </summary>
    public static int ToVirtualKey(char key) => char.ToUpperInvariant(key);

    /// <summary>The chord as a user would write it, e.g. <c>Ctrl+V</c>.</summary>
    public static string Describe(char key) => $"Ctrl+{char.ToUpperInvariant(key)}";
}
