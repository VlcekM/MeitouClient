using System.Numerics;
using Meitou.Audio;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Marks from real body contact (docs/render-terrain.md "Ground trails"): each frame the posed body of a character that is not walking upright (an animal, a crawling, downed, dead
/// or ragdolled person) is read as capsules, and where one rests on or slides over the soft ground it presses or ploughs a mark shaped by its contact.
/// </summary>
sealed partial class GameHost
{
    /// <summary>
    /// How far above the ground (units: 5 cm) a capsule's underside still presses a mark: the plane it is cut at to find its contact (a capsule resting on the ground sinks that far in).
    /// </summary>
    const float ContactPlane = 0.5f, MinSink = 0.5f;

    /// <summary>A part that moves over 1.6 times its body's step (and 0.25 units more) in a frame swings through the air, over 0.3 times (and 0.12 more) it slides, else it rests.</summary>
    const float SwingShare = 1.6f, SwingSlack = 0.25f, RestShare = 0.3f, SlideSlack = 0.12f;

    /// <summary>How deep a slide ploughs against a press of the same sink.</summary>
    const float PloughShare = 0.9f;

    /// <summary>A resting part presses again when it moved this far from its last mark or sank this much (of <see cref="ContactPlane"/>) deeper.</summary>
    const float RestampMove = 0.3f, RestampSink = 0.15f;

    /// <summary>A body that moved further than this between two frames was placed, not moved: its contacts start again.</summary>
    const float Placed = 6;

    /// <summary>One capsule's state in the last frame: where its middle was, where it last touched the ground and its last mark.</summary>
    struct Contact
    {
        public Vector2 Middle, Last, Stamped;
        public float StampedSink;
        public bool Touched, Marked;
    }

    /// <summary>Where a capsule's underside meets the ground: the centre, direction and whole size of the stadium it presses, and how hard (0 to 1).</summary>
    readonly record struct ContactPatch(Vector2 At, Vector2 Along, float Length, float Width, float Sink);

    /// <summary>A capsule whose underside is this far (units) over the ground under the body's root is left out without a look at the terrain under it.</summary>
    const float NearRoot = 5;

    readonly List<WaterBody> contactBodies = [];

    /// <summary>
    /// A character's frame: its posed capsules against the ground. False when it has no pose (it was not drawn), then its path's prints stand in. <paramref name="step"/> is the body's
    /// own movement this frame; <paramref name="upright"/> bodies press (a foot lands) but never plough.
    /// </summary>
    bool BodyContact(TerrainTrailRenderer trails, GroundSurfaces ground, long key, ref Walker w, Vector3 root, bool animal, bool upright, float step, bool continues)
    {
        contactBodies.Clear();
        gpu.Characters?.ContactCapsules(key, contactBodies);
        if (contactBodies.Count == 0) return false;
        if (w.Contacts is null || w.Contacts.Length != contactBodies.Count) w.Contacts = new Contact[contactBodies.Count];
        bool known = continues && step < Placed;
        float rootGround = gpu.Terrain.HeightAt(root.X, root.Z);
        for (int i = 0; i < contactBodies.Count; i++)
            Touch(trails, ground, contactBodies[i], ref w.Contacts[i], rootGround, animal, upright, step, known);
        return true;
    }

    /// <summary>One capsule: how fast it moves (a swing through the air, a slide, or at rest), where its underside is within the plane of the ground and the mark for that.</summary>
    void Touch(TerrainTrailRenderer trails, GroundSurfaces ground, in WaterBody cap, ref Contact st, float rootGround, bool animal, bool upright, float step, bool known)
    {
        var middle = new Vector2(cap.A.X + cap.B.X, cap.A.Z + cap.B.Z) / 2;
        float speed = known ? Vector2.Distance(middle, st.Middle) : 0;
        st.Middle = middle;
        bool swinging = speed > step * SwingShare + SwingSlack, moving = speed > step * RestShare + SlideSlack;
        if (cap.Radius <= 0 || swinging || MathF.Min(cap.A.Y, cap.B.Y) - cap.Radius > rootGround + NearRoot || !Contacting(cap, out var patch) || patch.Sink < MinSink)
        {
            st.Touched = false;
            return;
        }
        bool sliding = moving && st.Touched && !upright;
        if (moving && !sliding && upright)
        {
            st.Touched = false;
            return;
        }
        float depth = (patch.Sink - MinSink) / (1 - MinSink) * (0.9f + 0.1f * Math.Clamp(cap.Radius / 2, 0, 1)) * (animal ? 0.9f : 1);
        var before = st.Last;
        (st.Last, st.Touched) = (patch.At, true);
        float moved = sliding ? Vector2.Distance(patch.At, before) : 0;
        if (moved < 0.02f) sliding = false;
        if (!sliding && st.Marked && Vector2.Distance(patch.At, st.Stamped) < RestampMove && patch.Sink <= st.StampedSink + RestampSink) return;
        (st.Stamped, st.StampedSink, st.Marked) = (patch.At, patch.Sink, true);
        var at = new Vector3(patch.At.X, MathF.Min(cap.A.Y, cap.B.Y) - cap.Radius, patch.At.Y);
        if (!sliding) Press(trails, ground, at, new TrailStamp(patch.At, patch.Along, patch.Length, patch.Width, depth, TrailKind.Press));
        else Press(trails, ground, at, new TrailStamp((patch.At + before) / 2, (patch.At - before) / moved, moved + patch.Width, patch.Width, depth * PloughShare, TrailKind.Plough));
    }

    /// <summary>
    /// The contact of a capsule with the terrain: the part of its axis whose underside is within <see cref="ContactPlane"/> of the ground (a line between the ground under its ends), cut
    /// there, makes a stadium as wide as the capsule at that depth (at least 0.9 of its radius, 5 cm: flesh is soft); the sink is how far in it is, 1 resting on the ground.
    /// </summary>
    bool Contacting(in WaterBody cap, out ContactPatch patch)
    {
        patch = default;
        float r = cap.Radius;
        float ca = cap.A.Y - r - gpu.Terrain.HeightAt(cap.A.X, cap.A.Z), cb = cap.B.Y - r - gpu.Terrain.HeightAt(cap.B.X, cap.B.Z);
        if (MathF.Min(ca, cb) > ContactPlane) return false;
        float t0 = ca > ContactPlane ? (ca - ContactPlane) / (ca - cb) : 0, t1 = cb > ContactPlane ? (ContactPlane - ca) / (cb - ca) : 1;
        float pen = ContactPlane - MathF.Min(ca, cb);
        float half = MathF.Min(MathF.Max(MathF.Sqrt(MathF.Max(2 * r * MathF.Min(pen, r) - pen * pen, 0)), MathF.Max(0.9f * r, 0.5f)), MathF.Max(1.2f * r, 0.5f));
        var axis = new Vector2(cap.B.X - cap.A.X, cap.B.Z - cap.A.Z);
        float axisLength = axis.Length();
        var mid = Vector3.Lerp(cap.A, cap.B, (t0 + t1) / 2);
        patch = new ContactPatch(new Vector2(mid.X, mid.Z), axisLength > 1e-3f ? axis / axisLength : Vector2.UnitX, (t1 - t0) * axisLength + 2 * half, 2 * half, Math.Clamp(pen / ContactPlane, 0, 1));
        return true;
    }
}
