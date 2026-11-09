using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Game.Host;
using Meitou.Game.Host.WorldSync;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Objects;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Game.Effects;

/// <summary>
/// The pieces that fly and lie on the ground, display only: luckyren Mode's burst, falling severed limbs, and the bolts with their tracers.
/// </summary>
sealed class Debris(GameHost host)
{
    // luckyren Mode's burst (docs/game/luckyren.md): an BloodMarkKind.Explosion throws the game's bone meshes and the race's severed limbs out of
    // the body, which fly, tumble and bounce on the ground and then lie there as long as a corpse would, and it splashes blood all round (with the
    // <c>Blood</c> setting on). Display only: the pieces move in game time (still while paused) and are not saved.
    /// <summary>
    /// The nest bones' meshes (<c>data/items/nests</c>; only <c>bone1</c> has an item record, whose material all of them take).
    /// </summary>
    static readonly string[] BoneMeshes = [.. Enumerable.Range(1, 14).Select(i => $"bone{i}.mesh")];

    /// <summary>
    /// Raw meat (rebirth.mod's item), thrown with the bones of a race that bleeds.
    /// </summary>
    const string MeatMesh = "Item_Meat_Raw.mesh";
    /// <summary>
    /// The blood particle systems: the splash at the burst and where flesh lands, and the spray (along its +Z) thrown out of the body.
    /// </summary>
    internal const string BurstSplash = "Bloodsplash01", BurstSpray = "HackBlood-system";

    /// <summary>
    /// The burst's mist: its sprites twice the systems' size and living 3 times as long (they keep growing, so they end about 6 times as big).
    /// </summary>
    const float MistSize = 2, MistLife = 3;

    const int BonesPerBurst = 12, MeatPerBurst = 4, SpraysPerBurst = 5, MaxPieces = 400, BurstSplashes = 24;
    const float Gravity = 98, BurstHeight = 10, Bounce = 0.35f, Grip = 0.6f, BurstReach = 60, BoneScale = 0.12f;

    internal sealed class Piece
    {
        public required string Mesh;
        public Vector3 Position, Velocity, Spin;
        public Quaternion Rotation;
        public Vector3 Extent;
        public float Scale;
        public float Floor;
        public bool Resting, Landed, Flesh;
        public long Born;
        public uint Colour;
        /// <summary>
        /// The splash particles' colour for a race whose blood is not red (<see cref="BloodDecals.BloodRecolour"/>).
        /// </summary>
        public Vector3? Recolour;
    }

    internal readonly List<Piece> Pieces = [];
    readonly Dictionary<string, string[]> _limbMeshes = new(StringComparer.Ordinal);

    /// <summary>
    /// The fall of a severed limb (<see cref="GroundItemSync.StartFall"/>; docs/formats/collision.md "Item physics": the original's item is a dynamic
    /// PhysX body made at the stump's bone with no speed of its own, in the scene's gravity 98.1, tipping to rest on the ground). Its mesh's origin
    /// falls from <see cref="Start"/> (the stump, once sampled) to <see cref="Rest"/>'s height, drifting at <see cref="Drift"/> (units/s), then turns
    /// from <see cref="Hanging"/> to <see cref="Lying"/> in <see cref="FallTipTime"/>, and the item is shown at rest.
    /// </summary>
    internal sealed class FallingLimb
    {
        public required GroundItemSnapshot Ground;
        public required string Mesh;
        public LimbLook? Look;
        public required FallSource Stump;
        public required Vector3 Rest;
        public required float RestY;
        public required Quaternion Hanging, Lying;
        public Vector3? Start;
        public float Flight;
        public Vector2 Drift;
    }

    internal readonly List<FallingLimb> Falls = [];
    internal const float FallGravity = 98.1f, FallTipTime = 0.25f, FallUnsampledSeconds = 2;

