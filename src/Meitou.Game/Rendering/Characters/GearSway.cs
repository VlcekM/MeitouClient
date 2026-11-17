using System.Numerics;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The <c>sway</c> switch (Meitou, optional, off): the weapons, sheaths and crossbows hanging at the hip or on the back, and a backpack, swing behind
/// the body's motion instead of following it rigidly (docs/character-renderer.md "Gear sway"). The original attaches the first to a bone at a fixed
/// offset and skins the second to the spine, and never moves either again. Drawing only: the simulation never sees it.
/// </summary>
static class GearSway
{
    /// <summary>
    /// The switch is on.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>
    /// How a hanging mass swings: its tip's spring toward where the pose holds it (per second squared), the damping of the tip's speed relative to
    /// the posed tip's (per second), the farthest it turns from its posed direction (radians), whether the wearer's body stops it on one side
    /// (<see cref="Update"/>'s wall), and then the farthest it turns away from the body (radians).
    /// </summary>
    public readonly record struct Spring(float Stiffness, float Damping, float MaxAngle, bool Backed, float Outward);

    /// <summary>
    /// A weapon, sheath or crossbow on its point: natural frequency 1.6 Hz, damping ratio 0.3 (a swing dies out in about a second), 22 degrees at
    /// most along the body, never past its posed place towards the wearer (swinging both ways, a long weapon on the back went through the back and
    /// the legs) and 6 degrees at most away from it: a weapon held near its middle turns its other end into the wearer by as much.
    /// </summary>
    public static readonly Spring Weapon = new(101f, 6f, 0.384f, true, 0.105f);

    /// <summary>
    /// A backpack on its straps: tighter than a weapon on a belt (2.2 Hz, damping ratio 0.45: one overshoot), 7 degrees at most, and never into
    /// the wearer's back.
    /// </summary>
    public static readonly Spring Pack = new(191f, 12.4f, 0.122f, true, 0.122f);

    /// <summary>
    /// The longest integration step (seconds): the spring is stepped at least 120 times a second of game time, whatever the frame rate.
    /// </summary>
    const float Step = 1f / 120;

    /// <summary>
    /// A gap longer than this (seconds of game time, e.g. back on screen after a while, or a load) starts the gear at rest instead of integrating.
    /// </summary>
    const float LongGap = 0.25f;

    /// <summary>
    /// A pivot that moved farther than this many lever lengths in one update has been placed (a teleport, a carry, a new look): the gear starts at
    /// rest there.
    /// </summary>
    const float JumpLevers = 6;

    /// <summary>
    /// Characters smaller than this on screen (pixels tall) are not swayed: the swing would not show (their gear is drawn as posed).
    /// </summary>
    public const float MinPixels = 40;

    /// <summary>
    /// One point's hanging gear: the tip's place and speed in the world, the pivot and posed tip at the last update, and whether they hold.
    /// </summary>
    public struct State
    {
        public Vector3 Tip, Velocity, Pivot, Rest;
        public bool Valid;
    }

