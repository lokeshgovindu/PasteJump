namespace PasteJump.Core.PasteMode;

/// <summary>An application PasteJump appears to be unable to hear the keyboard in.</summary>
/// <param name="Application">Executable name, as the foreground reports it.</param>
/// <param name="FocusedFor">How long it has held the foreground, in total, with nothing heard.</param>
/// <param name="Spells">How many separate times it has held the foreground.</param>
/// <param name="Corroborated">
/// A copy was made in that application while not one keystroke was heard from it - so the user demonstrably
/// pressed keys there that never reached us. Far stronger evidence than silence alone.
/// </param>
public readonly record struct DeafApplication(string Application, TimeSpan FocusedFor, int Spells, bool Corroborated);

/// <summary>
/// Notices that the keyboard hook is receiving nothing while one particular application holds the foreground,
/// while working normally everywhere else.
/// </summary>
/// <remarks>
/// <para>
/// This exists because that failure is real, is not PasteJump's fault, and is <b>completely silent</b>. Measured
/// on a managed machine on 2026-08-21: Edge delivered 939 keystrokes to the hook one day and none at all the
/// next, while Terminal, Visual Studio, Teams and VS Code delivered thousands. A throwaway probe with no
/// relation to PasteJump then found <b>four</b> independent mechanisms equally dark while Edge held the
/// foreground - <c>WH_KEYBOARD_LL</c>, raw input with <c>RIDEV_INPUTSINK</c>, <c>RegisterHotKey</c>, and even
/// <c>GetLastInputInfo</c> - with <c>SendInput</c> reporting success for every event it injected. Nothing in user
/// mode can see the keyboard there, so there is nothing to fix and nothing to work around.
/// </para>
/// <para>
/// What can be fixed is the <em>appearance</em>. Until this existed the user pressed Ctrl+V, nothing happened,
/// and PasteJump said nothing - which reads as PasteJump being broken, and cost a morning of looking for a bug
/// in it. Naming what is happening is the whole feature.
/// </para>
/// <para>
/// <b>This is a guess, and the wording of the notice has to admit it</b> - the same rule
/// <c>RivalClipboardManagers</c> follows. Silence from an application is ambiguous: somebody may simply be
/// reading a page with the mouse, and no measurement available to us separates that from being deafened. So the
/// thresholds are generous, the notice is a passing toast rather than a dialog, and it is said once per
/// application per run.
/// </para>
/// <para>
/// <b>The test is RELATIVE, never absolute</b>, which is the lesson <see cref="HookHealthPolicy"/> paid for: a
/// fixed "no keys for N seconds" fires on a perfectly healthy hook the moment somebody stops typing. The
/// question is whether this application is silent <em>while others are not</em>.
/// </para>
/// </remarks>
public sealed class ForegroundDeafnessTracker
{
    /// <summary>Foreground time with nothing heard before silence alone is worth mentioning.</summary>
    /// <remarks>
    /// Two minutes, which is a long time to be typing into something and hearing nothing, and far longer than
    /// the idle glances that make up most short focus spells. Reached only when the corroborated route below has
    /// not already fired.
    /// </remarks>
    public const double DefaultMinFocusSeconds = 120;

    /// <summary>How many separate visits are needed, so one long unattended window cannot trigger it.</summary>
    public const int DefaultMinSpells = 4;

    /// <summary>
    /// How many <em>other</em> applications must have delivered keys first.
    /// </summary>
    /// <remarks>
    /// This is what makes the finding relative rather than absolute. With no working comparison the honest
    /// conclusion is "the hook is dead", which is <see cref="HookHealthPolicy"/>'s business and not this one's -
    /// and reporting one application as filtered when in fact nothing is being heard anywhere would send the user
    /// hunting for a policy that does not exist.
    /// </remarks>
    public const int DefaultMinOtherApps = 2;

    /// <summary>Foreground time needed once a copy has proved the user was typing there.</summary>
    /// <remarks>
    /// Much shorter, because the ambiguity is gone. A clipboard change sourced from an application whose
    /// keystrokes we have never seen means the user pressed Ctrl+C there and we missed it - and capture rides
    /// <c>WM_CLIPBOARDUPDATE</c>, which no hook can suppress, so that signal arrives even when every key is
    /// filtered. It is the one witness this failure cannot silence.
    /// </remarks>
    public const double CorroboratedMinFocusSeconds = 15;

    private sealed class Stats
    {
        public TimeSpan Focused;
        public int Spells;
        public bool HeardKeys;
        public bool CopiedWithoutBeingHeard;
        public bool Reported;
    }

    private readonly Dictionary<string, Stats> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly double _minFocusSeconds;
    private readonly int _minSpells;
    private readonly int _minOtherApps;
    private readonly double _corroboratedMinFocusSeconds;

    public ForegroundDeafnessTracker(
        double minFocusSeconds = DefaultMinFocusSeconds,
        int minSpells = DefaultMinSpells,
        int minOtherApps = DefaultMinOtherApps,
        double corroboratedMinFocusSeconds = CorroboratedMinFocusSeconds)
    {
        _minFocusSeconds = minFocusSeconds;
        _minSpells = minSpells;
        _minOtherApps = minOtherApps;
        _corroboratedMinFocusSeconds = corroboratedMinFocusSeconds;
    }

    /// <summary>How many applications have delivered at least one key. Diagnostics.</summary>
    public int ApplicationsHeard => _apps.Values.Count(static s => s.HeardKeys);

