using System.Numerics;

namespace Meitou.ModelViewer;

/// <summary>Orbit camera around a target, Y up (Ogre's convention).</summary>
public sealed class Camera
{
    public Vector3 Target { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Distance { get; set; } = 10;
    public float ModelRadius { get; set; } = 1;
    public float FieldOfView { get; set; } = 40 * MathF.PI / 180;

    /// <summary>Frames a bounding sphere: the whole sphere fits the vertical field of view.</summary>
    public void Frame(Vector3 center, float radius, float yawDegrees = 35, float pitchDegrees = 20)
    {
        Target = center;
        ModelRadius = radius;
        Yaw = yawDegrees * MathF.PI / 180;
        Pitch = pitchDegrees * MathF.PI / 180;
        Distance = radius / MathF.Sin(FieldOfView / 2) * 1.05f;
    }

    public Vector3 Eye
    {
        get
        {
            var dir = new Vector3(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));
            return Target + dir * Distance;
        }
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);

    public Matrix4x4 Projection(float aspect)
    {
        float near = Math.Max(Distance - ModelRadius * 4, Distance * 0.01f);
        near = Math.Max(near, 1e-3f);
        float far = Distance + ModelRadius * 30;
        return Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, near, far);
    }

    public void Orbit(float dx, float dy)
    {
        Yaw -= dx * 0.01f;
        Pitch = Math.Clamp(Pitch + dy * 0.01f, -1.55f, 1.55f);
    }

    public void Zoom(float steps) => Distance = Math.Max(Distance * MathF.Pow(0.88f, steps), ModelRadius * 0.02f);

    /// <summary>Moves the target in the view plane by a pixel delta.</summary>
    public void Pan(float dx, float dy, int viewportHeight)
    {
        Matrix4x4.Invert(View, out var inv);
        var right = new Vector3(inv.M11, inv.M12, inv.M13);
        var up = new Vector3(inv.M21, inv.M22, inv.M23);
        float scale = 2 * Distance * MathF.Tan(FieldOfView / 2) / Math.Max(viewportHeight, 1);
        Target += (-right * dx + up * dy) * scale;
    }
}
