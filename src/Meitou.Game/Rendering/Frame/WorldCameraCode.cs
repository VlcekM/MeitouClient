using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The world camera as a hex string, for putting two viewers (e.g. two builds being compared) at the same spot: the viewer's Ctrl+C copies
/// it, Ctrl+V and <c>--camera-code</c> move the camera to one. Layout: byte 1 (the version), then the target X, Y, Z, yaw, pitch (radians)
/// and distance as little-endian floats (25 bytes, 50 hex digits). The format is fixed, so a code from any build works in the others.
/// A camera location as text (a feedback report's "target x, y, z; eye ...; yaw a, pitch b, boom d", degrees) is read as well.
/// </summary>
public static class WorldCameraCode
{
    /// <summary>
    /// The code's first byte; a code with another is not read.
    /// </summary>
    const byte Version = 1;

    /// <summary>
    /// A code's length in bytes: the version and six floats.
    /// </summary>
    const int Bytes = 1 + 6 * 4;

    /// <summary>
    /// A camera pose read from a code or a location: the target, yaw and pitch (radians) and the distance.
    /// </summary>
    public readonly record struct Pose(Vector3 Target, float Yaw, float Pitch, float Distance);

    /// <summary>
    /// A number in a location text, signed with optional decimals, as a regex group.
    /// </summary>
    const string Number = @"(-?\d+(?:\.\d+)?)";

    /// <summary>
    /// A location text: the target's three numbers, then the yaw, pitch (degrees) and boom or distance.
    /// </summary>
    static readonly Regex Location = new(
        $@"target\s+{Number},\s*{Number},\s*{Number}.*?yaw\s+{Number},\s*pitch\s+{Number},\s*(?:boom|distance)\s+{Number}",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// The code for <paramref name="camera"/>'s target, yaw, pitch and distance: 50 hex digits.
    /// </summary>
    public static string Encode(WorldCamera camera)
    {
        Span<byte> b = stackalloc byte[Bytes];
        b[0] = Version;
        float[] values = [camera.Target.X, camera.Target.Y, camera.Target.Z, camera.Yaw, camera.Pitch, camera.Distance];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b[(1 + 4 * i)..], values[i]);
        return Convert.ToHexString(b);
    }

    /// <summary>
    /// The pose in <paramref name="text"/>, a code (spaces ignored) or a location as text; null when it is neither.
    /// </summary>
    public static Pose? Decode(string? text) => DecodeHex(text) ?? DecodeLocation(text);

    /// <summary>
    /// The pose in a hex code (spaces ignored); null when the length, the hex, the version or a value (not finite, distance not positive) is wrong.
    /// </summary>
    static Pose? DecodeHex(string? text)
    {
        string hex = new((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (hex.Length != 2 * Bytes) return null;
        byte[] b;
        try
        {
            b = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }

        if (b[0] != Version) return null;
        var v = new float[6];
        for (int i = 0; i < 6; i++) v[i] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(1 + 4 * i));
        if (v.Any(x => !float.IsFinite(x)) || v[5] <= 0) return null;
        return new Pose(new Vector3(v[0], v[1], v[2]), v[3], v[4], v[5]);
    }

    /// <summary>
    /// The pose in a location as text (angles in degrees); null when <paramref name="text"/> holds none.
    /// </summary>
    static Pose? DecodeLocation(string? text)
    {
        if (Location.Match(text ?? "") is not { Success: true } m) return null;
        float At(int group) => float.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
        float distance = At(6);
        if (distance <= 0) return null;
        var target = new Vector3(At(1), At(2), At(3));
        return new Pose(target, At(4) * MathF.PI / 180, At(5) * MathF.PI / 180, distance);
    }

    /// <summary>
    /// Puts <paramref name="camera"/> at <paramref name="pose"/>: its target, yaw, pitch and distance.
    /// </summary>
    public static void Apply(Pose pose, WorldCamera camera) =>
        (camera.Target, camera.Yaw, camera.Pitch, camera.Distance) = (pose.Target, pose.Yaw, pose.Pitch, pose.Distance);

    /// <summary>
    /// Moves <paramref name="camera"/> to the code in <paramref name="text"/>; false when it is not a camera code.
    /// </summary>
    public static bool TryApply(string? text, WorldCamera camera)
    {
        if (Decode(text) is not { } pose) return false;
        Apply(pose, camera);
        return true;
    }

    /// <summary>
    /// <paramref name="c"/> as a location text (the target, yaw and pitch in degrees, the distance), the form <see cref="Decode"/> reads.
    /// </summary>
    public static string Describe(WorldCamera c) =>
        $"target {c.Target.X:0}, {c.Target.Y:0}, {c.Target.Z:0}, yaw {c.Yaw * 180 / MathF.PI:0.#}, "
        + $"pitch {c.Pitch * 180 / MathF.PI:0.#}, distance {c.Distance:0}";
}