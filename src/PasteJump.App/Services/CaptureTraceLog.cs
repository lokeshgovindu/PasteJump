using System.IO;
using System.Text;

namespace PasteJump.App.Services;

/// <summary>
/// A one-line-per-decision account of what capture did, written to <c>logs\capture.log</c> in the data folder.
/// </summary>
/// <remarks>
/// <para>
/// Exists because a reported bug - one copy arriving as two clips, or a new copy announcing itself as "Same as the
/// last copy" - was fixed blind twice. What decides the behaviour is the timing between an application's clipboard
/// publishing steps, and that differs per application: a WinForms image write raised its second notification
/// <b>45 ms</b> after the first, a multi-format text write <b>345 ms</b> after and under the <b>same sequence
/// number</b>. None of it was visible after the fact, so each fix was a guess at what the application had done.
/// </para>
/// <para>
/// <b>Metadata only, never content.</b> Kinds, byte counts, format counts, a source process name and a short hash
/// of the dedup key - enough to tell two copies apart and nothing that reconstructs what was copied. A diagnostics
/// file that accumulated clipboard text would be a worse problem than the bug it explains.
/// </para>
/// <para>
/// Truncated rather than rotated: this is read when something has just gone wrong, so the newest lines are the
/// point and a second file to hunt through is not.
/// </para>
/// </remarks>
internal sealed class CaptureTraceLog
{
    /// <summary>Where it truncates. Roughly ten thousand lines, which is days of ordinary use.</summary>
    private const long MaxBytes = 512 * 1024;

    private readonly string _path;
    private readonly Lock _gate = new();

    public CaptureTraceLog(string dataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);

        var folder = Path.Combine(dataFolder, "logs");

        Directory.CreateDirectory(folder);

        _path = Path.Combine(folder, "capture.log");
    }

    /// <summary>The file, so the Settings dialog can offer to open it.</summary>
    public string Path_ => _path;

    /// <summary>
    /// Appends one line. Swallows every IO failure on purpose: diagnostics must not be able to break capture,
    /// which is the thing they exist to explain.
    /// </summary>
    public void Write(string message)
    {
        try
        {
            lock (_gate)
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    File.Delete(_path);
                }

                File.AppendAllText(
                    _path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
