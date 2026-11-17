using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Simulation;

namespace Meitou.Game.Host;

/// <summary>
/// The drawn head turning (docs/animation.md "Head turning"): the original plays the ANIMATION <c>head turning</c> as an overlay held at a clip time
/// that maps the angle from the body's facing to the looked-at point (-90 to +90 degrees) onto the clip, while a task makes its character look
/// at something (<see cref="CharacterSnapshot.LookAt"/>). Kept here per character slot between frames; nothing goes back to the simulation.
/// </summary>
sealed class HeadTurning
{
    /// <summary>
    /// The ANIMATION record (and clip) played.
    /// </summary>
    public const string Record = "head turning";

    /// <summary>
    /// The clip time at +90 degrees (<c>FUN_1405b8430</c>: (angle + pi/2) / pi times this constant, <c>DAT_1416c0efc</c>, <b>Verified</b>); the
    /// base clip is 0.0667 s long, so the last 5 % hold its last key.
    /// </summary>
    const float TimeSpan = 0.07f;

    /// <summary>
    /// The last time drawn: just short of the base clip's length (the renderer wraps a time at the length round to the start, the original clamps).
    /// </summary>
    const float LastTime = 0.0666f;

    /// <summary>
    /// One character's head layer: whether it is held (requested and in range), its clip time and weight, its id and the clock it was last stepped
    /// at.
    /// </summary>
    struct Head
    {
        public CharacterId Id;
        public bool Held;
        public float Time, Weight;
        public double Clock;
    }

    Head[] _heads = [];

    /// <summary>
    /// The head layer of character <paramref name="c"/> drawn at <paramref name="position"/> facing <paramref name="yaw"/>, at simulation clock
    /// <paramref name="clock"/> (seconds); null when it has none. <paramref name="blendRate"/> is the CONSTANTS <c>animation blend rate</c>.
    /// </summary>
    /// <param name="lookAt">The point looked at between the ticks (null: no task looks this tick).</param>
    /// <param name="released">The layer fades out whatever the task (a body down, carried or in a ragdoll; or the Fixes option turning the head
    /// back once nothing is looked at).</param>
    public CharacterPose? Pose(CharacterSnapshot c, Vector3? lookAt, Vector3 position, float yaw, double clock, float blendRate, bool released)
    {
        int slot = c.Id.Slot;
        if (slot >= _heads.Length) Array.Resize(ref _heads, Math.Max(slot + 1, _heads.Length * 2));
        ref var h = ref _heads[slot];
        if (h.Id != c.Id) h = new Head { Id = c.Id, Clock = clock };

        // Game seconds since the last frame (none for a clock that went back: a load). A long gap (out of reach a while) moves the weight as far as
        // the original's blend would have gone meanwhile; the weight only ramps, so a large step cannot overshoot.
        float dt = (float)Math.Max(clock - h.Clock, 0);
        h.Clock = clock;

        // FUN_1405b8430 each time a task calls it: in range the layer is (re)started at the angle's time; out of range it is removed (fades out,
        // its time held). With no call the layer keeps what it had (the original's frame-end cleanup drops a requested one-shot only once its time
        // reaches the clip's end, FUN_1405b3ae0).
        if (released) h.Held = false;
        else if (lookAt is { } at)
        {
            var to = new Vector2(at.X - position.X, at.Z - position.Z);
            if (to.LengthSquared() > 1e-6f)
            {
                float angle = SignedAngle(yaw, Vector2.Normalize(to));
                h.Held = MathF.Abs(angle) <= MathF.PI / 2;
                if (h.Held) h.Time = Math.Min((angle + MathF.PI / 2) / MathF.PI * TimeSpan, LastTime);
            }
        }

        // Overlay manager (rate x 1): a held layer rises at half the rate (its play rate 0 is clamped to 0.5), a removed one falls at the full rate
        // (FUN_1405b1630, FUN_1405b5c30).
        h.Weight = h.Held ? Math.Min(h.Weight + 0.5f * blendRate * dt, 1) : Math.Max(h.Weight - blendRate * dt, 0);
        return h.Weight > 0 ? new CharacterPose(Record, h.Time, h.Weight) : null;
    }

    /// <summary>
    /// The original's head angle (<c>FUN_1405b8430</c>): the angle between the facing and the way to the point, negative when the point is on the
    /// side where facing x way has a positive Y (the point toward +X of a body facing +Z).
    /// </summary>
    internal static float SignedAngle(float yaw, Vector2 way)
    {
        var facing = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
        float angle = MathF.Acos(Math.Clamp(Vector2.Dot(facing, way), -1, 1));
        // Ogre's (forward x way).y with forward = (fx, 0, fz), way = (wx, 0, wz): fz wx - fx wz.
        return facing.Y * way.X - facing.X * way.Y > 0 ? -angle : angle;
    }
}
