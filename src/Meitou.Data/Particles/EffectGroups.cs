using System.Numerics;

namespace Meitou.Data.Particles;

/// <summary>
/// The camera as the groups see it each frame: where the eye is and which way it looks.
/// </summary>
public readonly record struct EffectCamera(Vector3 Eye, Vector3 Forward);

/// <summary>
/// One entry of the weather's effect list at work (docs/formats/weather.md "Spawning"): the group class is keyed by the EFFECT <c>type</c>;
/// <see cref="EffectGroups.Create"/> makes the right one. A group owns its <see cref="Units"/>. Two phases per frame: <see cref="Update"/> on the
/// main thread (schedules, places and moves the units, queues the time) and <see cref="Simulate"/> (the particles; thread-safe per unit).
/// </summary>
public abstract class EffectGroup
{
    /// <summary>
    /// The group's live units; the derived group makes and moves them, <see cref="RemoveDone"/> drops the finished ones.
    /// </summary>
    protected readonly List<EffectUnit> _units = [];
    /// <summary>
    /// The group's own random source, seeded from its seed: positions, lives, timers.
    /// </summary>
    protected readonly Random _random;
    /// <summary>
    /// The terrain, area and density the group places its units in.
    /// </summary>
    protected EffectWorld _world;
    /// <summary>
    /// The group's seed; each unit's simulation seed is made from it and the unit's number.
    /// </summary>
    readonly int _seed;
    /// <summary>
    /// How many units the group has made, numbering the next one.
    /// </summary>
    int _made;

    /// <summary>
    /// A group for <paramref name="effect"/> (and its weather <paramref name="entry"/>, null for a placer) running <paramref name="system"/> in
    /// <paramref name="world"/>.
    /// </summary>
    protected EffectGroup(EffectRecord effect, WeatherEffectEntry? entry, PuSystemDef system, int seed, EffectWorld world)
    {
        Effect = effect;
        Entry = entry;
        System = system;
        this._seed = seed;
        this._world = world;
        _random = new Random(seed * 7919 + 17);
        Tint = TintOf(effect);
    }

    /// <summary>
    /// Hands the group to another world (the live one, after a worker warmed it with a <see cref="EffectWorld.Frozen"/> copy); while nobody else
    /// touches the group.
    /// </summary>
    internal void Rebind(EffectWorld to) => _world = to;

    /// <summary>
    /// The EFFECT record the group runs.
    /// </summary>
    public EffectRecord Effect { get; }
    /// <summary>
    /// The weather's entry the group belongs to; null for a map-feature placer.
    /// </summary>
    public WeatherEffectEntry? Entry { get; }
    /// <summary>
    /// The particle system each unit simulates.
    /// </summary>
    public PuSystemDef System { get; }
    /// <summary>
    /// The group's live units.
    /// </summary>
    public IReadOnlyList<EffectUnit> Units => _units;
    /// <summary>
    /// The EFFECT's <c>colour multiplier</c> applied to the particles' rgb (Observed: "for weather effects only"; a black multiplier, which would
    /// blank the particles, is ignored: <c>Sand-Stream any altitude</c> has one next to <c>ground colour</c>).
    /// </summary>
    public Vector3 Tint { get; }
    /// <summary>
    /// How many particles all units' simulations hold now.
    /// </summary>
    public int ParticleCount => _units.Sum(u => u.Simulation?.ParticleCount ?? 0);

    /// <summary>
    /// The EFFECT's colour multiplier, or white when it is black.
    /// </summary>
    static Vector3 TintOf(EffectRecord e) => e.ColourMultiplier.LengthSquared() < 1e-4f ? Vector3.One : e.ColourMultiplier;

    /// <summary>
    /// Steps the group by <paramref name="dt"/> seconds of the frame clock (0: not at all, a held-still frame): the main-thread part.
    /// </summary>
    public abstract void Update(float dt, in EffectCamera camera, WeatherEffectInput weather);

