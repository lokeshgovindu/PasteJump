namespace PasteJump.Core.Settings;

/// <summary>
/// Where the paste overlay is put on screen.
/// </summary>
/// <remarks>
/// <para>
/// Exists because the right answer genuinely depends on the person. The overlay follows the text caret when
/// Windows will say where it is, which is what makes the gesture feel like it belongs to the line you are typing -
/// but most modern applications expose no caret at all, and there the second choice is a matter of taste rather
/// than of correctness. Some people want it where the pointer is; some want it in the middle of the window they
/// are pasting into; some want it pinned to one spot and never moving.
/// </para>
/// <para>
/// <see cref="Automatic"/> is the zero value on purpose, so a settings file written before this existed reads back
/// as the behaviour it already had rather than as whichever member happened to be declared first.
/// </para>
/// </remarks>
public enum OverlayPosition
{
    /// <summary>
    /// Beside the text caret when the application exposes one, otherwise centred on the window being pasted into.
    /// </summary>
    /// <remarks>
    /// The default, and the only option that cannot put the overlay somewhere useless: the caret is where you are
    /// looking, and the window being pasted into cannot be on the wrong monitor.
    /// </remarks>
    Automatic = 0,

    /// <summary>
    /// Beside the caret when there is one, otherwise at the mouse pointer.
    /// </summary>
    /// <remarks>
    /// What PasteJump did until 2026-08-19, kept because it is a reasonable preference for anyone who works with a
    /// hand on the mouse. It is not the default because the pointer is wherever it was last left - frequently in a
    /// toolbar at the top of the window, and on a multi-monitor desktop frequently on another screen, which is
    /// what "I cannot see the overlay" turned out to mean.
    /// </remarks>
    CaretOrMouse,

    /// <summary>Always at the mouse pointer, even when the application does expose a caret.</summary>
    MousePointer,

    /// <summary>Always centred on the window being pasted into, ignoring the caret.</summary>
    WindowCentre,

    /// <summary>
    /// At the fixed screen position given by <c>OverlayX</c> and <c>OverlayY</c>.
    /// </summary>
    /// <remarks>
    /// Degrades to <see cref="Automatic"/> when either coordinate is unset, rather than putting the overlay at
    /// (0,0): a half-configured fixed position is a mistake, and the corner of the primary monitor is not a useful
    /// guess at what was meant.
    /// </remarks>
    FixedPoint,
}
