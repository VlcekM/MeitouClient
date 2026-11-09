using System.Diagnostics.CodeAnalysis;
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
/// The turrets' manning (docs/game/combat.md "Turrets"): each turret building's gun state, kept from its first manning, and which character mans
/// which turret each tick (a player character operating its turret, an NPC standing at the turret its squad gave it). The firing is
/// <see cref="CombatSystem"/>'s (CombatSystem.Turrets.cs), which owns this.
/// </summary>
sealed class TurretManning
{
    /// <summary>
    /// The GAMESTATE_BUILDING int that holds a turret gun's loaded harpoons (<c>GunClass::serialise</c> <c>FUN_1404390a0</c>, called by
    /// <c>TurretBuilding::serialise</c>).
    /// </summary>
    public const string ShotsKey = "current shots";

    /// <summary>
    /// The gun state of every turret manned since loading, by key.
    /// </summary>
    readonly SortedDictionary<string, TurretState> _turrets = new(StringComparer.Ordinal);

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
    /// The turrets' states in key order (the firing's order).
    /// </summary>
    internal SortedDictionary<string, TurretState>.ValueCollection States => _turrets.Values;

    /// <summary>
    /// The state of the turret <paramref name="key"/>, when it has been manned since loading.
    /// </summary>
    internal bool TryGet(string key, [NotNullWhen(true)] out TurretState? t) => _turrets.TryGetValue(key, out t);

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
            perception = CombatSystem.Eff(user, StatsEnumerated.Perception);
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

    /// <summary>
    /// Adds every turret manned since loading, in key order.
    /// </summary>
    internal void Hash(ref StateHasher h)
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

        return new Post(site.Id, site.Record, site.Position, TownBuildings.YawOf(site.Rotation), 1, e,
            world.Research.Upgrade(site.Record).ProductionMult);
    }

    /// <summary>
    /// The post of an NPC gunner standing at the turret its squad gave it (<see cref="Manning"/>).
    /// </summary>
    Post? NpcPost(World world, TurretCatalog catalog, int i, CharacterCold cold)
    {
        var n = world.Characters.Next[i];
        if (cold.Ai is not { Behaviour: Ai.AiBehaviour.Turret, Squad.Ai: { } board } || n.Task != (byte)CharacterTask.Idle
            || !Ai.AiSystem.Holds(n, cold))
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
    /// Finds this tick's gunners: no turret is manned until a character mans it, in slot order.
    /// </summary>
    internal void FindGunners(World world)
    {
        var table = world.Characters;
        foreach (var t in _turrets.Values) t.Manned = false;
        for (int i = 0; i < table.HighWater; i++) FindGunner(world, table, i);
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
}

/// <summary>
/// One turret building's gun state (the original's <c>GunClassTurret</c> and <c>TurretBuilding</c> fields, and its gunner's controller fields while one mans it).
/// </summary>
internal sealed class TurretState(string key, TurretGun gun, Vector3 position, float yaw, float mount)
{
    /// <summary>
    /// The turret's key: its building instance id (a town turret's building key).
    /// </summary>
    public readonly string Key = key;

    /// <summary>
    /// The building's gun.
    /// </summary>
    public readonly TurretGun Gun = gun;

    /// <summary>
    /// Where the building stands.
    /// </summary>
    public readonly Vector3 Position = position;

    /// <summary>
    /// The building's facing (radians).
    /// </summary>
    public readonly float Yaw = yaw;

    /// <summary>
    /// The <see cref="TurretFormulas.MountFactor"/> of the wall it stands on.
    /// </summary>
    public readonly float Mount = mount;

    /// <summary>
    /// The turret's roll entity, from its key.
    /// </summary>
    public readonly ulong Entity = Rng.StableHash(key) ^ 0x5475727265744755UL;

    /// <summary>
    /// The turn of its parts (radians, <c>TurretGun.Parts</c> order).
    /// </summary>
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

    /// <summary>
    /// The reload's time left and length, and the steady-aim timer (game seconds).
    /// </summary>
    public float ReloadLeft, ReloadTime, Steady = RangedFormulas.SteadyAim;

    /// <summary>
    /// The gunner and its target (slot and generation; -1: none).
    /// </summary>
    public int Gunner = -1, GunnerGeneration, Target = -1, TargetGeneration;

    /// <summary>
    /// This tick's gunner efficiency e (health x power grant) and research damage multiplier.
    /// </summary>
    public float Efficiency = 1, DamageMult = 1;

    /// <summary>
    /// A gunner mans it this tick.
    /// </summary>
    public bool Manned;

    /// <summary>
    /// The last gunner (the gun's user hand <c>+0x30</c>) and its Turrets level x 0.01 (<c>+0x2c</c>), both set by gun slot 16
    /// <c>FUN_140446ef0</c> every frame the reload step runs and on each shot; kept when the gunner leaves. Only the panel reads them (not
    /// hashed; the original does not save them).
    /// </summary>
    public int User = -1, UserGeneration;

    /// <summary>
    /// The last gunner's Turrets level x 0.01 (see <see cref="User"/>).
    /// </summary>
    public float UserSkill;

    /// <summary>
    /// Adds the gun's and its gunner's controller state (not the panel's last user).
    /// </summary>
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
