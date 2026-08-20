using System.Windows;
using PasteJump.Core.PasteMode;

namespace PasteJump.App.Services;

/// <summary>
/// Puts a window where an <see cref="OverlayAnchor"/> says, in device-independent units, on the right monitor.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the paste overlay and the copy notification, which is the point: both answer the same question - "the
/// user asked for it <i>there</i>, where is that in WPF units on this monitor" - and having answered it twice
/// would mean the two drifting apart the first time either was fixed. The toast used to carry its own
/// cursor-relative arithmetic with its own edge clamping.
/// </para>
/// <para>
/// Anchor coordinates arrive in physical pixels from Win32, but WPF positions windows in device-independent
/// units, so they must be scaled by the DPI of the monitor the anchor is actually on - not the primary monitor's.
/// Skipping that conversion is what puts windows in the wrong place on mixed-DPI desktops.
/// </para>
/// </remarks>
internal static class AnchoredPlacement
{
    /// <summary>Gap left at a screen edge, and beside a point.</summary>
    private const double Margin = 4;

    /// <summary>
    /// Positions <paramref name="window"/>. The fallback size is used before the first layout pass, when
    /// <c>ActualWidth</c> is still zero.
    /// </summary>
    public static void Apply(Window window, OverlayAnchor anchor, double fallbackWidth, double fallbackHeight)
    {
        ArgumentNullException.ThrowIfNull(window);

        var scale = WindowInterop.GetScaleForPoint(anchor.X, anchor.Y);
        var bounds = WindowInterop.GetWorkAreaForPoint(anchor.X, anchor.Y, scale);

        var width = window.ActualWidth > 0 ? window.ActualWidth : fallbackWidth;
        var height = window.ActualHeight > 0 ? window.ActualHeight : fallbackHeight;

        double left;
        double top;

        switch (anchor.Placement)
        {
            // Beside a window we cannot draw above. Solved in Core against the work area and returned already
            // clamped, so it skips the edge handling below - which exists for the point-anchored cases and would
            // happily push the window back onto the very thing it was told to avoid.
            case OverlayPlacement.OutsideWindow when anchor.Avoid is { } avoid:
            {
                (left, top) = OverlayPlacementSolver.Beside(
                    new ScreenBox(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                    new ScreenBox(avoid.Left / scale, avoid.Top / scale, avoid.Right / scale, avoid.Bottom / scale),
                    width,
                    height);

                break;
            }

            // Where Windows puts its own notifications. The anchor point was only ever a hint as to which
            // monitor, so nothing here reads it beyond the work area it selected.
            case OverlayPlacement.WorkAreaBottomRight:
                left = bounds.Right - width - (Margin * 2);
                top = bounds.Bottom - height - (Margin * 2);
                break;

            default:
            {
                var centred = anchor.Placement == OverlayPlacement.CentredOn;

                left = centred ? (anchor.X / scale) - (width / 2) : (anchor.X / scale) + Margin;
                top = centred ? (anchor.Y / scale) - (height / 2) : (anchor.Y / scale) + 20;

                if (left + width > bounds.Right)
                {
                    left = bounds.Right - width - Margin;
                }

                if (top + height > bounds.Bottom)
                {
                    // Flip above the point rather than clamping to the bottom edge, so the window does not cover
                    // the line the user is typing on. Nothing to avoid when centred on a window, where flipping
                    // would move it a whole window-height from where it was asked to be.
                    top = centred
                        ? bounds.Bottom - height - Margin
                        : (anchor.Y / scale) - height - 6;
                }

                break;
            }
        }

        // Snapped to whole device pixels. Everything above divides physical pixels by the scale factor, so at 150%
        // the result routinely lands on a half pixel and WPF renders the whole window - text included - visibly
        // soft. UseLayoutRounding does not help: it rounds layout within a window, not the window's own origin.
        window.Left = WindowInterop.SnapToDevicePixel(Math.Max(bounds.Left, left), scale);
        window.Top = WindowInterop.SnapToDevicePixel(Math.Max(bounds.Top, top), scale);
    }
}
