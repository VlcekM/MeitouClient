using System.Collections.Concurrent;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using Meitou.Data.Characters;
using Meitou.Data.Physics;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Saving;

namespace Meitou.Simulation.Ragdolls;

/// <summary>The parts of a body that are a ragdoll (the game's <c>RagdollPart</c>, <b>Verified</b> in <c>fcs_enums.def</c>; LOCATIONAL_DAMAGE <c>collapse part</c> uses it).</summary>
[Flags]
public enum RagdollPart
{
    None = 0, Whole = 1, RightArm = 2, LeftArm = 4, Head = 8, RightLeg = 16, LeftLeg = 32, CarryMode = 2048,
    Limbs = RightArm | LeftArm | Head | RightLeg | LeftLeg,
}

/// <summary>What the animation system asks about ragdolls (in its parallel phase: answers are the last tick's state or fixed data).</summary>
public interface IRagdollState
{
    /// <summary>Whether the character's look has a ragdoll (its race names a ragdoll file that binds to its skeleton).</summary>
    bool HasRagdoll(CharacterCold cold);
    /// <summary>Whether the whole body was a ragdoll at the end of the last tick.</summary>
    bool WasWhole(CharacterId id);
}

/// <summary>What the world asks to publish a character's ragdoll.</summary>
public interface IRagdollSource
{
    /// <summary>The bones the ragdoll sets, in the model space of the character's drawn placement; null without a ragdoll.</summary>
    BonePose? Publish(CharacterId id);
}

/// <summary>
/// Physics ragdolls (docs/game/ragdolls.md; engine choices in docs/simulation.md "Ragdolls"): the whole body of a knocked-out or dead character, the forearms and head of a
/// carried one (all but the carry bone of an animal), and a human's collapsed limbs. Bodies are built from the race's ragdoll file on the character's animated pose; bodies the
/// part mask does not simulate are pinned to the animation (kinematic). While the whole body is a ragdoll the character's position follows the root body. Runs serially in the
/// slow-world step after carrying and animation, and steps one Bepu scene on the calling thread, so the state hash does not depend on the thread count.
/// </summary>
public sealed class RagdollSystem : ITickSystem, IStateHashed, IRagdollState, IRagdollSource, IDisposable
{
    /// <summary>Getting up blends from the ragdoll's pose to the animation over this many seconds (<b>Observed</b>, <c>FUN_1407d3130</c>).</summary>
    public const float GetUpBlend = 0.75f;
    /// <summary>The edge of a square of static ground and buildings the scene is made of, in world units, on a grid from the origin (engine choice).</summary>
    public const float TileSize = 32;
    /// <summary>Freezing (docs/game/ragdolls.md "Sleep and limits"): not before this age, then when this share of the bodies is still; at the latest after <see cref="FreezeLatest"/>.</summary>
    public const float FreezeEarliest = 2, FreezeShare = 0.6f, FreezeLatest = 7;
    /// <summary>A body is still under these squared speeds (the game's PhysX sleep velocities: linear² 2.25, angular² 1.96).</summary>
    const float StillLinear = 2.25f, StillAngular = 1.96f;
    /// <summary>How far under the terrain a body's centre must get, in world units, to count as through it (engine choice; a resting limb's centre is at least its radius, 0.4 and up, above it).</summary>
    const float SinkDepth = 1, MaxLift = 8;

    readonly CharacterRigs rigs;
    IRagdollEnvironment? environment;
    readonly Bodies.BodyOptions? options;
    readonly RagdollPhysics physics = new();
    readonly Dictionary<CharacterId, Ragdoll> ragdolls = [];
    readonly List<CharacterId> order = [];
    readonly ConcurrentDictionary<CharacterAppearance, Lazy<(PhysicsFile File, string Path)?>> files = new(ReferenceEqualityComparer.Instance);
    readonly ConcurrentDictionary<(string Path, SkeletonRig Rig, int Scale), Lazy<RagdollDefinition?>> definitions = new();
    readonly Dictionary<RagdollDefinition, (TypedIndex Shape, BodyInertia Inertia)[]> shapes = new(ReferenceEqualityComparer.Instance);
    IAnimationSource? animations;
    int nextTag = 1;

    /// <param name="environment">Ground and buildings; null: the world's walkable ground (<see cref="WalkabilityEnvironment"/>).</param>
    /// <param name="options">The body options whose <see cref="Bodies.BodyOptions.AdvancedRagdolls"/> gives a new ragdoll its start speed; null: at rest, as the original.</param>
    public RagdollSystem(CharacterRigs rigs, IRagdollEnvironment? environment = null, Bodies.BodyOptions? options = null)
    {
        this.rigs = rigs;
        this.environment = environment;
        this.options = options;
        RagdollPhysics.WarmUpOnce();
    }

    /// <summary>Ragdolls in play (frozen ones included) and how many are moving.</summary>
    public int Count => ragdolls.Count;
    public int Moving { get; private set; }
    public int Tiles => physics.TileCount;

    sealed class Ragdoll
    {
        public required CharacterId Id;
        public required RagdollDefinition Definition;
        public required CharacterPoseSetup Setup;
        public required float Scale;
        /// <summary>An animal's carry bone (RACE <c>carry bone</c>, else the skeleton's root; <c>FUN_1407d3850</c>).</summary>
        public string CarryBone = "";
        public RagdollPart Mask;
        public int Tag;
        /// <summary>By body: its handle when it is in the scene.</summary>
        public BodyHandle?[] Bodies = [];
        public bool[] Dynamic = [];
        public readonly List<ConstraintHandle> Joints = [];
        /// <summary>Advanced ragdolls: the joints have soft limits (set when the bodies are built), and the relaxed pull of <see cref="Tone"/> (its servos in <see cref="Relax"/>).</summary>
        public bool Soft;
        public Tone Tone;
        public readonly List<ConstraintHandle> Relax = [];
        /// <summary>By body: the last world pose (also kept for bodies not in the scene while getting up).</summary>
        public RigidTransform[] Pose = [];
        public bool Frozen;
        public float Age;
        /// <summary>Seconds of the get-up blend left (0: none), and the bones it blends from, in world space.</summary>
        public float BlendLeft;
        /// <summary>The published bones and the placement they were made in: a frozen ragdoll still in it hands over the same object (the renderer poses it again only for new bones).</summary>
        public BonePose? Published;
        public Matrix4x4 PublishedIn;
        public bool InScene => Bodies.Any(b => b is not null);
    }

