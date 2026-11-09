using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;

namespace Meitou.Simulation.Ragdolls;

/// <summary>
/// What a body is to the collision filter: its ragdoll (a running number, 0 for none) and its index there, and the bodies of the same ragdoll joined
/// to it.
/// </summary>
internal struct BodyTag
{
    /// <summary>
    /// The body's ragdoll (0: none).
    /// </summary>
    public int Ragdoll;
    /// <summary>
    /// The body's index in its ragdoll.
    /// </summary>
    public int Index;
    /// <summary>
    /// The bodies of the same ragdoll joined to it, one bit per index (indices from 64 on are not filtered).
    /// </summary>
    public ulong Joined;
}

/// <summary>
/// The contact rules (docs/game/ragdolls.md, "Collision groups" and the materials): ragdolls collide with each other, with the ground and the
/// buildings, but two bodies of one ragdoll joined by a joint do not (no shipped joint turns that on). Friction 1 as the game's ragdoll material; its
/// restitution of 0.05 is left out (Bepu has none).
/// </summary>
internal struct ContactRules : INarrowPhaseCallbacks
{
    /// <summary>
    /// The bodies' tags.
    /// </summary>
    public CollidableProperty<BodyTag> Tags;

    /// <summary>
    /// Initializes the tags for the simulation.
    /// </summary>
    public void Initialize(BepuPhysics.Simulation simulation) => Tags.Initialize(simulation);

    /// <summary>
    /// Whether two collidables may touch: one must be dynamic; a static always may; two bodies of one ragdoll only when no joint joins them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        if (a.Mobility != CollidableMobility.Dynamic && b.Mobility != CollidableMobility.Dynamic) return false;
        if (a.Mobility == CollidableMobility.Static || b.Mobility == CollidableMobility.Static) return true;
        ref var ta = ref Tags[a.BodyHandle];
        ref var tb = ref Tags[b.BodyHandle];
        return ta.Ragdoll != tb.Ragdoll || ta.Ragdoll == 0 || tb.Index >= 64 || (ta.Joined & (1UL << tb.Index)) == 0;
    }

    /// <summary>
    /// Every child pair of a compound may touch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    /// <summary>
    /// Every contact gets <see cref="RagdollPhysics.Friction"/>, <see cref="RagdollPhysics.RecoverySpeed"/> and a stiff spring.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
        out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial = new PairMaterialProperties(RagdollPhysics.Friction, RagdollPhysics.RecoverySpeed, new SpringSettings(30, 1));
        return true;
    }

    /// <summary>
    /// Every child contact is kept.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
        ref ConvexContactManifold manifold) => true;

    /// <summary>
    /// Frees the tags.
    /// </summary>
    public void Dispose() => Tags.Dispose();
}

/// <summary>
/// Gravity and the damping of a simulated ragdoll body (docs/game/ragdolls.md: linear 0.05, angular 0.01 per second, gravity 98.1 units/s² down).
/// </summary>
internal struct Integration : IPoseIntegratorCallbacks
{
    /// <summary>
    /// The gravity of one step.
    /// </summary>
    Vector3Wide _gravityDt;
    /// <summary>
    /// The linear and angular damping factors of one step, and the squared speed and spin caps.
    /// </summary>
    Vector<float> _linear, _angular, _maxSpeedSquared, _maxSpinSquared;

    /// <summary>
    /// Angular motion is integrated without conserving momentum.
    /// </summary>
    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    /// <summary>
    /// Unconstrained bodies take no substeps.
    /// </summary>
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    /// <summary>
    /// Kinematic bodies are not integrated.
    /// </summary>
    public readonly bool IntegrateVelocityForKinematics => false;

    /// <summary>
    /// Nothing to set up.
    /// </summary>
    public void Initialize(BepuPhysics.Simulation simulation) { }

