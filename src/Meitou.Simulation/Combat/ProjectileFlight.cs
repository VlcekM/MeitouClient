using System.Numerics;

namespace Meitou.Simulation.Combat;

/// <summary>
/// A bolt in flight or stuck in the ground (the original's <c>Harpoon</c>, pooled by <c>HarpoonManager</c>).
/// </summary>
struct Projectile
{
    /// <summary>
    /// The bolt's number, in firing order (its rolls and the snapshot's identity).
    /// </summary>
    public int Id;

    /// <summary>
    /// The shooter and its intended target (slot and generation), and the shooter's faction when it fired.
    /// </summary>
    public int Shooter, ShooterGeneration, Target, TargetGeneration, ShooterFaction;

    /// <summary>
    /// The shooter is one of the player's characters (floaters, a unique's death).
    /// </summary>
    public bool ShooterIsPlayer;

    /// <summary>
    /// The shooter's effective Crossbows (or Turrets) level when it fired: the victim's "hit taken" XP weighs it.
    /// </summary>
    public float ShooterSkill;

    /// <summary>
    /// Where the bolt is and the way it flies (unit length).
    /// </summary>
    public Vector3 Position, Direction;

    /// <summary>
    /// Units a second, the range still to fly before the nose drops, the pierce damage and the friendly-fire avoidance chance.
    /// </summary>
    public float Speed, RangeLeft, Damage, Avoidance;

    /// <summary>
    /// Seconds in flight (the original's Harpoon +0x60): which stuck bolt goes first over the limit.
    /// </summary>
    public float Flown;

    /// <summary>
    /// Stuck in the ground (a visual only: bolts are not recoverable, <b>Observed</b>).
    /// </summary>
    public bool Stuck;

    /// <summary>
    /// The last character it passed through (friendly fire avoided), so the next step does not test it again.
    /// </summary>
    public int Passed;

    /// <summary>
    /// The ammunition's live mesh (empty: none shown, none stuck in a body).
    /// </summary>
    public string Mesh;

    /// <summary>
    /// Adds the bolt's simulated fields to the state hash.
    /// </summary>
    internal readonly void Hash(ref StateHasher h)
    {
        h.Add(Id);
        h.Add(Shooter);
        h.Add(ShooterGeneration);
        h.Add(Target);
        h.Add(TargetGeneration);
        h.Add(Position);
        h.Add(Direction);
        h.Add(Speed);
        h.Add(RangeLeft);
        h.Add(Damage);
        h.Add(Avoidance);
        h.Add(Stuck);
        h.Add(Passed);
    }
}

/// <summary>
/// The bolts of crossbows and the harpoons of turrets (the original's <c>HarpoonManager</c>): the list in firing order, their flight each tick and
/// the stuck ones kept up to <see cref="StuckLimit"/>. What a bolt does to a body is <see cref="CombatSystem"/>'s (its hit and friendly-fire rules).
/// </summary>
sealed class ProjectileFlight
{
    /// <summary>
    /// The bolts in flight and stuck, in firing order.
    /// </summary>
    readonly List<Projectile> _projectiles = [];

    /// <summary>
    /// The number the next bolt fired gets.
    /// </summary>
    int _nextProjectile;

    /// <summary>
    /// What a bolt's flight step tests against the click hulls in the grid query.
    /// </summary>
    struct FlightScan
    {
        /// <summary>
        /// The characters, for their last-tick places and click hulls.
        /// </summary>
        public CharacterTable Table;

        /// <summary>
        /// The shooter and the friend just passed, both left out.
        /// </summary>
        public int Shooter, Skip;

        /// <summary>
        /// The step's start and its length and direction.
        /// </summary>
        public Vector3 From, Seg;

        /// <summary>
        /// Hulls entered at or before <see cref="After"/> are passed already; the nearest so far is at <see cref="Best"/> (fractions of the step).
        /// </summary>
        public float After, Best;

        /// <summary>
        /// The body hit so far, -1 for none.
        /// </summary>
        public int Hit;
    }

    #region Pool

    /// <summary>
    /// Stuck bolts kept in the world per bolt mesh (<see cref="CombatSystem.StuckLimit"/>).
    /// </summary>
    public int StuckLimit { get; set; }

    /// <summary>
    /// Bolts in flight (for tests and the HUD).
    /// </summary>
    public int InFlight
    {
        get
        {
            int n = 0;
            foreach (var p in _projectiles)
                if (!p.Stuck) n++;

            return n;
        }
    }

    /// <summary>
    /// Bolts fired so far.
    /// </summary>
    public int Fired => _nextProjectile;

    /// <summary>
    /// Takes the number of a bolt being fired.
    /// </summary>
    public int NextId() => _nextProjectile++;

    /// <summary>
    /// A bolt just fired joins the list (flown from the next tick's schedule).
    /// </summary>
    public void Add(in Projectile p) => _projectiles.Add(p);

