using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Game;

/// <summary>
/// luckyren Mode's burst (docs/game/luckyren.md): an <see cref="BloodMarkKind.Explosion"/> throws the game's bone meshes and the race's severed limbs out of
/// the body, which fly, tumble and bounce on the ground and then lie there as long as a corpse would, and it splashes blood all round (with the
/// <c>Blood</c> setting on). Display only: the pieces move in game time (still while paused) and are not saved.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The nest bones' meshes (<c>data/items/nests</c>; only <c>bone1</c> has an item record, whose material all of them take).</summary>
    static readonly string[] BoneMeshes = [.. Enumerable.Range(1, 14).Select(i => $"bone{i}.mesh")];

    /// <summary>Raw meat (rebirth.mod's item), thrown with the bones of a race that bleeds.</summary>
    const string MeatMesh = "Item_Meat_Raw.mesh";
    /// <summary>The blood particle systems: the splash at the burst and where flesh lands, and the spray (along its +Z) thrown out of the body.</summary>
    const string BurstSplash = "Bloodsplash01", BurstSpray = "HackBlood-system";

    /// <summary>The burst's mist: its sprites twice the systems' size and living 3 times as long (they keep growing, so they end about 6 times as big).</summary>
    const float MistSize = 2, MistLife = 3;

    const int BonesPerBurst = 12, MeatPerBurst = 4, SpraysPerBurst = 5, MaxPieces = 400, BurstSplashes = 24;
    const float Gravity = 98, BurstHeight = 10, Bounce = 0.35f, Grip = 0.6f, BurstReach = 60, BoneScale = 0.12f;

    sealed class Piece
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
        /// <summary>The splash particles' colour for a race whose blood is not red (<see cref="BloodRecolour"/>).</summary>
        public Vector3? Recolour;
    }

    readonly List<Piece> pieces = [];
    readonly Dictionary<string, string[]> limbMeshes = new(StringComparer.Ordinal);

    /// <summary>
    /// The fall of a severed limb (<see cref="StartFall"/>; docs/formats/collision.md "Item physics": the original's item is a dynamic PhysX body made at the stump's bone with no
    /// speed of its own, in the scene's gravity 98.1, tipping to rest on the ground). Its mesh's origin falls from <see cref="Start"/> (the stump, once sampled) to <see cref="Rest"/>'s
    /// height, drifting at <see cref="Drift"/> (units/s), then turns from <see cref="Hanging"/> to <see cref="Lying"/> in <see cref="FallTipTime"/>, and the item is shown at rest.
    /// </summary>
    sealed class FallingLimb
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

    readonly List<FallingLimb> falls = [];
    const float FallGravity = 98.1f, FallTipTime = 0.25f, FallUnsampledSeconds = 2;

    /// <summary>A burst: blood all round and the pieces thrown.</summary>
    void Burst(in BloodMark m, World world)
    {
        bool bleeds = m.Amount > 0;
        var blow = m.Direction.LengthSquared() > 1e-6f ? Vector2.Normalize(m.Direction) : Vector2.Zero;
        if (decalStore is { } store && bleeds && m.Position.Y <= gpu.Terrain.HeightAt(m.Position.X, m.Position.Z) + BloodAboveGround)
        {
            uint opaque = 0xFF000000 | Abgr(m.Colour);
            store.Add(TerrainDecalType.Pool, m.Position, RandomDirection(), 1.5f, opaque, TerrainDecalStore.NewLife, bloodRandom);
            for (int i = 0; i < BurstSplashes; i++)
            {
                var d = Vector2.Normalize(RandomDirection() + blow * 0.5f);
                float far = MathF.Sqrt(bloodRandom.NextSingle()) * BurstReach;
                store.Add(TerrainDecalType.Splash, m.Position + new Vector3(d.X, 0, d.Y) * far, d, 0.3f + bloodRandom.NextSingle() * 0.2f, opaque, TerrainDecalStore.NewLife, bloodRandom);
            }
        }
        var centre = m.Position + new Vector3(0, BurstHeight, 0);
        characterSounds?.Burst(centre);
        if (bleeds && gpu.Particles is { } particles)
        {
            var recolour = BloodRecolour(m.Colour);
            particles.Burst(BurstSplash, centre, Quaternion.Identity, 0.3f, MistSize, MistLife, recolour);
            for (int i = 0; i < SpraysPerBurst; i++)
            {
                var d = Vector2.Normalize(RandomDirection() + blow * 0.6f);
                var spray = Vector3.Normalize(new Vector3(d.X, 0.3f + bloodRandom.NextSingle() * 0.7f, d.Y));
                particles.Burst(BurstSpray, centre, Turn(Vector3.UnitZ, spray), 0.4f + bloodRandom.NextSingle() * 0.3f, MistSize, MistLife, recolour);
            }
        }
        if (gpu.Debris is not { } debris) return;
        float terrain = gpu.Terrain.HeightAt(m.Position.X, m.Position.Z);
        // On an upper floor the pieces land on its level; the terrain is beneath the building.
        float floor = m.Position.Y > terrain + BloodAboveGround ? m.Position.Y : float.NaN;
        var limbs = LimbMeshes(m.Race);
        var meshes = new List<string>(limbs);
        int meat = bleeds ? MeatPerBurst : 0;
        for (int i = 0; i < meat; i++) meshes.Add(MeatMesh);
        for (int i = 0; i < BonesPerBurst; i++) meshes.Add(BoneMeshes[bloodRandom.Next(BoneMeshes.Length)]);
        for (int i = 0; i < meshes.Count; i++)
        {
            string mesh = meshes[i];
            bool limb = i < limbs.Length, flesh = i < limbs.Length + meat;
            if (debris.Extent(mesh, flesh ? null : BoneMeshes[0]) is not { } size) continue;
            // The nest bones are a beast's (up to 3.5 m long): all shrunk alike by BoneScale (±15%), so a vertebra stays smaller than a thigh bone.
            // The meat is a 39 cm slab: torn smaller.
            float scale = limb ? 1 : flesh ? 0.5f + bloodRandom.NextSingle() * 0.4f : BoneScale * (0.85f + bloodRandom.NextSingle() * 0.3f);
            var extent = size * scale;
            var d = Vector2.Normalize(RandomDirection() + blow * 0.6f);
            float speed = 30 + bloodRandom.NextSingle() * 60;
            var spinAxis = Vector3.Normalize(new Vector3(bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() - 0.5f) + new Vector3(1e-3f));
            pieces.Add(new Piece
            {
                Mesh = mesh, Scale = scale, Extent = extent, Floor = floor, Born = world.Tick, Colour = bleeds ? 0xFF000000 | Abgr(m.Colour) : 0, Recolour = BloodRecolour(m.Colour), Flesh = flesh,
                Position = centre,
                Velocity = new Vector3(d.X * speed, 30 + bloodRandom.NextSingle() * 50, d.Y * speed),
                Rotation = Quaternion.Normalize(new Quaternion(bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() - 0.5f)),
                Spin = spinAxis * (4 + bloodRandom.NextSingle() * 10),
            });
        }
        if (pieces.Count > MaxPieces) pieces.RemoveRange(0, pieces.Count - MaxPieces);
    }

    /// <summary>The shortest rotation taking unit vector <paramref name="from"/> to unit vector <paramref name="to"/>.</summary>
    static Quaternion Turn(Vector3 from, Vector3 to)
    {
        var axis = Vector3.Cross(from, to);
        float s = axis.Length();
        if (s < 1e-5f) return Vector3.Dot(from, to) > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        return Quaternion.CreateFromAxisAngle(axis / s, MathF.Atan2(s, Vector3.Dot(from, to)));
    }

    Vector2 RandomDirection()
    {
        float a = bloodRandom.NextSingle() * MathF.Tau;
        return new Vector2(MathF.Sin(a), MathF.Cos(a));
    }

    /// <summary>The meshes of a race's <c>severed limbs</c> items (their <c>ground mesh</c>, else <c>mesh</c>).</summary>
    string[] LimbMeshes(string? race)
    {
        if (race is null || scene.Database is not { } db) return [];
        if (limbMeshes.TryGetValue(race, out var known)) return known;
        var list = new List<string>();
        if (db.Find(race) is { } record)
            foreach (var limb in record.GetReferences("severed limbs"))
                if (db.Find(limb.TargetStringId) is { } item && (item.GetPath("ground mesh") is { Length: > 0 } ground ? ground : item.GetPath("mesh")) is { Length: > 0 } mesh)
                    list.Add(mesh);
        return limbMeshes[race] = [.. list];
    }

    /// <summary>Moves the pieces by <paramref name="dt"/> seconds of game time and hands the renderer this frame's list; those older than a corpse go.</summary>
    void UpdateDebris(World world, float dt)
    {
        pieces.RemoveAll(p => world.Tick - p.Born >= BodySystem.CorpseTicksOf(world));
        gpu.DebrisPieces.Clear();
        AddBolts();
        AddTracers(world);
        UpdateFalls(world);
        AddRestingLimbs();
        if (pieces.Count == 0) return;
        int steps = Math.Clamp((int)MathF.Ceiling(dt * 60), 1, 8);
        float h = dt / steps;
        foreach (var p in pieces)
        {
            for (int s = 0; s < steps && !p.Resting && h > 0; s++) Step(p, h);
            gpu.DebrisPieces.Add(new DebrisPiece(p.Mesh, p.Scale, p.Rotation, p.Position));
        }
    }

    /// <summary>
    /// The severed limbs falling or tipping (docs/simulation.md "Ground items"), timed from the drop's tick by the shown tick and the sub-tick alpha, not by the frame's time, so a
    /// picture taken n ticks after the cut shows the fall at that point (still while paused). The stump is sampled once its character is drawn; with no stump drawn after
    /// <see cref="FallUnsampledSeconds"/> the item is set at rest. Landed, it becomes a ground item's placement; with no placement layer (the animation sandbox) the piece stays drawn.
    /// </summary>
    void UpdateFalls(World world)
    {
        double clock = session.ShownTick + session.SimulationAlpha;
        float tick = world.TickSeconds;
        for (int i = falls.Count - 1; i >= 0; i--)
        {
            var f = falls[i];
            if (f.Start is null) SampleStump(f);
            float elapsed = MathF.Max(0, (float)((clock - f.Stump.Tick) * tick));
            bool landed = f.Start is null ? elapsed > FallUnsampledSeconds : elapsed >= f.Flight + FallTipTime;
            if (landed)
            {
                falls.RemoveAt(i);
                RestLimb(f.Ground, f.Lying, f.RestY);
                continue;
            }
            if (f.Start is not { } start)
            {
                if (landed) gpu.DebrisPieces.Add(new DebrisPiece(f.Mesh, 1, f.Lying, LimbMiddle(f.Mesh, f.Look, f.Lying, f.Rest), 1, f.Look));
                continue;
            }
            if (elapsed < f.Flight)
            {
                var at = new Vector3(start.X + f.Drift.X * elapsed, start.Y - 0.5f * FallGravity * elapsed * elapsed, start.Z + f.Drift.Y * elapsed);
                gpu.DebrisPieces.Add(new DebrisPiece(f.Mesh, 1, f.Hanging, LimbMiddle(f.Mesh, f.Look, f.Hanging, at), 1, f.Look));
            }
            else
            {
                float s = Math.Min(1, (elapsed - f.Flight) / FallTipTime), eased = s * s * (3 - 2 * s);
                var turned = Quaternion.Slerp(f.Hanging, f.Lying, eased);
                gpu.DebrisPieces.Add(new DebrisPiece(f.Mesh, 1, turned, LimbMiddle(f.Mesh, f.Look, turned, f.Rest), 1, f.Look));
            }
        }
    }

    /// <summary>Samples the stump's bone where its character is drawn now (the pose of the last frame); the flight and the drift follow from its height over the rest point.</summary>
    void SampleStump(FallingLimb f)
    {
        if (gpu?.Characters is not { } characters || characters.BonePosition(DrawKey(f.Stump.Character), f.Stump.Bone) is not { } start) return;
        f.Start = start;
        f.Flight = MathF.Sqrt(2 * MathF.Max(start.Y - f.RestY, 0) / FallGravity);
        f.Drift = f.Flight > 0 ? new Vector2(f.Rest.X - start.X, f.Rest.Z - start.Z) / f.Flight : Vector2.Zero;
    }

    readonly Dictionary<int, ProjectileSnapshot> boltsBefore = [];
    /// <summary>Each bolt in flight's gap from its shooter's muzzle when it was first drawn, and where that was (by bolt id).</summary>
    readonly Dictionary<int, (Vector3 Gap, Vector3 From)> boltLaunches = [];
    /// <summary>
    /// How far (units) a drawn bolt takes to close the gap between the crossbow's muzzle, where it is first drawn, and its simulated flight, which starts at a fixed point by the
    /// shooter (engine choice: the simulation has no posed bones).
    /// </summary>
    const float LaunchBlend = 150;

    /// <summary>
    /// The bolts of the snapshot (docs/game/combat.md "Bolt visuals"): each the crossbow's <c>live ammo</c> mesh with its +z along the flight, a bolt in flight between its last two
    /// positions by the simulation's alpha and stretched along z by the snapshot's factor (the original's fake motion blur), a landed one where it stuck.
    /// </summary>
    void AddBolts()
    {
        var now = session.CurrentSnapshot.Projectiles;
        boltHeads.Clear();
        if (now.Count == 0) boltLaunches.Clear();
        if (now.Count == 0 || gpu.Debris is not { } debris) return;
        boltsBefore.Clear();
        if (session.PreviousSnapshot is { } previous)
            foreach (var b in previous.Projectiles) boltsBefore[b.Id] = b;
        float alpha = session.SimulationAlpha;
        foreach (var p in now)
        {
            if (p.Mesh.Length == 0 || debris.Extent(p.Mesh, null) is null) continue;
            var at = !p.Stuck && boltsBefore.TryGetValue(p.Id, out var b) && !b.Stuck ? Vector3.Lerp(b.Position, p.Position, alpha) : p.Position;
            // A bolt leaves the crossbow: drawn from the muzzle, it joins its simulated flight over LaunchBlend units.
            if (!p.Stuck)
            {
                if (!boltLaunches.TryGetValue(p.Id, out var launch))
                    boltLaunches[p.Id] = launch = (gpu.Characters?.Muzzle(DrawKey(p.Shooter)) is { } muzzle ? muzzle - at : Vector3.Zero, at);
                float left = 1 - Vector3.Distance(at, launch.From) / LaunchBlend;
                if (left > 0) at += launch.Gap * left;
                boltHeads[p.Id] = (at, launch.From + launch.Gap);
            }
            else boltLaunches.Remove(p.Id);
            gpu.DebrisPieces.Add(new DebrisPiece(p.Mesh, 1, Turn(Vector3.UnitZ, p.Direction), at, p.Stuck ? 1 : p.Stretch));
        }
        if (boltLaunches.Count > now.Count)
            foreach (int id in boltLaunches.Keys.ToArray())
                if (!now.Any(p => p.Id == id)) boltLaunches.Remove(id);
    }

    /// <summary>Where each bolt in flight is drawn this frame and the muzzle it left (by bolt id), for <see cref="AddTracers"/>.</summary>
    readonly Dictionary<int, (Vector3 At, Vector3 Muzzle)> boltHeads = [];
    /// <summary>The tracers drawn, by bolt id: one keeps shrinking after its bolt stops (<see cref="Trail.Stopped"/>) until it is gone.</summary>
    readonly Dictionary<int, Trail> trails = [];
    readonly List<int> trailsGone = [];
    /// <summary>The <see cref="worldEpoch"/> <see cref="trails"/> belong to: a load or a new world drops the old world's.</summary>
    int trailsEpoch = -1;

    sealed class Trail
    {
        public Vector3 Head, Direction;
        public float Length, Speed;
        /// <summary>The game time (seconds) its bolt stuck or went; null while it flies.</summary>
        public double? Stopped;
    }

    /// <summary>Seconds of a bolt's flight its tracer covers, and the longest one (units).</summary>
    const float TracerSeconds = 0.2f, TracerMaxLength = 500;
    /// <summary>The longest a stopped bolt's tracer takes to shrink into it (seconds; a fast bolt's goes at its speed, quicker).</summary>
    const float TracerFade = 0.15f;
    const float TracerWidth = 0.7f;
    static readonly Vector4 TracerColour = new(1, 1, 1, 0.6f);

    /// <summary>
    /// The Meitou <c>tracers</c> switch (docs/game/combat.md "Bolt visuals"): a semi-transparent streak behind each drawn bolt in flight, <see cref="TracerSeconds"/> of
    /// its flight long but never reaching back past the muzzle, so it grows out of the crossbow. When the bolt sticks or goes, the tail flies on into where it stopped and
    /// the streak shrinks away; timed by the shown game time, so a paused game keeps its tracers.
    /// </summary>
    void AddTracers(World world)
    {
        if (gpu.Particles is not { } particles) return;
        particles.Tracers.Clear();
        if (trailsEpoch != worldEpoch) { trails.Clear(); trailsEpoch = worldEpoch; }
        if (!o.MeitouTracers) { trails.Clear(); return; }
        double clock = (session.ShownTick + session.SimulationAlpha) * world.TickSeconds;
        foreach (var p in session.CurrentSnapshot.Projectiles)
        {
            if (p.Stuck)
            {
                if (trails.TryGetValue(p.Id, out var hit) && hit.Stopped is null) Stop(hit, p.Position, clock);
                continue;
            }
            if (!boltHeads.TryGetValue(p.Id, out var head)) continue;
            if (!trails.TryGetValue(p.Id, out var t) || t.Stopped is not null) trails[p.Id] = t = new Trail();
            (t.Head, t.Direction, t.Speed) = (head.At, p.Direction, p.Speed);
            t.Length = MathF.Min(MathF.Min(TracerMaxLength, p.Speed * TracerSeconds), Vector3.Distance(head.At, head.Muzzle));
        }
        trailsGone.Clear();
        foreach (var (id, t) in trails)
        {
            // A bolt gone without sticking (recycled past its range, a glancing terrain hit) stops where it was last drawn.
            if (t.Stopped is null && !boltHeads.ContainsKey(id)) Stop(t, t.Head, clock);
            float length = t.Length;
            if (t.Stopped is { } stopped)
                length = clock < stopped ? 0 : length - (float)(clock - stopped) * MathF.Max(t.Speed, t.Length / TracerFade);   // back in time: a load
            if (length <= 0)
            {
                if (t.Stopped is not null) trailsGone.Add(id);
                continue;
            }
            particles.Tracers.Add(new(t.Head, t.Head - t.Direction * length, TracerWidth, TracerColour));
        }
        foreach (int id in trailsGone) trails.Remove(id);
    }

    /// <summary>A tracer's bolt stopped at <paramref name="at"/>: the tail stays where it was drawn and the head moves there, from which the streak shrinks.</summary>
    static void Stop(Trail t, Vector3 at, double clock)
    {
        var tail = t.Head - t.Direction * t.Length;
        t.Head = at;
        t.Length = Vector3.Distance(tail, at);
        if (t.Length > 1e-3f) t.Direction = (at - tail) / t.Length;
        t.Stopped = clock;
    }

    /// <summary>A character's stuck bolts for the renderer, in its slot's array (<see cref="boltsBySlot"/>).</summary>
    IReadOnlyList<StuckBoltMesh> BoltsOf(CharacterSnapshot c)
    {
        var stuck = c.StuckBolts;
        if (stuck.Count == 0) return [];
        ref var kept = ref Slot(ref boltsBySlot, c.Id.Slot);
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
        float floor = float.IsNaN(p.Floor) ? gpu.Terrain.HeightAt(p.Position.X, p.Position.Z) : p.Floor;
        float thin = MathF.Min(p.Extent.X, MathF.Min(p.Extent.Y, p.Extent.Z));
        if (p.Position.Y - thin > floor) return;
        p.Position.Y = floor + thin;
        if (!p.Landed && p.Colour != 0 && decalStore is { } store)
            store.Add(TerrainDecalType.Splash, p.Position, new Vector2(p.Velocity.X, p.Velocity.Z), 0.12f, p.Colour, TerrainDecalStore.NewLife, bloodRandom);
        if (!p.Landed && p.Flesh)
        {
            characterSounds?.Splat(p.Position);
            if (p.Colour != 0) gpu.Particles?.Burst(BurstSplash, p.Position, Quaternion.Identity, 0.15f, 1.3f, 1.5f, p.Recolour);
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

    /// <summary>The rotation that lays a piece on its thinnest side, keeping where its long axis pointed on the ground.</summary>
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
}
