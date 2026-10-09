using Meitou.Rendering;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Meitou.ModelViewer;

/// <summary>
/// The world camera as a hex string (<see cref="WorldCameraCode"/>): Ctrl+C copies it, Ctrl+V moves the camera to the code in the clipboard.
/// </summary>
static class CameraCode
{
    public static string Encode(WorldCamera camera) => WorldCameraCode.Encode(camera);

    /// <summary>Moves <paramref name="camera"/> to the code in <paramref name="text"/> (spaces ignored); false when it is not a camera code.</summary>
    public static bool TryApply(string? text, WorldCamera camera) => WorldCameraCode.TryApply(text, camera);

    public static string Describe(WorldCamera c) => WorldCameraCode.Describe(c);

    /// <summary>The window options moved onto monitor <paramref name="monitor"/> (1-based, <c>--monitor</c>), where it then maximizes.</summary>
    public static WindowOptions OnMonitor(WindowOptions options, int? monitor)
    {
        if (monitor is not { } n) return options;
        var monitors = Silk.NET.Windowing.Monitor.GetMonitors(null).ToList();
        if (n < 1 || n > monitors.Count)
        {
            Console.WriteLine($"warning   --monitor {n}: there are {monitors.Count} monitors");
            return options;
        }
        var bounds = monitors[n - 1].Bounds;
        Console.WriteLine($"monitor   {n} ({monitors[n - 1].Name}, {bounds.Size.X}x{bounds.Size.Y} at {bounds.Origin.X}, {bounds.Origin.Y})");
        return options with { Position = bounds.Origin + new Vector2D<int>(40, 40) };
    }
}
