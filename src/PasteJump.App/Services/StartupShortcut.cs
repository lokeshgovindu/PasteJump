using System.Runtime.InteropServices;

namespace PasteJump.App.Services;

/// <summary>
/// Manages the run-at-logon shortcut in the user's Startup folder.
/// <para>
/// A shortcut rather than a <c>Run</c> registry value, because it keeps the app portable and
/// visibly user-controlled: it shows up in Task Manager's Startup tab where people expect to find
/// it, and moving the folder elsewhere leaves a broken shortcut the user can see rather than a
/// silent registry entry pointing at nothing. This is also what the original did
/// (Clipjump.ahk:278).
/// </para>
/// </summary>
internal static class StartupShortcut
{
    private const string ShortcutFileName = "PasteJump.lnk";

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        ShortcutFileName);

    public static bool Exists => File.Exists(ShortcutPath);

    public static void Apply(bool enabled)
    {
        try
        {
            if (enabled)
            {
                Create();
            }
            else if (Exists)
            {
                File.Delete(ShortcutPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            // A failure here must not be fatal - the app still works, it just will not auto-start.
        }
    }

    private static void Create()
    {
        var target = Environment.ProcessPath;

        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        // Late-bound WScript.Shell rather than a COM reference to IWshRuntimeLibrary: it avoids an
        // interop assembly and a build-time dependency on the Windows Script Host type library.
        var shellType = Type.GetTypeFromProgID("WScript.Shell");

        if (shellType is null)
        {
            return;
        }

        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType);

            if (shell is null)
            {
                return;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                [ShortcutPath]);

            if (shortcut is null)
            {
                return;
            }

            var shortcutType = shortcut.GetType();

            SetProperty(shortcutType, shortcut, "TargetPath", target);
            SetProperty(shortcutType, shortcut, "WorkingDirectory", Path.GetDirectoryName(target) ?? string.Empty);
            SetProperty(shortcutType, shortcut, "Description", "PasteJump clipboard manager");

            shortcutType.InvokeMember(
                "Save",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shortcut,
                null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.ReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.ReleaseComObject(shell);
            }
        }
    }

    private static void SetProperty(Type type, object instance, string name, string value)
        => type.InvokeMember(
            name,
            System.Reflection.BindingFlags.SetProperty,
            null,
            instance,
            [value]);
}
