using System.Globalization;
using System.Text;
using PasteJump.Core.Settings;

namespace PasteJump.Core.Paste;

/// <summary>
/// Per-application overrides for the pause between writing a clip to the clipboard and sending the paste
/// keystroke.
/// <para>
/// This exists because the delay is a property of the <em>target application</em>, not of PasteJump. Office,
/// Electron shells and remote-desktop clients cache the clipboard and can serve a keystroke that arrives too early
/// from that stale cache - which is why the help tells you to raise the delay when one particular application
/// pastes the previous clip. Until now the only way to do that was to raise it globally, so fixing Word slowed
/// every paste in every other program for ever.
/// </para>
/// <para>
/// Stored as <c>name=ms</c> pairs separated by semicolons - <c>winword.exe=80;ms-teams.exe=100</c> - for the same
/// reasons <c>PasteModeKeys</c> is a string: the Advanced tab compares values to decide whether a row differs from
/// its default, which a dictionary does not do usefully, and it has to render as one readable line.
/// </para>
/// </summary>
public sealed class PerAppSettleDelays
{
    private readonly Dictionary<string, int> _byProcess = new(StringComparer.OrdinalIgnoreCase);

    private PerAppSettleDelays()
    {
    }

    /// <summary>No overrides, which is what a fresh install has.</summary>
    public static PerAppSettleDelays Empty { get; } = new();

    /// <summary>The overrides, in the order the settings dialog should list them.</summary>
    public IReadOnlyList<(string Process, int Milliseconds)> Entries =>
        [.. _byProcess
            .Select(static pair => (pair.Key, pair.Value))
            .OrderBy(static entry => entry.Key, StringComparer.CurrentCultureIgnoreCase)];

    public int Count => _byProcess.Count;

    /// <summary>
    /// The delay to use for a process, or <paramref name="fallback"/> when it has no override.
    /// <para>
    /// Matched on the executable's file name, case-insensitively - the same key <c>ExcludedApps</c> uses, so a
    /// name typed into one is recognised by the other. A null process name (the foreground window could not be
    /// identified, which happens on a secure desktop) takes the fallback rather than guessing.
    /// </para>
    /// </summary>
    public int For(string? processName, int fallback)
    {
        var normalised = ExcludedApps.Normalise(processName);

        return normalised is not null && _byProcess.TryGetValue(normalised, out var milliseconds)
            ? milliseconds
            : fallback;
    }

    /// <summary>
    /// Reads the settings string. Tolerant, like <c>PasteKeyMap.Parse</c> and for the same reason: this runs during
    /// start-up before there is a window to report in, so an unparseable entry is dropped rather than refused. The
    /// dialog is where a bad entry is rejected, against what the user typed.
    /// </summary>
    public static PerAppSettleDelays Parse(string? stored)
    {
        var delays = new PerAppSettleDelays();

        if (string.IsNullOrWhiteSpace(stored))
        {
            return delays;
        }

        foreach (var pair in stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = pair.LastIndexOf('=');

            if (split <= 0)
            {
                continue;
            }

            var name = ExcludedApps.Normalise(pair[..split]);

            if (name is null)
            {
                continue;
            }

            if (!int.TryParse(pair[(split + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            {
                continue;
            }

            // Clamped rather than dropped: a hand-edited 5000 plainly means "as long as possible", and honouring
            // the ceiling is closer to that intent than ignoring the line.
            delays._byProcess[name] = Math.Clamp(
                ms,
                SettingsBounds.PasteSettleDelayMs.Min,
                SettingsBounds.PasteSettleDelayMs.Max);
        }

        return delays;
    }

    /// <summary>Builds from explicit pairs, for the settings dialog.</summary>
    public static PerAppSettleDelays FromEntries(IEnumerable<(string Process, int Milliseconds)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var delays = new PerAppSettleDelays();

        foreach (var (process, milliseconds) in entries)
        {
            if (ExcludedApps.Normalise(process) is { } name)
            {
                delays._byProcess[name] = Math.Clamp(
                    milliseconds,
                    SettingsBounds.PasteSettleDelayMs.Min,
                    SettingsBounds.PasteSettleDelayMs.Max);
            }
        }

        return delays;
    }

    /// <summary>
    /// Why a set of entries cannot be used, or null when it can. The duplicate check is the one that matters: two
    /// rows for one program is not a preference half of which could be honoured.
    /// </summary>
    public static string? Validate(IEnumerable<(string Process, int Milliseconds)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (process, milliseconds) in entries)
        {
            var name = ExcludedApps.Normalise(process);

            if (name is null)
            {
                return "Every row needs a program name, such as WINWORD.EXE.";
            }

            if (!seen.Add(name))
            {
                return $"{name} is listed twice. One delay per program.";
            }

            if (!SettingsBounds.PasteSettleDelayMs.Admits(milliseconds))
            {
                return SettingsBounds.PasteSettleDelayMs.Refuse($"The delay for {name}", "milliseconds");
            }
        }

        return null;
    }

    public string ToSettingsString()
    {
        var builder = new StringBuilder();

        foreach (var (process, milliseconds) in Entries)
        {
            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            builder.Append(process).Append('=').Append(milliseconds.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