    /// <summary>
    /// A burst: blood all round and the pieces thrown.
    /// </summary>
    internal void Burst(in BloodMark m, World world)
    {
        bool bleeds = m.Amount > 0;
        var blow = m.Direction.LengthSquared() > 1e-6f ? Vector2.Normalize(m.Direction) : Vector2.Zero;
        if (host.BloodDecals.DecalStore is { } store && bleeds
                && m.Position.Y <= host.Gpu.Terrain.HeightAt(m.Position.X, m.Position.Z) + BloodDecals.BloodAboveGround)
        {
            uint opaque = 0xFF000000 | BloodDecals.Abgr(m.Colour);
            store.Add(TerrainDecalType.Pool, m.Position, RandomDirection(), 1.5f, opaque, TerrainDecalStore.NewLife, host.BloodDecals.BloodRandom);
            for (int i = 0; i < BurstSplashes; i++)
            {
                var d = Vector2.Normalize(RandomDirection() + blow * 0.5f);
                float far = MathF.Sqrt(host.BloodDecals.BloodRandom.NextSingle()) * BurstReach;
                store.Add(TerrainDecalType.Splash, m.Position + new Vector3(d.X, 0, d.Y) * far, d,
                    0.3f + host.BloodDecals.BloodRandom.NextSingle() * 0.2f, opaque, TerrainDecalStore.NewLife, host.BloodDecals.BloodRandom);
            }
        }
        var centre = m.Position + new Vector3(0, BurstHeight, 0);
        host.Sound.Characters.Sounds?.Burst(centre);
        if (bleeds && host.Gpu.Particles is { } particles)
        {
            var recolour = BloodDecals.BloodRecolour(m.Colour);
            particles.Burst(BurstSplash, centre, Quaternion.Identity, 0.3f, MistSize, MistLife, recolour);
            for (int i = 0; i < SpraysPerBurst; i++)
            {
                var d = Vector2.Normalize(RandomDirection() + blow * 0.6f);
                var spray = Vector3.Normalize(new Vector3(d.X, 0.3f + host.BloodDecals.BloodRandom.NextSingle() * 0.7f, d.Y));
                particles.Burst(BurstSpray, centre, Turn(Vector3.UnitZ, spray), 0.4f + host.BloodDecals.BloodRandom.NextSingle() * 0.3f,
                    MistSize, MistLife, recolour);
            }
        }
        if (host.Gpu.Debris is not { } debris) return;
        float terrain = host.Gpu.Terrain.HeightAt(m.Position.X, m.Position.Z);
        // On an upper floor the pieces land on its level; the terrain is beneath the building.
        float floor = m.Position.Y > terrain + BloodDecals.BloodAboveGround ? m.Position.Y : float.NaN;
        var limbs = LimbMeshes(m.Race);
        var meshes = new List<string>(limbs);
        int meat = bleeds ? MeatPerBurst : 0;
        for (int i = 0; i < meat; i++) meshes.Add(MeatMesh);
        for (int i = 0; i < BonesPerBurst; i++) meshes.Add(BoneMeshes[host.BloodDecals.BloodRandom.Next(BoneMeshes.Length)]);
        for (int i = 0; i < meshes.Count; i++)
        {
            string mesh = meshes[i];
            bool limb = i < limbs.Length, flesh = i < limbs.Length + meat;
            if (debris.Extent(mesh, flesh ? null : BoneMeshes[0]) is not { } size) continue;
            // The nest bones are a beast's (up to 3.5 m long): all shrunk alike by BoneScale (±15%), so a vertebra stays smaller than a thigh bone.
            // The meat is a 39 cm slab: torn smaller.
            float scale = limb ? 1 : flesh ? 0.5f + host.BloodDecals.BloodRandom.NextSingle() * 0.4f
                : BoneScale * (0.85f + host.BloodDecals.BloodRandom.NextSingle() * 0.3f);
            var extent = size * scale;
            var d = Vector2.Normalize(RandomDirection() + blow * 0.6f);
            float speed = 30 + host.BloodDecals.BloodRandom.NextSingle() * 60;
            var spinAxis = Vector3.Normalize(new Vector3(host.BloodDecals.BloodRandom.NextSingle() - 0.5f,
                host.BloodDecals.BloodRandom.NextSingle() - 0.5f, host.BloodDecals.BloodRandom.NextSingle() - 0.5f) + new Vector3(1e-3f));
            Pieces.Add(new Piece
            {
                Mesh = mesh,
                Scale = scale,
                Extent = extent,
                Floor = floor,
                Born = world.Tick,
                Colour = bleeds ? 0xFF000000 | BloodDecals.Abgr(m.Colour) : 0,
                Recolour = BloodDecals.BloodRecolour(m.Colour),
                Flesh = flesh,
                Position = centre,
                Velocity = new Vector3(d.X * speed, 30 + host.BloodDecals.BloodRandom.NextSingle() * 50, d.Y * speed),
                Rotation = Quaternion.Normalize(new Quaternion(host.BloodDecals.BloodRandom.NextSingle() - 0.5f,
                    host.BloodDecals.BloodRandom.NextSingle() - 0.5f, host.BloodDecals.BloodRandom.NextSingle() - 0.5f,
                    host.BloodDecals.BloodRandom.NextSingle() - 0.5f)),
                Spin = spinAxis * (4 + host.BloodDecals.BloodRandom.NextSingle() * 10),
            });
        }
        if (Pieces.Count > MaxPieces) Pieces.RemoveRange(0, Pieces.Count - MaxPieces);
    }

