namespace PasteJump.Interop;

/// <summary>
/// The optional tone on capture - Clipjump's <c>CopyBeep</c> and <c>beepFrequency</c>.
/// </summary>
public static class CopyBeep
{
    /// <summary>Matches the original's <c>BeepAt</c> default duration.</summary>
    private const int DurationMs = 150;

    /// <summary>
    /// Sounds a tone without blocking the caller.
    /// <para>
    /// The thread-pool hop is not optional. <see cref="Console.Beep(int, int)"/> is <em>synchronous</em> -
    /// it returns only once the tone has finished - and this is called from the capture path, which runs on
    /// the dispatcher. Calling it directly would freeze the UI for the duration of every copy, and the
    /// capture path is also reached from the keyboard hook, where 150 ms of dead time is halfway to the
    /// <c>LowLevelHooksTimeout</c> that makes Windows silently discard the hook.
    /// </para>
    /// </summary>
    public static void Play(int frequencyHz)
    {
        // Clamped to the range the Win32 Beep API accepts. Outside it the call simply fails, which would
        // turn a mistyped setting into a silently dead feature.
        var frequency = Math.Clamp(frequencyHz, 37, 32_767);

        _ = Task.Run(() =>
        {
            try
            {
                Console.Beep(frequency, DurationMs);
            }
            catch (Exception)
            {
                // No sound device, or a session with no audio endpoint. A missing beep is not worth
                // surfacing, and an unobserved task exception would otherwise be a crash on some hosts.
            }
        });
    }
}