    /// <summary>
    /// Starts the group as if it had been running for <paramref name="seconds"/> (the viewer's start-up choice, Observed; the game has only
    /// <c>fast_forward</c>): the schedule runs in steps, the first unit of every respawning group exists at once, and the simulations are run.
    /// </summary>
    public void Prewarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        BeginWarm(seconds, camera, weather);
        Simulate();
    }

    /// <summary>
    /// The first half of <see cref="Prewarm"/>, which is cheap: the schedule runs in steps (places the units, which asks the world for the ground
    /// height: on a worker thread the group must have a world whose functions any thread may call, <see cref="EffectWorld.Frozen"/>) and every unit
    /// that is near enough is woken with the time it has to run queued in <see cref="EffectUnit.Pending"/>. Nothing is simulated yet:
    /// <see cref="Simulate"/> or <see cref="SimulateSerial"/> does that, on any thread, while nothing else touches the group.
    /// </summary>
    public abstract void BeginWarm(float seconds, in EffectCamera camera, WeatherEffectInput weather);

    /// <summary>
    /// Runs every unit's queued time (the units are independent: in parallel when there are several).
    /// </summary>
    public void Simulate()
    {
        while (true)
        {
            var active = _units.Where(u => u.Active && u.Pending > 0).ToArray();
            if (active.Length == 0) return;
            if (active.Length > 1) Parallel.ForEach(active, u => u.Advance());
            else foreach (var u in active) u.Advance();   // a backlog is run in full here (start-up), unlike the per-frame path
        }
    }

    /// <summary>
    /// <see cref="Simulate"/> on the calling thread alone, one unit after the other, checking <paramref name="cancel"/> between the steps: for a
    /// group that is being made in the background, where the thread pool is not to be taken from the frame's own work. Returns false when it was
    /// cancelled.
    /// </summary>
    public bool SimulateSerial(CancellationToken cancel)
    {
        foreach (var u in _units)
            while (u.Active && u.Pending > 0)
            {
                if (cancel.IsCancellationRequested) return false;
                u.Advance();
            }
        return true;
    }

    /// <summary>
    /// The emission multiplier from the wind: <c>min wind span rate</c> is the wind speed needed for any particles, <c>max wind span rate</c> for all
    /// of them.
    /// </summary>
    public static float WindSpanScale(EffectRecord effect, float windSpeed)
    {
        float lo = effect.MinWindSpan, hi = effect.MaxWindSpan;
        if (hi > lo) return Math.Clamp((windSpeed - lo) / (hi - lo), 0, 1);
        return lo > 0 && windSpeed < lo ? 0 : 1;
    }

    /// <summary>
    /// What the units' simulations read this frame: the wind, the effect's wind settings and the emission scale (the wind span times the
    /// world's density).
    /// </summary>
    protected ParticleEnvironment Environment(WeatherEffectInput weather)
    {
        var e = Effect;
        float windSpan = WindSpanScale(e, weather.Wind.Length()) * _world.Density;
        return new ParticleEnvironment(weather.Wind, e.WindSpeedMultiplier, windSpan, e.WindAffected, e.WindDirectionEmission);
    }

    /// <summary>
    /// Makes and adds a unit at <paramref name="position"/>: its seed, tint (times the ground colour when the effect asks), bounding radius, and a
    /// life between the effect's times to live unless it is immortal.
    /// </summary>
    protected EffectUnit NewUnit(Vector3 position)
    {
        var u = new EffectUnit(Effect, System, _seed * 31 + _made++)
        {
            Position = position,
            Anchor = position,
            Tint = Tint,
            Radius = System.BoundingRadius,
        };
        if (Effect.GroundColour && _world.GroundColour is { } ground) u.Tint = Tint * ground(position.X, position.Z);
        // The life: random between min and max time to live, none when the maximum is 0 (Verified (decompiled), FUN_1401034f0).
        if (!Effect.Immortal)
            u.Life = Effect.MinTimeToLive + (Effect.MaxTimeToLive - Effect.MinTimeToLive) * _random.NextSingle();
        _units.Add(u);
        return u;
    }

    /// <summary>
    /// A random value between <paramref name="a"/> and <paramref name="b"/>.
    /// </summary>
    protected float Range(float a, float b) => a + (b - a) * _random.NextSingle();

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
        float range = _world.ActiveRadius + Effect.MaximumViewDistance;
        float reach = float.IsPositiveInfinity(u.Radius) ? 0 : u.Radius;
        bool near = alwaysActive || u.DistanceToCamera - reach <= range;
        bool far = !alwaysActive && u.DistanceToCamera - reach > range * 1.15f;
        u.RangeFade = alwaysActive ? 1 : Math.Clamp((range - (u.DistanceToCamera - reach)) / (0.15f * range), 0, 1);
        if (!u.Active && near && !u.Done)
        {
            u.Activate();
            u.Pending = Math.Min(Math.Min(u.Age, u.LongestLife), 30);
        }
        else if (u.Active && far) u.Deactivate();
        if (u.Active) u.Pending += dt;
    }

    /// <summary>
    /// Removes the units that have finished.
    /// </summary>
    protected void RemoveDone()
    {
        for (int i = _units.Count - 1; i >= 0; i--)
            if (_units[i].Done) _units.RemoveAt(i);
    }
}