    // ---- what the animation system asks ----

    public bool HasRagdoll(CharacterCold cold) => cold.Appearance is { } look && FileOf(look) is not null && rigs.For(look) is not null;

    public bool WasWhole(CharacterId id) => ragdolls.TryGetValue(id, out var r) && (r.Mask & RagdollPart.Whole) != 0;

    public BonePose? Publish(CharacterId id) => ragdolls.TryGetValue(id, out var r) ? r.Published : null;

    // A static factory given the rigs: nothing allocated on a hit.
    (PhysicsFile File, string Path)? FileOf(CharacterAppearance look) => files.GetOrAdd(look, static (a, rigs) => new(() =>
    {
        // RACE male ragdoll / female ragdoll (the female file only for a female look; docs/game/ragdolls.md "RagdollPart and requests").
        var name = a.Race.GetPath(a.Female ? "female ragdoll" : "male ragdoll") is { Length: > 0 } f ? f : a.Race.GetPath("male ragdoll");
        if (string.IsNullOrEmpty(name) || rigs.Find(name) is not { } path) return null;
        try { return (PhysicsFile.ReadFile(path), path); }
        catch (Exception e) when (e is FormatException or IOException) { return null; }
    }), rigs).Value;

    RagdollDefinition? DefinitionOf(CharacterAppearance look, SkeletonRig rig, float scale)
    {
        if (FileOf(look) is not { } file) return null;
        int key = (int)MathF.Round(scale * 100);
        return definitions.GetOrAdd((file.Path, rig, key), _ => new(() => RagdollDefinition.Build(file.File, rig, key / 100f, Hull))).Value;
    }

    Vector3[]? Hull(string cooked)
    {
        if (rigs.Find(cooked) is not { } path) return null;
        try { return CookedMeshReader.ReadConvex(File.ReadAllBytes(path)).Vertices; }
        catch (Exception e) when (e is FormatException or IOException or InvalidDataException) { return null; }
    }

    // ---- the tick ----

    /// <summary>
    /// <c>Character::isRagdoll</c> (<c>FUN_1407d1440</c>): carried or whole-body ragdolled, the WHOLE or CARRY_MODE parts of <see cref="Wanted"/>; the simulation's rule, whether or not the host
    /// has the rig to show it (the original needs a visual or death to ragdoll).
    /// </summary>
    internal static bool IsRagdoll(CharacterCold cold) => (Wanted(cold) & (RagdollPart.Whole | RagdollPart.CarryMode)) != 0;

    /// <summary>The parts that should be a ragdoll now (docs/game/ragdolls.md "Triggers").</summary>
    static RagdollPart Wanted(CharacterCold cold)
    {
        // In a bed or a cage: no ragdoll ("on" requests are refused in furniture, and using it releases all).
        if (cold.InSomething != Locks.HeldState.None) return RagdollPart.None;
        bool carried = !cold.CarriedBy.IsNone;
        if (cold.Medical is { Incapacitated: true }) return carried ? RagdollPart.CarryMode : RagdollPart.Whole;
        if (carried) return RagdollPart.CarryMode;
        // A human's collapsed limbs, not a robotic one (animals have none).
        if (cold.Fighter is { IsAnimal: true } || cold.Medical is not { } medical || cold.Race is not { } race) return RagdollPart.None;
        var parts = RagdollPart.None;
        // A leg goes by the crawl's own test (the tick keeps its bits on while the character crawls), an arm by h - s + rig < 0.
        foreach (var p in medical.PartSpan)
            if (p.Template.Collapses && (p.IsLeg ? p.LegDown(medical.LegKoPoint) : p.IsDown) && !p.IsRobotic(race)) parts |= (RagdollPart)p.Template.CollapsePart & RagdollPart.Limbs;
        return parts;
    }

