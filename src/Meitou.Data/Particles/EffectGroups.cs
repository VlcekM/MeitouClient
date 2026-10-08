using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Data.Particles;

/// <summary>The camera as the groups see it each frame: where the eye is and which way it looks.</summary>
public readonly record struct EffectCamera(Vector3 Eye, Vector3 Forward);

/// <summary>Where an effect may be placed: a random point of an area (the weather region's cells, or a disc round the camera).</summary>
public interface IEffectArea
{
    bool TryPick(Random random, out float x, out float z);
}

/// <summary>A disc (sqrt-uniform): the viewer's test override <c>--particle-area</c>, and the fallback when no region is known.</summary>
public sealed class DiscArea(float centreX, float centreZ, float radius) : IEffectArea
{
    public bool TryPick(Random random, out float x, out float z)
    {
        float a = random.NextSingle() * MathF.Tau, r = MathF.Sqrt(random.NextSingle()) * radius;
        x = centreX + MathF.Cos(a) * r;
        z = centreZ + MathF.Sin(a) * r;
        return true;
    }
}

/// <summary>
/// A weather region's cells (docs/formats/weather.md "Where"): a random point is taken in a uniformly chosen cell, inside the middle 80 % of it
/// (Verified (decompiled), FUN_1408fc3c0: a margin of 0.2 · 0.5 of the width on each side; the margin's sign in z is not resolved in the decompile, both axes use it here).
/// </summary>
public sealed class RegionArea : IEffectArea
{
    readonly (float X0, float Z0, float X1, float Z1)[] rects;

    public RegionArea(IEnumerable<(float X0, float Z0, float X1, float Z1)> rects) => this.rects = [.. rects];
    public int Count => rects.Length;

    /// <summary>The cells that have the colour of the cell at (<paramref name="x"/>, <paramref name="z"/>) (the game's region cells), or null off the map.</summary>
    public static RegionArea? At(WeatherAreas areas, float x, float z)
    {
        var (cx, cz) = WeatherAreas.CellOf(x, z);
        if (!WeatherAreas.InMap(cx, cz)) return null;
        int colour = areas.ColourOf(cx, cz);
        var list = new List<(float, float, float, float)>();
        for (int j = 0; j < WeatherAreas.Cells; j++)
            for (int i = 0; i < WeatherAreas.Cells; i++)
                if (areas.ColourOf(i, j) == colour) list.Add(WeatherAreas.CellRect(i, j));
        return new RegionArea(list);
    }

    public bool TryPick(Random random, out float x, out float z)
    {
        x = z = 0;
        if (rects.Length == 0) return false;
        var r = rects[random.Next(rects.Length)];
        float mx = (r.X1 - r.X0) * 0.1f, mz = (r.Z1 - r.Z0) * 0.1f;
        x = r.X0 + mx + random.NextSingle() * (r.X1 - r.X0 - 2 * mx);
        z = r.Z0 + mz + random.NextSingle() * (r.Z1 - r.Z0 - 2 * mz);
        return true;
    }
}

/// <summary>
/// What the groups need of the world besides the camera: the ground height (called on the main thread only: the renderer's height grid swaps as
/// the terrain streams), the area effects are placed in, and how far from the camera effects are simulated.
/// </summary>
public sealed class EffectWorld
{
    public Func<float, float, float> GroundHeight { get; set; } = static (_, _) => 100;
    /// <summary>Null: a disc round the camera of <see cref="FallbackRadius"/>.</summary>
    public IEffectArea? Area { get; set; }
    /// <summary>
    /// How far from the camera effects exist for us (Observed: the game simulates the active zones, 3 × 3 of 4608 units, and an effect in an
    /// inactive zone only within its <c>maximum view distance</c> of them): an effect is simulated while the camera is within this plus its view
    /// distance of its bounds.
    /// </summary>
    public float ActiveRadius { get; set; } = 6912;
    public float FallbackRadius { get; set; } = 8000;
    /// <summary>The biome's ground colour at a place (for <c>ground colour</c> effects); null: not used.</summary>
    public Func<float, float, Vector3>? GroundColour { get; set; }