/// <summary>
/// CAMERA, CAMERA_RAIN and CAMERA_ACID_RAIN (docs/formats/weather.md "Spawning", Verified (decompiled): the camera group's functions): the system's
/// node sits at the camera's position and moves with it; its particles live in world space and a wrapping rule keeps them in a cube of edge <c>2d /
/// 1.5</c> centred <c>d</c> ahead of the camera along its view direction, where <c>d</c> is the effect's size, the EFFECT loader's 0.75 × the largest
/// Box extent (or Circle radius) times the system's scale (so the cube's edge is that extent: 120 for <c>Kenshi_Heavy_Rain</c>, which is its
/// emitter's box). Particles are emitted round the camera and wrapped into the cube in front of it. There is exactly one handler whatever the entry's
/// count (Verified (decompiled), FUN_140102ff0 sets the group's count to 1).
/// </summary>
public sealed class CameraEffectGroup : EffectGroup
{
    /// <summary>
    /// The group's one unit.
    /// </summary>
    readonly EffectUnit _unit;
    /// <summary>
    /// <c>d</c>: the effect's size, 0.75 times the largest emitter extent (100 when that is 0).
    /// </summary>
    readonly float _distance;
    /// <summary>
    /// Whether the unit has been placed at the camera and activated.
    /// </summary>
    bool _started;

    /// <summary>
    /// The group of <paramref name="entry"/>: one unit that wraps its particles in the cube ahead of the camera.
    /// </summary>
    public CameraEffectGroup(WeatherEffectEntry entry, PuSystemDef system, int seed, EffectWorld? world = null)
        : base(entry.Effect, entry, system, seed, world ?? new EffectWorld())
    {
        float size = system.LargestEmitterExtent * 0.75f;
        _distance = size > 0 ? size : 100;
        _unit = NewUnit(Vector3.Zero);
        _unit.Radius = float.PositiveInfinity;
        _unit.Wrap = UnitWrap.Cube;
        _unit.WrapSize = Edge;
    }

    /// <summary>
    /// The cube's centre's distance ahead of the camera, <c>d</c>.
    /// </summary>
    public float Distance => _distance;
    /// <summary>
    /// The cube's edge, <c>2d / 1.5</c>.
    /// </summary>
    public float Edge => _distance * 2 / 1.5f;
    /// <summary>
    /// The unit's simulation.
    /// </summary>
    public ParticleSimulation Simulation => _unit.Simulation!;
    /// <summary>
    /// Where the simulation's own coordinate origin is in the world: add it to <see cref="ParticleSimulation.Collect"/>'s positions.
    /// </summary>
    public Vector3 Anchor => _unit.Anchor;

