using System.Buffers.Binary;
using System.Numerics;
using Meitou.Rendering;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Meitou.ModelViewer;

/// <summary>
/// The world camera as a hex string, for putting two viewers (e.g. two builds being compared) at the same spot: Ctrl+C copies it, Ctrl+V
/// moves the camera to the code in the clipboard. Layout: byte 1 (the version), then the target X, Y, Z, yaw, pitch (radians) and distance
/// as little-endian floats (25 bytes, 50 hex digits). The format is fixed, so a code from any build with these keys works in the others.
/// </summary>
static class CameraCode
{
    const byte Version = 1;
    const int Bytes = 1 + 6 * 4;

    public static string Encode(WorldCamera camera)
    {
        Span<byte> b = stackalloc byte[Bytes];
        b[0] = Version;
        float[] values = [camera.Target.X, camera.Target.Y, camera.Target.Z, camera.Yaw, camera.Pitch, camera.Distance];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b[(1 + 4 * i)..], values[i]);
        return Convert.ToHexString(b);
    }

    /// <summary>Moves <paramref name="camera"/> to the code in <paramref name="text"/> (spaces ignored); false when it is not a camera code.</summary>
    public static bool TryApply(string? text, WorldCamera camera)
    {
        string hex = new((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (hex.Length != 2 * Bytes) return false;
        byte[] b;
        try { b = Convert.FromHexString(hex); }
        catch (FormatException) { return false; }
        if (b[0] != Version) return false;
        var v = new float[6];
        for (int i = 0; i < 6; i++) v[i] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(1 + 4 * i));
        if (v.Any(x => !float.IsFinite(x)) || v[5] <= 0) return false;
        camera.Target = new Vector3(v[0], v[1], v[2]);
        camera.Yaw = v[3];
        camera.Pitch = v[4];
        camera.Distance = v[5];
        return true;
    }

    public static string Describe(WorldCamera c) =>
        $"target {c.Target.X:0}, {c.Target.Y:0}, {c.Target.Z:0}, yaw {c.Yaw * 180 / MathF.PI:0.#}, pitch {c.Pitch * 180 / MathF.PI:0.#}, distance {c.Distance:0}";

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
