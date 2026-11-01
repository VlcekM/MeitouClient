using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>Engine choices of the ranged fight where the research stops (each is labelled in docs/simulation.md "Combat", ranged).</summary>
public static class RangedTuning
{
    /// <summary>
    /// Height of the barrel above the feet and how far ahead of the body it is (units): the original fires from the crossbow's <c>barrel pos</c> on the posed hands
    /// (docs/game/combat.md "Shot origin"), which the simulation does not pose; a human's in-hand muzzle measured 13 to 15 up and 6 to 9 ahead in Meitou's renderer.
    /// </summary>
    public const float BarrelHeight = 15, BarrelForward = 8;
    /// <summary>Height above the feet the aim point (the original's bone <c>Bip01 Spine2</c>) stands for.</summary>
    public const float AimHeight = 13;
    /// <summary>The body a bolt hits: an upright cylinder of the footprint radius, this tall (the original tests the physics shape, <b>Unknown</b>).</summary>
    public const float BodyHeight = 18;
    /// <summary>While closing in, the line of fire must be clear this long (seconds) before the shooter stops and aims again (<c>FUN_140438c70</c>'s 0.3 s, its ray flags <b>Unknown</b>).</summary>
    public const float ClearToAim = 0.3f;
    /// <summary>The settings' <c>harpoonLimit</c> default and range (<see cref="CombatSystem.StuckLimit"/> is the live value).</summary>
    public const int DefaultStuckLimit = 60, MinStuckLimit = 10, MaxStuckLimit = 10000;
    /// <summary>Spacing of the terrain samples along a line of fire and along a bolt's step (units).</summary>
    public const float TerrainStep = 20, FlightTerrainStep = 5;
    /// <summary>Length of the pieces a long line of fire is cut into for the neighbour grid (units).</summary>
    public const float LineChunk = 100;
    /// <summary>The largest body radius a bolt is tested against (the biggest animals' footprints are about this).</summary>
    public const float MaxBodyRadius = 30;
}

public sealed partial class CombatSystem : IProjectileSource
{
    /// <summary>The <see cref="Effect.Kind"/> of a shot: <c>Arg</c> is the shooter's shot number; the target is the shooter's target.</summary>
    public const int FireEffect = 2;
    /// <summary>The <see cref="Effect.Kind"/> of a finished reload (target = the shooter): one bolt is taken from its inventory.</summary>
    public const int ReloadEffect = 3;

    /// <summary>A bolt in flight or stuck in the ground (the original's <c>Harpoon</c>, pooled by <c>HarpoonManager</c>).</summary>
    struct Projectile
    {
        public int Id;
        public int Shooter, ShooterGeneration, Target, TargetGeneration, ShooterFaction;
        public bool ShooterIsPlayer;
        public float ShooterSkill;
        public Vector3 Position, Direction;
        public float Speed, RangeLeft, Damage, Avoidance;
        /// <summary>Seconds in flight (the original's Harpoon +0x60): which stuck bolt goes first over the limit.</summary>
        public float Flown;
        /// <summary>Stuck in the ground (a visual only: bolts are not recoverable, <b>Observed</b>).</summary>
        public bool Stuck;
        /// <summary>The last character it passed through (friendly fire avoided), so the next step does not test it again.</summary>
        public int Passed;
        public string Mesh;

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

    readonly List<Projectile> projectiles = [];
    int nextProjectile;

    /// <summary>Bolts in flight (for tests and the HUD).</summary>
    public int ProjectilesInFlight
    {
        get
        {
            int n = 0;
            foreach (var p in projectiles) if (!p.Stuck) n++;
            return n;
        }
    }

    /// <summary>Bolts fired so far.</summary>
    public int ProjectilesFired => nextProjectile;

    public IReadOnlyList<ProjectileSnapshot> PublishProjectiles()
    {
        if (projectiles.Count == 0) return [];
        var list = new ProjectileSnapshot[projectiles.Count];
        for (int k = 0; k < list.Length; k++)
        {
            var p = projectiles[k];
            list[k] = new ProjectileSnapshot(p.Id, p.Position, p.Direction, p.Stuck ? 1 : MathF.Max(1, p.Speed * 0.005f), p.Stuck, p.Mesh)
                { Shooter = new CharacterId(p.Shooter, p.ShooterGeneration), Speed = p.Stuck ? 0 : p.Speed };
        }
        return list;
    }

    void HashRanged(ref StateHasher h)
    {
        // Only once a bolt has flown, so worlds without crossbows hash as before.
        if (nextProjectile == 0) return;
        h.Add(nextProjectile);
        h.Add(projectiles.Count);
        foreach (var p in projectiles) p.Hash(ref h);
    }

    // ------------------------------------------------------------------ geometry

    static Vector3 Barrel(in CharacterHot c) =>
        c.Position + new Vector3(MathF.Sin(c.Yaw) * RangedTuning.BarrelForward, RangedTuning.BarrelHeight, MathF.Cos(c.Yaw) * RangedTuning.BarrelForward);

