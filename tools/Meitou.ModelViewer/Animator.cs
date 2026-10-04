using System.Numerics;
using Meitou.Data.Ogre;

namespace Meitou.ModelViewer;

/// <summary>
/// Poses an Ogre skeleton. Follows Ogre's v1 node maths (MIT source, docs/formats/ogre-skeleton.md): keyframes are
/// relative to the binding pose (position + T, orientation * R, scale * S), children inherit
/// orientation (scale only through Kenshi's rule, see Pose), and a skinning matrix is the posed derived transform times the inverse bound one.
/// Rotations are interpolated with normalised lerp along the shortest path (Ogre's default linear mode).
/// </summary>
public sealed class Animator
{
    readonly OgreSkeleton skeleton;
    readonly OgreBone?[] byHandle;
    readonly int[] order; // handles, parents before children
    readonly Matrix4x4[] inverseBind;
    readonly Vector3[] pos, scale;
    readonly Quaternion[] rot;
    readonly Vector3[] dPos, dScale;
    readonly Quaternion[] dRot;

    public Animator(OgreSkeleton skeleton)
    {
        this.skeleton = skeleton;
        int count = skeleton.Bones.Count == 0 ? 0 : skeleton.Bones.Max(b => b.Handle) + 1;
        BoneCount = count;
        byHandle = new OgreBone?[count];
        foreach (var b in skeleton.Bones) byHandle[b.Handle] = b;
        var sorted = new List<int>();
        var placed = new bool[count];
        void Place(OgreBone b)
        {
            if (placed[b.Handle]) return;
            placed[b.Handle] = true; // set first: guards against (not seen) cycles
            if (b.Parent is { } p && p < count && byHandle[p] is { } parent) Place(parent);
            sorted.Add(b.Handle);
        }
        foreach (var b in skeleton.Bones) Place(b);
        order = [.. sorted];
        pos = new Vector3[count]; scale = new Vector3[count]; rot = new Quaternion[count];
        dPos = new Vector3[count]; dScale = new Vector3[count]; dRot = new Quaternion[count];
        SkinMatrices = new Matrix4x4[count];
        inverseBind = new Matrix4x4[count];
        Pose(null, 0);
        for (int h = 0; h < count; h++)
        {
            Matrix4x4.Invert(Derived(h), out inverseBind[h]);
            SkinMatrices[h] = Matrix4x4.Identity;
        }
    }

    public int BoneCount { get; }
    public IReadOnlyList<OgreAnimation> Animations => skeleton.Animations;

    /// <summary>Per bone handle: posed derived transform × inverse binding transform (System.Numerics row-vector order).</summary>
    public Matrix4x4[] SkinMatrices { get; }

    /// <summary>Derived (model-space) position of each bone handle in the current pose, for drawing the skeleton.</summary>
    public Vector3 BonePosition(int handle) => dPos[handle];
    public int? BoneParent(int handle) => byHandle[handle]?.Parent;

    /// <summary>Poses the skeleton at <paramref name="time"/> seconds of <paramref name="animation"/> (null = binding pose) and updates <see cref="SkinMatrices"/>.</summary>
    public void Pose(OgreAnimation? animation, float time)
    {
        for (int h = 0; h < BoneCount; h++)
        {
            var b = byHandle[h];
            pos[h] = b?.Position ?? Vector3.Zero;
            rot[h] = b?.Orientation ?? Quaternion.Identity;
            scale[h] = b?.Scale ?? Vector3.One;
        }
        if (animation is not null)
            foreach (var track in animation.Tracks)
            {
                if (track.Bone >= BoneCount || track.KeyFrames.Count == 0) continue;
                var (t, r, s) = Sample(track.KeyFrames, time);
                pos[track.Bone] += t;
                rot[track.Bone] = Quaternion.Normalize(rot[track.Bone] * r); // Ogre's Node::rotate in local space: q = q * r
                scale[track.Bone] *= s;
            }

        foreach (int h in order)
        {
            var parent = byHandle[h]?.Parent is { } p && p < BoneCount && byHandle[p] is not null ? p : -1;
            if (parent < 0)
            {
                dPos[h] = pos[h]; dRot[h] = rot[h]; dScale[h] = scale[h];
            }
            else
            {
                // Kenshi's modified Ogre (docs/formats/ogre-skeleton.md, "Bone maths"): scale is not inherited, and the
                // child's offset is scaled by the Y component of the parent's derived scale on all axes.
                dRot[h] = dRot[parent] * rot[h];
                dScale[h] = scale[h];
                dPos[h] = Vector3.Transform(pos[h] * dScale[parent].Y, dRot[parent]) + dPos[parent];
            }
        }
        if (inverseBind is null) return;
        for (int h = 0; h < BoneCount; h++)
            SkinMatrices[h] = inverseBind[h] * Derived(h);
    }

    Matrix4x4 Derived(int h) =>
        Matrix4x4.CreateScale(dScale[h]) * Matrix4x4.CreateFromQuaternion(dRot[h]) * Matrix4x4.CreateTranslation(dPos[h]);

    static (Vector3 T, Quaternion R, Vector3 S) Sample(List<OgreKeyFrame> keys, float time)
    {
        if (time <= keys[0].Time || keys.Count == 1) return (keys[0].Translation, keys[0].Rotation, keys[0].Scale);
        if (time >= keys[^1].Time) return (keys[^1].Translation, keys[^1].Rotation, keys[^1].Scale);
        int lo = 0, hi = keys.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid].Time <= time) lo = mid; else hi = mid;
        }
        var a = keys[lo];
        var b = keys[hi];
        float f = (time - a.Time) / Math.Max(b.Time - a.Time, 1e-6f);
        var qb = Quaternion.Dot(a.Rotation, b.Rotation) < 0 ? -b.Rotation : b.Rotation;
        var q = Quaternion.Normalize(new Quaternion(
            a.Rotation.X + (qb.X - a.Rotation.X) * f, a.Rotation.Y + (qb.Y - a.Rotation.Y) * f,
            a.Rotation.Z + (qb.Z - a.Rotation.Z) * f, a.Rotation.W + (qb.W - a.Rotation.W) * f));
        return (Vector3.Lerp(a.Translation, b.Translation, f), q, Vector3.Lerp(a.Scale, b.Scale, f));
    }
}