    public void SlowWorld(World world)
    {
        animations ??= world.Systems.OfType<IAnimationSource>().FirstOrDefault();
        var table = world.Characters;
        var env = environment ??= new WalkabilityEnvironment(world.Walkability);
        float dt = world.TickSeconds, stepDt = RagdollPhysics.StepOf(dt, world.PhysicsSpeed);   // the timers run in game time, the physics in real time
        long tick = world.Tick;

        // Ragdolls of characters that are gone.
        foreach (var id in order)
            if (!table.TryResolveNext(id, out _)) Destroy(ragdolls[id]);
        order.RemoveAll(id => !ragdolls.ContainsKey(id));

        // 1. Masks, building and pinning.
        var next = table.Next;
        for (int i = 0; i < next.Length; i++)
        {
            if (!next[i].Alive || table.Cold(i) is not { } cold) continue;
            // Meitou (BodyOptions.AdvancedRagdolls): a new whole-body ragdoll goes on as the character moved, pushed by the blow that put it down (CombatSystem.Knockdown), else as it moved last tick.
            bool advanced = options is { AdvancedRagdolls: true };
            var (launch, push) = !advanced ? default : cold.Blow ?? (table.Previous[i].Velocity, Vector3.Zero);
            cold.Blow = null;
            cold.CarryAttach = cold.CarriedBy.IsNone ? null : AttachOf(table, cold);
            var id = table.IdOf(i);
            ragdolls.TryGetValue(id, out var r);
            // The look's ragdoll file asked only of a character that wants one (two cache lookups per standing character every tick were most of this loop in a big battle).
            var wanted = cold.Appearance is null ? RagdollPart.None : Wanted(cold);
            if (wanted != RagdollPart.None && !HasRagdoll(cold)) wanted = RagdollPart.None;
            // A ragdoll a save holds (Saving.RagdollSave): taken the first time the character is seen, used when it starts as a whole body.
            SavedRagdoll? restore = null;
            if (cold.Save is { Ragdoll: { } saved })
            {
                cold.Save.Ragdoll = null;
                if (r is null && (wanted & RagdollPart.Whole) != 0) restore = saved;
            }
            if (r is null && wanted == RagdollPart.None) continue;
            if (r is null)
            {
                float scale = cold is { Fighter.IsAnimal: true, Medical: { } size } ? size.ScaleK : 1;
                var setup = rigs.For(cold.Appearance!)!;
                if (DefinitionOf(cold.Appearance!, setup.Rig, scale) is not { } definition) continue;
                var carryBone = cold.Appearance!.Race.GetString("carry bone");
                if (string.IsNullOrEmpty(carryBone) || setup.Rig.Handle(carryBone) is null) carryBone = setup.Rig.Skeleton.Bones.FirstOrDefault(b => b.Parent is null)?.Name ?? "";
                r = new Ragdoll { Id = id, Definition = definition, Setup = setup, Scale = scale, CarryBone = carryBone };
                r.Pose = new RigidTransform[definition.Bodies.Length];
                ragdolls[id] = r;
                order.Add(id);
            }
            var placement = Placement(r, cold, next[i]);
            RigidTransform[]? animated = null;
            if (wanted != r.Mask)
            {
                // Getting up: the whole body leaves the ragdoll and blends back into the animation, facing from the root to the head (not when it is
                // laid in a bed or a cage, which places it).
                if ((r.Mask & RagdollPart.Whole) != 0 && (wanted & RagdollPart.Whole) == 0 && (wanted & RagdollPart.CarryMode) == 0 && cold.InSomething == Locks.HeldState.None)
                    StartGetUp(world, r, ref next[i]);
                animated = Animated(r, cold, placement);
                bool restored = restore is not null && Fits(r, restore, next[i].Position);
                Rebuild(r, wanted, restored ? restore!.Pose : animated, restored ? default : (launch, push), advanced, world.Seed);
                if (restored)
                {
                    r.Age = restore!.Age;
                    if (restore.Frozen) Freeze(r);
                }
            }
            if (r.Mask == RagdollPart.None) continue;
            if (!r.Frozen && ToneOf(cold, r.Mask, advanced) is var tone && tone != r.Tone) SetTone(r, tone);
            if (!r.Frozen && r.Dynamic.Any(d => !d))
                Pin(r, animated ?? Animated(r, cold, placement), dt);
            if (!r.Frozen) physics.Cover(r.Pose[r.Definition.Root].Position, 12 * r.Scale, env, tick);
        }

        world.Profile.Part("rag setup");   // F12: the masks, the builds, the pins and the ground tiles
        // 2. The step.
        Moving = 0;
        foreach (var id in order) if (ragdolls[id] is { Frozen: false, Mask: not RagdollPart.None }) Moving++;
        // The physics on the world's thread count (its own workers: the world's pool is idle in this serial phase).
        if (Moving > 0 && threads is null && world.Settings.Threads > 1) threads = new BepuUtilities.ThreadDispatcher(world.Settings.Threads);
        if (Moving > 0)
        {
            foreach (var id in order) if (ragdolls[id] is { Frozen: false, Mask: not RagdollPart.None } floating) Buoy(world, floating, stepDt);
            physics.Step(stepDt, threads);
        }
        world.Profile.Part("rag step");

        // 3. Poses back, the whole body's position, freezing, publishing.
        foreach (var id in order)
        {
            var r = ragdolls[id];
            if (!table.TryResolveNext(id, out int slot)) continue;
            ref var hot = ref table.Next[slot];
            var cold = table.Cold(slot)!;
            if (r.Mask != RagdollPart.None && !r.Frozen)
            {
                Surface(r, env);
                ReadBack(r);
                // In water the freeze timer runs at half speed (with the buoyancy, Buoy).
                r.Age += Afloat(world, r) ? dt * 0.5f : dt;
                if ((r.Mask & RagdollPart.Whole) != 0)
                {
                    var root = r.Pose[r.Definition.Root].Position;
                    float ground = env.GroundHeight(root.X, root.Z, root.Y);
                    // A root far under the ground is lifted back onto it (FUN_1407d4470: more than 200 below), and frozen.
                    if (root.Y < ground - 200) { Shift(r, new Vector3(0, ground + 2 - root.Y, 0)); Freeze(r); }
                    else if (r.Age >= FreezeLatest || r.Age >= FreezeEarliest && StillShare(r) >= FreezeShare) Freeze(r);
                    hot.Position = r.Pose[r.Definition.Root].Position;
                    hot.Velocity = Vector3.Zero;
                }
            }
            if (r.BlendLeft > 0) r.BlendLeft = MathF.Max(0, r.BlendLeft - dt);
            if (r.Mask == RagdollPart.None && r.BlendLeft <= 0) { Destroy(r); continue; }
            var placement = Placement(r, cold, hot);
            if (!r.Frozen || r.BlendLeft > 0 || r.Published is null || placement != r.PublishedIn) (r.Published, r.PublishedIn) = (Build(r, placement), placement);
        }
        order.RemoveAll(id => !ragdolls.ContainsKey(id));
        world.Profile.Part("rag back");
        physics.Sweep(tick);
        world.Profile.Part("rag sweep");
    }

    /// <summary>The character's drawn placement: position, facing (a carried body's on its carrier's shoulder: <see cref="CarryPose"/>), size; the renderer's world matrix.</summary>
    static Matrix4x4 Placement(Ragdoll r, CharacterCold cold, in CharacterHot hot)
    {
        if (cold.CarriedBy.IsNone) return CharacterRigs.Placement(r.Setup, hot.Position, hot.Yaw, r.Scale);
        var (at, rotation) = (cold.CarryAttach ?? CarryPose.Fallback).World(hot.Position, hot.Yaw);
        return CharacterRigs.Placement(r.Setup, at, rotation, r.Scale);
    }

