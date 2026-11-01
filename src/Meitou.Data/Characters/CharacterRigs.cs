using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Ogre;

namespace Meitou.Data.Characters;

/// <summary>
/// How an appearance is posed: its skeleton, its body shape, how far the shape lifts the feet, and the posture layers that always apply. The renderer and the
/// simulation's ragdolls take it from one <see cref="CharacterRigs"/>, so the pose a ragdoll starts from is the drawn one.
/// </summary>
public sealed class CharacterPoseSetup
{
    public required SkeletonRig Rig { get; init; }
    public BodyShapeData? Shape { get; init; }
    /// <summary>How far the body shape lifts the feet in the bind pose (negative: sinks them); the character is drawn lowered by it.</summary>
    public float GroundOffset { get; init; }
    /// <summary>The posture libraries (<c>postures</c>, <c>neck set</c>, <c>shoulder set</c>) held at the body file's sliders, weight 1.</summary>
    public IReadOnlyList<(LayerDef Def, float Time)> Fixed { get; init; } = [];
    /// <summary>The default idle: the body file's <c>idle stance</c>.</summary>
    public string Idle { get; init; } = "idle_stand_relax";
    public bool Animal { get; init; }
}

/// <summary>
/// Skeleton rigs by file and pose setups by appearance, read once; safe from any thread. <paramref name="findMesh"/> finds a mesh the way Kenshi does (bare name
/// through resources.cfg first), <paramref name="find"/> any other art file; both return a full path or null.
/// </summary>
public sealed class CharacterRigs(GameDatabase db, Func<string, string?> findMesh, Func<string, string?> find)
{
    readonly ConcurrentDictionary<string, Lazy<SkeletonRig>> rigs = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Lazy<string?>> skeletonNames = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<CharacterAppearance, Lazy<CharacterPoseSetup?>> setups = new(ReferenceEqualityComparer.Instance);

    public GameDatabase Database => db;

    /// <summary>The rig of the skeleton file <paramref name="name"/>, or null when it is not in the install.</summary>
    public SkeletonRig? Rig(string name)
    {
        var path = find(name);
        if (path is null) return null;
        return rigs.GetOrAdd(path, p => new Lazy<SkeletonRig>(() => new SkeletonRig(OgreSkeletonReader.ReadFile(p)))).Value;
    }

    readonly ConcurrentDictionary<string, Lazy<SkeletonRig>> linkedRigs = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Lazy<OgreSkeleton?>> sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The rig of the skeleton file <paramref name="name"/> linking <paramref name="animationFiles"/> (<see cref="AnimationFiles"/>, FCS paths in link order); a file
    /// not in the install is left out, as the game links only the ones it finds. Null when the skeleton is not in the install.
    /// </summary>
    public SkeletonRig? Rig(string name, IReadOnlyList<string> animationFiles)
    {
        if (Rig(name) is not { } own) return null;
        var found = animationFiles.Select(f => find(AnimationFiles.BareName(f))).OfType<string>().ToList();
        if (found.Count == 0) return own;
        return linkedRigs.GetOrAdd(find(name) + "|" + string.Join("|", found),
            _ => new Lazy<SkeletonRig>(() => new SkeletonRig(own.Skeleton, [.. found.Select(Source)]))).Value;
    }

    /// <summary>The rig of the skeleton file <paramref name="name"/> as <paramref name="c"/>'s body links it: with its race's and record's animation files.</summary>
    public SkeletonRig? Rig(string name, CharacterAppearance c) => Rig(name, AnimationFiles.Of(db, c.Race, c.Character ?? c.Animal, c.Female));

    // A linked skeleton, read once per file whichever rigs link it; null when it cannot be read (the game logs and plays without it).
    Lazy<OgreSkeleton?> Source(string path) => sources.GetOrAdd(path, p => new Lazy<OgreSkeleton?>(() =>
    {
        try { return OgreSkeletonReader.ReadFile(p); }
        catch (Exception e) when (e is OgreFormatException or IOException or EndOfStreamException or InvalidDataException) { return null; }
    }));

    /// <summary>The full path of a mesh file, or null.</summary>
    public string? FindMesh(string fcsPath) => findMesh(fcsPath);

    /// <summary>The full path of any other art file (a ragdoll, a cooked mesh), or null.</summary>
    public string? Find(string fcsPath) => find(fcsPath);

    /// <summary>The skeleton a body mesh file links, read from the file once per path; throws what the reader throws.</summary>
    public string? BodySkeletonName(string path) => skeletonNames.GetOrAdd(path, p => new Lazy<string?>(() => OgreMeshReader.ReadFile(p).SkeletonName)).Value;

    /// <summary>The pose setup of <paramref name="c"/> (cached per appearance); null when its body mesh or skeleton cannot be found or read.</summary>
    public CharacterPoseSetup? For(CharacterAppearance c) =>
        setups.GetOrAdd(c, static (a, self) => new Lazy<CharacterPoseSetup?>(() => self.Build(a)), this).Value;   // a static factory: nothing allocated on a hit

