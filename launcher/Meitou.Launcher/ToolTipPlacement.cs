using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Meitou.Launcher;

/// <summary>
/// Places every launcher tooltip beside the pointer, never under it: below the cursor where it fits, else above it. Avalonia's own pointer
/// placement keeps its 20 px downward offset when it flips a tooltip above the pointer (near the screen's bottom, where the footer's Play and
/// Continue sit), so the tooltip covered the cursor and took the next click.
/// </summary>
static class ToolTipPlacement
{
    /// <summary>
    /// The gap kept between the pointer and a tooltip above it, in pixels.
    /// </summary>
    const double GapAbove = 4;

    /// <summary>
    /// The room left below the pointer for the cursor's arrow, in pixels (Avalonia's own tooltip offset).
    /// </summary>
    const double GapBelow = 20;

    /// <summary>
    /// The pointer's last position on the screen; null before it first moves over a launcher window.
    /// </summary>
    static PixelPoint? s_pointer;

    /// <summary>
    /// Tracks the pointer over every launcher window and has every tooltip placed by <see cref="Place"/>.
    /// </summary>
    public static void Register()
    {
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>(PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>(Opening);
    }

    /// <summary>
    /// Remembers where the pointer is, in screen pixels.
    /// </summary>
    static void PointerMoved(TopLevel topLevel, PointerEventArgs e) => s_pointer = topLevel.PointToScreen(e.GetPosition(topLevel));

    /// <summary>
    /// Has the tooltip <paramref name="control"/> is about to show placed by <see cref="Place"/>.
    /// </summary>
    static void Opening(Control control, CancelRoutedEventArgs e)
    {
        control.SetCurrentValue(ToolTip.PlacementProperty, PlacementMode.Custom);
        control.SetCurrentValue(ToolTip.CustomPopupPlacementCallbackProperty, Place);
    }

    /// <summary>
    /// Puts the tooltip right of and below the pointer's keep-out box, flipped to its left or above it where it does not fit. The gaps are part
    /// of the box, not an offset, so a flip mirrors them; the tooltip never slides vertically, which could bring it back over the pointer.
    /// </summary>
    static void Place(CustomPopupPlacement placement)
    {
        placement.AnchorRectangle = KeepOut(placement);
        placement.Anchor = PopupAnchor.BottomLeft;
        placement.Gravity = PopupGravity.BottomRight;
        placement.Offset = default;
        placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipX
            | PopupPositionerConstraintAdjustment.FlipY
            | PopupPositionerConstraintAdjustment.SlideX;
    }

    /// <summary>
    /// The box the tooltip must stay out of, in its window's coordinates: the pointer with the room its cursor takes, or the control's own bounds
    /// while the pointer is unknown or off the control.
    /// </summary>
    static Rect KeepOut(CustomPopupPlacement placement)
    {
        var bounds = placement.AnchorRectangle;
        if (s_pointer is not { } pointer || TopLevel.GetTopLevel(placement.Target) is not { } topLevel) return bounds;

        var at = topLevel.PointToClient(pointer);
        if (!bounds.Inflate(1).Contains(at)) return bounds;

        return new Rect(at.X, at.Y - GapAbove, 1, GapAbove + GapBelow);
    }
}
