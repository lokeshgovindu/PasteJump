using System.Runtime.InteropServices;
using System.Text;
using PasteJump.Core.Abstractions;
using PasteJump.Core.Model;
using PasteJump.Interop.Win32;

namespace PasteJump.Interop;

/// <summary>
/// Reads and writes the system clipboard, preserving every HGLOBAL-backed format.
/// </summary>
public sealed class Win32ClipboardAccess : IClipboardAccess
{
    /// <summary>
    /// Registered (non-standard) clipboard format ids start here. Below this are the fixed
    /// <c>CF_*</c> constants, which have no retrievable name.
    /// </summary>
    private const uint FirstRegisteredFormatId = 0xC000;

    /// <summary>
    /// Backoff schedule for acquiring the clipboard, in milliseconds. The first attempt is
    /// immediate, so the common uncontended case costs nothing.
    /// <para>
    /// Measured rather than guessed: a flat 5 x 40 ms budget dropped roughly half of the captures
    /// in a smoke test that wrote to the clipboard three times in quick succession, because the
    /// writing process still held the lock. Ramping out to about 600 ms total absorbs a slow
    /// writer while still being bounded - which is the point, since the original's equivalent spun
    /// on <c>OpenClipboard</c> forever and turned another app's misbehaviour into a hang.
    /// </para>
    /// </summary>
    private static readonly int[] DefaultBackoffMs = [0, 15, 30, 50, 75, 100, 150, 200];

    private readonly IForegroundWindowInfo? _foreground;
    private readonly int[] _backoffMs;

    public Win32ClipboardAccess(
        IForegroundWindowInfo? foreground = null,
        int[]? backoffMs = null)
    {
        _foreground = foreground;
        _backoffMs = backoffMs is { Length: > 0 } ? backoffMs : DefaultBackoffMs;
    }

    /// <summary>Total time the acquire loop may spend waiting, for diagnostics.</summary>
    public int MaxAcquireWaitMs => _backoffMs.Sum();