    /// <summary>The y of the terrain normal at a point, by finite differences of the height (the game's own terrain query returns the normal).</summary>
    public float NormalY(float x, float z)
    {
        const float s = 12;
        float dx = GroundHeight(x - s, z) - GroundHeight(x + s, z), dz = GroundHeight(x, z - s) - GroundHeight(x, z + s);
        return 1 / MathF.Sqrt(1 + (dx * dx + dz * dz) / (4 * s * s));
    }

    /// <summary>
    /// A place for an effect (Verified (decompiled), FUN_1408fc3c0): a random point of the area, the ground height there, up to 10 tries for
    /// one whose height is within <c>min/max altitude</c> (skipped when both are 0) and whose terrain normal y is within <c>min/max slope</c>;
    /// after 10 tries the last stands.
    /// </summary>
    public Vector3 Place(EffectRecord e, Random random, in EffectCamera camera)
    {
        var area = Area ?? new DiscArea(camera.Eye.X, camera.Eye.Z, FallbackRadius);
        float x = camera.Eye.X, z = camera.Eye.Z, y = 0;
        for (int tries = 10; tries > 0; tries--)
        {
            if (!area.TryPick(random, out x, out z)) { x = camera.Eye.X; z = camera.Eye.Z; }
            y = GroundHeight(x, z);
            bool altitude = (e.MinAltitude == 0 && e.MaxAltitude == 0) || (y >= e.MinAltitude && y <= e.MaxAltitude);
            float ny = NormalY(x, z);
            if (altitude && ny >= e.MinSlope && ny <= e.MaxSlope) break;
        }
        return new Vector3(x, y, z);
    }
}

/// <summary>How an effect's particles are kept near its node.</summary>
public enum UnitWrap { None, Cube, Sphere }

/// <summary>
/// One live effect (the game's "effect handler"): a particle system at a place. It exists as a record (position, life) all the time and holds a
/// simulation only while the camera is near enough to see it (<see cref="Simulation"/> is null otherwise: Observed, the plugin's own
/// <c>nonvisible_update_timeout</c> is the same idea). The main thread moves it (<see cref="EffectGroup.Update"/>) and queues time in
/// <see cref="Pending"/>; <see cref="Advance"/> runs the simulation and may run on any thread, one unit per thread.
/// </summary>
public sealed class EffectUnit
{
    public EffectUnit(EffectRecord effect, PuSystemDef system, int seed)
    {
        Effect = effect;
        System = system;
        Seed = seed;
        LongestLife = ParticleSimulation.LongestLife(system);
    }

    public EffectRecord Effect { get; }
    public PuSystemDef System { get; }
    public int Seed { get; }
    public float LongestLife { get; }
    public ParticleSimulation? Simulation { get; set; }
    /// <summary>The node's place in the world.</summary>
    public Vector3 Position { get; set; }
    /// <summary>Where the simulation's coordinates start in the world (kept near the node so a far-off place does not cost the particles precision).</summary>
    public Vector3 Anchor { get; set; }
    public Vector3 Tint { get; set; } = Vector3.One;
    public float Alpha { get; set; } = 1;
    /// <summary>The node's bounding sphere for culling (<see cref="float.PositiveInfinity"/>: always drawn).</summary>
    public float Radius { get; set; }
    /// <summary>Seconds of life left (<see cref="float.PositiveInfinity"/>: never ends).</summary>
    public float Life { get; set; } = float.PositiveInfinity;
    /// <summary>Seconds since the unit was made.</summary>
    public float Age { get; set; }
    /// <summary>The life is over: the emitters make nothing more, the particles live out (Verified (decompiled), the handlers' stop).</summary>
    public bool Stopped { get; set; }
    public float StoppedFor { get; set; }
    public ParticleEnvironment Environment { get; set; } = ParticleEnvironment.None;
    public float DistanceToCamera { get; set; }
    // wandering
    public Vector2 Heading { get; set; }
    public float TurnTimer { get; set; }
    public float GroundTimer { get; set; }
    // wrapping
    public UnitWrap Wrap { get; set; }
    public Vector3 WrapCentre { get; set; }
    public float WrapSize { get; set; }
    public float WrapMaxHeight { get; set; } = float.PositiveInfinity;
    /// <summary>Seconds of simulation to run on the next <see cref="Advance"/>.</summary>
    public float Pending { get; set; }
    int wrapSalt;

