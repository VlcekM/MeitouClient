using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Population;

/// <summary>
/// The homeless squads of the loaded area sectors after they are made (docs/game/factions-squads-towns.md "Homeless squads"), at every roaming look
/// (2 s):
/// <list type="bullet">
/// <item><b>Patrols</b> (<c>Task_Patrol</c>, <c>FUN_1408fc730</c>): a squad whose leader acts out a PATROL goal outside a patrol-class package walks
/// from sector to sector of its biome group: a neighbouring sector (the four diagonal or the four straight ones, a coin flip), a random point in it,
/// or with <c>patrol approaches towns</c> sometimes a town of it, aimed 200 outside its gate or wall (<see cref="OutsideTown"/>); near the point it
/// picks the next. Neighbours are weighted by how crowded they are (<see cref="SectorWeight"/>); the leader walks to the point
/// (<see cref="Squad.HomeCentre"/>), its squad following, and the next is drawn once it is within <see cref="PointArrive"/>.</item>
/// <item><b>Refill</b> (area tick <c>FUN_1408f9240</c>): while the loaded sectors hold fewer squads than the sum of P × <c>homeless spawn amount</c>,
/// one sector below its own share and with no town in it makes one squad. Engine choice: the loaded sectors are one island.</item>
/// <item><b>Sectors by place</b>: a squad belongs to the sector it stands in (<see cref="Refile"/>), so one that walks out of the loaded sectors
/// leaves the world (after a grace and away from the player, <see cref="MayRefile"/>) (kept by its new sector with <see cref="KeepWild"/>, up to that
/// sector's count) and its old sector is refilled, while the total stays bounded.</item>
/// </list>
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>
    /// Each patrolling homeless squad's point, how many it has picked and the distance to the point at which it picks the next, by squad id.
    /// </summary>
    readonly Dictionary<int, (Vector2 Point, int Picks, float Arrive)> _homelessPatrols = [];
    /// <summary>
    /// Whether a squad template's patrol approaches towns, cached by template id.
    /// </summary>
    readonly Dictionary<string, bool> _approachesTowns = new(StringComparer.Ordinal);
    /// <summary>
    /// The squads seen in this patrol pass; a patrol of one no longer seen is dropped.
    /// </summary>
    readonly HashSet<int> _patrolSeen = [];
    /// <summary>
    /// The homeless squads standing in each zone (living leader), counted at the start of each patrol pass for <see cref="SectorWeight"/>.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, float> _patrolStanding = [];
    /// <summary>
    /// Each homeless squad's home sector, its patrol's tether (<see cref="HomeCell"/>; null: none), by squad id.
    /// </summary>
    readonly Dictionary<int, (int X, int Z)?> _homelessHomes = [];

    /// <summary>
    /// The home radius kept round a loaded patrol's point (a sector point or a town's, <see cref="OutsideTown"/>) and the distance from it at which
    /// the next is drawn. The original sends the character to that exact point and draws the next once the move has ended
    /// (<c>CharMovement::setPatrolInput</c> mode 3, <c>FUN_1405c6b30</c> mode 1: Verified); the leader walks there exactly (Ai.AiSystem's patrol
    /// act-out), and the arrive distance with this step's 2 s cadence is the engine's reading of "the move has ended". The 500 of
    /// <c>FUN_1407ee790</c> is the off-screen rule only (<see cref="Ai.PlatoonJobs.PatrolArrive"/>).
    /// </summary>
    const float PointRadius = 50, PointArrive = 100;
    /// <summary>
    /// The homeless step: the homeless squads patrol, then the sectors are refilled.
    /// </summary>
    void StepHomeless(World world)
    {
        if (_wildSites is null || _data.Biomes is null) return;
        PatrolHomeless(world);
        RefillSectors(world);
    }

    /// <summary>
    /// Moves the loaded sectors' homeless squads on: each picks its next patrol point, and one that walked into another sector's zone may be filed
    /// under it.
    /// </summary>
    void PatrolHomeless(World world)
    {
        var table = world.Characters;
        _patrolSeen.Clear();
        _sectorSeen.Clear();
        _refiles.Clear();
        _patrolStanding.Clear();
        foreach (int i in _wildLoaded)
        {
            if (_wildSites![i].Kind != WildKind.Sector || _wildStates[i].Status != SiteStatus.Loaded) continue;
            foreach (int id in _wildStates[i].Squads)
            {
                if (world.Squads.Find(id) is not { } squad || !table.TryResolveNext(squad.Leader, out int slot) || !table.IsAlive(squad.Leader))
                    continue;

                var zone = WorldLayout.ZoneOf(table.Previous[slot].Position.X, table.Previous[slot].Position.Z);
                _patrolStanding[zone] = _patrolStanding.GetValueOrDefault(zone) + (SquadIntact(world, squad) ? 1 : 0.5f);
            }
        }

        AddPlatoonLoad(world, _patrolStanding, null);
        foreach (int i in _wildLoaded)
        {
            var site = _wildSites![i];
            if (site.Kind != WildKind.Sector || _wildStates[i].Status != SiteStatus.Loaded) continue;
            foreach (int id in _wildStates[i].Squads)
            {
                if (world.Squads.Find(id) is not { } squad || !table.TryResolveNext(squad.Leader, out int slot) || !table.IsAlive(squad.Leader))
                    continue;

                _sectorSeen.Add(id);
                var zone = WorldLayout.ZoneOf(table.Previous[slot].Position.X, table.Previous[slot].Position.Z);
                if (zone != site.Zone && SectorAt(zone) is var to and >= 0 && MayRefile(world, id, to, zone, slot)) _refiles.Add((id, i, to));
                PatrolStep(world, squad, slot, site.Group, site.Zone);
            }
        }

        PatrolLoadedSquads(world);
        foreach (var (id, from, to) in _refiles) Refile(world, id, from, to);
        if (_homelessPatrols.Count > _patrolSeen.Count)
            foreach (int id in _homelessPatrols.Keys.Where(k => !_patrolSeen.Contains(k)).ToList()) _homelessPatrols.Remove(id);
        if (_outsideSince.Count > 0)
            foreach (int id in _outsideSince.Keys.Where(k => !_sectorSeen.Contains(k)).ToList()) _outsideSince.Remove(id);
        if (_homelessHomes.Count > _sectorSeen.Count)
            foreach (int id in _homelessHomes.Keys.Where(k => world.Squads.Find(k) is null).ToList()) _homelessHomes.Remove(id);
    }

    /// <summary>
    /// The homeless squads seen in this patrol pass (their homes of squads not seen are dropped).
    /// </summary>
    readonly HashSet<int> _sectorSeen = [];

    /// <summary>
    /// The squads to file under another sector after this pass: the squad, the sector it leaves and the one it joins.
    /// </summary>
    readonly List<(int Squad, int From, int To)> _refiles = [];
    /// <summary>
    /// The tick each homeless squad's leader was first seen outside the active zones, by squad id.
    /// </summary>
    readonly Dictionary<int, long> _outsideSince = [];

    /// <summary>
    /// Whether a squad standing in another sector moves to it now: at once into a loaded one; into one that is not loaded only once its leader has
    /// been outside the active zones for <see cref="PopulationSettings.RoamUnloadGraceSeconds"/> and is
    /// <see cref="PopulationSettings.HomelessPlayerDistance"/> from every player character (engine choice, as roaming squads leave the world, so it
    /// does not vanish in view).
    /// </summary>
    bool MayRefile(World world, int id, int to, ZoneCoordinate zone, int slot)
    {
        if (_wildStates[to].Status == SiteStatus.Loaded) return true;
        if (_active.Contains(zone))
        {
            _outsideSince.Remove(id);
            return false;
        }

        if (!_outsideSince.TryGetValue(id, out long since)) _outsideSince[id] = since = world.Tick;
        if (world.Tick - since < (long)Math.Ceiling(_settings.RoamUnloadGraceSeconds / world.TickSeconds)) return false;
        var p = world.Characters.Previous[slot].Position;
        var at = new Vector2(p.X, p.Z);
        foreach (var player in PlayerSpots(world))
            if (Vector2.DistanceSquared(player, at) < _settings.HomelessPlayerDistance * _settings.HomelessPlayerDistance) return false;
        _outsideSince.Remove(id);
        return true;
    }

    /// <summary>
    /// The sector site of a zone, or -1.
    /// </summary>
    int SectorAt(ZoneCoordinate zone)
    {
        if (_wildByZone.TryGetValue(zone, out var list))
        {
            foreach (int i in list)
                if (_wildSites![i].Kind == WildKind.Sector) return i;
        }

        return -1;
    }

    /// <summary>
    /// A homeless squad that walked into another sector is that sector's (the original files a platoon in the sector it stands in,
    /// <c>FUN_1408f6c80</c>): a loaded one takes it as it is; an unloaded one keeps it as its kept squads (the Meitou <see cref="KeepWild"/> switch),
    /// or, Faithful, it leaves the world as the original's off-screen homeless platoons do.
    /// </summary>
    void Refile(World world, int id, int from, int to)
    {
        _wildStates[from].Squads.Remove(id);
        var state = _wildStates[to];
        if (state.Status == SiteStatus.Loaded)
        {
            state.Squads.Add(id);
            return;
        }

        var one = new SiteState();
        one.Squads.Add(id);

        // Kept only up to the sector's own count, so sectors round where the player goes do not pile them up (engine choice); an occupier always, as
        // the original's persistent one.
        bool occupier = world.Squads.Find(id) is { } squad && Occupying(squad);
        if (!occupier
            && (!KeepWild || KeptWildOf(to).Count >= HomelessCount(_data.Biomes!.Groups[_wildSites![to].Group], _settings.PopulationMultiplier)))
        {
            Drop(world, one);
            return;
        }

        KeepAlso(world, to, one);
    }

    /// <summary>
    /// One squad's patrol (<c>Task_Patrol</c>): when its leader acts a PATROL goal that is not on a patrol package's route nor under a mission town
    /// (a trader on its way, an assault or occupation roaming the town it took), it draws the next point once it has reached the last
    /// (<see cref="PatrolPoint"/>) and walks to it. Every squad does, homeless or not: the picker's only caller is the patrol task
    /// (<c>FUN_1408fd6c0</c>, <c>FUN_1408fe0d0</c>, <b>Verified</b>).
    /// </summary>
    void PatrolStep(World world, Squad squad, int slot, int group, ZoneCoordinate zone)
    {
        var table = world.Characters;
        if (table.Cold(slot)?.Ai is not { Goal.Task.Type: TaskType.Patrol }
            || squad.Ai is { CurrentClass: PackageClass.Patrol or PackageClass.PatrolRunning } board
            && board.RouteIndex < board.Route.Length || squad.Ai is { Destination.IsNone: false }) return;
        _patrolSeen.Add(squad.Id);
        var at = new Vector2(table.Previous[slot].Position.X, table.Previous[slot].Position.Z);
        _homelessPatrols.TryGetValue(squad.Id, out var patrol);
        if (patrol.Picks > 0 && Vector2.DistanceSquared(at, patrol.Point) > patrol.Arrive * patrol.Arrive) return;
        if (PatrolPoint(world, squad, group, zone, at, patrol.Picks) is not { } next) return;
        _homelessPatrols[squad.Id] = (next.Point, patrol.Picks + 1, PointArrive);
        squad.HomeCentre = next.Point;
        squad.HomeRadius = PointRadius;
    }

    /// <summary>
    /// The loaded squads outside the homeless sectors (a town's, a platoon's) whose leader acts a PATROL goal outside a patrol package walk from
    /// sector to sector as well (a2-011).
    /// </summary>
    void PatrolLoadedSquads(World world)
    {
        var table = world.Characters;
        foreach (var squad in world.Squads.All)
        {
            if (_sectorSeen.Contains(squad.Id) || !table.TryResolveNext(squad.Leader, out int slot) || !table.IsAlive(squad.Leader)) continue;
            var p = table.Previous[slot].Position;
            var (cx, cz) = WeatherAreas.CellOf(p.X, p.Z);
            if (_data.Biomes!.GroupOf(cx, cz) is var group and >= 0) PatrolStep(world, squad, slot, group, new ZoneCoordinate(cx, cz));
        }
    }

    /// <summary>
    /// The next point of a patrol from <paramref name="at"/> (<c>FUN_1408fc730</c>): a neighbouring sector of the group by
    /// <see cref="SectorWeight"/>, then in it a town (a player's at even odds with weight 1, another's at 1 in 4 with weight 0.2) when the
    /// template approaches towns, whose point is <see cref="OutsideTown"/> (<c>Town</c> true), else a placed spot (<c>FUN_1408f50d0</c>); null
    /// when none is found.
    /// </summary>
    (Vector2 Point, bool Town)? PatrolPoint(World world, Squad squad, int group, ZoneCoordinate zone, Vector2 at, int picks)
    {
        var biomes = _data.Biomes!;
        ulong key = Rng.Mix(SeedSalts.HomelessPatrol ^ (ulong)squad.Id << 20 ^ (uint)picks);
        ulong n = 0;
        ulong Roll() => Rng.Hash(world.Seed, key, RngPurpose.Wander, n++);
        var (cx, cz) = WeatherAreas.CellOf(at.X, at.Y);
        if (!_homelessHomes.TryGetValue(squad.Id, out var home)) _homelessHomes[squad.Id] = home = HomeCell(squad, zone, at);
        Span<(int X, int Z)> near = stackalloc (int, int)[4];
        Span<float> weight = stackalloc float[4];
        bool diagonal = Rng.Float(Roll()) < 0.5f;
        int count = 0;
        float sum = 0;
        for (int k = 0; k < 4; k++)
        {
            (int X, int Z) cell = diagonal
                ? (cx + ((k & 1) == 0 ? 1 : -1), cz + (k < 2 ? 1 : -1))
                : k switch { 0 => (cx + 1, cz), 1 => (cx, cz + 1), 2 => (cx - 1, cz), _ => (cx, cz - 1) };
            if (biomes.GroupOf(cell.X, cell.Z) != group) continue;

            // The tether: a homed squad's sectors are within one of its home's (FUN_1408f5ad0's range factor, near 0 and far 1 from
            // Task_Patrol's start).
            if (home is { } h && Math.Max(Math.Abs(cell.X - h.X), Math.Abs(cell.Z - h.Z)) > 1) continue;
            weight[count] = SectorWeight(new ZoneCoordinate(cell.X, cell.Z));
            sum += weight[count];
            near[count++] = cell;
        }

        int drawn = 0;
        for (float r = Rng.Float(Roll()) * sum; drawn < count - 1 && r >= weight[drawn]; drawn++) r -= weight[drawn];

        // No neighbour: a point of the home's sector, else of its own (engine choice; the original's fallback, FUN_1408fc1a0, takes the first active
        // sector of the group with no town and fewer than 2 excluded platoons where a spot is found, else any sector of the group).
        var (tx, tz) = count > 0 ? near[drawn] : home is { } back ? back : (cx, cz);
        if (ApproachesTowns(squad.TemplateId))
        {
            var target = new ZoneCoordinate(tx, tz);
            float total = 0;
            TownSite? pick = null;
            foreach (var s in _data.Sites)
            {
                if (s.Town.Type is TownType.NestMarker or TownType.PointOfInterest or TownType.Null
                    || WorldLayout.ZoneOf(s.Position.X, s.Position.Z) != target)
                    continue;

                bool players = _data.PlayerFaction >= 0 && _data.SiteFactions[s.Index] == _data.PlayerFaction;
                if (Rng.Float(Roll()) >= (players ? 0.5f : 0.25f)) continue;
                float w = players ? 1 : 0.2f;
                total += w;
                if (Rng.Float(Roll()) * total < w) pick = s;   // a running weighted pick
            }

            if (pick is not null) return (OutsideTown(world, pick), true);
        }

        var (minX, minZ, maxX, maxZ) = WeatherAreas.CellRect(tx, tz);
        var spot = PlaceInSector(minX, minZ, maxX, maxZ, squad.Faction, world.Seed, key, world.Walkability, RegionsOf(world), []);
        return spot is { } placed ? (placed, false) : null;
    }

    /// <summary>
    /// A town's patrol waypoint (<c>Town::getPositionOutsideTownGates(200)</c>): the first of its buildings, in list order, that is a gate with a
    /// door (not a ruin) outside the town walls (gate code 0, or unknown) or a wall with <c>interior</c> references, moved
    /// <see cref="TownExit.Offset"/> outward from the town's position; the town's position when there is none. The original moves the gate's door
    /// point and its direction is Observed; the building's origin and the outward direction are engine readings.
    /// </summary>
    Vector2 OutsideTown(World world, TownSite town)
    {
        _locks ??= world.Systems.OfType<Locks.LockSystem>().FirstOrDefault();
        return TownExit.Of(town, _locks?.Regions, world.Fixtures.IsDestroyed);
    }

    /// <summary>
    /// A neighbour's weight in a patrol's pick (<c>FUN_1408f5ad0</c>, disassembly, <b>Verified</b>): 1 when its zone is not active; active, by its
    /// load n (<see cref="PatrolWeight"/>).
    /// </summary>
    float SectorWeight(ZoneCoordinate zone) => !_active.Contains(zone) ? 1 : PatrolWeight(_patrolStanding.GetValueOrDefault(zone));

    /// <summary>
    /// An active sector's weight for load n: 10 when empty, a flat 5 below 2, 1 at 2, 1 / n above (the constants 10, 2, 5, 1 of
    /// <c>FUN_1408f5ad0</c>).
    /// </summary>
    internal static float PatrolWeight(float n) => n == 0 ? 10 : n < 2 ? 5 : n > 2 ? 1 / n : 1;

    /// <summary>
    /// Every non-persistent platoon not homed to a nest counts in the area sector of its place (<c>FUN_1407ebd00</c> -> <c>FUN_1408f6c80</c> /
    /// <c>FUN_1408f68a0</c>, <b>Verified</b>), town roamers, stand-ins and a save's homeless platoons too: 1 when intact, 0.5 when not
    /// (<c>FUN_1408f6d20</c>). The homeless squads the wild sites made are added by the callers; the platoons made for a wild site are not
    /// counted twice.
    /// </summary>
    void AddPlatoonLoad(World world, Dictionary<ZoneCoordinate, float> load, Dictionary<ZoneCoordinate, int>? intactIn)
    {
        foreach (var p in world.Platoons.All)
        {
            // A save's homeless platoon counts like any other; a nest's is homed to its nest.
            if (p.Saved?.Wild == WildKind.Nest || p.Campaign is not null || PersistentPlatoon(p)) continue;

            var zone = WorldLayout.ZoneOf(p.Position.X, p.Position.Y);
            bool intact = Intact(world, p);
            load[zone] = load.GetValueOrDefault(zone) + (intact ? 1 : 0.5f);
            if (intact && intactIn is not null) intactIn[zone] = intactIn.GetValueOrDefault(zone) + 1;
        }
    }

    /// <summary>
    /// The sector of a homeless squad's home town (<c>FUN_1408f8060</c>, then <c>Ownerships::setHomeTown</c>), measured from <paramref name="at"/>:
    /// the town nearest its sector's centre when it is of the squad's faction; else the faction's nearest town, or its nearest nest when that is 9216
    /// nearer, none beyond 50000; null when none (no faction, or no town of it). Engine choice: decided at its first patrol rather than as it is
    /// made, so saved and kept squads get one too.
    /// </summary>
    (int X, int Z)? HomeCell(Squad squad, ZoneCoordinate zone, Vector2 at)
    {
        if (squad.Faction < 0) return null;
        var (minX, minZ, maxX, maxZ) = WeatherAreas.CellRect(zone.X, zone.Y);
        Vector2? home = null;
        if (NearestTown(new Vector2((minX + maxX) / 2, (minZ + maxZ) / 2), out _) is { } local && _data.SiteFactions[local.Index] == squad.Faction)
            home = new Vector2(local.Position.X, local.Position.Z);
        else
        {
            float town = float.MaxValue, nest = float.MaxValue;
            Vector2 townAt = default, nestAt = default;
            foreach (var s in _data.Sites)
            {
                if (s.Town.Type is TownType.NestMarker or TownType.PointOfInterest or TownType.Null || _data.SiteFactions[s.Index] != squad.Faction)
                    continue;

                var p = new Vector2(s.Position.X, s.Position.Z);
                if (Vector2.Distance(at, p) is var d && d < town) (town, townAt) = (d, p);
            }

            for (int i = 0; i < _wildSites!.Count; i++)
            {
                if (_wildSites[i] is not { Kind: WildKind.Nest } w || w.Faction != squad.Faction) continue;
                var p = _wildCentres[i] ?? w.Spots[0];
                if (Vector2.Distance(at, p) is var d && d < nest) (nest, nestAt) = (d, p);
            }

            if (town == float.MaxValue) return null;
            (home, float far) = nest + 9216 < town ? (nestAt, nest) : (townAt, town);
            if (far > 50000) return null;
        }

        return home is { } h ? WeatherAreas.CellOf(h.X, h.Y) : null;
    }

    /// <summary>
    /// Whether a squad template's patrol approaches towns (cached).
    /// </summary>
    bool ApproachesTowns(string templateId)
    {
        if (!_approachesTowns.TryGetValue(templateId, out bool yes))
        {
            _approachesTowns[templateId] = yes = _data.Db.Find(templateId) is { } r && SquadFactory.IsTemplate(r)
                && SquadTemplate.From(r).PatrolApproachesTowns;
        }

        return yes;
    }

    /// <summary>
    /// The area tick's homeless branch (<c>FUN_1408f9240</c>): when the loaded sectors' load (an intact squad 1, another 0.5, a persistent one
    /// nothing; a sector with a town counts at most 2) is below the sum of their shares, one sector is drawn by weight max(0, share + 1 − load),
    /// halved by its own town, (w + 1) × 2 when nobody stands in it, and 0 when a town stands in it (<see cref="TownZones"/>) or the camera looks at
    /// it; one whose intact squads are below its share makes one squad (built on the population thread), else the next draw.
    /// </summary>
    void RefillSectors(World world)
    {
        var groups = _data.Biomes!.Groups;
        var table = world.Characters;

        // Each zone's load (intact squads 1, the others 0.5) and its intact count (the original's sets B and C, FUN_1408f6d20), by where the
        // squads stand.
        var standing = new Dictionary<ZoneCoordinate, float>();
        var intactIn = new Dictionary<ZoneCoordinate, int>();
        var sectors = new List<int>();
        var ownTown = new List<bool>();
        var towns = TownZones();
        HashSet<int>? persistent = null;
        float target = 0, load = 0;
        var roaming = new Dictionary<ZoneCoordinate, float>();
        AddPlatoonLoad(world, roaming, intactIn);
        foreach (var (zone, held) in roaming) standing[zone] = standing.GetValueOrDefault(zone) + held;
        foreach (int i in _wildLoaded)
        {
            var site = _wildSites![i];
            if (site.Kind != WildKind.Sector) continue;
            var state = _wildStates[i];
            if (state.Status != SiteStatus.Loaded || state.Pending) return;   // one at a time, as the original's tick
            sectors.Add(i);
            ownTown.Add(OwnTown(site));
            target += _settings.PopulationMultiplier * groups[site.Group].HomelessSpawnAmount;
            float held = 0;
            foreach (int id in state.Squads)
            {
                // A persistent squad (a save's, an occupier) is filed in the uncounted set A.
                if (world.Squads.Find(id) is not { } squad || (persistent ??= PersistentSquads(world)).Contains(id) || Occupying(squad)) continue;
                bool intact = SquadIntact(world, squad);
                held += intact ? 1 : 0.5f;
                var zone = site.Zone;
                foreach (var m in squad.Members.OrderBy(m => !Living(world, m)))
                {
                    if (table.TryResolveNext(m, out int slot))
                    {
                        zone = WorldLayout.ZoneOf(table.Previous[slot].Position.X, table.Previous[slot].Position.Z);
                        break;
                    }
                }

                standing[zone] = standing.GetValueOrDefault(zone) + (intact ? 1 : 0.5f);
                if (intact) intactIn[zone] = intactIn.GetValueOrDefault(zone) + 1;
            }

            held += roaming.GetValueOrDefault(site.Zone);

            // Clamped to [0, 2] where a town is: its own, or the sector flag (FUN_1408f62e0, +0x30).
            load += ownTown[^1] || towns.Contains(site.Zone) ? Math.Min(held, 2) : held;
        }

        if (sectors.Count == 0 || load >= target) return;

        var weights = SectorWeights(groups, sectors, ownTown, standing, intactIn, towns);

        // The sectors in draw order: the original draws one by weight, takes it out of the tree and tries the next while it made no squad (a sector
        // with no usable spot), so the order is a weighted draw without replacement, the first draw as before.
        ulong key = Rng.Mix(SeedSalts.HomelessRefill ^ (ulong)world.Tick);
        var tries = new List<(WildSite Site, long LoadId, WildHold Hold, int Number)>();
        for (ulong n = 0; Pick(weights, Rng.Hash(world.Seed, key, RngPurpose.Spawn, n)) is >= 0 and var picked; n++)
        {
            weights[picked] = 0;
            var st = _wildStates[sectors[picked]];
            st.Pending = true;
            var hold = new WildHold(0, 0, new Dictionary<string, int>(StringComparer.Ordinal));
            foreach (int id in st.Squads)
            {
                if (world.Squads.Find(id) is { } squad && squad.Members.Any(m => Living(world, m)))
                    hold.Templates[squad.TemplateId] = hold.Templates.GetValueOrDefault(squad.TemplateId) + 1;
            }

            tries.Add((_wildSites![sectors[picked]], st.LoadId, hold, st.Made));
        }

        if (tries.Count == 0) return;

        BuildTries(world, tries);
    }

    /// <summary>
    /// Each loaded sector's draw weight: max(0, share + 1 - load), halved by its own town, (w + 1) x 2 when nobody stands in it; 0 at
    /// its share, in a town or in view.
    /// </summary>
    float[] SectorWeights(IReadOnlyList<BiomeGroupData> groups, List<int> sectors, List<bool> ownTown, Dictionary<ZoneCoordinate, float> standing,
        Dictionary<ZoneCoordinate, int> intactIn, HashSet<ZoneCoordinate> towns)
    {
        // The sector under the camera focus is never drawn (DAT_142135230, set every frame from the GameWorld +0x580 object's place); the focus as
        // RefreshZones takes it.
        ZoneCoordinate? view = (_focus ?? (this._players.Count > 0 ? this._players[0] : null)) is { } eye ? WorldLayout.ZoneOf(eye.X, eye.Z) : null;
        var weights = new float[sectors.Count];
        for (int k = 0; k < sectors.Count; k++)
        {
            var site = _wildSites![sectors[k]];
            float share = _settings.PopulationMultiplier * groups[site.Group].HomelessSpawnAmount;
            float here = standing.GetValueOrDefault(site.Zone);
            float w = MathF.Max(0, share + 1 - here);
            if (ownTown[k]) w *= 0.5f;
            if (here == 0) w = (w + 1) * 2;

            // A sector whose intact squads are at its share is drawn and passed over in the original; it never makes one. A town's sector and the one
            // in view never enter the pick.
            weights[k] = intactIn.GetValueOrDefault(site.Zone) < share && !towns.Contains(site.Zone) && site.Zone != view ? w : 0;
        }

        return weights;
    }

    /// <summary>
    /// Builds the sectors tried, on the population thread when there is one, and spawns the squads made.
    /// </summary>
    void BuildTries(World world, List<(WildSite Site, long LoadId, WildHold Hold, int Number)> tries)
    {
        ulong seed = world.Seed;
        var walk = world.Walkability;
        var regions = RegionsOf(world);
        var players = PlayerSpots(world);

        // One job builds them in order until a squad is made; the sectors it did not reach come back empty so their pending marks clear.
        List<BuiltWild> Build()
        {
            var results = new List<BuiltWild>(tries.Count);
            bool made = false;
            foreach (var t in tries)
            {
                var b = made
                    ? new BuiltWild(t.Site.Index, t.LoadId, default, [], TopUp: true)
                    : BuildSector(t.Site, t.LoadId, seed, walk, regions, players, t.Hold, t.Number);
                made |= b.Squads.Count > 0;
                results.Add(b);
            }

            return results;
        }

        if (_work is null) foreach (var b in Build()) SpawnWild(world, b);
        else _work.Add(() => { foreach (var b in Build()) _builtWild.Enqueue(b); });
    }

    /// <summary>
    /// The loaded squads of a save's persistent platoons (the original's persistent flag +0x115, <c>FUN_1407ec570</c>): filed in a sector's uncounted
    /// set, not its load.
    /// </summary>
    static HashSet<int> PersistentSquads(World world)
    {
        HashSet<int> ids = [];
        foreach (var p in world.Platoons.All)
            if (p.SquadId >= 0 && p.Saved?.Source.Record.Bools.GetValueOrDefault("persistent") == true) ids.Add(p.SquadId);
        return ids;
    }

    /// <summary>
    /// Whether the town nearest a sector's centre stands in it (<c>FUN_1408f62e0</c>).
    /// </summary>
    bool OwnTown(WildSite site)
    {
        var (minX, minZ, maxX, maxZ) = WeatherAreas.CellRect(site.Zone.X, site.Zone.Y);
        return NearestTown(new Vector2((minX + maxX) / 2, (minZ + maxZ) / 2), out _) is { } town
            && WorldLayout.ZoneOf(town.Position.X, town.Position.Z) == site.Zone;
    }

    /// <summary>
    /// The zones a town stands in (<c>Town::setup</c>'s sector flag +0x30, <b>Verified</b> <c>FUN_1409353c0</c>): each town's zone list, else its
    /// position's zone, for every town type but Ruins and points of interest (a nest marker sets it); whether <c>setup</c> runs for the Null type is
    /// Unknown, so those are left out. Built afresh on each refill pass, so it follows town overrides (<see cref="TownSite.Replacement"/>).
    /// </summary>
    HashSet<ZoneCoordinate> TownZones()
    {
        HashSet<ZoneCoordinate> zones = [];
        foreach (var s in _data.Sites)
        {
            if (s.Town.Type is TownType.Ruins or TownType.PointOfInterest or TownType.Null) continue;
            if (s.Coverage.Count > 0) zones.UnionWith(s.Coverage);
            else zones.Add(WorldLayout.ZoneOf(s.Position.X, s.Position.Z));
        }

        return zones;
    }
}