    /// <summary>
    /// The camera's view direction, normalised; -Z when it has none.
    /// </summary>
    static Vector3 Forward(in EffectCamera camera) => camera.Forward.LengthSquared() > 1e-6f ? Vector3.Normalize(camera.Forward) : -Vector3.UnitZ;

    /// <summary>
    /// The cube's centre in the world.
    /// </summary>
    public Vector3 Centre(in EffectCamera camera) => camera.Eye + Forward(camera) * _distance;

    /// <summary>
    /// Puts the unit at the camera and the wrap cube ahead of it; the first time also activates the unit and anchors its coordinates there.
    /// </summary>
    void Follow(in EffectCamera camera)
    {
        _unit.Position = camera.Eye;
        _unit.WrapCentre = Centre(camera);
        if (!_started)
        {
            _started = true;
            _unit.Activate();
            _unit.Anchor = camera.Eye;
        }
    }

    public override void BeginWarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        Follow(camera);
        _unit.Environment = Environment(weather);
        _unit.Pending += seconds;
    }

    public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
    {
        bool first = !_started;
        Follow(camera);
        _unit.Environment = Environment(weather);
        if (first && Simulation.Definition.FastForward is { } ff && ff.Time > 0) _unit.Pending += ff.Time;
        _unit.Pending += dt;
        _unit.Age += dt;
    }
}

/// <summary>
/// POINT (2), POINT_LIGHTING (7) and WANDERING (3, 8, 9) groups (docs/formats/weather.md "Spawning"; Verified (decompiled): FUN_140102370 the
/// schedule, FUN_1401039a0 / FUN_140103ca0 the spawns, FUN_140102b50 the wandering). A timer starts at the entry's minimum respawn time, counts down
/// in frame-clock seconds and, at 0 and while fewer than <c>count</c> units exist (<c>count</c> 0: no limit), makes a unit and adds a random time
/// between the respawn minimum and maximum (0 / 0: all at once).
/// </summary>
public sealed class RespawningEffectGroup : EffectGroup
{
    /// <summary>
    /// The most units a group keeps, and the most made in one frame.
    /// </summary>
    const int MaxUnits = 256;
    /// <summary>
    /// Whether the units wander (WANDERING types); whether they are lightning strikes (POINT_LIGHTING).
    /// </summary>
    readonly bool _wandering, _lightning;
    /// <summary>
    /// Seconds until the next unit is made.
    /// </summary>
    float _timer;

    /// <summary>
    /// The group of <paramref name="entry"/>, its timer at the entry's minimum respawn time.
    /// </summary>
    public RespawningEffectGroup(WeatherEffectEntry entry, PuSystemDef system, int seed, EffectWorld world)
        : base(entry.Effect, entry, system, seed, world)
    {
        _wandering = entry.Effect.Type is EffectType.Wandering or EffectType.WanderingStorm or EffectType.WanderingGas;
        _lightning = entry.Effect.Type == EffectType.PointLighting;
        _timer = entry.RespawnMin;
    }

    /// <summary>
    /// The entry's <c>count</c>: the most units at once; 0 no limit.
    /// </summary>
    int Count => Entry!.Count;

    /// <summary>
    /// Makes a unit: a lightning strike at a random place within 3000 of the camera, else where the world places the effect; a wandering one gets a
    /// random heading.
    /// </summary>
    void Spawn(in EffectCamera camera)
    {
        Vector3 p;
        if (_lightning)
        {
            // Observed: the game snaps a random point to a metal object found near it (±2000 in x and z, 10 above the ground); the viewer has no
            // such lookup and strikes at a random place round the camera, 3000 units out at most (so a bolt is within sight).
            float a = _random.NextSingle() * MathF.Tau, r = MathF.Sqrt(_random.NextSingle()) * 3000;
            float x = camera.Eye.X + MathF.Cos(a) * r, z = camera.Eye.Z + MathF.Sin(a) * r;
            p = new Vector3(x, _world.GroundHeight(x, z) + 10, z);
        }
        else p = _world.Place(Effect, _random, camera);
        var u = NewUnit(p);
        if (_wandering)
        {
            // A random direction in the ground plane (x and z in [-1, 1], normalised at the first turn),
            // turned by up to ±90° every random(time to live) / 10 seconds.
            u.Heading = new Vector2(Range(-1, 1), Range(-1, 1));
            if (u.Heading.LengthSquared() < 1e-4f) u.Heading = Vector2.UnitX;
            u.Heading = Vector2.Normalize(u.Heading);
            u.TurnTimer = Range(Effect.MinTimeToLive, Effect.MaxTimeToLive) * 0.1f;
        }
    }

