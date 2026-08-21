using System.Globalization;

namespace PasteJump.Core;

/// <summary>
/// How PasteJump writes the moment a clip was copied, wherever it shows one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The system's own format, not one of ours.</b> Every user-facing timestamp used to be a hard-coded
/// <c>yyyy-MM-dd HH:mm</c>, which is nobody's Windows setting: on an English (India) machine the shell writes
/// <c>21-08-2026 3:12 pm</c>, and an application that insists on ISO in the middle of that reads as a
/// developer's debug output rather than as part of the desktop. Reported 2026-08-21, in the plainest possible
/// terms - "I am seeing the system format only".
/// </para>
/// <para>
/// The <c>g</c> specifier is exactly that pairing - <c>ShortDatePattern</c> then <c>ShortTimePattern</c> - and
/// on Windows .NET fills both from the user's regional settings <em>including their custom overrides</em>. So
/// somebody who has set a 24-hour clock or a <c>yyyy/MM/dd</c> order gets it, which is the whole point and is
/// not something a hand-written pattern can do.
/// </para>
/// <para>
/// In <c>Core</c>, and used by both the overlay and the history window, because the alternative is two
/// formats that drift: they showed the same clip's time two different ways within a day of each other before
/// this existed.
/// </para>
/// </remarks>
public static class LocalTimestamp
{
    /// <summary>
    /// The instant as the local machine would write it: local time, the system's short date and short time.
    /// </summary>
    public static string Format(DateTimeOffset instant, CultureInfo? culture = null) =>
        instant.ToLocalTime().ToString("g", culture ?? CultureInfo.CurrentCulture);

    /// <summary>
    /// The widest string <see cref="Format"/> can produce in this culture, for measuring a column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A column sized for "now" is a column that truncates later: <c>1/2/2026 1:02 am</c> is far shorter than
    /// <c>22-12-2026 11:58 pm</c>, and which one you happen to measure depends on the day you ran it. This
    /// formats a deliberately wide instant instead - a two-digit day and month, a two-digit 12-hour hour, and
    /// a post-noon time so the longer of the AM/PM designators is used where the culture has them.
    /// </para>
    /// <para>
    /// Constructed in local time, then handed back through <see cref="Format"/>, so the sample goes through
    /// exactly the same path as a real value rather than a second formatting rule that could disagree.
    /// </para>
    /// </remarks>
    public static string WidestSample(CultureInfo? culture = null)
    {
        var resolved = culture ?? CultureInfo.CurrentCulture;

        // 22 December, 23:58 local - wide in every field that varies, and unambiguous about which is the day.
        var wide = new DateTime(2026, 12, 22, 23, 58, 0, DateTimeKind.Local);

        var candidate = Format(new DateTimeOffset(wide), resolved);

        // The designator can be the longer half of the pair either side of noon, and cultures differ about
        // which - so the widest is whichever of the two actually measures longer as text.
        var morning = Format(new DateTimeOffset(wide.AddHours(-12)), resolved);

        return morning.Length > candidate.Length ? morning : candidate;
    }
}