    /// <summary>
    /// The shortest rotation taking unit vector <paramref name="from"/> to unit vector <paramref name="to"/>.
    /// </summary>
    internal static Quaternion Turn(Vector3 from, Vector3 to)
    {
        var axis = Vector3.Cross(from, to);
        float s = axis.Length();
        if (s < 1e-5f) return Vector3.Dot(from, to) > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        return Quaternion.CreateFromAxisAngle(axis / s, MathF.Atan2(s, Vector3.Dot(from, to)));
    }

    internal Vector2 RandomDirection()
    {
        float a = host.BloodDecals.BloodRandom.NextSingle() * MathF.Tau;
        return new Vector2(MathF.Sin(a), MathF.Cos(a));
    }

    /// <summary>
    /// The meshes of a race's <c>severed limbs</c> items (their <c>ground mesh</c>, else <c>mesh</c>).
    /// </summary>
    string[] LimbMeshes(string? race)
    {
        if (race is null || host.Scene.Database is not { } db) return [];
        if (_limbMeshes.TryGetValue(race, out var known)) return known;
        var list = new List<string>();
        if (db.Find(race) is { } record)
            foreach (var limb in record.GetReferences("severed limbs"))
                if (db.Find(limb.TargetStringId) is { } item
                        && (item.GetPath("ground mesh") is { Length: > 0 } ground ? ground : item.GetPath("mesh")) is { Length: > 0 } mesh)
                    list.Add(mesh);
        return _limbMeshes[race] = [.. list];
    }

    /// <summary>
    /// Moves the pieces by <paramref name="dt"/> seconds of game time and hands the renderer this frame's list; those older than a corpse go.
    /// </summary>
    internal void UpdateDebris(World world, float dt)
    {
        Pieces.RemoveAll(p => world.Tick - p.Born >= BodySystem.CorpseTicksOf(world));
        host.Gpu.DebrisPieces.Clear();
        AddBolts();
        AddTracers(world);
        UpdateFalls(world);
        AddRestingLimbs();
        if (Pieces.Count == 0) return;
        int steps = Math.Clamp((int)MathF.Ceiling(dt * 60), 1, 8);
        float h = dt / steps;
        foreach (var p in Pieces)
        {
            for (int s = 0; s < steps && !p.Resting && h > 0; s++) Step(p, h);
            host.Gpu.DebrisPieces.Add(new DebrisPiece(p.Mesh, p.Scale, p.Rotation, p.Position));
        }
    }

