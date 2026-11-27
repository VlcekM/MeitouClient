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
/// the loaded and full shots, the share of the running reload still to go (0 while none runs), the GUN_DATA reload times and the gunner's Crossbows
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
    /// The share of the running reload still to go, 1 - gun <c>+0xe0</c> (0 while none runs: a finished reload leaves <c>+0xe0</c> at 1).
    /// </summary>
    public float ReloadLeft { get; init; }

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
    /// Whether another character than <paramref name="slot"/> mans the turret <paramref name="key"/> as of the serial turret step (its accept test,
    /// <c>FUN_1400f7c90</c>: a user already, or fewer users than its one operator).
    /// </summary>
    internal bool TurretTakenByOther(string key, int slot) => _turretManning.TryGet(key, out var t) && t.Manned && t.Gunner != slot;

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
    /// How near (units) the mount's own crossing must be to where the line met a building for that building to be the mount.
    /// </summary>
    const float MountTolerance = 0.01f;

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
    /// and with a target in range and not hidden in another building (<see cref="IndoorTargetsNeedSight"/>), the parts within 1 degree, the steady
    /// time held and the line along the barrel clear, fire. A turret never walks
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
        if (loaded && target >= 0) t.Aim = AimPoint(muzzle, prev[target], world.Characters.Cold(target), gun.ShotSpeed);

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

        if (target < 0 || !loaded || HidesIndoorTarget(world, g, target)) return;

        var at = prev[g].Position;
        float h = RangedFormulas.HeightFactor(at.Y, prev[target].Position.Y);
        if (!RangedFormulas.InRange(Vector3.DistanceSquared(at, prev[target].Position), gun.Range, h)) return;

        _xp.Continuous(cold.Stats!, cold.Race!, StatsEnumerated.Perception, dt, RangedFormulas.PerceptionXp, ExperienceSource.Turret);
        _xp.Continuous(cold.Stats!, cold.Race!, StatsEnumerated.Turrets, dt, TurretFormulas.TurretXp, ExperienceSource.Turret);

        // The fire decision (FUN_140437ad0): aimed within a degree, then the steady time, then the line along the barrel.
        if (t.Worst > TurretFormulas.AimedDegrees) return;

        t.Steady -= dt;
        if (t.Steady > 0) return;

        var (from, forward) = TurretRig.Barrel(t.Gun, t.Angles, t.Position, t.Yaw);
        float reach = Vector3.Distance(from, t.Aim ?? prev[target].Position);
        if (TurretLineOfFire(world, t, g, target, from, forward * reach, cold.Faction) != 1) return;

        FireTurret(world, t, g, cold, target, from, forward);
        t.Loaded--;
        t.Shots++;
        t.Steady = RangedFormulas.SteadyAim;
    }

    /// <summary>
    /// A turret's line of fire (<see cref="LineOfFire"/>) from <paramref name="from"/> along <paramref name="seg"/>: when the first thing it meets is
    /// the building the turret is mounted on (gun slot 11 <c>GunClassTurret::didThisTraceHitOurMountedBuilding</c>, <c>FUN_140438050</c>, in the
    /// ray request <c>FUN_140435b40</c>), the ray starts again from <see cref="TurretRig.RecastFrom"/> along the same direction
    /// (<c>FUN_140434750</c>) and what that one meets decides.
    /// </summary>
    int TurretLineOfFire(World world, TurretState t, int g, int target, Vector3 from, Vector3 seg, int faction)
    {
        float range = t.Gun.Gun.Range;
        var met = FirstMet(world, g, from, from + seg, range);
        if (met.Ready && met.Building && OnMount(t, met))
        {
            var again = TurretRig.RecastFrom(t.Gun, t.Angles, t.Position, t.Yaw);
            met = FirstMet(world, g, again, again + seg, range);
        }

        return FireDecision(world, g, target, faction, met);
    }

    /// <summary>
    /// Whether the building <paramref name="met"/> names is the one turret <paramref name="t"/> is mounted on: its own triangles
    /// (<see cref="TurretState.MountTriangles"/>, read on the first need) are crossed where the line met a building.
    /// </summary>
    bool OnMount(TurretState t, LineMet met)
    {
        if (t.MountedOn is not { } mount || Buildings is null) return false;

        if (t.MountTriangles is null)
        {
            t.MountTriangles = [];
            Buildings.BuildingTriangles(mount, t.Position, t.MountTriangles);
        }

        float at = SolidHits.FirstCrossing(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(t.MountTriangles), met.From, met.Seg);
        return at >= 0 && MathF.Abs(at - met.At) * met.Seg.Length() < MountTolerance;
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

            float s = RangedFormulas.SkillBlend(Eff(cold, StatsEnumerated.Turrets), Eff(cold, StatsEnumerated.Dexterity));
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

        _xp.Continuous(cold.Stats!, cold.Race!, StatsEnumerated.Turrets, dt, TurretFormulas.TurretXp, ExperienceSource.Turret);
        _xp.Continuous(cold.Stats!, cold.Race!, StatsEnumerated.Dexterity, dt, RangedFormulas.DexterityReloadXp, ExperienceSource.Turret);
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
    /// Whether shooter <paramref name="s"/> leaves target <paramref name="o"/> alone under <see cref="IndoorTargetsNeedSight"/>: the setting is on,
    /// the world has senses and <see cref="HiddenIndoors"/> holds for the shooter's.
    /// </summary>
    bool HidesIndoorTarget(World world, int s, int o)
    {
        if (!IndoorTargetsNeedSight || world.SystemOf<AggroSystem>() is not { } look) return false;

        return HiddenIndoors(world, s, o, look.SensesOf(s));
    }

    /// <summary>
    /// Target <paramref name="o"/> is inside a building other than shooter <paramref name="s"/>'s or on another floor of it (<see cref="Indoors"/>;
    /// not on top of one, <see cref="SightMap.BuildingOf"/>) and the shooter's <paramref name="senses"/> did not see it (<see cref="Unseen"/>).
    /// </summary>
    internal static bool HiddenIndoors(World world, int s, int o, SenseBlock? senses) =>
        Indoors(world, s, o) && Unseen(senses, new CharacterId(o, world.Characters.Previous[o].Generation));

    /// <summary>
    /// Whether <paramref name="senses"/> (null: no look yet) did not see <paramref name="who"/> at the last look.
    /// </summary>
    internal static bool Unseen(SenseBlock? senses, CharacterId who) => senses is null || !senses.TryGet(who, out var e) || !e.Seen;

    /// <summary>
    /// The target choice (<c>FUN_140437f40</c>, <c>FUN_1404374d0</c>, the score <c>FUN_140435d70</c>): the characters within 1.5 x range of the
    /// gunner that its senses hold (not imprisoned) score <see cref="TurretFormulas.Closeness"/> x <see cref="TurretFormulas.Arc"/>, x 1.5 when it
    /// fights the gunner, + 0.1 for the current target, x 0.1 when not seen at the last look, and the current target's score is x 1.5 again in the
    /// first pass; the best above 1e-6 wins, else the current one is kept. The first pass takes the entries tagged ENEMY (slot 125: a slave only when
    /// the gunner is hostile to its owner too). With none, an NPC gunner takes the NEUTRAL ones of default-hostile factions (own default relation
    /// below -30) or fighting a member of the gunner's faction, either only when it threatens (<see cref="AggroSystem.Threatens"/>,
    /// <c>FUN_14099b820</c>). Not targets: the dead, the carried, the caged; a target down scores 0 for an NPC gunner and x 0.01 for the player's.
    /// <b>Engine choices</b>: the 1.5 for "fights the gunner" (<c>FUN_1405ca6f0</c>), the candidate radius, the gunner's faction for the victim's
    /// Ownerships town; a world without senses takes the faction relations for the tags. A player gunner with Shoot first then takes the NEUTRAL
    /// ones of <see cref="SquadAiRules.ShootsFirstAt"/>.
    /// </summary>
    int ChooseTurretTarget(World world, TurretState t, int g, CharacterCold gc)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var at = prev[g].Position;
        float range = t.Gun.Gun.Range;
        int current = t.Target >= 0 && t.Target < prev.Length && prev[t.Target].Alive && prev[t.Target].Generation == t.TargetGeneration
            ? t.Target : -1;

        int kept = KeptTurretOrder(world, Towns, g, gc);
        if (kept >= 0) return kept;

        var found = TurretCandidates(world, at, range);
        var facing = new Vector3(MathF.Sin(t.Yaw), 0, MathF.Cos(t.Yaw));
        int generation = prev[g].Generation;
        var look = world.SystemOf<AggroSystem>();
        var senses = new TurretSenses(look, look?.SensesOf(g), this);
        var choice = new TurretChoice(world, Towns, _read, t, g, gc, at, range, facing, generation, found, senses);

        // Shoot At (FUN_140437f40, Verified): the ordered target is taken while it scores above 0 (closeness x arc: in range and not blocked) and the
        // gunner does not regard it as friendly; otherwise the gunner chooses as it always does this tick, the order going on. A target out of range
        // still scores 0.001 (kept, never fired at); one blocked (inside elsewhere) scores 0.
        var shootAt = ShootAt.TargetOf(gc);
        if (!shootAt.IsNone)
        {
            var talk = world.SystemOf<Dialogue.DialogueSystem>();
            if (table.TryResolveNext(shootAt, out int ordered) && prev[ordered].Alive
                && choice.Score(ordered, TurretFormulas.FullRange, current) > 0 && (talk is null || !talk.RegardsFriendly(world, g, ordered)))
                return ordered;
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
    /// Shoot At (<c>FUN_140437f40</c>, <b>Verified</b>): the target of the gunner's running order 235 (<see cref="ShootAt.TargetOf"/>) is kept while it
    /// lives, is free, is not down and the gunner does not regard it as friendly; otherwise the gunner chooses as it always does (row 235 list A state
    /// 7, which counts a crawler, ends the order; out of range or blocked, the turret waits). Returns the ordered target kept, else -1.
    /// </summary>
    static int KeptTurretOrder(World world, PopulationData? towns, int g, CharacterCold gc)
    {
        var shootAt = ShootAt.TargetOf(gc);
        if (shootAt.IsNone) return -1;

        var table = world.Characters;
        var prev = table.Previous;
        var talk = world.SystemOf<Dialogue.DialogueSystem>();
        if (table.TryResolveNext(shootAt, out int ordered) && prev[ordered].Alive && !ScoresZero(world, towns, gc, ordered)
            && !CharacterFacts.TargetDown(table.Cold(ordered)) && (talk is null || !talk.RegardsFriendly(world, g, ordered)))
            return ordered;

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
    /// <paramref name="facing"/> with the gun's <paramref name="range"/>, <paramref name="read"/> the last tick's combat slots and
    /// <paramref name="towns"/> the population data (the diplomats' biomes), <paramref name="senses"/> the gunner's.
    /// </summary>
    readonly struct TurretChoice(World world, PopulationData? towns, CombatSlot[] read, TurretState t, int g, CharacterCold gc, Vector3 at,
        float range, Vector3 facing, int generation, List<int> found, TurretSenses senses)
    {
        /// <summary>
        /// The score of candidate <paramref name="o"/> (<c>FUN_140435d70</c>) at <paramref name="rangeFactor"/> x the range, + 0.1 when it is the
        /// <paramref name="current"/> target.
        /// </summary>
        public float Score(int o, float rangeFactor, int current)
        {
            var table = world.Characters;
            var oc = table.Cold(o);
            if (ScoresZero(world, towns, gc, o) || senses.HidesIndoors(world, g, o)) return 0;

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
        /// The first pass: the entries the gunner's senses tag ENEMY (without senses: the faction relations, or anyone fighting it; another faction
        /// without relations), the current target x 1.5 again. Returns the best above <see cref="TurretFormulas.MinScore"/>, -1 for none, and its
        /// score.
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

                if (!senses.Holds(table, o, SenseTag.Enemy, out float seen) || !senses.Sensing && !FactionEnemy(relations, o, oc)) continue;

                float s = Score(o, TurretFormulas.FullRange, current) * seen;
                if (o == current) s *= 1.5f;
                if (s > bestScore) (best, bestScore) = (o, s);
            }

            return best;
        }

        /// <summary>
        /// The first pass's test without senses: one fighting the gunner, else the faction relations (another faction without them).
        /// </summary>
        bool FactionEnemy(FactionRelations? relations, int o, CharacterCold oc) =>
            read[o].TargetSlot == g && read[o].TargetGeneration == generation
            || (relations is not null ? AggroSystem.IsEnemy(relations, gc.Faction, oc.Faction) : oc.Faction != gc.Faction);

        /// <summary>
        /// Shoot first (the squad AI option): with no enemy in its sights a player gunner also takes dangerous neutrals (entries tagged NEUTRAL),
        /// scored at range factor 0.6.
        /// </summary>
        public int ShootFirst(FactionRelations relations, int current, int best, float bestScore)
        {
            var options = SquadAiRules.For(world, gc);
            if ((options & SquadAi.ShootFirst) == 0) return best;

            var table = world.Characters;
            var prev = table.Previous;
            foreach (int o in found)
            {
                if (o == g || !prev[o].Alive || table.Cold(o) is not { } oc || !relations.Contains(oc.Faction)) continue;
                if (!senses.Holds(table, o, SenseTag.Neutral, out float seen)) continue;

                var faction = relations.Factions[oc.Faction];
                if (!SquadAiRules.ShootsFirstAt(options, (int)faction.FundamentalType, faction.DefaultRelation)) continue;

                float s = Score(o, TurretFormulas.ShootFirstRange, current) * seen;
                if (s > bestScore) (best, bestScore) = (o, s);
            }

            return best;
        }

        /// <summary>
        /// An NPC gunner's pass with no enemy (<c>FUN_1404374d0</c>'s last, <b>Verified</b>): the entries tagged NEUTRAL of default-hostile factions
        /// (own default relation below -30) or fighting a member of the gunner's faction, either only when it threatens
        /// (<see cref="AggroSystem.Threatens"/>): so a slave led past the turret is spared, whatever its faction.
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
                if (!hostile && !attacksFriend || !senses.Holds(table, o, SenseTag.Neutral, out float seen)) continue;
                if (!senses.Threatens(world, gc, oc, o)) continue;

                float s = Score(o, TurretFormulas.FullRange, current) * seen;
                if (s > bestScore) (best, bestScore) = (o, s);
            }

            return best;
        }
    }

    /// <summary>
    /// The gunner's senses as its turret's target choice reads them (<c>FUN_140853960</c> over the gunner's entries, <b>Verified</b>): its
    /// <paramref name="look"/> system (null: the world has no senses, every candidate passes), its <paramref name="block"/> (null: no look yet, none
    /// passes) and the <paramref name="fights"/>.
    /// </summary>
    readonly struct TurretSenses(AggroSystem? look, SenseBlock? block, CombatSystem fights)
    {
        /// <summary>
        /// The weight of a candidate the last look did not see (<c>DAT_14167fad0</c>).
        /// </summary>
        const float Unseen = 0.1f;

        /// <summary>
        /// Whether this world has senses (an <see cref="AggroSystem"/>).
        /// </summary>
        public bool Sensing => look is not null;

        /// <summary>
        /// Whether the gunner's senses hold <paramref name="o"/> with a tag of <paramref name="want"/> and not imprisoned (the iterator's masks: the
        /// dead are not candidates either), and <paramref name="seen"/>, the score's weight: 1 when the last look saw it, else 0.1.
        /// </summary>
        public bool Holds(CharacterTable table, int o, SenseTag want, out float seen)
        {
            seen = 1;
            if (look is null) return true;
            if (block is null || !block.TryGet(new CharacterId(o, table.Previous[o].Generation), out var e)) return false;
            if ((e.Tags & want) == 0 || (e.Tags & SenseTag.Imprisoned) != 0) return false;

            if (!e.Seen) seen = Unseen;
            return true;
        }

        /// <summary>
        /// Whether the gunner <paramref name="g"/> leaves <paramref name="o"/> alone (<see cref="IndoorTargetsNeedSight"/>, <see cref="HiddenIndoors"/>).
        /// </summary>
        public bool HidesIndoors(World world, int g, int o) => fights.IndoorTargetsNeedSight && look is not null && HiddenIndoors(world, g, o, block);

        /// <summary>
        /// <see cref="AggroSystem.Threatens"/> of the gunner <paramref name="gc"/> on <paramref name="oc"/> in <paramref name="o"/>; without senses,
        /// true.
        /// </summary>
        public bool Threatens(World world, CharacterCold gc, CharacterCold oc, int o)
        {
            if (look is null) return true;

            return look.Threatens(world, fights, gc, oc, o, world.Characters.Previous[o].Generation);
        }
    }

    /// <summary>
    /// A turret's shot (<c>FUN_14043a730</c> with a <c>GunClassTurret</c>): from the muzzle along the barrel tilted by u x D degrees (D =
    /// <see cref="RangedFormulas.Deviation"/> of the gun's <c>accuracy deviation at 0 skill</c> with Turrets and perception), a harpoon of
    /// <see cref="TurretFormulas.Harpoon"/> with the height factor from the muzzle, the gunner's friendly-fire avoidance. The harpoon is the gunner's
    /// bolt: its hits, XP and crimes are the gunner's (a player gunner's Assault: <see cref="TurretAssault"/>).
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
            Turret = true,
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
        Sound(world, SoundKind.TurretShot, g, -1, muzzle, shootSound: gun.ShootSound);
    }

    /// <summary>
    /// A target every turret scores 0 (<c>FUN_140435d70</c>, <b>Verified</b>): down for every gunner (out, dead, playing dead, carried), in a bed or
    /// cage, for a gunner not of the player's faction one that is not upright, and a diplomat that intends no aggression
    /// (<see cref="SparedDiplomat"/>). An ordered target that scores 0 is dropped (<c>FUN_140437f40</c>).
    /// </summary>
    static bool ScoresZero(World world, PopulationData? towns, CharacterCold gunner, int target)
    {
        var tc = world.Characters.Cold(target);
        if (tc?.Medical is not { } med || med.Incapacitated || med.PlayingDead || !tc.CarriedBy.IsNone) return true;
        if (tc.InSomething != global::Meitou.Simulation.Locks.HeldState.None) return true;
        if (!(gunner.IsPlayer || world.Player.Faction >= 0 && gunner.Faction == world.Player.Faction) && med.Prone) return true;

        return SparedDiplomat(world, towns, gunner, target, tc);
    }

    /// <summary>
    /// The turret score's own diplomat test (<c>FUN_140435d70</c>, <b>Verified</b> by decompile): a target with diplomatic status
    /// (<c>FUN_140796240</c>, the biome factored in for a gunner not with the player, <c>FUN_140856bc0</c>;
    /// <see cref="CharacterFacts.DiplomaticStatus"/>) scores 0 unless its intended aggression (vfunc <c>0x1e8</c>, its current goal's,
    /// <see cref="Ai.TaskAggression.OfGoal"/>) is above 0.
    /// </summary>
    static bool SparedDiplomat(World world, PopulationData? towns, CharacterCold gunner, int target, CharacterCold tc)
    {
        var at = world.Characters.Previous[target].Position;
        bool withPlayer = Aggro.AggroSystem.PlayerSide(world, gunner);

        return CharacterFacts.DiplomaticStatus(tc, at, towns, !withPlayer) && Ai.TaskAggression.OfGoal(tc) <= 0;
    }

    #endregion
}