    /// <summary>
    /// The aim point (<c>FUN_140436d10</c>): the target's spine (here <see cref="RangedTuning.AimHeight"/> above its feet) plus its velocity times the bolt's flight time over the
    /// distance from the barrel (the lead, <b>Verified</b> in the disassembly).
    /// </summary>
    static Vector3 AimPoint(Vector3 barrel, in CharacterHot target, float shotSpeed)
    {
        var spine = target.Position + new Vector3(0, RangedTuning.AimHeight, 0);
        float flight = Vector3.Distance(barrel, spine) / MathF.Max(shotSpeed, 1);
        return spine + target.Velocity * flight;
    }

    /// <summary>
    /// Where a segment from <paramref name="from"/> along <paramref name="seg"/> first enters an upright cylinder (radius <paramref name="radius"/>, from <paramref name="feet"/>
    /// up <paramref name="height"/>), as a fraction of the segment in [0, 1].
    /// </summary>
    internal static bool HitBody(Vector3 from, Vector3 seg, Vector3 feet, float radius, float height, out float t)
    {
        t = 0;
        float ax = seg.X, az = seg.Z, wx = from.X - feet.X, wz = from.Z - feet.Z;
        float a = ax * ax + az * az, c = wx * wx + wz * wz - radius * radius;
        float t0, t1;
        if (a < 1e-8f)
        {
            if (c > 0) return false;
            t0 = 0;
            t1 = 1;
        }
        else
        {
            float b = 2 * (wx * ax + wz * az);
            float disc = b * b - 4 * a * c;
            if (disc < 0) return false;
            float root = MathF.Sqrt(disc);
            t0 = (-b - root) / (2 * a);
            t1 = (-b + root) / (2 * a);
        }
        t0 = MathF.Max(t0, 0);
        t1 = MathF.Min(t1, 1);
        if (t0 > t1) return false;
        // Within [t0, t1] the segment is inside the circle; it must also be between the feet and the head there.
        float lo = feet.Y, hi = feet.Y + height;
        float y0 = from.Y + seg.Y * t0, y1 = from.Y + seg.Y * t1;
        if (y0 >= lo && y0 <= hi) { t = t0; return true; }
        if (MathF.Abs(seg.Y) < 1e-8f) return false;
        float plane = y0 < lo ? lo : hi;
        if ((y0 < lo && y1 < lo) || (y0 > hi && y1 > hi)) return false;
        t = (plane - from.Y) / seg.Y;
        return t >= t0 && t <= t1;
    }

    /// <summary>Whether <paramref name="shooter"/> (of <paramref name="faction"/>) counts <paramref name="other"/> as an enemy for the line of fire and friendly fire.</summary>
    bool Hostile(CharacterTable table, int shooter, int shooterGeneration, int faction, int target, int other)
    {
        if (other == target) return true;
        ref readonly var o = ref read[other];
        if (o.TargetSlot == shooter && o.TargetGeneration == shooterGeneration) return true;
        var oc = table.Cold(other);
        if (oc is null) return false;
        // The original asks the character's enemy test (Character slot 125); the engine uses the faction relations, and without a table (hand-built worlds) another faction.
        return CharacterFacts.RegardsEnemy(Relations, faction, oc.Faction);
    }

    /// <summary>Can be hit by a bolt: alive, with a body, not down (a lying body does not stop bolts: <b>engine</b>, the original's hit shape is <b>Unknown</b>).</summary>
    static bool Shootable(ReadOnlySpan<CharacterHot> state, CharacterTable table, int slot) =>
        state[slot].Alive && table.Cold(slot) is { Medical.Incapacitated: false, Race: not null };

    struct LineScan
    {
        public CharacterTable Table;
        public int Self, Target;
        public Vector3 From, Seg;
        public float Best;
        public int Hit;
    }

    /// <summary>
    /// The fire decision's line of fire (<c>FUN_140437ad0</c>): 1 fire, 0 something nearer than the target is in the way (close in), 2 a friend is in the way (hold).
    /// The original holds when terrain is hit first; the engine closes in instead, so a shooter behind a dune or a wall does not stand for ever (the ray flags are <b>Unknown</b>).
    /// The terrain and the buildings' collision are tested (<see cref="SolidHits"/>).
    /// </summary>
    int LineOfFire(World world, int self, int target, Vector3 from, Vector3 to, int faction)
    {
        var seg = to - from;
        float length = seg.Length();
        if (length < 1e-3f) return 1;
        // Terrain and buildings, the last few units at the target left out.
        float solid = length > RangedTuning.TerrainStep ? SolidHits.Hit(world.Walkability, Buildings, from, seg * (1 - RangedTuning.TerrainStep / length), RangedTuning.TerrainStep, out _) : -1;
        if (solid >= 0) return 0;
        // Characters: the nearest body crossed before the target, the grid queried piece by piece.
        var scan = new LineScan { Table = world.Characters, Self = self, Target = target, From = from, Seg = seg, Best = 1, Hit = -1 };
        int chunks = Math.Max(1, (int)MathF.Ceiling(length / RangedTuning.LineChunk));
        for (int k = 0; k < chunks; k++)
        {
            var mid = from + seg * ((k + 0.5f) / chunks);
            world.Grid.Query(mid.X, mid.Z, length / chunks * 0.5f + RangedTuning.MaxBodyRadius, ref scan, static (int other, ref LineScan st) =>
            {
                if (other == st.Self || other == st.Target || other == st.Hit) return;
                var state = st.Table.Previous;
                if (!Shootable(state, st.Table, other)) return;
                float r = CombatTuning.Footprint(st.Table.Cold(other));
                if (HitBody(st.From, st.Seg, state[other].Position, r, RangedTuning.BodyHeight, out float t) && (t < st.Best || t == st.Best && other < st.Hit))
                {
                    st.Best = t;
                    st.Hit = other;
                }
            });
        }
        if (scan.Hit < 0) return 1;
        return Hostile(world.Characters, self, world.Characters.Previous[self].Generation, faction, target, scan.Hit) ? 1 : 2;
    }