    public bool Active => Simulation is not null;
    public bool Done => Stopped && (Simulation is null || Simulation.ParticleCount == 0);

    /// <summary>The fog volumes' opacity factor: in over <c>fog fade in duration</c> from the unit's start, out over <c>fog fade out duration</c> from its stop (0 = no fade).</summary>
    public float FogFade
    {
        get
        {
            float f = Effect.FogFadeIn > 0 ? Math.Clamp(Age / Effect.FogFadeIn, 0, 1) : 1;
            if (Stopped) f *= Effect.FogFadeOut > 0 ? Math.Clamp(1 - StoppedFor / Effect.FogFadeOut, 0, 1) : 0;
            return f;
        }
    }

    public void Activate()
    {
        Simulation = new ParticleSimulation(System, Seed) { EmissionStopped = Stopped };
        Anchor = Position;
    }

    public void Deactivate() { Simulation = null; Pending = 0; }

    /// <summary>Runs the queued time: <see cref="Pending"/> in steps of at most 1/30 s, wrapping the particles after each.</summary>
    public void Advance()
    {
        var sim = Simulation;
        if (sim is null) { Pending = 0; return; }
        var local = Position - Anchor;
        if (local.LengthSquared() > 1500f * 1500f)
        {
            sim.Translate(-local);
            Anchor = Position;
            local = Vector3.Zero;
        }
        sim.WorldOffset = Anchor;
        sim.Origin = local;
        sim.EmissionStopped = Stopped;
        float left = Pending;
        Pending = 0;
        // A backlog (a unit just woken, or a frame hitch) is caught up in coarser steps (1/10 s) and over several frames: at most MaxStepsPerFrame
        // steps per call, the rest stays in Pending. Observed choice; the game simulates what it activates at once.
        const float substep = 1f / 30;
        for (int steps = 0; left > 1e-5f; steps++)
        {
            if (steps >= MaxStepsPerFrame) { Pending = left; break; }
            float s = Math.Min(left, left > 3f ? 0.1f : substep);
            left -= s;
            sim.Advance(s, Environment);
            ApplyWrap(sim);
        }
    }

    /// <summary>Simulation steps one <see cref="Advance"/> call runs at most.</summary>
    public const int MaxStepsPerFrame = 40;

    /// <summary>True while the unit is still catching up on a backlog of more than a second (it is not drawn until it has).</summary>
    public bool CatchingUp => Pending > 1f;

    void ApplyWrap(ParticleSimulation sim)
    {
        switch (Wrap)
        {
            case UnitWrap.Cube: sim.Wrap(WrapCentre - Anchor, WrapSize / 2); break;
            case UnitWrap.Sphere: sim.WrapSphere(WrapCentre - Anchor, WrapSize, WrapMaxHeight - Anchor.Y, wrapSalt++); break;
        }
    }
}

/// <summary>
/// One entry of the weather's effect list at work (docs/formats/weather.md "Spawning"): the group class is keyed by the EFFECT <c>type</c>;
/// <see cref="EffectGroups.Create"/> makes the right one. A group owns its <see cref="Units"/>. Two phases per frame: <see cref="Update"/> on the
/// main thread (schedules, places and moves the units, queues the time) and <see cref="Simulate"/> (the particles; thread-safe per unit).
/// </summary>
public abstract class EffectGroup
{
    protected readonly List<EffectUnit> units = [];
    protected readonly Random random;
    protected readonly EffectWorld world;
    readonly int seed;
    int made;

    protected EffectGroup(EffectRecord effect, WeatherEffectEntry? entry, PuSystemDef system, int seed, EffectWorld world)
    {
        Effect = effect;
        Entry = entry;
        System = system;
        this.seed = seed;
        this.world = world;
        random = new Random(seed * 7919 + 17);
        Tint = TintOf(effect);
    }

