using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Ogre;

namespace Meitou.Data.Characters;

/// <summary>
/// One bone's keyframes of an animation as flat arrays (what the sampler binary-searches).
/// </summary>
public sealed class BoneTrack(float[] times, Vector3[] translation, Quaternion[] rotation, Vector3[] scale)
{
    /// <summary>
    /// The key times in seconds, ascending.
    /// </summary>
    public readonly float[] Times = times;

    /// <summary>
    /// The translation of each key, relative to the binding pose.
    /// </summary>
    public readonly Vector3[] Translation = translation;

    /// <summary>
    /// The rotation of each key, relative to the binding pose.
    /// </summary>
    public readonly Quaternion[] Rotation = rotation;

    /// <summary>
    /// The scale of each key.
    /// </summary>
    public readonly Vector3[] Scale = scale;
}

/// <summary>
/// An Ogre animation with its tracks indexed by bone handle (null: the animation does not move that bone).
/// </summary>
public sealed class AnimationClip(string name, float length, BoneTrack?[] tracks)
{
    /// <summary>
    /// The Ogre animation's name.
    /// </summary>
    public readonly string Name = name;

    /// <summary>
    /// The animation's length in seconds.
    /// </summary>
    public readonly float Length = length;

    /// <summary>
    /// The track of each bone handle; null where the animation does not move the bone.
    /// </summary>
    public readonly BoneTrack?[] Tracks = tracks;
}

/// <summary>
/// An animation as a layer of a pose: the clip with the tracks its ANIMATION record deletes and the bones it overrides
/// (docs/animation.md, "Startup preprocessing"). Immutable, one per (skeleton, record or animation name).
/// </summary>
public sealed class LayerDef(AnimationClip clip, bool[]? excluded, int[] overrides, string label, int rootMotionBone = -1)
{
    /// <summary>
    /// The clip the layer samples.
    /// </summary>
    public readonly AnimationClip Clip = clip;

    /// <summary>
    /// By bone handle: tracks ignored while sampling (null: none).
    /// </summary>
    public readonly bool[]? Excluded = excluded;

    /// <summary>
    /// The handles of the bones the layer overrides (blended in the second pass).
    /// </summary>
    public readonly int[] Override = overrides;

    /// <summary>
    /// A readable name: the record name with the clip name when they differ, else the clip name.
    /// </summary>
    public readonly string Label = label;

    /// <summary>
    /// The handle of <c>Bip01</c> when the clip moves the character (<see cref="AnimationMask.Relocates"/>), else -1: that track keeps only its
    /// height, as a state with translation disabled in Kenshi's Ogre (docs/formats/ogre-skeleton.md), and the simulation moves the character by
    /// the rest.
    /// </summary>
    public readonly int RootMotionBone = rootMotionBone;
}

/// <summary>
/// One animation of a pose: its definition, the time in seconds (looped over the length) and the blend weight.
/// </summary>
public readonly record struct ActiveLayer(LayerDef Def, float Time, float Weight);

/// <summary>
/// Bone transforms in model space (the skeleton's own space, before the character's placement) that replace the animated ones when posing
/// (<see cref="PoseScratch.PoseDerived"/>), by bone handle: a ragdoll's bodies. Only a root bone (no parent) takes <see cref="Position"/>; the others
/// keep their offset from the parent. <see cref="Weight"/> blends from the animated pose (0) to these (1).
/// </summary>
public sealed class BonePose(int boneCount)
{
    /// <summary>
    /// By bone handle: whether the bone takes this pose.
    /// </summary>
    public readonly bool[] Set = new bool[boneCount];

    /// <summary>
    /// By bone handle: the model-space rotation.
    /// </summary>
    public readonly Quaternion[] Rotation = new Quaternion[boneCount];

    /// <summary>
    /// By bone handle: the model-space position (used for root bones only).
    /// </summary>
    public readonly Vector3[] Position = new Vector3[boneCount];

    /// <summary>
    /// The blend from the animated pose (0) to this one (1).
    /// </summary>
    public float Weight = 1;
}

