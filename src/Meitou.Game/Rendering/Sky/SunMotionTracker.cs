using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The sun's own motion in the temporal upscalers' motion vectors, shared by the Vulkan chain (<see cref="PostProcess"/>) and the seam's Metal chain
/// (<see cref="PostTaa"/>). The sky's motion is the camera's alone, so while the clock runs and the camera stands still the upscalers read the sun
/// disc and its glow as unmoving and blend last frame's sun into this frame's. The sky pixels round the sun also get the sun's motion across the
/// screen since the last frame.
/// </summary>
sealed class SunMotionTracker
{
    /// <summary>
    /// The angles from the sun's centre (degrees) within which the sky takes all of the sun's motion, and past which none.
    /// </summary>
    const float InnerAngle = 10, OuterAngle = 16;

    /// <summary>
    /// A larger step of the sun in one frame (degrees) is a jump of the clock, not a motion to follow.
    /// </summary>
    const float JumpAngle = 2;

    /// <summary>
    /// The sun direction of the last frame.
    /// </summary>
    Vector3 _previous;

    /// <summary>
    /// <see cref="_previous"/> holds a sun (false after <see cref="Reset"/>).
    /// </summary>
    bool _previousValid;

    /// <summary>
    /// Forgets the previous sun (a camera cut or a teleport must not turn the old direction into a motion streak).
    /// </summary>
    public void Reset() => _previousValid = false;

    /// <summary>
    /// This frame's sun uniforms: xyz the direction to the sun, w cos <see cref="InnerAngle"/>; and xy the motion to add (the motion target's units),
    /// z cos <see cref="OuterAngle"/>, w 1 when on.
    /// <paramref name="enabled"/> is the user's sun-motion switch; <paramref name="framePreviousValid"/> says whether the
    /// previous frame can be read at all (a temporal history exists).
    /// </summary>
    public (Vector4 Sun, Vector4 Motion) Prepare(FrameGlobals globals, bool enabled, bool framePreviousValid,
        Matrix4x4 viewRotation, float fov, float aspect)
    {
        var raw = ReadGlobal<Vector4>(globals, "uAtmoSun");
        var sun = new Vector3(raw.X, raw.Y, raw.Z);
        if (sun.LengthSquared() < 1e-6f)
        {
            _previousValid = false;
            return default;
        }
        sun = Vector3.Normalize(sun);
        var result = default((Vector4 Sun, Vector4 Motion));
        if (enabled && _previousValid && framePreviousValid
            && Vector3.Dot(sun, _previous) is float c && c < 1 && c > MathF.Cos(JumpAngle * MathF.PI / 180)
            && Project(sun, viewRotation, fov, aspect) is { } now
            && Project(_previous, viewRotation, fov, aspect) is { } before)
        {
            // Both with this frame's camera: the camera's own motion is the reprojection's part already.
            var delta = (now - before) * 0.5f;
            result = (new Vector4(sun, MathF.Cos(InnerAngle * MathF.PI / 180)),
                new Vector4(delta, MathF.Cos(OuterAngle * MathF.PI / 180), 1));
        }
        (_previous, _previousValid) = (sun, true);
        return result;
    }

    /// <summary>
    /// A direction's place on the screen (NDC) with this frame's camera, or null when it is not well in front.
    /// </summary>
    static Vector2? Project(Vector3 direction, Matrix4x4 rotation, float fov, float aspect)
    {
        float x = Vector3.Dot(direction, new Vector3(rotation.M11, rotation.M21, rotation.M31));
        float y = Vector3.Dot(direction, new Vector3(rotation.M12, rotation.M22, rotation.M32));
        float z = -Vector3.Dot(direction, new Vector3(rotation.M13, rotation.M23, rotation.M33));
        if (z < 0.05f) return null;
        float tanY = MathF.Tan(fov * 0.5f);
        return ViewWindow.Current.ToTile(new Vector2(x / (z * tanY * aspect), y / (z * tanY)));
    }

    /// <summary>
    /// Reads a published uniform's value from the frame globals (default when not published).
    /// </summary>
    static T ReadGlobal<T>(FrameGlobals globals, string name) where T : unmanaged
    {
        T value = default;
        globals.UniformValue(name)?.TryRead(MemoryMarshal.AsBytes(new Span<T>(ref value)));
        return value;
    }
}