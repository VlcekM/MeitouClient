using System.Numerics;
using Meitou.Audio;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Marks from real body contact (docs/render-terrain.md "Ground trails"): each frame the posed body of a character is read as capsules, and where one plants on or slides over the soft
/// ground it presses or ploughs a mark shaped by its contact: a planted foot, paw or hoof prints the shape of its own posed mesh (<see cref="CharacterRenderer.RenderImprints"/>), a body part of a lying body a stadium.
/// </summary>
sealed partial class GameHost
{
    /// <summary>
    /// How far above the ground (units: 5 cm) a capsule's underside still presses a mark: the plane it is cut at to find its contact (a capsule resting on the ground sinks that far in);
    /// and the sink (of 1) under which it only hovers.
    /// </summary>
    const float ContactPlane = 0.5f, MinSink = 0.5f;

    /// <summary>
    /// A part's speed over the ground against its body's: over 1.2 times (+3 units/s) it swings through the air, over 0.4 times (+1 unit/s) it slides, else it is planted (the old rule for a
    /// foot: moving fast, then still). Per second, so the frame rate does not matter.
    /// </summary>
    const float SwingShare = 1.2f, SwingSlack = 3, SlideShare = 0.4f, SlideSlack = 1;

    /// <summary>How deep a slide ploughs against a press of the same sink.</summary>
    const float PloughShare = 0.9f;

    /// <summary>A body that moved further than this between two frames was placed, not moved: its contacts start again.</summary>
    const float Placed = 6;

    /// <summary>A capsule whose underside is this far (units) over the ground under the body's root is left out without a look at the terrain under it.</summary>
    const float NearRoot = 5;

    /// <summary>A part over a lower one of its own body that touches it (their stadia within this much, 0.6 of it across) is left out of an upright body's marks, and of two at one height the shorter: a shin over a foot, a toe in line with it.</summary>
    const float ShadowedBy = 0.15f, ShadowReach = 0.5f;

    /// <summary>An imprint's tile: how far it reaches past the contact's size (the mesh goes beyond its capsule), its least half side, and how far under the ground its band starts.</summary>
    const float ImprintMargin = 2, ImprintLeast = 3, ImprintBelow = 0.4f;
    /// <summary>A print's depth against a full mark's 12 cm: about 7 cm: visible from the game's camera, its slope spread enough to show no stairs.</summary>
    const float ImprintDepth = 0.6f;

    /// <summary>
    /// A part must be off the ground (swinging, sliding or hovering) this long (seconds) before it may print again: the animation's contact phase makes a foot bob a little, which is no new step.
    /// </summary>
    const float LiftTime = 0.15f;

    /// <summary>A new upright print within this share of its length of one of the body's last prints is not made: one footfall, one print (a toe or shin contact, a foot replanted, two feet close).</summary>
    const float PrintGap = 0.6f;

    /// <summary>How far (units) a heel or toe piece may lie from the edge of a sole of the same body and still belong to it, and how much of that sole's length it is at most.</summary>
    const float PieceReach = 0.5f, PieceShare = 0.8f;

    /// <summary>How many of a body's last prints are remembered for <see cref="PrintGap"/>.</summary>
    const int RecentPrints = 8;

    /// <summary>One capsule's state in the last frame: where its middle was, where it last touched the ground, how long it has been off it, and whether it is already printed for this planting.</summary>
    struct Contact
    {
        public Vector2 Middle, Last;
        public float Air;
        public bool Touched, Printed;
    }

    /// <summary>Where a capsule's underside meets the ground: the centre, direction and whole size of the stadium it presses, how hard (0 to 1) and its lowest point.</summary>
    readonly record struct ContactPatch(Vector2 At, Vector2 Along, float Length, float Width, float Sink, float Bottom, bool Valid, bool Whole);

