using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;

namespace Meitou.Rendering.Characters;

/// <summary>One bone's keyframes of an animation as flat arrays (what the sampler binary-searches).</summary>
internal sealed class BoneTrack(float[] times, Vector3[] translation, Quaternion[] rotation, Vector3[] scale)
{
    public readonly float[] Times = times;
    public readonly Vector3[] Translation = translation;
    public readonly Quaternion[] Rotation = rotation;
    public readonly Vector3[] Scale = scale;
}

/// <summary>An Ogre animation with its tracks indexed by bone handle (null: the animation does not move that bone).</summary>
internal sealed class AnimationClip(string name, float length, BoneTrack?[] tracks)
{
    public readonly string Name = name;
    public readonly float Length = length;
    public readonly BoneTrack?[] Tracks = tracks;
}

/// <summary>
/// An animation as a layer of a pose: the clip with the tracks its ANIMATION record deletes and the bones it overrides
/// (docs/animation.md, "Startup preprocessing"). Immutable, one per (skeleton, record or animation name).
/// </summary>
internal sealed class LayerDef(AnimationClip clip, bool[]? excluded, int[] overrides, string label)
{
    public readonly AnimationClip Clip = clip;
    /// <summary>By bone handle: tracks ignored while sampling (null: none).</summary>
    public readonly bool[]? Excluded = excluded;
    public readonly int[] Override = overrides;
    public readonly string Label = label;
}

/// <summary>One animation of a pose: its definition, the time in seconds (looped over the length) and the blend weight.</summary>
internal readonly record struct ActiveLayer(LayerDef Def, float Time, float Weight);

/// <summary>Kenshi's two extra per-bone vectors and the movement scale that the body-shape sliders set (<see cref="CharacterShape"/>).</summary>
internal sealed class BodyShapeData(Vector3[] boneSize, Vector3[] positionalSize, float movementScale)
{
    public readonly Vector3[] BoneSize = boneSize;
    public readonly Vector3[] PositionalSize = positionalSize;
    public readonly float MovementScale = movementScale;
}

/// <summary>
/// An Ogre skeleton prepared for posing many characters: bones by handle, parents before children, the binding pose, the animations as
/// arrays and the layer definitions found so far. Immutable once built except for those caches, which only the render thread fills
/// (<see cref="Layer"/>); posing itself (<see cref="PoseScratch"/>) only reads, so it may run on any thread.
/// Maths: <see cref="PoseScratch.Pose"/> (Ogre's v1 node maths with Kenshi's changes, docs/formats/ogre-skeleton.md).
/// </summary>
internal sealed class SkeletonRig
{
    public readonly OgreSkeleton Skeleton;
    public readonly int BoneCount;
    public readonly OgreBone?[] ByHandle;
    /// <summary>Handles, parents before children.</summary>
    public readonly int[] Order;
    /// <summary>Parent handle per handle, -1 for roots.</summary>
    public readonly int[] Parent;
    public readonly bool AverageBlend;
    readonly Dictionary<string, int> handles = new(StringComparer.Ordinal);
    readonly Dictionary<string, AnimationClip?> clips = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, LayerDef?> layers = new(StringComparer.OrdinalIgnoreCase);
    // The binding pose in model space, with neutral shape: what the skin matrices are relative to (Ogre's OldBone::_getOffsetTransform).
    internal readonly Vector3[] BindPosition = [], BindScale = [];
    internal readonly Quaternion[] BindRotationInverse = [];