    /// <summary>
    /// Counts the timer down by <paramref name="dt"/> and makes units while it is due and the count allows, adding a random respawn time after each.
    /// </summary>
    void Schedule(float dt, in EffectCamera camera)
    {
        _timer -= dt;
        int guard = 0;
        while (_timer <= 0 && (Count == 0 || _units.Count < Count) && _units.Count < MaxUnits && guard++ < MaxUnits)
        {
            Spawn(camera);
            _timer += Range(Entry!.RespawnMin, Math.Max(Entry.RespawnMax, Entry.RespawnMin));
            // Count 0 with no respawn time would spawn for ever (the game's loop does not end): one per frame here.
            if (Count == 0 && Entry.RespawnMax <= 0) break;
        }
    }

    /// <summary>
    /// Moves a wandering unit: along its heading at the wandering speed, plus the wind when wind affected, turning on its timer, its height on the
    /// ground every half second near the camera.
    /// </summary>
    void Move(EffectUnit u, float dt, WeatherEffectInput weather)
    {
        if (!_wandering) return;
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
            if (u.DistanceToCamera < _world.ActiveRadius + 3 * u.Radius) p.Y = _world.GroundHeight(p.X, p.Z);
        }
        u.Position = p;
    }

    public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
    {
        Schedule(dt, camera);
        var env = Environment(weather);
        foreach (var u in _units)
        {
            Move(u, dt, weather);
            Tick(u, dt, camera, env);
        }
        RemoveDone();
    }

    public override void BeginWarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        _timer = seconds > 0 ? 0 : Entry!.RespawnMin;
        const float step = 0.5f;
        for (float t = 0; t < seconds; t += step) Update(Math.Min(step, seconds - t), camera, weather);
    }
}

/// <summary>
/// GLOBAL (4): one handler per unit of <c>count</c>, all made at once (Verified (decompiled), FUN_140102040: while fewer than <c>count</c> exist they
/// are made, whatever the respawn times; <c>count</c> 0 makes none, so <c>fog islands</c> never appears in the base game). Each follows the camera:
/// its node is at the camera's x and z, the ground there + 100 limited to <c>min/max altitude</c> when either is non-zero (FUN_140101dc0); its
/// particles are kept within <c>d</c> of the node, moved ahead of a moving camera first (<see cref="ParticleSimulation.WrapSphere"/>).
/// </summary>
public sealed class GlobalEffectGroup : EffectGroup
{
    /// <summary>
    /// <c>d</c>: the effect's size, 0.75 times the largest emitter extent (100 when that is 0); the units' wrap radius.
    /// </summary>
    readonly float _size;
    /// <summary>
    /// The camera's position last frame, for its direction of movement; null before the first.
    /// </summary>
    Vector3? _lastEye;

    /// <summary>
    /// The group of <paramref name="entry"/>.
    /// </summary>
    public GlobalEffectGroup(WeatherEffectEntry entry, PuSystemDef system, int seed, EffectWorld world)
        : base(entry.Effect, entry, system, seed, world)
    {
        float s = system.LargestEmitterExtent * 0.75f;
        _size = s > 0 ? s : 100;
    }