    /// <summary>
    /// Steps one point's gear from the last update to this one (<paramref name="dt"/> seconds of game time) and returns its swing as a rotation in
    /// the point's own frame (identity at rest).
    /// </summary>
    /// <param name="point">The attachment point's posed transform in the world (its frame, at the pivot).</param>
    /// <param name="lever">From the pivot to the gear's centre, in the point's frame.</param>
    /// <param name="wall">For a <see cref="Spring.Backed"/> spring: the unit direction in the world from the gear to the body it rests against; the
    /// tip never passes its posed place that way.</param>
    public static Quaternion Update(ref State s, in Matrix4x4 point, Vector3 lever, float dt, in Spring spring, Vector3 wall = default)
    {
        var pivot = point.Translation;
        var arm = Vector3.TransformNormal(lever, point);
        float length = arm.Length();
        if (length < 1e-4f || !Matrix4x4.Invert(point, out var toPoint)) return Quaternion.Identity;
        var rest = pivot + arm;

        if (!s.Valid || !(dt >= 0) || dt > LongGap || Vector3.Distance(pivot, s.Pivot) > JumpLevers * length)
        {
            s = new State { Tip = rest, Pivot = pivot, Rest = rest, Valid = true };
            return Quaternion.Identity;
        }
        if (dt == 0) return Swing(s, pivot, toPoint, lever);   // posed again with no time gone (paused): as it hangs

        int steps = Math.Max(1, (int)MathF.Ceiling(dt / Step));
        float h = dt / steps, limit = MathF.Cos(spring.MaxAngle);
        var restSpeed = (rest - s.Rest) / dt;
        for (int k = 1; k <= steps; k++)
        {
            float f = (float)k / steps;
            var p = Vector3.Lerp(s.Pivot, pivot, f);
            var target = Vector3.Lerp(s.Rest, rest, f);

            // A damped spring toward the posed tip, damped against the posed tip's own speed so steady walking leaves no lag.
            s.Velocity += (spring.Stiffness * (target - s.Tip) + spring.Damping * (restSpeed - s.Velocity)) * h;
            s.Tip += s.Velocity * h;

            // On the sphere of the lever's length round the pivot, no farther than the spring's angle from the posed direction.
            var restWay = Vector3.Normalize(target - p);
            var way = s.Tip - p;
            way = way.LengthSquared() > 1e-10f ? Vector3.Normalize(way) : restWay;
            if (spring.Backed)
            {
                // The body is in the way: the tip stops at its posed place on that side, and keeps no speed into it; away from the body it stops at
                // the spring's outward angle.
                float into = Vector3.Dot(way - restWay, wall), away = -MathF.Sin(spring.Outward);
                if (into > 0 || into < away)
                {
                    way -= wall * (into > 0 ? into : into - away);
                    way = way.LengthSquared() > 1e-10f ? Vector3.Normalize(way) : restWay;
                    float speedInto = Vector3.Dot(s.Velocity - restSpeed, wall);
                    if (into > 0 ? speedInto > 0 : speedInto < 0) s.Velocity -= wall * speedInto;
                }
            }
            float cos = Vector3.Dot(way, restWay);
            if (cos < limit)
            {
                var side = way - restWay * cos;
                side = side.LengthSquared() > 1e-10f ? Vector3.Normalize(side) : Vector3.Zero;
                way = restWay * limit + side * MathF.Sin(spring.MaxAngle);
                s.Velocity = restSpeed + (s.Velocity - restSpeed) * 0.5f;   // a soft stop, no bounce off the limit
            }
            s.Tip = p + way * length;
            var relative = s.Velocity - restSpeed;
            s.Velocity -= way * Vector3.Dot(relative, way);   // no speed along the lever
        }
        (s.Pivot, s.Rest) = (pivot, rest);
        return Swing(s, pivot, toPoint, lever);
    }

    /// <summary>
    /// The swing in the point's frame (<paramref name="toPoint"/>: world to point): from the lever's direction to where the tip hangs now.
    /// </summary>
    static Quaternion Swing(in State s, Vector3 pivot, in Matrix4x4 toPoint, Vector3 lever)
    {
        var now = Vector3.TransformNormal(s.Tip - pivot, toPoint);
        return now.LengthSquared() > 1e-10f ? FromTo(Vector3.Normalize(lever), Vector3.Normalize(now)) : Quaternion.Identity;
    }