    // ------------------------------------------------------------------ act: the controller

    /// <summary>The gap between two bodies (centre distance minus both footprints), from the last tick's positions.</summary>
    static float Gap(ReadOnlySpan<CharacterHot> prev, CharacterTable table, int a, int b) =>
        Vector2.Distance(new(prev[a].Position.X, prev[a].Position.Z), new(prev[b].Position.X, prev[b].Position.Z)) - CombatTuning.Footprint(table.Cold(a)) - CombatTuning.Footprint(table.Cold(b));

    /// <summary>
    /// A threat within <see cref="RetaliationSystem.ImmediateReach"/> (the planner's <c>iAmBeingMeleeAttacked immediate</c>, Character +0x2b0, recomputed by <c>FUN_1404378f0</c> over the
    /// threats list, <b>Verified</b>; docs/game/combat.md "Fight lifecycle and orders"): one of the senses' threats (<see cref="SenseBlock.Threats"/>) by centre distance, not down (staying
    /// low or crawling still count), not in a bed or cage, not carried, and not behind a gate that is not open between them (<c>FUN_140598450</c>, <see cref="Locks.LockSystem.NotBlocked"/>).
    /// The original's "getting up" byte (+0x278) is not modelled.
    /// </summary>
    internal bool ImmediateThreat(World world, int self)
    {
        var table = world.Characters;
        var prev = table.Previous;
        if (CharacterFacts.Senses(world, self) is not { } senses) return false;
        var here = prev[self].Position;
        foreach (var id in senses.Threats)
        {
            int a = id.Slot;
            if ((uint)a >= (uint)prev.Length || !prev[a].Alive || prev[a].Generation != id.Generation || read[a].Down) continue;
            if (table.Cold(a) is not { } c || c.InSomething != global::Meitou.Simulation.Locks.HeldState.None || !c.CarriedBy.IsNone) continue;
            var at = prev[a].Position;
            var d = at - here;
            if (d.X * d.X + d.Z * d.Z > RetaliationSystem.ImmediateReach * RetaliationSystem.ImmediateReach) continue;
            if (Locks is { } locks && !locks.NotBlocked(world, here, at)) continue;
            return true;
        }
        return false;
    }

    internal static WeaponKinds MeleeDrawn(Fighter f) => f.Weapon is { } w && !f.Unarmed ? HandHold.OfCategory(w.Data.AnimationCategory).Weapon : WeaponKinds.None;

    /// <summary>
    /// Chooses crossbow or melee for a fighting character that carries a crossbow (the planner's desires, docs/game/combat.md "Fight lifecycle and orders", melee or
    /// shooting): in Ranged mode (on for every NPC, the player's toggle for its characters) the crossbow while it has shots or ammunition and no threat is within
    /// <see cref="RetaliationSystem.ImmediateReach"/>, melee otherwise; out of Ranged mode never the crossbow. A character without a crossbow is left alone.
    /// </summary>
    void ChooseMode(World world, int i, ref CombatSlot s, CharacterCold cold, bool fighting)
    {
        var fighter = cold.Fighter!;
        if (fighter.Crossbow is not { } x) return;
        if (!fighting)
        {
            if (s.Ranged || s.ReloadLeft > 0 || s.RangedState != 0 || s.ClearTime != 0 || s.Steady != 0)
            {
                // The fight is over: the controller stops (a reload in progress is abandoned; the bolts loaded stay loaded).
                s.Ranged = false;
                cold.RangedCombatMode = false;
                s.RangedState = 0;
                s.ReloadLeft = 0;
                s.ClearTime = 0;
                s.Steady = 0;
            }
            return;
        }
        bool hasAmmo = x.InfiniteAmmo || Ammunition.Find(cold.Inventory, x.Data.AmmoId) is not null;
        // A crossbow (weapon category Bow) needs both arms (FUN_1407f2840, FUN_1409991d0); losing one puts it away (FUN_1405cc230) and the fight goes on in melee.
        // The same gate needs the posture (vfunc 0x368) 0: staying low or crawling it does not shoot (docs/game/combat.md "Arms"); and the shooting rows want !me swim (139): not
        // while swimming (water state 3).
        bool can = (s.Loaded > 0 || hasAmmo) && cold.Medical is not { BothArmsWork: false } && cold.Medical is not { Prone: true } && cold.WaterState != 3;
        if (s.Dry == can) s.Dry = !can;
        // Ranged mode takes the crossbow however close the target is (between swings): melee is x0.1 against shooting's 1, and a threat within 40 refuses the shooting rows.
        bool rangedMode = Orders.RangedMode(cold);
        // Block does not stop it: neither the shooting desire (FUN_140598260) nor the gate reads isDefensiveMode.
        bool want = can && rangedMode && !ImmediateThreat(world, i) && (s.Ranged || s.AttackTech < 0);
        if (want == s.Ranged) return;
        s.Ranged = want;
        if (!want) cold.RangedCombatMode = false;
        s.RangedState = want ? (byte)RangedStates.Aiming : (byte)0;
        s.Steady = want ? RangedFormulas.SteadyAim : 0;
        s.ReloadLeft = 0;
        s.ClearTime = 0;
        cold.DrawnWeapon = want ? HandHold.OfCategory((int)WeaponCategory.Bow).Weapon : MeleeDrawn(fighter);
    }

