using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Data.World;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Combat;

/// <summary>
/// A manned turret as the snapshot shows it: its BUILDING and GUN_DATA records, the point it aims at (null: none yet) and the turn of its parts
/// (radians, <c>TurretGun.Parts</c> order).
/// </summary>
public readonly record struct TurretView(string BuildingId, string GunId, Vector3 Position, float Yaw, Vector3? Aim, float[]? Angles = null);

/// <summary>
/// The gun numbers a selected turret's Stats 1 lines read (<c>GunClassTurret</c> slot 14 <c>FUN_14030d620</c>, docs/game/ui-screens.md "Production
/// building"): the GUN_DATA <c>range</c>, <c>aim speed</c> and <c>accuracy deviation at 0 skill</c>, the mount factor M (gun slot 18), the height
/// factor h from the building to its last aim point (gun <c>+0x118</c>, 1 before any), the last gunner's Turrets level x 0.01 (gun <c>+0x2c</c>, 0
/// for a turret never manned) and that gunner's perception level while it exists (null: none). For the Height rows (<c>FUN_14030c2a0</c> section 3)
/// the height above the last aim point in metres (null before any aim); for the Loaded bar (<c>GunClass</c> slot 14 <c>FUN_14030ce20</c> section 4)
/// the loaded and full shots, the share of the running reload still to go (1 while none runs), the GUN_DATA reload times and the gunner's Crossbows
/// level (null: none).
/// </summary>
public readonly record struct TurretPanelView(string Instance, float Range, float AimSpeed, float Deviation, float Mount, float Height,
    float UserSkill, float? Perception)
{
    /// <summary>
    /// The height above the last aim point in metres; null before any aim.
    /// </summary>
    public int? HeightMetres { get; init; }

    /// <summary>
    /// The shots loaded.
    /// </summary>
    public int Loaded { get; init; }

    /// <summary>
    /// The shots a full gun holds.
    /// </summary>
    public int Shots { get; init; } = 1;

    /// <summary>
    /// The share of the running reload still to go (1 while none runs).
    /// </summary>
    public float ReloadLeft { get; init; } = 1;

    /// <summary>
    /// The GUN_DATA shortest reload time.
    /// </summary>
    public float ReloadMin { get; init; }

    /// <summary>
    /// The GUN_DATA longest reload time.
    /// </summary>
    public float ReloadMax { get; init; }

    /// <summary>
    /// The gunner's Crossbows level; null: no gunner.
    /// </summary>
    public float? Crossbows { get; init; }
}

/// <summary>
/// The turrets (docs/game/combat.md "Turrets", docs/simulation.md "Combat", turrets). The original runs a turret through its gunner's ranged
/// controller (<c>Task_UseTurret</c> <c>FUN_1403474f0</c> hands the controller the building and the Turrets skill, then ticks it): the controller
/// picks the target, reloads, holds the aim and fires the building's gun, whose shots, turn and loaded harpoons live on the building. Here one serial
/// step of the <see cref="Schedule"/> phase does the same for every manned turret, in key order: gunners are found (a player character operating its
/// turret, an NPC standing at the turret its squad gave it), then each turret chooses, reloads, turns and fires. Harpoons are bolts
/// (<see cref="Projectile"/>): they fly, hit and wound as a crossbow's, from the next tick.
/// </summary>
public sealed partial class CombatSystem
{
    /// <summary>
    /// Which character mans which turret, and the turrets' gun state.
    /// </summary>
    readonly TurretManning _turretManning = new();

    /// <summary>
    /// The candidates of a turret's target choice (one list per thread, reused).
    /// </summary>
    [ThreadStatic]
    static List<int>? s_turretCandidates;

    #region Turret state

    /// <summary>
    /// The turret guns by BUILDING id (null: no turret fires; <see cref="TurretManning.TurretGuns"/>).
    /// </summary>
    public TurretCatalog? TurretGuns
    {
        get => _turretManning.TurretGuns;
        set => _turretManning.TurretGuns = value;
    }

    /// <summary>
    /// The towns' buildings, for the wall a town turret is mounted on (null: no mount bonus; <see cref="TurretManning.Towns"/>).
    /// </summary>
    public PopulationData? Towns
    {
        get => _turretManning.Towns;
        set => _turretManning.Towns = value;
    }