    /// <summary>
    /// Works out the step's gravity, damping factors and caps.
    /// </summary>
    public void PrepareForIntegration(float dt)
    {
        _gravityDt = Vector3Wide.Broadcast(new Vector3(0, -RagdollPhysics.Gravity * dt, 0));
        _linear = new Vector<float>(1 / (1 + RagdollPhysics.LinearDamping * dt));
        _angular = new Vector<float>(1 / (1 + RagdollPhysics.AngularDamping * dt));
        _maxSpeedSquared = new Vector<float>(RagdollPhysics.MaxSpeed * RagdollPhysics.MaxSpeed);
        _maxSpinSquared = new Vector<float>(RagdollPhysics.MaxSpin * RagdollPhysics.MaxSpin);
    }

    /// <summary>
    /// Adds gravity, damps, then caps the speed and the spin.
    /// </summary>
    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia,
        Vector<int> integrationMask,
        int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
    {
        velocity.Linear = (velocity.Linear + _gravityDt) * _linear;
        velocity.Angular *= _angular;

        // A body is never faster than the ground can catch (see RagdollPhysics.MaxSpeed): a joint pushed past its limits would otherwise fling a limb
        // through the terrain.
        var speedSquared = Vector3Wide.Dot(velocity.Linear, velocity.Linear);
        var keep = Vector.ConditionalSelect(Vector.GreaterThan(speedSquared, _maxSpeedSquared), Vector.SquareRoot(_maxSpeedSquared / speedSquared),
            Vector<float>.One);
        velocity.Linear *= keep;
        var spinSquared = Vector3Wide.Dot(velocity.Angular, velocity.Angular);
        velocity.Angular *= Vector.ConditionalSelect(Vector.GreaterThan(spinSquared, _maxSpinSquared),
            Vector.SquareRoot(_maxSpinSquared / spinSquared), Vector<float>.One);
    }
}

/// <summary>
/// The physics scene of the ragdolls: a Bepu simulation stepped on the calling thread only (no thread dispatcher, so the result is the same whatever
/// the world's thread count), the ragdoll bodies, and static ground and building tiles made around the moving bodies. The scene settings follow the
/// game's PhysX scene where Bepu has a match (docs/game/ragdolls.md, "Middleware and scene").
/// </summary>
internal sealed class RagdollPhysics : IDisposable
{
    #region The scene

    /// <summary>
    /// Gravity in units/s² down, the linear and angular damping per second, and the contact friction.
    /// </summary>
    public const float Gravity = 98.1f, LinearDamping = 0.05f, AngularDamping = 0.01f, Friction = 1;
    /// <summary>
    /// The largest speed a contact pushes bodies apart with (engine choice: 2 m/s).
    /// </summary>
    public const float RecoverySpeed = 20;
    /// <summary>
    /// The fastest a body may move, in units per second, and the speculative margin of its collidable. The ground is a one-sided triangle mesh: a
    /// body that gets more than its own radius past it in one 1/30 s tick has no contact left (it is behind the face) and falls on for ever. Bepu
    /// only makes contacts within the margin, so the margin must reach a whole tick of the fastest motion (engine choice; 20 m/s is a fall of 20 m,
    /// far more than any in the game, and only a joint gone wrong gets there).
    /// </summary>
    public const float MaxSpeed = 200, SpeculativeMargin = 8;
    /// <summary>
    /// The fastest a body may turn, in radians per second: PhysX's default NX_MAX_ANGULAR_VELOCITY (7), which the game leaves: its bodies' own
    /// maximum is -1 (= the global one) and the SDK parameter is never set (docs/game/ragdolls.md "Limits of motion").
    /// </summary>
    public const float MaxSpin = 7;