    /// <summary>
    /// The severed limbs falling or tipping (docs/simulation.md "Ground items"), timed from the drop's tick by the shown tick and the sub-tick alpha,
    /// not by the frame's time, so a picture taken n ticks after the cut shows the fall at that point (still while paused). The stump is sampled once
    /// its character is drawn; with no stump drawn after <see cref="FallUnsampledSeconds"/> the item is set at rest. Landed, it becomes a ground
    /// item's placement; with no placement layer (the animation sandbox) the piece stays drawn.
    /// </summary>
    internal void UpdateFalls(World world)
    {
        double clock = host.Session.ShownTick + host.Session.SimulationAlpha;
        float tick = world.TickSeconds;
        for (int i = Falls.Count - 1; i >= 0; i--)
        {
            var f = Falls[i];
            if (f.Start is null) SampleStump(f);
            float elapsed = MathF.Max(0, (float)((clock - f.Stump.Tick) * tick));
            bool landed = f.Start is null ? elapsed > FallUnsampledSeconds : elapsed >= f.Flight + FallTipTime;
            if (landed)
            {
                Falls.RemoveAt(i);
                RestLimb(f.Ground, f.Lying, f.RestY);
                continue;
            }
            if (f.Start is not { } start)
            {
                if (landed) host.Gpu.DebrisPieces.Add(new DebrisPiece(f.Mesh, 1, f.Lying, LimbMiddle(f.Mesh, f.Look, f.Lying, f.Rest), 1, f.Look));
                continue;
            }
            if (elapsed < f.Flight)
            {
                var at = new Vector3(start.X + f.Drift.X * elapsed, start.Y - 0.5f * FallGravity * elapsed * elapsed, start.Z + f.Drift.Y * elapsed);
                host.Gpu.DebrisPieces.Add(new DebrisPiece(f.Mesh, 1, f.Hanging, LimbMiddle(f.Mesh, f.Look, f.Hanging, at), 1, f.Look));
            }
            else
            {
                float s = Math.Min(1, (elapsed - f.Flight) / FallTipTime), eased = s * s * (3 - 2 * s);
                var turned = Quaternion.Slerp(f.Hanging, f.Lying, eased);
                host.Gpu.DebrisPieces.Add(new DebrisPiece(f.Mesh, 1, turned, LimbMiddle(f.Mesh, f.Look, turned, f.Rest), 1, f.Look));
            }
        }
    }

    /// <summary>
    /// Samples the stump's bone where its character is drawn now (the pose of the last frame); the flight and the drift follow from its height over
    /// the rest point.
    /// </summary>
    void SampleStump(FallingLimb f)
    {
        if (host.Gpu?.Characters is not { } characters || characters.BonePosition(host.DrawKey(f.Stump.Character), f.Stump.Bone) is not { } start)
            return;
        f.Start = start;
        f.Flight = MathF.Sqrt(2 * MathF.Max(start.Y - f.RestY, 0) / FallGravity);
        f.Drift = f.Flight > 0 ? new Vector2(f.Rest.X - start.X, f.Rest.Z - start.Z) / f.Flight : Vector2.Zero;
    }

    readonly Dictionary<int, ProjectileSnapshot> _boltsBefore = [];
    /// <summary>
    /// Each bolt in flight's gap from its shooter's muzzle when it was first drawn, and where that was (by bolt id).
    /// </summary>
    readonly Dictionary<int, (Vector3 Gap, Vector3 From)> _boltLaunches = [];
    /// <summary>
    /// How far (units) a drawn bolt takes to close the gap between the crossbow's muzzle, where it is first drawn, and its simulated flight, which
    /// starts at a fixed point by the shooter (engine choice: the simulation has no posed bones).
    /// </summary>
    const float LaunchBlend = 150;