    public uint SequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    public ClipboardSnapshot? TryRead()
    {
        if (!TryOpen())
        {
            return null;
        }

        try
        {
            var payloads = new List<ClipPayload>();
            uint format = 0;

            while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
            {
                if (NativeConstants.NonGlobalFormats.Contains(format))
                {
                    continue;
                }

                var data = TryReadFormat(format);

                if (data is null)
                {
                    continue;
                }

                payloads.Add(new ClipPayload(format, TryGetFormatName(format), data));
            }

            if (payloads.Count == 0)
            {
                return null;
            }

            var text = ExtractText(payloads);
            var kind = ClassifyKind(payloads, text);

            return new ClipboardSnapshot(payloads, text, kind, _foreground?.GetForegroundProcessName());
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public bool TryWrite(IReadOnlyList<ClipPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        if (payloads.Count == 0)
        {
            return false;
        }

        var toWrite = FilterForWrite(payloads);

        if (!TryOpen())
        {
            return false;
        }

        try
        {
            if (!NativeMethods.EmptyClipboard())
            {
                return false;
            }

            var wroteAnything = false;

            foreach (var payload in toWrite)
            {
                // Re-register by name. Registered ids are only stable within a Windows session,
                // so replaying yesterday's numeric id would attach the bytes to whatever format
                // now happens to hold it.
                var formatId = payload.IsRegisteredFormat
                    ? NativeMethods.RegisterClipboardFormat(payload.FormatName!)
                    : payload.FormatId;

                if (formatId == 0)
                {
                    continue;
                }

                if (WriteFormat(formatId, payload.Data))
                {
                    wroteAnything = true;
                }
            }

            return wroteAnything;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>Builds a text-only payload set, for formatters that narrow the output.</summary>
    public static IReadOnlyList<ClipPayload> TextOnlyPayloads(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Null-terminated, as every consumer of CF_UNICODETEXT expects.
        var bytes = Encoding.Unicode.GetBytes(text + '\0');
        return [new ClipPayload(NativeConstants.CF_UNICODETEXT, null, bytes)];
    }

    /// <summary>
    /// Pulls the plain text out of a payload set, if it has any.
    /// <para>
    /// Only <c>CF_UNICODETEXT</c> is consulted, and there is no <c>CF_TEXT</c> fallback by design.
    /// Windows synthesises <c>CF_UNICODETEXT</c> from <c>CF_TEXT</c> during enumeration, so any
    /// clipboard carrying text offers the Unicode form - meaning a fallback would be unreachable
    /// in practice while being actively wrong if it ever ran: <c>CF_TEXT</c> is in the system ANSI
    /// codepage, but .NET Core's <c>Encoding.Default</c> is UTF-8, so decoding it that way would
    /// mangle every non-ASCII character it touched.
    /// </para>
    /// </summary>
    public static string? ExtractText(IReadOnlyList<ClipPayload> payloads)
    {
        var unicode = payloads.FirstOrDefault(static p => p.FormatId == NativeConstants.CF_UNICODETEXT);

        return unicode is not null ? TrimNul(Encoding.Unicode.GetString(unicode.Data)) : null;
    }

    /// <summary>Classifies a payload set for icon and preview purposes.</summary>
    public static ClipKind ClassifyKind(IReadOnlyList<ClipPayload> payloads, string? text)
    {
        if (payloads.Any(static p => p.FormatId == NativeConstants.CF_HDROP))
        {
            return ClipKind.Files;
        }

        if (payloads.Any(static p =>
            p.FormatId is NativeConstants.CF_DIB or NativeConstants.CF_DIBV5))
        {
            return ClipKind.Image;
        }

        return string.IsNullOrEmpty(text) ? ClipKind.Other : ClipKind.Text;
    }

    // ------------------------------------------------------------- internals

    /// <summary>
    /// Bounded open attempt. The clipboard is a machine-wide lock any process can hold, so this
    /// must be able to fail: the original's equivalent spun on <c>OpenClipboard</c> indefinitely
    /// (<c>MakeClipboardAvailable</c>), which turns another app's misbehaviour into our hang.
    /// A dropped capture is recoverable; a wedged UI thread is not.
    /// </summary>
    private bool TryOpen()
    {
        for (var attempt = 0; attempt < _backoffMs.Length; attempt++)
        {
            var delay = _backoffMs[attempt];

            if (delay > 0)
            {
                Thread.Sleep(delay);
            }

            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[]? TryReadFormat(uint format)
    {
        var handle = NativeMethods.GetClipboardData(format);

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);

        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = (long)NativeMethods.GlobalSize(handle);

            if (size <= 0)
            {
                return null;
            }

            // Guard against a hostile or broken owner advertising an absurd size.
            if (size > int.MaxValue)
            {
                return null;
            }

            var buffer = new byte[size];
            Marshal.Copy(pointer, buffer, 0, (int)size);
            return buffer;
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    private static bool WriteFormat(uint format, byte[] data)
    {
        var handle = NativeMethods.GlobalAlloc(NativeConstants.GMEM_MOVEABLE, (UIntPtr)(uint)data.Length);

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var pointer = NativeMethods.GlobalLock(handle);

        if (pointer == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            return false;
        }

        try
        {
            Marshal.Copy(data, 0, pointer, data.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }

        if (NativeMethods.SetClipboardData(format, handle) == IntPtr.Zero)
        {
            // Ownership only transfers on success, so on failure the block is still ours to free.
            NativeMethods.GlobalFree(handle);
            return false;
        }

        // Succeeded: the clipboard owns the block now. Freeing it here would be a double free.
        return true;
    }

    private static string? TryGetFormatName(uint format)
    {
        if (format < FirstRegisteredFormatId)
        {
            return null;
        }

        var buffer = new StringBuilder(256);
        var length = NativeMethods.GetClipboardFormatName(format, buffer, buffer.Capacity);

        return length > 0 ? buffer.ToString(0, length) : null;
    }

    /// <summary>
    /// Drops formats Windows will synthesise for us, so a stale sibling cannot contradict the
    /// authoritative one.
    /// </summary>
    private static List<ClipPayload> FilterForWrite(IReadOnlyList<ClipPayload> payloads)
    {
        var hasUnicodeText = payloads.Any(static p => p.FormatId == NativeConstants.CF_UNICODETEXT);
        var result = new List<ClipPayload>(payloads.Count);

        foreach (var payload in payloads)
        {
            if (hasUnicodeText && NativeConstants.SynthesisedFromUnicodeText.Contains(payload.FormatId))
            {
                continue;
            }

            if (NativeConstants.NonGlobalFormats.Contains(payload.FormatId))
            {
                continue;
            }

            result.Add(payload);
        }

        return result;
    }

    private static string TrimNul(string value)
    {
        var terminator = value.IndexOf('\0', StringComparison.Ordinal);
        return terminator >= 0 ? value[..terminator] : value;
    }
}