    /// <summary>
    /// The shortest rotation taking unit vector <paramref name="a"/> to unit vector <paramref name="b"/>.
    /// </summary>
    static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        var axis = Vector3.Cross(a, b);
        if (axis.LengthSquared() < 1e-12f) return Quaternion.Identity;   // opposite: never within the spring's angle
        return Quaternion.Normalize(new Quaternion(axis, 1 + d));
    }

    /// <summary>
    /// The lever a hanging mass is stepped with: along <paramref name="lever"/> (from its pivot to its centre), as long as the simple pendulum
    /// that swings like it. The mass is not a point at its centre: a long thing held near its middle hardly swings, and stepped as a point on
    /// that short lever it see-sawed at the least motion. With k its radius of gyration about its centre (a rod's: 0.58 of
    /// <paramref name="radius"/>, half its length) the length is (k² + l²) / l.
    /// </summary>
    public static Vector3 Pendulum(Vector3 lever, float radius)
    {
        float l = lever.Length();
        if (l < 1e-4f) return Vector3.Zero;
        float k = 0.58f * radius;
        return lever * ((k * k + l * l) / (l * l));
    }

    #region Keeping gear out of the wearer

    /// <summary>
    /// The farthest gear is turned out of the wearer's limbs (radians, 40 degrees).
    /// </summary>
    const float MaxPush = 0.7f;

    /// <summary>
    /// The turns tried up to <see cref="MaxPush"/>.
    /// </summary>
    const int PushSteps = 14;

    /// <summary>
    /// The shortest distance between the segments a0-a1 and b0-b1.
    /// </summary>
    static float SegmentDistance(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1)
    {
        Vector3 u = a1 - a0, v = b1 - b0, w = a0 - b0;
        float a = Vector3.Dot(u, u), b = Vector3.Dot(u, v), c = Vector3.Dot(v, v), d = Vector3.Dot(u, w), e = Vector3.Dot(v, w);
        float det = a * c - b * b, s, t;
        if (a < 1e-10f) s = 0;
        else if (det < 1e-10f) s = Math.Clamp(-d / a, 0, 1);
        else s = Math.Clamp((b * e - c * d) / det, 0, 1);
        t = c < 1e-10f ? 0 : Math.Clamp((b * s + e) / c, 0, 1);
        if (a >= 1e-10f) s = Math.Clamp((b * t - d) / a, 0, 1);
        return ((a0 + u * s) - (b0 + v * t)).Length();
    }

    /// <summary>
    /// How far the gear's span (<paramref name="from"/> to <paramref name="to"/>, model space, <paramref name="thickness"/> its half thickness)
    /// stands clear of the wearer's limbs as posed (<paramref name="skin"/>: the palette), negative inside one. A limb on the gear's own bone
    /// (<paramref name="ownBone"/>) does not count: the gear is fixed to it.
    /// </summary>
    /// <param name="side">From the span to one edge of the gear (half its width, across its flat): both edges are kept clear as the middle is. A
    /// blade is thin and wide: with its middle alone it stood clear between the two thighs, its edges in them.</param>
    public static float Clearance(ReadOnlySpan<SwayCollider> limbs, ReadOnlySpan<Matrix4x4> skin, int ownBone, Vector3 from, Vector3 to,
        Vector3 side, float thickness)
    {
        float clear = float.MaxValue;
        foreach (ref readonly var limb in limbs)
        {
            if (limb.Bone == ownBone || (uint)limb.Bone >= (uint)skin.Length) continue;
            ref readonly var m = ref skin[limb.Bone];
            float scale = Vector3.TransformNormal(Vector3.UnitX, m).Length();
            Vector3 a = Vector3.Transform(limb.From, m), b = Vector3.Transform(limb.To, m);
            float away = MathF.Min(SegmentDistance(from, to, a, b),
                MathF.Min(SegmentDistance(from + side, to + side, a, b), SegmentDistance(from - side, to - side, a, b)));
            clear = MathF.Min(clear, away - limb.Radius * scale - thickness);
        }
        return clear;
    }

    /// <summary>
    /// The least turn about <paramref name="axis"/> through <paramref name="pivot"/> (radians, 0 to <see cref="MaxPush"/>) that takes the gear's
    /// span out of the wearer's limbs, to a clearance of <paramref name="floor"/> (what it has in the binding pose, at most 0: what the gear's
    /// placement means to have). The turn with the most clearance when none reaches it.
    /// </summary>
    public static float PushAngle(ReadOnlySpan<SwayCollider> limbs, ReadOnlySpan<Matrix4x4> skin, int ownBone, Vector3 pivot, Vector3 from,
        Vector3 to, Vector3 side, Vector3 axis, float thickness, float floor)
    {
        if (Turned(limbs, skin, ownBone, pivot, from, to, side, axis, thickness, 0) >= floor) return 0;
        float last = 0, best = 0, bestClear = float.MinValue;
        for (int k = 1; k <= PushSteps; k++)
        {
            float angle = MaxPush * k / PushSteps, clear = Turned(limbs, skin, ownBone, pivot, from, to, side, axis, thickness, angle);
            if (clear >= floor)
            {
                // Between the last turn still inside and this one: halved four times.
                float low = last, high = angle;
                for (int i = 0; i < 4; i++)
                {
                    float mid = (low + high) / 2;
                    if (Turned(limbs, skin, ownBone, pivot, from, to, side, axis, thickness, mid) >= floor) high = mid; else low = mid;
                }
                return high;
            }
            if (clear > bestClear) (best, bestClear) = (angle, clear);
            last = angle;
        }
        return best;
    }

    /// <summary>
    /// <see cref="Clearance"/> of the span turned by <paramref name="angle"/> about <paramref name="axis"/> through <paramref name="pivot"/>.
    /// </summary>
    static float Turned(ReadOnlySpan<SwayCollider> limbs, ReadOnlySpan<Matrix4x4> skin, int ownBone, Vector3 pivot, Vector3 from, Vector3 to,
        Vector3 side, Vector3 axis, float thickness, float angle)
    {
        var q = Quaternion.CreateFromAxisAngle(axis, angle);
        return Clearance(limbs, skin, ownBone, pivot + Vector3.Transform(from - pivot, q), pivot + Vector3.Transform(to - pivot, q),
            Vector3.Transform(side, q), thickness);
    }

    #endregion

    #region Skinned gear

    /// <summary>
    /// Where a worn mesh's hanging mass is told from its straps, in the mesh's space (the wearer's binding pose, +Z its front), for a skeleton of
    /// <see cref="Bones"/> bones: a vertex in front of <see cref="FrontZ"/> lies on the wearer and stays; one behind <see cref="BackZ"/> hangs and
    /// swings whole, with a smooth share between the two (a strap going over the shoulder stretches); above <see cref="TopY"/> nothing lies on the
    /// wearer (a frame's canopy over the head), so everything there swings whole.
    /// </summary>
    public readonly record struct Bake(int Bones, float FrontZ, float BackZ, float TopY);

    /// <summary>
    /// A baked mesh's hanging mass, in the mesh's space: the bone that carries most of it, the pivot it swings about (on the wearer's back, at the
    /// height its straps go over the shoulders) and the lever from the pivot to its centre.
    /// </summary>
    public readonly record struct Mass(int Bone, Vector3 Pivot, Vector3 Lever);

    /// <summary>
    /// The least share of a mesh's skinned vertices that must hang for it to swing: a coat in the backpack slot wraps the wearer.
    /// </summary>
    const float MinHanging = 0.5f;

    /// <summary>
    /// The least share of the hanging mass's weight its two main bones must hold: a pack rides the chest; a cloth down the back and the legs is
    /// spread over the spine, the pelvis and the thighs, and bends with them.
    /// </summary>
    const float MinCarried = 0.75f;

    /// <summary>
    /// The shortest lever (mesh units) worth swinging.
    /// </summary>
    const float MinLever = 1;

    /// <summary>
    /// Makes a worn mesh's hanging mass swing: each vertex's bones are split by its hanging share between the wearer's own (handles below
    /// <see cref="Bake.Bones"/>) and their swung copies (the same handles plus <see cref="Bake.Bones"/>), which the renderer poses as the bones
    /// turned about the mass's pivot (<c>CharacterRenderer.Sway</c>). With the copies equal to the bones the mesh is skinned as the file has it.
    /// A vertex has four bone slots: one on three bones or more takes the nearer of all or nothing. Null, and the mesh untouched, when it is no
    /// hanging mass (<see cref="MinHanging"/>, <see cref="MinCarried"/>, <see cref="MinLever"/>) or the copies' handles would not fit a byte.
    /// </summary>
    public static Mass? BakeMesh(IReadOnlyList<ModelPart> parts, in Bake bake)
    {
        if (bake.Bones is <= 0 or > 128 || !(bake.FrontZ > bake.BackZ)) return null;
        var shares = new float[parts.Count][];
        var carried = new double[bake.Bones];
        int skinned = 0, hanging = 0;
        var centre = Vector3.Zero;
        float strapTop = float.MinValue, top = float.MinValue;
        for (int p = 0; p < parts.Count; p++)
        {
            var vertices = parts[p].Vertices;
            shares[p] = new float[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                ref readonly var v = ref vertices[i];
                int influences = 0;
                for (int k = 0; k < 4; k++)
                {
                    if (!(v.Weights[k] > 0)) continue;
                    if (((v.Bones >> (8 * k)) & 0xFF) >= bake.Bones) return null;
                    influences++;
                }
                if (influences == 0) continue;
                skinned++;
                float t = Math.Clamp((bake.FrontZ - v.Position.Z) / (bake.FrontZ - bake.BackZ), 0, 1);
                float share = v.Position.Y > bake.TopY ? 1 : t * t * (3 - 2 * t);
                if (share is > 0 and < 1)
                {
                    if (influences > 2) share = share >= 0.5f ? 1 : 0;
                    else strapTop = MathF.Max(strapTop, v.Position.Y);
                }
                shares[p][i] = share;
                if (share < 0.5f) continue;
                hanging++;
                centre += v.Position;
                top = MathF.Max(top, v.Position.Y);
                for (int k = 0; k < 4; k++)
                    if (v.Weights[k] > 0) carried[(v.Bones >> (8 * k)) & 0xFF] += v.Weights[k];
            }
        }
        if (hanging == 0 || hanging < MinHanging * skinned) return null;

        int first = 0, second = -1;
        for (int b = 1; b < carried.Length; b++)
        {
            if (carried[b] > carried[first]) (first, second) = (b, first);
            else if (second < 0 || carried[b] > carried[second]) second = b;
        }
        if (carried[first] + (second >= 0 ? carried[second] : 0) < MinCarried * carried.Sum()) return null;

        centre /= hanging;
        var pivot = new Vector3(centre.X, strapTop > float.MinValue ? strapTop : MathF.Min(top, bake.TopY), bake.BackZ);
        var lever = centre - pivot;
        if (lever.Length() < MinLever) return null;

        Span<(uint Bone, float Weight)> slots = stackalloc (uint, float)[8];
        for (int p = 0; p < parts.Count; p++)
        {
            var vertices = parts[p].Vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                float share = shares[p][i];
                if (share <= 0) continue;
                ref var v = ref vertices[i];
                int n = 0;
                for (int k = 0; k < 4; k++)
                {
                    float weight = v.Weights[k];
                    if (!(weight > 0)) continue;
                    uint bone = (v.Bones >> (8 * k)) & 0xFF;
                    if (share < 1) slots[n++] = (bone, weight * (1 - share));
                    slots[n++] = (bone + (uint)bake.Bones, weight * share);
                }
                (v.Bones, v.Weights) = (0, Vector4.Zero);
                for (int k = 0; k < Math.Min(n, 4); k++)
                {
                    v.Bones |= slots[k].Bone << (8 * k);
                    v.Weights[k] = slots[k].Weight;
                }
            }
        }
        return new Mass(first, pivot, lever);
    }

    #endregion
}