    /// <summary>
    /// The bolts as the snapshot shows them.
    /// </summary>
    public IReadOnlyList<ProjectileSnapshot> Publish()
    {
        if (_projectiles.Count == 0) return [];

        var list = new ProjectileSnapshot[_projectiles.Count];
        for (int k = 0; k < list.Length; k++)
        {
            var p = _projectiles[k];
            float scale = p.Stuck ? 1 : MathF.Max(1, p.Speed * 0.005f);
            list[k] = new ProjectileSnapshot(p.Id, p.Position, p.Direction, scale, p.Stuck, p.Mesh)
            {
                Shooter = new CharacterId(p.Shooter, p.ShooterGeneration),
                Speed = p.Stuck ? 0 : p.Speed,
            };
        }

        return list;
    }

    /// <summary>
    /// Adds the bolts to the state hash.
    /// </summary>
    public void Hash(ref StateHasher h)
    {
        // Only once a bolt has flown, so worlds without crossbows hash as before.
        if (_nextProjectile == 0) return;

        h.Add(_nextProjectile);
        h.Add(_projectiles.Count);
        foreach (var p in _projectiles) p.Hash(ref h);
    }

    #endregion

    #region Flight

    /// <summary>
    /// Advances every bolt one tick, in the order they were fired (serial, against the last tick's positions, which is what the grid holds;
    /// <c>FUN_1404380e0</c>): a straight line while range is left, then the nose pitching down 8 degrees a second; the first click hull
    /// (<see cref="CombatSystem.HullHalf"/>) or the ground on the step is hit. A friend in the way is passed with the shooter's avoidance chance; the
    /// ground or a building sticks the bolt (a grazing hit on the ground drops it). Stuck bolts stay up to <see cref="StuckLimit"/>.
    /// </summary>
    public void Fly(World world, CombatSystem combat)
    {
        if (_projectiles.Count == 0) return;

        float dt = world.TickSeconds;
        for (int k = 0; k < _projectiles.Count; k++)
        {
            var p = _projectiles[k];
            if (p.Stuck) continue;

            float step = p.Speed * dt;
            p.Flown += dt;
            p.RangeLeft -= step;
            if (p.RangeLeft < 0)
            {
                if (p.RangeLeft < -RangedFormulas.DropAfter)
                {
                    _projectiles.RemoveAt(k--);
                    continue;
                }

                // Spent: the nose drops.
                p.Direction = Dropped(p.Direction, dt);
            }

            var seg = p.Direction * step;
            if (Step(world, combat, ref k, ref p, seg, step)) continue;

            p.Position += seg;
            _projectiles[k] = p;
        }
    }

    /// <summary>
    /// A spent bolt's direction after one tick of its nose pitching down (<see cref="RangedFormulas.DropDegreesPerSecond"/>), never past
    /// straight down.
    /// </summary>
    static Vector3 Dropped(Vector3 direction, float dt)
    {
        var flat = new Vector2(direction.X, direction.Z);
        float pitch = MathF.Asin(Math.Clamp(direction.Y, -1, 1)) - RangedFormulas.DropDegreesPerSecond * dt * (MathF.PI / 180);
        pitch = MathF.Max(pitch, -MathF.PI / 2 + 1e-3f);
        flat = flat.LengthSquared() > 1e-8f ? Vector2.Normalize(flat) : Vector2.UnitY;

        return new Vector3(flat.X * MathF.Cos(pitch), MathF.Sin(pitch), flat.Y * MathF.Cos(pitch));
    }

