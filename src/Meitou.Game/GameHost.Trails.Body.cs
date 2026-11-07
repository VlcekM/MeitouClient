using System.Numerics;
using Meitou.Audio;
using Meitou.Data.World;
using Meitou.Rendering;
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

    /// <summary>A part over a lower one of its own body within this much (plus both radii) is left out of an upright body's marks: a shin over a foot.</summary>
    const float ShadowedBy = 0.15f, ShadowReach = 0.5f;

    /// <summary>A capsule longer than this many times its width, lying flat (its axis within 0.6 of the ground's plane), is a foot with a sole; any other upright contact is a round print.</summary>
    const float SoleAspect = 1.4f, SoleFlat = 0.6f;

    /// <summary>A paw print's quad against the print's width (<see cref="TerrainTrailRenderer.StampFragment"/> draws it 0.86 of the quad).</summary>
    const float PawPerWidth = 1.16f;

    /// <summary>One capsule's state in the last frame: where its middle was, where it last touched the ground, and whether it is already printed for this planting.</summary>
    struct Contact
    {
        public Vector2 Middle, Last;
        public bool Touched, Printed;
    }

    /// <summary>Where a capsule's underside meets the ground: the centre, direction and whole size of the stadium it presses, how hard (0 to 1), its lowest point and whether it lies flat.</summary>
    readonly record struct ContactPatch(Vector2 At, Vector2 Along, float Length, float Width, float Sink, float Bottom, bool Flat, bool Valid);

    readonly List<WaterBody> contactBodies = [];
    ContactPatch[] contactPatches = new ContactPatch[64];

    /// <summary>
    /// A character's frame: its posed capsules against the ground. False when it has no pose (it was not drawn), then its path's prints stand in. <paramref name="speed"/> is the body's own
    /// ground speed (units/s); <paramref name="upright"/> bodies (standing, walking, animals) press where a foot or paw plants and never plough.
    /// </summary>
    bool BodyContact(TerrainTrailRenderer trails, GroundSurfaces ground, long key, ref Walker w, Vector3 root, bool animal, bool upright, float speed, bool continues)
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
            Touch(trails, ground, contactBodies[i], contactPatches[i], ref w.Contacts[i], new Vector2(root.X, root.Z), animal, upright, speed, known);
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
                float reach = contactBodies[i].Radius + contactBodies[j].Radius + ShadowReach;
                if (contactPatches[j].Bottom < contactPatches[i].Bottom - ShadowedBy && Vector2.Distance(contactPatches[i].At, contactPatches[j].At) < reach)
                {
                    contactPatches[i] = contactPatches[i] with { Valid = false };
                    break;
                }
            }
        }
    }

    /// <summary>
    /// One capsule: how fast it moves over the ground (swinging, sliding or planted); a part planting prints once (never again until it has lifted or slid), a lying body's part sliding ploughs.
    /// </summary>
    void Touch(TerrainTrailRenderer trails, GroundSurfaces ground, in WaterBody cap, in ContactPatch patch, ref Contact st, Vector2 root, bool animal, bool upright, float bodySpeed, bool known)
    {
        var middle = new Vector2(cap.A.X + cap.B.X, cap.A.Z + cap.B.Z) / 2;
        float speed = known && trailDt > 0 ? Vector2.Distance(middle, st.Middle) / trailDt : 0;
        st.Middle = middle;
        bool swinging = speed > bodySpeed * SwingShare + SwingSlack, sliding = speed > bodySpeed * SlideShare + SlideSlack;
        if (cap.Radius <= 0 || swinging || !patch.Valid)
        {
            st.Touched = st.Printed = false;
            return;
        }
        float depth = (patch.Sink - MinSink) / (1 - MinSink) * (0.9f + 0.1f * Math.Clamp(cap.Radius / 2, 0, 1)) * (animal ? 0.9f : 1);
        var at = new Vector3(patch.At.X, patch.Bottom, patch.At.Y);
        var before = st.Last;
        bool was = st.Touched;
        (st.Last, st.Touched) = (patch.At, true);
        if (sliding)
        {
            st.Printed = false;
            float moved = Vector2.Distance(patch.At, before);
            if (!upright && was && moved > 0.02f)
                Press(trails, ground, at, new TrailStamp((patch.At + before) / 2, (patch.At - before) / moved, moved + patch.Width, patch.Width, depth * PloughShare, TrailKind.Plough));
            return;
        }
        if (st.Printed) return;
        st.Printed = true;
        Press(trails, ground, at, PrintOf(patch, root, depth, upright));
    }

    /// <summary>The print a planted part leaves: a sole for a foot lying flat (its toe away from the body), a round print for a paw, hoof or stick foot, a stadium for a lying body's part.</summary>
    static TrailStamp PrintOf(in ContactPatch patch, Vector2 root, float depth, bool upright)
    {
        if (!upright) return new TrailStamp(patch.At, patch.Along, patch.Length, patch.Width, depth, TrailKind.Press);
        if (patch.Flat && patch.Length > SoleAspect * patch.Width)
        {
            var along = patch.Along;
            if (Vector2.Dot(along, patch.At - root) < 0) along = -along;
            return new TrailStamp(patch.At, along, patch.Length, patch.Length, depth, TrailKind.Foot);
        }
        float size = MathF.Max(patch.Width, 0.8f) * PawPerWidth;
        return new TrailStamp(patch.At, patch.Along, size, size, depth, TrailKind.Paw);
    }

    /// <summary>
    /// The contact of a capsule with the terrain: the part of its axis whose underside is within <see cref="ContactPlane"/> of the ground (a line between the ground under its ends), cut
    /// there, makes a stadium as wide as the capsule at that depth (at least 0.9 of its radius and 5 cm: flesh is soft); the sink is how far in it is, 1 resting on the ground.
    /// </summary>
    ContactPatch Contacting(in WaterBody cap, float rootGround)
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
        return new ContactPatch(new Vector2(mid.X, mid.Z), flatLength > 1e-3f ? axis / flatLength : Vector2.UnitX, (t1 - t0) * flatLength + 2 * half, 2 * half, sink,
            MathF.Min(cap.A.Y, cap.B.Y) - r, flat, sink >= MinSink);
    }
}
