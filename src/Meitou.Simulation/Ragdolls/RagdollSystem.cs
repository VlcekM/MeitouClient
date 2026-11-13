using System.Collections.Concurrent;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using Meitou.Data.Characters;
using Meitou.Data.Physics;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Saving;
using Meitou.Simulation.Weather;

namespace Meitou.Simulation.Ragdolls;

/// <summary>
/// The parts of a body that are a ragdoll (the game's <c>RagdollPart</c>, <b>Verified</b> in <c>fcs_enums.def</c>; LOCATIONAL_DAMAGE <c>collapse
/// part</c> uses it).
/// </summary>
[Flags]
public enum RagdollPart
{
    /// <summary>
    /// No part.
    /// </summary>
    None = 0,
    /// <summary>
    /// The whole body.
    /// </summary>
    Whole = 1,
    /// <summary>
    /// The right arm.
    /// </summary>
    RightArm = 2,
    /// <summary>
    /// The left arm.
    /// </summary>
    LeftArm = 4,
    /// <summary>
    /// The head.
    /// </summary>
    Head = 8,
    /// <summary>
    /// The right leg.
    /// </summary>
    RightLeg = 16,
    /// <summary>
    /// The left leg.
    /// </summary>
    LeftLeg = 32,
    /// <summary>
    /// Carried.
    /// </summary>
    CarryMode = 2048,
    /// <summary>
    /// Any limb (arms, head and legs).
    /// </summary>
    Limbs = RightArm | LeftArm | Head | RightLeg | LeftLeg,
}

/// <summary>
/// Meitou: the hit that put a character down, as its new ragdoll starts: the speed (world units/s) of its own motion then plus a push along the blow,
/// that push alone, and the struck part's bone (null: none), whose body takes more of the push (<see cref="RagdollSystem"/>, docs/game/ragdolls.md
/// "Advanced ragdolls").
/// </summary>
public readonly record struct RagdollBlow(Vector3 Velocity, Vector3 Push, string? Bone);

/// <summary>
/// What the animation system asks about ragdolls (in its parallel phase: answers are the last tick's state or fixed data).
/// </summary>
public interface IRagdollState
{
    /// <summary>
    /// Whether the character's look has a ragdoll (its race names a ragdoll file that binds to its skeleton).
    /// </summary>
    bool HasRagdoll(CharacterCold cold);
    /// <summary>
    /// Whether the whole body was a ragdoll at the end of the last tick.
    /// </summary>
    bool WasWhole(CharacterId id);
}

/// <summary>
/// What the world asks to publish a character's ragdoll.
/// </summary>
public interface IRagdollSource
{
    /// <summary>
    /// The bones the ragdoll sets, in the model space of the character's drawn placement; null without a ragdoll.
    /// </summary>
    BonePose? Publish(CharacterId id);
}

/// <summary>
/// Physics ragdolls (docs/game/ragdolls.md; engine choices in docs/simulation.md "Ragdolls"): the whole body of a knocked-out or dead character, the
/// forearms and head of a carried one (all but the carry bone of an animal), and a human's collapsed limbs. Bodies are built from the race's ragdoll
/// file on the character's animated pose; bodies the part mask does not simulate are pinned to the animation (kinematic). While the whole body is a
/// ragdoll the character's position follows the root body. Runs serially in the slow-world step after carrying and animation, and steps one Bepu
/// scene on the calling thread, so the state hash does not depend on the thread count.
/// </summary>
public sealed class RagdollSystem : ITickSystem, IStateHashed, IRagdollState, IRagdollSource, IDisposable
{
    #region State

    /// <summary>
    /// Getting up blends from the ragdoll's pose to the animation over this many seconds (<b>Observed</b>, <c>FUN_1407d3130</c>).
    /// </summary>
    public const float GetUpBlend = 0.75f;
    /// <summary>
    /// The edge of a square of static ground and buildings the scene is made of, in world units, on a grid from the origin (engine choice).
    /// </summary>
    public const float TileSize = 32;
    /// <summary>
    /// Ground tiles are kept this far round a moving ragdoll's root (<see cref="RagdollPhysics.Cover"/>).
    /// </summary>
    const float TileReach = 12;
    /// <summary>
    /// Ground tile units made in one tick (<see cref="RagdollPhysics.Cover"/>: a ground mesh 1, a building mesh
    /// <see cref="RagdollPhysics.BuildingUnits"/>), the starting ragdolls' first; a ragdoll whose tiles the budget does not reach starts on a later
    /// tick, the character animating meanwhile (engine choice: a zone's downed characters arriving together asked for 80-100 tiles in one tick, 45-60
    /// ms, and a town's building meshes came to its pending tiles in one tick when the zone was gathered; docs/simulation.md "Ragdolls").
    /// </summary>
    const int TileBudget = 8;
    /// <summary>
    /// Whole bodies started falling in one tick (a knockdown, a put-down, a stored squad's downed member coming back); the rest start on a later
    /// tick. A save's frozen ragdoll is not counted: it is the save's state and costs no settling. Engine choice: a build is 0.7 ms (bodies, joints,
    /// the ground probe), and a zone's arrivals asked for 20 in a tick.
    /// </summary>
    const int StartBudget = 4;
    /// <summary>
    /// Awake bodies from which the physics steps on the world's thread count. Below it the step runs on the calling thread: it costs the same
    /// (**Observed**, headless flight probe, 15-300 awake bodies: 0.4-0.8 ms either way; the dispatcher's signals and waits are 0.3 ms a step), and a
    /// dispatcher worker preempted by the render, streaming or GC threads stalled the step for a scheduler quantum or more (16-85 ms waits in the
    /// probe's trace), which a single thread cannot suffer from another thread.
    /// </summary>
    const int ThreadedBodies = 256;
    /// <summary>
    /// Freezing (docs/game/ragdolls.md "Sleep and limits"): not before this age, then when this share of the bodies is still; at the latest after
    /// <see cref="FreezeLatest"/>.
    /// </summary>
    public const float FreezeEarliest = 2, FreezeShare = 0.6f, FreezeLatest = 7;
    /// <summary>
    /// Meitou (<b>engine choice</b>): a collapsed limb of a character still up dangles only within this of the camera
    /// (<see cref="World.ViewPosition"/>; about where the human body's first reduced mesh level starts), and one already dangling until 10 % farther;
    /// beyond, the limb follows the animation and the physics need not step for it. The original dangles them at any distance (docs/game/ragdolls.md
    /// "Sleep and limits").
    /// </summary>
    public const float LimbReach = 300;
    /// <summary>
    /// A body is still under these squared speeds (the game's PhysX sleep velocities: linear² 2.25, angular² 1.96).
    /// </summary>
    const float StillLinear = 2.25f, StillAngular = 1.96f;
    /// <summary>
    /// How far under the terrain a body's centre must get, in world units, to count as through it (engine choice; a resting limb's centre is at least
    /// its radius, 0.4 and up, above it).
    /// </summary>
    const float SinkDepth = 1, MaxLift = 8;

    /// <summary>
    /// The skeleton rigs and ragdoll files.
    /// </summary>
    readonly CharacterRigs _rigs;
    /// <summary>
    /// Ground and buildings (null: the world's walkable ground, made on first use).
    /// </summary>
    IRagdollEnvironment? _environment;
    /// <summary>
    /// The body options (null: a new ragdoll starts at rest).
    /// </summary>
    readonly Bodies.BodyOptions? _options;
    /// <summary>
    /// The physics scene.
    /// </summary>
    readonly RagdollPhysics _physics = new();
    /// <summary>
    /// The ragdolls by character.
    /// </summary>
    readonly Dictionary<CharacterId, Ragdoll> _ragdolls = [];
    /// <summary>
    /// The ragdolls' characters in the order they were made (the hash's order).
    /// </summary>
    readonly List<CharacterId> _order = [];
    /// <summary>
    /// The ragdoll file and its path of each look (see <see cref="FileOf"/>).
    /// </summary>
    readonly ConcurrentDictionary<CharacterAppearance, Lazy<(PhysicsFile File, string Path)?>> _files = new(ReferenceEqualityComparer.Instance);
    /// <summary>
    /// The ragdoll files by path, read once each.
    /// </summary>
    readonly ConcurrentDictionary<string, Lazy<PhysicsFile?>> _physicsFiles = new();
    /// <summary>
    /// The definitions by file, rig and scale (in hundredths).
    /// </summary>
    readonly ConcurrentDictionary<(string Path, SkeletonRig Rig, int Scale), Lazy<RagdollDefinition?>> _definitions = new();
    /// <summary>
    /// The shapes and inertias of each definition's bodies.
    /// </summary>
    readonly Dictionary<RagdollDefinition, (TypedIndex Shape, BodyInertia Inertia)[]> _shapes = new(ReferenceEqualityComparer.Instance);
    /// <summary>
    /// The animation system, looked up on first use.
    /// </summary>
    IAnimationSource? _animations;
    /// <summary>
    /// The collision tag of the next ragdoll.
    /// </summary>
    int _nextTag = 1;
    /// <summary>
    /// Real seconds of physics owed since the last step (SlowWorld); not saved or hashed.
    /// </summary>
    float _physicsDue;
    /// <summary>
    /// The physics' thread dispatcher, made when a ragdoll first moves with more than one world thread.
    /// </summary>
    BepuUtilities.ThreadDispatcher? _threads;