    /// <summary>
    /// Tests the bolt at index <paramref name="k"/> along its step <paramref name="seg"/>: up to four passes, each the ground or a building first
    /// along the step, then the nearest body before it; a friend passed lets the next pass look beyond it. True when the bolt hit a body or the
    /// ground (removed or stored stuck, <paramref name="k"/> moved to where the loop goes on), false when it flies the whole step.
    /// </summary>
    bool Step(World world, CombatSystem combat, ref int k, ref Projectile p, Vector3 seg, float step)
    {
        var table = world.Characters;
        var prev = table.Previous;
        float after = -1;
        for (int pass = 0; pass < 4; pass++)
        {
            // The ground or a building first along the step, then the nearest body before it.
            float ground = SolidHits.Hit(world.Walkability, combat.Buildings, p.Position, seg, RangedTuning.FlightTerrainStep, out bool building);
            int v = FirstBody(world, p, seg, step, after, ground, out float best);
            if (v >= 0)
            {
                bool intended = v == p.Target && prev[v].Generation == p.TargetGeneration;
                bool enemy = intended || combat.Hostile(table, p.Shooter, p.ShooterGeneration, p.ShooterFaction, p.Target, v);
                if (!enemy && FriendPasses(world, combat, p, v))
                {
                    p.Passed = v;
                    after = best;
                    continue;
                }

                combat.BoltHit(world, p, v, intended);
                _projectiles.RemoveAt(k--);
                return true;
            }

            if (ground < 0) return false;

            Land(world, ref k, ref p, p.Position + seg * ground, building);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The body whose click hull the step enters first after <paramref name="after"/> and before the ground at <paramref name="ground"/> (the
    /// shooter's own and the friend just passed left out), -1 for none, and where (<paramref name="best"/>, a fraction of the step).
    /// </summary>
    static int FirstBody(World world, in Projectile p, Vector3 seg, float step, float after, float ground, out float best)
    {
        var scan = new FlightScan
        {
            Table = world.Characters,
            Shooter = p.Shooter,
            Skip = p.Passed,
            From = p.Position,
            Seg = seg,
            After = after,
            Best = ground < 0 ? 1 : ground,
            Hit = -1,
        };
        var mid = p.Position + seg * 0.5f;
        world.Grid.Query(mid.X, mid.Z, step * 0.5f + RangedTuning.MaxHullReach, ref scan, static (int other, ref FlightScan st) =>
        {
            if (other == st.Shooter || other == st.Skip || !CombatSystem.Shootable(st.Table.Previous, st.Table, other)) return;

            var half = CombatSystem.HullHalf(st.Table.Cold(other)!);
            if (!CombatSystem.HitHull(st.From, st.Seg, st.Table.Previous[other].Position, half, out float t)) return;
            if (t > st.After && (t < st.Best || t == st.Best && other < st.Hit))
            {
                st.Best = t;
                st.Hit = other;
            }
        });

        best = scan.Best;
        return scan.Hit;
    }

    /// <summary>
    /// A friend in the way (<c>FUN_1404380e0</c>): passed with the shooter's avoidance chance (FRIENDLY_FIRE XP 20), else hit (XP 100).
    /// </summary>
    static bool FriendPasses(World world, CombatSystem combat, in Projectile p, int v)
    {
        var prev = world.Characters.Previous;
        float roll = Rng.Float(Rng.Hash(world.Seed, (ulong)(uint)p.Id, RngPurpose.Ranged, ((ulong)(uint)v << 32) | (uint)prev[v].Generation));
        bool friendlyPass = roll < p.Avoidance;
        combat.ShooterXp(world.Characters, p, friendlyPass ? RangedFormulas.FriendlyPassXp : RangedFormulas.FriendlyHitXp);

        return friendlyPass;
    }

    /// <summary>
    /// The bolt at index <paramref name="k"/> meets the ground or a building at <paramref name="at"/>: a grazing hit on the ground drops it, else it
    /// sticks there (<see cref="TrimStuck"/>).
    /// </summary>
    void Land(World world, ref int k, ref Projectile p, Vector3 at, bool building)
    {
        var normal = GroundNormal(world.Walkability, at.X, at.Z);
        if (!building && MathF.Abs(Vector3.Dot(normal, p.Direction)) < RangedFormulas.Grazing)
        {
            _projectiles.RemoveAt(k--);
            return;
        }

        p.Position = at;
        p.Stuck = true;
        _projectiles[k] = p;
        CombatSystem.Sound(world, SoundKind.BoltStuck, -1, p.Shooter, at);
        k = TrimStuck(k, p.Mesh);
    }

    /// <summary>
    /// A bolt of <paramref name="mesh"/> has just stuck at index <paramref name="k"/>: over <see cref="StuckLimit"/> stuck bolts of that mesh, the
    /// one that flew longest (the first of equals, the new bolt included) is removed (<c>FUN_14043acf0</c>). Returns the index the loop continues
    /// from.
    /// </summary>
    int TrimStuck(int k, string mesh)
    {
        int count = 0, worst = -1;
        float longest = -1;
        for (int j = 0; j < _projectiles.Count; j++)
        {
            var q = _projectiles[j];
            if (!q.Stuck || q.Mesh != mesh) continue;

            count++;
            if (q.Flown > longest)
            {
                longest = q.Flown;
                worst = j;
            }
        }

        if (count <= StuckLimit) return k;

        _projectiles.RemoveAt(worst);
        return worst <= k ? k - 1 : k;
    }

    /// <summary>
    /// The terrain's normal at (<paramref name="x"/>, <paramref name="z"/>), from the heights one unit either side.
    /// </summary>
    static Vector3 GroundNormal(IWalkability walk, float x, float z)
    {
        float dx = walk.TerrainHeight(x - 1, z) - walk.TerrainHeight(x + 1, z);
        float dz = walk.TerrainHeight(x, z - 1) - walk.TerrainHeight(x, z + 1);

        return Vector3.Normalize(new Vector3(dx, 2, dz));
    }

    #endregion
}
