using System.Numerics;
using Meitou.Audio;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Marks from real body contact (docs/render-terrain.md "Ground trails"): each frame the posed body of a character is read as capsules, and where one plants on or slides over the soft
/// ground it presses or ploughs a mark shaped by its contact: a sole for a foot lying flat, a round print for a paw, hoof or stick foot, a stadium for a body part of a lying body.
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

    /// <summary>A capsule longer than this many times its width, lying flat (its axis within 0.6 of the ground's plane), is a foot with a sole; any other upright contact is a round print.</summary>
    const float SoleAspect = 1.4f, SoleFlat = 0.6f;

    /// <summary>A paw print's quad against the print's width (<see cref="TerrainTrailRenderer.StampFragment"/> draws it 0.86 of the quad).</summary>
    const float PawPerWidth = 1.16f;

    /// <summary>A sole's width over its length at the ball (<see cref="TerrainTrailRenderer.StampFragment"/> draws it).</summary>
    const float SoleWidth = 0.38f;

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

    /// <summary>Where a capsule's underside meets the ground: the centre, direction and whole size of the stadium it presses, how hard (0 to 1), its lowest point and whether it lies flat.</summary>
    readonly record struct ContactPatch(Vector2 At, Vector2 Along, float Length, float Width, float Sink, float Bottom, bool Flat, bool Valid, Vector2 Toe);

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
        for (int i = 0; i < count; i++)
            Touch(trails, ground, contactBodies[i], contactPatches[i], ref w, i, heading, animal, upright, speed, known);
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
    void Touch(TerrainTrailRenderer trails, GroundSurfaces ground, in BodyCapsule cap, in ContactPatch patch, ref Walker w, int index, Vector2 heading, bool animal, bool upright, float bodySpeed, bool known)
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
        if (st.Printed) return;
        st.Printed = true;
        var print = PrintOf(patch, heading, depth, upright, animal);
        if (upright && !Remember(ref w, print, upright && !animal && print.Kind == TrailKind.Foot)) return;
        Press(trails, ground, at, print);
    }

    /// <summary>A part that is not planted this frame: once it has been off the ground for <see cref="LiftTime"/> it may print again.</summary>
    void Lifted(ref Contact st)
    {
        st.Air += trailDt;
        if (st.Air >= LiftTime) st.Printed = false;
    }

    /// <summary>
    /// Whether a contact is a heel or toe piece of a foot of a two-legged body: flat on the ground but too short to be a whole sole. An animal's paw or hoof, and a stick foot's upright tip,
    /// are no pieces: they print round.
    /// </summary>
    static bool Partial(in ContactPatch patch, bool upright, bool animal) => upright && !animal && patch.Flat && patch.Length <= SoleAspect * patch.Width;

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

    /// <summary>The print a planted part leaves: a sole for a foot lying flat (its toe away from the body; a two-legged body's short flat contact, a heel or toe, is a small sole, never a round print), a round print for a paw, hoof or stick foot, a stadium for a lying body's part.</summary>
    static TrailStamp PrintOf(in ContactPatch patch, Vector2 heading, float depth, bool upright, bool animal)
    {
        if (!upright) return new TrailStamp(patch.At, patch.Along, patch.Length, patch.Width, depth, TrailKind.Press);
        // Soles are a two-legged body's: an animal's long flat contact (a dog's hock on the ground) is a paw too.
        if (!animal && patch.Flat && (patch.Length > SoleAspect * patch.Width || Partial(patch, upright, animal)))
        {
            // Heel to toe: the way the foot bone points to its toe; without one, its axis turned to the way the body moves.
            var along = patch.Toe != Vector2.Zero ? patch.Toe : Vector2.Dot(patch.Along, heading) < 0 ? -patch.Along : patch.Along;
            return new TrailStamp(patch.At, along, patch.Length, patch.Length * SoleWidth, depth, TrailKind.Foot);
        }
        float size = MathF.Max(patch.Width, 0.8f) * PawPerWidth;
        // Its toes the way the body moves (its axis is no guide: a paw's capsule lies any way round).
        return new TrailStamp(patch.At, heading != Vector2.Zero ? heading : patch.Along, size, size, depth, TrailKind.Paw);
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
        float flatLength = axis.Length(), length3 = Vector3.Distance(cap.A, cap.B);
        var mid = Vector3.Lerp(cap.A, cap.B, (t0 + t1) / 2);
        bool flat = length3 > 1e-3f && flatLength / length3 > SoleFlat;
        float sink = Math.Clamp(pen / ContactPlane, 0, 1);
        var toe = new Vector2(cap.Forward.X, cap.Forward.Z);
        return new ContactPatch(new Vector2(mid.X, mid.Z), flatLength > 1e-3f ? axis / flatLength : Vector2.UnitX, (t1 - t0) * flatLength + 2 * half, 2 * half, sink,
            MathF.Min(cap.A.Y, cap.B.Y) - r, flat, sink >= MinSink, toe.Length() > 0.3f ? Vector2.Normalize(toe) : Vector2.Zero);
    }
}