    /// <summary>
    /// The bolts of the snapshot (docs/game/combat.md "Bolt visuals"): each the crossbow's <c>live ammo</c> mesh with its +z along the flight, a bolt
    /// in flight between its last two positions by the simulation's alpha and stretched along z by the snapshot's factor (the original's fake motion
    /// blur), a landed one where it stuck.
    /// </summary>
    internal void AddBolts()
    {
        var now = host.Session.CurrentSnapshot.Projectiles;
        _boltHeads.Clear();
        if (now.Count == 0) _boltLaunches.Clear();
        if (now.Count == 0 || host.Gpu.Debris is not { } debris) return;
        _boltsBefore.Clear();
        if (host.Session.PreviousSnapshot is { } previous)
            foreach (var b in previous.Projectiles) _boltsBefore[b.Id] = b;
        float alpha = host.Session.SimulationAlpha;
        foreach (var p in now)
        {
            if (p.Mesh.Length == 0 || debris.Extent(p.Mesh, null) is null) continue;
            var at = !p.Stuck && _boltsBefore.TryGetValue(p.Id, out var b) && !b.Stuck ? Vector3.Lerp(b.Position, p.Position, alpha) : p.Position;
            // A bolt leaves the crossbow: drawn from the muzzle, it joins its simulated flight over LaunchBlend units.
            if (!p.Stuck)
            {
                if (!_boltLaunches.TryGetValue(p.Id, out var launch))
                    _boltLaunches[p.Id] = launch =
                        (host.Gpu.Characters?.Muzzle(host.DrawKey(p.Shooter)) is { } muzzle ? muzzle - at : Vector3.Zero, at);
                float left = 1 - Vector3.Distance(at, launch.From) / LaunchBlend;
                if (left > 0) at += launch.Gap * left;
                _boltHeads[p.Id] = (at, launch.From + launch.Gap);
            }
            else _boltLaunches.Remove(p.Id);
            host.Gpu.DebrisPieces.Add(new DebrisPiece(p.Mesh, 1, Turn(Vector3.UnitZ, p.Direction), at, p.Stuck ? 1 : p.Stretch));
        }
        if (_boltLaunches.Count > now.Count)
            foreach (int id in _boltLaunches.Keys.ToArray())
                if (!now.Any(p => p.Id == id)) _boltLaunches.Remove(id);
    }

    /// <summary>
    /// Where each bolt in flight is drawn this frame and the muzzle it left (by bolt id), for <see cref="AddTracers"/>.
    /// </summary>
    readonly Dictionary<int, (Vector3 At, Vector3 Muzzle)> _boltHeads = [];
    /// <summary>
    /// The tracers drawn, by bolt id: one keeps shrinking after its bolt stops (<see cref="Trail.Stopped"/>) until it is gone.
    /// </summary>
    readonly Dictionary<int, Trail> _trails = [];
    readonly List<int> _trailsGone = [];
    /// <summary>
    /// The <see cref="GameSaves.WorldEpoch"/> <see cref="_trails"/> belong to: a load or a new world drops the old world's.
    /// </summary>
    int _trailsEpoch = -1;

    sealed class Trail
    {
        public Vector3 Head, Direction;
        public float Length, Speed;
        /// <summary>
        /// The game time (seconds) its bolt stuck or went; null while it flies.
        /// </summary>
        public double? Stopped;
    }

    /// <summary>
    /// Seconds of a bolt's flight its tracer covers, and the longest one (units).
    /// </summary>
    const float TracerSeconds = 0.2f, TracerMaxLength = 500;
    /// <summary>
    /// The longest a stopped bolt's tracer takes to shrink into it (seconds; a fast bolt's goes at its speed, quicker).
    /// </summary>
    const float TracerFade = 0.15f;
    const float TracerWidth = 0.7f;
    static readonly Vector4 TracerColour = new(1, 1, 1, 0.6f);