    /// <summary>Where a carried body hangs: on its carrier's attach bone as the carrier's animation poses it now; null without the carrier's skeleton or that bone.</summary>
    CarryAttach? AttachOf(CharacterTable table, CharacterCold cold)
    {
        if (!table.TryResolveNext(cold.CarriedBy, out int slot) || table.Cold(slot) is not { Appearance: { } look } carrier || rigs.For(look) is not { } setup) return null;
        if (setup.Rig.Handle(CarrySystem.AttachBone) is not int bone) return null;
        var scratch = setup.Rig.Rent();
        try
        {
            PoseDerived(scratch, setup, carrier);
            return CarryPose.Attach(scratch.DerivedPosition[bone] - new Vector3(0, setup.GroundOffset, 0), scratch.DerivedRotation[bone], CarryPose.Height01(cold.Appearance));
        }
        finally { setup.Rig.Return(scratch); }
    }

    /// <summary>Poses <paramref name="scratch"/> as the character's animation and its fixed posture layers do now (model space).</summary>
    void PoseDerived(PoseScratch scratch, CharacterPoseSetup setup, CharacterCold cold)
    {
        var layers = new List<ActiveLayer>();
        if (cold.Animation is { } animation && animations is not null)
            foreach (var layer in animations.Publish(animation))
                if (setup.Rig.Layer(rigs.Database, layer.Name, setup.Animal) is { } def) layers.Add(new ActiveLayer(def, MathF.Max(layer.Time, 0), layer.Weight));
        foreach (var (def, time) in setup.Fixed) layers.Add(new ActiveLayer(def, time, 1));
        scratch.PoseDerived(setup.Shape, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(layers));
    }

    /// <summary>Whether a saved ragdoll belongs to this one: a pose for each body, its root where the character stands (a platoon that travelled since moves its characters).</summary>
    static bool Fits(Ragdoll r, SavedRagdoll saved, Vector3 at)
    {
        if (saved.Pose.Length != r.Definition.Bodies.Length) return false;
        var root = saved.Pose[r.Definition.Root].Position;
        return Vector2.DistanceSquared(new Vector2(root.X, root.Z), new Vector2(at.X, at.Z)) <= 20 * 20 && MathF.Abs(root.Y - at.Y) <= 50;
    }

    /// <summary>A whole-body ragdoll's state for a save (<see cref="RagdollSave"/>); null when the character has none or only a carried or limb ragdoll.</summary>
    public SavedRagdoll? SaveState(CharacterId id) =>
        ragdolls.TryGetValue(id, out var r) && (r.Mask & RagdollPart.Whole) != 0 && (r.Mask & RagdollPart.CarryMode) == 0 && r.Pose.Length > 0
            ? new SavedRagdoll(r.Frozen, r.Age, (RigidTransform[])r.Pose.Clone())
            : null;

    /// <summary>Every body's pose where the character's animation puts its bone now, in world space.</summary>
    RigidTransform[] Animated(Ragdoll r, CharacterCold cold, Matrix4x4 placement)
    {
        var rig = r.Setup.Rig;
        var scratch = rig.Rent();
        try
        {
            PoseDerived(scratch, r.Setup, cold);
            var turn = Quaternion.CreateFromRotationMatrix(placement with { M41 = 0, M42 = 0, M43 = 0 });
            turn = Quaternion.Normalize(turn);
            var bodies = r.Definition.Bodies;
            var poses = new RigidTransform[bodies.Length];
            for (int b = 0; b < bodies.Length; b++)
            {
                int h = bodies[b].Bone;
                var bone = new RigidTransform(Vector3.Transform(scratch.DerivedPosition[h], placement), Quaternion.Normalize(turn * scratch.DerivedRotation[h]));
                poses[b] = bodies[b].BoneInBody.Inverse().Then(bone);
            }
            return poses;
        }
        finally { rig.Return(scratch); }
    }

    /// <summary>Which bodies a mask simulates (docs/game/ragdolls.md, "Bodies: kinematic or simulated").</summary>
    static bool Simulates(Ragdoll r, RagdollPart mask, RagdollBody body)
    {
        if ((mask & RagdollPart.Whole) != 0) return true;
        string n = body.Name;
        if ((mask & RagdollPart.CarryMode) != 0)
        {
            if (r.Setup.Animal) return n != r.CarryBone;
            return n is "Bip01 L Forearm" or "Bip01 R Forearm" or "Bip01 Head";
        }
        return (mask & RagdollPart.LeftArm) != 0 && n is "Bip01 L UpperArm" or "Bip01 L Forearm"
            || (mask & RagdollPart.RightArm) != 0 && n is "Bip01 R UpperArm" or "Bip01 R Forearm"
            || (mask & RagdollPart.LeftLeg) != 0 && n is "Bip01 L Thigh" or "Bip01 L Calf"
            || (mask & RagdollPart.RightLeg) != 0 && n is "Bip01 R Thigh" or "Bip01 R Calf"
            || (mask & RagdollPart.Head) != 0 && n is "Bip01 Head" or "Bip01 Neck";
    }