    /// <summary>
    /// One tick of the ranged controller (<c>FUN_140438c70</c>, the reload <c>FUN_140437ce0</c>, the fire decision <c>FUN_140437ad0</c>): reload when empty (one bolt
    /// taken at the end, through <see cref="ReloadEffect"/>); out of range or with the line blocked, close in (the pursuit system walks); in range, hold the aim for the
    /// steady time and fire (<see cref="FireEffect"/>) when the line is clear. XP: crossbows and dexterity while reloading, crossbows and perception while aiming.
    /// </summary>
    void TickRanged(World world, int i, ref CombatSlot s, CharacterCold cold, EffectBuffer effects, int now, float dt)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var fighter = cold.Fighter!;
        var x = fighter.Crossbow!;
        var stats = cold.Stats!;
        var race = cold.Race!;
        int t = s.TargetSlot;
        // The shooting task's update sets Character +0x198 to Crossbows (35) every tick (Task_RangedAttack FUN_14033bfb0): the HUD's Current Skill line.
        cold.UseSkill(StatsEnumerated.Crossbows, world.Tick);
        cold.RangedCombatMode = true;

        if (s.Loaded <= 0)
        {
            s.RangedState = (byte)RangedStates.Reloading;
            if (s.ReloadLeft <= 0)
            {
                if (!x.InfiniteAmmo && Ammunition.Find(cold.Inventory, x.Data.AmmoId) is null)
                {
                    s.Dry = true;
                    return;
                }
                float skill = RangedFormulas.SkillBlend(Eff(cold, StatsEnumerated.Crossbows), Eff(cold, StatsEnumerated.Dexterity), fighter.DexterityMult);
                s.ReloadTime = RangedFormulas.ReloadTime(skill, x.Stats.ReloadMin, x.Stats.ReloadMax);
                s.ReloadLeft = s.ReloadTime;
            }
            s.ReloadLeft -= dt;
            xp.Continuous(stats, race, StatsEnumerated.Crossbows, dt, RangedFormulas.CrossbowXp);
            xp.Continuous(stats, race, StatsEnumerated.Dexterity, dt, RangedFormulas.DexterityReloadXp);
            if (s.ReloadLeft <= 0)
            {
                s.ReloadLeft = 0;
                s.Loaded = x.NumShots;
                cold.CrossbowShots = s.Loaded;
                s.Steady = RangedFormulas.SteadyAim;
                s.RangedState = (byte)RangedStates.Aiming;
                if (!x.InfiniteAmmo) effects.Emit(i, i, ReloadEffect);
            }
            return;
        }

        var barrel = Barrel(prev[i]);
        float h = RangedFormulas.HeightFactor(barrel.Y, prev[t].Position.Y);
        if (!RangedFormulas.InRange(Vector3.DistanceSquared(prev[i].Position, prev[t].Position), x.Stats.Range, h))
        {
            s.RangedState = (byte)RangedStates.Closing;
            s.ClearTime = 0;
            s.Steady = RangedFormulas.SteadyAim;
            return;
        }
        var aim = AimPoint(barrel, prev[t], x.Stats.ShotSpeed);
        if (s.RangedState == (byte)RangedStates.Closing)
        {
            // Walking up: stop and aim once the line has been clear for a moment.
            if (LineOfFire(world, i, t, barrel, aim, cold.Faction) == 0)
            {
                s.ClearTime = 0;
                return;
            }
            s.ClearTime += dt;
            if (s.ClearTime < RangedTuning.ClearToAim) return;
            s.ClearTime = 0;
        }
        s.RangedState = (byte)RangedStates.Aiming;
        xp.Continuous(stats, race, StatsEnumerated.Perception, dt, RangedFormulas.PerceptionXp);
        xp.Continuous(stats, race, StatsEnumerated.Crossbows, dt, RangedFormulas.CrossbowXp);
        if (s.Steady > 0)
        {
            s.Steady -= dt;
            if (s.Steady > 0) return;
        }
        switch (LineOfFire(world, i, t, barrel, aim, cold.Faction))
        {
            case 0:
                s.RangedState = (byte)RangedStates.Closing;
                s.ClearTime = 0;
                return;
            case 2:
                return;
        }
        effects.Emit(i, t, FireEffect, 0, default, s.Shots);
        s.Shots++;
        s.Loaded--;
        cold.CrossbowShots = s.Loaded;
        s.Steady = RangedFormulas.SteadyAim;
        s.ShotTick = now;
    }

    // ------------------------------------------------------------------ commit: shots and reloads