    CharacterPoseSetup? Build(CharacterAppearance c)
    {
        var bodyPath = findMesh(c.BodyMesh);
        if (bodyPath is null) return null;
        SkeletonRig? rig;
        try { rig = BodySkeletonName(bodyPath) is { } name ? Rig(name, c) : null; }
        catch (Exception e) when (e is OgreFormatException or IOException or EndOfStreamException or InvalidDataException) { return null; }
        if (rig is null) return null;
        bool animal = c.Animal is not null;
        var shape = animal ? null : Shape(c, rig);
        var setup = new List<(LayerDef, float)>();
        string idle = "idle_stand_relax";
        // The posture libraries, held at the body file's sliders (docs/animation.md, "Posture sliders").
        if (c.Body is { } bodyFile)
        {
            idle = bodyFile.IdleStance ?? idle;
            foreach (var (animation, slider) in new[] { ("postures", "Posture"), ("neck set", "Neck position"), ("shoulder set", "Shoulder set") })
                if (bodyFile.Floats.ContainsKey(slider) && rig.Layer(db, animation) is { } def)
                    setup.Add((def, def.Clip.Length * bodyFile.Get(slider) / 100));
        }
        return new CharacterPoseSetup { Rig = rig, Shape = shape, GroundOffset = animal ? 0 : GroundOffset(c, rig, shape), Fixed = setup, Idle = idle, Animal = animal };
    }

    /// <summary>Body-shape sliders as bone sizes (docs/animation.md, "Body shape sliders"); only skeletons with exactly 30 bones, as in Kenshi.</summary>
    /// <param name="build">The stat-driven body (its muscle and starvation) in place of the appearance's own.</param>
    /// <param name="whole">With every limb (<see cref="CharacterShapeInput.Missing"/> empty): the shape the ground offset is measured on.</param>
    BodyShapeData? Shape(CharacterAppearance c, SkeletonRig rig, BodyBuild? build = null, bool whole = false)
    {
        if (rig.BoneCount != CharacterShape.HumanBoneCount) return null;
        var input = c.Shape ?? CharacterShape.InputFor(db, c);
        if (build is { } b) input = input with { Muscle = b.Muscle, Starvation = b.Starvation };
        if (whole && input.Missing.Count > 0) input = input with { Missing = new HashSet<Limb>() };
        var shape = CharacterShape.Compute(input, name => rig.Handle(name) is not null);
        return rig.Shape(shape.BoneSize, shape.PositionalSize, shape.MovementScale);
    }

    /// <summary>
    /// The bone shape and ground offset of <paramref name="c"/> with the stat-driven <paramref name="build"/> (its muscle and starvation); null for an animal or a skeleton
    /// the body sliders do not apply to.
    /// </summary>
    public (BodyShapeData? Shape, float GroundOffset)? Reshape(CharacterAppearance c, BodyBuild build)
    {
        if (For(c) is not { Animal: false, Rig: { BoneCount: CharacterShape.HumanBoneCount } rig }) return null;
        var data = Shape(c, rig, build);
        return (data, GroundOffset(c, rig, data, build));
    }

    static readonly string[] FootBones = ["Bip01 L Foot", "Bip01 R Foot", "Bip01 L Toe0", "Bip01 R Toe0"];

    /// <summary>
    /// How far <paramref name="shape"/> raises the lowest foot bone in the bind pose over the neutral shape (docs/animation.md, "Body shape sliders"): the bone sizes
    /// scale the legs about the hips while <c>Bip01</c> keeps its height, so a short body floats and a tall one sinks. What grounds them in Kenshi was not found;
    /// lowering the character by this is an engine choice. It is measured with every limb: a lost leg's foot shrinks to its knee, and a body with no legs lowered by
    /// that would lie under the ground while it crawls and sit low on a carrier's shoulder. Less the game's lift from boots and prosthetic legs
    /// (<see cref="CharacterShape.FeetLiftOf"/>): high-heeled boots raise the body.
    /// </summary>
    float GroundOffset(CharacterAppearance c, SkeletonRig rig, BodyShapeData? shape, BodyBuild? build = null) =>
        (shape is not null && (c.Shape ?? CharacterShape.InputFor(db, c)).Missing.Count > 0 ? GroundOffset(rig, Shape(c, rig, build, whole: true)) : GroundOffset(rig, shape))
        - CharacterShape.FeetLiftOf(c.Parts);

    static float GroundOffset(SkeletonRig rig, BodyShapeData? shape)
    {
        if (shape is null) return 0;
        var handles = FootBones.Select(rig.Handle).OfType<int>().ToArray();
        if (handles.Length == 0) return 0;
        var scratch = rig.Rent();
        try
        {
            scratch.PoseDerived(null, []);
            float neutral = handles.Min(h => scratch.DerivedPosition[h].Y);
            scratch.PoseDerived(shape, []);
            return handles.Min(h => scratch.DerivedPosition[h].Y) - neutral;
        }
        finally { rig.Return(scratch); }
    }

    /// <summary>The model-space placement of a character drawn at <paramref name="position"/> facing <paramref name="yaw"/> at size <paramref name="scale"/> (the renderer's world matrix).</summary>
    public static Matrix4x4 Placement(CharacterPoseSetup setup, Vector3 position, float yaw, float scale)
    {
        var world = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(position - new Vector3(0, setup.GroundOffset, 0));
        return scale != 1 && scale > 0 ? Matrix4x4.CreateScale(scale) * world : world;
    }

    /// <summary>The same for a node turned by <paramref name="rotation"/> (a carried body's): lowered by the ground offset in its own frame, then turned and moved.</summary>
    public static Matrix4x4 Placement(CharacterPoseSetup setup, Vector3 position, Quaternion rotation, float scale)
    {
        var world = Matrix4x4.CreateTranslation(0, -setup.GroundOffset, 0) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        return scale != 1 && scale > 0 ? Matrix4x4.CreateScale(scale) * world : world;
    }
}