    /// <summary>
    /// Replaces the bodies for <paramref name="mask"/>: simulated bodies, and pinned (kinematic) ones joined to them. A body that was in the scene keeps its pose and velocity, a
    /// new one starts at the animated pose at rest (the game starts from the animated pose with no velocity and no hit impulse). Meitou: a new body of a whole (not carried)
    /// ragdoll starts at <paramref name="start"/>'s velocity (world units/s, at most half <see cref="RagdollPhysics.MaxSpeed"/>), its arms and legs spun by the push (<see cref="Spin"/>);
    /// with <paramref name="soft"/> its joints get soft limits (<see cref="AddJoint"/>).
    /// </summary>
    void Rebuild(Ragdoll r, RagdollPart mask, RigidTransform[] animated, (Vector3 Velocity, Vector3 Push) start = default, bool soft = false, ulong seed = 0)
    {
        var launch = start.Velocity;
        if ((mask & RagdollPart.Whole) == 0 || (mask & RagdollPart.CarryMode) != 0) (launch, start.Push) = (Vector3.Zero, Vector3.Zero);
        else if (launch.Length() > RagdollPhysics.MaxSpeed / 2) launch = Vector3.Normalize(launch) * (RagdollPhysics.MaxSpeed / 2);
        var bodies = r.Definition.Bodies;
        var velocities = new BodyVelocity[bodies.Length];
        var had = new bool[bodies.Length];
        for (int b = 0; b < r.Bodies.Length; b++)
            if (r.Bodies[b] is { } handle)
            {
                var body = physics.Simulation.Bodies.GetBodyReference(handle);
                r.Pose[b] = new RigidTransform(body.Pose.Position, body.Pose.Orientation);
                velocities[b] = body.Velocity;
                had[b] = r.Dynamic[b];
            }
        Clear(r);
        r.Mask = mask;
        r.Frozen = false;
        r.Soft = soft;
        if (mask == RagdollPart.None) return;
        if ((mask & RagdollPart.Whole) != 0 && !r.InScene) r.Age = 0;
        if ((mask & RagdollPart.Whole) == 0) r.Age = 0;

        var dynamic = new bool[bodies.Length];
        for (int b = 0; b < bodies.Length; b++) dynamic[b] = Simulates(r, mask, bodies[b]);
        var needed = (bool[])dynamic.Clone();
        foreach (var j in r.Definition.Joints)
            if (dynamic[j.A] || dynamic[j.B]) needed[j.A] = needed[j.B] = true;
        var kinds = Shapes(r.Definition);
        // A whole body that starts in the ground (a foot on a slope: the animated pose is not clipped to the terrain) is lifted out of it as one, as the game snaps a new
        // ragdoll up to the terrain: the ground is a one-sided mesh, and a body that starts behind it has nothing to land on.
        float lift = (mask & RagdollPart.Whole) != 0 && !had.Contains(true) ? OutOfGround(environment, animated, dynamic) : 0;
        r.Tag = nextTag++;
        r.Bodies = new BodyHandle?[bodies.Length];
        r.Dynamic = dynamic;
        for (int b = 0; b < bodies.Length; b++)
        {
            // A simulated body carries on from where it was simulated (a body dropped from the shoulder falls from there); anything else starts from the animation,
            // and so does everything when the body is lifted onto a shoulder.
            bool keep = had[b] && dynamic[b] && (mask & RagdollPart.CarryMode) == 0;
            var pose = keep ? r.Pose[b] : lift > 0 ? animated[b] with { Position = animated[b].Position + new Vector3(0, lift, 0) } : animated[b];
            var velocity = keep ? velocities[b] : new BodyVelocity(launch, Spin(r, bodies[b].Name, b, start.Push, seed));
            if (!needed[b]) { r.Pose[b] = pose; continue; }
            r.Pose[b] = pose;
            var rigid = new RigidPose(pose.Position, pose.Rotation);
            var collidable = RagdollPhysics.Collidable(kinds[b].Shape);
            var activity = new BodyActivityDescription(0.01f);
            var description = dynamic[b]
                ? BodyDescription.CreateDynamic(rigid, velocity, kinds[b].Inertia, collidable, activity)
                : BodyDescription.CreateKinematic(rigid, collidable, activity);
            r.Bodies[b] = physics.AddBody(description, new BodyTag { Ragdoll = r.Tag, Index = b, Joined = r.Definition.Joined[b] });
        }
        foreach (var j in r.Definition.Joints)
            if (dynamic[j.A] || dynamic[j.B]) AddJoint(r, j);
    }

    /// <summary>How far a new whole-body ragdoll must go up to have no body centre under the terrain (the lowest ends 0.25 above it); 0 when none is under it, or when it is more than <see cref="MaxLift"/> (a floor sunk into a hill, not a foot in a slope).</summary>
    internal static float OutOfGround(IRagdollEnvironment? environment, RigidTransform[] animated, bool[] dynamic)
    {
        if (environment is not { } env) return 0;
        float lift = 0;
        for (int b = 0; b < animated.Length; b++)
        {
            if (!dynamic[b]) continue;
            var p = animated[b].Position;
            float ground = env.GroundHeight(p.X, p.Z, p.Y);
            if (p.Y < ground) lift = MathF.Max(lift, ground + 0.25f - p.Y);
        }
        return lift <= MaxLift ? lift : 0;
    }

    /// <summary>
    /// A revolute joint's spring (docs/game/ragdolls.md "Joint springs"): a torque of stiffness x (angle - target) - damper x spin about the hinge, as the file's spring 1 sets it
    /// (the head's neck joint, 10000 with no damper). As a servo on the hinge angle with the frequency and damping ratio of that spring on the joint's moving mass: the
    /// inertia about the axis of the lighter body (a pinned body does not move; the other one does), omega = sqrt(k / I), ratio = damper / (2 sqrt(k I)).
    /// </summary>
    TwistServo HingeSpring(Ragdoll r, RagdollJoint j)
    {
        var shapes = Shapes(r.Definition);
        float Inertia(int body, Vector3 axis)
        {
            var inverse = shapes[body].Inertia.InverseInertiaTensor;
            float along = inverse.XX * axis.X * axis.X + inverse.YY * axis.Y * axis.Y + inverse.ZZ * axis.Z * axis.Z + 2 * (inverse.YX * axis.X * axis.Y + inverse.ZX * axis.X * axis.Z + inverse.ZY * axis.Y * axis.Z);
            return along > 1e-9f ? 1 / along : 1;
        }
        float inertia = r.Dynamic[j.A] && r.Dynamic[j.B] ? 1 / (1 / Inertia(j.A, j.AxisA) + 1 / Inertia(j.B, j.AxisB)) : r.Dynamic[j.A] ? Inertia(j.A, j.AxisA) : Inertia(j.B, j.AxisB);
        float omega = MathF.Sqrt(j.SpringStiffness / inertia);
        float ratio = j.SpringDamper / (2 * MathF.Sqrt(j.SpringStiffness * inertia));
        return new TwistServo
        {
            LocalBasisA = j.BasisA, LocalBasisB = j.BasisB, TargetAngle = j.SpringTarget,
            SpringSettings = new SpringSettings(omega / (2 * MathF.PI), ratio),
            ServoSettings = new ServoSettings(float.MaxValue, 0, float.MaxValue),
        };
    }