    public EffectRecord Effect { get; }
    public WeatherEffectEntry? Entry { get; }
    public PuSystemDef System { get; }
    public IReadOnlyList<EffectUnit> Units => units;
    /// <summary>The EFFECT's <c>colour multiplier</c> applied to the particles' rgb (Observed: "for weather effects only"; a black multiplier, which would blank the particles, is ignored: <c>Sand-Stream any altitude</c> has one next to <c>ground colour</c>).</summary>
    public Vector3 Tint { get; }
    public int ParticleCount => units.Sum(u => u.Simulation?.ParticleCount ?? 0);

    static Vector3 TintOf(EffectRecord e) => e.ColourMultiplier.LengthSquared() < 1e-4f ? Vector3.One : e.ColourMultiplier;

    /// <summary>Steps the group by <paramref name="dt"/> seconds of the frame clock (0: not at all, a held-still frame): the main-thread part.</summary>
    public abstract void Update(float dt, in EffectCamera camera, WeatherEffectInput weather);

    /// <summary>
    /// Starts the group as if it had been running for <paramref name="seconds"/> (the viewer's start-up choice, Observed; the game has only
    /// <c>fast_forward</c>): the schedule runs in steps, the first unit of every respawning group exists at once, and the simulations are run.
    /// </summary>
    public abstract void Prewarm(float seconds, in EffectCamera camera, WeatherEffectInput weather);

    /// <summary>Runs every unit's queued time (the units are independent: in parallel when there are several).</summary>
    public void Simulate()
    {
        while (true)
        {
            var active = units.Where(u => u.Active && u.Pending > 0).ToArray();
            if (active.Length == 0) return;
            if (active.Length > 1) Parallel.ForEach(active, u => u.Advance());
            else foreach (var u in active) u.Advance();   // a backlog is run in full here (start-up), unlike the per-frame path
        }
    }

    /// <summary>The emission multiplier from the wind: <c>min wind span rate</c> is the wind speed needed for any particles, <c>max wind span rate</c> for all of them.</summary>
    public static float WindSpanScale(EffectRecord effect, float windSpeed)
    {
        float lo = effect.MinWindSpan, hi = effect.MaxWindSpan;
        if (hi > lo) return Math.Clamp((windSpeed - lo) / (hi - lo), 0, 1);
        return lo > 0 && windSpeed < lo ? 0 : 1;
    }

    protected ParticleEnvironment Environment(WeatherEffectInput weather)
    {
        var e = Effect;
        return new ParticleEnvironment(weather.Wind, e.WindSpeedMultiplier, WindSpanScale(e, weather.Wind.Length()), e.WindAffected, e.WindDirectionEmission);
    }

    protected EffectUnit NewUnit(Vector3 position)
    {
        var u = new EffectUnit(Effect, System, seed * 31 + made++) { Position = position, Anchor = position, Tint = Tint, Radius = System.BoundingRadius };
        if (Effect.GroundColour && world.GroundColour is { } ground) u.Tint = Tint * ground(position.X, position.Z);
        // The life: random between min and max time to live, none when the maximum is 0 (Verified (decompiled), FUN_1401034f0).
        if (!Effect.Immortal) u.Life = Effect.MinTimeToLive + (Effect.MaxTimeToLive - Effect.MinTimeToLive) * random.NextSingle();
        units.Add(u);
        return u;
    }

    protected float Range(float a, float b) => a + (b - a) * random.NextSingle();

    /// <summary>
    /// What every unit gets each frame: age and life, then whether the camera is near enough for it to be simulated (activated with the particles it
    /// would have by now, the longest life or its age, at most 30 s), and the time queued for the simulation.
    /// </summary>
    protected void Tick(EffectUnit u, float dt, in EffectCamera camera, in ParticleEnvironment environment, bool alwaysActive = false)
    {
        u.Age += dt;
        if (!u.Stopped && u.Life != float.PositiveInfinity)
        {
            u.Life -= dt;
            if (u.Life <= 0) u.Stopped = true;
        }
        if (u.Stopped) u.StoppedFor += dt;
        u.Environment = environment;
        u.DistanceToCamera = Vector3.Distance(u.Position, camera.Eye);
        float range = world.ActiveRadius + Effect.MaximumViewDistance;
        float reach = float.IsPositiveInfinity(u.Radius) ? 0 : u.Radius;
        bool near = alwaysActive || u.DistanceToCamera - reach <= range;
        bool far = !alwaysActive && u.DistanceToCamera - reach > range * 1.15f;
        if (!u.Active && near && !u.Done)
        {
            u.Activate();
            u.Pending = Math.Min(Math.Min(u.Age, u.LongestLife), 30);
        }
        else if (u.Active && far) u.Deactivate();
        if (u.Active) u.Pending += dt;
    }