    /// <summary>
    /// A ragdoll system on <paramref name="rigs"/>; the physics are warmed up once per process.
    /// </summary>
    /// <param name="environment">Ground and buildings; null: the world's walkable ground (<see cref="WalkabilityEnvironment"/>).</param>
    /// <param name="options">The body options whose <see cref="Bodies.BodyOptions.AdvancedRagdolls"/> gives a new ragdoll its start speed; null: at
    /// rest, as the original.</param>
    public RagdollSystem(CharacterRigs rigs, IRagdollEnvironment? environment = null, Bodies.BodyOptions? options = null)
    {
        this._rigs = rigs;
        this._environment = environment;
        this._options = options;
        RagdollPhysics.WarmUpOnce();
    }

    /// <summary>
    /// Ragdolls in play (frozen ones included) and how many are moving.
    /// </summary>
    public int Count => _ragdolls.Count;
    /// <summary>
    /// The ragdolls moving.
    /// </summary>
    public int Moving { get; private set; }
    /// <summary>
    /// The moving ones by kind: carried, whole-body (falling or settling) and limbs only (a collapsed arm or leg of a character still up).
    /// </summary>
    public int MovingCarried { get; private set; }
    /// <summary>
    /// The whole-body ragdolls moving.
    /// </summary>
    public int MovingWhole { get; private set; }
    /// <summary>
    /// The limbs-only ragdolls moving.
    /// </summary>
    public int MovingLimbs { get; private set; }
    /// <summary>
    /// The physics scene's static tiles.
    /// </summary>
    public int Tiles => _physics.TileCount;

    /// <summary>
    /// Bodies the physics steps (its awake set, ground tiles included).
    /// </summary>
    public int AwakeBodies => _physics.Simulation.Bodies.ActiveSet.Count;

    /// <summary>
    /// One character's ragdoll: its definition, parts, bodies and joints, pose, and the bones it last published.
    /// </summary>
    sealed class Ragdoll
    {
        /// <summary>
        /// The character.
        /// </summary>
        public required CharacterId Id;
        /// <summary>
        /// The ragdoll's definition.
        /// </summary>
        public required RagdollDefinition Definition;
        /// <summary>
        /// The character's pose setup.
        /// </summary>
        public required CharacterPoseSetup Setup;
        /// <summary>
        /// The scale of the definition.
        /// </summary>
        public required float Scale;
        /// <summary>
        /// An animal's carry bone handle (RACE <c>carry bone</c>, else the skeleton's root; <c>FUN_1407d3850</c>), -1 without one. A bone, not an
        /// actor name: the dog ragdoll's root actor is named <c>Actor</c> and moves <c>Bip01</c>.
        /// </summary>
        public int CarryBone = -1;
        /// <summary>
        /// While carried: the placement it hangs in (<see cref="Placement"/>), where it falls from when set down or dropped.
        /// </summary>
        public Matrix4x4? HungIn;
        /// <summary>
        /// The parts that are a ragdoll.
        /// </summary>
        public RagdollPart Mask;
        /// <summary>
        /// The collision tag of its bodies.
        /// </summary>
        public int Tag;
        /// <summary>
        /// By body: its handle when it is in the scene.
        /// </summary>
        public BodyHandle?[] Bodies = [];
        /// <summary>
        /// By body: whether it is simulated (else pinned to the animation).
        /// </summary>
        public bool[] Dynamic = [];
        /// <summary>
        /// The joints in the scene.
        /// </summary>
        public readonly List<ConstraintHandle> Joints = [];
        /// <summary>
        /// Advanced ragdolls: the joints have soft limits (set when the bodies are built), and the relaxed pull of <see cref="Tone"/> (its servos in
        /// <see cref="Relax"/>).
        /// </summary>
        public bool Soft;
        /// <summary>
        /// The relaxed pull the joints get (see <see cref="Soft"/>).
        /// </summary>
        public Tone Tone;
        /// <summary>
        /// The servos of <see cref="Tone"/>.
        /// </summary>
        public readonly List<ConstraintHandle> Relax = [];
        /// <summary>
        /// By body: the last world pose (also kept for bodies not in the scene while getting up).
        /// </summary>
        public RigidTransform[] Pose = [];
        /// <summary>
        /// Whether it is frozen.
        /// </summary>
        public bool Frozen;
        /// <summary>
        /// The seconds since the whole-body ragdoll started (see <see cref="FreezeEarliest"/>).
        /// </summary>
        public float Age;
        /// <summary>
        /// Seconds of the get-up blend left (0: none), and the bones it blends from, in world space.
        /// </summary>
        public float BlendLeft;
        /// <summary>
        /// The published bones and the placement they were made in: a frozen ragdoll still in it hands over the same object (the renderer poses it
        /// again only for new bones).
        /// </summary>
        public BonePose? Published;
        /// <summary>
        /// The placement <see cref="Published"/> was made in.
        /// </summary>
        public Matrix4x4 PublishedIn;
        /// <summary>
        /// Whether any body is in the scene.
        /// </summary>
        public bool InScene => Bodies.Any(b => b is not null);
    }

    #endregion

    #region What the animation system asks

    /// <summary>
    /// Whether a character's look has a ragdoll file and a rig.
    /// </summary>
    public bool HasRagdoll(CharacterCold cold) => cold.Appearance is { } look && FileOf(look) is not null && _rigs.For(look) is not null;

    /// <summary>
    /// Whether the character's ragdoll is a whole-body one.
    /// </summary>
    public bool WasWhole(CharacterId id) => _ragdolls.TryGetValue(id, out var r) && (r.Mask & RagdollPart.Whole) != 0;