    /// <summary>
    /// The turret a character mans as of the last tick (<see cref="TurretManning.TurretOf"/>).
    /// </summary>
    public TurretView? TurretOf(string? key) => _turretManning.TurretOf(key);

    /// <summary>
    /// The gun numbers of a turret building for its panel (<see cref="TurretManning.TurretPanelOf"/>).
    /// </summary>
    public TurretPanelView? TurretPanelOf(World world, string? instance) => _turretManning.TurretPanelOf(world, instance);

    /// <summary>
    /// Writes the loaded harpoons of every turret manned since loading (<see cref="TurretManning.WriteTurretShots"/>).
    /// </summary>
    public void WriteTurretShots(World world, IReadOnlyDictionary<string, Data.Fcs.FcsRecord> buildings) =>
        _turretManning.WriteTurretShots(world, buildings);

    #endregion

    #region Turret fire

    /// <summary>
    /// The serial turret step (after the bolts fly): finds the gunners, then runs every manned turret in key order.
    /// </summary>
    void TickTurrets(World world)
    {
        if (TurretGuns is null) return;

        _turretManning.FindGunners(world);

        foreach (var t in _turretManning.States)
        {
            if (t.Manned)
            {
                TickTurret(world, t);
                continue;
            }

            if (t.Gunner < 0) continue;

            t.Gunner = -1;
            t.Target = -1;
            t.Reloading = false;
            t.ReloadLeft = 0;
            t.Worst = -99;
        }
    }

    /// <summary>
    /// One tick of a manned turret (the controller <c>FUN_140438c70</c> with a turret, its fire gate <c>FUN_14043aee0</c>, which the controller
    /// triggers every tick): choose the target, reload when empty (no ammunition: GUN_DATA has none), set the aim point while loaded, turn the parts,
    /// and with a target in range, the parts within 1 degree, the steady time held and the line along the barrel clear, fire. A turret never walks
    /// its gunner closer: out of range or blocked it only waits.
    /// </summary>
    void TickTurret(World world, TurretState t)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int g = t.Gunner;
        var cold = table.Cold(g)!;
        var gun = t.Gun.Gun;
        float dt = world.TickSeconds;

        int target = ChooseTurretTarget(world, t, g, cold);
        (t.Target, t.TargetGeneration) = target >= 0 ? (target, prev[target].Generation) : (-1, 0);
        (t.User, t.UserGeneration, t.UserSkill) = (g, prev[g].Generation, Eff(cold, StatsEnumerated.Turrets) * 0.01f);

        // The reload (FUN_140437ce0): a skill blend of Turrets and dexterity, XP for both while it runs.
        bool loaded = TurretReload(t, cold, dt);

        var (muzzle, _) = TurretRig.Barrel(t.Gun, t.Angles, t.Position, t.Yaw);
        if (loaded && target >= 0) t.Aim = AimPoint(muzzle, prev[target], gun.ShotSpeed);

        float rate = TurretFormulas.TurnRate(t.Efficiency, gun.AimSpeed, t.Gun.Damage * t.DamageMult);
        t.Worst = rate > 0 && t.Aim is { } aim && t.Gun.Parts.Count > 0
            ? TurretRig.Turn(t.Gun.Parts, t.Angles, t.Position, t.Yaw, t.Gun.Scale, aim, rate * dt)
            : -99;

        // Meitou fix: the gunner faces along the barrel as it turns (after the production and AI steps, which face it toward the turret as the
        // game does).
        if (GunnerFacesBarrel)
        {
            var (_, heading) = TurretRig.Barrel(t.Gun, t.Angles, t.Position, t.Yaw);
            if (heading.X * heading.X + heading.Z * heading.Z > 1e-6f) table.Next[g].Yaw = MathF.Atan2(heading.X, heading.Z);
        }

        if (target < 0 || !loaded) return;

        var at = prev[g].Position;
        float h = RangedFormulas.HeightFactor(at.Y, prev[target].Position.Y);
        if (!RangedFormulas.InRange(Vector3.DistanceSquared(at, prev[target].Position), gun.Range, h)) return;

        _xp.Continuous(cold.Stats!, cold.Race!, StatsEnumerated.Perception, dt, RangedFormulas.PerceptionXp);
        _xp.Continuous(cold.Stats!, cold.Race!, StatsEnumerated.Turrets, dt, TurretFormulas.TurretXp);