    void AddJoint(Ragdoll r, RagdollJoint j)
    {
        var a = r.Bodies[j.A]!.Value;
        var b = r.Bodies[j.B]!.Value;
        var solver = physics.Simulation.Solver;
        var spring = new SpringSettings(30, 1);
        switch (j.Type)
        {
            case PhysicsJointType.Revolute:
                r.Joints.Add(solver.Add(a, b, new Hinge { LocalOffsetA = j.OffsetA, LocalHingeAxisA = j.AxisA, LocalOffsetB = j.OffsetB, LocalHingeAxisB = j.AxisB, SpringSettings = spring }));
                if (j.Limits)
                    r.Joints.Add(solver.Add(a, b, new TwistLimit { LocalBasisA = j.BasisA, LocalBasisB = j.BasisB, MinimumAngle = j.Low, MaximumAngle = j.High, SpringSettings = spring }));
                if (j.SpringStiffness > 0) r.Joints.Add(solver.Add(a, b, HingeSpring(r, j)));
                if (j.Limits && r.Soft)
                {
                    float band = SoftBand * (j.High - j.Low);
                    r.Joints.Add(solver.Add(a, b, new TwistLimit { LocalBasisA = j.BasisA, LocalBasisB = j.BasisB, MinimumAngle = j.Low + band, MaximumAngle = j.High - band, SpringSettings = SoftLimit }));
                }
                break;
            case PhysicsJointType.Spherical:
                r.Joints.Add(solver.Add(a, b, new BallSocket { LocalOffsetA = j.OffsetA, LocalOffsetB = j.OffsetB, SpringSettings = spring }));
                if (j.Limits)
                {
                    r.Joints.Add(solver.Add(a, b, new SwingLimit { AxisLocalA = j.AxisA, AxisLocalB = j.AxisB, MaximumSwingAngle = j.Swing, SpringSettings = spring }));
                    r.Joints.Add(solver.Add(a, b, new TwistLimit { LocalBasisA = j.BasisA, LocalBasisB = j.BasisB, MinimumAngle = j.Low, MaximumAngle = j.High, SpringSettings = spring }));
                    if (r.Soft)
                    {
                        float band = SoftBand * (j.High - j.Low);
                        r.Joints.Add(solver.Add(a, b, new SwingLimit { AxisLocalA = j.AxisA, AxisLocalB = j.AxisB, MaximumSwingAngle = j.Swing * (1 - SoftBand), SpringSettings = SoftLimit }));
                        r.Joints.Add(solver.Add(a, b, new TwistLimit { LocalBasisA = j.BasisA, LocalBasisB = j.BasisB, MinimumAngle = j.Low + band, MaximumAngle = j.High - band, SpringSettings = SoftLimit }));
                    }
                }
                break;
            default:
                var (ra, rb) = (r.Definition.Bodies[j.A].Bind, r.Definition.Bodies[j.B].Bind);
                var relative = rb.Then(ra.Inverse());
                r.Joints.Add(solver.Add(a, b, new Weld { LocalOffset = relative.Position, LocalOrientation = relative.Rotation, SpringSettings = spring }));
                break;
        }
    }

    (TypedIndex Shape, BodyInertia Inertia)[] Shapes(RagdollDefinition definition)
    {
        if (shapes.TryGetValue(definition, out var made)) return made;
        var all = physics.Simulation.Shapes;
        made = new (TypedIndex, BodyInertia)[definition.Bodies.Length];
        for (int b = 0; b < made.Length; b++)
        {
            var body = definition.Bodies[b];
            switch (body.Type)
            {
                case PhysicsShapeType.Sphere:
                    var sphere = new Sphere(body.Size.X);
                    made[b] = (all.Add(sphere), sphere.ComputeInertia(body.Mass));
                    break;
                case PhysicsShapeType.Capsule:
                    var capsule = new Capsule(body.Size.X, body.Size.Y);
                    made[b] = (all.Add(capsule), capsule.ComputeInertia(body.Mass));
                    break;
                case PhysicsShapeType.Mesh:
                    var hull = new BepuPhysics.Collidables.ConvexHull(body.Hull, physics.Pool, out _);
                    made[b] = (all.Add(hull), hull.ComputeInertia(body.Mass));
                    break;
                default:
                    var box = new Box(body.Size.X, body.Size.Y, body.Size.Z);
                    made[b] = (all.Add(box), box.ComputeInertia(body.Mass));
                    break;
            }
        }
        return shapes[definition] = made;
    }

    // ---- advanced ragdolls (Meitou, BodyOptions.AdvancedRagdolls; docs/game/ragdolls.md "Advanced ragdolls (Meitou)") ----

    /// <summary>The relaxed pull a ragdoll's joints get: none (awake, or Faithful), the weak one of a dead body, the resting muscle tone of an unconscious one.</summary>
    internal enum Tone { None, Dead, Unconscious }

    /// <summary>
    /// Soft limits: each limit gets an inner one this share of its range in from either end, a soft overdamped spring that brakes the joint as it nears the hard stop
    /// (engine choice, tuned by eye).
    /// </summary>
    const float SoftBand = 0.25f;
    static readonly SpringSettings SoftLimit = new(5, 3);
    /// <summary>The relaxed angle of a hinge: this share of the way from the binding pose (0) to the middle of its range (a knee bent about 50 degrees, an elbow about 45).</summary>
    const float RelaxedShare = 0.6f;
    /// <summary>The spin a blow's push gives an arm or a leg: rad/s per unit/s of push, at most this, scaled by a roll of 0.6 to 1.4.</summary>
    const float SpinPerPush = 0.15f, MaxPushSpin = 6;

    /// <summary>The tone of a whole or carried ragdoll; none for a conscious character's collapsed limbs (a broken limb hangs loose) or without advanced ragdolls.</summary>
    static Tone ToneOf(CharacterCold cold, RagdollPart mask, bool advanced) =>
        !advanced || (mask & (RagdollPart.Whole | RagdollPart.CarryMode)) == 0 || cold.Medical is not { Incapacitated: true } medical ? Tone.None
        : medical.Dead ? Tone.Dead : Tone.Unconscious;

