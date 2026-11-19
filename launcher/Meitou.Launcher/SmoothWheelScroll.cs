using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Meitou.Launcher;

/// <summary>
/// Smooth mouse-wheel scrolling for a list: each notch moves a target, and the list's scroll viewer eases towards it frame by frame instead
/// of jumping. A wheel with a modifier key held keeps the default.
/// </summary>
sealed class SmoothWheelScroll
{
    /// <summary>
    /// How far one wheel notch scrolls, in pixels.
    /// </summary>
    const double NotchPixels = 100;

    /// <summary>
    /// How fast the offset closes on the target: the share of the gap left after a second is <c>e^-Smoothing</c>.
    /// </summary>
    const double Smoothing = 16;

    /// <summary>
    /// The list whose scroll viewer is eased.
    /// </summary>
    readonly Control _host;

    /// <summary>
    /// The list's scroll viewer, found at the first wheel turn.
    /// </summary>
    ScrollViewer? _viewer;

    /// <summary>
    /// The vertical offset being eased towards.
    /// </summary>
    double _target;

    /// <summary>
    /// The time of the last frame drawn while easing; null before the first.
    /// </summary>
    TimeSpan? _lastFrame;

    /// <summary>
    /// Whether frames are being requested.
    /// </summary>
    bool _running;

    /// <summary>
    /// Takes over the wheel of <paramref name="host"/> (before its scroll viewer sees it).
    /// </summary>
    public SmoothWheelScroll(Control host)
    {
        _host = host;
        host.AddHandler(InputElement.PointerWheelChangedEvent, Wheel, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// A wheel turn moves the target by its notches, within the list, and starts easing.
    /// </summary>
    void Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Delta.Y == 0) return;

        _viewer ??= _host.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_viewer is null) return;

        double max = Math.Max(0, _viewer.Extent.Height - _viewer.Viewport.Height);
        double from = _running ? _target : _viewer.Offset.Y;
        _target = Math.Clamp(from - e.Delta.Y * NotchPixels, 0, max);
        e.Handled = true;
        Start();
    }

    /// <summary>
    /// Starts requesting frames, unless already.
    /// </summary>
    void Start()
    {
        if (_running) return;

        _running = true;
        _lastFrame = null;
        RequestFrame();
    }

    /// <summary>
    /// Asks for the next frame; stops when the list is not in a window.
    /// </summary>
    void RequestFrame()
    {
        if (TopLevel.GetTopLevel(_host) is { } top) top.RequestAnimationFrame(Frame);
        else _running = false;
    }

    /// <summary>
    /// One frame: the offset moves its share of the gap for the time passed, and lands on the target once within half a pixel.
    /// </summary>
    void Frame(TimeSpan now)
    {
        if (_viewer is null)
        {
            _running = false;
            return;
        }

        double seconds = _lastFrame is { } last ? (now - last).TotalSeconds : 1 / 60.0;
        _lastFrame = now;
        double y = _viewer.Offset.Y;
        if (Math.Abs(_target - y) < 0.5)
        {
            _viewer.Offset = _viewer.Offset.WithY(_target);
            _running = false;
            return;
        }

        _viewer.Offset = _viewer.Offset.WithY(y + (_target - y) * (1 - Math.Exp(-Smoothing * seconds)));
        RequestFrame();
    }
}
