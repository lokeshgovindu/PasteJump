using System.Text;

namespace PasteJump.Core.PasteMode;

/// <summary>
/// Turns a stream of raw key transitions into paste-mode operations, and decides which keystrokes
/// to swallow.
/// <para>
/// Swallowing correctly is the delicate part. Every key we consume is a key the foreground app
/// never sees, so an over-eager rule here breaks typing system-wide. The rule is therefore
/// narrow: consume nothing unless a session is genuinely open, and never consume the modifiers
/// themselves.
/// </para>
/// </summary>
public sealed class PasteGestureRecognizer
{
    private readonly PasteModeController _controller;
    private readonly StringBuilder _searchBuffer = new();
    private readonly HashSet<GestureKey> _swallowedDownKeys = [];

    public PasteGestureRecognizer(PasteModeController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public bool IsControlDown { get; private set; }

    public bool IsSessionActive => _controller.IsActive;

    /// <summary>Raised after every state change, so the overlay can be repositioned or redrawn.</summary>
    public event Action<PasteCommitKind>? Committed;

    /// <summary>
    /// Handles a key transition. Returns true when the keystroke should be swallowed.
    /// </summary>
    public bool Handle(GestureKey key, bool isDown)
    {
        switch (key)
        {
            case GestureKey.Control:
                return HandleControl(isDown);

            case GestureKey.Shift:
                // Never swallowed: Shift has meaning to the app underneath, and we only observe it.
                _controller.ShiftHeld = isDown;
                return false;

            case GestureKey.None:
                return false;
        }

        if (!isDown)
        {
            // Keep up/down symmetric. Passing through a key-up whose key-down we swallowed leaves
            // the foreground app believing a key it never saw pressed has been released, which
            // some apps handle badly.
            return _swallowedDownKeys.Remove(key);
        }

        var swallowed = HandleKeyDown(key);

        if (swallowed)
        {
            _swallowedDownKeys.Add(key);
        }

        return swallowed;
    }

    /// <summary>
    /// Feeds a printable character to the search box. Only consumed while searching.
    /// <para>
    /// Search input arrives through the hook rather than through a focused text box on purpose.
    /// A focusable search window would have to take foreground away from the target app and then
    /// hand it back before pasting - which is what the original did, and it is a race. Keeping the
    /// overlay permanently non-activating means focus never moves at all.
    /// </para>
    /// </summary>
    public bool HandleCharacter(char character)
    {
        if (_controller.State != PasteSessionState.Searching)
        {
            return false;
        }

        if (char.IsControl(character))
        {
            return false;
        }

        _searchBuffer.Append(character);
        _controller.SetSearchQuery(_searchBuffer.ToString());
        return true;
    }

    /// <summary>
    /// Whether a keystroke that no paste-mode action claimed should nevertheless be swallowed, because a
    /// session is open.
    /// <para>
    /// It must be. While the overlay is up the user is holding Ctrl, and almost every <c>Ctrl</c>+key in every
    /// application is a command: <c>Ctrl+0</c> and <c>Ctrl+=</c> zoom VS Code, <c>Ctrl+W</c> closes a tab,
    /// <c>Ctrl+S</c> saves. Letting those through meant tapping around during a gesture quietly reformatted,
    /// zoomed or closed whatever was underneath - reported as the editor zooming while browsing clips.
    /// </para>
    /// <para>
    /// <paramref name="altHeld"/> and <paramref name="winHeld"/> are the escape hatch, and they are not
    /// decoration. Swallowing everything would mean <c>Alt+Tab</c> could not switch away while a session is
    /// open, and - if a session ever failed to close - that the keyboard appeared dead with no way out.
    /// Chords the shell owns are therefore always let through; losing focus aborts the session anyway.
    /// </para>
    /// <para>
    /// The caller supplies the facts (is a session open is ours; is this a modifier, is Alt down are the
    /// platform's) so that this decision itself stays here, where it can be tested.
    /// </para>
    /// </summary>
    public bool ShouldSwallowUnhandled(bool altHeld, bool winHeld)
        => _controller.IsActive && !altHeld && !winHeld;

    /// <summary>Cancels any in-flight session. For focus loss and shutdown.</summary>
    public void Reset()
    {
        _searchBuffer.Clear();
        _swallowedDownKeys.Clear();
        IsControlDown = false;

        if (_controller.IsActive)
        {
            _controller.Abort();
        }
    }

    private bool HandleControl(bool isDown)
    {
        IsControlDown = isDown;

        if (isDown)
        {
            return false;
        }

        if (_controller.State == PasteSessionState.Browsing)
        {
            var kind = _controller.ModifierReleased();
            _searchBuffer.Clear();
            _swallowedDownKeys.Clear();
            Committed?.Invoke(kind);
        }

        // Never swallow the modifier itself - the foreground app may be tracking it.
        return false;
    }

    private bool HandleKeyDown(GestureKey key)
    {
        var searching = _controller.State == PasteSessionState.Searching;

        // ---- entry
        if (key == GestureKey.Paste && IsControlDown && !_controller.IsActive)
        {
            // Ctrl+Shift+V is not ours. It is how every terminal pastes - Visual Studio's, VS Code's,
            // Windows Terminal's - and how browsers and editors paste as plain text. Starting a session here
            // would swallow the V, so the application never receives the chord it owns and gets our paste
            // instead of its own; and because Shift also means "pop", the clip was then deleted. Reported from
            // a Visual Studio terminal, where it looked simply like Ctrl+Shift+V had stopped working.
            //
            // Paste popping still exists: press Shift AFTER the gesture is open, which is what the key list
            // has always described. This only declines to claim the chord as an entry point.
            if (_controller.ShiftHeld)
            {
                return false;
            }

            var kind = _controller.Begin();

            if (kind == PasteCommitKind.PassedThrough)
            {
                // The host already synthesised a native paste, so the original keystroke has been
                // served and must not also reach the app.
                Committed?.Invoke(kind);
            }

            return true;
        }

        if (!_controller.IsActive)
        {
            return false;
        }

        // ---- search-specific keys
        if (searching)
        {
            switch (key)
            {
                case GestureKey.Backspace:
                    if (_searchBuffer.Length > 0)
                    {
                        _searchBuffer.Length--;
                        _controller.SetSearchQuery(_searchBuffer.ToString());
                    }

                    return true;

                case GestureKey.Commit:
                {
                    var kind = _controller.CommitExplicitly();
                    _searchBuffer.Clear();
                    Committed?.Invoke(kind);
                    return true;
                }

                case GestureKey.Escape:
                {
                    var kind = _controller.Abort();
                    _searchBuffer.Clear();
                    Committed?.Invoke(kind);
                    return true;
                }

                case GestureKey.ToggleSearch when IsControlDown:
                    _searchBuffer.Clear();
                    _controller.Handle(PasteAction.ToggleSearch);
                    return true;
            }

            // While searching, letters and digits are search text, not commands - except when
            // Ctrl is held, which is how the original let paste-mode keys stay reachable.
            if (!IsControlDown)
            {
                return false;
            }
        }

        // ---- digits jump
        var digit = key.DigitValue();

        if (digit > 0)
        {
            _controller.HandleDigit(digit);
            return true;
        }

        // ---- everything else
        var action = key.ToAction();

        if (action is null)
        {
            return false;
        }

        // Outside search mode the modifier must be held for these to mean anything; otherwise a
        // stale session would silently eat ordinary typing.
        if (!searching && !IsControlDown && action != PasteAction.Escape)
        {
            return false;
        }

        var result = _controller.Handle(action.Value);

        if (result is not PasteCommitKind.None)
        {
            if (!_controller.IsActive)
            {
                _searchBuffer.Clear();
            }

            Committed?.Invoke(result);
        }

        return true;
    }
}