    /// <summary>
    /// Where the units sit: at the camera's x and z, the ground there + 100, limited to the effect's altitudes when either is set.
    /// </summary>
    Vector3 Node(in EffectCamera camera)
    {
        float y = _world.GroundHeight(camera.Eye.X, camera.Eye.Z) + 100;
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
        while (_units.Count < Entry!.Count)
        {
            var u = NewUnit(node);
            u.Life = float.PositiveInfinity;   // Verified (decompiled), FUN_140103f50: the global handler is made immortal whatever its time to live
            u.Wrap = UnitWrap.Sphere;
            u.WrapSize = _size;
            u.Radius = _size;
        }
        // The camera's direction of movement since the last frame (the game's FUN_1401014f0), for the wrap.
        var moved = _lastEye is { } last ? camera.Eye - last : Vector3.Zero;
        _lastEye = camera.Eye;
        var ahead = moved.LengthSquared() > 1e-8f ? Vector3.Normalize(moved) : Vector3.Zero;
        var env = Environment(weather);
        foreach (var u in _units)
        {
            u.Position = node;
            u.WrapCentre = node;
            u.WrapAhead = ahead;
            u.WrapMaxHeight = Effect.MaxAltitude > 0 ? Effect.MaxAltitude : float.PositiveInfinity;
            Tick(u, dt, camera, env, alwaysActive: true);
        }
        RemoveDone();
    }

    public override void BeginWarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        Update(0, camera, weather);
        foreach (var u in _units) u.Pending += seconds;
    }
}

/// <summary>
/// GLOBAL_POINT (10): points round the camera (Verified (decompiled), FUN_140103160 / FUN_1401048b0 / FUN_140104610). <c>R</c> is the effect's
/// <c>maximum view distance</c> (1000 when 0); the group keeps <c>count</c> units within R of the camera and <c>count</c> between R and 2R (3 ×
/// <c>count</c> at most), each at a sqrt-uniform random distance in its ring on the ground, and stops the ones farther than 4R. Respawn times are not
/// used. A unit lives <c>min..max time to live</c>.
/// </summary>
public sealed class GlobalPointEffectGroup : EffectGroup
{
    /// <summary>
    /// <c>R</c>: the effect's maximum view distance, 1000 when 0.
    /// </summary>
    readonly float _radius;

    /// <summary>
    /// The group of <paramref name="entry"/>.
    /// </summary>
    public GlobalPointEffectGroup(WeatherEffectEntry entry, PuSystemDef system, int seed, EffectWorld world)
        : base(entry.Effect, entry, system, seed, world)
    {
        _radius = entry.Effect.MaximumViewDistance > 0 ? entry.Effect.MaximumViewDistance : 1000;
    }

    /// <summary>
    /// Makes a unit on the ground at a random distance from the camera between <paramref name="inner"/> and <paramref name="outer"/>, trying up to 10
    /// places for one within the effect's altitudes and slopes.
    /// </summary>
    void Spawn(in EffectCamera camera, float inner, float outer)
    {
        Vector3 p = default;
        var e = Effect;
        for (int tries = 10; tries > 0; tries--)
        {
            float a = _random.NextSingle() * MathF.Tau, r = MathF.Sqrt(_random.NextSingle()) * (outer - inner) + inner;
            p = new Vector3(camera.Eye.X + MathF.Cos(a) * r, 0, camera.Eye.Z + MathF.Sin(a) * r);
            p.Y = _world.GroundHeight(p.X, p.Z);
            float ny = _world.NormalY(p.X, p.Z);
            if ((e.MaxAltitude == 0 || (p.Y >= e.MinAltitude && p.Y <= e.MaxAltitude)) && ny >= e.MinSlope && ny <= e.MaxSlope)
                break;
        }
        NewUnit(p);
    }

