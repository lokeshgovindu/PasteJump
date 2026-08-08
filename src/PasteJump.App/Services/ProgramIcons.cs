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

        return FromExtractIconEx(executablePath) ?? FromShell(executablePath);
    }

    /// <summary>The icon embedded in the executable itself.</summary>
    private static BitmapSource? FromExtractIconEx(string executablePath)
    {
        var small = new IntPtr[1];

        if (ExtractIconEx(executablePath, 0, null, small, 1) <= 0 || small[0] == IntPtr.Zero)
        {
            return null;
        }

        return Convert(small[0]);
    }

    /// <summary>
    /// Asks the shell instead, which knows things the executable does not.
    /// <para>
    /// Needed for packaged applications. Windows Terminal, the Settings host and the input host all ship an
    /// executable with no icon resource - the icon lives in the app package - so
    /// <see cref="ExtractIconEx"/> returns nothing and those rows came up blank. The shell resolves them.
    /// </para>
    /// </summary>
    private static BitmapSource? FromShell(string executablePath)
    {
        var info = default(SHFILEINFO);

        if (SHGetFileInfo(
                executablePath,
                0,
                ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_SMALLICON) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        return Convert(info.hIcon);
    }

    /// <summary>
    /// Turns an <c>HICON</c> into a frozen bitmap and releases the handle.
    /// <para>
    /// Note the source keeps whatever size the icon actually is - often 32x32 even when the small icon was
    /// requested. Callers must scale rather than crop; an <c>Image</c> with <c>Stretch="None"</c> in a 16x16
    /// box renders the icon's top-left quarter, which is how this first shipped.
    /// </para>
    /// </summary>
    private static BitmapSource? Convert(IntPtr icon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon,
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
            DestroyIcon(icon);
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

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref SHFILEINFO info,
        uint sizeOfInfo,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