/// <summary>
/// Kenshi's two extra per-bone vectors and the movement scale that the body-shape sliders set (<see cref="CharacterShape"/>).
/// </summary>
public sealed class BodyShapeData(Vector3[] boneSize, Vector3[] positionalSize, float movementScale)
{
    /// <summary>
    /// The bone size vector per handle (1 where no slider sets it).
    /// </summary>
    public readonly Vector3[] BoneSize = boneSize;

    /// <summary>
    /// The positional size vector per handle (1 where no slider sets it).
    /// </summary>
    public readonly Vector3[] PositionalSize = positionalSize;

    /// <summary>
    /// The movement scale the sliders set; it scales the animated translations.
    /// </summary>
    public readonly float MovementScale = movementScale;
}

/// <summary>
/// An Ogre skeleton prepared for posing many characters: bones by handle, parents before children, the binding pose, the animations as
/// arrays and the layer definitions found so far. Immutable once built except for those caches (<see cref="Layer"/>), which any thread may
/// fill (the renderer and the simulation's ragdolls share rigs); posing itself (<see cref="PoseScratch"/>) only reads.
/// Maths: <see cref="PoseScratch.Pose"/> (Ogre's v1 node maths with Kenshi's changes, docs/formats/ogre-skeleton.md).
/// </summary>
public sealed class SkeletonRig
{
    /// <summary>
    /// The Ogre skeleton the rig was built from.
    /// </summary>
    public readonly OgreSkeleton Skeleton;

    /// <summary>
    /// The number of handles (the highest bone handle + 1).
    /// </summary>
    public readonly int BoneCount;

    /// <summary>
    /// The bone of each handle; null for handles no bone uses.
    /// </summary>
    public readonly OgreBone?[] ByHandle;

    /// <summary>
    /// Handles, parents before children.
    /// </summary>
    public readonly int[] Order;

    /// <summary>
    /// Parent handle per handle, -1 for roots.
    /// </summary>
    public readonly int[] Parent;

    /// <summary>
    /// The blend mode the file stores (average in every base-game file). Kenshi switches every character's skeleton instance to cumulative when it
    /// attaches its animation controller (docs/formats/ogre-skeleton.md, <b>Verified</b>), so posing never uses it.
    /// </summary>
    public readonly bool AverageBlend;

    /// <summary>
    /// Bone handles by name (the first bone of a name wins).
    /// </summary>
    readonly Dictionary<string, int> _handles = new(StringComparer.Ordinal);