    void ApplyReload(World world, in Effect e)
    {
        var cold = world.Characters.Cold(e.Source);
        if (cold?.Fighter?.Crossbow is not { } x) return;
        Ammunition.UseOne(cold.Inventory, x.Data.AmmoId);
    }

    /// <summary>
    /// A shot (<c>FUN_14043a730</c>): from the barrel towards the aim point, tilted by u x D degrees (D the deviation cone); damage U[min, max) x h, range x h,
    /// speed x (0.8 h + 0.2) with h the height factor; the friendly-fire avoidance from the shooter's skill. The bolt joins the list (flown from the next tick's schedule).
    /// </summary>
    void ApplyFire(World world, in Effect e)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int a = e.Source, t = e.Target, shot = e.Arg;
        var ac = table.Cold(a);
        if (ac?.Fighter?.Crossbow is not { } x || ac.Stats is null || ac.Medical is null || !prev[t].Alive) return;
        var barrel = Barrel(prev[a]);
        var aim = AimPoint(barrel, prev[t], x.Stats.ShotSpeed);
        var to = aim - barrel;
        if (to.LengthSquared() < 1e-6f) return;
        float h = RangedFormulas.HeightFactor(barrel.Y, prev[t].Position.Y);
        float deviation = RangedFormulas.Deviation(x.Stats.AccuracyDeviation, Eff(ac, StatsEnumerated.Crossbows), Eff(ac, StatsEnumerated.Perception));
        ulong key = Rng.Key(table.IdOf(a));
        ulong counter = (ulong)(uint)shot << 3;
        float u = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Ranged, counter));
        float spin = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Ranged, counter + 1));
        float w = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Ranged, counter + 2));
        var dir = RangedFormulas.Deviate(Vector3.Normalize(to), u * deviation, spin);
        var (damage, range, speed) = RangedFormulas.Bolt(x.Stats, h, w);
        projectiles.Add(new Projectile
        {
            Id = nextProjectile++,
            Shooter = a,
            ShooterGeneration = prev[a].Generation,
            Target = t,
            TargetGeneration = prev[t].Generation,
            ShooterFaction = ac.Faction,
            ShooterIsPlayer = ac.IsPlayer,
            ShooterSkill = Eff(ac, StatsEnumerated.Crossbows),
            Position = barrel,
            Direction = dir,
            Speed = speed,
            RangeLeft = range,
            Damage = damage,
            Avoidance = RangedFormulas.FriendlyAvoidance(Eff(ac, StatsEnumerated.FriendlyFire)),
            Passed = -1,
            Mesh = x.Data.LiveAmmoMesh,
        });
        ShotAt(world, t, ac.Faction);
        Sound(world, SoundKind.Shot, a, -1, prev[a].Position);
    }

    /// <summary>
    /// The target of a shot notices the shooter (<c>FUN_14043a730</c> → <c>FUN_140999d50</c>): one of the player's characters that senses meets the shooter's faction at once
    /// (docs/game/factions-squads-towns.md "Factions met").
    /// </summary>
    static void ShotAt(World world, int target, int faction)
    {
        if (world.Characters.Cold(target) is { } cold && PopulationSystem.MeetsFactions(cold)) world.Player.Meet(faction);
    }

    // ------------------------------------------------------------------ schedule: the flight

    struct FlightScan
    {
        public CharacterTable Table;
        public int Shooter, Skip;
        public Vector3 From, Seg;
        public float After, Best;
        public int Hit;
    }

    /// <summary>
    /// Advances every bolt one tick, in the order they were fired (serial, against the last tick's positions, which is what the grid holds; <c>FUN_1404380e0</c>):
    /// a straight line while range is left, then the nose pitching down 8 degrees a second; the first body or the ground on the step is hit. A friend in the way is
    /// passed with the shooter's avoidance chance; the ground or a building sticks the bolt (a grazing hit on the ground drops it). Stuck bolts stay up to <see cref="StuckLimit"/>.
    /// </summary>
    void FlyProjectiles(World world)
    {
        if (projectiles.Count == 0) return;
        var table = world.Characters;
        var prev = table.Previous;
        float dt = world.TickSeconds;
        var walk = world.Walkability;
        for (int k = 0; k < projectiles.Count; k++)
        {
            var p = projectiles[k];
            if (p.Stuck) continue;
            float step = p.Speed * dt;
            p.Flown += dt;
            p.RangeLeft -= step;
            if (p.RangeLeft < 0)
            {
                if (p.RangeLeft < -RangedFormulas.DropAfter)
                {
                    projectiles.RemoveAt(k--);
                    continue;
                }
                // Spent: the nose drops.
                var flat = new Vector2(p.Direction.X, p.Direction.Z);
                float pitch = MathF.Asin(Math.Clamp(p.Direction.Y, -1, 1)) - RangedFormulas.DropDegreesPerSecond * dt * (MathF.PI / 180);
                pitch = MathF.Max(pitch, -MathF.PI / 2 + 1e-3f);
                flat = flat.LengthSquared() > 1e-8f ? Vector2.Normalize(flat) : Vector2.UnitY;
                p.Direction = new Vector3(flat.X * MathF.Cos(pitch), MathF.Sin(pitch), flat.Y * MathF.Cos(pitch));
            }
            var seg = p.Direction * step;
            bool done = false;
            float after = -1;
            for (int pass = 0; pass < 4 && !done; pass++)
            {
                // The ground or a building first along the step, then the nearest body before it.
                float ground = SolidHits.Hit(walk, Buildings, p.Position, seg, RangedTuning.FlightTerrainStep, out bool building);
                var scan = new FlightScan { Table = table, Shooter = p.Shooter, Skip = p.Passed, From = p.Position, Seg = seg, After = after, Best = ground < 0 ? 1 : ground, Hit = -1 };
                var mid = p.Position + seg * 0.5f;
                world.Grid.Query(mid.X, mid.Z, step * 0.5f + RangedTuning.MaxBodyRadius, ref scan, static (int other, ref FlightScan st) =>
                {
                    if (other == st.Shooter || other == st.Skip || !Shootable(st.Table.Previous, st.Table, other)) return;
                    float r = CombatTuning.Footprint(st.Table.Cold(other));
                    if (HitBody(st.From, st.Seg, st.Table.Previous[other].Position, r, RangedTuning.BodyHeight, out float t) && t > st.After
                        && (t < st.Best || t == st.Best && other < st.Hit))
                    {
                        st.Best = t;
                        st.Hit = other;
                    }
                });
                if (scan.Hit >= 0)
                {
                    int v = scan.Hit;
                    bool intended = v == p.Target && prev[v].Generation == p.TargetGeneration;
                    bool enemy = intended || Hostile(table, p.Shooter, p.ShooterGeneration, p.ShooterFaction, p.Target, v);
                    if (!enemy)
                    {
                        // A friend in the way (FUN_1404380e0): passed with the shooter's avoidance chance (FRIENDLY_FIRE XP 20), else hit (XP 100).
                        float roll = Rng.Float(Rng.Hash(world.Seed, (ulong)(uint)p.Id, RngPurpose.Ranged, ((ulong)(uint)v << 32) | (uint)prev[v].Generation));
                        bool pass_ = roll < p.Avoidance;
                        ShooterXp(table, p, pass_ ? RangedFormulas.FriendlyPassXp : RangedFormulas.FriendlyHitXp);
                        if (pass_)
                        {
                            p.Passed = v;
                            after = scan.Best;
                            continue;
                        }
                    }
                    BoltHit(world, p, v, intended);
                    done = true;
                    projectiles.RemoveAt(k--);
                    break;
                }
                if (ground >= 0)
                {
                    var at = p.Position + seg * ground;
                    var normal = GroundNormal(walk, at.X, at.Z);
                    done = true;
                    if (!building && MathF.Abs(Vector3.Dot(normal, p.Direction)) < RangedFormulas.Grazing) projectiles.RemoveAt(k--);
                    else
                    {
                        p.Position = at;
                        p.Stuck = true;
                        projectiles[k] = p;
                        Sound(world, SoundKind.BoltStuck, -1, p.Shooter, at);
                        k = TrimStuck(k, p.Mesh);
                    }
                    break;
                }
                break;
            }
            if (done) continue;
            p.Position += seg;
            projectiles[k] = p;
        }
    }

    /// <summary>
    /// A bolt of <paramref name="mesh"/> has just stuck at index <paramref name="k"/>: over <see cref="StuckLimit"/> stuck bolts of that mesh, the one that flew longest
    /// (the first of equals, the new bolt included) is removed (<c>FUN_14043acf0</c>). Returns the index the loop continues from.
    /// </summary>
    int TrimStuck(int k, string mesh)
    {
        int count = 0, worst = -1;
        float longest = -1;
        for (int j = 0; j < projectiles.Count; j++)
        {
            var q = projectiles[j];
            if (!q.Stuck || q.Mesh != mesh) continue;
            count++;
            if (q.Flown > longest) { longest = q.Flown; worst = j; }
        }
        if (count <= StuckLimit) return k;
        projectiles.RemoveAt(worst);
        return worst <= k ? k - 1 : k;
    }

    static Vector3 GroundNormal(IWalkability walk, float x, float z)
    {
        float dx = walk.TerrainHeight(x - 1, z) - walk.TerrainHeight(x + 1, z);
        float dz = walk.TerrainHeight(x, z - 1) - walk.TerrainHeight(x, z + 1);
        return Vector3.Normalize(new Vector3(dx, 2, dz));
    }

    void ShooterXp(CharacterTable table, in Projectile p, float amount)
    {
        var state = table.Previous;
        if (p.Shooter >= state.Length || !state[p.Shooter].Alive || state[p.Shooter].Generation != p.ShooterGeneration) return;
        if (table.Cold(p.Shooter) is { Stats: { } stats, Race: { } race }) xp.Continuous(stats, race, StatsEnumerated.FriendlyFire, 1, amount);
    }

    /// <summary>
    /// A bolt in a body (<c>FUN_140439c90</c>): a packet of pierce = the bolt's damage, bleed 2, no penetration, through the hit pipeline with hit direction 6 (the part by its
    /// <c>hit</c> weight alone), armour and toughness, the global damage multiplier applied once more there (<b>Observed</b>, so bolts scale with its square). No block, no dodge.
    /// The victim takes "hit taken" XP; the shooter gains none for a hit. A heavy hit staggers as a melee one does.
    /// </summary>
    /// <summary>
    /// The bolt stays in the character it hit (<c>FUN_1406508d0</c>, <b>Observed</b>): on the struck part's <c>bone name</c>, or its <c>bone name 2</c> half the time when it has
    /// one (<c>FUN_140649fd0</c>), keeping its flight direction (here in the character's frame; the renderer fixes it to the bone, docs/game/combat.md "Bolt visuals").
    /// </summary>
    static void StickBolt(World world, in Projectile p, int v, CharacterCold dc, int part)
    {
        if (p.Mesh.Length == 0 || dc.Medical is not { } med || (uint)part >= (uint)med.Parts.Count) return;
        var template = med.Parts[part].Template;
        bool second = template.BoneName2.Length > 0 && Rng.Float(Rng.Hash(world.Seed, (ulong)(uint)p.Id, RngPurpose.Ranged, 0x426F6E65)) < 0.5f;
        string bone = second ? template.BoneName2 : template.BoneName;
        float yaw = world.Characters.Previous[v].Yaw;
        var local = Vector3.TransformNormal(p.Direction, Matrix4x4.CreateRotationY(-yaw));
        (dc.StuckBolts ??= []).Add(new StuckBolt(p.Id, p.Mesh, bone, local));
    }

    void BoltHit(World world, in Projectile p, int v, bool intended)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var dc = table.Cold(v);
        if (dc is null || dc.Medical is null || dc.Race is null) return;
        int now = (int)world.Tick;
        ref var D = ref write[v];
        var packet = new DamagePacket(0, 0, p.Damage, 0, RangedFormulas.BoltBleed, 0);
        var ctx = MakeContext(world, v, dc);
        var events = hitEvents;
        events.Clear();
        float toughness = dc.Stats is { } ds ? ds[StatsEnumerated.Toughness] * dc.Medical.StatMultiplier(StatsEnumerated.Toughness) : 0;
        ReadOnlySpan<ArmourPiece> armour = dc.Fighter is { } df ? df.Armour : default;
        var cover = new BoltCover(world.Seed, Rng.Key(table.IdOf(v)), p.Id);
        var (part, damage, absorbed) = HitResolver.LandDetailed(dc.Medical, ctx, packet, armour, cover, toughness, false, true, combat, events);
        FloatHit(world, v, dc, p.ShooterIsPlayer, part, damage, absorbed);
        Sound(world, SoundKind.BoltHit, v, p.Shooter, prev[v].Position, armoured: absorbed.Cut + absorbed.Blunt + absorbed.Pierce > 0, robot: false);
        Splash(world, v, dc, new Vector2(p.Direction.X, p.Direction.Z), damage.Cut + damage.Blunt + damage.Pierce);

        StickBolt(world, p, v, dc, part);
        D.Taken++;
        D.HitTick = now;
        D.HitPart = part;
        D.HitHeavy = false;
        // From behind: the bolt flies the way the victim faces.
        D.HitBehind = p.Direction.X * MathF.Sin(prev[v].Yaw) + p.Direction.Z * MathF.Cos(prev[v].Yaw) > 0;
        bool knockout = false, death = false;
        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case MedicalEventKind.KnockedOut: knockout = true; if (dc.Stats is { } ko) xp.ToughnessFromKnockOut(ko, dc.Race); break;
                case MedicalEventKind.Died: death = true; break;
                case MedicalEventKind.Decapitated: table.LimbsChanged.Enqueue((v, -1)); break;   // the look loses its head (nothing drops)
                case MedicalEventKind.PartSevered: if (dc.Stats is { } st) xp.ToughnessFromLimbLoss(st, dc.Race); table.LimbsChanged.Enqueue((v, dc.Medical.Parts[ev.Part].Template.LimbSlot));
                    world.Defer(new Dialogue.DialogueEvent(table.IdOf(v), CharacterId.None, dc.Medical.Parts[ev.Part].Template.LimbSlot is 2 or 3 ? 70 : 71) { Tick = world.Tick + 1 });
                    break;
            }
        }
        if (knockout || death) Knockdown(world, v, dc, new Vector2(p.Direction.X, p.Direction.Z), damage.Cut + damage.Blunt + damage.Pierce);
        if (dc.Medical.Incapacitated)
        {
            D.Down = true;
            D.AttackTech = -1;
            D.NextBlow = 0;
            D.ReactTech = -1;
            Sound(world, SoundKind.Down, v, p.Shooter, prev[v].Position, fatal: death);
        }
        // Only a heavy bolt staggers (engine choice: the original's reaction to a bolt was not traced).
        else if (damage.Cut + damage.Blunt + damage.Pierce + damage.ExtraStun > DamageFormulas.StumbleThreshold(toughness, combat))
            Stagger(world, ref D, v, dc, true, now, (ulong)(uint)p.Id ^ 0x424F4C54UL);
        if (dc.Stats is { } vs && dc.Fighter is { } vf)
            xp.Combat(vs, dc.Race, XpEvent.HitTaken, vf.Unarmed, StatsEnumerated.None, p.ShooterSkill, StrengthFactor(dc, vf));
        bool shooterAlive = p.Shooter < prev.Length && prev[p.Shooter].Alive && prev[p.Shooter].Generation == p.ShooterGeneration;
        if (shooterAlive && intended) write[p.Shooter].Landed++;
        if (shooterAlive) Crimes?.Hit(world, p.Shooter, v);
        if (shooterAlive && !dc.Medical.Incapacitated)
        {
            world.SystemOf<AggroSystem>()?.Notice(world, world.Characters.IdOf(v), world.Characters.IdOf(p.Shooter));   // the victim senses the shooter (FUN_140999d50)
            HitReplan(dc, world.Tick);
        }
        // The bolt hit's relation events (FUN_140439c90: none without a shooter; a bolt that was not meant for the victim is a minor knock-out and no hit).
        if (shooterAlive) Deeds?.Blow(world, p.Shooter, v, dc.Medical.Incapacitated, death, bolt: true, aimed: intended);
        if (death && world.Uniques.RecordOf(world.Characters.IdOf(v)) is { } unique) world.Uniques.Died(unique, p.ShooterIsPlayer, dc.Faction);
        if (death && world.Luckyren) dc.Burst = new Vector2(p.Direction.X, p.Direction.Z);
        if (Settings.RecordLog)
            log.Add(new CombatLogEntry(now, p.Shooter, v, 0, BlowOutcome.Hit, part, damage.Cut, damage.Blunt, damage.Pierce, damage.ExtraStun, knockout, death) { Ranged = true, Intended = intended });
    }

    /// <summary>The cover roll of a bolt: a function of the victim, the bolt and the armour piece.</summary>
    readonly struct BoltCover(ulong seed, ulong victimKey, int bolt) : ICoverRoll
    {
        public float Roll(int piece) => CombatRolls.Float(seed, victimKey, CombatRoll.Cover, ((ulong)0xB017 << 48) ^ ((ulong)(uint)bolt << 8) ^ (uint)piece);
    }

    /// <summary>
    /// What a character's crossbow is doing for its animation (the last finished tick): reloading (the progress of the reload), aiming (the steady time held), or just shot
    /// (for <see cref="ShotSeconds"/> after a shot); null when it is not fighting with the crossbow.
    /// </summary>
    public (RangedPose Pose, float Progress)? RangedPoseOf(CharacterId id, long tick, float tickSeconds)
    {
        var s = StateOf(id);
        if (!s.Ranged) return null;
        if (s.RangedState == (byte)RangedStates.Reloading && s.ReloadTime > 0)
            return (RangedPose.Reload, Math.Clamp(1 - s.ReloadLeft / s.ReloadTime, 0, 1));
        if (s.ShotTick > 0 && (tick - s.ShotTick) * tickSeconds < ShotSeconds)
            return (RangedPose.Shot, Math.Clamp((tick - s.ShotTick) * tickSeconds / ShotSeconds, 0, 1));
        if (s.RangedState == (byte)RangedStates.Aiming) return (RangedPose.Aim, Math.Clamp(1 - s.Steady / RangedFormulas.SteadyAim, 0, 1));
        return null;
    }

    /// <summary>Seconds the shot pose is shown after a bolt leaves (engine).</summary>
    public const float ShotSeconds = 0.3f;

    /// <summary>
    /// The ranged controller's State line (<c>FUN_140438640</c>, shown while <c>Character::isInCombatMode(0, 1)</c> holds, <see cref="IsInCombatMode"/>): the gun of the turret the
    /// character mans, else its crossbow; null otherwise.
    /// </summary>
    public RangedLineView? RangedLineOf(World world, CharacterId id, CharacterCold cold)
    {
        if (!IsInCombatMode(world.Characters, id, cold, false, true)) return null;
        if (cold.Gunning is { } key && turrets.TryGetValue(key, out var t))
        {
            bool noPower = t.Gun.IsConsumer && world.Machines.Find(key) is not { HasPower: true };
            return new(t.Reloading, t.Loaded, Math.Max(t.Gun.Gun.NumShots, 1), t.Reloading && t.ReloadTime > 0 ? t.ReloadLeft / t.ReloadTime : 0, noPower);
        }
        var s = StateOf(id);
        if (cold.Fighter?.Crossbow is not { } x) return null;
        // The controller's reloading flag (+0x80, FUN_140437ce0) is set only while a reload runs: a dry crossbow stays at Aiming.
        bool reloading = s.RangedState == (byte)RangedStates.Reloading && s.ReloadLeft > 0;
        return new(reloading, s.Loaded, x.NumShots, reloading && s.ReloadTime > 0 ? s.ReloadLeft / s.ReloadTime : 0, false);
    }
}

/// <summary>
/// The ranged controller's State line numbers (<see cref="CombatSystem.RangedLineOf"/>): reloading (else aiming), the gun's loaded and full shots (gun <c>+0x20</c>, <c>+0x1c</c>),
/// the share of the running reload still to go (gun <c>+0xe0</c>) and a powered gun without power.
/// </summary>
public readonly record struct RangedLineView(bool Reloading, int Loaded, int Shots, float ReloadLeft, bool NoPower);

/// <summary>The crossbow pose the animation shows (engine choice of clips: docs/simulation.md "Combat", ranged).</summary>
public enum RangedPose : byte { Aim, Shot, Reload }
