using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PasteJump.App.Services;

/// <summary>
/// Resolves a running process to its executable path and icon, for the excluded-application picker.
/// <para>
/// Both are best-effort. A picker that shows nothing for a process it cannot inspect is still useful - the
/// file name alone identifies the program - so every failure here degrades to null rather than throwing.
/// </para>
/// </summary>
internal static class ProgramIcons
{
    /// <summary>
    /// Full path of a process's executable, or null.
    /// <para>
    /// <c>QueryFullProcessImageName</c> rather than <c>Process.MainModule</c>. <c>MainModule</c> enumerates the
    /// module list, which fails outright across a bitness boundary and for anything running at a higher
    /// elevation - so on an ordinary desktop it throws for a good fraction of what the picker lists. This
    /// needs only <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which is granted far more readily.
    /// </para>
    /// </summary>
    public static string? TryGetPath(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;

            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? buffer.ToString(0, size)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// The executable's small icon as a frozen <see cref="BitmapSource"/>, or null.
    /// <para>
    /// Frozen because these are built on one thread and then handed to a grid: an unfrozen
    /// <c>BitmapSource</c> has thread affinity, and freezing also lets WPF skip its change-tracking.
    /// </para>
    /// </summary>
    public static BitmapSource? TryGetIcon(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        var small = new IntPtr[1];

        // Small icon specifically: this renders at 16-20px in a list, and the large icon downscaled to that
        // is visibly worse than the frame the author drew for the size.
        if (ExtractIconEx(executablePath, 0, null, small, 1) <= 0 || small[0] == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                small[0],
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            // A malformed or unreadable icon resource. Not worth surfacing - the row still has its name.
            return null;
        }
        finally
        {
            // Destroyed only after the conversion, which copies the pixels rather than referencing them.
            DestroyIcon(small[0]);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, int icons);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