        // The fire decision (FUN_140437ad0): aimed within a degree, then the steady time, then the line along the barrel.
        if (t.Worst > TurretFormulas.AimedDegrees) return;

        t.Steady -= dt;
        if (t.Steady > 0) return;

        var (from, forward) = TurretRig.Barrel(t.Gun, t.Angles, t.Position, t.Yaw);
        float reach = Vector3.Distance(from, t.Aim ?? prev[target].Position);
        if (LineOfFire(world, g, target, from, from + forward * reach, cold.Faction, gun.Range) != 1) return;

        FireTurret(world, t, g, cold, target, from, forward);
        t.Loaded--;
        t.Shots++;
        t.Steady = RangedFormulas.SteadyAim;
    }

    /// <summary>
    /// The reload (<c>FUN_140437ce0</c>): an empty gun starts one of clamp(lerp(s, reload max, reload min)) seconds, s = 0.01 (0.6 Turrets + 0.4
    /// dexterity) (<see cref="RangedFormulas.SkillBlend"/>); while it runs the gunner gains Turrets and dexterity XP; at its end the gun holds <c>num
    /// shots</c> and the aim is steadied again. True when the gun is loaded and not reloading.
    /// </summary>
    bool TurretReload(TurretState t, CharacterCold cold, float dt)
    {
        var gun = t.Gun.Gun;
        if (!t.Reloading)
        {
            if (t.Loaded > 0) return true;

            float s = RangedFormulas.SkillBlend(Eff(cold, StatsEnumerated.Turrets), Eff(cold, StatsEnumerated.Dexterity),
                cold.Fighter?.DexterityMult ?? 1);
            t.ReloadTime = RangedFormulas.ReloadTime(s, gun.ReloadTimeMin, gun.ReloadTimeMax);
            t.ReloadLeft = t.ReloadTime;
            t.Reloading = true;
            return false;
        }

        t.ReloadLeft -= dt;
        if (t.ReloadLeft <= 0)
        {
            t.ReloadLeft = 0;
            t.Reloading = false;
            t.Loaded = Math.Max(gun.NumShots, 1);
            t.Steady = RangedFormulas.SteadyAim;
        }

        _xp.Continuous(cold.Stats!, cold.Race!, StatsEnumerated.Turrets, dt, TurretFormulas.TurretXp);
        _xp.Continuous(cold.Stats!, cold.Race!, StatsEnumerated.Dexterity, dt, RangedFormulas.DexterityReloadXp);
        return false;
    }

    /// <summary>
    /// The state of the target choice's grid query.
    /// </summary>
    struct TurretScan
    {
        /// <summary>
        /// The characters the query reads.
        /// </summary>
        public CharacterTable Table;
        /// <summary>
        /// The slots found within the query's radius.
        /// </summary>
        public List<int> Found;
    }

    /// <summary>
    /// Target <paramref name="o"/> is inside a building other than the turret's or on another floor of it (<see cref="TurretFormulas.Arc"/>'s 0
    /// case). Engine reading: the turret's building and floor are its gunner's (<see cref="SightMap.BuildingOf"/>, within
    /// <see cref="SameFloorHeight"/>).
    /// </summary>
    static bool Indoors(World world, int g, int o)
    {
        if (world.Sight.BuildingOf(o) is not { } building) return false;

        var prev = world.Characters.Previous;
        return world.Sight.BuildingOf(g) != building || MathF.Abs(prev[g].Position.Y - prev[o].Position.Y) > SameFloorHeight;
    }

    /// <summary>
    /// The target choice (<c>FUN_140437f40</c>, <c>FUN_1404374d0</c>, the score <c>FUN_140435d70</c>): every candidate within 1.5 x range of the
    /// gunner scores <see cref="TurretFormulas.Closeness"/> x <see cref="TurretFormulas.Arc"/>, x 1.5 when it fights the gunner, + 0.1 for the
    /// current target, and the current target's score is x 1.5 again in the pass; the best above 1e-6 wins, else the current one is kept. The first
    /// pass takes the gunner's enemies (the faction relations, or anyone fighting it). With none, an NPC gunner's turret takes characters of
    /// default-hostile factions (own default relation below -30) and anyone fighting a member of the gunner's faction. Not targets: the dead, the
    /// carried, the caged; a target down scores 0 for an NPC gunner and x 0.01 for the player's. <b>Engine choices</b>: every candidate counts as
    /// seen (the original's sense list and its seen flag), the 1.5 for "fights the gunner" (<c>FUN_1405ca6f0</c>), the candidate radius. A player
    /// gunner with Shoot first then takes the neutrals of <see cref="SquadAiRules.ShootsFirstAt"/>.
    /// </summary>
    int ChooseTurretTarget(World world, TurretState t, int g, CharacterCold gc)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var at = prev[g].Position;
        float range = t.Gun.Gun.Range;
        int current = t.Target >= 0 && t.Target < prev.Length && prev[t.Target].Alive && prev[t.Target].Generation == t.TargetGeneration
            ? t.Target : -1;

        int kept = KeptTurretOrder(world, g, gc);
        if (kept >= 0) return kept;

        var found = TurretCandidates(world, at, range);
        var facing = new Vector3(MathF.Sin(t.Yaw), 0, MathF.Cos(t.Yaw));
        int generation = prev[g].Generation;
        var choice = new TurretChoice(world, _read, t, g, gc, at, range, facing, generation, found);

        // Shoot At (FUN_140437f40, Verified): the ordered target is kept while it scores above 0 (closeness x arc: in range and not blocked) and the
        // gunner does not regard it as friendly; otherwise the order is dropped and the gunner chooses as it always does. A target out of range still
        // scores 0.001 (kept, never fired at); one blocked (inside elsewhere) scores 0.
        if (!gc.TurretTarget.IsNone)
        {
            var talk = world.SystemOf<Dialogue.DialogueSystem>();
            if (table.TryResolveNext(gc.TurretTarget, out int ordered) && prev[ordered].Alive
                && choice.Score(ordered, TurretFormulas.FullRange, current) > 0 && (talk is null || !talk.RegardsFriendly(world, g, ordered)))
                return ordered;

            gc.TurretTarget = CharacterId.None;
        }

        // The current target is dropped when it no longer scores.
        if (current >= 0 && choice.Score(current, TurretFormulas.FullRange, current) <= 0) current = -1;

        int best = choice.BestEnemy(Relations, current, out float bestScore);
        if (best < 0) best = current;
        if (best >= 0 || Relations is null) return best;

        if (gc.IsPlayer) return choice.ShootFirst(Relations, current, best, bestScore);

        return choice.HostileOrAttacker(Relations, current, best, bestScore);
    }

    /// <summary>
    /// Shoot At (<c>FUN_140437f40</c>, <b>Verified</b>): the ordered target is kept while it lives, is free, is not down and the gunner does not
    /// regard it as friendly; otherwise the order is dropped and the gunner chooses as it always does (row 235 list A state 7, which counts a
    /// crawler; out of range or blocked, the turret waits). Returns the ordered target kept, else -1.
    /// </summary>
    static int KeptTurretOrder(World world, int g, CharacterCold gc)
    {
        if (gc.TurretTarget.IsNone) return -1;

        var table = world.Characters;
        var prev = table.Previous;
        var talk = world.SystemOf<Dialogue.DialogueSystem>();
        if (table.TryResolveNext(gc.TurretTarget, out int ordered) && prev[ordered].Alive && !ScoresZero(world, gc, table.Cold(ordered))
            && !CharacterFacts.TargetDown(table.Cold(ordered)) && (talk is null || !talk.RegardsFriendly(world, g, ordered)))
            return ordered;   // row 235 ends on state 7 (isTargetDown: crawling too)

        gc.TurretTarget = CharacterId.None;
        return -1;
    }

    /// <summary>
    /// The characters within 1.5 x <paramref name="range"/> of the gunner at <paramref name="at"/>, in slot order (the thread's reused list).
    /// </summary>
    static List<int> TurretCandidates(World world, Vector3 at, float range)
    {
        var found = s_turretCandidates ??= [];
        found.Clear();
        var scan = new TurretScan { Table = world.Characters, Found = found };
        world.Grid.Query(at.X, at.Z, range * 1.5f, ref scan, static (int other, ref TurretScan st) => st.Found.Add(other));
        found.Sort();

        return found;
    }

    /// <summary>
    /// One turret's choice among its <paramref name="found"/> candidates (<see cref="ChooseTurretTarget"/>): gunner <paramref name="g"/>
    /// (<paramref name="gc"/>, of <paramref name="generation"/>) at <paramref name="at"/>, the turret <paramref name="t"/> facing
    /// <paramref name="facing"/> with the gun's <paramref name="range"/>, and <paramref name="read"/> the last tick's combat slots.
    /// </summary>
    readonly struct TurretChoice(World world, CombatSlot[] read, TurretState t, int g, CharacterCold gc, Vector3 at, float range, Vector3 facing,
        int generation, List<int> found)
    {
        /// <summary>
        /// The score of candidate <paramref name="o"/> (<c>FUN_140435d70</c>) at <paramref name="rangeFactor"/> x the range, + 0.1 when it is the
        /// <paramref name="current"/> target.
        /// </summary>
        public float Score(int o, float rangeFactor, int current)
        {
            var table = world.Characters;
            var oc = table.Cold(o);
            if (ScoresZero(world, gc, oc)) return 0;

            var med = oc!.Medical!;
            var there = table.Previous[o].Position;
            float distance = Vector3.Distance(at, there);

            // x0.1 for a crippled target and x0.1 more for one not upright (FUN_140435d70, Verified; "crippled" is the engine reading of vfunc 0x360:
            // crawling or unable to attack).
            float f = (med.Crawling || oc.Fighter is { CanAttack: false } ? 0.1f : 1) * (med.Prone ? 0.1f : 1);
            float s = TurretFormulas.Closeness(distance, range, rangeFactor, f);
            if (distance >= rangeFactor * range) return s;

            s *= TurretFormulas.Arc(facing, there - t.Position, Indoors(world, g, o));
            if (s <= 0) return 0;

            if (read[o].TargetSlot == g && read[o].TargetGeneration == generation) s *= 1.5f;
            if (o == current) s += 0.1f;

            return s;
        }

        /// <summary>
        /// The first pass: the gunner's enemies (the faction relations, or anyone fighting it; another faction without relations), the current target
        /// x 1.5 again. Returns the best above <see cref="TurretFormulas.MinScore"/>, -1 for none, and its score.
        /// </summary>
        public int BestEnemy(FactionRelations? relations, int current, out float bestScore)
        {
            var table = world.Characters;
            var prev = table.Previous;
            int best = -1;
            bestScore = TurretFormulas.MinScore;
            foreach (int o in found)
            {
                if (o == g || !prev[o].Alive) continue;

                var oc = table.Cold(o);
                if (oc is null) continue;

                bool enemy = read[o].TargetSlot == g && read[o].TargetGeneration == generation
                    || (relations is not null ? AggroSystem.IsEnemy(relations, gc.Faction, oc.Faction) : oc.Faction != gc.Faction);
                if (!enemy) continue;

                float s = Score(o, TurretFormulas.FullRange, current);
                if (o == current) s *= 1.5f;
                if (s > bestScore) (best, bestScore) = (o, s);
            }

            return best;
        }

        /// <summary>
        /// Shoot first (the squad AI option): with no enemy in its sights a player gunner also takes dangerous neutrals, scored at range factor 0.6
        /// (no extra weight).
        /// </summary>
        public int ShootFirst(FactionRelations relations, int current, int best, float bestScore)
        {
            if ((world.Player.SquadAi & SquadAi.ShootFirst) == 0) return best;

            var table = world.Characters;
            var prev = table.Previous;
            foreach (int o in found)
            {
                if (o == g || !prev[o].Alive || table.Cold(o) is not { } oc || !relations.Contains(oc.Faction)) continue;

                var faction = relations.Factions[oc.Faction];
                if (!SquadAiRules.ShootsFirstAt(world.Player.SquadAi, (int)faction.FundamentalType, faction.DefaultRelation)) continue;

                float s = Score(o, TurretFormulas.ShootFirstRange, current);
                if (s > bestScore) (best, bestScore) = (o, s);
            }

            return best;
        }

        /// <summary>
        /// An NPC gunner's pass with no enemy: characters of default-hostile factions (own default relation below -30) and anyone fighting a member
        /// of the gunner's faction.
        /// </summary>
        public int HostileOrAttacker(FactionRelations relations, int current, int best, float bestScore)
        {
            var table = world.Characters;
            var prev = table.Previous;
            foreach (int o in found)
            {
                if (o == g || !prev[o].Alive || table.Cold(o) is not { } oc) continue;

                bool hostile = relations.Contains(oc.Faction) && relations.Factions[oc.Faction].DefaultRelation < FactionRelations.HostileThreshold;
                int their = read[o].TargetSlot;
                bool attacksFriend = their >= 0 && their < prev.Length && prev[their].Alive
                    && table.Cold(their) is { } victim && victim.Faction == gc.Faction;
                if (!hostile && !attacksFriend) continue;

                float s = Score(o, TurretFormulas.FullRange, current);
                if (s > bestScore) (best, bestScore) = (o, s);
            }

            return best;
        }
    }

    /// <summary>
    /// A turret's shot (<c>FUN_14043a730</c> with a <c>GunClassTurret</c>): from the muzzle along the barrel tilted by u x D degrees (D =
    /// <see cref="RangedFormulas.Deviation"/> of the gun's <c>accuracy deviation at 0 skill</c> with Turrets and perception), a harpoon of
    /// <see cref="TurretFormulas.Harpoon"/> with the height factor from the muzzle, the gunner's friendly-fire avoidance. The harpoon is the gunner's
    /// bolt: its hits, XP and crimes are the gunner's.
    /// </summary>
    void FireTurret(World world, TurretState t, int g, CharacterCold cold, int target, Vector3 muzzle, Vector3 forward)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var gun = t.Gun.Gun;
        ulong counter = (ulong)(uint)t.Shots << 3;
        float u = Rng.Float(Rng.Hash(world.Seed, t.Entity, RngPurpose.Ranged, counter));
        float spin = Rng.Float(Rng.Hash(world.Seed, t.Entity, RngPurpose.Ranged, counter + 1));
        float w = Rng.Float(Rng.Hash(world.Seed, t.Entity, RngPurpose.Ranged, counter + 2));
        float deviation = RangedFormulas.Deviation(gun.AccuracyDeviationAtZeroSkill, Eff(cold, StatsEnumerated.Turrets),
            Eff(cold, StatsEnumerated.Perception));
        var dir = RangedFormulas.Deviate(forward, u * deviation, spin);
        float hm = RangedFormulas.HeightFactor(muzzle.Y, prev[target].Position.Y) * t.Mount;
        var (lo, hi) = TurretFormulas.PierceEnds(t.Gun.Damage * t.DamageMult);
        var (damage, range, speed) = TurretFormulas.Harpoon(lo, hi, gun.Range, gun.ShotSpeed, TurretFormulas.PowerFactor(t.Efficiency), hm, w);
        _projectiles.Add(new Projectile
        {
            Id = _projectiles.NextId(),
            Shooter = g,
            ShooterGeneration = prev[g].Generation,
            Target = target,
            TargetGeneration = prev[target].Generation,
            ShooterFaction = cold.Faction,
            ShooterIsPlayer = cold.IsPlayer,
            ShooterSkill = Eff(cold, StatsEnumerated.Turrets),
            Position = muzzle,
            Direction = dir,
            Speed = speed,
            RangeLeft = range,
            Damage = damage,
            Avoidance = RangedFormulas.FriendlyAvoidance(Eff(cold, StatsEnumerated.FriendlyFire)),
            Passed = -1,
            Mesh = gun.LiveAmmoMesh,
        });
        ShotAt(world, target, cold.Faction);
        Sound(world, SoundKind.TurretShot, g, -1, muzzle);
    }

    /// <summary>
    /// A target every turret scores 0 (<c>FUN_140435d70</c>, <b>Verified</b>): down for every gunner (out, dead, playing dead, carried), in a bed or
    /// cage, and for a gunner not of the player's faction one that is not upright. An ordered target that scores 0 is dropped (<c>FUN_140437f40</c>).
    /// </summary>
    static bool ScoresZero(World world, CharacterCold gunner, CharacterCold? target) =>
        target?.Medical is not { } med || med.Incapacitated || med.PlayingDead || !target.CarriedBy.IsNone
        || target.InSomething != global::Meitou.Simulation.Locks.HeldState.None
        || !(gunner.IsPlayer || world.Player.Faction >= 0 && gunner.Faction == world.Player.Faction) && med.Prone;

    #endregion
}