    public SkeletonRig(OgreSkeleton skeleton)
    {
        Skeleton = skeleton;
        BoneCount = skeleton.Bones.Count == 0 ? 0 : skeleton.Bones.Max(b => b.Handle) + 1;
        ByHandle = new OgreBone?[BoneCount];
        Parent = new int[BoneCount];
        Array.Fill(Parent, -1);
        foreach (var b in skeleton.Bones)
        {
            ByHandle[b.Handle] = b;
            handles.TryAdd(b.Name, b.Handle);
        }
        foreach (var b in skeleton.Bones)
            if (b.Parent is { } p && p < BoneCount && ByHandle[p] is not null) Parent[b.Handle] = p;
        var sorted = new List<int>();
        var placed = new bool[BoneCount];
        void Place(OgreBone b)
        {
            if (placed[b.Handle]) return;
            placed[b.Handle] = true;
            if (Parent[b.Handle] >= 0) Place(ByHandle[Parent[b.Handle]]!);
            sorted.Add(b.Handle);
        }
        foreach (var b in skeleton.Bones) Place(b);
        Order = [.. sorted];
        AverageBlend = skeleton.BlendMode == OgreSkeletonBlendMode.Average;

        // The binding pose: derived transforms of the rest pose with neutral shape.
        var scratch = new PoseScratch(this);
        scratch.PoseDerived(null, []);
        BindPosition = [.. scratch.DerivedPosition.AsSpan(0, BoneCount)];
        BindScale = [.. scratch.DerivedScale.AsSpan(0, BoneCount)];
        BindRotationInverse = new Quaternion[BoneCount];
        for (int h = 0; h < BoneCount; h++) BindRotationInverse[h] = Quaternion.Inverse(scratch.DerivedRotation[h]);
    }

    readonly System.Collections.Concurrent.ConcurrentBag<PoseScratch> scratches = [];

    /// <summary>A working set for posing on the calling thread; give it back with <see cref="Return"/>.</summary>
    public PoseScratch Rent() => scratches.TryTake(out var s) ? s : new PoseScratch(this);
    public void Return(PoseScratch scratch) => scratches.Add(scratch);

    /// <summary>Handle of the bone called <paramref name="name"/>, or null.</summary>
    public int? Handle(string name) => handles.TryGetValue(name, out var h) ? h : null;

    /// <summary>The animation called <paramref name="name"/> as arrays (null: the skeleton has none).</summary>
    public AnimationClip? Clip(string name)
    {
        if (clips.TryGetValue(name, out var cached)) return cached;
        var animation = Skeleton.Animations.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        return clips[name] = animation is null ? null : Compile(animation);
    }

    AnimationClip Compile(OgreAnimation animation)
    {
        var tracks = new BoneTrack?[BoneCount];
        foreach (var t in animation.Tracks)
        {
            if (t.Bone >= BoneCount || t.KeyFrames.Count == 0) continue;
            var keys = t.KeyFrames;
            tracks[t.Bone] = new BoneTrack([.. keys.Select(k => k.Time)], [.. keys.Select(k => k.Translation)], [.. keys.Select(k => k.Rotation)], [.. keys.Select(k => k.Scale)]);
        }
        return new AnimationClip(animation.Name, animation.Length, tracks);
    }

    /// <summary>
    /// The layer for an ANIMATION record name or an Ogre animation name, with that record's deleted tracks and override bones
    /// (<see cref="AnimationMask.Find"/>), cached; null when the skeleton has no such animation.
    /// </summary>
    public LayerDef? Layer(GameDatabase? db, string name)
    {
        if (layers.TryGetValue(name, out var cached)) return cached;
        var mask = AnimationMask.Find(db, name);
        var clip = Clip(mask.AnimationName) ?? Clip(name);
        LayerDef? def = null;
        if (clip is not null)
        {
            bool[]? excluded = null;
            foreach (var bone in mask.DeletedBones)
                if (Handle(bone) is { } h) (excluded ??= new bool[BoneCount])[h] = true;
            int[] overrides = [.. mask.OverrideBones.Select(Handle).OfType<int>()];
            def = new LayerDef(clip, excluded, overrides, mask.RecordName is { } r && r != clip.Name ? $"{r} ({clip.Name})" : clip.Name);
        }
        return layers[name] = def;
    }