    /// <summary>
    /// Solver substeps per tick and velocity iterations per substep: 4 substeps at 30 Hz is 120 Hz, finer than PhysX's 1/60 s maximum step (engine
    /// choice).
    /// </summary>
    const int Substeps = 4, Iterations = 2;
    /// <summary>
    /// Constraint batches before Bepu puts a body's further constraints in its sequential fallback batch (its default is 64). Bepu 2.4.0 throws in
    /// the narrow phase's flush when the fallback's last constraint on a dynamic body goes in the same flush as one on a kinematic body (it empties
    /// its count table, then looks the kinematic up in it), which a corpse heap of a big battle reaches; no body gets this many constraints, so the
    /// fallback is never used. Only lists of this length are allocated for it.
    /// </summary>
    const int FallbackBatches = 1024;
    /// <summary>
    /// The longest step the original's physics takes (<c>FUN_1407dd060</c>: <c>DAT_1416fb690</c> = 0.04 s, <b>Verified</b>).
    /// </summary>
    public const float MaxStep = 0.04f;

    /// <summary>
    /// The memory pool of the simulation.
    /// </summary>
    public readonly BufferPool Pool = new();
    /// <summary>
    /// The Bepu simulation.
    /// </summary>
    public readonly BepuPhysics.Simulation Simulation;
    /// <summary>
    /// The bodies' collision tags.
    /// </summary>
    readonly CollidableProperty<BodyTag> _tags;

    /// <summary>
    /// Makes the deterministic simulation with the contact rules and integration.
    /// </summary>
    public RagdollPhysics()
    {
        _tags = new CollidableProperty<BodyTag>(Pool);
        Simulation = BepuPhysics.Simulation.Create(Pool, new ContactRules { Tags = _tags }, new Integration(),
            new SolveDescription(Iterations, Substeps, FallbackBatches));
        Simulation.Deterministic = true;
    }

    /// <summary>
    /// The collidable of a ragdoll body of <paramref name="shape"/>.
    /// </summary>
    public static CollidableDescription Collidable(TypedIndex shape) => new(shape, SpeculativeMargin);

    /// <summary>
    /// The step of one world tick (docs/game/ragdolls.md "Stepping"): the original's physics gets the frame's real elapsed time, not the
    /// game-speed-scaled one (<c>FUN_140788a00</c>), so a tick of <paramref name="tickSeconds"/> game seconds at <paramref name="speed"/> times real
    /// time is a real step of tickSeconds / speed, clamped to <see cref="MaxStep"/>.
    /// </summary>
    public static float StepOf(float tickSeconds, float speed) => MathF.Min(tickSeconds / (speed >= 1 ? speed : 1), MaxStep);

    /// <summary>
    /// Steps the bodies, on <paramref name="threads"/> when given (<see cref="BepuPhysics.Simulation.Deterministic"/> is on, so the result does not
    /// depend on them).
    /// </summary>
    public void Step(float dt, IThreadDispatcher? threads = null) => Simulation.Timestep(dt, threads);

    /// <summary>
    /// The collision tag of <paramref name="body"/>.
    /// </summary>
    public ref BodyTag Tag(BodyHandle body) => ref _tags[body];

    /// <summary>
    /// Adds a body with its collision tag.
    /// </summary>
    public BodyHandle AddBody(in BodyDescription description, BodyTag tag)
    {
        var handle = Simulation.Bodies.Add(description);
        _tags.Allocate(handle) = tag;
        return handle;
    }

    /// <summary>
    /// Frees the simulation and its memory.
    /// </summary>
    public void Dispose()
    {
        Simulation.Dispose();
        Pool.Clear();
    }

    #endregion

    #region Warm-up

    /// <summary>
    /// Whether the warm-up was started (1: yes).
    /// </summary>
    static int s_warmed;

    /// <summary>
    /// Once per process, on a worker: a throwaway scene with every shape, joint and kind of contact the ragdolls use, stepped and frozen as a ragdoll
    /// is. The game compiles each method once, fully optimised, on its first call (no tiering, DECISIONS 19): without this the tick that first moved
    /// a ragdoll compiled Bepu's solver and collision code, 200-250 ms, and the next ones 10-60 ms more (docs/bench.md "Play benchmark").
    /// </summary>
    public static void WarmUpOnce()
    {
        if (Interlocked.Exchange(ref s_warmed, 1) == 0) Task.Run(WarmUp);
    }