    protected void RemoveDone()
    {
        for (int i = units.Count - 1; i >= 0; i--)
            if (units[i].Done) units.RemoveAt(i);
    }
}

/// <summary>
/// CAMERA, CAMERA_RAIN and CAMERA_ACID_RAIN (docs/formats/weather.md "Spawning", Verified (decompiled): the camera group's functions): the
/// system's node sits at the camera's position and moves with it; its particles live in world space and a wrapping rule keeps them in a
/// cube of edge <c>2d / 1.5</c> centred <c>d</c> ahead of the camera along its view direction, where <c>d</c> is the effect's size, the EFFECT
/// loader's 0.75 × the largest Box extent (or Circle radius) times the system's scale (so the cube's edge is that extent: 120 for
/// <c>Kenshi_Heavy_Rain</c>, which is its emitter's box). Particles are emitted round the camera and wrapped into the cube in front of it.
/// There is exactly one handler whatever the entry's count (Verified (decompiled), FUN_140102ff0 sets the group's count to 1).
/// </summary>
public sealed class CameraEffectGroup : EffectGroup
{
    readonly EffectUnit unit;
    readonly float distance;
    bool started;

    public CameraEffectGroup(WeatherEffectEntry entry, PuSystemDef system, int seed, EffectWorld? world = null) : base(entry.Effect, entry, system, seed, world ?? new EffectWorld())
    {
        float size = system.LargestEmitterExtent * 0.75f;
        distance = size > 0 ? size : 100;
        unit = NewUnit(Vector3.Zero);
        unit.Radius = float.PositiveInfinity;
        unit.Wrap = UnitWrap.Cube;
        unit.WrapSize = Edge;
    }

    /// <summary>The cube's centre's distance ahead of the camera, <c>d</c>.</summary>
    public float Distance => distance;
    /// <summary>The cube's edge, <c>2d / 1.5</c>.</summary>
    public float Edge => distance * 2 / 1.5f;
    public ParticleSimulation Simulation => unit.Simulation!;
    /// <summary>Where the simulation's own coordinate origin is in the world: add it to <see cref="ParticleSimulation.Collect"/>'s positions.</summary>
    public Vector3 Anchor => unit.Anchor;

    static Vector3 Forward(in EffectCamera camera) => camera.Forward.LengthSquared() > 1e-6f ? Vector3.Normalize(camera.Forward) : -Vector3.UnitZ;

    /// <summary>The cube's centre in the world.</summary>
    public Vector3 Centre(in EffectCamera camera) => camera.Eye + Forward(camera) * distance;

    void Follow(in EffectCamera camera)
    {
        unit.Position = camera.Eye;
        unit.WrapCentre = Centre(camera);
        if (!started)
        {
            started = true;
            unit.Activate();
            unit.Anchor = camera.Eye;
        }
    }

    public override void Prewarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        Follow(camera);
        unit.Environment = Environment(weather);
        unit.Pending += seconds;
        Simulate();
    }

    public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
    {
        bool first = !started;
        Follow(camera);
        unit.Environment = Environment(weather);
        if (first && Simulation.Definition.FastForward is { } ff && ff.Time > 0) unit.Pending += ff.Time;
        unit.Pending += dt;
        unit.Age += dt;
    }
}

