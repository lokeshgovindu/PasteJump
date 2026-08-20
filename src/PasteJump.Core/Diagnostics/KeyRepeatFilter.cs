namespace PasteJump.Core.Diagnostics;

/// <summary>
/// Collapses a held key's auto-repeat into one line for the gesture trace: the first press, then the release
/// carrying how many repeats arrived in between.
/// </summary>
/// <remarks>
/// <para>
/// Windows auto-repeats <c>WM_KEYDOWN</c> for a held key - modifiers included - so holding Ctrl while reading the
/// overlay wrote a line every ~30 ms, and each of those was two lines with the recognizer's verdict beneath it. A
/// second of thinking buried the events that matter. Reported as exactly that.
/// </para>
/// <para>
/// <b>This filters the LOG and nothing else.</b> The recognizer still receives every event, because the repeats
/// are not noise to it: a repeated trigger key steps to another clip, so dropping them would change what the
/// gesture does. That distinction is the whole reason this is a separate object rather than an early return in the
/// hook.
/// </para>
/// <para>
/// State is a 256-entry array rather than a dictionary, for the same reason <c>PasteKeyMap</c>'s lookup is: this
/// is read on the hook callback, once per keystroke machine-wide, where an allocation or a hash is work that
/// counts against <c>LowLevelHooksTimeout</c>.
/// </para>
/// </remarks>
public sealed class KeyRepeatFilter
{
    /// <summary>-1 means the key is up; anything else is the number of repeats seen while it has been down.</summary>
    private readonly int[] _repeatsWhileDown = new int[256];

    public KeyRepeatFilter() => Array.Fill(_repeatsWhileDown, -1);

    /// <summary>
    /// Whether this event deserves a line, and how many repeats it accounts for.
    /// </summary>
    /// <param name="virtualKey">The virtual key code. Anything outside 0-255 is always written, never tracked.</param>
    /// <param name="isKeyDown">True for a press, false for a release.</param>
    /// <param name="repeats">
    /// On a release, the repeats swallowed since the press. Zero everywhere else - a first press has nothing to
    /// report yet, and a suppressed repeat is not written at all.
    /// </param>
    public bool ShouldWrite(int virtualKey, bool isKeyDown, out int repeats)
    {
        repeats = 0;

        if (virtualKey is < 0 or > 255)
        {
            return true;
        }

        if (isKeyDown)
        {
            if (_repeatsWhileDown[virtualKey] >= 0)
            {
                // Already down, so this is auto-repeat. Counted and swallowed.
                _repeatsWhileDown[virtualKey]++;
                return false;
            }

            _repeatsWhileDown[virtualKey] = 0;
            return true;
        }

        // A release always gets a line, including one whose press was never seen - which happens whenever the key
        // went down before this filter existed, or while another hook was suppressing the press. Reporting zero
        // repeats is honest there: none were observed.
        repeats = Math.Max(0, _repeatsWhileDown[virtualKey]);
        _repeatsWhileDown[virtualKey] = -1;

        return true;
    }

    /// <summary>
    /// Forgets every key, so a run of repeats cannot be attributed across a gap in which we heard nothing.
    /// </summary>
    /// <remarks>
    /// Called when the hook is reinstalled. Windows discards a hook without telling us and every event in the gap
    /// goes with it, so a key held across one would otherwise have its next press read as auto-repeat and be
    /// swallowed - losing the one line that says the key was pressed again.
    /// </remarks>
    public void Reset() => Array.Fill(_repeatsWhileDown, -1);
}
