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
/// A manned turret as the snapshot shows it: its BUILDING and GUN_DATA records, the point it aims at (null: none yet) and the turn of its parts (radians,
/// <c>TurretGun.Parts</c> order).
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
public readonly record struct TurretPanelView(string Instance, float Range, float AimSpeed, float Deviation, float Mount, float Height, float UserSkill, float? Perception)
{
    public int? HeightMetres { get; init; }

    public int Loaded { get; init; }

    public int Shots { get; init; } = 1;

    public float ReloadLeft { get; init; } = 1;

    public float ReloadMin { get; init; }

    public float ReloadMax { get; init; }

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
    /// The GAMESTATE_BUILDING int that holds a turret gun's loaded harpoons (<c>GunClass::serialise</c> <c>FUN_1404390a0</c>, called by
    /// <c>TurretBuilding::serialise</c>).
    /// </summary>
    public const string ShotsKey = "current shots";

    readonly SortedDictionary<string, TurretState> _turrets = new(StringComparer.Ordinal);

    [ThreadStatic]
    static List<int>? s_turretCandidates;

    #region Turret state

    /// <summary>
    /// The turret guns by BUILDING id (null: no turret fires).
    /// </summary>
    public TurretCatalog? TurretGuns { get; set; }

    /// <summary>
    /// The towns' buildings, for the wall a town turret is mounted on (null: no mount bonus).
    /// </summary>
    public PopulationData? Towns { get; set; }

    /// <summary>
    /// One turret building's gun state (the original's <c>GunClassTurret</c> and <c>TurretBuilding</c> fields, and its gunner's controller fields while one mans it).
    /// </summary>
    sealed class TurretState(string key, TurretGun gun, Vector3 position, float yaw, float mount)
    {
        public readonly string Key = key;
        public readonly TurretGun Gun = gun;
        public readonly Vector3 Position = position;
        public readonly float Yaw = yaw;

        /// <summary>
        /// The <see cref="TurretFormulas.MountFactor"/> of the wall it stands on.
        /// </summary>
        public readonly float Mount = mount;

        public readonly ulong Entity = Rng.StableHash(key) ^ 0x5475727265744755UL;
        public readonly float[] Angles = new float[gun.Parts.Count];

        /// <summary>
        /// The aim point (gun slot 5, building <c>+0x4a8</c>), set while the gun is loaded and has a target; it stays when the target goes.
        /// </summary>
        public Vector3? Aim;

        /// <summary>
        /// The largest error its parts still had to turn this tick, degrees (<c>+0x448</c>; -99 when it did not turn).
        /// </summary>
        public float Worst = -99;

        /// <summary>
        /// Harpoons loaded (the gun's <c>+0x20</c>, "current shots"; a new gun starts empty) and fired (part of the roll keys).
        /// </summary>
        public int Loaded, Shots;

        /// <summary>
        /// The gunner's controller: reloading, its time left and length, the steady-aim timer.
        /// </summary>
        public bool Reloading;
        public float ReloadLeft, ReloadTime, Steady = RangedFormulas.SteadyAim;
        public int Gunner = -1, GunnerGeneration, Target = -1, TargetGeneration;

        /// <summary>
        /// This tick's gunner efficiency e (health x power grant) and research damage multiplier.
        /// </summary>
        public float Efficiency = 1, DamageMult = 1;
        public bool Manned;

        /// <summary>
        /// The last gunner (the gun's user hand <c>+0x30</c>) and its Turrets level x 0.01 (<c>+0x2c</c>), both set by gun slot 16
        /// <c>FUN_140446ef0</c> every frame the reload step runs and on each shot; kept when the gunner leaves. Only the panel reads them (not
        /// hashed; the original does not save them).
        /// </summary>
        public int User = -1, UserGeneration;
        public float UserSkill;

        public void Hash(ref StateHasher h)
        {
            h.Add(Rng.StableHash(Key));
            foreach (var a in Angles) h.Add(a);
            h.Add(Aim ?? new Vector3(float.NaN));
            h.Add(Worst);
            h.Add(Loaded);
            h.Add(Shots);
            h.Add(Reloading);
            h.Add(ReloadLeft);
            h.Add(ReloadTime);
            h.Add(Steady);
            h.Add(Gunner);
            h.Add(GunnerGeneration);
            h.Add(Target);
            h.Add(TargetGeneration);
        }
    }

    /// <summary>
    /// The turret a character mans as of the last tick (its <see cref="CharacterCold.Gunning"/> key), for the snapshot; null when it mans none.
    /// </summary>
    public TurretView? TurretOf(string? key) =>
        key is not null && _turrets.TryGetValue(key, out var t)
            ? new TurretView(t.Gun.BuildingId, t.Gun.Gun.StringId, t.Position, t.Yaw, t.Aim, [.. t.Angles])
            : null;

    /// <summary>
    /// The gun numbers of the turret building <paramref name="instance"/> for its panel (<see cref="TurretPanelView"/>); a turret nobody has manned
    /// yet has the gun's fresh state. Null for a building that is not a turret.
    /// </summary>
    public TurretPanelView? TurretPanelOf(World world, string? instance)
    {
        if (instance is null || TurretGuns is not { } catalog || world.Buildings.Find(instance) is not { } site) return null;
        if (catalog.Get(site.Record) is not { } gun) return null;

        var g = gun.Gun;
        if (!_turrets.TryGetValue(instance, out var t))
            return new(instance, g.Range, g.AimSpeed, g.AccuracyDeviationAtZeroSkill, 1, 1, 0, null)
            {
                Shots = g.NumShots,
                ReloadMin = g.ReloadTimeMin,
                ReloadMax = g.ReloadTimeMax,
            };

        float? perception = null, crossbows = null;
        if (t.User >= 0 && t.User < world.Characters.HighWater && world.Characters.Previous[t.User].Generation == t.UserGeneration
            && world.Characters.Cold(t.User) is { Stats: not null, Medical: not null } user)
        {
            perception = Eff(user, StatsEnumerated.Perception);
            crossbows = user.Stats![StatsEnumerated.Crossbows];
        }

        float height = t.Aim is { } aim ? RangedFormulas.HeightFactor(t.Position.Y, aim.Y) : 1;
        return new(instance, g.Range, g.AimSpeed, g.AccuracyDeviationAtZeroSkill, t.Mount, height, t.UserSkill, perception)
        {
            HeightMetres = t.Aim is { } at ? (int)((site.Position.Y - at.Y) * 0.1f) : null,
            Loaded = t.Loaded,
            Shots = g.NumShots,
            ReloadLeft = t.Reloading && t.ReloadTime > 0 ? t.ReloadLeft / t.ReloadTime : 1,
            ReloadMin = g.ReloadTimeMin,
            ReloadMax = g.ReloadTimeMax,
            Crossbows = crossbows,
        };
    }

    /// <summary>
    /// The saved-state key of a turret: a player building's zone and id, a town building's own key.
    /// </summary>
    static string SaveKeyOf(World world, string key) => world.Buildings.Find(key) is { } site ? BuildingKey.Of(site.Zone, site.Id) : key;

    /// <summary>
    /// The harpoons a turret first manned since loading starts with: its building's saved <see cref="ShotsKey"/> capped at the gun's shots (<c>GunClass::loadFromSerialise</c>
    /// <c>FUN_140439140</c>: min(saved, <c>+0x1c</c>); a missing key reads 0). Meitou's cap is the reload's <c>max(num shots, 1)</c> and it floors at 0.
    /// </summary>
    static int SavedShots(World world, string key, TurretGun gun) =>
        world.Fixtures.Saved?.GetValueOrDefault(SaveKeyOf(world, key)) is { } state && state.Ints.TryGetValue(ShotsKey, out int shots)
            ? Math.Clamp(shots, 0, Math.Max(gun.Gun.NumShots, 1))
            : 0;

    /// <summary>
    /// Writes the loaded harpoons of every turret manned since loading into its building's state (<paramref name="buildings"/>, by
    /// <see cref="BuildingKey"/>); the others keep the loaded record's value. The reload, aim and turn are not saved, as in the original.
    /// </summary>
    public void WriteTurretShots(World world, IReadOnlyDictionary<string, Data.Fcs.FcsRecord> buildings)
    {
        foreach (var t in _turrets.Values)
            if (buildings.TryGetValue(SaveKeyOf(world, t.Key), out var state)) state.Ints[ShotsKey] = t.Loaded;
    }

    void HashTurrets(ref StateHasher h)
    {
        // Only once a turret has been manned, so worlds without gunners hash as before.
        if (_turrets.Count == 0) return;

        h.Add(_turrets.Count);
        foreach (var t in _turrets.Values) t.Hash(ref h);
    }

    #endregion

    #region Manning

    /// <summary>
    /// A gunner's turret this tick: its key, BUILDING, place, facing, mount bonus, efficiency and research damage multiplier.
    /// </summary>
    readonly record struct Post(string Key, string BuildingId, Vector3 Position, float Yaw, float Mount, float Efficiency, float DamageMult);

    /// <summary>
    /// The turret a character mans now, or null. Any gunner must be awake, free (not fighting, carrying, carried or caged) and have a working arm (task 146's
    /// <c>hasOneWorkingArm</c>). A player character operates a turret of its own (<see cref="CharacterTask.Operate"/> at the site,
    /// <see cref="Production.ProductionSystem"/>); its efficiency is the machine's power grant (0 unpowered) and its damage takes the research multiplier of the
    /// building. An NPC stands at the turret of its town its squad gave it (the AI's turret goals, <see cref="Ai.AiBehaviour.Turret"/>); <b>engine
    /// choice</b>: efficiency 1 (NPC towns have no power grid here).
    /// </summary>
    Post? Manning(World world, int i, CharacterCold cold)
    {
        if (TurretGuns is not { } catalog || !CanMan(cold)) return null;
        if (cold.IsPlayer) return PlayerPost(world, catalog, i, cold);

        return NpcPost(world, catalog, i, cold);
    }

    /// <summary>
    /// Whether a character is free to man a turret at all: a body, awake, not fighting, carrying or carried, not caged, with a working arm.
    /// </summary>
    static bool CanMan(CharacterCold cold) =>
        cold.Stats is not null && cold.Race is not null && cold.Medical is { Incapacitated: false } medical && !cold.InCombat && !cold.IsImprisoned
        && cold.CarriedBy.IsNone && cold.Carrying.IsNone && (medical.ArmWorks(left: false) || medical.ArmWorks(left: true));

    /// <summary>
    /// The post of a player character operating its own turret building (<see cref="Manning"/>).
    /// </summary>
    Post? PlayerPost(World world, TurretCatalog catalog, int i, CharacterCold cold)
    {
        if (world.Characters.Next[i].Task != (byte)CharacterTask.Operate || !cold.Operating || cold.OperateSite is not { } id) return null;
        if (world.Buildings.Find(id) is not { Complete: true } site || catalog.Get(site.Record) is not { } gun) return null;

        // broken: manning is impossible (Task_UseTurret ends)
        if (world.Machines.Find(id) is { Broken: true }) return null;

        float e = 1;
        if (gun.IsConsumer) e = world.Machines.Find(id) is { } m ? m.HasPower ? m.Power : 0 : 0;

        return new Post(site.Id, site.Record, site.Position, TownBuildings.YawOf(site.Rotation), 1, e, world.Research.Upgrade(site.Record).ProductionMult);
    }

    /// <summary>
    /// The post of an NPC gunner standing at the turret its squad gave it (<see cref="Manning"/>).
    /// </summary>
    Post? NpcPost(World world, TurretCatalog catalog, int i, CharacterCold cold)
    {
        var n = world.Characters.Next[i];
        if (cold.Ai is not { Behaviour: Ai.AiBehaviour.Turret, Squad.Ai: { } board } || n.Task != (byte)CharacterTask.Idle || !Ai.AiSystem.Holds(n, cold))
            return null;
        if (!board.TurretClaims.TryGetValue(i, out int claimed) || (uint)claimed >= (uint)board.Turrets.Count) return null;
        if (board.Turrets[claimed] is not { Turret: { } post } building) return null;

        // A town turret is a TurretBuilding like the player's: its saved `broken` (FUN_1402a1750) stops manning as well. Nothing at run time breaks a
        // town's own turret (only a player owner may dismantle, slot 79; hits never damage machines, slot 62), so the zone state is its only source
        // (docs/game/buildings-production.md "Broken machines").
        if (world.Fixtures.Saved?.GetValueOrDefault(building.Key)?.Bools.GetValueOrDefault("broken") == true) return null;

        float reach = post.UseRange + Ai.AiSystem.TurretSlack;
        if (Vector2.DistanceSquared(new(n.Position.X, n.Position.Z), new(post.Position.X, post.Position.Z)) > reach * reach) return null;

        float mount = 1;
        if (post.MountedOn is { } on && Towns is { } towns && (uint)board.Town < (uint)towns.Sites.Count)
            foreach (var b in towns.Sites[board.Town].Buildings)
                if (b.Key == on)
                {
                    mount = TurretFormulas.MountFactor(catalog.OutputOf(b.BuildingId));
                    break;
                }

        return new Post(building.Key, building.BuildingId, post.Position, post.Yaw, mount, 1, 1);
    }

    /// <summary>
    /// The serial turret step (after the bolts fly): finds the gunners, then runs every manned turret in key order.
    /// </summary>
    void TickTurrets(World world)
    {
        if (TurretGuns is null) return;

        var table = world.Characters;
        foreach (var t in _turrets.Values) t.Manned = false;
        for (int i = 0; i < table.HighWater; i++) FindGunner(world, table, i);

        foreach (var t in _turrets.Values)
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
    /// Sets the turret a character mans this tick (its <see cref="CharacterCold.Gunning"/>), creating the turret's state on its first manning.
    /// </summary>
    void FindGunner(World world, CharacterTable table, int i)
    {
        var cold = table.Cold(i);
        if (cold is null) return;

        cold.Gunning = null;
        if (!table.Next[i].Alive || Manning(world, i, cold) is not { } post)
        {
            cold.TurretTarget = CharacterId.None;
            return;
        }

        if (!_turrets.TryGetValue(post.Key, out var t))
        {
            if (TurretGuns!.Get(post.BuildingId) is not { } gun) return;

            _turrets[post.Key] = t = new TurretState(post.Key, gun, post.Position, post.Yaw, post.Mount);
            t.Loaded = SavedShots(world, post.Key, gun);
        }

        // One gunner a turret (max operators 1): the lowest slot.
        if (t.Manned) return;

        t.Manned = true;
        t.Efficiency = post.Efficiency;
        t.DamageMult = post.DamageMult;
        cold.Gunning = post.Key;

        // Manning sets Character +0x198 to Turrets (11) every update (Task_UseTurret FUN_1403474f0), an NPC gunner's too: the HUD's Current Skill line.
        cold.UseSkill(StatsEnumerated.Turrets, world.Tick);
        int generation = table.Previous[i].Generation;
        if (t.Gunner == i && t.GunnerGeneration == generation) return;

        // A new gunner takes over the gun as it is (its loaded harpoons stay); a reload in progress was the last gunner's (the controller's timer).
        (t.Gunner, t.GunnerGeneration) = (i, generation);
        t.Reloading = false;
        t.ReloadLeft = 0;
        t.Steady = RangedFormulas.SteadyAim;
        t.Target = -1;
    }

    #endregion

    #region Turret fire

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

        // Meitou fix: the gunner faces along the barrel as it turns (after the production and AI steps, which face it toward the turret as the game does).
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

            float s = RangedFormulas.SkillBlend(Eff(cold, StatsEnumerated.Turrets), Eff(cold, StatsEnumerated.Dexterity), cold.Fighter?.DexterityMult ?? 1);
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

    struct TurretScan
    {
        public CharacterTable Table;
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
    /// The target choice (<c>FUN_140437f40</c>, <c>FUN_1404374d0</c>, the score <c>FUN_140435d70</c>): every candidate within 1.5 x range of the gunner scores
    /// <see cref="TurretFormulas.Closeness"/> x <see cref="TurretFormulas.Arc"/>, x 1.5 when it fights the gunner, + 0.1 for the current target, and the current
    /// target's score is x 1.5 again in the pass; the best above 1e-6 wins, else the current one is kept. The first pass takes the gunner's enemies
    /// (the faction relations, or anyone fighting it). With none, an NPC gunner's turret takes characters of default-hostile factions (own default
    /// relation below -30) and anyone fighting a member of the gunner's faction. Not targets: the dead, the carried, the caged; a target down scores
    /// 0 for an NPC gunner and x 0.01 for the player's. <b>Engine choices</b>: every candidate counts as seen (the original's sense list and its seen
    /// flag), the 1.5 for "fights the gunner" (<c>FUN_1405ca6f0</c>), the candidate radius. A player gunner with Shoot first then takes the neutrals
    /// of <see cref="SquadAiRules.ShootsFirstAt"/>.
    /// </summary>
    int ChooseTurretTarget(World world, TurretState t, int g, CharacterCold gc)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var at = prev[g].Position;
        float range = t.Gun.Gun.Range;
        int current = t.Target >= 0 && t.Target < prev.Length && prev[t.Target].Alive && prev[t.Target].Generation == t.TargetGeneration ? t.Target : -1;

        // Shoot At (FUN_140437f40, Verified): the ordered target is kept while it lives, is free, is not down and the gunner does not regard it as friendly;
        // otherwise the order is dropped and the gunner chooses as it always does (row 235 list A state 7, which counts a crawler; out of range or blocked, the
        // turret waits).
        if (!gc.TurretTarget.IsNone)
        {
            var talk = world.SystemOf<Dialogue.DialogueSystem>();
            if (table.TryResolveNext(gc.TurretTarget, out int ordered) && prev[ordered].Alive && !ScoresZero(world, gc, table.Cold(ordered))
                && !CharacterFacts.TargetDown(table.Cold(ordered)) && (talk is null || !talk.RegardsFriendly(world, g, ordered)))
                return ordered;   // row 235 ends on state 7 (isTargetDown: crawling too)

            gc.TurretTarget = CharacterId.None;
        }

        var found = s_turretCandidates ??= [];
        found.Clear();
        var scan = new TurretScan { Table = table, Found = found };
        world.Grid.Query(at.X, at.Z, range * 1.5f, ref scan, static (int other, ref TurretScan st) => st.Found.Add(other));
        found.Sort();
        var facing = new Vector3(MathF.Sin(t.Yaw), 0, MathF.Cos(t.Yaw));
        int generation = prev[g].Generation;

        float Score(int o, float rangeFactor)
        {
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

            if (_read[o].TargetSlot == g && _read[o].TargetGeneration == generation) s *= 1.5f;
            if (o == current) s += 0.1f;

            return s;
        }

        // Shoot At (FUN_140437f40, Verified): the ordered target is kept while it scores above 0 (closeness x arc: in range and not blocked) and the
        // gunner does not regard it as friendly; otherwise the order is dropped and the gunner chooses as it always does. A target out of range still
        // scores 0.001 (kept, never fired at); one blocked (inside elsewhere) scores 0.
        if (!gc.TurretTarget.IsNone)
        {
            var talk = world.SystemOf<Dialogue.DialogueSystem>();
            if (table.TryResolveNext(gc.TurretTarget, out int ordered) && prev[ordered].Alive && Score(ordered, TurretFormulas.FullRange) > 0
                && (talk is null || !talk.RegardsFriendly(world, g, ordered)))
                return ordered;

            gc.TurretTarget = CharacterId.None;
        }

        // The current target is dropped when it no longer scores.
        if (current >= 0 && Score(current, TurretFormulas.FullRange) <= 0) current = -1;

        int best = -1;
        float bestScore = TurretFormulas.MinScore;
        foreach (int o in found)
        {
            if (o == g || !prev[o].Alive) continue;

            var oc = table.Cold(o);
            if (oc is null) continue;

            bool enemy = _read[o].TargetSlot == g && _read[o].TargetGeneration == generation
                || (Relations is not null ? AggroSystem.IsEnemy(Relations, gc.Faction, oc.Faction) : oc.Faction != gc.Faction);
            if (!enemy) continue;

            float s = Score(o, TurretFormulas.FullRange);
            if (o == current) s *= 1.5f;
            if (s > bestScore) (best, bestScore) = (o, s);
        }

        if (best < 0) best = current;
        if (best >= 0 || Relations is null) return best;

        if (gc.IsPlayer)
        {
            // Shoot first (the squad AI option): with no enemy in its sights a player gunner also takes dangerous neutrals, scored at range factor
            // 0.6 (no extra weight).
            if ((world.Player.SquadAi & SquadAi.ShootFirst) == 0) return best;

            foreach (int o in found)
            {
                if (o == g || !prev[o].Alive || table.Cold(o) is not { } oc || !Relations.Contains(oc.Faction)) continue;

                var faction = Relations.Factions[oc.Faction];
                if (!SquadAiRules.ShootsFirstAt(world.Player.SquadAi, (int)faction.FundamentalType, faction.DefaultRelation)) continue;

                float s = Score(o, TurretFormulas.ShootFirstRange);
                if (s > bestScore) (best, bestScore) = (o, s);
            }

            return best;
        }

        foreach (int o in found)
        {
            if (o == g || !prev[o].Alive || table.Cold(o) is not { } oc) continue;

            bool hostile = Relations.Contains(oc.Faction) && Relations.Factions[oc.Faction].DefaultRelation < FactionRelations.HostileThreshold;
            int their = _read[o].TargetSlot;
            bool attacksFriend = their >= 0 && their < prev.Length && prev[their].Alive && table.Cold(their) is { } victim && victim.Faction == gc.Faction;
            if (!hostile && !attacksFriend) continue;

            float s = Score(o, TurretFormulas.FullRange);
            if (s > bestScore) (best, bestScore) = (o, s);
        }

        return best;
    }

    /// <summary>
    /// A turret's shot (<c>FUN_14043a730</c> with a <c>GunClassTurret</c>): from the muzzle along the barrel tilted by u x D degrees (D =
    /// <see cref="RangedFormulas.Deviation"/> of the gun's <c>accuracy deviation at 0 skill</c> with Turrets and perception), a harpoon of
    /// <see cref="TurretFormulas.Harpoon"/> with the height factor from the muzzle, the gunner's friendly-fire avoidance. The harpoon is the gunner's bolt: its
    /// hits, XP and crimes are the gunner's.
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
        float deviation = RangedFormulas.Deviation(gun.AccuracyDeviationAtZeroSkill, Eff(cold, StatsEnumerated.Turrets), Eff(cold, StatsEnumerated.Perception));
        var dir = RangedFormulas.Deviate(forward, u * deviation, spin);
        float hm = RangedFormulas.HeightFactor(muzzle.Y, prev[target].Position.Y) * t.Mount;
        var (lo, hi) = TurretFormulas.PierceEnds(t.Gun.Damage * t.DamageMult);
        var (damage, range, speed) = TurretFormulas.Harpoon(lo, hi, gun.Range, gun.ShotSpeed, TurretFormulas.PowerFactor(t.Efficiency), hm, w);
        _projectiles.Add(new Projectile
        {
            Id = _nextProjectile++,
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