    /// <summary>
    /// Records that an application has just stopped holding the foreground, and what the hook heard while it did.
    /// </summary>
    /// <param name="application">Executable name, or null when the foreground could not be identified.</param>
    /// <param name="focusedFor">How long it held the foreground.</param>
    /// <param name="keysHeard">Key events the hook delivered during that spell.</param>
    public void NoteFocusSpell(string? application, TimeSpan focusedFor, int keysHeard)
    {
        if (string.IsNullOrEmpty(application))
        {
            return;
        }

        var stats = For(application);

        if (keysHeard > 0)
        {
            // One key is enough, for ever: this application is not the one being filtered. Kept rather than
            // decayed on purpose - a per-application fault does not come and go within a session, and forgetting
            // would let a quiet afternoon in a working application produce a false report.
            stats.HeardKeys = true;
            return;
        }

        // Only accumulated for applications that have never been heard from, so an ordinary application does not
        // creep towards the threshold during the hours nobody types into it.
        if (!stats.HeardKeys)
        {
            stats.Focused += focusedFor;
            stats.Spells++;
        }
    }

    /// <summary>
    /// Records that a copy was made while this application held the foreground.
    /// </summary>
    /// <remarks>
    /// Only meaningful for an application we have never heard a key from, and then it is close to conclusive:
    /// copying is overwhelmingly Ctrl+C. It is deliberately not treated as proof - a copy can be made from a
    /// context menu with the mouse - which is why it lowers the threshold rather than bypassing it.
    /// </remarks>
    public void NoteClipboardActivity(string? application)
    {
        if (string.IsNullOrEmpty(application))
        {
            return;
        }

        var stats = For(application);

        if (!stats.HeardKeys)
        {
            stats.CopiedWithoutBeingHeard = true;
        }
    }

    /// <summary>
    /// Returns an application worth reporting, once. Null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Claimed rather than merely queried: the notice must be said once per application per run, not once per
    /// watchdog tick. Under a sustained fault this is asked four times a second.
    /// </remarks>
    public DeafApplication? TryClaimNotice()
    {
        // Counted across everything except the candidate itself, below.
        var heard = ApplicationsHeard;

        if (heard < _minOtherApps)
        {
            return null;
        }

        foreach (var (name, stats) in _apps)
        {
            if (stats.HeardKeys || stats.Reported)
            {
                continue;
            }

            var minSeconds = stats.CopiedWithoutBeingHeard ? _corroboratedMinFocusSeconds : _minFocusSeconds;
            var minSpells = stats.CopiedWithoutBeingHeard ? 1 : _minSpells;

            if (stats.Focused.TotalSeconds < minSeconds || stats.Spells < minSpells)
            {
                continue;
            }

            stats.Reported = true;

            return new DeafApplication(name, stats.Focused, stats.Spells, stats.CopiedWithoutBeingHeard);
        }

        return null;
    }

    /// <summary>
    /// Wording for the notice. Here rather than in the window so it can be asserted, and so the hedge cannot be
    /// quietly dropped by whoever next edits the UI.
    /// </summary>
    /// <param name="app">What was observed.</param>
    /// <param name="runningElevated">
    /// Whether PasteJump is already running elevated, which decides whether there is a remedy to offer.
    /// <para>
    /// Elevation is the remedy, and it is measured rather than guessed: on the machine that produced this,
    /// an elevated hook received Alt+Tab pressed inside the affected application at the same moments a
    /// medium-integrity hook received nothing, and running PasteJump elevated restored the gesture there. The
    /// mechanism is UIPI - Windows excludes a lower-integrity hook from input whose effective owner outranks
    /// it, which is what an interceptor running above medium integrity produces.
    /// </para>
    /// <para>
    /// Suggesting it when already elevated would be worse than saying nothing: the one remedy has been tried,
    /// and repeating it reads as the application not knowing its own state.
    /// </para>
    /// </param>
    public static string Describe(DeafApplication app, bool runningElevated = false)
    {
        var howLong = app.FocusedFor.TotalSeconds < 90
            ? $"{app.FocusedFor.TotalSeconds:F0} seconds"
            : $"{app.FocusedFor.TotalMinutes:F0} minutes";

        var evidence = app.Corroborated
            ? $"A copy was made in {app.Application} without PasteJump seeing the keystroke, "
              + $"and it has held focus for {howLong} without one."
            : $"{app.Application} has held focus for {howLong} over {app.Spells} visits "
              + "without PasteJump receiving a single keystroke, while other applications are delivering them "
              + "normally.";

        // The remedy has to be one that needs no keystroke FROM PasteJump, because measurement says it cannot
        // deliver one there either: injected keys do not arrive in the affected application any more than real
        // ones are seen. The user's own Ctrl+V does work, so the route is Copy in the history window and paste
        // by hand. An earlier wording said "pasting from the history window works", which that window cannot do
        // - it has a Copy button, not a Paste one.
        var remedy = runningElevated

            // Already elevated and still deaf: the one thing that usually works has been done, so offer the
            // route that needs no keystroke from us at all rather than repeating advice already taken.
            ? " PasteJump is already running as administrator, so the remaining route is the clipboard history"
              + " window: press Copy there and paste with your own Ctrl+V, which still works."

            : " Running PasteJump as administrator usually restores it, because Windows hides this input from"
              + " programs with fewer privileges than whatever is intercepting it. Until then, open the"
              + " clipboard history window, press Copy, and paste with your own Ctrl+V, which still works.";

        return evidence
            + " If Ctrl+V does not open the overlay there, something on this machine is taking keyboard input"
            + " before PasteJump can see it - most often endpoint security policy. Your clips are safe."
            + remedy;
    }

    private Stats For(string application)
    {
        if (!_apps.TryGetValue(application, out var stats))
        {
            stats = new Stats();
            _apps[application] = stats;
        }

        return stats;
    }
}