    /// <summary>
    /// The compiled clips by name (case-insensitive), null for a name no source has.
    /// </summary>
    readonly ConcurrentDictionary<string, AnimationClip?> _clips = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The layer definitions by name (animal layers under a <c>\u0001</c> prefix), null for a name without a clip.
    /// </summary>
    readonly ConcurrentDictionary<string, LayerDef?> _layers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The binding pose in model space, with neutral shape: what the skin matrices are relative to (Ogre's <c>OldBone::_getOffsetTransform</c>).
    /// </summary>
    public readonly Vector3[] BindPosition = [], BindScale = [];

    /// <summary>
    /// The inverse of each bone's binding rotation in model space (see <see cref="BindPosition"/>).
    /// </summary>
    public readonly Quaternion[] BindRotationInverse = [];

    /// <summary>
    /// The skeletons the instance links (docs/animation.md, "Animation files"), read when a clip is first looked for past the own ones.
    /// </summary>
    readonly IReadOnlyList<Lazy<OgreSkeleton?>> _linked;

    /// <summary>
    /// The scratches given back, shared by every thread.
    /// </summary>
    readonly ConcurrentBag<PoseScratch> _scratches = [];

    /// <summary>
    /// The scratch the thread gave back last, kept on the thread: a crowd of one rig posed in parallel then takes no lock (the shared bag's
    /// were contended).
    /// </summary>
    [ThreadStatic] static PoseScratch? s_threadScratch;

    /// <summary>
    /// A rig of <paramref name="skeleton"/>: its bones by handle, the parents-first order and the binding pose.
    /// </summary>
    /// <param name="linked">The linked animation sources in link order: a clip the skeleton lacks is taken from the first that has it, its tracks
    /// applied by bone handle.</param>
    public SkeletonRig(OgreSkeleton skeleton, IReadOnlyList<Lazy<OgreSkeleton?>>? linked = null)
    {
        Skeleton = skeleton;
        this._linked = linked ?? [];
        BoneCount = skeleton.Bones.Count == 0 ? 0 : skeleton.Bones.Max(b => b.Handle) + 1;
        ByHandle = new OgreBone?[BoneCount];
        Parent = new int[BoneCount];
        Array.Fill(Parent, -1);
        foreach (var b in skeleton.Bones)
        {
            ByHandle[b.Handle] = b;
            _handles.TryAdd(b.Name, b.Handle);
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

    /// <summary>
    /// A working set for posing on the calling thread; give it back with <see cref="Return"/>.
    /// </summary>
    public PoseScratch Rent()
    {
        if (s_threadScratch is { } kept && kept.Rig == this)
        {
            s_threadScratch = null;
            return kept;
        }
        return _scratches.TryTake(out var s) ? s : new PoseScratch(this);
    }

    /// <summary>
    /// Gives <paramref name="scratch"/> back: it stays on the thread, and the one kept before goes to its rig's shared bag.
    /// </summary>
    public void Return(PoseScratch scratch)
    {
        if (s_threadScratch is { } kept) kept.Rig._scratches.Add(kept);
        s_threadScratch = scratch;
    }

    /// <summary>
    /// Handle of the bone called <paramref name="name"/>, or null.
    /// </summary>
    public int? Handle(string name) => _handles.TryGetValue(name, out var h) ? h : null;

    /// <summary>
    /// The animation called <paramref name="name"/> as arrays, the skeleton's own else the first linked source's (null: none has it).
    /// </summary>
    public AnimationClip? Clip(string name)
    {
        if (_clips.TryGetValue(name, out var cached)) return cached;
        var animation = Find(Skeleton, name);
        for (int i = 0; animation is null && i < _linked.Count; i++)
            if (_linked[i].Value is { } source) animation = Find(source, name);
        return _clips[name] = animation is null ? null : Compile(animation);
    }

    /// <summary>
    /// The animation of <paramref name="skeleton"/> called <paramref name="name"/> (case-insensitive), or null.
    /// </summary>
    static OgreAnimation? Find(OgreSkeleton skeleton, string name) =>
        skeleton.Animations.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The animation as flat per-bone track arrays; tracks of handles past <see cref="BoneCount"/> and empty ones are dropped.
    /// </summary>
    AnimationClip Compile(OgreAnimation animation)
    {
        var tracks = new BoneTrack?[BoneCount];
        foreach (var t in animation.Tracks)
        {
            if (t.Bone >= BoneCount || t.KeyFrames.Count == 0) continue;
            var keys = t.KeyFrames;
            tracks[t.Bone] = new BoneTrack([.. keys.Select(k => k.Time)], [.. keys.Select(k => k.Translation)], [.. keys.Select(k => k.Rotation)],
                    [.. keys.Select(k => k.Scale)]);
        }
        return new AnimationClip(animation.Name, animation.Length, tracks);
    }

    /// <summary>
    /// The layer for an ANIMATION record name or an Ogre animation name, with that record's deleted tracks and override bones
    /// (<see cref="AnimationMask.Find"/>), cached; null when the skeleton has no such animation.
    /// </summary>
    /// <param name="animal">The rig of an animal: <paramref name="name"/> is a creature technique's record name or a clip of its own skeleton, never
    /// a humanoid ANIMATION (whose masks, <c>delete tail</c> above all, would hit the animal's like-named bones); ANIMAL_ANIMATION records are not
    /// preprocessed, so a clip plays whole.</param>
    public LayerDef? Layer(GameDatabase? db, string name, bool animal = false)
    {
        if (_layers.TryGetValue(animal ? "\u0001" + name : name, out var cached)) return cached;
        var def = Build(db, name, animal);
        _layers[animal ? "\u0001" + name : name] = def;
        return def;
    }

    /// <summary>
    /// The layer of <paramref name="name"/>: the record's mask (a creature technique's or a plain one for an animal), its clip, deleted tracks,
    /// override bones and root-motion bone; null when no clip is found.
    /// </summary>
    LayerDef? Build(GameDatabase? db, string name, bool animal)
    {
        var mask = !animal ? AnimationMask.Find(db, name)
            : db?.OfType(Fcs.FcsRecordType.CombatTechnique).FirstOrDefault(r => r.Name == name) is { } technique
                    ? AnimationMask.FromTechnique(technique)
            : AnimationMask.Plain(name);
        var clip = Clip(mask.AnimationName) ?? Clip(name);
        LayerDef? def = null;
        if (clip is not null)
        {
            bool[]? excluded = null;
            foreach (var bone in mask.DeletedBones)
                if (Handle(bone) is { } h) (excluded ??= new bool[BoneCount])[h] = true;
            int[] overrides = [.. mask.OverrideBones.Select(Handle).OfType<int>()];
            def = new LayerDef(clip, excluded, overrides, mask.RecordName is { } r && r != clip.Name ? $"{r} ({clip.Name})" : clip.Name,
                mask.Relocates && Handle("Bip01") is { } root ? root : -1);
        }
        return def;
    }

    /// <summary>
    /// The body-shape vectors by handle for the sliders' result (<see cref="CharacterShape.Compute"/>); bones not named stay 1.
    /// </summary>
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
public sealed class PoseScratch
{
    /// <summary>
    /// The rig being posed.
    /// </summary>
    readonly SkeletonRig _rig;

    /// <summary>
    /// The rig being posed (the scratch pool checks it).
    /// </summary>
    internal SkeletonRig Rig => _rig;

    /// <summary>
    /// The local position and scale of each bone while posing.
    /// </summary>
    readonly Vector3[] _pos, _scale;

    /// <summary>
    /// The local rotation of each bone while posing.
    /// </summary>
    readonly Quaternion[] _rot;

    /// <summary>
    /// The posed model-space position and scale of each bone.
    /// </summary>
    public readonly Vector3[] DerivedPosition, DerivedScale;

    /// <summary>
    /// The posed model-space rotation of each bone.
    /// </summary>
    public readonly Quaternion[] DerivedRotation;

    /// <summary>
    /// A scratch with arrays sized for <paramref name="rig"/>.
    /// </summary>
    public PoseScratch(SkeletonRig rig)
    {
        this._rig = rig;
        int n = rig.BoneCount;
        _pos = new Vector3[n];
        _scale = new Vector3[n];
        _rot = new Quaternion[n];

        DerivedPosition = new Vector3[n];
        DerivedScale = new Vector3[n];
        DerivedRotation = new Quaternion[n];
    }

    /// <summary>
    /// Poses the skeleton with <paramref name="layers"/> (empty: the binding pose) and the body shape <paramref name="shape"/> (null: neutral);
    /// writes each bone's skin matrix into <paramref name="skin"/> (<see cref="SkeletonRig.BoneCount"/> entries, System.Numerics row-vector form,
    /// the memory a GLSL <c>mat4</c> reads as its column-vector form).
    /// </summary>
    public void Pose(BodyShapeData? shape, ReadOnlySpan<ActiveLayer> layers, Span<Matrix4x4> skin, BonePose? overrides = null)
    {
        PoseDerived(shape, layers, overrides);
        for (int h = 0; h < _rig.BoneCount; h++) skin[h] = Offset(h);
    }

    /// <summary>
    /// The posed derived (model-space) transform of a bone: scale, rotation, translation.
    /// </summary>
    public Matrix4x4 BoneTransform(int h) =>
        Matrix4x4.CreateScale(DerivedScale[h]) * Matrix4x4.CreateFromQuaternion(DerivedRotation[h]) * Matrix4x4.CreateTranslation(DerivedPosition[h]);

    /// <summary>
    /// An object attached to bone <paramref name="h"/> at <paramref name="offset"/> (rotation and translation): Kenshi's tag point takes the bone's
    /// rotation and position, its offset scaled by the bone's Y scale like a child bone's, but not the bone's scale (docs/formats/ogre-skeleton.md
    /// "Binding worn meshes to the character"), so a weapon keeps its shape.
    /// </summary>
    public Matrix4x4 TagTransform(int h, Matrix4x4 offset)
    {
        offset.Translation *= DerivedScale[h].Y;
        return offset * Matrix4x4.CreateFromQuaternion(DerivedRotation[h]) * Matrix4x4.CreateTranslation(DerivedPosition[h]);
    }

    /// <summary>
    /// Poses the derived transforms; <paramref name="overrides"/> (null: none) replaces the model-space rotations of some bones afterwards, and their
    /// children follow.
    /// </summary>
    public void PoseDerived(BodyShapeData? shape, ReadOnlySpan<ActiveLayer> layers, BonePose? overrides = null)
    {
        int count = _rig.BoneCount;
        BindLocal(count);
        float movement = shape?.MovementScale ?? 1;
        BlendLayers(layers, count, movement);
        Derive(shape, overrides);
    }

    /// <summary>
    /// Starts every bone's local transform at its binding one.
    /// </summary>
    void BindLocal(int count)
    {
        for (int h = 0; h < count; h++)
        {
            var b = _rig.ByHandle[h];
            _pos[h] = b?.Position ?? Vector3.Zero;
            _rot[h] = b?.Orientation ?? Quaternion.Identity;
            _scale[h] = b?.Scale ?? Vector3.One;
        }
    }

    /// <summary>
    /// Adds the layers' weighted keys to the local transforms: the layers without override bones first, then those with.
    /// </summary>
    void BlendLayers(ReadOnlySpan<ActiveLayer> layers, int count, float movement)
    {
        // Cumulative blending (Kenshi sets it on every character's skeleton): each layer adds its weighted keys; the simulation keeps each body
        // half's weights at a total of 1 (AnimationSystem.Publish), and the posture libraries add on top.
        for (int pass = 0; pass < 2; pass++)
        {
            bool overridePass = pass == 1;
            foreach (var layer in layers)
            {
                if ((layer.Def.Override.Length > 0) != overridePass) continue;
                float w = Math.Max(layer.Weight, 0);
                if (w <= 0) continue;
                if (overridePass) PullOverrideBones(layer.Def, w, count);
                AddLayer(layer, w, count, movement);
            }
        }
    }

    /// <summary>
    /// Pulls the accumulated rotations of the layer's override bones back toward their binding ones by <paramref name="w"/>.
    /// </summary>
    void PullOverrideBones(LayerDef def, float w, int count)
    {
        foreach (int h in def.Override)
        {
            // Kenshi's override bones: pull the accumulated rotation back toward the binding one by the weight
            // (fully at 0.99 and above) before adding this animation's rotation.
            if (h >= count || _rig.ByHandle[h] is not { } b) continue;
            _rot[h] = w >= 0.99f ? b.Orientation : Nlerp(_rot[h], b.Orientation, w);
        }
    }

    /// <summary>
    /// Adds one layer's keys at its time, weighted by <paramref name="w"/>, to the local transforms of the bones it does not exclude.
    /// </summary>
    void AddLayer(in ActiveLayer layer, float w, int count, float movement)
    {
        var clip = layer.Def.Clip;
        var excluded = layer.Def.Excluded;
        float time = clip.Length > 0 ? ((layer.Time % clip.Length) + clip.Length) % clip.Length : 0;
        for (int h = 0; h < count; h++)
        {
            var track = clip.Tracks[h];
            if (track is null || excluded is not null && excluded[h]) continue;
            Sample(track, time, out var t, out var r, out var s);
            if (h == layer.Def.RootMotionBone) t = new Vector3(0, t.Y, 0);
            _pos[h] += t * (w * movement);
            _rot[h] = Quaternion.Normalize(_rot[h] * Nlerp(Quaternion.Identity, r, w));   // Node::rotate, local space
            if (s != Vector3.One) _scale[h] *= Vector3.One + (s - Vector3.One) * w;
        }
    }

    /// <summary>
    /// The derived (model-space) transforms from the local ones, parents first, with the body shape's sizes; then the overrides.
    /// </summary>
    void Derive(BodyShapeData? shape, BonePose? overrides)
    {
        var boneSize = shape?.BoneSize;
        var positionalSize = shape?.PositionalSize;
        foreach (int h in _rig.Order)
        {
            int parent = _rig.Parent[h];
            var size = boneSize is null ? Vector3.One : boneSize[h];
            if (parent < 0)
            {
                DerivedPosition[h] = _pos[h];
                DerivedRotation[h] = _rot[h];
                DerivedScale[h] = size * _scale[h];
            }
            else
            {
                // Kenshi's modified Ogre: derived scale = bone size × own scale (not inherited); the child's offset × its positional size
                // is scaled by the Y component of the parent's derived scale on all axes.
                var positional = positionalSize is null ? Vector3.One : positionalSize[h];
                DerivedRotation[h] = DerivedRotation[parent] * _rot[h];
                DerivedScale[h] = size * _scale[h];
                DerivedPosition[h] = Vector3.Transform(_pos[h] * positional * DerivedScale[parent].Y,
                        DerivedRotation[parent]) + DerivedPosition[parent];
            }

            if (overrides is not null && h < overrides.Set.Length && overrides.Set[h]) ApplyOverride(overrides, h, parent);
        }
    }

    /// <summary>
    /// Blends bone <paramref name="h"/>'s derived rotation (and a root bone's position) to the override's.
    /// </summary>
    void ApplyOverride(BonePose overrides, int h, int parent)
    {
        // A ragdoll's body sets its bone's rotation (and a root bone's position) in model space, as Kenshi writes a simulated body's
        // orientation into its manually controlled bone (docs/game/ragdolls.md, "Per frame"); the weight blends from the animated pose (the
        // 0.75 s of getting up).
        float w = overrides.Weight;
        DerivedRotation[h] = w >= 1 ? overrides.Rotation[h] : Nlerp(DerivedRotation[h], overrides.Rotation[h], w);
        if (parent < 0)
            DerivedPosition[h] = Vector3.Lerp(DerivedPosition[h], overrides.Position[h], Math.Clamp(w, 0, 1));
    }

    /// <summary>
    /// Ogre's (and Kenshi's) skinning transform: the vertex relative to the bone's binding position is scaled by derived scale / binding
    /// scale in the binding pose's model axes, rotated by derived × inverse binding orientation and moved to the derived position.
    /// </summary>
    Matrix4x4 Offset(int h)
    {
        var s = DerivedScale[h] / _rig.BindScale[h];
        var r = DerivedRotation[h] * _rig.BindRotationInverse[h];
        return Matrix4x4.CreateTranslation(-_rig.BindPosition[h]) * Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r)
            * Matrix4x4.CreateTranslation(DerivedPosition[h]);
    }

    /// <summary>
    /// Normalised linear interpolation from <paramref name="a"/> to <paramref name="b"/>, on the shorter arc.
    /// </summary>
    static Quaternion Nlerp(Quaternion a, Quaternion b, float f)
    {
        if (Quaternion.Dot(a, b) < 0) b = -b;
        return Quaternion.Normalize(new Quaternion(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f, a.W + (b.W - a.W) * f));
    }

    /// <summary>
    /// The track's key values at <paramref name="time"/>: clamped to the first and last keys, linearly interpolated between (rotations by nlerp).
    /// </summary>
    static void Sample(BoneTrack track, float time, out Vector3 t, out Quaternion r, out Vector3 s)
    {
        var times = track.Times;
        if (times.Length == 1 || time <= times[0])
        {
            t = track.Translation[0];
            r = track.Rotation[0];
            s = track.Scale[0];
            return;
        }
        int last = times.Length - 1;
        if (time >= times[last])
        {
            t = track.Translation[last];
            r = track.Rotation[last];
            s = track.Scale[last];
            return;
        }
        int lo = 0, hi = last;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (times[mid] <= time) lo = mid;
            else hi = mid;
        }
        float f = (time - times[lo]) / Math.Max(times[hi] - times[lo], 1e-6f);
        t = Vector3.Lerp(track.Translation[lo], track.Translation[hi], f);
        r = Nlerp(track.Rotation[lo], track.Rotation[hi], f);
        s = Vector3.Lerp(track.Scale[lo], track.Scale[hi], f);
    }
}