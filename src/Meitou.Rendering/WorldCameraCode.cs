using System.Buffers.Binary;
using System.Numerics;

namespace Meitou.Rendering;

/// <summary>
/// The world camera as a hex string, for putting two viewers (e.g. two builds being compared) at the same spot: the viewer's Ctrl+C copies
/// it, Ctrl+V and <c>--camera-code</c> move the camera to one. Layout: byte 1 (the version), then the target X, Y, Z, yaw, pitch (radians)
/// and distance as little-endian floats (25 bytes, 50 hex digits). The format is fixed, so a code from any build works in the others.
/// </summary>
public static class WorldCameraCode
{
    const byte Version = 1;
    const int Bytes = 1 + 6 * 4;

    public readonly record struct Pose(Vector3 Target, float Yaw, float Pitch, float Distance);

    public static string Encode(WorldCamera camera)
    {
        Span<byte> b = stackalloc byte[Bytes];
        b[0] = Version;
        float[] values = [camera.Target.X, camera.Target.Y, camera.Target.Z, camera.Yaw, camera.Pitch, camera.Distance];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b[(1 + 4 * i)..], values[i]);
        return Convert.ToHexString(b);
    }

    /// <summary>The pose in <paramref name="text"/> (spaces ignored); null when it is not a camera code.</summary>
    public static Pose? Decode(string? text)
    {
        string hex = new((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (hex.Length != 2 * Bytes) return null;
        byte[] b;
        try { b = Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
        if (b[0] != Version) return null;
        var v = new float[6];
        for (int i = 0; i < 6; i++) v[i] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(1 + 4 * i));
        if (v.Any(x => !float.IsFinite(x)) || v[5] <= 0) return null;
        return new Pose(new Vector3(v[0], v[1], v[2]), v[3], v[4], v[5]);
    }

    public static void Apply(Pose pose, WorldCamera camera) =>
        (camera.Target, camera.Yaw, camera.Pitch, camera.Distance) = (pose.Target, pose.Yaw, pose.Pitch, pose.Distance);

    /// <summary>Moves <paramref name="camera"/> to the code in <paramref name="text"/>; false when it is not a camera code.</summary>
    public static bool TryApply(string? text, WorldCamera camera)
    {
        if (Decode(text) is not { } pose) return false;
        Apply(pose, camera);
        return true;
    }

    public static string Describe(WorldCamera c) =>
        $"target {c.Target.X:0}, {c.Target.Y:0}, {c.Target.Z:0}, yaw {c.Yaw * 180 / MathF.PI:0.#}, pitch {c.Pitch * 180 / MathF.PI:0.#}, distance {c.Distance:0}";
}
