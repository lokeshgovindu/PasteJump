using System.Collections.Concurrent;
using System.Windows;

namespace PasteJump.App.Services;

/// <summary>
/// The three tray icons, read out of the assembly rather than off the disk.
/// <para>
/// They were loose files in an <c>Assets</c> folder until 2026-08-12. The reason was never that a file was
/// wanted: it was that <c>LoadImage(LR_LOADFROMFILE)</c> was the only call that would render an icon at the
/// size the notification area asks for, and it takes a path. <c>CreateIconFromResourceEx</c> takes a size and
/// raw bytes, so the folder could go - see <c>TrayIcon.SetIcon</c>.
/// </para>
/// <para>
/// Cached, because the tray icon is reapplied on every state change - each toggle of Pause or Disable, and
/// every Settings apply - and re-reading a resource stream each time would be work for nothing. Three entries,
/// about 54 KB in total, held for the life of the process.
/// </para>
/// </summary>
/// <remarks>
/// Public rather than internal only so the UI smoke harness can read the same three resources the app does -
/// which is the only place the pack:// URI is ever checked.
/// </remarks>
public static class TrayIconArt
{
    /// <summary>Running normally.</summary>
    public const string Normal = "pastejump.ico";

    /// <summary>Hook uninstalled and hotkey released, so Ctrl+V passes through.</summary>
    public const string Disabled = "pastejump-disabled.ico";

    /// <summary>Capture stopped, but the gesture still works on the clips already held.</summary>
    public const string Paused = "pastejump-paused.ico";

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Bytes of one embedded icon, or an empty array if it cannot be read.
    /// <para>
    /// Empty rather than an exception, and the caller leaves the current icon in place: this runs during
    /// start-up, and failing to draw a decoration must not be able to stop the application from starting.
    /// </para>
    /// </summary>
    public static byte[] Read(string name)
        => Cache.GetOrAdd(name, static key =>
        {
            try
            {
                // Application.GetResourceStream rather than Assembly.GetManifestResourceStream: a WPF
                // <Resource> is packed inside the assembly's .g.resources blob, so it is reachable by pack://
                // URI and not by name. The component form is what works for both the app and the UI smoke
                // harness, which loads these same resources out of a different executable.
                var uri = new Uri($"pack://application:,,,/PasteJump;component/Assets/{key}", UriKind.Absolute);
                var info = Application.GetResourceStream(uri);

                if (info is null)
                {
                    return [];
                }

                using var stream = info.Stream;
                using var memory = new MemoryStream();

                stream.CopyTo(memory);

                return memory.ToArray();
            }
            catch (Exception exception) when (exception is IOException or UriFormatException or InvalidOperationException)
            {
                return [];
            }
        });
}
