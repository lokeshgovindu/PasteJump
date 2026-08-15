namespace PasteJump.App.Services;

/// <summary>
/// The menu glyphs, as codepoints in the icon font Windows already ships.
/// </summary>
/// <remarks>
/// <para>
/// A font rather than image assets: nothing to draw, nothing to keep at nine sizes, it inherits the item's
/// <c>Foreground</c> so it themes for free and greys out with a disabled row, and it is crisp at any DPI.
/// <c>Segoe MDL2 Assets</c> has shipped since Windows 10, which is this application's floor;
/// <c>Segoe Fluent Icons</c> is the Windows 11 successor and shares these codepoints, so it is named first.
/// </para>
/// <para>
/// Every one of these was chosen by <b>rendering the candidates and looking at them</b>, four per item, rather
/// than by reading a codepoint table. That is not diligence for its own sake: a wrong codepoint is not a compile
/// error, it is a box or something faintly absurd, and only a picture shows it. Two were changed as a result -
/// <c>E768</c> is a play triangle, so it is Resume rather than Pause, and <c>E733</c> is a clean prohibition sign
/// where <c>E71A</c> turned out to be a plain rounded square.
/// </para>
/// <para>
/// Help was <c>E897</c>, a bare question mark, and it changed after the menu was rendered beside itself: every other
/// glyph of its sort here is enclosed - About's <c>i</c>, Disable's prohibition sign, Exit's cross - so the loose
/// <c>?</c> read as lighter and smaller than its neighbours. <c>E9CE</c> is the same circle as About's, and was
/// confirmed to exist in <c>Segoe MDL2 Assets</c> as well as Fluent, which is what the Windows 10 floor needs.
/// </para>
/// </remarks>
internal static class TrayGlyph
{
    public const string About = "";        // Info: an i in a circle
    public const string History = "";      // History: a clock with a return arrow
    public const string Pause = "";        // two bars
    public const string Resume = "";       // a play triangle - the same item, the other way round
    public const string Settings = "";     // a gear
    public const string Manual = "";       // a question mark in a circle, matching About's i
    public const string Keys = "";         // a keyboard
    public const string Updates = "";      // Sync: two arrows chasing each other
    public const string Disable = "";      // a prohibition sign
    public const string Restart = "";      // one circular arrow, distinct from Sync's two
    public const string Exit = "";         // an X in a circle
}

/// <summary>One entry in the tray menu.</summary>
/// <param name="Text">The label. An <c>_</c> marks the access key, as WPF spells it.</param>
/// <param name="Invoke">What choosing it does. Null makes a separator.</param>
/// <param name="Glyph">A codepoint from <see cref="TrayGlyph"/>, or null for none.</param>
/// <param name="Gesture">Shortcut text, shown right-aligned and muted. None of today's items has one.</param>
/// <param name="IsChecked">Draws a tick in place of the glyph. State beats decoration.</param>
/// <param name="IsEnabled">A greyed, unclickable row.</param>
/// <param name="Emphasised">Semi-bold. About, plus whichever toggle offers to undo an off state.</param>
/// <param name="Submenu">Nested items. A header with a submenu is not itself clickable.</param>
internal sealed record TrayMenuItem(
    string Text,
    Action? Invoke = null,
    string? Glyph = null,
    string? Gesture = null,
    bool IsChecked = false,
    bool IsEnabled = true,
    bool Emphasised = false,
    IReadOnlyList<TrayMenuItem>? Submenu = null)
{
    /// <summary>A horizontal rule.</summary>
    public static TrayMenuItem Separator { get; } = new(string.Empty);

    public bool IsSeparator => Text.Length == 0 && Submenu is null;
}

/// <summary>What each tray menu item does.</summary>
/// <remarks>
/// Named members rather than the ten positional <c>Action</c> parameters this replaced. That signature -
/// <c>Build(onAbout, onHistory, onSettings, onManual, onHelp, onCheckForUpdates, onPauseToggle, onDisableToggle,
/// onRestart, onExit, isPaused, isDisabled)</c> - could be mis-ordered silently, since every parameter had the
/// same type, and it forced a static field holding the current set because the reused menu outlived the call that
/// built it. Both problems go away when an item carries its own action.
/// </remarks>
internal sealed record TrayCommands(
    Action About,
    Action History,
    Action Settings,
    Action Manual,
    Action Keys,
    Action CheckForUpdates,
    Action PauseToggle,
    Action DisableToggle,
    Action Restart,
    Action Exit);

