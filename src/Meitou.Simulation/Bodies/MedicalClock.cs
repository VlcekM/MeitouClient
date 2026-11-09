using System.Numerics;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The camera the world is looked at through, passed by the host once a frame (nothing is on screen without it, as in a headless world): where the
/// eye is, which way it looks, the vertical field of view and the aspect ratio, and the NPC view range (<c>npc range</c>) beyond which a character is
/// not shown.
/// </summary>
public sealed record CameraViewCommand(Vector3 Eye, Vector3 Forward, float FovY, float Aspect, float NpcRange) : SimCommand;

/// <summary>
/// The view test of the original's update flag (<c>Character +0x1a9</c>, <c>FUN_1405c9f60</c>): within the NPC range of the eye and inside the view
/// frustum (<b>Observed</b>; the original's building and town refinements are not modelled). Read by <see cref="Bodies.MedicalTimeBudget"/> (the
/// update flag) and the package sensing hooks (<c>AggroSystem.HookRuns</c>).
/// </summary>
public static class MedicalClock
{
    /// <summary>
    /// The size of the sphere tested against the frustum (engine choice: about a standing body).
    /// </summary>
    public const float BodyRadius = 100;

    /// <summary>
    /// Whether a point's body is inside the view: within the range of the eye and the frustum.
    /// </summary>
    public static bool OnScreen(CameraViewCommand view, Vector3 at)
    {
        var v = at + new Vector3(0, BodyRadius, 0) - view.Eye;
        if (v.LengthSquared() > view.NpcRange * view.NpcRange) return false;
        var forward = Vector3.Normalize(view.Forward);
        var right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared() < 1e-8f) right = Vector3.UnitX;
        right = Vector3.Normalize(right);
        var up = Vector3.Cross(right, forward);
        float z = Vector3.Dot(v, forward);
        float tanY = MathF.Tan(view.FovY / 2), tanX = tanY * view.Aspect;
        float slackY = BodyRadius * MathF.Sqrt(1 + tanY * tanY), slackX = BodyRadius * MathF.Sqrt(1 + tanX * tanX);
        return z > -BodyRadius && MathF.Abs(Vector3.Dot(v, up)) <= z * tanY + slackY && MathF.Abs(Vector3.Dot(v, right)) <= z * tanX + slackX;
    }
}