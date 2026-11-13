using System.Numerics;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// Camera for the world view, Y up: orbits a target point (left drag), looks around from the eye (right drag),
/// flies with the keyboard. The scene is drawn into one depth buffer in <see cref="DepthConvention"/> (reversed on Vulkan), from
/// <see cref="ClipNear"/> (which follows the height above the terrain) to <see cref="ViewDistance"/>.
/// </summary>
public sealed class WorldCamera
{
    /// <summary>
    /// The point the camera orbits and looks at, in world units.
    /// </summary>
    public Vector3 Target
    {
        get;
        set
        {
            field = value;
            PreciseEye = null;
        }
    }

    /// <summary>
    /// The eye itself in double precision, for a camera that is placed by its eye (the game's free camera): <see cref="Target"/> is then a float
    /// point far along the view, and the eye worked back from it stepped with the float's grid as it moved (0.004 units at 50000: a jump of a
    /// pixel or two for something a few units away). Set after the pose; setting <see cref="Target"/>, <see cref="Yaw"/>, <see cref="Pitch"/> or
    /// <see cref="Distance"/> clears it.
    /// </summary>
    public (double X, double Y, double Z)? PreciseEye { get; set; }

    /// <summary>
    /// Radians; 0 looks along −Z from +Z (the eye sits at +Z of the target).
    /// </summary>
    public float Yaw
    {
        get;
        set
        {
            field = value;
            PreciseEye = null;
        }
    }

    /// <summary>
    /// Radians above the horizon of the eye, as seen from the target.
    /// </summary>
    public float Pitch
    {
        get;
        set
        {
            field = value;
            PreciseEye = null;
        }
    } = 0.5f;

    /// <summary>
    /// The eye's distance from <see cref="Target"/>, in world units (<see cref="Zoom"/> keeps it 5 .. <see cref="ViewDistance"/>).
    /// </summary>
    public float Distance
    {
        get;
        set
        {
            field = value;
            PreciseEye = null;
        }
    } = 2000;

    /// <summary>
    /// Radians the picture is turned about the view direction (photo mode; 0 keeps the horizon level). Positive banks the camera to the right (the
    /// view's up turns clockwise as seen), so the horizon's right end rises.
    /// </summary>
    public float Roll { get; set; }

    /// <summary>
    /// The vertical field of view in radians (50 degrees by default).
    /// </summary>
    public float FieldOfView { get; set; } = 50 * MathF.PI / 180;

    /// <summary>
    /// The depth of field's focus, world units from the eye, as a cinema key sets it; null: the target's distance.
    /// </summary>
    public float? FocusDistance { get; set; }

    /// <summary>
    /// The f-number as a cinema key sets it; null: <see cref="PostOptions.DofAperture"/>.
    /// </summary>
    public float? Aperture { get; set; }

    /// <summary>
    /// The view distance asked for; the app raises <see cref="ViewDistance"/> above it when the haze reaches further (high eye).
    /// </summary>
    public float MinViewDistance { get; set; } = 60000;

    /// <summary>
    /// Furthest distance anything is drawn at.
    /// </summary>
    public float ViewDistance { get; set; } = 60000;

    /// <summary>
    /// Height of the eye above the terrain (set by the app each frame), for the near plane.
    /// </summary>
    public float EyeClearance { get; set; } = 100;