/// <summary>
/// What is in the tray menu, in order. Deliberately separate from the WPF that draws it
/// (<see cref="TrayMenuBuilder"/>) so the shape of the menu can be asserted without opening a popup - which is
/// what the UI smoke harness does.
/// </summary>
internal static class TrayMenu
{
    /// <param name="isPaused">Capture is paused, so the item offers to resume.</param>
    /// <param name="isDisabled">The hook is released, so the item offers to enable.</param>
    public static IReadOnlyList<TrayMenuItem> Items(TrayCommands commands, bool isPaused, bool isDisabled)
    {
        ArgumentNullException.ThrowIfNull(commands);

        return
        [
            // About first and semi-bold, as requested. Note that bold in a context menu conventionally marks the
            // DEFAULT item - the one a double-click invokes - and that is still "Clipboard history", which is what
            // a left-click on the tray icon opens. The emphasis here is presentational only; the tray's own
            // activation behaviour is unchanged.
            new("_About PasteJump…", commands.About, TrayGlyph.About, Emphasised: true),
            TrayMenuItem.Separator,
            new("Clipboard _History…", commands.History, TrayGlyph.History),
            TrayMenuItem.Separator,

            // Both labels name their effect on Ctrl+V, because that is the sole difference between this and
            // Disable below, and "Pause monitoring" beside "Disable PasteJump" was reported as two names for one
            // thing. Title case for the command, sentence case inside the parentheses: "(Keep Pasting)" reads as a
            // second command rather than as the explanation it is.
            //
            // Bold while paused, and the same for Disable below. Until this the only sign of an off state was the
            // tray icon's hue - and the menu is opened by right-clicking that icon, so opening the menu covers up
            // the one thing that said so. Bold marks the row that puts the application back to normal, which is
            // both the state indicator and the way out of it, so it is the row worth finding fastest.
            isPaused
                ? new TrayMenuItem("_Resume Capture", commands.PauseToggle, TrayGlyph.Resume, Emphasised: true)
                : new TrayMenuItem("_Pause Capture (keep pasting)", commands.PauseToggle, TrayGlyph.Pause),

            new("_Settings…", commands.Settings, TrayGlyph.Settings),

            // The manual had no route into it from anywhere in the application until this item existed - it
            // shipped in the download and could only be found in Explorer. It sits above the keys card because it
            // is the general answer and the card is the specific one, which is also the order the two appear in
            // F1's own window.
            //
            // Access key is L, not H: "Clipboard _History" already owns H, and History is the item people reach
            // for by keyboard. A duplicate access key does not fail, it just makes the first press select rather
            // than invoke - which reads as the menu ignoring you.
            new("He_lp…", commands.Manual, TrayGlyph.Manual),
            new("Paste-Mode _Keys…", commands.Keys, TrayGlyph.Keys),

            // No "clear clips" item here, deliberately. One was added and then removed: the gesture's X cycle
            // already reaches DELETE ALL and now confirms before acting, which was the real problem with it, and
            // the Paste Mode tab names the keys. A destructive item one click away in the tray earned nothing
            // beyond a mouse-only route, in an application whose whole premise is the keyboard.
            TrayMenuItem.Separator,

            // Fourth from the bottom, in the group with Restart and Exit rather than beside About. It belongs
            // here: what an update leads to is replacing the program and restarting it, and the ellipsis says it
            // opens a dialog rather than acting silently. It only ever runs when clicked - see UpdateChecker for
            // why nothing checks at start-up.
            new("Check for _Updates…", commands.CheckForUpdates, TrayGlyph.Updates),

            // Distinct from Pause above, and the difference is worth the two menu items. Pause stops capturing but
            // keeps the gesture, so Ctrl+V still opens the overlay on the clips already held. Disable also
            // releases the keyboard hook, handing Ctrl+V back to Windows untouched - which is what you want in
            // order to use another clipboard manager, or to rule PasteJump out when something else misbehaves.
            //
            // One glyph for both directions: the prohibition sign marks the subject - interception - while the
            // label carries the direction. Pause/Resume above earns two glyphs because play and pause are a
            // universally understood pair; there is no such pair for this.
            //
            // Bold while disabled, for the reason given on the pause toggle above. Note that both can be bold at
            // once - disabling also stops capture, so a paused-then-disabled PasteJump genuinely has two things to
            // switch back on and the menu should not hide one of them behind a precedence rule. That is the one
            // place this departs from ApplyTrayIcon and BuildTrayTooltip, which must pick a single answer.
            isDisabled
                ? new TrayMenuItem("_Enable PasteJump", commands.DisableToggle, TrayGlyph.Disable, Emphasised: true)
                : new TrayMenuItem("_Disable PasteJump (Ctrl+V passes through)", commands.DisableToggle, TrayGlyph.Disable),

            // Restart sits immediately above Exit. Both are the same kind of end-of-session action, and grouping
            // them leaves Exit at the very bottom where muscle memory expects it - appending Restart last would
            // have moved Exit and caused mis-clicks.
            new("_Restart PasteJump", commands.Restart, TrayGlyph.Restart),
            new("E_xit PasteJump", commands.Exit, TrayGlyph.Exit),
        ];
    }
}
