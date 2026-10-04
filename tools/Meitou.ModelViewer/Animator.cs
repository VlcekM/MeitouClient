using System.Numerics;
using Meitou.Data.Ogre;

namespace Meitou.ModelViewer;

/// <summary>One animation playing on a skeleton: time, weight, and Kenshi's per-animation track deletions and override bones.</summary>
public sealed class AnimationLayer
{
    public required OgreAnimation Animation { get; init; }
    public float Time { get; set; }
    public float Weight { get; set; } = 1;
    public float Speed { get; set; } = 1;
    public bool Loop { get; set; } = true;
    /// <summary>Bone handles whose tracks are ignored (ANIMATION <c>delete *</c> fields).</summary>
    public IReadOnlySet<int> Excluded { get; init; } = new HashSet<int>();
    /// <summary>Bone handles marked as override bones (ANIMATION <c>override *</c> fields).</summary>
    public IReadOnlySet<int> Override { get; init; } = new HashSet<int>();
    /// <summary>Label for the console / title, e.g. the ANIMATION record name.</summary>
    public string Label { get; init; } = "";

    public void Advance(float dt)
    {
        Time += dt * Speed;
        float length = Animation.Length;
        if (length <= 0) { Time = 0; return; }
        Time = Loop ? ((Time % length) + length) % length : Math.Clamp(Time, 0, length);
    }
}

/// <summary>
/// Poses an Ogre skeleton. Follows Ogre's v1 node maths (MIT source, docs/formats/ogre-skeleton.md): keyframes are
/// relative to the binding pose, children inherit orientation (scale only through Kenshi's rule, see Pose), and a
/// skinning matrix is the posed derived transform times the inverse bound one. Rotations are interpolated with
/// normalised lerp along the shortest path (Ogre's default linear mode). Several animations blend as Kenshi's Ogre
/// does: average mode (weights scaled down only when they sum above 1), animations with override bones applied in a
/// second pass that first pulls those bones back toward the binding pose.
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
    readonly Dictionary<string, int> handles = new(StringComparer.Ordinal);

    public Animator(OgreSkeleton skeleton)
    {
        this.skeleton = skeleton;
        int count = skeleton.Bones.Count == 0 ? 0 : skeleton.Bones.Max(b => b.Handle) + 1;
        BoneCount = count;
        byHandle = new OgreBone?[count];
        foreach (var b in skeleton.Bones)
        {
            byHandle[b.Handle] = b;
            handles.TryAdd(b.Name, b.Handle);
        }
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
        BindDerived = new Matrix4x4[count];
        for (int h = 0; h < count; h++)
        {
            BindDerived[h] = Derived(h);
            Matrix4x4.Invert(BindDerived[h], out inverseBind[h]);
            SkinMatrices[h] = Matrix4x4.Identity;
        }
    }

    public int BoneCount { get; }
    public IReadOnlyList<OgreAnimation> Animations => skeleton.Animations;
    public OgreSkeleton Skeleton => skeleton;

    /// <summary>Per bone handle: posed derived transform × inverse binding transform (System.Numerics row-vector order).</summary>
    public Matrix4x4[] SkinMatrices { get; }

    /// <summary>Derived (model-space) transform of each bone in the binding pose.</summary>
    public Matrix4x4[] BindDerived { get; }

    /// <summary>Derived (model-space) position of each bone handle in the current pose, for drawing the skeleton.</summary>
    public Vector3 BonePosition(int handle) => dPos[handle];
    public int? BoneParent(int handle) => byHandle[handle]?.Parent;

    /// <summary>Derived (model-space) transform of a bone in the current pose.</summary>
    public Matrix4x4 BoneTransform(int handle) => Derived(handle);

    /// <summary>Handle of the bone called <paramref name="name"/>, or null.</summary>
    public int? Handle(string name) => handles.TryGetValue(name, out var h) ? h : null;

    /// <summary>Handles of the named bones the skeleton has (others are skipped, as Kenshi does).</summary>
    public HashSet<int> Handles(IEnumerable<string> names) => [.. names.Select(Handle).OfType<int>()];

    /// <summary>Poses the skeleton at <paramref name="time"/> seconds of <paramref name="animation"/> (null = binding pose) and updates <see cref="SkinMatrices"/>.</summary>
    public void Pose(OgreAnimation? animation, float time) =>
        Pose(animation is null ? [] : [new AnimationLayer { Animation = animation, Time = time }]);

    /// <summary>Blends <paramref name="layers"/> (an empty list gives the binding pose) and updates <see cref="SkinMatrices"/>.</summary>
    public void Pose(IReadOnlyList<AnimationLayer> layers)
    {
        for (int h = 0; h < BoneCount; h++)
        {
            var b = byHandle[h];
            pos[h] = b?.Position ?? Vector3.Zero;
            rot[h] = b?.Orientation ?? Quaternion.Identity;
            scale[h] = b?.Scale ?? Vector3.One;
        }

        // Average blend mode (all base-game skeletons): weights are scaled down only when they add up to more than 1.
        float total = 0;
        foreach (var l in layers) total += Math.Max(l.Weight, 0);
        float norm = skeleton.BlendMode == OgreSkeletonBlendMode.Average && total > 1 ? 1 / total : 1;
        foreach (bool overridePass in (ReadOnlySpan<bool>)[false, true])
            foreach (var layer in layers)
            {
                if ((layer.Override.Count > 0) != overridePass) continue;
                float w = Math.Max(layer.Weight, 0) * norm;
                if (w <= 0) continue;
                if (overridePass)
                    foreach (int h in layer.Override)
                    {
                        // Kenshi's override bones: pull the accumulated rotation back toward the binding one by the weight
                        // (fully at 0.99 and above) before adding this animation's rotation.
                        if (h >= BoneCount || byHandle[h] is not { } b) continue;
                        rot[h] = w >= 0.99f ? b.Orientation : Nlerp(rot[h], b.Orientation, w);
                    }
                foreach (var track in layer.Animation.Tracks)
                {
                    if (track.Bone >= BoneCount || track.KeyFrames.Count == 0 || layer.Excluded.Contains(track.Bone)) continue;
                    var (t, r, s) = Sample(track.KeyFrames, layer.Time);
                    pos[track.Bone] += t * w;
                    rot[track.Bone] = Quaternion.Normalize(rot[track.Bone] * Nlerp(Quaternion.Identity, r, w)); // Node::rotate, local space
                    if (s != Vector3.One) scale[track.Bone] *= Vector3.One + (s - Vector3.One) * w;
                }
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

    static Quaternion Nlerp(Quaternion a, Quaternion b, float f)
    {
        if (Quaternion.Dot(a, b) < 0) b = -b;
        return Quaternion.Normalize(new Quaternion(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f, a.W + (b.W - a.W) * f));
    }

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
        return (Vector3.Lerp(a.Translation, b.Translation, f), Nlerp(a.Rotation, b.Rotation, f), Vector3.Lerp(a.Scale, b.Scale, f));
    }
}