    /// <summary>
    /// Sets the relaxed pull: a weak, capped servo on each joint's twist (the hinge angle) towards its relaxed angle (<see cref="RelaxedShare"/> of a hinge's mid-range; a
    /// shoulder's twist to its binding pose, its swing left to gravity), so a limp body drifts into a natural rest instead of floating to its limits. Gravity beats it.
    /// Not on a joint with the file's own spring (the head's).
    /// </summary>
    void SetTone(Ragdoll r, Tone tone)
    {
        foreach (var held in r.Relax) physics.Simulation.Solver.Remove(held);
        r.Relax.Clear();
        r.Tone = tone;
        if (tone == Tone.None) return;
        var spring = tone == Tone.Dead ? new SpringSettings(0.8f, 1) : new SpringSettings(1.5f, 1);
        var servo = new ServoSettings(float.MaxValue, 0, tone == Tone.Dead ? 12 : 40);
        foreach (var j in r.Definition.Joints)
        {
            if (j.Type is not (PhysicsJointType.Revolute or PhysicsJointType.Spherical) || j.SpringStiffness > 0) continue;
            if (r.Bodies[j.A] is not { } a || r.Bodies[j.B] is not { } b || !r.Dynamic[j.A] && !r.Dynamic[j.B]) continue;
            float target = j.Type == PhysicsJointType.Revolute && j.Limits ? Math.Clamp(RelaxedShare * (j.Low + j.High) / 2, j.Low, j.High) : 0;
            r.Relax.Add(physics.Simulation.Solver.Add(a, b, new TwistServo { LocalBasisA = j.BasisA, LocalBasisB = j.BasisB, TargetAngle = target, SpringSettings = spring, ServoSettings = servo }));
        }
    }