    /// <summary>
    /// The Meitou <c>tracers</c> switch (docs/game/combat.md "Bolt visuals"): a semi-transparent streak behind each drawn bolt in flight, <see
    /// cref="TracerSeconds"/> of its flight long but never reaching back past the muzzle, so it grows out of the crossbow. When the bolt sticks or
    /// goes, the tail flies on into where it stopped and the streak shrinks away; timed by the shown game time, so a paused game keeps its tracers.
    /// </summary>
    internal void AddTracers(World world)
    {
        if (host.Gpu.Particles is not { } particles) return;
        particles.Tracers.Clear();
        if (_trailsEpoch != host.Saves.WorldEpoch)
        {
            _trails.Clear();
            _trailsEpoch = host.Saves.WorldEpoch;
        }
        if (!host.WorldOptions.MeitouTracers)
        {
            _trails.Clear();
            return;
        }
        double clock = (host.Session.ShownTick + host.Session.SimulationAlpha) * world.TickSeconds;
        foreach (var p in host.Session.CurrentSnapshot.Projectiles)
        {
            if (p.Stuck)
            {
                if (_trails.TryGetValue(p.Id, out var hit) && hit.Stopped is null) Stop(hit, p.Position, clock);
                continue;
            }
            if (!_boltHeads.TryGetValue(p.Id, out var head)) continue;
            if (!_trails.TryGetValue(p.Id, out var t) || t.Stopped is not null) _trails[p.Id] = t = new Trail();
            (t.Head, t.Direction, t.Speed) = (head.At, p.Direction, p.Speed);
            t.Length = MathF.Min(MathF.Min(TracerMaxLength, p.Speed * TracerSeconds), Vector3.Distance(head.At, head.Muzzle));
        }
        _trailsGone.Clear();
        foreach (var (id, t) in _trails)
        {
            // A bolt gone without sticking (recycled past its range, a glancing terrain hit) stops where it was last drawn.
            if (t.Stopped is null && !_boltHeads.ContainsKey(id)) Stop(t, t.Head, clock);
            float length = t.Length;
            if (t.Stopped is { } stopped)
                // back in time: a load
                length = clock < stopped ? 0 : length - (float)(clock - stopped) * MathF.Max(t.Speed, t.Length / TracerFade);
            if (length <= 0)
            {
                if (t.Stopped is not null) _trailsGone.Add(id);
                continue;
            }
            particles.Tracers.Add(new(t.Head, t.Head - t.Direction * length, TracerWidth, TracerColour));
        }
        foreach (int id in _trailsGone) _trails.Remove(id);
    }

    /// <summary>
    /// A tracer's bolt stopped at <paramref name="at"/>: the tail stays where it was drawn and the head moves there, from which the streak shrinks.
    /// </summary>
    static void Stop(Trail t, Vector3 at, double clock)
    {
        var tail = t.Head - t.Direction * t.Length;
        t.Head = at;
        t.Length = Vector3.Distance(tail, at);
        if (t.Length > 1e-3f) t.Direction = (at - tail) / t.Length;
        t.Stopped = clock;
    }

    /// <summary>
    /// A character's stuck bolts for the renderer, in its slot's array (<see cref="GameHost.BoltsBySlot"/>).
    /// </summary>
    internal IReadOnlyList<StuckBoltMesh> BoltsOf(CharacterSnapshot c)
    {
        var stuck = c.StuckBolts;
        if (stuck.Count == 0) return [];
        ref var kept = ref GameHost.Slot(ref host.BoltsBySlot, c.Id.Slot);
        if (kept is null || kept.Length != stuck.Count) kept = new StuckBoltMesh[stuck.Count];
        for (int i = 0; i < stuck.Count; i++) kept[i] = new StuckBoltMesh(stuck[i].Id, stuck[i].Mesh, stuck[i].Bone, stuck[i].Direction);
        return kept;
    }