    /// <summary>
    /// Runs the warm-up scene; a failure is ignored.
    /// </summary>
    static void WarmUp()
    {
        try
        {
            // Declared first so it outlives the physics: the pair cache keeps worker caches in the dispatcher's thread pools, read by every later
            // step (single-threaded too) and freed when the simulation is disposed (disposed first, it was a use after free: a rare crash).
            using var threads = new ThreadDispatcher(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
            using var physics = new RagdollPhysics();
            var simulation = physics.Simulation;
            physics.Cover(new Vector3(16, 0, 16), 20, new WarmUpEnvironment(), 0);
            Span<Vector3> corners = stackalloc Vector3[8];
            for (int i = 0; i < 8; i++) corners[i] = new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f) * 2;
            var hull = new ConvexHull(corners, physics.Pool, out _);
            (TypedIndex Shape, BodyInertia Inertia)[] shapes =
            [
                (simulation.Shapes.Add(new Sphere(1)), new Sphere(1).ComputeInertia(1)),
                (simulation.Shapes.Add(new Capsule(0.6f, 2.5f)), new Capsule(0.6f, 2.5f).ComputeInertia(1)),
                (simulation.Shapes.Add(new Box(2, 1.5f, 2.5f)), new Box(2, 1.5f, 2.5f).ComputeInertia(1)),
                (simulation.Shapes.Add(hull), hull.ComputeInertia(1)),
            ];
            var spring = new SpringSettings(30, 1);
            var joints = new List<ConstraintHandle>();
            var bodies = new List<BodyHandle>();

            // Three chains of five, piled on each other, the ground and the building's corner; the first body of the first is pinned
            // (kinematic, moving).
            AddChains(physics, shapes, spring, joints, bodies);

            // Single-threaded, then on a dispatcher as RagdollSystem steps with more than one world thread: Bepu's multithreaded solver and broad
            // phase are code paths of their own (they still compiled 50 ms in the first moving tick with the single-threaded warm-up alone).
            for (int tick = 0; tick < 45; tick++) physics.Step(1 / 30f);
            for (int tick = 0; tick < 45; tick++) physics.Step(1 / 30f, threads);

            // Frozen as RagdollSystem.Freeze does: the joints go, the bodies stop and turn kinematic; then the tiles are swept away.
            foreach (var joint in joints) simulation.Solver.Remove(joint);
            foreach (var handle in bodies)
            {
                var body = simulation.Bodies.GetBodyReference(handle);
                body.Velocity = default;
                if (!body.Kinematic) body.BecomeKinematic();
            }

            for (int tick = 0; tick < 5; tick++) physics.Step(1 / 30f);
            physics.Sweep(TileLife + 1);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"warning   the ragdoll warm-up failed: {e.Message}");
        }
    }

    /// <summary>
    /// Three chains of five, piled on each other, the ground and the building's corner; the first body of the first is pinned (kinematic,
    /// moving). Each joint kind the ragdolls use joins them.
    /// </summary>
    static void AddChains(RagdollPhysics physics, (TypedIndex Shape, BodyInertia Inertia)[] shapes, SpringSettings spring,
        List<ConstraintHandle> joints, List<BodyHandle> bodies)
    {
        var simulation = physics.Simulation;
        for (int r = 0; r < 3; r++)
        {
            var chain = new BodyHandle[5];
            for (int b = 0; b < chain.Length; b++)
            {
                var (shape, inertia) = shapes[(b + r) % shapes.Length];
                var pose = new RigidPose(new Vector3(8 + 3 * r + 1.6f * b, 2 + 2.5f * r, 10 + 0.5f * b),
                    Quaternion.CreateFromYawPitchRoll(r, 0.3f * b, 0.2f));
                var activity = new BodyActivityDescription(0.01f);
                var description = r == 0 && b == 0
                    ? BodyDescription.CreateKinematic(pose, new BodyVelocity(new Vector3(6, 0, 0)), Collidable(shape), activity)
                    : BodyDescription.CreateDynamic(pose, inertia, Collidable(shape), activity);
                bodies.Add(chain[b] = physics.AddBody(description,
                    new BodyTag { Ragdoll = r + 1, Index = b, Joined = (b > 0 ? 1UL << (b - 1) : 0) | (b < 4 ? 1UL << (b + 1) : 0) }));
            }

            var solver = simulation.Solver;
            joints.Add(solver.Add(chain[0], chain[1],
                new Hinge { LocalOffsetA = new(0.8f, 0, 0), LocalHingeAxisA = Vector3.UnitZ, LocalOffsetB = new(-0.8f, 0, 0),
                    LocalHingeAxisB = Vector3.UnitZ, SpringSettings = spring }));
            joints.Add(solver.Add(chain[0], chain[1],
                new TwistLimit { LocalBasisA = Quaternion.Identity, LocalBasisB = Quaternion.Identity, MinimumAngle = -0.5f, MaximumAngle = 0.5f,
                    SpringSettings = spring }));
            joints.Add(solver.Add(chain[1], chain[2],
                new BallSocket { LocalOffsetA = new(0.8f, 0, 0), LocalOffsetB = new(-0.8f, 0, 0), SpringSettings = spring }));
            joints.Add(solver.Add(chain[1], chain[2],
                new SwingLimit { AxisLocalA = Vector3.UnitX, AxisLocalB = Vector3.UnitX, MaximumSwingAngle = 0.6f, SpringSettings = spring }));
            joints.Add(solver.Add(chain[1], chain[2],
                new TwistLimit { LocalBasisA = Quaternion.Identity, LocalBasisB = Quaternion.Identity, MinimumAngle = -0.3f, MaximumAngle = 0.3f,
                    SpringSettings = spring }));
            joints.Add(solver.Add(chain[2], chain[3],
                new Weld { LocalOffset = new(1.6f, 0, 0), LocalOrientation = Quaternion.Identity, SpringSettings = spring }));
            joints.Add(solver.Add(chain[3], chain[4],
                new BallSocket { LocalOffsetA = new(0.8f, 0, 0), LocalOffsetB = new(-0.8f, 0, 0), SpringSettings = spring }));
        }
    }

    /// <summary>
    /// The warm-up's ground (gently waved) and building (a box standing on it).
    /// </summary>
    sealed class WarmUpEnvironment : IRagdollEnvironment
    {
        /// <summary>
        /// A gently waved ground.
        /// </summary>
        public float GroundHeight(float x, float z, float nearY) => 0.3f * MathF.Sin(0.4f * x) * MathF.Cos(0.3f * z);

        /// <summary>
        /// A box standing on the ground at (14, 14), 6 by 4 by 6.
        /// </summary>
        public bool Buildings(Vector3 min, Vector3 max, List<Vector3> triangles)
        {
            static Vector3 P(int i) => new(i & 1, (i >> 1) & 1, (i >> 2) & 1);
            int[] faces = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];
            foreach (int i in faces) triangles.Add(new Vector3(14, 0, 14) + P(i) * new Vector3(6, 4, 6));
            return true;
        }
    }

    #endregion

    #region Static tiles

    /// <summary>
    /// Tile edge and ground cell spacing in world units. A tile's ground is a mesh of <see cref="Cell"/>-unit cells (the heightmap's samples are 18
    /// units apart: finer cells only tessellate the same bilinear patches). Engine choice: the Bepu mesh build is nearly all of a tile's cost,
    /// 0.45 ms at 2-unit cells (512 triangles), and a zone's downed characters arriving together asked for 80-100 tiles in one tick.
    /// </summary>
    public const float TileSize = RagdollSystem.TileSize, Cell = 4;
    /// <summary>
    /// Ticks a tile outlives its last use: a tile no moving body has needed for this many ticks is removed (engine choice: a minute, so a camera
    /// flight back to a zone finds the tiles its bodies used; a tile is a few KB).
    /// </summary>
    const int TileLife = 1800;
    /// <summary>
    /// A tile's building mesh counts this many tile units of the budget: a town tile's is 2 ms against a ground mesh's 0.12 (**Observed**, headless
    /// flight probe).
    /// </summary>
    public const int BuildingUnits = 4;

    /// <summary>
    /// A static tile: its ground mesh, its building mesh and when it was last used.
    /// </summary>
    sealed class Tile
    {
        /// <summary>
        /// The tile's ground static and its building static.
        /// </summary>
        public StaticHandle Ground, Buildings;
        /// <summary>
        /// The shapes of the ground and of the buildings.
        /// </summary>
        public TypedIndex GroundShape, BuildingShape;
        /// <summary>
        /// Whether the tile has a building mesh, and whether its buildings are still to be asked for.
        /// </summary>
        public bool HasBuildings, BuildingsPending;
        /// <summary>
        /// The environment's <see cref="IRagdollEnvironment.Generation"/> the buildings were asked at.
        /// </summary>
        public int Generation;
        /// <summary>
        /// The tick the tile was last used at.
        /// </summary>
        public long Used;
    }

    /// <summary>
    /// The tiles by tile coordinates.
    /// </summary>
    readonly Dictionary<(int X, int Z), Tile> _tiles = [];
    /// <summary>
    /// A scratch list of the tiles a sweep removes.
    /// </summary>
    readonly List<(int X, int Z)> _stale = [];
    /// <summary>
    /// A scratch list of a tile's building triangles.
    /// </summary>
    readonly List<Vector3> _gathered = [];

    /// <summary>
    /// The number of tiles.
    /// </summary>
    public int TileCount => _tiles.Count;

    /// <summary>
    /// Makes sure the tiles round <paramref name="centre"/> within <paramref name="reach"/> exist (ground from <paramref name="environment"/>),
    /// marked used at <paramref name="tick"/>, within <paramref name="budget"/> tile units (a ground mesh 1, a building mesh
    /// <see cref="BuildingUnits"/>; spent as it goes): a tile the budget does not reach stays missing or without its buildings until a later call, so
    /// a zone's tiles arriving together cost a few ticks, not one.
    /// </summary>
    public void Cover(Vector3 centre, float reach, IRagdollEnvironment environment, long tick, ref int budget)
    {
        var (x0, x1, z0, z1) = Span(centre, reach);
        for (int x = x0; x <= x1; x++)
        {
            for (int z = z0; z <= z1; z++)
            {
                if (!_tiles.TryGetValue((x, z), out var tile))
                {
                    if (budget <= 0) continue;
                    budget--;
                    _tiles[(x, z)] = tile = MakeTile(x, z, centre.Y, environment, ref budget);
                }
                else
                {
                    // Collision that changed since the tile's buildings were built (a building placed or ruined): asked again, the old mesh standing
                    // until the new one is ready.
                    if (tile.Generation != environment.Generation) (tile.Generation, tile.BuildingsPending) = (environment.Generation, true);
                    if (tile.BuildingsPending && budget > 0 && AddBuildings(tile, new Vector3(x * TileSize, 0, z * TileSize), environment))
                        budget -= BuildingUnits;
                }

                tile.Used = tick;
            }
        }
    }

    /// <summary>
    /// As <see cref="Cover(Vector3, float, IRagdollEnvironment, long, ref int)"/> without a budget (the warm-up, tests).
    /// </summary>
    public void Cover(Vector3 centre, float reach, IRagdollEnvironment environment, long tick)
    {
        int budget = int.MaxValue;
        Cover(centre, reach, environment, tick, ref budget);
    }

    /// <summary>
    /// Whether every tile round <paramref name="centre"/> within <paramref name="reach"/> exists with its buildings known (not
    /// <see cref="Tile.BuildingsPending"/>): a body that starts on a tile whose floors are not there yet falls through them to the ground below
    /// (Observed: a knocked-out character on a town gate's walkway, the zone's collision still being read).
    /// </summary>
    public bool Covered(Vector3 centre, float reach)
    {
        var (x0, x1, z0, z1) = Span(centre, reach);
        for (int x = x0; x <= x1; x++)
        {
            for (int z = z0; z <= z1; z++)
                if (!_tiles.TryGetValue((x, z), out var tile) || tile.BuildingsPending) return false;
        }

        return true;
    }

    /// <summary>
    /// The tile coordinates round <paramref name="centre"/> within <paramref name="reach"/>.
    /// </summary>
    static (int X0, int X1, int Z0, int Z1) Span(Vector3 centre, float reach) =>
        ((int)MathF.Floor((centre.X - reach) / TileSize), (int)MathF.Floor((centre.X + reach) / TileSize),
            (int)MathF.Floor((centre.Z - reach) / TileSize), (int)MathF.Floor((centre.Z + reach) / TileSize));

    /// <summary>
    /// Removes the tiles not used for a while.
    /// </summary>
    public void Sweep(long tick)
    {
        _stale.Clear();
        foreach (var (key, tile) in _tiles) if (tick - tile.Used > TileLife) _stale.Add(key);
        _stale.Sort();
        foreach (var key in _stale)
        {
            RemoveTile(_tiles[key]);
            _tiles.Remove(key);
        }
    }

    /// <summary>
    /// Makes a tile: its ground mesh from the environment's heights, and its buildings when the budget allows.
    /// </summary>
    Tile MakeTile(int tx, int tz, float nearY, IRagdollEnvironment environment, ref int budget)
    {
        const int n = (int)(TileSize / Cell);
        var origin = new Vector3(tx * TileSize, 0, tz * TileSize);
        var heights = new float[(n + 1) * (n + 1)];
        for (int i = 0; i <= n; i++)
        {
            for (int k = 0; k <= n; k++)
                heights[i * (n + 1) + k] = environment.GroundHeight(origin.X + i * Cell, origin.Z + k * Cell, nearY);
        }

        Pool.Take<Triangle>(n * n * 2, out var triangles);
        int t = 0;
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < n; k++)
            {
                Vector3 P(int a, int b) => new(a * Cell, heights[a * (n + 1) + b], b * Cell);

                // Wound so the face looks up (Bepu's triangles collide on one side).
                triangles[t++] = new Triangle(P(i, k), P(i + 1, k), P(i, k + 1));
                triangles[t++] = new Triangle(P(i + 1, k), P(i + 1, k + 1), P(i, k + 1));
            }
        }

        var tile = new Tile { Generation = environment.Generation };
        tile.GroundShape = Simulation.Shapes.Add(new Mesh(triangles, Vector3.One, Pool));
        tile.Ground = Simulation.Statics.Add(new StaticDescription(origin, Quaternion.Identity, tile.GroundShape));

        // Its buildings when the budget allows, else on a later cover (the tile asks again while BuildingsPending).
        tile.BuildingsPending = true;
        if (budget > 0 && AddBuildings(tile, origin, environment)) budget -= BuildingUnits;
        return tile;
    }

    /// <summary>
    /// The buildings' triangles of a tile, once they are all known (until then the tile has ground only and asks again); true when a mesh was built
    /// (the budget's charge).
    /// </summary>
    bool AddBuildings(Tile tile, Vector3 origin, IRagdollEnvironment environment)
    {
        _gathered.Clear();
        tile.BuildingsPending = !environment.Buildings(origin, origin + new Vector3(TileSize, 0, TileSize), _gathered);
        if (tile.BuildingsPending) return false;
        if (_gathered.Count < 3)
        {
            if (!tile.HasBuildings) return false;
            RemoveBuildings(tile);
            return true;
        }

        // Both faces of every triangle: the files' windings are not trusted, and a body may meet a wall or floor from either side.
        int count = _gathered.Count / 3;
        Pool.Take<Triangle>(count * 2, out var building);
        for (int i = 0; i < count; i++)
        {
            Vector3 a = _gathered[3 * i] - origin, b = _gathered[3 * i + 1] - origin, c = _gathered[3 * i + 2] - origin;
            building[2 * i] = new Triangle(a, b, c);
            building[2 * i + 1] = new Triangle(a, c, b);
        }

        if (tile.HasBuildings) RemoveBuildings(tile);
        tile.BuildingShape = Simulation.Shapes.Add(new Mesh(building, Vector3.One, Pool));
        tile.Buildings = Simulation.Statics.Add(new StaticDescription(origin, Quaternion.Identity, tile.BuildingShape));
        tile.HasBuildings = true;
        return true;
    }

    /// <summary>
    /// Removes a tile's building mesh.
    /// </summary>
    void RemoveBuildings(Tile tile)
    {
        Simulation.Statics.Remove(tile.Buildings);
        Simulation.Shapes.RemoveAndDispose(tile.BuildingShape, Pool);
        tile.HasBuildings = false;
    }

    /// <summary>
    /// Removes a tile's ground and building meshes.
    /// </summary>
    void RemoveTile(Tile tile)
    {
        Simulation.Statics.Remove(tile.Ground);
        Simulation.Shapes.RemoveAndDispose(tile.GroundShape, Pool);
        if (!tile.HasBuildings) return;
        Simulation.Statics.Remove(tile.Buildings);
        Simulation.Shapes.RemoveAndDispose(tile.BuildingShape, Pool);
    }

    #endregion
}