/// <summary>
/// POINT (2), POINT_LIGHTING (7) and WANDERING (3, 8, 9) groups (docs/formats/weather.md "Spawning"; Verified (decompiled): FUN_140102370 the
/// schedule, FUN_1401039a0 / FUN_140103ca0 the spawns, FUN_140102b50 the wandering). A timer starts at the entry's minimum respawn time, counts
/// down in frame-clock seconds and, at 0 and while fewer than <c>count</c> units exist (<c>count</c> 0: no limit), makes a unit and adds a
/// random time between the respawn minimum and maximum (0 / 0: all at once).
/// </summary>
public sealed class RespawningEffectGroup : EffectGroup
{
    const int MaxUnits = 256;
    readonly bool wandering, lightning;
    float timer;

    public RespawningEffectGroup(WeatherEffectEntry entry, PuSystemDef system, int seed, EffectWorld world) : base(entry.Effect, entry, system, seed, world)
    {
        wandering = entry.Effect.Type is EffectType.Wandering or EffectType.WanderingStorm or EffectType.WanderingGas;
        lightning = entry.Effect.Type == EffectType.PointLighting;
        timer = entry.RespawnMin;
    }

    int Count => Entry!.Count;

    void Spawn(in EffectCamera camera)
    {
        Vector3 p;
        if (lightning)
        {
            // Observed: the game snaps a random point to a metal object found near it (±2000 in x and z, 10 above the ground); the viewer has no
            // such lookup and strikes at a random place round the camera, 3000 units out at most (so a bolt is within sight).
            float a = random.NextSingle() * MathF.Tau, r = MathF.Sqrt(random.NextSingle()) * 3000;
            float x = camera.Eye.X + MathF.Cos(a) * r, z = camera.Eye.Z + MathF.Sin(a) * r;
            p = new Vector3(x, world.GroundHeight(x, z) + 10, z);
        }
        else p = world.Place(Effect, random, camera);
        var u = NewUnit(p);
        if (wandering)
        {
            // A random direction in the ground plane (x and z in [-1, 1], normalised at the first turn), turned by up to ±90° every random(time to live) / 10 seconds.
            u.Heading = new Vector2(Range(-1, 1), Range(-1, 1));
            if (u.Heading.LengthSquared() < 1e-4f) u.Heading = Vector2.UnitX;
            u.Heading = Vector2.Normalize(u.Heading);
            u.TurnTimer = Range(Effect.MinTimeToLive, Effect.MaxTimeToLive) * 0.1f;
        }
    }

    void Schedule(float dt, in EffectCamera camera)
    {
        timer -= dt;
        int guard = 0;
        while (timer <= 0 && (Count == 0 || units.Count < Count) && units.Count < MaxUnits && guard++ < MaxUnits)
        {
            Spawn(camera);
            timer += Range(Entry!.RespawnMin, Math.Max(Entry.RespawnMax, Entry.RespawnMin));
            // Count 0 with no respawn time would spawn for ever (the game's loop does not end): one per frame here.
            if (Count == 0 && Entry.RespawnMax <= 0) break;
        }
    }

    void Move(EffectUnit u, float dt, WeatherEffectInput weather)
    {
        if (!wandering) return;
        // Observed: the node walks along its heading at `wandering speed` (the decompile's arithmetic is not resolved), is carried by the wind when
        // wind affected, turns on its timer, and its height follows the ground.
        u.TurnTimer -= dt;
        if (u.TurnTimer <= 0)
        {
            float angle = Range(-0.5f, 0.5f) * MathF.PI, c = MathF.Cos(angle), s = MathF.Sin(angle);
            var h = u.Heading;
            u.Heading = Vector2.Normalize(new Vector2(h.X * c + h.Y * s, -h.X * s + h.Y * c));
            u.TurnTimer = Range(Effect.MinTimeToLive, Effect.MaxTimeToLive) * 0.1f;
        }
        var step = u.Heading * (Effect.WanderingSpeed * dt);
        if (Effect.WindAffected) step += weather.Wind * (Effect.WindSpeedMultiplier * dt);
        var p = u.Position;
        p.X += step.X;
        p.Z += step.Y;
        u.GroundTimer -= dt;
        if (u.GroundTimer <= 0)
        {
            u.GroundTimer = 0.5f;
            if (u.DistanceToCamera < world.ActiveRadius + 3 * u.Radius) p.Y = world.GroundHeight(p.X, p.Z);
        }
        u.Position = p;
    }