    /// <summary>
    /// The spin a blow's <paramref name="push"/> gives body <paramref name="b"/> (named <paramref name="name"/>) as it starts: the upper arms swing the way the push goes, the thighs
    /// against it (the feet held the ground), each by its own roll, so a body thrown back trails its arms and kicks its legs forward. Zero for other bodies or no push.
    /// </summary>
    static Vector3 Spin(Ragdoll r, string name, int b, Vector3 push, ulong seed)
    {
        var flat = new Vector3(push.X, 0, push.Z);
        float side = name is "Bip01 L UpperArm" or "Bip01 R UpperArm" ? 1 : name is "Bip01 L Thigh" or "Bip01 R Thigh" ? -1 : 0;
        if (side == 0 || flat.LengthSquared() < 1e-6f) return Vector3.Zero;
        float roll = 0.6f + 0.8f * Rng.Float(Rng.Hash(seed, Rng.Key(r.Id), RngPurpose.RagdollSpin, (ulong)b));
        float speed = MathF.Min(flat.Length() * SpinPerPush, MaxPushSpin) * roll;
        // A limb hanging down from its joint swings its far end along d when it turns about d x up.
        return Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitY)) * (side * speed);
    }

    /// <summary>Sets the pinned bodies to the animated pose with no velocity, as the original does: the joints and contacts see the jump, not a speed.</summary>
    void Pin(Ragdoll r, RigidTransform[] animated, float dt)
    {
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is not { } handle || r.Dynamic[b]) continue;
            var body = physics.Simulation.Bodies.GetBodyReference(handle);
            var target = animated[b];
            // The original sets the pose and zeroes both velocities (FUN_1407d1550 -> FUN_1401a9ea0 / FUN_1401a9f00): the joints see only the position jump.
            body.Pose = new RigidPose(target.Position, target.Rotation);
            body.Velocity = default;
            if (!body.Awake) body.Awake = true;
        }
        // Partial ragdolls are woken every frame (FUN_1407d3c00, wake 20).
        for (int b = 0; b < r.Bodies.Length; b++)
            if (r.Bodies[b] is { } handle && r.Dynamic[b] && physics.Simulation.Bodies.GetBodyReference(handle) is { Awake: false } sleeping)
                sleeping.Awake = true;
    }

    /// <summary>
    /// A body that went through the ground between two ticks is put back on it. The ground is a one-sided mesh, so a body that tunnels (<see cref="RagdollPhysics.MaxSpeed"/> makes it
    /// rare, not impossible: a thin limb spun hard onto a slope) has nothing left to stop it and would fall on until the freeze. Only a crossing counts, from at or above the
    /// surface to more than <see cref="SinkDepth"/> under it: a body that starts below the terrain (a floor sunk into a hill) is left where it is.
    /// </summary>
    void Surface(Ragdoll r, IRagdollEnvironment env)
    {
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is not { } handle || !r.Dynamic[b]) continue;
            var body = physics.Simulation.Bodies.GetBodyReference(handle);
            var now = body.Pose.Position;
            float ground = env.GroundHeight(now.X, now.Z, now.Y);
            if (now.Y >= ground - SinkDepth) continue;
            var before = r.Pose[b].Position;
            if (before.Y < env.GroundHeight(before.X, before.Z, before.Y) - SinkDepth) continue;
            body.Pose.Position = new Vector3(now.X, ground + SinkDepth, now.Z);
            body.Velocity.Linear = new Vector3(body.Velocity.Linear.X, MathF.Max(body.Velocity.Linear.Y, 0), body.Velocity.Linear.Z);
            if (!body.Awake) body.Awake = true;
        }
    }

    void ReadBack(Ragdoll r)
    {
        for (int b = 0; b < r.Bodies.Length; b++)
            if (r.Bodies[b] is { } handle)
            {
                var pose = physics.Simulation.Bodies.GetBodyReference(handle).Pose;
                r.Pose[b] = new RigidTransform(pose.Position, pose.Orientation);
            }
    }

    float StillShare(Ragdoll r)
    {
        int still = 0, count = 0;
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is not { } handle) continue;
            count++;
            var v = physics.Simulation.Bodies.GetBodyReference(handle).Velocity;
            if (v.Linear.LengthSquared() < StillLinear && v.Angular.LengthSquared() < StillAngular) still++;
        }
        return count == 0 ? 1 : still / (float)count;
    }

    /// <summary>A ragdoll of a race that <c>swims</c> (race byte <c>+0x79</c>) whose root is at or under the water level (docs/game/ragdolls.md "Bodies: kinematic or simulated").</summary>
    static bool Afloat(World world, Ragdoll r) =>
        r.Pose.Length > 0 && r.Pose[r.Definition.Root].Position.Y <= Meitou.Data.World.WorldWater.Height
        && world.Characters.TryResolveNext(r.Id, out int slot) && world.Characters.Cold(slot)?.Race is { Swims: true };

    /// <summary>
    /// Buoyancy (<b>Observed</b>, the ragdoll update): while <see cref="Afloat"/>, each simulated body under the water level gets an upward acceleration of 98.1 + ln(level - y + 1),
    /// which beats gravity, so the body floats up to the surface.
    /// </summary>
    void Buoy(World world, Ragdoll r, float dt)
    {
        if (!Afloat(world, r)) return;
        float level = Meitou.Data.World.WorldWater.Height;
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is not { } handle || !r.Dynamic[b]) continue;
            var body = physics.Simulation.Bodies[handle];
            float y = body.Pose.Position.Y;
            if (y > level) continue;
            body.Velocity.Linear.Y += dt * (RagdollPhysics.Gravity + MathF.Log(level - y + 1));
            body.Awake = true;
        }
    }

    /// <summary>Freezes a settled ragdoll: the joints go and the bodies stay as still kinematic ones, which others still land on.</summary>
    void Freeze(Ragdoll r)
    {
        ReadBack(r);
        foreach (var joint in r.Joints) physics.Simulation.Solver.Remove(joint);
        r.Joints.Clear();
        SetTone(r, Tone.None);
        for (int b = 0; b < r.Bodies.Length; b++)
            if (r.Bodies[b] is { } handle)
            {
                var body = physics.Simulation.Bodies.GetBodyReference(handle);
                body.Velocity = default;
                if (r.Dynamic[b]) body.BecomeKinematic();
            }
        r.Frozen = true;
    }

    void Shift(Ragdoll r, Vector3 by)
    {
        for (int b = 0; b < r.Bodies.Length; b++)
            if (r.Bodies[b] is { } handle)
            {
                var body = physics.Simulation.Bodies.GetBodyReference(handle);
                body.Pose.Position += by;
                body.Velocity = default;
            }
        ReadBack(r);
    }

    /// <summary>Starts getting up: the character turns to face from the root to the head along the ground and stands where the root lies; the bones blend back.</summary>
    void StartGetUp(World world, Ragdoll r, ref CharacterHot hot)
    {
        var rig = r.Setup.Rig;
        var root = r.Pose[r.Definition.Root].Position;
        if (rig.Handle("Bip01 Head") is int head && r.Definition.BodyOfBone[head] is int hb and >= 0)
        {
            var toward = r.Pose[hb].Position - root;
            if (toward.X * toward.X + toward.Z * toward.Z > 1e-4f) hot.Yaw = MathF.Atan2(toward.X, toward.Z);
        }
        var walk = world.Walkability;
        if (walk.IsWalkable(root.X, root.Z, root.Y)) hot.Position = new Vector3(root.X, walk.GroundHeight(root.X, root.Z, root.Y), root.Z);
        r.BlendLeft = GetUpBlend;
    }

    /// <summary>The bones the ragdoll sets, in the model space of <paramref name="placement"/>: the simulated bodies' bones (all of them while getting up, blended).</summary>
    static BonePose? Build(Ragdoll r, Matrix4x4 placement)
    {
        bool blending = r.Mask == RagdollPart.None;
        if (!blending && !r.Dynamic.Any(d => d)) return null;
        if (!Matrix4x4.Invert(placement, out var toModel)) return null;
        var turn = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(placement with { M41 = 0, M42 = 0, M43 = 0 }));
        var unturn = Quaternion.Inverse(turn);
        var pose = new BonePose(r.Setup.Rig.BoneCount) { Weight = blending ? r.BlendLeft / GetUpBlend : 1 };
        var bodies = r.Definition.Bodies;
        for (int b = 0; b < bodies.Length; b++)
        {
            if (!blending && (b >= r.Dynamic.Length || !r.Dynamic[b] || r.Bodies[b] is null)) continue;
            var bone = bodies[b].BoneInBody.Then(r.Pose[b]);
            int h = bodies[b].Bone;
            pose.Set[h] = true;
            pose.Rotation[h] = Quaternion.Normalize(unturn * bone.Rotation);
            pose.Position[h] = Vector3.Transform(bone.Position, toModel);
        }
        return pose;
    }

    void Clear(Ragdoll r)
    {
        foreach (var joint in r.Joints) physics.Simulation.Solver.Remove(joint);
        r.Joints.Clear();
        SetTone(r, Tone.None);
        for (int b = 0; b < r.Bodies.Length; b++)
            if (r.Bodies[b] is { } handle) physics.Simulation.Bodies.Remove(handle);
        r.Bodies = [];
        r.Dynamic = [];
    }

    void Destroy(Ragdoll r)
    {
        Clear(r);
        ragdolls.Remove(r.Id);
    }

    public void Hash(ref StateHasher hasher)
    {
        if (ragdolls.Count == 0) return;
        hasher.Add(0x52616764);
        foreach (var id in order)
        {
            var r = ragdolls[id];
            hasher.Add(id);
            hasher.Add((int)r.Mask);
            hasher.Add(r.Frozen);
            hasher.Add(r.Age);
            hasher.Add(r.BlendLeft);
            foreach (var p in r.Pose)
            {
                hasher.Add(p.Position);
                hasher.Add(p.Rotation.X); hasher.Add(p.Rotation.Y); hasher.Add(p.Rotation.Z); hasher.Add(p.Rotation.W);
            }
        }
    }

    BepuUtilities.ThreadDispatcher? threads;

    public void Dispose()
    {
        physics.Dispose();
        threads?.Dispose();
    }
}