/// <summary>
/// What ragdolls fall onto: the ground height (the terrain, or a floor near <c>nearY</c>) and the collision triangles of the buildings in a box. The
/// host gives the real terrain and the buildings' collision files; without one, <see cref="WalkabilityEnvironment"/> uses the world's walkable ground
/// and no buildings.
/// </summary>
public interface IRagdollEnvironment
{
    /// <summary>
    /// The ground height at (<paramref name="x"/>, <paramref name="z"/>): the terrain, or a floor near <paramref name="nearY"/>.
    /// </summary>
    float GroundHeight(float x, float z, float nearY);
    /// <summary>
    /// Changes whenever the buildings' collision changed (a building placed, removed or ruined at run time): tiles built from an older one are built
    /// again.
    /// </summary>
    int Generation => 0;
    /// <summary>
    /// Adds the triangles (three points each, world space, any winding) of the building collision between <paramref name="min"/> and
    /// <paramref name="max"/> (X and Z).
    /// </summary>
    /// <returns>False while some of them are not known yet (being read): the tile asks again.</returns>
    bool Buildings(Vector3 min, Vector3 max, List<Vector3> triangles);
    /// <summary>
    /// The triangles of collision groups 23..26 only (passable parts, buildings paths ignore) between the corners, what the weather's shelter ray
    /// tests; the same meaning of the result. Environments without them answer with the ordinary buildings.
    /// </summary>
    bool Roofs(Vector3 min, Vector3 max, List<Vector3> triangles) => Buildings(min, max, triangles);
}

/// <summary>
/// The stand-in environment: the world's walkable ground, no buildings.
/// </summary>
public sealed class WalkabilityEnvironment(IWalkability walkability) : IRagdollEnvironment
{
    /// <summary>
    /// The walkable ground.
    /// </summary>
    public float GroundHeight(float x, float z, float nearY) => walkability.GroundHeight(x, z, nearY);
    /// <summary>
    /// No buildings: always known, none added.
    /// </summary>
    public bool Buildings(Vector3 min, Vector3 max, List<Vector3> triangles) => true;
}