    public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
    {
        Schedule(dt, camera);
        var env = Environment(weather);
        foreach (var u in units)
        {
            Move(u, dt, weather);
            Tick(u, dt, camera, env);
        }
        RemoveDone();
    }

    public override void Prewarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        timer = seconds > 0 ? 0 : Entry!.RespawnMin;
        const float step = 0.5f;
        for (float t = 0; t < seconds; t += step) Update(Math.Min(step, seconds - t), camera, weather);
        Simulate();
    }
}

/// <summary>
/// GLOBAL (4): one handler per unit of <c>count</c>, all made at once (Verified (decompiled), FUN_140102040: while fewer than <c>count</c> exist
/// they are made, whatever the respawn times; <c>count</c> 0 makes none, so <c>fog islands</c> never appears in the base game). Each follows the
/// camera: its node is at the camera's x and z, the ground there + 100 limited to <c>min/max altitude</c> when either is non-zero (FUN_140101dc0);
/// its particles are kept within <c>d</c> of the node (<see cref="ParticleSimulation.WrapSphere"/>).
/// </summary>
public sealed class GlobalEffectGroup : EffectGroup
{
    readonly float size;

    public GlobalEffectGroup(WeatherEffectEntry entry, PuSystemDef system, int seed, EffectWorld world) : base(entry.Effect, entry, system, seed, world)
    {
        float s = system.LargestEmitterExtent * 0.75f;
        size = s > 0 ? s : 100;
    }

    Vector3 Node(in EffectCamera camera)
    {
        float y = world.GroundHeight(camera.Eye.X, camera.Eye.Z) + 100;
        if (Effect.MinAltitude != 0 || Effect.MaxAltitude != 0)
        {
            if (y >= Effect.MaxAltitude) y = Effect.MaxAltitude;
            if (y <= Effect.MinAltitude) y = Effect.MinAltitude;
        }
        return new Vector3(camera.Eye.X, y, camera.Eye.Z);
    }

    public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
    {
        var node = Node(camera);
        while (units.Count < Entry!.Count)
        {
            var u = NewUnit(node);
            u.Life = float.PositiveInfinity;   // Verified (decompiled), FUN_140103f50: the global handler is made immortal whatever its time to live
            u.Wrap = UnitWrap.Sphere;
            u.WrapSize = size;
            u.Radius = size;
        }
        var env = Environment(weather);
        foreach (var u in units)
        {
            u.Position = node;
            u.WrapCentre = node;
            u.WrapMaxHeight = Effect.MaxAltitude > 0 ? Effect.MaxAltitude : float.PositiveInfinity;
            Tick(u, dt, camera, env, alwaysActive: true);
        }
        RemoveDone();
    }

    public override void Prewarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        Update(0, camera, weather);
        foreach (var u in units) u.Pending += seconds;
        Simulate();
    }
}

/// <summary>
/// GLOBAL_POINT (10): points round the camera (Verified (decompiled), FUN_140103160 / FUN_1401048b0 / FUN_140104610). <c>R</c> is the effect's
/// <c>maximum view distance</c> (1000 when 0); the group keeps <c>count</c> units within R of the camera and <c>count</c> between R and 2R (3 ×
/// <c>count</c> at most), each at a sqrt-uniform random distance in its ring on the ground, and stops the ones farther than 4R. Respawn times
/// are not used. A unit lives <c>min..max time to live</c>.
/// </summary>
public sealed class GlobalPointEffectGroup : EffectGroup
{
    readonly float radius;

    public GlobalPointEffectGroup(WeatherEffectEntry entry, PuSystemDef system, int seed, EffectWorld world) : base(entry.Effect, entry, system, seed, world)
    {
        radius = entry.Effect.MaximumViewDistance > 0 ? entry.Effect.MaximumViewDistance : 1000;
    }

