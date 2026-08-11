using System.Diagnostics;
using PasteJump.Core;

namespace PasteJump.App.Services;

/// <summary>
/// Finds and opens the compiled user manual, <c>PasteJump.chm</c>.
/// <para>
/// The manual shipped in the release download for months with nothing in the application able to open it, so
/// the only way to read it was to find the file in Explorer. This is the missing route.
/// </para>
/// </summary>
internal static class HelpDocument
{
    public const string FileName = "PasteJump.chm";

    /// <summary>
    /// The manual's path, or null when it is not present.
    /// <para>
    /// Two candidates, and both are needed. Beside the executable is where a release puts it - the ZIP stages
    /// the exe and the .chm together, and the installer copies both into the program folder. The extraction
    /// directory is the single-file fallback and is deliberately probed rather than inferred from how the app
    /// was published, exactly as <see cref="AppPaths.AssetsDirectory"/> does.
    /// </para>
    /// <para>
    /// A development build has neither, because the .chm is built separately by tools/build-help.ps1 and is not
    /// a compile output. That is why this returns null instead of throwing: absent is a normal state, not a
    /// failure.
    /// </para>
    /// </summary>
    public static string? Locate()
    {
        foreach (var directory in new[] { AppPaths.ApplicationDirectory, AppContext.BaseDirectory })
        {
            var candidate = Path.Combine(directory, FileName);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the file carries a mark-of-the-web stream, which makes hh.exe show a blank topic pane.
    /// <para>
    /// This is the classic .chm failure and it looks like a broken help file rather than a security measure:
    /// every page renders as "Navigation to the webpage was canceled" with no explanation. It happens to
    /// anyone who opens the manual straight out of the downloaded ZIP without unblocking it.
    /// </para>
    /// <para>
    /// Detected rather than removed. Deleting the stream is a one-line call, but silently stripping a
    /// Windows security marker from a file on the user's behalf is not this application's business - so the
    /// caller explains what to do instead. Opening an alternate data stream is the only way to ask; there is
    /// no managed API for it, and a missing stream throws rather than reporting false.
    /// </para>
    /// </summary>
    public static bool IsBlockedByZoneIdentifier(string path)
    {
        try
        {
            using var stream = File.OpenRead(path + ":Zone.Identifier");
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The file cannot be read at all, which is a different problem and one that opening it will report
            // far better than a guess here would.
            return false;
        }
    }

    /// <summary>
    /// Opens the manual with the shell, which hands it to hh.exe.
    /// <para>
    /// <c>UseShellExecute</c> is required: a .chm is a document rather than an executable, so without it .NET
    /// tries to run the file and throws.
    /// </para>
    /// </summary>
    public static void Open(string path)
        => Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
}