    /// <summary>The body-shape vectors by handle for the sliders' result (<see cref="CharacterShape.Compute"/>); bones not named stay 1.</summary>
    public BodyShapeData Shape(IReadOnlyDictionary<string, Vector3> sizes, IReadOnlyDictionary<string, Vector3> positionalSizes, float movementScale)
    {
        var size = new Vector3[BoneCount];
        var positional = new Vector3[BoneCount];
        Array.Fill(size, Vector3.One);
        Array.Fill(positional, Vector3.One);
        foreach (var (name, v) in sizes) if (Handle(name) is { } h) size[h] = v;
        foreach (var (name, v) in positionalSizes) if (Handle(name) is { } h) positional[h] = v;
        return new BodyShapeData(size, positional, movementScale);
    }
}

/// <summary>
/// Poses a <see cref="SkeletonRig"/>: one per thread (it holds the working arrays). Follows Ogre's v1 node maths with Kenshi's changes
/// (docs/formats/ogre-skeleton.md; the removed viewer's <c>Animator</c>, docs/character-viewer.md "Posing"): keyframes relative to the
/// binding pose, average blending with override bones in a second pass, rotations interpolated by nlerp, derived scale not inherited, and
/// skin matrices in Ogre's <c>OldBone::_getOffsetTransform</c> form.
/// </summary>
internal sealed class PoseScratch
{
    readonly SkeletonRig rig;
    readonly Vector3[] pos, scale;
    readonly Quaternion[] rot;
    public readonly Vector3[] DerivedPosition, DerivedScale;
    public readonly Quaternion[] DerivedRotation;

    public PoseScratch(SkeletonRig rig)
    {
        this.rig = rig;
        int n = rig.BoneCount;
        pos = new Vector3[n]; scale = new Vector3[n]; rot = new Quaternion[n];
        DerivedPosition = new Vector3[n]; DerivedScale = new Vector3[n]; DerivedRotation = new Quaternion[n];
    }

    /// <summary>
    /// Poses the skeleton with <paramref name="layers"/> (empty: the binding pose) and the body shape <paramref name="shape"/> (null: neutral);
    /// writes each bone's skin matrix into <paramref name="skin"/> (<see cref="SkeletonRig.BoneCount"/> entries, System.Numerics row-vector form,
    /// the memory a GLSL <c>mat4</c> reads as its column-vector form).
    /// </summary>
    public void Pose(BodyShapeData? shape, ReadOnlySpan<ActiveLayer> layers, Span<Matrix4x4> skin)
    {
        PoseDerived(shape, layers);
        for (int h = 0; h < rig.BoneCount; h++) skin[h] = Offset(h);
    }

    /// <summary>The posed derived (model-space) transform of a bone: scale, rotation, translation.</summary>
    public Matrix4x4 BoneTransform(int h) =>
        Matrix4x4.CreateScale(DerivedScale[h]) * Matrix4x4.CreateFromQuaternion(DerivedRotation[h]) * Matrix4x4.CreateTranslation(DerivedPosition[h]);