    /// <summary>How long (game seconds) a limb stays planted before it prints, at most: a foot lands on its heel and rolls flat, and what presses is the foot once flat. A capsule lying whole on the ground prints at once.</summary>
    const float SettleTime = 0.12f;

    /// <summary>One limb's footfall (<see cref="BodyCapsule.Limb"/>): one print from landing to lifting, however its heel and toes touch in turn.</summary>
    struct Limb
    {
        public float Planted, Air;
        public bool Down, Printed;
        public ContactPatch Patch;
        public float Depth;
        public Vector3 At;
        public bool Seen;
    }

    readonly List<BodyCapsule> contactBodies = [];
    ContactPatch[] contactPatches = new ContactPatch[64];

    /// <summary>
    /// A character's frame: its posed capsules against the ground. False when it has no pose (it was not drawn), then its path's prints stand in. <paramref name="speed"/> is the body's own
    /// ground speed (units/s); <paramref name="upright"/> bodies (standing, walking, animals) press where a foot or paw plants and never plough.
    /// </summary>
    bool BodyContact(TerrainTrailRenderer trails, GroundSurfaces ground, long key, ref Walker w, Vector3 root, Vector2 heading, bool animal, bool upright, float speed, bool continues)
    {
        contactBodies.Clear();
        gpu.Characters?.ContactCapsules(key, contactBodies);
        int count = contactBodies.Count;
        if (count == 0) return false;
        if (w.Contacts is null || w.Contacts.Length != count) w.Contacts = new Contact[count];
        if (contactPatches.Length < count) contactPatches = new ContactPatch[count];
        bool known = continues && speed * trailDt < Placed;
        float rootGround = gpu.Terrain.HeightAt(root.X, root.Z);
        for (int i = 0; i < count; i++) contactPatches[i] = Contacting(contactBodies[i], rootGround);
        if (upright) Unshadowed(count);
        if (upright)
        {
            int bones = 0;
            foreach (var c in contactBodies) bones = Math.Max(bones, c.Limb + 1);
            if (w.Limbs is null || w.Limbs.Length != bones) w.Limbs = new Limb[bones];
            foreach (ref var l in w.Limbs.AsSpan()) (l.Down, l.Seen) = (false, false);
        }
        for (int i = 0; i < count; i++)
            Touch(trails, ground, key, contactBodies[i], contactPatches[i], ref w, i, animal, upright, speed, known);
        if (upright) Footfalls(trails, ground, key, ref w, animal);
        return true;
    }