    /// <summary>
    /// The unit vector from the target towards the eye.
    /// </summary>
    Vector3 Back => new(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));

    /// <summary>
    /// The eye in float: the double-precision eye rounded (<see cref="EyeResidual"/> holds what the rounding left). Far from the origin a float
    /// steps by 0.004 units at 50000 and 0.016 at 200000; a target moving by less than that flipped the eye a whole step, a jump of one or two
    /// pixels for a character a few units from it (docs/character-renderer.md "Precision").
    /// </summary>
    public Vector3 Eye => EyePrecise(out _);

    /// <summary>
    /// The double-precision eye less <see cref="Eye"/>: under half a float step, exact in float. The eye-relative views take it in their
    /// translation.
    /// </summary>
    public Vector3 EyeResidual
    {
        get
        {
            EyePrecise(out var r);
            return r;
        }
    }

    /// <summary>
    /// Where the eye is (<see cref="Distance"/> from the target along the yaw and pitch, or <see cref="PreciseEye"/>), worked in double: the float
    /// eye and what its rounding left.
    /// </summary>
    Vector3 EyePrecise(out Vector3 residual)
    {
        double cp = Math.Cos(Pitch), d = Distance;
        double x = Target.X + cp * Math.Sin(Yaw) * d, y = Target.Y + Math.Sin(Pitch) * d, z = Target.Z + cp * Math.Cos(Yaw) * d;
        if (PreciseEye is { } precise) (x, y, z) = precise;

        var eye = new Vector3((float)x, (float)y, (float)z);
        residual = new Vector3((float)(x - eye.X), (float)(y - eye.Y), (float)(z - eye.Z));
        return eye;
    }

    /// <summary>
    /// The unit view direction, from the eye towards the target.
    /// </summary>
    public Vector3 Forward => -Back;

    /// <summary>
    /// The view's up direction: world up turned by <see cref="Roll"/> about <see cref="Forward"/>.
    /// </summary>
    public Vector3 Up => Roll == 0 ? Vector3.UnitY : Vector3.Transform(Vector3.UnitY, Quaternion.CreateFromAxisAngle(Forward, Roll));

    /// <summary>
    /// A vertical field of view and aspect whose cone holds the view's frustum at any <see cref="Roll"/> (the frustum itself when level), for the
    /// tests that take a cone from <see cref="Forward"/> and assume the horizon is level.
    /// </summary>
    public (float FieldOfView, float Aspect) CullCone(float aspect)
    {
        if (Roll == 0) return (FieldOfView, aspect);

        float tanY = MathF.Tan(FieldOfView * 0.5f), tanX = tanY * aspect;
        return (2 * MathF.Atan(MathF.Sqrt(tanX * tanX + tanY * tanY)), 1);
    }

    /// <summary>
    /// The view matrix, Y up: looks along <see cref="Forward"/> (at <see cref="Target"/>), the rotation from the angles built at the origin, not
    /// from the float eye, whose rounding steps would turn the view by up to half a float step over the distance (a quarter pixel at 15 units from
    /// the target at 50000) every time the eye crosses one; nor from a look-at point beside the far eye (eye + forward rounds to the eye's float
    /// step, a turn of a few pixels).
    /// </summary>
    public Matrix4x4 View => Matrix4x4.CreateTranslation(-Eye) * Matrix4x4.CreateLookAt(Vector3.Zero, Forward, Up);

    /// <summary>
    /// The near-plane rule: a quarter of the eye's height above the terrain (or of the distance, when smaller), 0.5 .. <see cref="MaxNear"/>.
    /// </summary>
    public float Near => Math.Clamp(Math.Min(EyeClearance, Distance) * 0.25f, 0.5f, MaxNear);

    /// <summary>
    /// The scene projection's near plane. Reversed (Vulkan): where the former GL-mapped clip volume (-1..1 over a 0..1 depth matrix) clipped a
    /// <see cref="Near"/> projection, <c>n f / (2 f - n)</c> with f = <see cref="ViewDistance"/>, about half of it; the reversed float depth
    /// keeps its precision there. Conventional (Metal, whose clip volume was always 0..1): <see cref="Near"/>. <see cref="Near"/> stays the
    /// rule for the shadows' fit and the close particles.
    /// </summary>
    public float ClipNear => DepthConvention.Reversed ? Near * ViewDistance / (2 * ViewDistance - Near) : Near;

    /// <summary>
    /// The farthest the near plane may sit (200; the Meitou title lowers it to draw its wanderer close in front of a high eye).
    /// </summary>
    public float MaxNear { get; set; } = 200;

    /// <summary>
    /// The scene projection in <see cref="DepthConvention"/> (reversed on Vulkan: depth 1 at the near plane, 0 at the far one).
    /// </summary>
    public Matrix4x4 Projection(float aspect, float near, float far) =>
        ViewWindow.Current.Apply(DepthConvention.Perspective(FieldOfView, aspect, near, far));

    /// <summary>
    /// Rotates the eye around the target.
    /// </summary>
    public void Orbit(float dx, float dy)
    {
        Yaw -= dx * 0.005f;
        Pitch = Math.Clamp(Pitch + dy * 0.005f, -1.5f, 1.55f);
    }

    /// <summary>
    /// Turns the view around the eye (the target moves).
    /// </summary>
    public void Look(float dx, float dy)
    {
        var eye = Eye;
        Yaw -= dx * 0.004f;
        Pitch = Math.Clamp(Pitch + dy * 0.004f, -1.5f, 1.55f);
        Target = eye - Back * Distance;
    }

    /// <summary>
    /// Moves the eye towards the target (positive <paramref name="steps"/>) or away, 15% a step, keeping the distance 5 .. <see
    /// cref="ViewDistance"/>.
    /// </summary>
    public void Zoom(float steps) => Distance = Math.Clamp(Distance * MathF.Pow(0.85f, steps), 5f, ViewDistance);

    /// <summary>
    /// Moves eye and target: <paramref name="forward"/> along the view projected on the ground, <paramref name="right"/> sideways, <paramref
    /// name="up"/> vertically.
    /// </summary>
    public void Fly(float forward, float right, float up)
    {
        var f = Forward;
        var ground = new Vector3(f.X, 0, f.Z);
        ground = ground.LengthSquared() < 1e-6f ? new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw)) : Vector3.Normalize(ground);
        var side = Vector3.Normalize(Vector3.Cross(ground, Vector3.UnitY));
        Target += ground * forward + side * right + Vector3.UnitY * up;
    }

    /// <summary>
    /// Free flight: moves eye and target <paramref name="forward"/> along the view direction (pitch included), <paramref name="right"/> sideways,
    /// <paramref name="up"/> along world Y.
    /// </summary>
    public void FlyFree(float forward, float right, float up)
    {
        var f = Forward;
        var flat = new Vector3(f.X, 0, f.Z);
        flat = flat.LengthSquared() < 1e-6f ? new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw)) : Vector3.Normalize(flat);
        var side = Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitY));
        Target += f * forward + side * right + Vector3.UnitY * up;
    }

    /// <summary>
    /// The six frustum planes (normal pointing inwards, d) of a view-projection matrix.
    /// </summary>
    public static Vector4[] FrustumPlanes(Matrix4x4 m)
    {
        // System.Numerics uses row vectors: clip = p * M, so the planes come from the matrix columns.
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);

        // Clip volume 0 <= z <= w for any 0..1 depth matrix: the same two planes (c3, c4 - c3) for the standard and the reversed
        // one (only which of them is the near plane swaps).
        return [c4 + c1, c4 - c1, c4 + c2, c4 - c2, c3, c4 - c3];
    }

    /// <summary>
    /// Whether an axis-aligned box is at least partly inside the planes.
    /// </summary>
    public static bool Intersects(Vector4[] planes, Vector3 min, Vector3 max)
    {
        foreach (var p in planes)
        {
            var v = new Vector3(p.X >= 0 ? max.X : min.X, p.Y >= 0 ? max.Y : min.Y, p.Z >= 0 ? max.Z : min.Z);
            if (p.X * v.X + p.Y * v.Y + p.Z * v.Z + p.W < 0) return false;
        }
        return true;
    }
}