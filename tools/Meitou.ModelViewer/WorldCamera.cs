using System.Numerics;

namespace Meitou.ModelViewer;

/// <summary>
/// Camera for the world view, Y up: orbits a target point (left drag), looks around from the eye (right drag),
/// flies with the keyboard. Near and far planes follow the height above the terrain so both close-ups and
/// whole regions keep usable depth precision.
/// </summary>
public sealed class WorldCamera
{
    public Vector3 Target { get; set; }
    /// <summary>Radians; 0 looks along −Z from +Z (the eye sits at +Z of the target).</summary>
    public float Yaw { get; set; }
    /// <summary>Radians above the horizon of the eye, as seen from the target.</summary>
    public float Pitch { get; set; } = 0.5f;
    public float Distance { get; set; } = 2000;
    public float FieldOfView { get; set; } = 50 * MathF.PI / 180;
    /// <summary>Furthest distance anything is drawn at.</summary>
    public float ViewDistance { get; set; } = 60000;
    /// <summary>Height of the eye above the terrain (set by the app each frame), for the near plane.</summary>
    public float EyeClearance { get; set; } = 100;

    Vector3 Back => new(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));

    public Vector3 Eye => Target + Back * Distance;
    public Vector3 Forward => -Back;

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);

    public float Near => Math.Clamp(Math.Min(EyeClearance, Distance) * 0.25f, 0.5f, 200f);

    public Matrix4x4 Projection(float aspect, float near, float far) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, near, far);

    /// <summary>
    /// Where the near and far depth slices meet: the far slice (<see cref="SplitDistance"/> .. <see cref="ViewDistance"/>)
    /// is drawn first, the depth buffer cleared, then the near slice (<see cref="Near"/> .. split), so a 24-bit depth
    /// buffer keeps its precision from a few units out to the horizon.
    /// </summary>
    public float SplitDistance { get; set; } = 20000;

    /// <summary>The depth slices to draw, far first: (near, far) planes.</summary>
    public IEnumerable<(float Near, float Far)> Slices()
    {
        float split = Math.Max(SplitDistance, Near * 4);
        if (ViewDistance > split) yield return (split, ViewDistance);
        yield return (Near, Math.Min(split * 1.02f, ViewDistance));
    }

    /// <summary>Rotates the eye around the target.</summary>
    public void Orbit(float dx, float dy)
    {
        Yaw -= dx * 0.005f;
        Pitch = Math.Clamp(Pitch + dy * 0.005f, -1.5f, 1.55f);
    }

    /// <summary>Turns the view around the eye (the target moves).</summary>
    public void Look(float dx, float dy)
    {
        var eye = Eye;
        Yaw -= dx * 0.004f;
        Pitch = Math.Clamp(Pitch + dy * 0.004f, -1.5f, 1.55f);
        Target = eye - Back * Distance;
    }

    public void Zoom(float steps) => Distance = Math.Clamp(Distance * MathF.Pow(0.85f, steps), 5f, ViewDistance);

    /// <summary>Moves eye and target: <paramref name="forward"/> along the view projected on the ground, <paramref name="right"/> sideways, <paramref name="up"/> vertically.</summary>
    public void Fly(float forward, float right, float up)
    {
        var f = Forward;
        var ground = new Vector3(f.X, 0, f.Z);
        ground = ground.LengthSquared() < 1e-6f ? new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw)) : Vector3.Normalize(ground);
        var side = Vector3.Normalize(Vector3.Cross(ground, Vector3.UnitY));
        Target += ground * forward + side * right + Vector3.UnitY * up;
    }

    /// <summary>The six frustum planes (normal pointing inwards, d) of a view-projection matrix.</summary>
    public static Vector4[] FrustumPlanes(Matrix4x4 m)
    {
        // System.Numerics uses row vectors: clip = p * M, so the planes come from the matrix columns.
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        // CreatePerspectiveFieldOfView maps depth to 0..1, so the near plane is c3 alone.
        return [c4 + c1, c4 - c1, c4 + c2, c4 - c2, c3, c4 - c3];
    }

    /// <summary>Whether an axis-aligned box is at least partly inside the planes.</summary>
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
