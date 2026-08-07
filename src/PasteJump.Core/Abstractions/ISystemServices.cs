namespace PasteJump.Core.Abstractions;

/// <summary>Sends the actual paste keystroke to whatever window has focus.</summary>
public interface IPasteSender
{
    /// <summary>
    /// Synthesises Ctrl+V into the foreground window. False when the keystroke could not be
    /// delivered at all - most often because the target window is elevated and UIPI drops synthetic
    /// input aimed at it.
    /// </summary>
    bool SendPaste();
}

/// <summary>Identifies the window we would be pasting into.</summary>
public interface IForegroundWindowInfo
{
    /// <summary>File name (no path) of the foreground window's process, or null if unavailable.</summary>
    string? GetForegroundProcessName();
}

/// <summary>
/// Injectable clock. Present so retention and ordering logic can be tested without sleeping.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Default clock backed by the system time.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