    internal void PoseDerived(BodyShapeData? shape, ReadOnlySpan<ActiveLayer> layers)
    {
        int count = rig.BoneCount;
        for (int h = 0; h < count; h++)
        {
            var b = rig.ByHandle[h];
            pos[h] = b?.Position ?? Vector3.Zero;
            rot[h] = b?.Orientation ?? Quaternion.Identity;
            scale[h] = b?.Scale ?? Vector3.One;
        }
        float movement = shape?.MovementScale ?? 1;

        // Average blend mode (all base-game skeletons): weights are scaled down only when they add up to more than 1.
        float total = 0;
        foreach (var l in layers) total += Math.Max(l.Weight, 0);
        float norm = rig.AverageBlend && total > 1 ? 1 / total : 1;
        for (int pass = 0; pass < 2; pass++)
        {
            bool overridePass = pass == 1;
            foreach (var layer in layers)
            {
                if ((layer.Def.Override.Length > 0) != overridePass) continue;
                float w = Math.Max(layer.Weight, 0) * norm;
                if (w <= 0) continue;
                if (overridePass)
                    foreach (int h in layer.Def.Override)
                    {
                        // Kenshi's override bones: pull the accumulated rotation back toward the binding one by the weight
                        // (fully at 0.99 and above) before adding this animation's rotation.
                        if (h >= count || rig.ByHandle[h] is not { } b) continue;
                        rot[h] = w >= 0.99f ? b.Orientation : Nlerp(rot[h], b.Orientation, w);
                    }
                var clip = layer.Def.Clip;
                var excluded = layer.Def.Excluded;
                float time = clip.Length > 0 ? ((layer.Time % clip.Length) + clip.Length) % clip.Length : 0;
                for (int h = 0; h < count; h++)
                {
                    var track = clip.Tracks[h];
                    if (track is null || excluded is not null && excluded[h]) continue;
                    Sample(track, time, out var t, out var r, out var s);
                    pos[h] += t * (w * movement);
                    rot[h] = Quaternion.Normalize(rot[h] * Nlerp(Quaternion.Identity, r, w));   // Node::rotate, local space
                    if (s != Vector3.One) scale[h] *= Vector3.One + (s - Vector3.One) * w;
                }
            }
        }

        var boneSize = shape?.BoneSize;
        var positionalSize = shape?.PositionalSize;
        foreach (int h in rig.Order)
        {
            int parent = rig.Parent[h];
            var size = boneSize is null ? Vector3.One : boneSize[h];
            if (parent < 0)
            {
                DerivedPosition[h] = pos[h]; DerivedRotation[h] = rot[h]; DerivedScale[h] = size * scale[h];
            }
            else
            {
                // Kenshi's modified Ogre: derived scale = bone size × own scale (not inherited); the child's offset × its positional size
                // is scaled by the Y component of the parent's derived scale on all axes.
                var positional = positionalSize is null ? Vector3.One : positionalSize[h];
                DerivedRotation[h] = DerivedRotation[parent] * rot[h];
                DerivedScale[h] = size * scale[h];
                DerivedPosition[h] = Vector3.Transform(pos[h] * positional * DerivedScale[parent].Y, DerivedRotation[parent]) + DerivedPosition[parent];
            }
        }
    }

    /// <summary>
    /// Ogre's (and Kenshi's) skinning transform: the vertex relative to the bone's binding position is scaled by derived scale / binding
    /// scale in the binding pose's model axes, rotated by derived × inverse binding orientation and moved to the derived position.
    /// </summary>
    Matrix4x4 Offset(int h)
    {
        var s = DerivedScale[h] / rig.BindScale[h];
        var r = DerivedRotation[h] * rig.BindRotationInverse[h];
        return Matrix4x4.CreateTranslation(-rig.BindPosition[h]) * Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r)
            * Matrix4x4.CreateTranslation(DerivedPosition[h]);
    }

    static Quaternion Nlerp(Quaternion a, Quaternion b, float f)
    {
        if (Quaternion.Dot(a, b) < 0) b = -b;
        return Quaternion.Normalize(new Quaternion(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f, a.W + (b.W - a.W) * f));
    }

    static void Sample(BoneTrack track, float time, out Vector3 t, out Quaternion r, out Vector3 s)
    {
        var times = track.Times;
        if (times.Length == 1 || time <= times[0]) { t = track.Translation[0]; r = track.Rotation[0]; s = track.Scale[0]; return; }
        int last = times.Length - 1;
        if (time >= times[last]) { t = track.Translation[last]; r = track.Rotation[last]; s = track.Scale[last]; return; }
        int lo = 0, hi = last;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (times[mid] <= time) lo = mid; else hi = mid;
        }
        float f = (time - times[lo]) / Math.Max(times[hi] - times[lo], 1e-6f);
        t = Vector3.Lerp(track.Translation[lo], track.Translation[hi], f);
        r = Nlerp(track.Rotation[lo], track.Rotation[hi], f);
        s = Vector3.Lerp(track.Scale[lo], track.Scale[hi], f);
    }
}