    void Spawn(in EffectCamera camera, float inner, float outer)
    {
        Vector3 p = default;
        var e = Effect;
        for (int tries = 10; tries > 0; tries--)
        {
            float a = random.NextSingle() * MathF.Tau, r = MathF.Sqrt(random.NextSingle()) * (outer - inner) + inner;
            p = new Vector3(camera.Eye.X + MathF.Cos(a) * r, 0, camera.Eye.Z + MathF.Sin(a) * r);
            p.Y = world.GroundHeight(p.X, p.Z);
            float ny = world.NormalY(p.X, p.Z);
            if ((e.MaxAltitude == 0 || (p.Y >= e.MinAltitude && p.Y <= e.MaxAltitude)) && ny >= e.MinSlope && ny <= e.MaxSlope) break;
        }
        NewUnit(p);
    }

    public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
    {
        var env = Environment(weather);
        int count = Entry!.Count, inner = 0, middle = 0;
        float r2 = radius * radius, far2 = 16 * r2;
        foreach (var u in units)
        {
            Tick(u, dt, camera, env, alwaysActive: true);
            // Horizontal distance (Observed choice): the unit sits on the ground and the eye may be hundreds of units up, which a 50-unit ring would never reach.
            float dx = u.Position.X - camera.Eye.X, dz = u.Position.Z - camera.Eye.Z, d2 = dx * dx + dz * dz;
            if (d2 >= far2) u.Stopped = true;
            else if (d2 <= r2) inner++;
            else if (d2 <= 4 * r2) middle++;
        }
        int need = count * 3 - units.Count;
        if (need > 0)
        {
            int a = Math.Max(Math.Min(need, count - inner), 0);
            int b = Math.Max(Math.Min(need - a, count - middle), 0);
            for (int i = 0; i < a; i++) Spawn(camera, 0, radius);
            for (int i = 0; i < b; i++) Spawn(camera, radius, 2 * radius);
            foreach (var u in units.Where(u => u.Age == 0)) Tick(u, 0, camera, env, alwaysActive: true);
        }
        RemoveDone();
    }

    public override void Prewarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        const float step = 0.5f;
        for (float t = 0; t < seconds; t += step) Update(Math.Min(step, seconds - t), camera, weather);
        Simulate();
    }
}

/// <summary>
/// The map-feature placers (docs/formats/weather.md "Effect placers on the map"): effects that belong to a hidden MAP_FEATURES record and are
/// always on, at fixed places. Not a weather group: they are made once from <c>features.dat</c> (<see cref="MapEffectPlacers"/>) and live as
/// static POINT effects (Observed: the game's handler lives <c>min..max time to live</c> and is made again; the viewer keeps it).
/// A unit is simulated while the camera is within <see cref="EffectWorld.ActiveRadius"/> plus the effect's <c>maximum view distance</c> of it.
/// </summary>
public sealed class PlacerEffectGroup : EffectGroup
{
    public PlacerEffectGroup(EffectRecord effect, PuSystemDef system, IEnumerable<Vector3> positions, int seed, EffectWorld world) : base(effect, null, system, seed, world)
    {
        foreach (var p in positions)
        {
            var u = NewUnit(p);
            u.Life = float.PositiveInfinity;
        }
    }

    public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
    {
        var env = Environment(weather);
        foreach (var u in units) Tick(u, dt, camera, env);
    }

    public override void Prewarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        Update(0, camera, weather);
        foreach (var u in units.Where(u => u.Active)) u.Pending += seconds;
        Simulate();
    }
}

public static class EffectGroups
{
    /// <summary>The group for an entry, or null when its particle system is unknown.</summary>
    public static EffectGroup? Create(WeatherEffectEntry entry, ParticleLibrary library, int seed, EffectWorld? world = null)
    {
        if (library.FindSystem(entry.Effect.ParticleSystem) is not { } system) return null;
        world ??= new EffectWorld();
        return entry.Effect.Type switch
        {
            EffectType.Camera or EffectType.CameraRain or EffectType.CameraAcidRain => new CameraEffectGroup(entry, system, seed, world),
            EffectType.Point or EffectType.PointLighting or EffectType.Wandering or EffectType.WanderingStorm or EffectType.WanderingGas => new RespawningEffectGroup(entry, system, seed, world),
            EffectType.Global => new GlobalEffectGroup(entry, system, seed, world),
            EffectType.GlobalPoint => new GlobalPointEffectGroup(entry, system, seed, world),
            _ => null,
        };
    }
}