    void Step(Piece p, float h)
    {
        p.Velocity.Y -= Gravity * h;
        p.Position += p.Velocity * h;
        float spin = p.Spin.Length();
        if (spin > 0) p.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(p.Spin / spin, spin * h) * p.Rotation);
        float floor = float.IsNaN(p.Floor) ? host.Gpu.Terrain.HeightAt(p.Position.X, p.Position.Z) : p.Floor;
        float thin = MathF.Min(p.Extent.X, MathF.Min(p.Extent.Y, p.Extent.Z));
        if (p.Position.Y - thin > floor) return;
        p.Position.Y = floor + thin;
        if (!p.Landed && p.Colour != 0 && host.BloodDecals.DecalStore is { } store)
            store.Add(TerrainDecalType.Splash, p.Position, new Vector2(p.Velocity.X, p.Velocity.Z), 0.12f, p.Colour, TerrainDecalStore.NewLife,
                host.BloodDecals.BloodRandom);
        if (!p.Landed && p.Flesh)
        {
            host.Sound.Characters.Sounds?.Splat(p.Position);
            if (p.Colour != 0) host.Gpu.Particles?.Burst(BurstSplash, p.Position, Quaternion.Identity, 0.15f, 1.3f, 1.5f, p.Recolour);
        }
        p.Landed = true;
        var flat = new Vector2(p.Velocity.X, p.Velocity.Z);
        if (MathF.Abs(p.Velocity.Y) < 15 && flat.Length() < 12)
        {
            p.Resting = true;
            p.Velocity = p.Spin = Vector3.Zero;
            p.Rotation = LieFlat(p.Rotation, p.Extent);
            return;
        }
        p.Velocity = new Vector3(p.Velocity.X * Grip, MathF.Abs(p.Velocity.Y) * Bounce, p.Velocity.Z * Grip);
        p.Spin *= Grip;
    }

    /// <summary>
    /// The rotation that lays a piece on its thinnest side, keeping where its long axis pointed on the ground.
    /// </summary>
    static Quaternion LieFlat(Quaternion rotation, Vector3 extent)
    {
        var axis = extent.X <= extent.Y && extent.X <= extent.Z ? Vector3.UnitX : extent.Y <= extent.Z ? Vector3.UnitY : Vector3.UnitZ;
        var up = Vector3.Transform(axis, rotation);
        if (up.Y < 0) up = -up;
        var tilt = Vector3.Cross(up, Vector3.UnitY);
        float s = tilt.Length();
        if (s < 1e-5f) return rotation;
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(tilt / s, MathF.Atan2(s, up.Y)) * rotation);
    }

    // Severed limbs on the ground, falling and at rest (docs/game/blood-decals.md "On the body and on severed limbs"): drawn by the debris renderer
    // with the original's SeveredLimb look (its material's textures, the skin-tone mask, blood with the cut end solid), not as a world object.
    /// <summary>
    /// A severed limb at rest: its mesh and look, its rotation and where its origin is.
    /// </summary>
    internal sealed record RestingLimb(string Mesh, LimbLook Look, Quaternion Rotation, Vector3 Origin);

    internal readonly Dictionary<int, RestingLimb> RestLimbs = [];
    Dictionary<string, GameRecord>? _limbRaces;
    readonly Dictionary<string, LimbLook?> _limbLooks = new(StringComparer.Ordinal);

    /// <summary>
    /// Sets a landed limb down where the object path would have put it, drawn as debris with the severed limb's look.
    /// </summary>
    internal void RestLimb(GroundItemSnapshot g, Quaternion rotation, float y)
    {
        if (host.Scene.Database?.Find(g.Record) is not { } item || WorldObjectLayout.ItemMesh(item) is not { Length: > 0 } mesh
                || LimbLookOf(g.Record) is not { } look)
        {
            host.GroundItemSync.ShowGround(g, rotation, y);
            return;
        }
        RestLimbs[g.Id] = new RestingLimb(mesh, ToneOf(look, g)!, rotation, new Vector3(g.Position.X, y, g.Position.Z));
    }

    /// <summary>
    /// The look of a ground item that is a race's <c>severed limbs</c> entry (null for any other item): its MATERIAL record's texture map and colour
    /// map (the skin-tone mask), the race's blood colour and its normal map. The skin tone comes with the cut (<see cref="ToneOf"/>).
    /// </summary>
    internal LimbLook? LimbLookOf(string record)
    {
        if (_limbLooks.TryGetValue(record, out var known)) return known;
        LimbLook? look = null;
        if (host.Scene.Database is { } db)
        {
            if (_limbRaces is null)
            {
                _limbRaces = new(StringComparer.Ordinal);
                foreach (var race in db.Records.Values)
                    if (race.Type == FcsRecordType.Race)
                        foreach (var limb in race.GetReferences("severed limbs")) _limbRaces.TryAdd(limb.TargetStringId, race);
            }
            if (_limbRaces.TryGetValue(record, out var owner) && db.Find(record) is { } item)
            {
                var spec = item.GetReferences("material").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(r => r is not null);
                string? Name(string key) => spec?.GetPath(key) is { Length: > 0 } p ? Path.GetFileName(p.Replace('\\', '/')) : null;
                if (Name("texture map") is { } diffuse)
                    look = new LimbLook(diffuse, Name("color map"), Vector3.Zero,
                        CharacterAppearance.Rgb(owner.GetInt("blood colour", 0x800000) & 0xFFFFFF), owner.StringId,
                        Rendering.Characters.CharacterRenderer.SkinByDefault(owner), Name("normal map"));
            }
        }
        return _limbLooks[record] = look;
    }

    /// <summary>
    /// The look with the skin tone of the character the limb was cut from (<see cref="FallSource.SkinTone"/>; none for a limb read from a save).
    /// </summary>
    internal static LimbLook? ToneOf(LimbLook? look, GroundItemSnapshot g) => look is null ? null
        : look with { SkinTone = g.Fall?.SkinTone ?? default };

    readonly Dictionary<(string Record, Vector3 Tone), MouthMesh?> _mouthMeshes = [];

    /// <summary>
    /// The limb an animal holds in its mouth (<see cref="CharacterSnapshot.Mouth"/>), drawn at its <c>mouth</c> point by the character renderer: the
    /// item's <c>mesh</c> (the original attaches that one, <c>FUN_140537690</c>) with the severed limb's look and the cut character's skin tone. Null
    /// for none, or an item without a mesh.
    /// </summary>
    internal MouthMesh? MouthOf(CharacterSnapshot c)
    {
        if (c.Mouth is not { } record) return null;
        if (_mouthMeshes.TryGetValue((record, c.MouthTone), out var known)) return known;
        MouthMesh? made = null;
        if (host.Scene.Database?.Find(record) is { } item && item.GetPath("mesh") is { Length: > 0 } mesh)
            made = new MouthMesh(mesh, LimbLookOf(record) is { } look ? look with { SkinTone = c.MouthTone } : null);
        return _mouthMeshes[(record, c.MouthTone)] = made;
    }

    /// <summary>
    /// The position of a limb's piece: its mesh's origin (a limb keeps the mesh's own space, <see cref="DebrisRenderer"/>; the other pieces are
    /// centred on their bounds).
    /// </summary>
    static Vector3 LimbMiddle(string mesh, LimbLook? look, Quaternion rotation, Vector3 origin) => origin;

    void AddRestingLimbs()
    {
        foreach (var (id, r) in RestLimbs)
            if (!host.GroundItemSync.HiddenGround.Contains(id) && host.Gpu.Debris?.Extent(r.Mesh, null, r.Look) is not null)
                host.Gpu.DebrisPieces.Add(new DebrisPiece(r.Mesh, 1, r.Rotation, LimbMiddle(r.Mesh, r.Look, r.Rotation, r.Origin), 1, r.Look));
    }
}