    public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
    {
        var env = Environment(weather);
        int count = Entry!.Count, inner = 0, middle = 0;
        float r2 = _radius * _radius, far2 = 16 * r2;
        foreach (var u in _units)
        {
            Tick(u, dt, camera, env, alwaysActive: true);
            // Horizontal distance (Observed choice): the unit sits on the ground and the eye may be hundreds of units up, which a 50-unit ring would
            // never reach.
            float dx = u.Position.X - camera.Eye.X, dz = u.Position.Z - camera.Eye.Z, d2 = dx * dx + dz * dz;
            if (d2 >= far2) u.Stopped = true;
            else if (d2 <= r2) inner++;
            else if (d2 <= 4 * r2) middle++;
        }
        int need = count * 3 - _units.Count;
        if (need > 0)
        {
            int a = Math.Max(Math.Min(need, count - inner), 0);
            int b = Math.Max(Math.Min(need - a, count - middle), 0);
            for (int i = 0; i < a; i++) Spawn(camera, 0, _radius);
            for (int i = 0; i < b; i++) Spawn(camera, _radius, 2 * _radius);
            foreach (var u in _units.Where(u => u.Age == 0)) Tick(u, 0, camera, env, alwaysActive: true);
        }
        RemoveDone();
    }

    public override void BeginWarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        const float step = 0.5f;
        for (float t = 0; t < seconds; t += step) Update(Math.Min(step, seconds - t), camera, weather);
    }
}

/// <summary>
/// The map-feature placers (docs/formats/zones.md): effects that belong to a MAP_FEATURES record (hidden or not) and are always on, at fixed places.
/// Not a weather group: they are made once from <c>features.dat</c> (<see cref="MapEffectPlacers"/>) and live as static POINT effects (Observed: the
/// game's handler lives <c>min..max time to live</c> and is made again; the viewer keeps it). A unit is simulated while the camera is within
/// <see cref="EffectWorld.ActiveRadius"/> plus the effect's <c>maximum view distance</c> of it.
/// </summary>
public sealed class PlacerEffectGroup : EffectGroup
{
    /// <summary>
    /// A unit at every one of <paramref name="positions"/>, unturned.
    /// </summary>
    public PlacerEffectGroup(EffectRecord effect, PuSystemDef system, IEnumerable<Vector3> positions, int seed, EffectWorld world)
        : this(effect, system, positions.Select(p => (p, Quaternion.Identity)), seed, world) { }

    /// <summary>
    /// A unit at every position, turned by its orientation (the effects on buildings).
    /// </summary>
    public PlacerEffectGroup(
        EffectRecord effect, PuSystemDef system, IEnumerable<(Vector3 Position, Quaternion Orientation)> placements, int seed, EffectWorld world)
        : base(effect, null, system, seed, world)
    {
        foreach (var (p, orientation) in placements)
        {
            var u = NewUnit(p);
            u.Orientation = orientation;
            u.Life = float.PositiveInfinity;
        }
    }

    public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
    {
        var env = Environment(weather);
        foreach (var u in _units) Tick(u, dt, camera, env);
    }

    public override void BeginWarm(float seconds, in EffectCamera camera, WeatherEffectInput weather)
    {
        Update(0, camera, weather);
        foreach (var u in _units.Where(u => u.Active)) u.Pending += seconds;
    }
}

/// <summary>
/// One-shot effects the host fires (luckyren Mode's blood, docs/game/luckyren.md): each <see cref="Spawn"/> is a unit that emits for its life, then
/// lives out its particles and is dropped. Not an EFFECT record of the game: the record is made up from the system's name.
/// </summary>
public sealed class BurstEffectGroup(PuSystemDef system, int seed, EffectWorld world)
    : EffectGroup(
        new EffectRecord(system.Name, EffectType.Point, system.Name, false, false, 1, 0, 0, Vector3.One, 1, false, 0, 0, 0, 0, 0),
        null, system, seed, world)
{
    /// <summary>
    /// A unit at <paramref name="position"/>, turned by <paramref name="orientation"/>, emitting for <paramref name="seconds"/>, its particles
    /// <paramref name="size"/> times as big and <paramref name="lifeScale"/> times as long-lived.
    /// </summary>
    /// <param name="recolour">A colour the particles take in place of their texture's hue: kept as a negative tint, which the renderer's fragment
    /// program reads as that.</param>
    /// <returns>The unit, which its owner may move while it emits (its particles stay in the world where they were thrown).</returns>
    public EffectUnit Spawn(Vector3 position, Quaternion orientation, float seconds, float size, float lifeScale, Vector3? recolour = null)
    {
        var u = NewUnit(position);
        if (recolour is { } colour) u.Tint = -Vector3.Max(colour, new Vector3(1e-4f));
        u.Orientation = orientation;
        u.Life = seconds;
        u.Size = size;
        u.LifeScale = lifeScale;
        u.Radius *= size * lifeScale;
        return u;
    }

    public override void Update(float dt, in EffectCamera camera, WeatherEffectInput weather)
    {
        foreach (var u in _units) Tick(u, dt, camera, ParticleEnvironment.None);
        RemoveDone();
    }

    public override void BeginWarm(float seconds, in EffectCamera camera, WeatherEffectInput weather) { }
}