    /// <summary>
    /// Debug (the console's <c>ragdoll-dump</c>): the character's bodies in world space and each joint's twist angle against its limits, one line
    /// each; null without a ragdoll.
    /// </summary>
    public string? Describe(CharacterId id)
    {
        if (!_ragdolls.TryGetValue(id, out var r) || r.Pose.Length != r.Definition.Bodies.Length) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"mask {r.Mask} tone {r.Tone} frozen {r.Frozen} age {r.Age:0.00}"));
        var d = r.Definition;
        for (int b = 0; b < d.Bodies.Length; b++) sb.AppendLine(DescribeBody(r, b));

        foreach (var j in d.Joints) sb.AppendLine(DescribeJoint(r, j));

        return sb.ToString();
    }

    /// <summary>
    /// A <see cref="Describe"/> line of body <paramref name="b"/>: whether it is dynamic, its position and its local Y axis in world space.
    /// </summary>
    static string DescribeBody(Ragdoll r, int b)
    {
        var p = r.Pose[b];
        var up = Vector3.Transform(Vector3.UnitY, p.Rotation);
        string name = r.Definition.Bodies[b].Name;
        bool dynamic = b < r.Dynamic.Length && r.Dynamic[b];
        string position = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"pos=({p.Position.X:0.00},{p.Position.Y:0.00},{p.Position.Z:0.00})");

        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"body {name} dyn={dynamic} {position} localY=({up.X:0.00},{up.Y:0.00},{up.Z:0.00})");
    }

    /// <summary>
    /// A <see cref="Describe"/> line of joint <paramref name="j"/>: its twist and swing (degrees) against its limits.
    /// </summary>
    static string DescribeJoint(Ragdoll r, RagdollJoint j)
    {
        float deg = 180 / MathF.PI;
        var d = r.Definition;
        var qa = r.Pose[j.A].Rotation * j.BasisA;
        var qb = r.Pose[j.B].Rotation * j.BasisB;
        var rel = Quaternion.Conjugate(qa) * qb;
        if (rel.W < 0) rel = new Quaternion(-rel.X, -rel.Y, -rel.Z, -rel.W);

        float twist = 2 * MathF.Atan2(rel.Z, rel.W) * deg;
        float swing = 2 * MathF.Asin(Math.Clamp(MathF.Sqrt(rel.X * rel.X + rel.Y * rel.Y), 0, 1)) * deg;
        string bodies = $"{d.Bodies[j.A].Name} -> {d.Bodies[j.B].Name}";

        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"joint {bodies} {j.Type} twist={twist:0.0} swing={swing:0.0} limits=[{j.Low * deg:0},{j.High * deg:0}] swingMax={j.Swing * deg:0}");
    }

    /// <summary>
    /// The bones the character's ragdoll last published; null without one.
    /// </summary>
    public BonePose? Publish(CharacterId id) => _ragdolls.TryGetValue(id, out var r) ? r.Published : null;

    /// <summary>
    /// The ragdoll file of a look and its path; null when the race has none or it does not read. A static factory given the system: nothing allocated
    /// on a hit. The file itself is read once per path (one a race and sex), not once per look: every character has a look of its own, and reading it
    /// again for each one put a file read in the tick of every character streamed in (docs/simulation.md "Ragdolls").
    /// </summary>
    (PhysicsFile File, string Path)? FileOf(CharacterAppearance look) => _files.GetOrAdd(look, static (a, self) => new(() =>
    {
        // RACE male ragdoll / female ragdoll (the female file only for a female look; docs/game/ragdolls.md "RagdollPart and requests").
        var name = a.Race.GetPath(a.Female ? "female ragdoll" : "male ragdoll") is { Length: > 0 } f ? f : a.Race.GetPath("male ragdoll");
        if (string.IsNullOrEmpty(name) || self._rigs.Find(name) is not { } path) return null;
        return self._physicsFiles.GetOrAdd(path, static p => new(() =>
        {
            try
            {
                return PhysicsFile.ReadFile(p);
            }
            catch (Exception e) when (e is FormatException or IOException)
            {
                return null;
            }
        })).Value is { } file ? (file, path) : null;
    }), this).Value;

    /// <summary>
    /// The definition of a look's ragdoll file on <paramref name="rig"/> at <paramref name="scale"/> (cached); null without a file.
    /// </summary>
    RagdollDefinition? DefinitionOf(CharacterAppearance look, SkeletonRig rig, float scale)
    {
        if (FileOf(look) is not { } file) return null;
        int key = (int)MathF.Round(scale * 100);
        return _definitions.GetOrAdd((file.Path, rig, key), _ => new(() => RagdollDefinition.Build(file.File, rig, key / 100f, Hull))).Value;
    }

    /// <summary>
    /// The bone a carried animal's ragdoll is pinned by: RACE <c>carry bone</c> when the skeleton has it, else the skeleton's root
    /// (<c>FUN_1407d3850</c>); -1 for neither.
    /// </summary>
    static int CarryBoneOf(CharacterAppearance look, SkeletonRig rig)
    {
        if (look.Race.GetString("carry bone") is { Length: > 0 } named && rig.Handle(named) is int bone) return bone;
        var root = rig.Skeleton.Bones.FirstOrDefault(b => b.Parent is null)?.Name;
        return root is not null && rig.Handle(root) is int handle ? handle : -1;
    }

    /// <summary>
    /// The convex hull points of a cooked mesh; null when it is missing or does not read.
    /// </summary>
    Vector3[]? Hull(string cooked)
    {
        if (_rigs.Find(cooked) is not { } path) return null;
        try
        {
            return CookedMeshReader.ReadConvex(File.ReadAllBytes(path)).Vertices;
        }
        catch (Exception e) when (e is FormatException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    #endregion

    #region The tick

    /// <summary>
    /// <c>Character::isRagdoll</c> (<c>FUN_1407d1440</c>): carried or whole-body ragdolled, the WHOLE or CARRY_MODE parts of <see cref="Wanted"/>;
    /// the simulation's rule, whether or not the host has the rig to show it (the original needs a visual or death to ragdoll).
    /// </summary>
    internal static bool IsRagdoll(CharacterCold cold) => (Wanted(cold) & (RagdollPart.Whole | RagdollPart.CarryMode)) != 0;

    /// <summary>
    /// The parts that should be a ragdoll now (docs/game/ragdolls.md "Triggers").
    /// </summary>
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
        {
            if (p.Template.Collapses && (p.IsLeg ? p.LegDown(medical.LegKoPoint) : p.IsDown) && !p.IsRobotic(race))
                parts |= (RagdollPart)p.Template.CollapsePart & RagdollPart.Limbs;
        }

        return parts;
    }

    /// <summary>
    /// The tick's ragdolls: the gone ones destroyed, then the masks, builds and pins, the physics step, and the poses back and published.
    /// </summary>
    public void SlowWorld(World world)
    {
        _animations ??= world.Systems.OfType<IAnimationSource>().FirstOrDefault();
        var table = world.Characters;
        var env = _environment ??= new WalkabilityEnvironment(world.Walkability);
        float dt = world.TickSeconds, stepDt = RagdollPhysics.StepOf(dt, world.PhysicsSpeed);   // the timers run in game time, the physics in real time
        long tick = world.Tick;

        // Ragdolls of characters that are gone.
        foreach (var id in _order)
            if (!table.TryResolveNext(id, out _)) Destroy(_ragdolls[id]);
        _order.RemoveAll(id => !_ragdolls.ContainsKey(id));

        // 1. Masks, building and pinning.
        var next = table.Next;
        int tilesLeft = TileBudget, startsLeft = StartBudget;
        for (int i = 0; i < next.Length; i++) SetUp(world, i, env, dt, tick, ref tilesLeft, ref startsLeft);

        world.Profile.Part("rag setup");   // F12: the masks, the builds, the pins and the ground tiles

        // 2. The step.
        Step(world, dt, stepDt);
        world.Profile.Part("rag step");

        // 3. Poses back, the whole body's position, freezing, publishing.
        PosesBack(world, env, dt);
        _order.RemoveAll(id => !_ragdolls.ContainsKey(id));
        world.Profile.Part("rag back");
        _physics.Sweep(tick);
        world.Profile.Part("rag sweep");
    }

    /// <summary>
    /// Step 1 for one character: its mask, the build of a new ragdoll (within the tick's budgets), and its pins.
    /// </summary>
    void SetUp(World world, int i, IRagdollEnvironment env, float dt, long tick, ref int tilesLeft, ref int startsLeft)
    {
        var table = world.Characters;
        var next = table.Next;
        if (!next[i].Alive || table.Cold(i) is not { } cold) return;

        // The far cadence (World.Steps): a far character skipped this tick is left as it is, unless its ragdoll is moving (its pins follow the
        // animation every physics step).
        if (!world.Steps(i) && !(_ragdolls.TryGetValue(table.IdOf(i), out var moving) && !moving.Frozen && moving.Mask != RagdollPart.None))
            return;

        cold.CarryAttach = cold.CarriedBy.IsNone ? null : AttachOf(table, cold);
        var id = table.IdOf(i);
        _ragdolls.TryGetValue(id, out var r);
        var wanted = WantedNear(world, i, cold, r);
        if (r is null && wanted == RagdollPart.None)
        {
            Slide(ref next[i], cold, env, dt);
            return;
        }

        float scale = ScaleOf(cold);
        if (!Budgeted(next[i].Position, cold, r, wanted, scale, env, tick, ref tilesLeft, ref startsLeft)) return;

        // Meitou (BodyOptions.AdvancedRagdolls): a new whole-body ragdoll goes on as the character moved, pushed by the blow that put it down
        // (CombatSystem.Knockdown), else as it moved last tick.
        bool advanced = _options is { AdvancedRagdolls: true };
        var blow = !advanced ? default : cold.Blow ?? new RagdollBlow(table.Previous[i].Velocity, Vector3.Zero, null);
        cold.Blow = null;
        var restore = TakeSaved(cold, r, wanted);
        r ??= NewRagdoll(id, cold, scale);
        if (r is null) return;

        var placement = Placement(r, cold, next[i]);
        var animated = wanted != r.Mask ? Remask(world, r, cold, ref next[i], wanted, placement, restore, blow, advanced) : null;
        if (r.Mask == RagdollPart.None) return;

        r.HungIn = (r.Mask & RagdollPart.CarryMode) != 0 ? placement : null;
        if (cold.Launch is { } launch)
        {
            cold.Launch = null;
            if ((r.Mask & RagdollPart.Whole) != 0 && (r.Mask & RagdollPart.CarryMode) == 0) Launch(r, launch, advanced, world.Seed);
        }

        if (!r.Frozen && ToneOf(cold, r.Mask, advanced) is var tone && tone != r.Tone) SetTone(r, tone);
        if (!r.Frozen && r.Dynamic.Any(d => !d)) Pin(r, animated ?? Animated(r, cold, placement), dt);
        if (!r.Frozen) _physics.Cover(r.Definition.RootAt(r.Pose), TileReach * r.Scale, env, tick, ref tilesLeft);
    }

    /// <summary>
    /// The parts a character wants ragdolled now: none past the limb reach from the eye for limbs only, none without a ragdoll file.
    /// </summary>
    RagdollPart WantedNear(World world, int i, CharacterCold cold, Ragdoll? r)
    {
        var next = world.Characters.Next;

        // The look's ragdoll file asked only of a character that wants one (two cache lookups per standing character every tick were most of this
        // loop in a big battle).
        var wanted = cold.Appearance is null ? RagdollPart.None : Wanted(cold);
        if (wanted != RagdollPart.None && (wanted & ~RagdollPart.Limbs) == 0 && world.ViewPosition is { } eye)
        {
            float reach = r is { Mask: not RagdollPart.None } ? LimbReach * 1.1f : LimbReach;
            if (Vector3.DistanceSquared(next[i].Position, eye) > reach * reach) wanted = RagdollPart.None;
        }

        if (wanted != RagdollPart.None && !HasRagdoll(cold)) wanted = RagdollPart.None;

        return wanted;
    }

    /// <summary>
    /// Whether a ragdoll may start or change now: a whole body about to fall takes one of the tick's builds and the ground under it; false holds it
    /// to a later tick.
    /// </summary>
    bool Budgeted(Vector3 at, CharacterCold cold, Ragdoll? r, RagdollPart wanted, float scale, IRagdollEnvironment env, long tick,
        ref int tilesLeft, ref int startsLeft)
    {
        // A whole body about to fall needs the ground under it and costs a build (TileBudget, StartBudget: past them it starts next tick, its blow
        // and saved pose kept); a frozen saved one needs none, and a get-up, a limb's dangle or a lift never waits.
        bool falls = (wanted & RagdollPart.Whole) != 0 && (wanted & RagdollPart.CarryMode) == 0;
        bool fell = r is not null && (r.Mask & RagdollPart.Whole) != 0 && (r.Mask & RagdollPart.CarryMode) == 0;
        bool starts = falls && !fell && cold.Save?.Ragdoll is not { Frozen: true };
        if (starts)
        {
            if (startsLeft <= 0) return false;
            if (!_physics.Covered(at, TileReach * scale))
            {
                if (tilesLeft <= 0) return false;
                _physics.Cover(at, TileReach * scale, env, tick, ref tilesLeft);
                if (!_physics.Covered(at, TileReach * scale)) return false;
            }

            startsLeft--;
        }

        return true;
    }

    /// <summary>
    /// The ragdoll a save holds for the character (Saving.RagdollSave), taken the first time it is seen; kept to restore only when it starts as a
    /// whole body.
    /// </summary>
    static SavedRagdoll? TakeSaved(CharacterCold cold, Ragdoll? r, RagdollPart wanted)
    {
        SavedRagdoll? restore = null;
        if (cold.Save is { Ragdoll: { } saved })
        {
            cold.Save.Ragdoll = null;
            if (r is null && (wanted & RagdollPart.Whole) != 0) restore = saved;
        }

        return restore;
    }

    /// <summary>
    /// A new ragdoll for the character, registered; null when its look has no ragdoll definition.
    /// </summary>
    Ragdoll? NewRagdoll(CharacterId id, CharacterCold cold, float scale)
    {
        var setup = _rigs.For(cold.Appearance!)!;
        if (DefinitionOf(cold.Appearance!, setup.Rig, scale) is not { } definition) return null;
        var r = new Ragdoll { Id = id, Definition = definition, Setup = setup, Scale = scale,
            CarryBone = CarryBoneOf(cold.Appearance!, setup.Rig) };
        r.Pose = new RigidTransform[definition.Bodies.Length];
        _ragdolls[id] = r;
        _order.Add(id);

        return r;
    }

    /// <summary>
    /// A mask change: the get-up, the let-go, the rebuild (from a saved pose when one fits); returns the animated pose it used.
    /// </summary>
    RigidTransform[] Remask(World world, Ragdoll r, CharacterCold cold, ref CharacterHot hot, RagdollPart wanted, Matrix4x4 placement,
        SavedRagdoll? restore, RagdollBlow blow, bool advanced)
    {
        // Getting up: the whole body leaves the ragdoll and blends back into the animation, facing from the root to the head (not when it is
        // laid in a bed or a cage, which places it).
        if ((r.Mask & RagdollPart.Whole) != 0 && (wanted & RagdollPart.Whole) == 0 && (wanted & RagdollPart.CarryMode) == 0
            && cold.InSomething == Locks.HeldState.None)
            StartGetUp(world, r, ref hot);
        // A body let go of falls from where it hung: the carried pose re-placed upright at its new position would hang its trunk
        // through the floor (#327).
        bool letGo = (r.Mask & RagdollPart.CarryMode) != 0 && (wanted & RagdollPart.CarryMode) == 0;
        var animated = Animated(r, cold, letGo && r.HungIn is { } hung ? hung : placement);
        bool restored = restore is not null && Fits(r, restore, hot.Position);
        Rebuild(r, wanted, restored ? restore!.Pose : animated, restored ? default : blow, advanced, world.Seed);
        if (restored)
        {
            r.Age = restore!.Age;
            if (restore.Frozen) Freeze(r);
            hot.Position = r.Definition.RootAt(r.Pose);   // a frozen one is never read back
        }

        return animated;
    }

    /// <summary>
    /// Step 2: counts the moving ragdolls and steps the physics when a step is due.
    /// </summary>
    void Step(World world, float dt, float stepDt)
    {
        (Moving, MovingCarried, MovingWhole, MovingLimbs) = (0, 0, 0, 0);
        foreach (var id in _order)
        {
            if (_ragdolls[id] is { Frozen: false, Mask: not RagdollPart.None and var mask })
            {
                Moving++;
                if ((mask & RagdollPart.CarryMode) != 0) MovingCarried++;
                else if ((mask & RagdollPart.Whole) != 0) MovingWhole++;
                else MovingLimbs++;
            }
        }

        // The physics on the world's thread count (its own workers: the world's pool is idle in this serial phase).
        if (Moving > 0 && _threads is null && world.Settings.Threads > 1) _threads = new BepuUtilities.ThreadDispatcher(world.Settings.Threads);

        // Meitou: the real time steps up until it makes a step of 1x (every tick at 1x, every 5th at 5x), so the physics costs per real second what
        // it does at 1x whatever the speed; a step costs about the same however short (Observed: 0.5 ms a tick for a few ragdolls at 1x and at 5x).
        _physicsDue = Moving > 0 ? _physicsDue + stepDt : 0;
        if (Moving > 0 && _physicsDue >= RagdollPhysics.StepOf(dt, 1) - 1e-5f)
        {
            foreach (var id in _order) if (_ragdolls[id] is { Frozen: false, Mask: not RagdollPart.None } floating) Buoy(world, floating, _physicsDue);
            _physics.Step(_physicsDue, _physics.Simulation.Bodies.ActiveSet.Count >= ThreadedBodies ? _threads : null);
            _physicsDue = 0;
        }
    }

    /// <summary>
    /// Step 3: the poses back, the whole body's position, freezing and publishing.
    /// </summary>
    void PosesBack(World world, IRagdollEnvironment env, float dt)
    {
        var table = world.Characters;
        foreach (var id in _order)
        {
            var r = _ragdolls[id];
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
                    var root = r.Definition.RootAt(r.Pose);
                    float ground = env.GroundHeight(root.X, root.Z, root.Y);

                    // A root far under the ground is lifted back onto it (FUN_1407d4470: more than 200 below), and frozen.
                    if (root.Y < ground - 200) { Shift(r, new Vector3(0, ground + 2 - root.Y, 0)); Freeze(r); }
                    // At once when the terrain tile under it is not loaded (FUN_1405b8180 -> FUN_1407d1f70): a zone outside the active set, or off
                    // the grid.
                    else if (world.ActiveZones is { } loaded && !loaded.Contains(Data.World.WorldLayout.ZoneOf(root.X, root.Z))) Freeze(r);
                    else if (r.Age >= FreezeLatest || r.Age >= FreezeEarliest && StillShare(r) >= FreezeShare) Freeze(r);
                    hot.Position = r.Definition.RootAt(r.Pose);
                    hot.Velocity = Vector3.Zero;
                    if (cold.CarriedBy.IsNone) StandOn(cold, ground);
                }
            }

            if (r.BlendLeft > 0) r.BlendLeft = MathF.Max(0, r.BlendLeft - dt);
            if (r.Mask == RagdollPart.None && r.BlendLeft <= 0) { Destroy(r); continue; }
            var placement = Placement(r, cold, hot);
            if (!r.Frozen || r.BlendLeft > 0 || r.Published is null || placement != r.PublishedIn)
                (r.Published, r.PublishedIn) = (Build(r, placement), placement);
        }
    }

    /// <summary>
    /// A ragdolled body's water state comes from a ground-height probe below it, not from its pose (<c>Character::threadedUpdatePeriodic</c>
    /// <c>FUN_1405d00b0</c>, <b>Verified</b>): the ray's hit height above 0 is stored as the stand height (<see cref="CharacterCold.StandHeight"/>, a
    /// miss keeps the last), which the water-state function <c>FUN_1405c7fd0</c> reads. The probe is the environment's ground height (terrain or the
    /// building floor near the body, as <see cref="IRagdollEnvironment"/> answers; the original's indoor/outdoor ray masks are folded into it). A
    /// carried body takes its carrier's state instead (<see cref="WeatherSystem"/>).
    /// </summary>
    static void StandOn(CharacterCold cold, float ground)
    {
        if (ground > 0) cold.StandHeight = ground;
        if (cold.Race is { } race && cold.StandHeight > 0) cold.WaterState = (byte)Bodies.Speed.WaterState(cold.StandHeight, 1, race.Swims);
    }

    /// <summary>
    /// The character's drawn placement: position, facing (a carried body's on its carrier's shoulder: <see cref="CarryPose"/>), size; the renderer's
    /// world matrix.
    /// </summary>
    static Matrix4x4 Placement(Ragdoll r, CharacterCold cold, in CharacterHot hot)
    {
        if (cold.CarriedBy.IsNone) return CharacterRigs.Placement(r.Setup, hot.Position, hot.Yaw, r.Scale);
        var (at, rotation) = (cold.CarryAttach ?? CarryPose.Fallback).World(hot.Position, hot.Yaw);
        return CharacterRigs.Placement(r.Setup, at, rotation, r.Scale);
    }

    /// <summary>
    /// Where a carried body hangs: on its carrier's attach bone as the carrier's animation poses it now; null without the carrier's skeleton or that
    /// bone.
    /// </summary>
    CarryAttach? AttachOf(CharacterTable table, CharacterCold cold)
    {
        if (!table.TryResolveNext(cold.CarriedBy, out int slot) || table.Cold(slot) is not { Appearance: { } look } carrier
            || _rigs.For(look) is not { } setup)
            return null;

        if (setup.Rig.Handle(CarrySystem.AttachBone) is not int bone) return null;
        var scratch = setup.Rig.Rent();
        try
        {
            PoseDerived(scratch, setup, carrier);
            var at = scratch.DerivedPosition[bone] - new Vector3(0, setup.GroundOffset, 0);
            var turned = scratch.DerivedRotation[bone];
            if (cold.Appearance is { } body && _rigs.For(body) is { Animal: true } animal)
                return AnimalAttach(cold, body, animal, at, Quaternion.Normalize(turned * setup.Rig.BindRotationInverse[bone]))?.On(at, turned);

            return CarryPose.Attach(at, turned, CarryPose.Height01(cold.Appearance)).On(at, turned);
        }
        finally
        {
            setup.Rig.Return(scratch);
        }
    }

    /// <summary>
    /// A carried animal's node, placed so that the body of its carry bone sits at <paramref name="at"/> turned by <paramref name="rotation"/>: the
    /// carrier's attach bone and its turn from the binding pose, the original's pin of that body (docs/game/ragdolls.md "Bodies: kinematic or
    /// simulated"); its other bodies hang from it. Null without the body.
    /// </summary>
    CarryAttach? AnimalAttach(CharacterCold cold, CharacterAppearance look, CharacterPoseSetup setup, Vector3 at, Quaternion rotation)
    {
        float scale = ScaleOf(cold);
        if (DefinitionOf(look, setup.Rig, scale) is not { } definition || CarryBoneOf(look, setup.Rig) is not (>= 0 and var bone)) return null;
        if (definition.BodyOfBone[bone] is not (>= 0 and var b)) return null;
        var scratch = setup.Rig.Rent();
        try
        {
            PoseDerived(scratch, setup, cold);

            // The pin places the actor, which has no rotation of its own; the body is its shape, at the shape's pose on it.
            var bind = definition.Bodies[b].Bind;
            var onActor = new RigidTransform(bind.Position - definition.Bodies[b].Actor, bind.Rotation);
            var target = onActor.Then(new RigidTransform(at, rotation));
            var boneInModel = new RigidTransform(scratch.DerivedPosition[bone] * scale, scratch.DerivedRotation[bone]);
            var body = definition.Bodies[b].BoneInBody.Inverse().Then(boneInModel);
            var turn = Quaternion.Normalize(target.Rotation * Quaternion.Inverse(body.Rotation));
            return new CarryAttach(target.Position - Vector3.Transform(body.Position, turn), turn);
        }
        finally
        {
            setup.Rig.Return(scratch);
        }
    }

    /// <summary>
    /// The ragdoll's size: an animal's age-lerped size, 1 for a person.
    /// </summary>
    static float ScaleOf(CharacterCold cold) => cold is { Fighter.IsAnimal: true, Medical: { } size } ? size.ScaleK : 1;

    /// <summary>
    /// Poses <paramref name="scratch"/> as the character's animation and its fixed posture layers do now (model space).
    /// </summary>
    void PoseDerived(PoseScratch scratch, CharacterPoseSetup setup, CharacterCold cold)
    {
        var layers = new List<ActiveLayer>();
        if (cold.Animation is { } animation && _animations is not null)
        {
            foreach (var layer in _animations.Publish(animation, []))
            {
                if (setup.Rig.Layer(_rigs.Database, layer.Name, setup.Animal) is { } def)
                    layers.Add(new ActiveLayer(def, MathF.Max(layer.Time, 0), layer.Weight));
            }
        }

        foreach (var (def, time) in setup.Fixed) layers.Add(new ActiveLayer(def, time, 1));
        scratch.PoseDerived(setup.Shape, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(layers));
    }

    /// <summary>
    /// Whether a saved ragdoll belongs to this one: a pose for each body, its root where the character stands (a platoon that travelled since moves
    /// its characters). Saves made before the root followed the actor's origin hold the root shape's centre as the character's place; either fits.
    /// </summary>
    static bool Fits(Ragdoll r, SavedRagdoll saved, Vector3 at)
    {
        if (saved.Pose.Length != r.Definition.Bodies.Length) return false;
        return Near(r.Definition.RootAt(saved.Pose), at) || Near(saved.Pose[r.Definition.Root].Position, at);
    }

    /// <summary>
    /// Whether <paramref name="root"/> is within 20 units of <paramref name="at"/> on the ground plane and 50 in height.
    /// </summary>
    static bool Near(Vector3 root, Vector3 at) =>
        Vector2.DistanceSquared(new Vector2(root.X, root.Z), new Vector2(at.X, at.Z)) <= 20 * 20 && MathF.Abs(root.Y - at.Y) <= 50;

    /// <summary>
    /// A whole-body ragdoll's state for a save (<see cref="RagdollSave"/>); null when the character has none or only a carried or limb ragdoll.
    /// </summary>
    public SavedRagdoll? SaveState(CharacterId id) =>
        _ragdolls.TryGetValue(id, out var r) && (r.Mask & RagdollPart.Whole) != 0 && (r.Mask & RagdollPart.CarryMode) == 0 && r.Pose.Length > 0
            ? new SavedRagdoll(r.Frozen, r.Age, (RigidTransform[])r.Pose.Clone())
            : null;

    /// <summary>
    /// Every body's pose where the character's animation puts its bone now, in world space.
    /// </summary>
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
                var bone = new RigidTransform(Vector3.Transform(scratch.DerivedPosition[h], placement),
                    Quaternion.Normalize(turn * scratch.DerivedRotation[h]));
                poses[b] = bodies[b].BoneInBody.Inverse().Then(bone);
            }

            return poses;
        }
        finally
        {
            rig.Return(scratch);
        }
    }

    /// <summary>
    /// Which bodies a mask simulates (docs/game/ragdolls.md, "Bodies: kinematic or simulated").
    /// </summary>
    static bool Simulates(Ragdoll r, RagdollPart mask, RagdollBody body)
    {
        if ((mask & RagdollPart.Whole) != 0) return true;
        string n = body.Name;
        if ((mask & RagdollPart.CarryMode) != 0)
        {
            if (r.Setup.Animal) return body.Bone != r.CarryBone;
            return n is "Bip01 L Forearm" or "Bip01 R Forearm" or "Bip01 Head";
        }

        return (mask & RagdollPart.LeftArm) != 0 && n is "Bip01 L UpperArm" or "Bip01 L Forearm"
            || (mask & RagdollPart.RightArm) != 0 && n is "Bip01 R UpperArm" or "Bip01 R Forearm"
            || (mask & RagdollPart.LeftLeg) != 0 && n is "Bip01 L Thigh" or "Bip01 L Calf"
            || (mask & RagdollPart.RightLeg) != 0 && n is "Bip01 R Thigh" or "Bip01 R Calf"
            || (mask & RagdollPart.Head) != 0 && n is "Bip01 Head" or "Bip01 Neck";
    }

    /// <summary>
    /// Replaces the bodies for <paramref name="mask"/>: simulated bodies, and pinned (kinematic) ones joined to them. A body that was in the scene
    /// keeps its pose and velocity, a new one starts at the animated pose at rest (the game starts from the animated pose with no velocity and no hit
    /// impulse). Meitou: a new body of a whole (not carried) ragdoll starts at <paramref name="start"/>'s velocity (<see cref="StartVelocity"/>);
    /// with <paramref name="soft"/> its joints get soft limits (<see cref="AddJoint"/>).
    /// </summary>
    void Rebuild(Ragdoll r, RagdollPart mask, RigidTransform[] animated, RagdollBlow start = default, bool soft = false, ulong seed = 0)
    {
        if ((mask & RagdollPart.Whole) == 0 || (mask & RagdollPart.CarryMode) != 0) start = default;
        var struck = Struck(r, start.Bone);
        var bodies = r.Definition.Bodies;
        var velocities = new BodyVelocity[bodies.Length];
        var had = new bool[bodies.Length];
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is { } handle)
            {
                var body = _physics.Simulation.Bodies.GetBodyReference(handle);
                r.Pose[b] = new RigidTransform(body.Pose.Position, body.Pose.Orientation);
                velocities[b] = body.Velocity;
                had[b] = r.Dynamic[b];
            }
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

        // A whole body that starts in the ground (a foot on a slope: the animated pose is not clipped to the terrain) is lifted out of it as one, as
        // the game snaps a new ragdoll up to the terrain: the ground is a one-sided mesh, and a body that starts behind it has nothing to land on.
        float lift = (mask & RagdollPart.Whole) != 0 && !had.Contains(true) ? OutOfGround(_environment, animated, dynamic) : 0;
        r.Tag = _nextTag++;
        r.Bodies = new BodyHandle?[bodies.Length];
        r.Dynamic = dynamic;
        for (int b = 0; b < bodies.Length; b++)
        {
            // A simulated body carries on from where it was simulated (a body dropped from the shoulder falls from there); anything else starts from
            // the animation, and so does everything when the body is lifted onto a shoulder.
            bool keep = had[b] && dynamic[b] && (mask & RagdollPart.CarryMode) == 0;
            var pose = keep ? r.Pose[b] : lift > 0 ? animated[b] with { Position = animated[b].Position + new Vector3(0, lift, 0) } : animated[b];
            var velocity = keep ? velocities[b] : StartVelocity(r, b, start, struck, seed);
            if (!needed[b]) { r.Pose[b] = pose; continue; }
            r.Pose[b] = pose;
            var rigid = new RigidPose(pose.Position, pose.Rotation);
            var collidable = RagdollPhysics.Collidable(kinds[b].Shape);
            var activity = new BodyActivityDescription(0.01f);
            var description = dynamic[b]
                ? BodyDescription.CreateDynamic(rigid, velocity, kinds[b].Inertia, collidable, activity)
                : BodyDescription.CreateKinematic(rigid, collidable, activity);
            r.Bodies[b] = _physics.AddBody(description, new BodyTag { Ragdoll = r.Tag, Index = b, Joined = r.Definition.Joined[b] });
        }

        foreach (var j in r.Definition.Joints)
            if (dynamic[j.A] || dynamic[j.B]) AddJoint(r, j);
    }

    /// <summary>
    /// How far a new whole-body ragdoll must go up to have no body centre under the terrain (the lowest ends 0.25 above it); 0 when none is under it,
    /// or when it is more than <see cref="MaxLift"/> (a floor sunk into a hill, not a foot in a slope).
    /// </summary>
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
    /// A revolute joint's spring (docs/game/ragdolls.md "Joint springs"): a torque of stiffness x (angle - target) - damper x spin about the hinge,
    /// as the file's spring 1 sets it (the head's neck joint, 10000 with no damper). As a servo on the hinge angle with the frequency and damping
    /// ratio of that spring on the joint's moving mass: the inertia about the axis of the lighter body (a pinned body does not move; the other one
    /// does), omega = sqrt(k / I), ratio = damper / (2 sqrt(k I)).
    /// </summary>
    TwistServo HingeSpring(Ragdoll r, RagdollJoint j)
    {
        var shapes = Shapes(r.Definition);
        float Inertia(int body, Vector3 axis)
        {
            var inverse = shapes[body].Inertia.InverseInertiaTensor;
            float along = inverse.XX * axis.X * axis.X + inverse.YY * axis.Y * axis.Y + inverse.ZZ * axis.Z * axis.Z
                + 2 * (inverse.YX * axis.X * axis.Y + inverse.ZX * axis.X * axis.Z + inverse.ZY * axis.Y * axis.Z);
            return along > 1e-9f ? 1 / along : 1;
        }

        float inertia = r.Dynamic[j.A] && r.Dynamic[j.B]
            ? 1 / (1 / Inertia(j.A, j.AxisA) + 1 / Inertia(j.B, j.AxisB))
            : r.Dynamic[j.A] ? Inertia(j.A, j.AxisA) : Inertia(j.B, j.AxisB);
        float omega = MathF.Sqrt(j.SpringStiffness / inertia);
        float ratio = j.SpringDamper / (2 * MathF.Sqrt(j.SpringStiffness * inertia));
        return new TwistServo
        {
            LocalBasisA = j.BasisA,
            LocalBasisB = j.BasisB,
            TargetAngle = j.SpringTarget,
            SpringSettings = new SpringSettings(omega / (2 * MathF.PI), ratio),
            ServoSettings = new ServoSettings(float.MaxValue, 0, float.MaxValue),
        };
    }

    /// <summary>
    /// Adds a joint's constraints: a hinge or a ball socket with its limits (and the soft ones), a hinge's spring, else a weld.
    /// </summary>
    void AddJoint(Ragdoll r, RagdollJoint j)
    {
        var a = r.Bodies[j.A]!.Value;
        var b = r.Bodies[j.B]!.Value;
        var solver = _physics.Simulation.Solver;
        var spring = new SpringSettings(30, 1);
        switch (j.Type)
        {
            case PhysicsJointType.Revolute:
                r.Joints.Add(solver.Add(a, b,
                    new Hinge { LocalOffsetA = j.OffsetA, LocalHingeAxisA = j.AxisA, LocalOffsetB = j.OffsetB, LocalHingeAxisB = j.AxisB,
                        SpringSettings = spring }));
                if (j.Limits)
                {
                    r.Joints.Add(solver.Add(a, b,
                        new TwistLimit { LocalBasisA = j.BasisA, LocalBasisB = j.BasisB, MinimumAngle = j.Low, MaximumAngle = j.High,
                            SpringSettings = spring }));
                }

                if (j.SpringStiffness > 0) r.Joints.Add(solver.Add(a, b, HingeSpring(r, j)));
                if (j.Limits && r.Soft)
                {
                    float band = SoftBand * (j.High - j.Low);
                    r.Joints.Add(solver.Add(a, b,
                        new TwistLimit { LocalBasisA = j.BasisA, LocalBasisB = j.BasisB, MinimumAngle = j.Low + band, MaximumAngle = j.High - band,
                            SpringSettings = SoftLimit }));
                }

                break;
            case PhysicsJointType.Spherical:
                r.Joints.Add(solver.Add(a, b, new BallSocket { LocalOffsetA = j.OffsetA, LocalOffsetB = j.OffsetB, SpringSettings = spring }));
                if (j.Limits)
                {
                    r.Joints.Add(solver.Add(a, b,
                        new SwingLimit { AxisLocalA = j.AxisA, AxisLocalB = j.AxisB, MaximumSwingAngle = j.Swing, SpringSettings = spring }));
                    r.Joints.Add(solver.Add(a, b,
                        new TwistLimit { LocalBasisA = j.BasisA, LocalBasisB = j.BasisB, MinimumAngle = j.Low, MaximumAngle = j.High,
                            SpringSettings = spring }));
                    if (r.Soft)
                    {
                        float band = SoftBand * (j.High - j.Low);
                        r.Joints.Add(solver.Add(a, b,
                            new SwingLimit { AxisLocalA = j.AxisA, AxisLocalB = j.AxisB, MaximumSwingAngle = j.Swing * (1 - SoftBand),
                                SpringSettings = SoftLimit }));
                        r.Joints.Add(solver.Add(a, b,
                            new TwistLimit { LocalBasisA = j.BasisA, LocalBasisB = j.BasisB, MinimumAngle = j.Low + band,
                                MaximumAngle = j.High - band, SpringSettings = SoftLimit }));
                    }
                }

                break;
            default:
                var (ra, rb) = (r.Definition.Bodies[j.A].Bind, r.Definition.Bodies[j.B].Bind);
                var relative = rb.Then(ra.Inverse());
                r.Joints.Add(solver.Add(a, b,
                    new Weld { LocalOffset = relative.Position, LocalOrientation = relative.Rotation, SpringSettings = spring }));
                break;
        }
    }

    /// <summary>
    /// The shapes and inertias of a definition's bodies (made once per definition).
    /// </summary>
    (TypedIndex Shape, BodyInertia Inertia)[] Shapes(RagdollDefinition definition)
    {
        if (_shapes.TryGetValue(definition, out var made)) return made;
        var all = _physics.Simulation.Shapes;
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
                    var hull = new BepuPhysics.Collidables.ConvexHull(body.Hull, _physics.Pool, out _);
                    made[b] = (all.Add(hull), hull.ComputeInertia(body.Mass));
                    break;
                default:
                    var box = new Box(body.Size.X, body.Size.Y, body.Size.Z);
                    made[b] = (all.Add(box), box.ComputeInertia(body.Mass));
                    break;
            }
        }

        return _shapes[definition] = made;
    }

    #endregion

    #region Advanced ragdolls (Meitou, BodyOptions.AdvancedRagdolls; docs/game/ragdolls.md "Advanced ragdolls (Meitou)")

    /// <summary>
    /// The relaxed pull a ragdoll's joints get: none (awake, or Faithful), the weak one of a dead body, the resting muscle tone of an unconscious
    /// one.
    /// </summary>
    internal enum Tone
    {
        /// <summary>
        /// No pull.
        /// </summary>
        None,
        /// <summary>
        /// The weak pull of a dead body.
        /// </summary>
        Dead,
        /// <summary>
        /// The resting muscle tone of an unconscious one.
        /// </summary>
        Unconscious
    }

    /// <summary>
    /// Soft limits: each limit gets an inner one this share of its range in from either end, a firm spring (20 Hz, critically damped) that stops
    /// the joint a quarter of its range short of the hard stop (engine choice). A weak overdamped one (5 Hz, ratio 3) lets a collapsing back creep
    /// into the file's lumbar and thoracic stops (docs/game/ragdolls.md "Rest poses of the spine").
    /// </summary>
    const float SoftBand = 0.25f;
    /// <summary>
    /// The spring of the soft limits (see <see cref="SoftBand"/>).
    /// </summary>
    static readonly SpringSettings SoftLimit = new(20, 1);
    /// <summary>
    /// The relaxed angle of a hinge: this share of the way from the binding pose (0) to the middle of its range (a knee bent about 50 degrees, an
    /// elbow about 45).
    /// </summary>
    const float RelaxedShare = 0.6f;
    /// <summary>
    /// The spin a blow's push gives an arm or a leg: rad/s per unit/s of push, at most this, scaled by a roll of 0.6 to 1.4.
    /// </summary>
    const float SpinPerPush = 0.15f, MaxPushSpin = 6;
    /// <summary>
    /// The share of a blow's push the other bodies take when one is struck (it takes the whole push), and at most this many times its own share of
    /// the body's mass.
    /// </summary>
    const float PushSpread = 0.4f, MaxFocusGain = 4;

    /// <summary>
    /// The tone of a whole or carried ragdoll; none for a conscious character's collapsed limbs (a broken limb hangs loose) or without advanced
    /// ragdolls.
    /// </summary>
    static Tone ToneOf(CharacterCold cold, RagdollPart mask, bool advanced) =>
        !advanced || (mask & (RagdollPart.Whole | RagdollPart.CarryMode)) == 0 || cold.Medical is not { Incapacitated: true } medical ? Tone.None
        : medical.Dead ? Tone.Dead : Tone.Unconscious;

    /// <summary>
    /// Sets the relaxed pull: a weak, capped servo on each joint's twist (the hinge angle) towards its relaxed angle (<see cref="RelaxedShare"/> of a
    /// hinge's mid-range; a shoulder's twist to its binding pose, its swing left to gravity), so a limp body drifts into a natural rest instead of
    /// floating to its limits. Gravity beats it. Not on a joint with the file's own spring (the head's).
    /// </summary>
    void SetTone(Ragdoll r, Tone tone)
    {
        foreach (var held in r.Relax) _physics.Simulation.Solver.Remove(held);
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
            r.Relax.Add(_physics.Simulation.Solver.Add(a, b,
                new TwistServo { LocalBasisA = j.BasisA, LocalBasisB = j.BasisB, TargetAngle = target, SpringSettings = spring,
                    ServoSettings = servo }));
        }
    }

    /// <summary>
    /// A new body's start velocity: <paramref name="start"/>'s velocity, its arms and legs spun by the push (<see cref="Spin"/>). With a struck body
    /// (<see cref="Struck"/>) the others keep only <see cref="PushSpread"/> of the push and the struck one takes the whole push as an impulse on it
    /// alone (times the ragdoll's mass over its own, at most <see cref="MaxFocusGain"/>), so the body is thrown from the part that was hit and its
    /// joints carry the rest along. Uncapped: the physics holds every body to <see cref="RagdollPhysics.MaxSpeed"/>.
    /// </summary>
    static BodyVelocity StartVelocity(Ragdoll r, int b, RagdollBlow start, (int Body, float Gain) struck, ulong seed)
    {
        var linear = start.Velocity;
        if (struck.Body >= 0)
        {
            float share = b == struck.Body ? struck.Gain : PushSpread;
            linear += start.Push * (share - 1);
        }

        return new BodyVelocity(linear, Spin(r, r.Definition.Bodies[b].Name, b, start.Push, seed));
    }

    /// <summary>
    /// The body moving <paramref name="bone"/> (else its nearest ancestor's) and the ragdoll's mass over that body's, at most
    /// <see cref="MaxFocusGain"/>; body -1 when no bone is named or none of it moves.
    /// </summary>
    static (int Body, float Gain) Struck(Ragdoll r, string? bone)
    {
        var rig = r.Setup.Rig;
        if (bone is null || rig.Handle(bone) is not int h) return (-1, 0);
        while (h >= 0 && r.Definition.BodyOfBone[h] < 0) h = rig.Parent[h];
        if (h < 0) return (-1, 0);
        var bodies = r.Definition.Bodies;
        int body = r.Definition.BodyOfBone[h];
        float total = 0;
        foreach (var each in bodies) total += each.Mass;
        return (body, MathF.Min(total / bodies[body].Mass, MaxFocusGain));
    }

    /// <summary>
    /// The spin a blow's <paramref name="push"/> gives body <paramref name="b"/> (named <paramref name="name"/>) as it starts: the upper arms swing
    /// the way the push goes, the thighs against it (the feet held the ground), each by its own roll, so a body thrown back trails its arms and kicks
    /// its legs forward. Zero for other bodies or no push.
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

    #endregion

    #region Bodies

    /// <summary>
    /// Drag of a slide (per second) and the flat speed below which it stops: a 30 u/s throw goes about 10 units.
    /// </summary>
    const float SlideDrag = 3, SlideStop = 0.5f;

    /// <summary>
    /// Sets the pinned bodies to the animated pose with no velocity, as the original does: the joints and contacts see the jump, not a speed.
    /// </summary>
    void Pin(Ragdoll r, RigidTransform[] animated, float dt)
    {
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is not { } handle || r.Dynamic[b]) continue;
            var body = _physics.Simulation.Bodies.GetBodyReference(handle);
            var target = animated[b];

            // The original sets the pose and zeroes both velocities (FUN_1407d1550 -> FUN_1401a9ea0 / FUN_1401a9f00): the joints see only the
            // position jump.
            body.Pose = new RigidPose(target.Position, target.Rotation);
            body.Velocity = default;
            if (!body.Awake) body.Awake = true;
        }

        // Partial ragdolls are woken every frame (FUN_1407d3c00, wake 20).
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is { } handle && r.Dynamic[b] && _physics.Simulation.Bodies.GetBodyReference(handle) is { Awake: false } sleeping)
                sleeping.Awake = true;
        }
    }

    /// <summary>
    /// A body that went through the ground between two ticks is put back on it. The ground is a one-sided mesh, so a body that tunnels
    /// (<see cref="RagdollPhysics.MaxSpeed"/> makes it rare, not impossible: a thin limb spun hard onto a slope) has nothing left to stop it and
    /// would fall on until the freeze. Only a crossing counts, from at or above the surface to more than <see cref="SinkDepth"/> under it: a body
    /// that starts below the terrain (a floor sunk into a hill) is left where it is.
    /// </summary>
    void Surface(Ragdoll r, IRagdollEnvironment env)
    {
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is not { } handle || !r.Dynamic[b]) continue;
            var body = _physics.Simulation.Bodies.GetBodyReference(handle);
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

    /// <summary>
    /// Reads the bodies' poses back into <see cref="Ragdoll.Pose"/>.
    /// </summary>
    void ReadBack(Ragdoll r)
    {
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is { } handle)
            {
                var pose = _physics.Simulation.Bodies.GetBodyReference(handle).Pose;
                r.Pose[b] = new RigidTransform(pose.Position, pose.Orientation);
            }
        }
    }

    /// <summary>
    /// The share of the ragdoll's bodies in the scene that are still (1 for none).
    /// </summary>
    float StillShare(Ragdoll r)
    {
        int still = 0, count = 0;
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is not { } handle) continue;
            count++;
            var v = _physics.Simulation.Bodies.GetBodyReference(handle).Velocity;
            if (v.Linear.LengthSquared() < StillLinear && v.Angular.LengthSquared() < StillAngular) still++;
        }

        return count == 0 ? 1 : still / (float)count;
    }

    /// <summary>
    /// A ragdoll of a race that <c>swims</c> (race byte <c>+0x79</c>) whose root is at or under the water level (docs/game/ragdolls.md "Bodies:
    /// kinematic or simulated").
    /// </summary>
    static bool Afloat(World world, Ragdoll r) =>
        r.Pose.Length > 0 && r.Definition.RootAt(r.Pose).Y <= Data.World.WorldWater.Height
        && world.Characters.TryResolveNext(r.Id, out int slot) && world.Characters.Cold(slot)?.Race is { Swims: true };

    /// <summary>
    /// Buoyancy (<b>Observed</b>, the ragdoll update): while <see cref="Afloat"/>, each simulated body under the water level gets an upward
    /// acceleration of 98.1 + ln(level - y + 1), which beats gravity, so the body floats up to the surface.
    /// </summary>
    void Buoy(World world, Ragdoll r, float dt)
    {
        if (!Afloat(world, r)) return;
        float level = Data.World.WorldWater.Height;
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is not { } handle || !r.Dynamic[b]) continue;
            var body = _physics.Simulation.Bodies[handle];
            float y = body.Pose.Position.Y;
            if (y > level) continue;
            body.Velocity.Linear.Y += dt * (RagdollPhysics.Gravity + MathF.Log(level - y + 1));
            body.Awake = true;
        }
    }

    /// <summary>
    /// A downed character without a ragdoll (no ragdoll file for its look): takes a pending <see cref="CharacterCold.Launch"/> as a slide, and moves
    /// along it, dragging to a stop, kept on the ground. Not in a bed, a cage or carried.
    /// </summary>
    static void Slide(ref CharacterHot hot, CharacterCold cold, IRagdollEnvironment env, float dt)
    {
        if (cold.Launch is { } launch)
        {
            cold.Launch = null;
            if (cold.InSomething == Locks.HeldState.None && cold.CarriedBy.IsNone && cold.Medical is { Incapacitated: true })
                cold.Slide = new Vector3(launch.X, 0, launch.Z);
        }

        var v = cold.Slide;
        if (v == default) return;
        var at = hot.Position + v * dt;
        hot.Position = new Vector3(at.X, env.GroundHeight(at.X, at.Z, hot.Position.Y), at.Z);
        hot.Velocity = Vector3.Zero;
        v *= MathF.Exp(-SlideDrag * dt);
        cold.Slide = v.Length() < SlideStop ? default : v;
    }

    /// <summary>
    /// Throws a whole-body ragdoll: adds <paramref name="push"/> to every simulated body (arms and legs spun by it, <see cref="StartVelocity"/>). A
    /// frozen ragdoll is rebuilt from its pose first, so it is simulated again with its joints; the ragdoll's age starts over.
    /// </summary>
    void Launch(Ragdoll r, Vector3 push, bool soft, ulong seed)
    {
        if (r.Frozen) Rebuild(r, r.Mask, (RigidTransform[])r.Pose.Clone(), default, soft, seed);
        var blow = new RagdollBlow(push, push, null);
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is not { } handle || !r.Dynamic[b]) continue;
            var body = _physics.Simulation.Bodies.GetBodyReference(handle);
            var add = StartVelocity(r, b, blow, (-1, 0), seed);
            body.Velocity.Linear += add.Linear;
            body.Velocity.Angular += add.Angular;
            body.Awake = true;
        }

        r.Age = 0;
    }

    /// <summary>
    /// Freezes a settled ragdoll: the joints go and the bodies stay as still kinematic ones, which others still land on.
    /// </summary>
    void Freeze(Ragdoll r)
    {
        ReadBack(r);
        foreach (var joint in r.Joints) _physics.Simulation.Solver.Remove(joint);
        r.Joints.Clear();
        SetTone(r, Tone.None);
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is { } handle)
            {
                var body = _physics.Simulation.Bodies.GetBodyReference(handle);
                body.Velocity = default;
                if (r.Dynamic[b]) body.BecomeKinematic();
            }
        }

        r.Frozen = true;
    }

    /// <summary>
    /// Moves every body by <paramref name="by"/> and stops it.
    /// </summary>
    void Shift(Ragdoll r, Vector3 by)
    {
        for (int b = 0; b < r.Bodies.Length; b++)
        {
            if (r.Bodies[b] is { } handle)
            {
                var body = _physics.Simulation.Bodies.GetBodyReference(handle);
                body.Pose.Position += by;
                body.Velocity = default;
            }
        }

        ReadBack(r);
    }

    /// <summary>
    /// Starts getting up: the character turns to face from the root to the head along the ground and stands where the root lies; the bones blend
    /// back.
    /// </summary>
    void StartGetUp(World world, Ragdoll r, ref CharacterHot hot)
    {
        var rig = r.Setup.Rig;
        var root = r.Definition.RootAt(r.Pose);
        if (rig.Handle("Bip01 Head") is int head && r.Definition.BodyOfBone[head] is int hb and >= 0)
        {
            var toward = r.Pose[hb].Position - root;
            if (toward.X * toward.X + toward.Z * toward.Z > 1e-4f) hot.Yaw = MathF.Atan2(toward.X, toward.Z);
        }

        var walk = world.Walkability;
        if (walk.IsWalkable(root.X, root.Z, root.Y)) hot.Position = new Vector3(root.X, walk.GroundHeight(root.X, root.Z, root.Y), root.Z);
        r.BlendLeft = GetUpBlend;
    }

    /// <summary>
    /// The bones the ragdoll sets, in the model space of <paramref name="placement"/>: the simulated bodies' bones (all of them while getting up,
    /// blended).
    /// </summary>
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

    #endregion

    #region Removal and the hash

    /// <summary>
    /// Removes the ragdoll's joints, servos and bodies from the scene.
    /// </summary>
    void Clear(Ragdoll r)
    {
        foreach (var joint in r.Joints) _physics.Simulation.Solver.Remove(joint);
        r.Joints.Clear();
        SetTone(r, Tone.None);
        for (int b = 0; b < r.Bodies.Length; b++)
            if (r.Bodies[b] is { } handle) _physics.Simulation.Bodies.Remove(handle);
        r.Bodies = [];
        r.Dynamic = [];
    }

    /// <summary>
    /// Removes the ragdoll from the scene and the system.
    /// </summary>
    void Destroy(Ragdoll r)
    {
        Clear(r);
        _ragdolls.Remove(r.Id);
    }

    /// <summary>
    /// Adds the ragdolls to the world's state hash, in the order they were made.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (_ragdolls.Count == 0) return;
        hasher.Add(0x52616764);
        foreach (var id in _order)
        {
            var r = _ragdolls[id];
            hasher.Add(id);
            hasher.Add((int)r.Mask);
            hasher.Add(r.Frozen);
            hasher.Add(r.Age);
            hasher.Add(r.BlendLeft);
            foreach (var p in r.Pose)
            {
                hasher.Add(p.Position);
                hasher.Add(p.Rotation.X);
                hasher.Add(p.Rotation.Y);
                hasher.Add(p.Rotation.Z);
                hasher.Add(p.Rotation.W);
            }
        }
    }

    /// <summary>
    /// Frees the physics scene and its threads.
    /// </summary>
    public void Dispose()
    {
        _physics.Dispose();
        _threads?.Dispose();
    }

    #endregion
}