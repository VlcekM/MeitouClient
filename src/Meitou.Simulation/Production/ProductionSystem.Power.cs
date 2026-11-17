using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Production;

/// <summary>
/// The power grid (docs/game/buildings-production.md "Power", docs/simulation.md "Production"): each town's generators, batteries and consumers,
/// shared as the original's <c>Town::vfunc_87</c>. Runs after the operators, so a consumer's grant (from last tick's operators) scales its work the
/// tick after.
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>
    /// Per town, whether batteries covered a shortfall last tick (the original's town <c>+0x470</c>, read the frame after it is set; not saved).
    /// </summary>
    readonly Dictionary<int, bool> _batteryMode = [];
    /// <summary>
    /// The machines of each town's grid this tick, by town (a scratch map).
    /// </summary>
    readonly Dictionary<int, List<Machine>> _grids = [];
    /// <summary>
    /// Per town, whether its generators give power and its batteries can (the town object's <c>+0x474</c> and <c>+0x480</c> above 0 after
    /// <c>Town::vfunc_87</c>); what <see cref="HasPower"/> reads. Not saved.
    /// </summary>
    readonly Dictionary<int, (bool Generators, bool Batteries)> _townSupply = [];

    /// <summary>
    /// The level towns' generator output this tick for the towns with no frame of their own (<see cref="NpcOutputNow"/>; derived, not saved).
    /// </summary>
    readonly Dictionary<int, float> _npcOutputs = [];

    /// <summary>
    /// The tick <see cref="_npcOutputs"/> was read for.
    /// </summary>
    long _npcOutputTick = -1;
    /// <summary>
    /// Per town, the grid's totals after the last tick (the town panel's power and battery bars, <see cref="TownPower"/>). Not saved.
    /// </summary>
    readonly Dictionary<int, TownPower> _townPower = [];
    /// <summary>
    /// The generation of each town of the level data (negative grid keys) that holds a player machine this tick: what its placed generators give
    /// (<see cref="NpcGeneration"/>). A scratch map.
    /// </summary>
    readonly Dictionary<int, float> _npcSupply = [];
    /// <summary>
    /// The pseudo consumers of each grid that holds a player machine this tick: an NPC town's level buildings and layout objects
    /// (<see cref="NpcDraws"/>), the town-less buildings nearest to it (<see cref="TownlessDraws"/>). A scratch map.
    /// </summary>
    readonly Dictionary<int, List<PseudoDraw>> _npcDraws = [];
    /// <summary>
    /// What each pseudo consumer of the last tick's grids was granted, by its key, with its grid and draw (<see cref="Share"/>). A scratch map.
    /// </summary>
    readonly Dictionary<string, (int Grid, float Demand, float Granted)> _pseudoGrants = new(StringComparer.Ordinal);
    /// <summary>
    /// The last grant of a pseudo consumer of each grid and draw this tick: a house's bed or seat spot, which has no key of its own among the
    /// layout objects, reads the share of a layout object of its draw on its frame. A scratch map.
    /// </summary>
    readonly Dictionary<(int Grid, float Demand), float> _pseudoByDemand = [];
    /// <summary>
    /// The wind range of each placed wind generator's rotor of the level data, by the building's key or the layout object's id (null: it has none);
    /// read once.
    /// </summary>
    readonly Dictionary<string, (float Min, float Max)?> _npcRotors = new(StringComparer.Ordinal);
    /// <summary>
    /// The construction targets of the level generators, by building record and database version.
    /// </summary>
    readonly Dictionary<string, (int Version, float Target)> _npcTargets = new(StringComparer.Ordinal);
    /// <summary>
    /// The level buildings by key with the index of the town they stand in, for <see cref="LevelPowerOf"/>; built for one population data.
    /// </summary>
    (PopulationData Data, Dictionary<string, (int Town, TownBuilding Building)> ByKey)? _levelBuildings;

    /// <summary>
    /// The research upgrade of the machine's building: the player's only (<c>FUN_14082ec70</c>).
    /// </summary>
    BuildingUpgrade UpgradeOf(World world, Machine m) => world.Research.Upgrade(world, m.Data.Id, Site(world, m.Site)?.Owner ?? -1);

    /// <summary>
    /// A generator's rated output: the data's plus the research increase.
    /// </summary>
    float Rated(World world, Machine m) => m.Data.PowerOutput + UpgradeOf(world, m).PowerIncrease;
    /// <summary>
    /// A battery's capacity now (<c>FUN_140297650</c>, <b>Verified</b>): the construction fraction x (capacity + research increase).
    /// </summary>
    float Capacity(World world, Machine m) => m.Health * (m.Data.PowerCapacity + UpgradeOf(world, m).CapacityIncrease);

    /// <summary>
    /// Whether a generator has fuel: no fuel sections (wind), or something left in one.
    /// </summary>
    static bool Fueled(Machine m) => m.Inputs.Length == 0 || Array.Exists(m.Inputs, i => i.Level > 0 || i.Count > 0);

    /// <summary>
    /// A generator's output now (slot 166, <c>FUN_140297760</c>, <b>Verified</b>): the construction fraction (<see cref="Machine.Health"/>) x (output
    /// + increase) while switched on, 0 out of fuel; wind (<c>FUN_140297810</c>) x <see cref="Machine.Wind"/>, nothing below 1. A battery gives the
    /// same health-scaled figure while it holds charge.
    /// </summary>
    float Output(World world, Machine m)
    {
        if (!m.PowerOn || !Fueled(m)) return 0;
        float output = m.Health * Rated(world, m);
        if (m.Data.IsWind && (output *= m.Wind) < 1) return 0;
        return output;
    }

    /// <summary>
    /// The player outposts with their grids' totals, for the town panel (docs/game/ui-screens.md "Town panel").
    /// </summary>
    public IReadOnlyList<OutpostSnapshot> OutpostView(World world) => world.Buildings.Towns.Count == 0 ? []
        : [.. world.Buildings.Towns.Select(t => new OutpostSnapshot(t.Number, t.Name, t.Position, t.Radius, t.Owner, t.Public,
            _townPower.GetValueOrDefault(t.Number)))];

    /// <summary>
    /// The towns' round-robin cursor of the power frame (the static <c>DAT_14213543c</c>, advanced before each use); not saved.
    /// </summary>
    int _powerCursor;

    /// <summary>
    /// The grid key of a machine that has no town at all (a machine in a world without any town).
    /// </summary>
    const int NoPowerTown = 0;
    /// <summary>
    /// The grid each town-less site was resolved to, with the player towns' layout it was resolved for (<see cref="_townLayout"/>).
    /// </summary>
    readonly Dictionary<string, (int Town, int Layout)> _powerTowns = new(StringComparer.Ordinal);
    /// <summary>
    /// A hash of the player towns' numbers and centres this tick; a cached <see cref="_powerTowns"/> entry is valid for one layout.
    /// </summary>
    int _townLayout;

    /// <summary>
    /// The town whose power frame a site's machine joins (<c>FUN_140296940</c> and <c>FUN_1409f9180</c>, <b>Observed</b>): the town of its building,
    /// else (no handle, or one whose town is gone) the nearest town by horizontal distance, whatever its owner and however far, a nest excluded;
    /// <see cref="NoPowerTown"/> for a building whose handle is NO_TOWN (<see cref="BuildSite.InNoTown"/>, no frame). A player town's key is its number; a town
    /// of the level data (an NPC town, whose machines Meitou gives a frame of their own) is <c>-(index + 1)</c>; <see cref="NoPowerTown"/> when the
    /// world has none. A tie goes to the player town, then to the lower index.
    /// </summary>
    int PowerTown(World world, BuildSite site)
    {
        if (site.Town != 0) return site.Town;
        if (site.InNoTown) return NoPowerTown;
        if (_powerTowns.TryGetValue(site.Id, out var known) && known.Layout == _townLayout) return known.Town;

        int found = TownLookup.NearestKey(world, site.Position);

        _powerTowns[site.Id] = (found, _townLayout);
        return found;
    }

    /// <summary>
    /// A hash of the player towns' numbers and centres, to invalidate the resolved <see cref="PowerTown"/>s when one moves or is added or removed.
    /// </summary>
    static int TownLayout(World world)
    {
        var hash = new HashCode();
        foreach (var t in world.Buildings.Towns)
        {
            hash.Add(t.Number);
            hash.Add(t.Position);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// The key of the grid a site's machine joins (what <see cref="PowerTick"/> shares, see <see cref="PowerTown"/>): its town's number, a level
    /// town's negative key, 0 for a machine with no town. Read by the agent control channel's <c>machines-power</c>.
    /// </summary>
    public int GridOf(World world, BuildSite site) => PowerTown(world, site);

    /// <summary>
    /// The power step: every machine's indoor, active, wind and rain state, each town's grid sharing its power (<see cref="ShareOneTown"/>), the
    /// generators' fuel burnt, then each machine's health, research multiplier and efficiency.
    /// </summary>
    void PowerTick(World world, float dt)
    {
        foreach (var list in _grids.Values) list.Clear();
        int layout = TownLayout(world);
        if (layout != _townLayout) _powerTowns.Clear();   // every entry is stale now, and the removed sites' are gone with them
        _townLayout = layout;
        foreach (var m in world.Machines.All)
        {
            // Slot 59 (isIndoors): interior furniture standing in a building; slot 164: a light building outdoors works only while it is not daytime.
            m.Indoors = IsIndoors(m.Data, Site(world, m.Site));
            m.Active = !m.Data.IsLight || m.Indoors || !world.Daytime;
            if (m.Data.IsWind) (m.Wind, m.WindRated) = (WindShare(world, m), Rated(world, m));
            if (m.Data.Function == MachineData.RainCollectorFunction)
                m.Rain = !m.Indoors && world.Weather?.At(m.Position.X, m.Position.Z) is { Affect: not WeatherAffecting.Acid } here ? here.Wetness : 0;

            // A town layout's battery bank joins the frame of the town its house is in (FUN_1402973f0 registers it with FUN_140296940's town).
            if (TownBatteryGrid(m) is { } npcTown)
            {
                if (!_grids.TryGetValue(npcTown, out var npcGrid)) _grids[npcTown] = npcGrid = [];
                npcGrid.Add(m);
                continue;
            }

            if (!m.Data.IsConsumer && !m.Data.IsGenerator && !m.Data.IsBattery || IsTownWork(m.Site)
                || Site(world, m.Site) is not { Complete: true } site)
            {
                m.Power = 1;
                continue;
            }
            int town = PowerTown(world, site);
            if (town == NoPowerTown)
            {
                // No town at all: the original never registers the machine, so a machine that draws power gets none.
                m.Power = Rated(world, m) == 0 ? 1 : 0;
                continue;
            }

            if (!_grids.TryGetValue(town, out var grid)) _grids[town] = grid = [];
            grid.Add(m);
        }

        ShareOneTown(world, dt);
        foreach (var m in world.Machines.All)
        {
            if (m.Data.IsGenerator)
            {
                m.Status = (byte)(m.Demand > 0 ? 2 : 0);
                if (m.Inputs.Length > 0 && m.PowerOn && Live(world, m)) BurnFuel(world, m, dt);
            }
        }

        foreach (var m in world.Machines.All)
        {
            var site = Site(world, m.Site);
            bool built = site is { Complete: true };
            m.Health = site is { Target: > 0 } s ? Math.Clamp(s.Progress / s.Target, 0, 1) : 1;
            m.ProductionMult = UpgradeOf(world, m).ProductionMult;
            m.Efficiency = built ? Efficiency(world, m) : 0;
            m.HasPower = built && (IsTownWork(m.Site) ? TownHasPower(world, m) : HasPower(world, m, PowerTown(world, site!)));
            m.Rotation = !built ? 0 : m.Data.IsGenerator ? (m.PowerOn ? Output(world, m) * 0.01f : 0) : m.Efficiency;
            if (m.Data.IsBattery)
            {
                // The battery's word reads its town grid's flows, not its own (FUN_140300b40).
                TownPower grid = default;
                m.OnGrid = site is not null && _townPower.TryGetValue(TownBatteryGrid(m) ?? PowerTown(world, site), out grid);
                m.GridCharging = grid.Charging > 0;
                m.GridDraining = grid.Draining > 0;
            }
        }
    }

    /// <summary>
    /// The generation of the town whose frame runs this pass (<paramref name="town"/>, null: none) when it is a town of the level data, which a
    /// player machine resolved to (<see cref="PowerTown"/>, a negative key) or a battery bank of its buildings' layouts joins
    /// (<see cref="TownBatteryGrid"/>) (<b>Verified</b>, <c>FUN_1402973f0</c> registers a building with its power town whatever its owner, a layout
    /// object with its house's, so an NPC town's frame has the output of the generators placed in it and in its houses). NPC generators ignore
    /// empty fuel inputs, but use their saved switch and construction fraction; wind also uses its rotor's wind share, nothing below 1. The NPC
    /// town's own machine-class buildings are consumers of the same frame (<see cref="NpcDraws"/>); with no running generator the player's machine
    /// gets 0. A player town's frame gets the town-less buildings nearest to it.
    /// </summary>
    void NpcGeneration(World world, int? town)
    {
        _npcSupply.Clear();
        _npcDraws.Clear();
        _pseudoGrants.Clear();
        _pseudoByDemand.Clear();
        if (town is not { } picked) return;

        if (picked > 0)
        {
            if (TownlessDraws(world, picked) is { Count: > 0 } townless) _npcDraws[picked] = townless;
            return;
        }

        _npcSupply[picked] = NpcOutput(world, -picked - 1);
        _npcDraws[picked] = NpcDraws(world, -picked - 1);
    }

    /// <summary>
    /// The grid of a town layout's battery bank (<see cref="PieceWork"/>): the frame of the level town its house stands in; null for any other
    /// machine.
    /// </summary>
    int? TownBatteryGrid(Machine m) => m.Data.IsBattery && IsTownWork(m.Site) && TownSiteOf(m.Site) is { } index ? -(index + 1) : null;

    /// <summary>
    /// The draws of the town-less level buildings whose nearest town is grid <paramref name="grid"/> (<see cref="TownlessGrid"/>).
    /// </summary>
    List<PseudoDraw> TownlessDraws(World world, int grid)
    {
        var draws = new List<PseudoDraw>();
        foreach (var b in world.SystemOf<PopulationSystem>()?.Data.Townless ?? [])
        {
            if (b.Destroyed || TownlessGrid(world, b) != grid) continue;

            if (Draw(world, b.RecordId, outdoors: true) is { } draw) draws.Add(new PseudoDraw(b.Key, draw, TownlessGetsBattery(world, b)));
        }

        return draws;
    }

    /// <summary>
    /// The grid of a level building no town takes: its handle is dropped on import, so its power town is the nearest non-nest town however far
    /// (<c>FUN_1409f9180</c>, <see cref="TownLookup.NearestKey"/>), a player town's included; resolved again when the player towns move.
    /// </summary>
    int TownlessGrid(World world, TownlessBuilding b)
    {
        string id = TownlessMark + b.Key;
        if (_powerTowns.TryGetValue(id, out var known) && known.Layout == _townLayout) return known.Town;

        int found = TownLookup.NearestKey(world, b.Position);
        _powerTowns[id] = (found, _townLayout);
        return found;
    }

    /// <summary>
    /// The draws of the machine-class buildings placed in the level-data town of <paramref name="index"/> (<b>Observed</b>, <c>FUN_1402a1750</c>
    /// registers a level building with its power town as a consumer by the sign of its <c>power output</c>): every standing building with a
    /// functionality whose output is negative (turrets, searchlights, wells, mines, refineries); a light only outside daytime (LightBuilding slot
    /// 164). Houses and stalls have no functionality and are left out. Each is one consumer of the frame at its level-data draw, and so is each
    /// such object of a standing building's layouts (ceiling fans, lamps, stoves, benches; <see cref="TownBuilding.LayoutBuildings"/>), which
    /// joins its house's town (<c>FUN_140296940</c> climbs to the root object); an interior's light draws all day.
    /// </summary>
    List<PseudoDraw> NpcDraws(World world, int index)
    {
        var draws = new List<PseudoDraw>();
        if (world.SystemOf<PopulationSystem>()?.Data.Sites.FirstOrDefault(t => t.Index == index) is not { } site) return draws;

        foreach (var b in site.Buildings)
        {
            if (b.Destroyed) continue;

            if (Draw(world, b.BuildingId, outdoors: true) is { } draw) draws.Add(new PseudoDraw(b.Key, draw, LevelGetsBattery(world, site, b)));
            if (world.Fixtures.IsDestroyed(b)) continue;   // a ruin holds no layout objects (FUN_140557280)

            foreach (var item in b.LayoutBuildings)
                if (Draw(world, item.RecordId, item.Outdoors) is { } drawn) draws.Add(new PseudoDraw(item.Id, drawn, PieceGetsBattery(world, item)));
        }

        draws.AddRange(TownlessDraws(world, -(index + 1)));
        return draws;
    }

    /// <summary>
    /// A level building's <c>gets battery</c> (+0x3b4): the UseableStuff constructor (<c>FUN_1402a0900</c>) sets it, and only a building of a saved
    /// zone or of the player's loads it from its state, where a missing key is false (<c>FUN_1402a1750</c>, the guard of "NPC generator state").
    /// </summary>
    static bool LevelGetsBattery(World world, TownSite site, TownBuilding b)
    {
        var saved = world.Fixtures.Saved?.GetValueOrDefault(b.Key);
        if (!world.SavedZones.Contains(b.Zone) && !PlayerGeneratorOwner(world, site, b, saved)) return true;

        return saved is not null ? saved.Bools.GetValueOrDefault("gets battery") : b.LevelState?.GetBool("gets battery") ?? true;
    }

    /// <summary>
    /// A layout object's <c>gets battery</c>, as <see cref="LevelGetsBattery"/> reads it: its own entry's in the save
    /// (<see cref="Saving.SavedPieces"/>), else the constructor's (an interior made again from its layout has no state).
    /// </summary>
    static bool PieceGetsBattery(World world, TownLayoutBuilding piece) =>
        SavedPieceState(world, piece) is not { } saved || saved.Bools.GetValueOrDefault("gets battery");

    /// <summary>
    /// The saved GAMESTATE_BUILDING of a layout object whose zone loads it (<see cref="Saving.SavedPiece.Fresh"/>); null for none, and for the
    /// objects of a house a town override furnished anew (<see cref="Locks.FixtureRegistry.IsRebuilt"/>: the save's are the old ones').
    /// </summary>
    static Meitou.Data.Fcs.FcsRecord? SavedPieceState(World world, TownLayoutBuilding piece)
    {
        if (world.Fixtures.IsRebuilt(piece.House)) return null;

        return world.Fixtures.SavedPieces?.Of(piece) is { Fresh: false, State: { } state } ? state : null;
    }

    /// <summary>
    /// A town-less building's <c>gets battery</c>, as <see cref="LevelGetsBattery"/> reads it; with no town, the owner is its saved owner alone.
    /// </summary>
    static bool TownlessGetsBattery(World world, TownlessBuilding b)
    {
        var saved = world.Fixtures.Saved?.GetValueOrDefault(b.Key);
        bool player = saved?.Strings.GetValueOrDefault("owner faction ID") is { Length: > 0 } id
            && world.SystemOf<PopulationSystem>()?.Data.FactionIndex(id) is >= 0 and var faction && faction == world.Player.Faction;
        if (!world.SavedZones.Contains(b.Zone) && !player) return true;

        return saved is not null ? saved.Bools.GetValueOrDefault("gets battery") : b.LevelState?.GetBool("gets battery") ?? true;
    }

    /// <summary>
    /// The draw of a level building or layout object of BUILDING <paramref name="record"/> on its frame: its <c>power output</c>'s size when it
    /// has a functionality and draws, 0 for a light by day when <paramref name="outdoors"/> (LightBuilding slot 164); null when it draws nothing.
    /// </summary>
    float? Draw(World world, string record, bool outdoors)
    {
        if (Data(record) is not { IsConsumer: true, Function: >= 0 } data) return null;

        return data.IsLight && outdoors && world.Daytime ? 0 : MathF.Abs(data.PowerOutput);
    }

    /// <summary>
    /// The mark before a town-less building's key among <see cref="_powerTowns"/>, apart from the sites' ids.
    /// </summary>
    const string TownlessMark = "townless:";

    /// <summary>
    /// The power of the level building <paramref name="key"/> when it draws power (<see cref="NpcDraws"/>, a light only outside daytime): its draw and
    /// what its frame grants it, the frame's supply shared out over its town draws and the player's machines of that frame this tick as
    /// <see cref="ShareOut"/> does, in ascending demand: an NPC town's generators (<see cref="NpcOutput"/>), or for a town-less building nearest to a
    /// player town that town's generators of the last tick (batteries left out). Null for any other instance; read only.
    /// </summary>
    public LevelPowerView? LevelPowerOf(World world, string? key)
    {
        if (key is null || LevelObject(world, key) is not { } found) return null;

        var (grid, record, outdoors) = found;
        if (Draw(world, record, outdoors) is not { } demand) return null;
        if (demand <= 0) return new LevelPowerView(key, 0, 0);
        if (grid == NoPowerTown) return new LevelPowerView(key, demand, 0);

        return new LevelPowerView(key, demand, Recorded(key, grid, demand) ?? UnsharedGrant(world, grid, demand));
    }

    /// <summary>
    /// What the last tick's frame of <paramref name="grid"/> granted the pseudo consumer <paramref name="key"/>, batteries included (<see cref="Share"/>),
    /// else one of its draw on that frame; null when the grid holds no player machine (no frame ran).
    /// </summary>
    float? Recorded(string key, int grid, float demand)
    {
        if (_pseudoGrants.TryGetValue(key, out var own) && own.Grid == grid) return own.Granted;

        return _pseudoByDemand.TryGetValue((grid, demand), out float alike) ? alike : null;
    }

    /// <summary>
    /// The grant of a draw of <paramref name="demand"/> on a frame that has not run (no machine is on it: no player machine and no battery bank of
    /// its houses' layouts, which join it, <see cref="TownBatteryGrid"/>): an NPC town's generators over its town draws (<see cref="GrantOf"/>);
    /// 0 on a player town's, which then has no generator either.
    /// </summary>
    float UnsharedGrant(World world, int grid, float demand) =>
        grid < 0 ? GrantOf(demand, NpcOutput(world, -grid - 1), [.. NpcDraws(world, -grid - 1).Select(d => d.Demand)], []) : 0;

    /// <summary>
    /// The standing level object of <paramref name="key"/> with its grid (a level town's <c>-(index + 1)</c>, a town-less building's
    /// <see cref="TownlessGrid"/>), its record and whether it stands outdoors; null for any other. It is a level building, a town-less one, an
    /// object of a house's layout (<see cref="TownLayoutBuilding.Id"/>: the house's key and "/") or a house's bed or seat spot (the house's key and
    /// "@", <see cref="LevelSpot"/>).
    /// </summary>
    (int Grid, string Record, bool Outdoors)? LevelObject(World world, string key)
    {
        if (LevelBuilding(world, key) is { } found)
            return found.Building.Destroyed ? null : (-(found.Town + 1), found.Building.BuildingId, true);

        if (world.SystemOf<PopulationSystem>()?.Data.Townless.FirstOrDefault(b => b.Key == key) is { Destroyed: false } townless)
            return (TownlessGrid(world, townless), townless.RecordId, true);

        if (key.IndexOfAny(['/', '@']) is not (> 0 and var end)) return null;
        if (LevelBuilding(world, key[..end]) is not { Building.Destroyed: false } house) return null;

        int grid = -(house.Town + 1);
        if (key[end] == '@') return LevelSpot(world, key, house.Building) is { } spot ? (grid, spot.Record, spot.Outdoors) : null;

        var item = house.Building.LayoutBuildings.FirstOrDefault(i => i.Id == key);
        return item is null ? null : (grid, item.RecordId, item.Outdoors);
    }

    /// <summary>
    /// The record of a house's bed or seat spot <paramref name="key"/> and whether it stands outdoors: a spot of the house's interior layout is
    /// indoors, the building's own when it stands alone (a bed or throne with no interior) outdoors; null for no such spot.
    /// </summary>
    static (string Record, bool Outdoors)? LevelSpot(World world, string key, TownBuilding house)
    {
        string? record = world.Fixtures.Find(key)?.RecordId is { Length: > 0 } bed ? bed : world.Fixtures.FindSeat(key)?.Record;
        return record is null ? null : (record, !house.HasInterior);
    }

    /// <summary>
    /// The level building of <paramref name="key"/> with its town's index; null when no town holds one.
    /// </summary>
    (int Town, TownBuilding Building)? LevelBuilding(World world, string key)
    {
        if (world.SystemOf<PopulationSystem>()?.Data is not { } data) return null;

        if (_levelBuildings is not { } known || known.Data != data)
        {
            var byKey = new Dictionary<string, (int, TownBuilding)>(StringComparer.Ordinal);
            foreach (var site in data.Sites)
                foreach (var b in site.Buildings) byKey.TryAdd(b.Key, (site.Index, b));

            _levelBuildings = known = (data, byKey);
        }

        return known.ByKey.TryGetValue(key, out var found) ? found : null;
    }

    /// <summary>
    /// What a draw of <paramref name="demand"/> among the town's <paramref name="npc"/> draws gets of <paramref name="supply"/>, the player's
    /// <paramref name="machines"/> of the frame drawing too: in ascending demand each takes at most an equal share of what is left, a town draw
    /// before a machine of equal demand.
    /// </summary>
    static float GrantOf(float demand, float supply, List<float> npc, List<float> machines)
    {
        var all = npc.Select(d => (Demand: d, Machine: false)).Concat(machines.Select(d => (Demand: d, Machine: true)))
            .OrderBy(d => d.Demand)
            .ThenBy(d => d.Machine)
            .ToList();
        int left = all.Count;
        foreach (var (draw, machine) in all)
        {
            float give = MathF.Min(supply / Math.Max(1, left--), draw);
            if (!machine && draw == demand) return give;

            supply -= give;
        }

        return 0;
    }

    /// <summary>
    /// What the generators placed in the level-data town of <paramref name="index"/> give now (<see cref="NpcGeneration"/>): its standing buildings
    /// that are generators and the generators of their layouts (a Power Station shack's Small Generators, which join their house's town as its
    /// battery banks do, <c>FUN_1402973f0</c>).
    /// </summary>
    float NpcOutput(World world, int index)
    {
        if (world.SystemOf<PopulationSystem>()?.Data.Sites.FirstOrDefault(t => t.Index == index) is not { } site) return 0;

        float total = 0;
        foreach (var b in site.Buildings)
        {
            if (b.Destroyed) continue;

            if (Data(b.BuildingId) is { IsGenerator: true } data)
                total += Generated(data, NpcGeneratorHealth(world, site, b), data.IsWind ? NpcWind(world, b) : 0);

            total += LayoutOutput(world, b);
        }

        return total;
    }

    /// <summary>
    /// What the generators of a standing building's layouts give now; none for a ruin, which holds no layout objects (<c>FUN_140557280</c>).
    /// </summary>
    float LayoutOutput(World world, TownBuilding b)
    {
        if (world.Fixtures.IsDestroyed(b)) return 0;

        float total = 0;
        foreach (var piece in b.LayoutBuildings)
        {
            if (Data(piece.RecordId) is not { IsGenerator: true } data) continue;

            total += Generated(data, PieceGeneratorHealth(world, piece), data.IsWind ? PieceWind(world, piece) : 0);
        }

        return total;
    }

    /// <summary>
    /// An NPC generator's output at its switched construction fraction <paramref name="health"/>: <c>power output</c> times it, a wind generator's
    /// times its <paramref name="wind"/> share and nothing below 1 (<c>FUN_140297760</c>, <c>FUN_140297810</c>).
    /// </summary>
    static float Generated(MachineData data, float health, float wind)
    {
        float output = health * data.PowerOutput;
        if (data.IsWind) output *= wind;
        return !data.IsWind || output >= 1 ? output : 0;
    }

    /// <summary>
    /// A level generator's switched construction fraction. Fresh/reset zones complete and switch on NPC buildings; player ownership bypasses
    /// that reset guard. Otherwise the save's whole state replaces the level state (FUN_1402a1750, FUN_140552b70; "NPC generator state").
    /// </summary>
    float NpcGeneratorHealth(World world, TownSite town, TownBuilding b)
    {
        var saved = world.Fixtures.Saved?.GetValueOrDefault(b.Key);
        if (!world.SavedZones.Contains(b.Zone) && !PlayerGeneratorOwner(world, town, b, saved)) return 1;

        return SwitchedHealth(b.BuildingId, saved, b.LevelState);
    }

    /// <summary>
    /// A layout generator's switched construction fraction, as <see cref="NpcGeneratorHealth"/> reads a level one's: from its own entry in the save
    /// (<see cref="SavedPieceState"/>; an entry is only in a zone file that loads, so the guard holds); with no entry (a fresh zone, or an interior
    /// made again from its layout) complete and switched on (<c>FUN_140562340</c>).
    /// </summary>
    float PieceGeneratorHealth(World world, TownLayoutBuilding piece) =>
        SavedPieceState(world, piece) is { } saved ? SwitchedHealth(piece.RecordId, saved, null) : 1;

    /// <summary>
    /// The construction fraction of a building of <paramref name="record"/> by its state (the save's, else the level's), 0 when switched off: a
    /// missing <c>power on</c> is off and missing progress 0 once a state is read; no state at all is complete and on.
    /// </summary>
    float SwitchedHealth(string record, Meitou.Data.Fcs.FcsRecord? saved, Meitou.Data.GameRecord? level)
    {
        bool on = saved is not null ? saved.Bools.GetValueOrDefault("power on") : level?.GetBool("power on") ?? true;
        if (!on) return 0;
        if (saved is null && level is null) return 1;

        if (!_npcTargets.TryGetValue(record, out var known) || known.Version != _db.Version)
        {
            float value = _db.Find(record) is { } found ? BuildingBuildData.From(found).Target : 0;
            _npcTargets[record] = known = (_db.Version, value);
        }

        float target = known.Target;
        if (target <= 0) return 1;

        float progress = saved is not null ? saved.Floats.GetValueOrDefault("construction progress") : level!.GetFloat("construction progress");
        return Math.Clamp(progress / target, 0, 1);
    }

    /// <summary>
    /// The saved owner when it resolves, otherwise the level owner (player override or town faction), using the same rules as fixture registration.
    /// </summary>
    static bool PlayerGeneratorOwner(World world, TownSite town, TownBuilding b, Meitou.Data.Fcs.FcsRecord? saved)
    {
        if (world.SystemOf<PopulationSystem>()?.Data is not { } data) return false;

        int townFaction = data.FactionIndex(town.Town.Faction ?? "");
        int owner = data.LevelOwnerOf(b, townFaction);
        if (saved?.Strings.GetValueOrDefault("owner faction ID") is { Length: > 0 } id && data.FactionIndex(id) is >= 0 and var faction)
            owner = faction;

        return owner >= 0 && owner == world.Player.Faction;
    }

    /// <summary>
    /// The wind share of the rotor this level placement spawns, at its own weather region; zero without a rotor or weather.
    /// </summary>
    float NpcWind(World world, TownBuilding b) => WindAt(world, Rotor(b.Key, b.BuildingId, b.Position), b.Position);

    /// <summary>
    /// The wind share of a layout wind generator's rotor, as <see cref="NpcWind"/>: the rotor its own placement spawns, at its weather region.
    /// </summary>
    float PieceWind(World world, TownLayoutBuilding piece) => WindAt(world, Rotor(piece.Id, piece.RecordId, piece.Position), piece.Position);

    /// <summary>
    /// A rotor's wind share at <paramref name="at"/>'s weather region; zero without a rotor or weather.
    /// </summary>
    static float WindAt(World world, (float Min, float Max)? rotor, Vector3 at)
    {
        var here = world.Weather?.At(at.X, at.Z);
        return rotor is { } r && here is { } w ? RotorSpec.WindShare(w.WindSpeed, r.Min, r.Max) : 0;
    }

    /// <summary>
    /// The rotor of a placed wind generator of <paramref name="record"/> at <paramref name="at"/> (its placement's seed picks the parts), read once
    /// by <paramref name="key"/>.
    /// </summary>
    (float Min, float Max)? Rotor(string key, string record, Vector3 at)
    {
        if (_npcRotors.TryGetValue(key, out var known)) return known;

        var rotor = _db.Find(record) is { } found ? Meitou.Data.World.WorldObjectLayout.WindRotor(_db, found, at) : null;
        return _npcRotors[key] = rotor;
    }

    /// <summary>
    /// One town's power frame per pass, the next town in ascending number after the cursor (<b>Verified</b>: <c>FUN_14092d850</c> advances the cursor
    /// and calls the town's <c>vfunc 0x2b8</c> for that one town only; the original cycles over every eligible town of the world per rendered frame,
    /// so the cadence per town is an engine choice). The other towns keep their last grants, power shares, totals and supply; the batteries of every
    /// town integrate their last grant each pass.
    /// </summary>
    void ShareOneTown(World world, float dt)
    {
        var towns = _grids.Where(g => g.Value.Count > 0).Select(g => g.Key).Order().ToList();
        foreach (var gone in _townPower.Keys.Where(t => !towns.Contains(t)).ToList()) _townPower.Remove(gone);

        // A town whose frame has gone reads its generators again (TownHasPower), not the supply of its last frame.
        foreach (var gone in _townSupply.Keys.Where(t => !towns.Contains(t)).ToList()) _townSupply.Remove(gone);

        if (towns.Count == 0)
        {
            NpcGeneration(world, null);
            return;
        }

        _powerCursor++;
        if (_powerCursor >= towns.Count) _powerCursor = 0;
        int picked = towns[_powerCursor];
        NpcGeneration(world, picked);
        foreach (var m in _grids[picked]) m.Power = 1;
        Share(world, picked, _grids[picked]);
        foreach (var town in towns) ChargeBatteries(world, town, _grids[town], dt);
    }

    /// <summary>
    /// The store of each battery moves by flow / rate x dt seconds, between 0 and the capacity (<c>FarmBuilding::vfunc_27</c> on batteries); the
    /// town's charge totals follow.
    /// </summary>
    void ChargeBatteries(World world, int town, List<Machine> grid, float dt)
    {
        float charge = 0, capacity = 0;
        foreach (var b in grid)
        {
            if (!b.Data.IsBattery) continue;
            float rate = Rated(world, b);
            b.Store = Capacity(world, b);
            if (rate > 0) b.Charge = Math.Clamp(b.Charge + b.Granted / rate * dt, 0, b.Store);
            (charge, capacity) = (charge + b.Charge, capacity + b.Store);
        }

        if (_townPower.TryGetValue(town, out var power)) _townPower[town] = power with { Charge = charge, Capacity = capacity };
    }

    /// <summary>
    /// A wind generator's share of its output (<c>PhysicsCollection::getWindRotationPower</c>, <b>Verified</b>): its first ROTATION_WIND_SPEED
    /// part's <see cref="RotorSpec.WindShare"/> of the wind speed of the weather region at the site; 0 without such a part or without weather. The
    /// original reads it from the part's render entity, so a generator without one loaded gives nothing; here every built generator has its wind
    /// (engine choice).
    /// </summary>
    static float WindShare(World world, Machine m)
    {
        m.WindSpeed = world.Buildings.Find(m.Site) is { } site && world.Weather?.At(site.Position.X, site.Position.Z) is { } here ? here.WindSpeed : 0;
        return m.WindRotor is { } rotor ? RotorSpec.WindShare(m.WindSpeed, rotor.Min, rotor.Max) : 0;
    }

    /// <summary>
    /// The efficiency a machine's sound emitter hears (<c>FarmBuilding::vfunc_108</c>, <b>Observed</b>): the share of its operators (1 when the data
    /// has no operator count) x health x the granted share of its demand (1 with no demand; for a battery, below its capacity, the share of its rate
    /// it is charging, negative while it drains) x the yield x the BUILDING's raw output rate x the research production multiplier. A wind generator
    /// hears its blade's wind share (<c>WindGeneratorBuilding::getSoundIntensity</c>, <b>Verified</b>): <see cref="Machine.Wind"/>.
    /// </summary>
    float Efficiency(World world, Machine m)
    {
        if (m.Data.IsWind) return m.Wind;
        float share = m.Data.PowerOperators > 0 ? (float)m.Operators / m.Data.PowerOperators : 1;
        float saturation = m.Power;
        if (m.Data.IsBattery)
        {
            float rate = Rated(world, m);
            saturation = rate > 0 && m.Charge < Capacity(world, m) ? m.Granted / rate : 1;
        }

        return m.Active ? share * m.Health * saturation * m.Yield * m.Data.OutputRate * m.ProductionMult : 0;
    }

    /// <summary>
    /// State 93 <c>BUILDING_HAS_POWER</c> (<c>FUN_140596ab0</c>, <b>Verified</b>), a start condition of operating (87, 221) and manning a turret (146):
    /// true for a building of class (<c>+0x198</c>, slot <c>0x358</c>) below 2, a plain building (0) or a door (1, <c>DoorStuff</c>), so never
    /// for a machine (UseableStuff 2, StorageBuilding 3, ProductionBuilding 4, ResearchBuilding 5, CraftingBuilding 6, TurretBuilding 8,
    /// FurnaceBuilding 10, FarmBuilding 13: the constructors' stores, <b>Verified</b> by disassembly); else the target building's
    /// <see cref="HasPower"/>, read from the town's supply rather than the machine's granted share, a town's machine included
    /// (<see cref="TownHasPower"/>).
    /// </summary>
    bool Powered(World world, BuildSite site, Machine m) =>
        IsTownWork(site.Id) ? TownHasPower(world, m) : HasPower(world, m, PowerTown(world, site));

    /// <summary>
    /// State 93 of any machine (<see cref="Powered(World, BuildSite, Machine)"/>): a player's by its site, a town's by its level-data town; true for
    /// a machine whose site is gone.
    /// </summary>
    public bool MachinePowered(World world, Machine m)
    {
        if (IsTownWork(m.Site)) return TownHasPower(world, m);

        return world.Buildings.Find(m.Site) is not { } site || Powered(world, site, m);
    }

    /// <summary>
    /// <see cref="HasPower"/> of a town's machine (<c>FUN_140299370</c>, <b>Verified</b>): a generator or a machine drawing nothing always; else
    /// switched on and its town's frame has generator output, the level's generators there and in its houses' layouts (<see cref="NpcOutput"/>) and
    /// any of the player's machines resolved to that town (<see cref="PowerTown"/>), or its batteries in battery mode; no town: true. A layout
    /// object's machine is its house's town's (<see cref="TownSiteOf"/>).
    /// </summary>
    bool TownHasPower(World world, Machine m)
    {
        if (m.Data.IsGenerator || Rated(world, m) == 0) return true;
        if (!m.PowerOn) return false;
        if (TownSiteOf(m.Site) is not (>= 0 and var index)) return true;

        int town = -index - 1;
        if (_townSupply.TryGetValue(town, out var supply))
            return supply.Generators || m.GetsBattery && _batteryMode.GetValueOrDefault(town) && supply.Batteries;

        return NpcOutputNow(world, index) > 0;
    }

    /// <summary>
    /// <see cref="NpcOutput"/> of a town with no player machine in its frame, once a tick (<see cref="_npcOutputTick"/>).
    /// </summary>
    float NpcOutputNow(World world, int index)
    {
        if (_npcOutputTick != world.Tick)
        {
            _npcOutputTick = world.Tick;
            _npcOutputs.Clear();
        }

        if (!_npcOutputs.TryGetValue(index, out float output)) _npcOutputs[index] = output = NpcOutput(world, index);
        return output;
    }

    /// <summary>
    /// Whether a machine counts as powered for its sound emitter (<c>FUN_140299370</c>, <b>Observed</b>): a generator always (fuel and switch are not
    /// asked); a machine drawing nothing always; else it must be switched on and its town's generators give power, or its batteries do while the grid
    /// is in battery mode and the machine takes battery power.
    /// </summary>
    bool HasPower(World world, Machine m, int town)
    {
        if (m.Data.IsGenerator || Rated(world, m) == 0) return true;
        if (!m.PowerOn) return false;
        if (!_townSupply.TryGetValue(town, out var supply)) return true;
        return supply.Generators || (m.GetsBattery && _batteryMode.GetValueOrDefault(town) && supply.Batteries);
    }

    /// <summary>
    /// One town's frame (<b>Observed</b>, <c>Town::vfunc_87</c>): generator output is shared among the consumers in ascending demand, each taking at
    /// most an equal share of what is left (<c>FUN_140927350</c> sorts); when batteries were in use last tick, consumers left short that get battery
    /// power share the batteries' output. With nothing left over the batteries drain by what was drawn; with a surplus they charge from it (each at
    /// most its rate) and the grid leaves battery mode.
    /// </summary>
    void Share(World world, int town, List<Machine> grid)
    {
        bool mode = _batteryMode.GetValueOrDefault(town);
        float supply = 0, batteries = 0, demandTotal = 0;
        var consumers = new List<Machine>();
        foreach (var m in grid)
        {
            m.Granted = 0;
            if (m.Data.IsGenerator) supply += m.Demand = Output(world, m);
            else if (m.Data.IsBattery) { m.Demand = m.PowerOn && m.Charge > 0 ? m.Health * Rated(world, m) : 0; batteries += m.Demand; }
            else
            {
                // A farm wants its whole draw whoever works it and whether or not it is switched on (FarmBuilding slot 163, FUN_1400df330:
                // |power output + increase|).
                float share = m.Data.Farm is null && m.Data.PowerOperators > 0 ? (float)m.LastOperators / m.Data.PowerOperators : 1;
                m.Demand = (m.Data.Farm is not null || m.PowerOn) && m.Active ? MathF.Abs(Rated(world, m)) * share : 0;
                demandTotal += m.Demand;
                consumers.Add(m);
            }
        }

        supply += _npcSupply.GetValueOrDefault(town);
        float generated = supply;
        _townSupply[town] = (generated > 0, batteries > 0);
        var npc = _npcDraws.GetValueOrDefault(town) ?? [];
        var order = ConsumerOrder(consumers, npc);
        int @short = ShareOut(order, ref supply);
        demandTotal += npc.Sum(d => d.Demand);

        float batteryLeft = batteries;
        if (mode && @short > 0) TopUpFromBatteries(order, ref batteryLeft, @short);
        RecordPseudoGrants(town, npc);

        float charged = 0, drained = 0;
        if (supply <= 0)
        {
            float used = drained = batteries - batteryLeft;
            foreach (var b in grid)
                if (b.Data.IsBattery) b.Granted = used > 0 && batteries > 0 ? -used * b.Demand / batteries : 0;
            _batteryMode[town] = true;
        }
        else
        {
            _batteryMode[town] = false;
            charged = ChargeFromSurplus(world, grid, supply, batteries: grid.Count(b => b.Data.IsBattery));
        }

        foreach (var m in grid)
        {
            if (m.Data.IsConsumer) m.Power = m.Demand > 0 ? m.Granted / m.Demand : 1;
            else if (m.Data.IsGenerator)
            {
                // The load (GeneratorBuilding::vfunc_167): output / rated x (charging + consumer demand) / all generators' output, at most 1.
                float rated = Rated(world, m);
                m.Usage = generated > 0 && rated > 0 ? MathF.Min(1, m.Demand / rated * (charged + demandTotal) / generated) : 0;
            }
        }

        _townPower[town] = new(generated, demandTotal, charged, drained, 0, 0);
    }

    /// <summary>
    /// The frame's consumers in the order the original sorts them (<c>FUN_140927350</c>): ascending demand, a pseudo consumer before a machine of
    /// equal demand, machines of equal demand by site.
    /// </summary>
    static List<IPowerDraw> ConsumerOrder(List<Machine> consumers, List<PseudoDraw> npc)
    {
        consumers.Sort((a, b) => a.Demand != b.Demand ? a.Demand.CompareTo(b.Demand) : string.CompareOrdinal(a.Site, b.Site));
        var order = new List<IPowerDraw>(consumers.Count + npc.Count);
        var draws = new Queue<PseudoDraw>(npc.OrderBy(d => d.Demand));
        foreach (var c in consumers)
        {
            while (draws.Count > 0 && draws.Peek().Demand <= c.Demand) order.Add(draws.Dequeue());

            order.Add(c);
        }

        order.AddRange(draws);
        return order;
    }

    /// <summary>
    /// The water-filling of <paramref name="supply"/> over the frame's consumers in <see cref="ConsumerOrder"/>, machines and pseudo consumers
    /// alike: each takes at most an equal share of what is left. Returns how many that get battery power were left short.
    /// </summary>
    static int ShareOut(List<IPowerDraw> order, ref float supply)
    {
        int left = order.Count, @short = 0;
        foreach (var c in order)
        {
            float give = MathF.Min(supply / Math.Max(1, left--), c.Demand);
            supply -= give;
            c.Granted = give;
            if (give < c.Demand && c.GetsBattery) @short++;
        }

        return @short;
    }

    /// <summary>
    /// In battery mode the consumers left short that get battery power (<c>gets battery</c>, a pseudo consumer's too) share the batteries' output
    /// in the same order, each at most what it still lacks.
    /// </summary>
    static void TopUpFromBatteries(List<IPowerDraw> order, ref float batteryLeft, int @short)
    {
        foreach (var c in order)
        {
            if (!c.GetsBattery || c.Granted >= c.Demand) continue;

            float give = MathF.Min(c.Demand - c.Granted, batteryLeft / Math.Max(1, @short--));
            batteryLeft -= give;
            c.Granted += give;
        }
    }

    /// <summary>
    /// Keeps what the frame of <paramref name="grid"/> granted each of its pseudo consumers this tick, for their panels (<see cref="Recorded"/>).
    /// </summary>
    void RecordPseudoGrants(int grid, List<PseudoDraw> npc)
    {
        foreach (var d in npc)
        {
            _pseudoGrants[d.Key] = (grid, d.Demand, d.Granted);
            _pseudoByDemand[(grid, d.Demand)] = d.Granted;
        }
    }

    /// <summary>
    /// The surplus among the batteries (<b>Verified</b>, <c>FUN_14092cd60</c>): in grid order every battery clears its grant; one that is on and
    /// below its capacity (the engine's reading of its switch byte and of a rate that is 0 when full, <b>Observed</b>) takes min(surplus left /
    /// batteries still to come, its rate); the count falls by one for every battery, chargeable or not. Returns the total charged.
    /// </summary>
    float ChargeFromSurplus(World world, List<Machine> grid, float surplus, int batteries)
    {
        float charged = 0;
        foreach (var b in grid)
        {
            if (!b.Data.IsBattery) continue;
            b.Granted = 0;
            if (b.PowerOn && b.Charge < Capacity(world, b))
            {
                float give = MathF.Min(surplus / Math.Max(1, batteries), MathF.Abs(Rated(world, b)));
                surplus -= give;
                charged += give;
                b.Granted = give;
            }

            batteries--;
        }

        return charged;
    }

    /// <summary>
    /// A generator burns fuel as a producer draws inputs (<c>vfunc_183</c>): <c>rate x dt x P x option</c>, times its load.
    /// </summary>
    void BurnFuel(World world, Machine m, float dt)
    {
        float amount = m.Data.Rate * dt * _constants.ProductionSpeed * Settings.ProductionOption * m.Usage;
        if (amount <= 0) return;
        foreach (var input in m.Inputs)
        {
            float need = input.Rate * amount;
            if (need <= 0) continue;
            while (input.Level < need && input.Count > 0) TakeInput(world, m, input);
            input.Level = MathF.Max(0, input.Level - need);
        }
    }

    /// <summary>
    /// The factor a machine's work is scaled by: its health, its power share (slot 109, which slot 150 <c>FUN_140298700</c> reads virtually) and its
    /// research production multiplier. A rain collector's slot 109 (<c>FUN_140298470</c>, <b>Verified</b>) is that times the rain strength, so it
    /// collects nothing while it does not rain.
    /// </summary>
    float WorkFactor(World world, Machine m) => m.Health * m.Power * UpgradeOf(world, m).ProductionMult
        * (m.Data.Function == MachineData.RainCollectorFunction ? m.Rain : 1);
}