/// <summary>
/// Makes the weather group of an effect entry by its EFFECT type.
/// </summary>
public static class EffectGroups
{
    /// <summary>
    /// A small particle system for <see cref="PrimeJit"/>.
    /// </summary>
    const string PrimeScript = """
        system prime
        {
            technique t
            {
                visual_particle_quota 50
                emitter Box
                {
                    emission_rate 100
                    time_to_live 1
                    velocity 5
                    direction 0 -1 0
                    box_width 10
                    box_height 10
                }
                affector Colour
                {
                    time_colour 0 1 1 1 1
                    time_colour 1 0 0 0 0
                }
            }
        }
        """;

    /// <summary>
    /// Makes, warms and updates one small group of every kind once, so the methods are compiled before a weather change needs them: the viewer runs
    /// without tiered compilation, and the first group of a kind made in the middle of a flight took 2 to 4 ms of compiling on the render thread
    /// (Observed, 2026-10-10). Meant for a worker at start-up; harmless anywhere.
    /// </summary>
    public static void PrimeJit()
    {
        try
        {
            var library = new ParticleLibrary();
            library.AddScript(PrimeScript, "prime.pu");
            var world = new EffectWorld { GroundHeight = static (_, _) => 0, Area = new DiscArea(0, 0, 100) };
            var camera = new EffectCamera(new Vector3(0, 100, 0), Vector3.UnitZ);
            var input = new WeatherEffectInput();
            ReadOnlySpan<EffectType> types =
            [EffectType.CameraRain, EffectType.Point, EffectType.Wandering, EffectType.PointLighting, EffectType.Global, EffectType.GlobalPoint,];
            foreach (var type in types)
            {
                var record = new EffectRecord("prime", type, "prime", true, false, 1, 0, 0, Vector3.One, 1, false, 100, 0, 0, 5, 10)
                {
                    MinAltitude = 0,
                    MaxAltitude = 0,
                };
                if (Create(new WeatherEffectEntry(record, 1, 0, 0), library, 1, world) is not { } group) continue;
                group.BeginWarm(1, camera, input);
                group.SimulateSerial(CancellationToken.None);
                group.Update(1f / 60, camera, input);
                foreach (var unit in group.Units.Where(u => u.Active)) unit.Advance();
            }
        }
        catch (Exception) { }   // only a head start
    }

    /// <summary>
    /// The group for an entry, or null when its particle system is unknown.
    /// </summary>
    public static EffectGroup? Create(WeatherEffectEntry entry, ParticleLibrary library, int seed, EffectWorld? world = null)
    {
        if (library.FindSystem(entry.Effect.ParticleSystem) is not { } system) return null;
        world ??= new EffectWorld();
        return entry.Effect.Type switch
        {
            EffectType.Camera or EffectType.CameraRain or EffectType.CameraAcidRain => new CameraEffectGroup(entry, system, seed, world),
            EffectType.Point or EffectType.PointLighting or EffectType.Wandering or EffectType.WanderingStorm or EffectType.WanderingGas
                => new RespawningEffectGroup(entry, system, seed, world),
            EffectType.Global => new GlobalEffectGroup(entry, system, seed, world),
            EffectType.GlobalPoint => new GlobalPointEffectGroup(entry, system, seed, world),
            _ => null,
        };
    }
}