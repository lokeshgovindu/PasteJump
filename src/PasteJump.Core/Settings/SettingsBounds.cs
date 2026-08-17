namespace PasteJump.Core.Settings;

/// <summary>
/// The accepted range for every numeric setting, in one place.
/// <para>
/// These were written twice: once as a <c>Math.Clamp</c> in <see cref="PasteJumpSettings.Normalise"/> and again as
/// a hand-typed comparison and message in the settings dialog. Lowering the notification floor from 250 to 1
/// changed the clamp and left the dialog refusing anything under 250 - with a message quoting the old number, so
/// the setting looked deliberately restricted rather than out of step. Nothing warned; the two simply disagreed.
/// </para>
/// <para>
/// One definition now, with the message generated from it, so a bound cannot be changed in one place only.
/// </para>
/// </summary>
public readonly record struct SettingBound(int Min, int Max)
{
    public bool Admits(int value) => value >= Min && value <= Max;

    /// <summary>
    /// The refusal, phrased from the bound itself. <paramref name="what"/> is the sentence's subject, capitalised
    /// and without a trailing stop - "Notification duration".
    /// </summary>
    public string Refuse(string what, string unit = "")
        => $"{what} must be between {Min} and {Max}{(unit.Length == 0 ? string.Empty : " " + unit)}.";
}

/// <summary>Where every numeric range is defined. See <see cref="SettingBound"/> for why.</summary>
public static class SettingsBounds
{
    public static SettingBound MaxClips { get; } = new(1, 100_000);

    public static SettingBound CopyNotificationMs { get; } = new(1, 10_000);

    public static SettingBound PasteSettleDelayMs { get; } = new(0, 500);

    public static SettingBound BeepFrequencyHz { get; } = new(37, 32_767);

    public static SettingBound BeepDurationMs { get; } = new(20, 2_000);

    public static SettingBound PreviewMaxChars { get; } = new(256, 65_536);

    public static SettingBound HistoryLoadLimit { get; } = new(100, 1_000_000);

    public static SettingBound HistoryPreviewMaxWidth { get; } = new(120, 4_096);

    /// <summary>
    /// Nine is the smallest size the overlay's two-line rows stay legible at; twenty-four is where it stops being
    /// a strip beside your work. Both ends were looked at rather than picked.
    /// </summary>
    public static SettingBound OverlayFontSize { get; } = new(9, 24);

    public static SettingBound OverlayPreviewChars { get; } = new(40, 4_000);

    public static SettingBound OverlayPreviewMaxWidth { get; } = new(120, 1_400);

    /// <summary>
    /// How long DELETED stays on the overlay. Zero is legal and means "never show it", so the floor is 0 rather
    /// than a minimum that would be visible - and the ceiling is five seconds, past which a chip meant to reassure
    /// has become a chip that lingers into the next gesture.
    /// </summary>
    public static SettingBound OverlayDeletedFlashMs { get; } = new(0, 5_000);

    public static SettingBound OverlayPreviewMaxHeight { get; } = new(80, 900);
}