    /// <summary>Drops the contacts of an upright body that have a lower one close beside them (a shin's end over the foot it stands on): the foot prints, not the leg.</summary>
    void Unshadowed(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!contactPatches[i].Valid) continue;
            for (int j = 0; j < count; j++)
            {
                if (j == i || !contactPatches[j].Valid) continue;
                if (!Adjacent(contactPatches[i], contactPatches[j])) continue;
                float lower = contactPatches[i].Bottom - contactPatches[j].Bottom;
                bool longer = contactPatches[j].Length > contactPatches[i].Length * 1.05f || (contactPatches[j].Length >= contactPatches[i].Length * 0.95f && j < i);
                if (lower > ShadowedBy || (lower >= -ShadowedBy && longer))
                {
                    contactPatches[i] = contactPatches[i] with { Valid = false };
                    break;
                }
            }
        }
    }

    /// <summary>Whether two contacts of one body touch or overlap, measured along the second one's axis and across it (a toe in line with its foot, a shin over it; not the two feet side by side).</summary>
    static bool Adjacent(in ContactPatch a, in ContactPatch b)
    {
        var d = a.At - b.At;
        float along = MathF.Abs(Vector2.Dot(d, b.Along)), across = MathF.Abs(d.X * b.Along.Y - d.Y * b.Along.X);
        return along < (a.Length + b.Length) / 2 + ShadowReach && across < (a.Width + b.Width) / 2 + ShadowReach * 0.6f;
    }

    /// <summary>
    /// One capsule: how fast it moves over the ground (swinging, sliding or planted); a part planting prints once (never again until it has lifted or slid), a lying body's part sliding ploughs.
    /// </summary>
    void Touch(TerrainTrailRenderer trails, GroundSurfaces ground, long key, in BodyCapsule cap, in ContactPatch patch, ref Walker w, int index, bool animal, bool upright, float bodySpeed, bool known)
    {
        ref var st = ref w.Contacts![index];
        var middle = new Vector2(cap.A.X + cap.B.X, cap.A.Z + cap.B.Z) / 2;
        float speed = known && trailDt > 0 ? Vector2.Distance(middle, st.Middle) / trailDt : 0;
        st.Middle = middle;
        bool swinging = speed > bodySpeed * SwingShare + SwingSlack, sliding = speed > bodySpeed * SlideShare + SlideSlack;
        if (cap.Radius <= 0 || swinging || !patch.Valid)
        {
            st.Touched = false;
            Lifted(ref st);
            return;
        }
        float depth = (patch.Sink - MinSink) / (1 - MinSink) * (0.9f + 0.1f * Math.Clamp(cap.Radius / 2, 0, 1)) * (animal ? 0.9f : 1);
        var at = new Vector3(patch.At.X, patch.Bottom, patch.At.Y);
        var before = st.Last;
        bool was = st.Touched;
        (st.Last, st.Touched) = (patch.At, true);
        if (sliding)
        {
            Lifted(ref st);
            float moved = Vector2.Distance(patch.At, before);
            if (!upright && was && moved > 0.02f)
                Press(trails, ground, at, new TrailStamp((patch.At + before) / 2, (patch.At - before) / moved, moved + patch.Width, patch.Width, depth * PloughShare, TrailKind.Plough));
            return;
        }
        st.Air = 0;
        if (upright)
        {
            // The limb's longest contact stands for it (a foot's sole, not a toe).
            ref var limb = ref w.Limbs![cap.Limb];
            if (!limb.Down || patch.Length > limb.Patch.Length) (limb.Patch, limb.Depth, limb.At) = (patch, depth, at);
            limb.Down = true;
            return;
        }
        if (st.Printed) return;
        st.Printed = true;
        Press(trails, ground, at, new TrailStamp(patch.At, patch.Along, patch.Length, patch.Width, depth, TrailKind.Press));
    }

    /// <summary>
    /// The frame's limbs of an upright body: a limb that has landed prints once, when it has settled (or lies whole on the ground), or as it leaves if it never did; it may print again once it has
    /// been off the ground for <see cref="LiftTime"/> of game time.
    /// </summary>
    void Footfalls(TerrainTrailRenderer trails, GroundSurfaces ground, long key, ref Walker w, bool animal)
    {
        float dt = trailDt * (float)session.Simulation.Speed;
        foreach (ref var l in w.Limbs!.AsSpan())
        {
            if (l.Down)
            {
                l.Air = 0;
                if (l.Printed) continue;
                l.Planted += dt;
                if (l.Planted >= SettleTime || l.Patch.Whole) Print(trails, ground, key, ref w, ref l, animal);
                continue;
            }
            if (l.Planted > 0 && !l.Printed) Print(trails, ground, key, ref w, ref l, animal);
            l.Air += dt;
            if (l.Air >= LiftTime) (l.Printed, l.Planted) = (false, 0);
        }
    }

    void Print(TerrainTrailRenderer trails, GroundSurfaces ground, long key, ref Walker w, ref Limb limb, bool animal)
    {
        limb.Printed = true;
        var print = PrintOf(limb.Patch, limb.Depth, key);
        if (Remember(ref w, print, !animal)) Press(trails, ground, limb.At, print);
    }

    /// <summary>A part that is not planted this frame: once it has been off the ground for <see cref="LiftTime"/> it may print again.</summary>
    void Lifted(ref Contact st)
    {
        st.Air += trailDt;
        if (st.Air >= LiftTime) st.Printed = false;
    }

    /// <summary>
    /// Notes a print among the body's last ones; false (nothing noted) when it is within <see cref="PrintGap"/> of its length of one of them: that footfall already printed. A <paramref name="foot"/>
    /// print under 0.8 of the length of one it touches (within <see cref="PieceReach"/>) is also left out: a heel or toe piece belongs to the sole beside it.
    /// </summary>
    static bool Remember(ref Walker w, in TrailStamp print, bool foot)
    {
        w.Recent ??= new Vector3[RecentPrints];
        foreach (var p in w.Recent)
        {
            if (p.Z <= 0) continue;
            float d = Vector2.Distance(new Vector2(p.X, p.Y), print.Centre);
            if (d < PrintGap * MathF.Max(p.Z, print.Length) || (foot && print.Length < PieceShare * p.Z && d < (p.Z + print.Length) / 2 + PieceReach)) return false;
        }
        w.Recent[w.RecentNext++ % RecentPrints] = new Vector3(print.Centre, print.Length);
        return true;
    }

    /// <summary>The mark a planted limb leaves: the shape of the body's own mesh.</summary>
    TrailStamp PrintOf(in ContactPatch patch, float depth, long key)
    {
        float floor = MathF.Min(patch.Bottom, gpu.Terrain.HeightAt(patch.At.X, patch.At.Y)) - ImprintBelow;
        return new TrailStamp(patch.At, Vector2.UnitY, patch.Length, patch.Width, depth * ImprintDepth, TrailKind.Imprint)
        {
            Body = key, Half = MathF.Max(MathF.Max(patch.Length, patch.Width) / 2 + ImprintMargin, ImprintLeast), Floor = floor,
        };
    }

    /// <summary>
    /// The contact of a capsule with the terrain: the part of its axis whose underside is within <see cref="ContactPlane"/> of the ground (a line between the ground under its ends), cut
    /// there, makes a stadium as wide as the capsule at that depth (at least 0.9 of its radius and 5 cm: flesh is soft); the sink is how far in it is, 1 resting on the ground.
    /// </summary>
    ContactPatch Contacting(in BodyCapsule cap, float rootGround)
    {
        float r = cap.Radius;
        if (r <= 0 || MathF.Min(cap.A.Y, cap.B.Y) - r > rootGround + NearRoot) return default;
        float ca = cap.A.Y - r - gpu.Terrain.HeightAt(cap.A.X, cap.A.Z), cb = cap.B.Y - r - gpu.Terrain.HeightAt(cap.B.X, cap.B.Z);
        if (MathF.Min(ca, cb) > ContactPlane) return default;
        float t0 = ca > ContactPlane ? (ca - ContactPlane) / (ca - cb) : 0, t1 = cb > ContactPlane ? (ContactPlane - ca) / (cb - ca) : 1;
        float pen = ContactPlane - MathF.Min(ca, cb);
        float half = MathF.Min(MathF.Max(MathF.Sqrt(MathF.Max(2 * r * MathF.Min(pen, r) - pen * pen, 0)), MathF.Max(0.9f * r, 0.5f)), MathF.Max(1.2f * r, 0.5f));
        var axis = new Vector2(cap.B.X - cap.A.X, cap.B.Z - cap.A.Z);
        float flatLength = axis.Length();
        var mid = Vector3.Lerp(cap.A, cap.B, (t0 + t1) / 2);
        float sink = Math.Clamp(pen / ContactPlane, 0, 1);
        return new ContactPatch(new Vector2(mid.X, mid.Z), flatLength > 1e-3f ? axis / flatLength : Vector2.UnitX, (t1 - t0) * flatLength + 2 * half, 2 * half, sink,
            MathF.Min(cap.A.Y, cap.B.Y) - r, sink >= MinSink, flatLength > 2 * r && MathF.Max(ca, cb) <= ContactPlane);
    }
}
