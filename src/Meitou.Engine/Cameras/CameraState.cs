using System.Numerics;
using Meitou.Engine.Time;

namespace Meitou.Engine.Cameras;

/// <summary>
/// One tick's camera, in the viewer's conventions (Y up, right handed; <c>tools/Meitou.ModelViewer/WorldCamera.cs</c>): the eye is
/// <c>Target + (cos(Pitch) sin(Yaw), sin(Pitch), cos(Pitch) cos(Yaw)) · Distance</c>, so yaw 0 looks along -Z and pitch is radians
/// of the eye above the horizon as seen from the target (a view 30 degrees down is pitch +30 degrees). A host copies
/// <see cref="Target"/>, <see cref="Yaw"/>, <see cref="Pitch"/> and <see cref="Distance"/> to a <c>WorldCamera</c> and gets the same
/// <see cref="Eye"/>; the free camera reports a <see cref="Distance"/> of its own choosing (the strategy boom it will return to)
/// with <see cref="Target"/> that far along the view.
/// </summary>
public readonly record struct CameraState(
    Vector3 Eye, Vector3 Forward, Vector3 Up, Vector3 Target, float Yaw, float Pitch, float Distance, bool IsFree)
{
    /// <summary>Right handed look-at view matrix (as <c>Matrix4x4.CreateLookAt</c>).</summary>
    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Eye + Forward, Up);

    /// <summary>
    /// Between two ticks' states: positions and the distance linearly, yaw and pitch along the shortest arc, the view direction
    /// by its normalised blend. A change of mode (<see cref="IsFree"/>) is a cut: the later state is returned.
    /// </summary>
    public static CameraState Lerp(CameraState a, CameraState b, float t)
    {
        if (a.IsFree != b.IsFree) return b;
        var forward = Vector3.Lerp(a.Forward, b.Forward, t);
        forward = forward.LengthSquared() < 1e-12f ? b.Forward : Vector3.Normalize(forward);
        return new CameraState(
            Vector3.Lerp(a.Eye, b.Eye, t), forward, Vector3.UnitY, Vector3.Lerp(a.Target, b.Target, t),
            Interp.LerpAngle(a.Yaw, b.Yaw, t), Interp.LerpAngle(a.Pitch, b.Pitch, t), Interp.Lerp(a.Distance, b.Distance, t), b.IsFree);
    }

    /// <summary>The eye for yaw, pitch and distance round a target (the formula in the type's summary).</summary>
    public static Vector3 EyeFor(Vector3 target, float yaw, float pitch, float distance) =>
        target + new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)) * distance;
}
