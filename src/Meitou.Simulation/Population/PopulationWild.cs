using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Population;

/// <summary>
/// What fills a wild site: the homeless squads of an area sector (6.2) or the roaming pool of a nest (6.3).
/// </summary>
public enum WildKind : byte
{
    /// <summary>
    /// An area sector's homeless squads (6.2).
    /// </summary>
    Sector,
    /// <summary>
    /// A nest's roaming pool (6.3).
    /// </summary>
    Nest
}

/// <summary>
/// A place in the wild that gets squads while its zone is active: an area sector (one zone cell and its BIOME_GROUP) or a nest of a biome group.
/// <see cref="Spots"/> are the seeded candidate centres of a nest (the first walkable one is used when it loads); a sector has none.
/// <see cref="SaveInstance"/> is the town-list entry of a nest that came from a save (its one spot is the saved position). <see cref="Key"/> keys a
/// nest's squads: its layout roll's, or for a saved nest no roll gives, its entry's (so a nest makes the same squads whatever its index).
/// </summary>
public sealed record WildSite(int Index, WildKind Kind, ZoneCoordinate Zone, int Group, TownData? Nest, int Faction, IReadOnlyList<Vector2> Spots,
    string? SaveInstance = null, ulong Key = 0);

/// <summary>
/// The wild population (docs/game/factions-squads-towns.md 6.2, 6.3): homeless squads of the area sectors and the animals (and camps) of the nests.
/// Runs only when <see cref="PopulationData.Biomes"/> is set.
/// <list type="bullet">
/// <item><b>Sectors</b> (6.2; patrols and the area tick's refill in <c>PopulationHomeless.cs</c>). Every zone cell is a sector of its BIOME_GROUP.
/// When its zone activates it gets ceil(P x <c>homeless spawn amount</c>) squads: what the original's area tick reaches for a sector loaded alone (it
/// spawns while the island's population is below P x the sum of the amounts and the sector's own count below its share; with one sector that stops at
/// the ceiling), but no more than the island's room (<see cref="IslandRoom"/>: the sum over the loaded sectors less their squads and the save's
/// homeless squads in them). The counters a and b and the "town nearby" halving are not modelled (engine choice). Template weights are 6.2's
/// homeless branch, <c>max(0, v0 - k x count)</c> with k = (sum of v0) / (P x amount) and the counts of this
/// sector's own picks; world-state gated templates are skipped. The spot: a random point of the sector, pushed out of the nearest town to twice its
/// radius and out of other towns of another faction to their radius, at least <see cref="PopulationSettings.HomelessPlayerDistance"/> from every
/// player character, walkable; 40 tries (the original's 4 rounds of 10), else that squad is not made. A sector whose nearest town is the player's
/// makes none, and so does one whose own town is a nest with roaming squads.</item>
/// <item><b>Nests</b> (6.3). The original adds one nest per area tick until a group has <c>num nests</c> x the nests multiplier; here the layout is
/// made at once from the world seed (engine choice; the end state is the same count): each nest's TOWN is a weighted pick from the group's
/// <c>nests</c> (v0, 0 as 100, the TOWN's <c>world state</c> gate), its place a seeded cell of the group's area with candidate spots away from placed
/// towns, or for groups with <c>nests at fixed markers only</c> one of the placed NEST_MARKER towns of the area. When its zone activates the nest
/// fills its roaming pool to its pool's floor(<c>pop2</c>) members (0.7 x <c>nest resident population</c> at first; <c>PopulationPools.cs</c>) from
/// the TOWN's <c>roaming squads</c> (weight max(0, v0 - count), k = 1; an entry whose expected size passes the free capacity is skipped, unless the
/// pool is empty and more than 4 are free), the squads wandering round the nest. While loaded it tops up one squad per look and can be wiped out for
/// good (<c>PopulationPools.cs</c>).</item>
/// <item><b>Saves</b> (docs/formats/save.md "Nests", "Wild and nest platoons"): a loaded save's nests are the layout's first (at their saved places),
/// the seeded roll (with the seed the save restores) only tops each group up to its count, leaving out the rolls whose nests the save holds, so a
/// Meitou save (which writes only the nests that have loaded) loads with its other nests where they were. The save's wild platoons stay as
/// themselves: a nest's counts its members against the nest's pool, a homeless one counts as one of its sector's squads (the sector its position is
/// in), so a site makes only the rest.</item>
/// <item><b>Loading</b> follows the towns: the same zone gate, the same unload grace; the squads are built on the population thread and taken in at a
/// tick boundary; on unload they are removed. Every roll is keyed by the world seed and the sector's zone or the nest's index and the squad's number,
/// never by the tick, so a reload makes the same squads (their spots can differ when the player stands elsewhere).</item>
/// </list>
/// </summary>
public sealed partial class PopulationSystem
{
    #region Wild sites and their state

    /// <summary>
    /// The wild squads built on the build thread, waiting to be taken in.
    /// </summary>
    readonly ConcurrentQueue<BuiltWild> _builtWild = new();
    /// <summary>
    /// The wild sites of the world's biome map (null: not laid out yet, or no biome data).
    /// </summary>
    List<WildSite>? _wildSites;
    /// <summary>
    /// Each wild site's state, by wild site.
    /// </summary>
    SiteState[] _wildStates = [];
    /// <summary>
    /// The wild sites by the zone they are in.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, List<int>> _wildByZone = [];
    /// <summary>
    /// The wild sites loaded or loading, in index order.
    /// </summary>
    readonly SortedSet<int> _wildLoaded = [];
    /// <summary>
    /// A scratch list of wild sites for <see cref="StepWild"/>.
    /// </summary>
    readonly List<int> _wildBuffer = [];

    /// <summary>
    /// The squads an unloaded wild site keeps as their characters (the Meitou <see cref="KeepWild"/> switch), by site; made again, where they stood,
    /// when it loads.
    /// </summary>
    readonly SortedDictionary<int, List<KeptWild>> _keptWild = [];

    /// <summary>
    /// The nest each nest squad was made for, by squad id (see <see cref="NestOf"/>).
    /// </summary>
    readonly Dictionary<int, TownData> _nestSquads = [];

    /// <summary>
    /// The wild squads made since the last take of the log.
    /// </summary>
    readonly List<WildSpawn> _wildLog = [];

    /// <summary>
    /// A kept wild squad: its characters, what it was made from and the circle it keeps to.
    /// </summary>
    internal sealed record KeptWild(StoredSquad Stored, string TemplateId, int Faction, long Money, int SpawnSize, Vector2 Centre, float Radius);

    /// <summary>
    /// The squads an unloaded wild site keeps (empty: none, or the site is loaded).
    /// </summary>
    internal IReadOnlyList<KeptWild> KeptWildOf(int site) => _keptWild.TryGetValue(site, out var kept) ? kept : [];

    /// <summary>
    /// A wild site's squads, built. <paramref name="Rolls"/>: the nest rolls used (the next top-up's keys follow them);
    /// <paramref name="Exhausted"/>: a nest roll found no entry that fits or weighs anything; <paramref name="Layout"/>: what a loading nest
    /// scatters (<see cref="NestFurniture"/>).
    /// </summary>
    sealed record BuiltWild(int Site, long LoadId, Vector2 Centre, List<BuiltSquad> Squads, int Rolls = 0, bool Exhausted = false, bool TopUp = false,
        NestLayout? Layout = null);

    /// <summary>
    /// What a nest build may make: members up to <paramref name="Cap"/> with <paramref name="Members"/> there already, <paramref name="Counts"/>
    /// squads per entry, rolls keyed from <paramref name="First"/>, at most <paramref name="Rolls"/> of them and <paramref name="Squads"/> squads;
    /// <paramref name="Place"/> false only weighs (a player is near).
    /// </summary>
    sealed record NestBudget(int Cap, int Members, int[] Counts, int First, int Rolls, int Squads, bool Place = true, Vector2? Centre = null);

    /// <summary>
    /// The wild sites (sectors and nests), made at the first look of a world with biomes; empty before or without.
    /// </summary>
    public IReadOnlyList<WildSite> WildSites => _wildSites ?? [];

    /// <summary>
    /// The nest (its TOWN record) a squad was made for, as the nest spawned it (derived, not saved: a load's nests spawn theirs again); null: not a
    /// nest's.
    /// </summary>
    public TownData? NestOf(int squad) => _nestSquads.GetValueOrDefault(squad);

    /// <summary>
    /// Where the squads of wild site <paramref name="site"/> are in their life.
    /// </summary>
    public SiteStatus WildStatusOf(int site) => _wildStates[site].Status;
    /// <summary>
    /// The squad ids a wild site has now.
    /// </summary>
    public IReadOnlyList<int> WildSquadsOf(int site) => _wildStates[site].Squads;
    /// <summary>
    /// Where a nest site stood when it last loaded (null: never loaded, or not a nest).
    /// </summary>
    internal Vector2? WildCentreOf(int site) => site >= 0 && site < _wildCentres.Length ? _wildCentres[site] : null;

    /// <summary>
    /// A wild squad made: when, by a sector or a nest (its biome group or nest name), the squad, its members.
    /// </summary>
    public readonly record struct WildSpawn(long Tick, WildKind Kind, string Place, string Squad, int Members);
    /// <summary>
    /// The wild squads made since the last call (for the host's log; not part of the state).
    /// </summary>
    public List<WildSpawn> TakeWildLog()
    {
        var list = new List<WildSpawn>(_wildLog);
        _wildLog.Clear();
        return list;
    }

    #endregion

    #region The layout (pure given the data, the settings and the seed)

    /// <summary>
    /// Lays out the wild sites once, when the data has a biome map (with a loaded save's nests first).
    /// </summary>
    void EnsureWild(World world)
    {
        if (_wildSites is not null || _data.Biomes is not { } biomes) return;
        var saved = world.Saved;
        _wildSites = BuildWildSites(_data, _settings, biomes, world.Seed, saved?.Nests, States);
        _wildStates = new SiteState[_wildSites.Count];
        _wildCentres = new Vector2?[_wildSites.Count];
        for (int i = 0; i < _wildStates.Length; i++) _wildStates[i] = new SiteState();
        foreach (var site in _wildSites)
        {
            if (!_wildByZone.TryGetValue(site.Zone, out var list)) _wildByZone[site.Zone] = list = [];
            list.Add(site.Index);
        }

        if (saved is not null) BindSavedWild(world, saved);
    }

    /// <summary>
    /// The save's wild platoons to their sites (a nest platoon to the site of its nest, a homeless one to the sector it stands in), and the nests the
    /// save's player knows (their states' <c>discovered</c> / <c>explored</c>) to the map.
    /// </summary>
    void BindSavedWild(World world, Saving.SavedPopulation saved)
    {
        var byInstance = new Dictionary<string, int>(StringComparer.Ordinal);
        var sectorOf = new Dictionary<ZoneCoordinate, int>();
        foreach (var site in _wildSites!)
        {
            if (site.SaveInstance is { } instance) byInstance.TryAdd(instance, site.Index);
            if (site.Kind == WildKind.Sector) sectorOf.TryAdd(site.Zone, site.Index);
        }

        foreach (var sp in saved.Platoons)
        {
            if (sp.Wild is not { } kind) continue;
            if (kind == WildKind.Nest) sp.WildSite = sp.Nest is { } n && byInstance.TryGetValue(n.Instance, out int s) ? s : -1;
            else sp.WildSite = sectorOf.TryGetValue(WorldLayout.ZoneOf(sp.Source.Position.X, sp.Source.Position.Z), out int s) ? s : -1;
        }

        foreach (var nest in saved.Nests)
        {
            var state = nest.State;
            var flags = (state.Bools.GetValueOrDefault("discovered") ? TownKnowledge.Discovered : 0)
                | (state.Bools.GetValueOrDefault("explored") ? TownKnowledge.Explored : 0)
                | (state.Bools.GetValueOrDefault("recently discovered") ? TownKnowledge.Recent : 0);
            if (flags == 0 || !byInstance.TryGetValue(nest.Instance, out int site) || _wildSites[site].Nest is not { } town) continue;
            var faction = _wildSites[site].Faction >= 0 ? _data.Factions[_wildSites[site].Faction] : null;
            world.Player.KnowCamp(new CampView(site, town.Id, town.Name, faction?.Id ?? town.Faction ?? "", faction?.NotReal ?? false, nest.Position,
                flags), flags);
        }

        foreach (var nest in saved.Nests)
        {
            if (byInstance.TryGetValue(nest.Instance, out int site))
            {
                _nestSaveIds[site] = (nest.Instance, nest.Handle);
                NestPools(world, _wildSites[site]);   // a save's nest grows from now on, visited or not (rolled ones exist once they load)
            }
        }
    }

    /// <summary>
    /// The town-list entry and handle each nest site has in a save (a save's nests from the start; a rolled nest once a capture wrote it), so later
    /// saves reuse them.
    /// </summary>
    readonly Dictionary<int, (string Instance, Data.Save.Hand Handle)> _nestSaveIds = [];
    /// <summary>
    /// The save instance and handle remembered for nest site <paramref name="site"/>; false when none is.
    /// </summary>
    internal bool NestSaveId(int site, out (string Instance, Data.Save.Hand Handle) id) => _nestSaveIds.TryGetValue(site, out id);
    /// <summary>
    /// Remembers the save instance and handle of nest site <paramref name="site"/>.
    /// </summary>
    internal void RememberNest(int site, string instance, Data.Save.Hand handle) => _nestSaveIds[site] = (instance, handle);

    /// <summary>
    /// What a wild site holds already when it loads: the save's platoons bound to it that still have someone (squads, members, squads per template).
    /// </summary>
    sealed record WildHold(int Squads, int Members, Dictionary<string, int> Templates);

    /// <summary>
    /// What the save's kept wild platoons bound to the site hold; null when none does.
    /// </summary>
    WildHold? HoldOf(World world, int site)
    {
        WildHold? hold = null;
        int squads = 0, members = 0;
        foreach (var k in KeptWildOf(site))
        {
            hold ??= new WildHold(0, 0, new Dictionary<string, int>(StringComparer.Ordinal));
            squads++;
            members += k.Stored.Members.Count;
            hold.Templates[k.TemplateId] = hold.Templates.GetValueOrDefault(k.TemplateId) + 1;
        }

        if (world.Saved is null) return hold is null ? null : hold with { Squads = squads, Members = members };
        foreach (var p in world.Platoons.All)
        {
            if (p.Saved is not { Wild: not null } sp || sp.WildSite != site) continue;
            // A never-made one (SaveLoader.UnmadeWild) counts its `char count`, then, once made, its squad's living (LivingOf): it has no saved
            // members.
            int alive = sp.Members.Count == 0
                ? LivingOf(world, p)
                : sp.Members.Count(m => !m.Dead && !m.Gone && (m.Position is not null || m.Source.Medical?.Bools.GetValueOrDefault("dead") != true));
            if (alive == 0) continue;
            hold ??= new WildHold(0, 0, new Dictionary<string, int>(StringComparer.Ordinal));
            squads++;
            members += alive;
            hold.Templates[p.TemplateId] = hold.Templates.GetValueOrDefault(p.TemplateId) + 1;
        }

        return hold is null ? null : hold with { Squads = squads, Members = members };
    }

    /// <summary>
    /// The sectors (every in-map zone whose group has homeless spawns), then the nests of every group in record order: the <paramref name="saved"/>
    /// nests in it (a save's), then rolled ones up to the group's count.
    /// </summary>
    public static List<WildSite> BuildWildSites(PopulationData data, PopulationSettings settings, BiomeMap biomes, ulong seed,
        IReadOnlyList<Saving.SavedNest>? saved = null, IWorldStates? states = null)
    {
        states ??= data.States;
        var builder = new WildSiteBuilder(data, settings, biomes, seed, states);
        builder.AddSectors();
        builder.NoteSaved(saved);
        for (int g = 0; g < biomes.Groups.Count; g++) builder.AddNests(g);
        builder.AddUngrouped();
        return builder.Sites;
    }

    /// <summary>
    /// Builds the wild sites of one world: its sectors, the save's nests and the rolled ones, in <see cref="BuildWildSites"/>'s order.
    /// </summary>
    sealed class WildSiteBuilder
    {
        /// <summary>
        /// The population data.
        /// </summary>
        readonly PopulationData _data;

        /// <summary>
        /// The population settings (the nest multiplier).
        /// </summary>
        readonly PopulationSettings _settings;

        /// <summary>
        /// The biome map.
        /// </summary>
        readonly BiomeMap _biomes;

        /// <summary>
        /// The world seed.
        /// </summary>
        readonly ulong _seed;

        /// <summary>
        /// The world states gating towns.
        /// </summary>
        readonly IWorldStates _states;

        /// <summary>
        /// The biome groups.
        /// </summary>
        readonly IReadOnlyList<BiomeGroupData> _groups;

        /// <summary>
        /// Each group's cells.
        /// </summary>
        readonly List<(int X, int Z)>[] _cellsOf;

        /// <summary>
        /// A save's nests by the group of the cell they stand in.
        /// </summary>
        readonly Dictionary<int, List<Saving.SavedNest>> _savedOf = new();

        /// <summary>
        /// The site index of each saved nest added, until a roll takes it over.
        /// </summary>
        readonly Dictionary<Saving.SavedNest, int> _savedSite = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The sites built so far.
        /// </summary>
        readonly List<WildSite> _sites = new();

        /// <summary>
        /// The sites, in the order built.
        /// </summary>
        public List<WildSite> Sites => _sites;

        /// <summary>
        /// Takes the world's data; the groups' cell lists start empty.
        /// </summary>
        public WildSiteBuilder(PopulationData data, PopulationSettings settings, BiomeMap biomes, ulong seed, IWorldStates states)
        {
            (_data, _settings, _biomes, _seed, _states) = (data, settings, biomes, seed, states);
            _groups = _biomes.Groups;
            _cellsOf = new List<(int X, int Z)>[_groups.Count];
            for (int g = 0; g < _groups.Count; g++) _cellsOf[g] = [];
        }

        /// <summary>
        /// Every in-map zone whose group has homeless spawns is a sector; each group's cells are noted.
        /// </summary>
        public void AddSectors()
        {
            for (int z = 0; z < WeatherAreas.Cells; z++)
            {
                for (int x = 0; x < WeatherAreas.Cells; x++)
                {
                    int g = _biomes.GroupOf(x, z);
                    if (g < 0) continue;
                    _cellsOf[g].Add((x, z));
                    if (_groups[g].HomelessSpawnAmount > 0 && _groups[g].HomelessSpawns.Count > 0)
                        _sites.Add(new WildSite(_sites.Count, WildKind.Sector, new ZoneCoordinate(x, z), g, null, -1, []));
                }
            }
        }

        /// <summary>
        /// Files a save's nests by the group of the cell they stand in (-1: outside every group; they are kept all the same).
        /// </summary>
        public void NoteSaved(IReadOnlyList<Saving.SavedNest>? saved)
        {
            foreach (var n in saved ?? [])
            {
                if (_data.Db.Find(n.TownId) is not { Type: Meitou.Data.Fcs.FcsRecordType.Town }) continue;
                var (cx, cz) = WeatherAreas.CellOf(n.Position.X, n.Position.Z);
                int g = _biomes.GroupOf(cx, cz);
                if (!_savedOf.TryGetValue(g, out var list)) _savedOf[g] = list = [];
                list.Add(n);
            }
        }

        /// <summary>
        /// The saved nests outside every group.
        /// </summary>
        public void AddUngrouped()
        {
            foreach (var n in _savedOf.GetValueOrDefault(-1) ?? []) AddSaved(n, -1);
        }

        /// <summary>
        /// The nests of a group: the saved ones in it, then rolled ones up to its count.
        /// </summary>
        public void AddNests(int g)
        {
            var group = _groups[g];
            var mine = _savedOf.GetValueOrDefault(g) ?? [];
            foreach (var n in mine) AddSaved(n, g);
            int count = (int)MathF.Round(group.NumNests * _settings.NestsMultiplier);
            if (count <= 0 || group.Nests.Count == 0 || _cellsOf[g].Count == 0) return;
            ulong groupKey = Rng.Mix(SeedSalts.NestLayout ^ Rng.StableHash(group.Id));
            List<TownSite>? markers = null;
            if (group.NestsAtFixedMarkersOnly)
            {
                markers = [.. _data.Sites.Where(s => s.Town.Type == TownType.NestMarker
                    && WeatherAreas.CellOf(s.Position.X, s.Position.Z) is var (cx, cz) && _biomes.GroupOf(cx, cz) == g)];
                // A marker within 2500 (3D) of its nearest town (by XZ) is never used (FUN_1408f6350).
                markers.RemoveAll(m => NearestTown(_data, new Vector2(m.Position.X, m.Position.Z), out _) is { } town
                    && Vector3.DistanceSquared(m.Position, town.Position) < MarkerTownGap * MarkerTownGap);
                count = Math.Min(count, markers.Count);
            }

            // The roll runs in a fixed order as for a world without a save and leaves out each roll whose nest the save holds (a saved nest stands at
            // one of its spots, or its marker: a Meitou save writes a rolled nest at the spot it loaded at), until the group has its count. A save
            // holds only the nests made so far (docs/formats/save.md "Nests"), so whichever of them it holds, the others come out as before: a
            // left-out roll takes no room, a held one is never rolled twice, and the saved nest takes over that roll's key.
            int room = count - mine.Count;
            for (int i = 0; i < count && room > 0; i++)
            {
                if (RollNest(g, group, mine, markers, groupKey, i)) room--;
            }
        }

        /// <summary>
        /// One roll of a group's nest: its spots (a marker, else up to 8 in a random cell), then a saved nest there takes it over, else a town is
        /// picked.
        /// </summary>
        bool RollNest(int g, BiomeGroupData group, List<Saving.SavedNest> mine, List<TownSite>? markers, ulong groupKey, int i)
        {
            ulong key = Rng.Mix(groupKey ^ (ulong)i << 16);
            ulong pick = 0, place = SeedSalts.NestPlace;
            ulong Pick() => Rng.Hash(_seed, key, RngPurpose.Spawn, pick++);
            ulong Place() => Rng.Hash(_seed, key, RngPurpose.Spawn, place++);
            var spots = new List<Vector2>();
            if (markers is not null)
            {
                // A seeded draw without repeats: marker i of a shuffled order.
                int m = i + Rng.Int(Place(), markers.Count - i);
                (markers[i], markers[m]) = (markers[m], markers[i]);
                spots.Add(new Vector2(markers[i].Position.X, markers[i].Position.Z));
            }
            else
            {
                var (cx, cz) = _cellsOf[g][Rng.Int(Place(), _cellsOf[g].Count)];
                var (minX, minZ, maxX, maxZ) = WeatherAreas.CellRect(cx, cz);
                for (int t = 0; t < 16 && spots.Count < 8; t++)
                {
                    var p = new Vector2(minX + Rng.Float(Place()) * (maxX - minX), minZ + Rng.Float(Place()) * (maxZ - minZ));
                    if (!NearTown(_data, p, 2)) spots.Add(p);
                }

                if (spots.Count == 0) return false;
            }

            float near = markers is not null ? 100 : 1;
            if (mine.FirstOrDefault(n => spots.Any(s => Vector2.DistanceSquared(s, new Vector2(n.Position.X, n.Position.Z)) < near * near))
                is { } held)
            {
                if (_savedSite.Remove(held, out int at)) _sites[at] = _sites[at] with { Key = key };
                return false;
            }

            if (SquadFactory.WeightedPick(group.Nests, Gated, Pick) is not { } townId || _data.Db.Find(townId) is not { } record) return false;
            var town = TownData.From(record);
            var zone = WorldLayout.ZoneOf(spots[0].X, spots[0].Y);
            int faction = town.Faction is { } f ? _data.FactionIndex(f) : -1;
            _sites.Add(new WildSite(_sites.Count, WildKind.Nest, zone, g, town, faction, spots, Key: key));
            return true;
        }

        /// <summary>
        /// A saved nest added as a site of the group.
        /// </summary>
        void AddSaved(Saving.SavedNest n, int g)
        {
            _savedSite[n] = _sites.Count;
            var town = TownData.From(_data.Db.Find(n.TownId)!);
            int faction = town.Faction is { } f ? _data.FactionIndex(f) : -1;
            _sites.Add(new WildSite(_sites.Count, WildKind.Nest, WorldLayout.ZoneOf(n.Position.X, n.Position.Z), g, town, faction,
                [new Vector2(n.Position.X, n.Position.Z)], n.Instance,
                Rng.Mix(SeedSalts.NestLayout ^ Rng.StableHash(n.Instance))));
        }

        /// <summary>
        /// Whether a town is left out by a world state it needs.
        /// </summary>
        bool Gated(string townId) => _data.Db.Find(townId) is not { } r
            || r.GetReferences("world state").Any(w => !IWorldStates.Passes(_states, _data.Db, w.TargetStringId, w.Values.Value0));
    }

    /// <summary>
    /// Whether a point is within <paramref name="share"/> x the radius of a placed town (markers and points of interest do not count).
    /// </summary>
    static bool NearTown(PopulationData data, Vector2 p, float share)
    {
        foreach (var s in data.Sites)
        {
            if (s.Town.Type is TownType.NestMarker or TownType.PointOfInterest or TownType.Null) continue;
            float r = Math.Max(s.Town.SizeRadius * s.Town.TownRadiusMult, 100) * share;
            if (Vector2.DistanceSquared(p, new Vector2(s.Position.X, s.Position.Z)) < r * r) return true;
        }

        return false;
    }

    #endregion

    #region The look, in the slow phase (after the towns)

    /// <summary>
    /// The build being spawned one squad per tick (docs/game/ai.md "Squad factory queue"), and the next of its squads.
    /// </summary>
    (BuiltWild Build, int Next)? _spawningWild;

    /// <summary>
    /// The wild builds taken from the queue, waiting to spawn.
    /// </summary>
    readonly List<BuiltWild> _readyWild = [];

    /// <summary>
    /// The wild step: the sites of the active zones load, the loaded ones unload once inactive past <paramref name="grace"/>.
    /// </summary>
    void StepWild(World world, long grace)
    {
        EnsureWild(world);
        if (_wildSites is null) return;
        _wildBuffer.Clear();
        foreach (var zone in _active)
            if (_wildByZone.TryGetValue(zone, out var list)) _wildBuffer.AddRange(list);
        _wildBuffer.Sort();
        foreach (int i in _wildBuffer)
        {
            var state = _wildStates[i];
            if (state.Status == SiteStatus.Unloaded) LoadWild(world, _wildSites[i], state);
            else state.UnloadAtTick = -1;
        }

        _wildBuffer.Clear();
        _wildBuffer.AddRange(_wildLoaded);
        foreach (int i in _wildBuffer)
        {
            var state = _wildStates[i];
            if (_active.Contains(_wildSites[i].Zone)) continue;
            if (AnyMemberActive(world, state)) { state.UnloadAtTick = -1; continue; }
            if (state.UnloadAtTick < 0) state.UnloadAtTick = world.Tick + grace;
            else if (world.Tick >= state.UnloadAtTick)
            {
                if (KeepWild) KeepWildSquads(world, i, state);
                else
                {
                    KeepOccupiers(world, i, state);
                    PayNestDespawn(world, _wildSites[i], state);
                }

                Drop(world, state);
                FoldCamp(world, i);
                _wildLoaded.Remove(i);
            }
        }
    }

    /// <summary>
    /// The Meitou <see cref="KeepWild"/> unload: each squad of the site with someone alive keeps its characters (<see cref="StoredSquad"/>), so the
    /// site makes the same ones, wounds and all, where they stood when it loads again; nothing leaves a nest's pool. Faithful: they are dropped and a
    /// nest pays for the hurt ones (<c>PayNestDespawn</c>).
    /// </summary>
    void KeepWildSquads(World world, int site, SiteState state)
    {
        List<KeptWild>? kept = null;
        foreach (int id in state.Squads)
        {
            if (world.Squads.Find(id) is not { } squad) continue;
            var stored = new StoredSquad { Name = squad.Name, Shop = squad.Shop, NextHomeFillTick = squad.NextHomeFillTick,
                Package = squad.Ai?.Current?.Id, Ai = Saving.AiSave.Of(squad.Ai) ?? squad.SavedAi, Memory = Memory?.Take(squad.Id) };
            Store(world, squad, stored);
            world.Squads.Remove(id);
            if (stored.Members.Count == 0) continue;
            (kept ??= []).Add(new KeptWild(stored, squad.TemplateId, squad.Faction, squad.Money, squad.SpawnSize, squad.HomeCentre, squad.HomeRadius));
        }

        if (kept is not null) _keptWild[site] = kept;
    }

    /// <summary>
    /// A squad running an occupation, whose platoon the original makes persistent while it lasts (docs/game/ai.md "Package classes"): kept out of the
    /// world by every switch.
    /// </summary>
    static bool Occupying(Squad squad) => squad.Ai is { CurrentClass: Ai.PackageClass.OccupyTown };

    /// <summary>
    /// The Faithful unload's exception: the site's occupiers are kept as with <see cref="KeepWild"/> (taken out of <paramref name="state"/>), the
    /// rest left to be dropped.
    /// </summary>
    void KeepOccupiers(World world, int site, SiteState state)
    {
        SiteState? occupiers = null;
        foreach (int id in state.Squads)
            if (world.Squads.Find(id) is { } squad && Occupying(squad)) (occupiers ??= new SiteState()).Squads.Add(id);
        if (occupiers is null) return;
        foreach (int id in occupiers.Squads) state.Squads.Remove(id);
        KeepAlso(world, site, occupiers);
    }

    /// <summary>
    /// Keeps the squads of <paramref name="state"/> (<see cref="KeepWildSquads"/>) after the ones <paramref name="site"/> keeps already.
    /// </summary>
    void KeepAlso(World world, int site, SiteState state)
    {
        _keptWild.Remove(site, out var before);
        KeepWildSquads(world, site, state);
        if (before is null) return;
        if (_keptWild.TryGetValue(site, out var now)) before.AddRange(now);
        _keptWild[site] = before;
    }

    /// <summary>
    /// The site's kept squads into the world (serial, as it loads), counted as its own.
    /// </summary>
    void SpawnKeptWild(World world, int site, SiteState state)
    {
        if (!_keptWild.Remove(site, out var kept)) return;
        foreach (var k in kept)
        {
            if (OffscreenHealing) RecoverStored(world, k.Stored);
            if (SpawnKept(world, k.Stored, k.TemplateId, k.Faction, -1, -1, k.Stored.Anchor, k.Centre, k.Radius, k.SpawnSize) is not { } squad)
                continue;

            squad.Money = k.Money;
            state.Squads.Add(squad.Id);
            if (_wildSites![site].Kind == WildKind.Nest) _nestSquads[squad.Id] = _wildSites[site].Nest!;
        }
    }

    /// <summary>
    /// Starts loading a wild site: its squads built (on the build thread when there is one) with the players' spots it keeps clear of.
    /// </summary>
    void LoadWild(World world, WildSite site, SiteState state)
    {
        if (ZoneGate is { } gate && !gate(site.Zone)) return;   // retried at the next look
        state.Status = SiteStatus.Loading;
        state.LoadId++;
        _wildLoaded.Add(site.Index);
        long loadId = state.LoadId;
        ulong seed = world.Seed;
        var walk = world.Walkability;
        var players = PlayerSpots(world);
        var hold = HoldOf(world, site.Index);
        if (site.Kind == WildKind.Nest)
        {
            // The nest's pool (docs/simulation.md "Off-screen tier"): up to its current max, less the save's members it holds; a wiped-out nest
            // makes none.
            var pools = NestPools(world, site);
            int cap = pools.Ded ? 0 : (int)MathF.Floor(pools.Roaming.Current);
            var counts = new int[site.Nest!.RoamingSquads.Count];
            for (int i = 0; hold is not null && i < counts.Length; i++) counts[i] = hold.Templates.GetValueOrDefault(site.Nest.RoamingSquads[i].Id);
            var budget = new NestBudget(cap, hold?.Members ?? 0, counts, 0, 64, 64);
            if (_work is null) SpawnWild(world, BuildNest(site, loadId, seed, walk, players, budget));
            else _work.Add(() => _builtWild.Enqueue(BuildNest(site, loadId, seed, walk, players, budget)));
            return;
        }

        int room = IslandRoom(world);
        int held = hold?.Squads ?? 0;
        int share = HomelessCount(_data.Biomes!.Groups[site.Group], _settings.PopulationMultiplier);
        state.Planned = Math.Max(0, Math.Min(share, held + room) - held);

        if (_work is null) SpawnWild(world, BuildSector(site, loadId, seed, walk, players, hold, room: room));
        else _work.Add(() => _builtWild.Enqueue(BuildSector(site, loadId, seed, walk, players, hold, room: room)));
    }

    /// <summary>
    /// The squads the island still has room for (6.2, the area tick <c>FUN_1408f9240</c>: it makes squads while the loaded sectors' load is below the
    /// sum of P x <c>homeless spawn amount</c> over them, <b>Verified</b>): the sum over the loaded sectors (the one loading among them) less their
    /// squads in the world, those asked for and not spawned yet, and the save's homeless squads standing in them. A sector's load-time fill makes no
    /// more than that, so a save whose homeless squads already fill the island adds none.
    /// </summary>
    int IslandRoom(World world)
    {
        float target = 0, load = 0;
        foreach (int i in _wildLoaded)
        {
            var sector = _wildSites![i];
            if (sector.Kind != WildKind.Sector) continue;

            var state = _wildStates[i];
            target += MathF.Max(0, _settings.PopulationMultiplier * _data.Biomes!.Groups[sector.Group].HomelessSpawnAmount);
            load += state.Planned + state.Squads.Count(id => world.Squads.Find(id) is not null) + (HoldOf(world, i)?.Squads ?? 0);
        }

        return (int)MathF.Max(0, MathF.Ceiling(target - load - 1e-4f));
    }

    /// <summary>
    /// The living player characters' places on the ground plane.
    /// </summary>
    static Vector2[] PlayerSpots(World world)
    {
        var list = new List<Vector2>();
        var table = world.Characters;
        for (int i = 0; i < table.Previous.Length; i++)
        {
            if (table.Previous[i].Alive && table.Cold(i) is { IsPlayer: true })
                list.Add(new Vector2(table.Previous[i].Position.X, table.Previous[i].Position.Z));
        }

        return [.. list];
    }

    /// <summary>
    /// Spawns at most one squad of the finished wild builds (the original builds one queued squad request per rendered frame,
    /// <c>FUN_140583310</c>; a tick stands for a frame here). Returns whether it made one. A build is finished (its site marked loaded, the nest
    /// scattered) with its last squad.
    /// </summary>
    bool DrainWild(World world)
    {
        while (_builtWild.TryDequeue(out var queued)) _readyWild.Add(queued);
        _readyWild.Sort(static (a, b) => a.Site.CompareTo(b.Site));
        while (true)
        {
            if (_spawningWild is null)
            {
                if (_readyWild.Count == 0) return false;
                var b = _readyWild[0];
                _readyWild.RemoveAt(0);
                if (!BeginWild(world, b)) continue;
                _spawningWild = (b, 0);
            }

            var (build, next) = _spawningWild.Value;
            if (_wildStates[build.Site].LoadId != build.LoadId) { _spawningWild = null; continue; }   // unloaded meanwhile
            if (next >= build.Squads.Count) { EndWild(world, build); _spawningWild = null; continue; }   // nothing (more) to make costs no frame
            SpawnWildSquad(world, build, build.Squads[next]);
            _spawningWild = (build, next + 1);
            if (next + 1 >= build.Squads.Count) { EndWild(world, build); _spawningWild = null; }
            return true;
        }
    }

    /// <summary>
    /// A build made and applied at once (no worker thread).
    /// </summary>
    void SpawnWild(World world, BuiltWild b)
    {
        if (!BeginWild(world, b)) return;
        foreach (var built in b.Squads) SpawnWildSquad(world, b, built);
        EndWild(world, b);
    }

    /// <summary>
    /// Begins spawning a wild build: false when the site was unloaded meanwhile; else its state takes the build's rolls, a nest's centre and camp are
    /// stood up.
    /// </summary>
    bool BeginWild(World world, BuiltWild b)
    {
        var state = _wildStates[b.Site];
        if (state.LoadId != b.LoadId || state.Status != (b.TopUp ? SiteStatus.Loaded : SiteStatus.Loading)) return false;   // unloaded meanwhile
        state.Pending = false;
        state.Made = (b.TopUp ? state.Made : 0) + b.Rolls;
        var site = _wildSites![b.Site];
        if (site.Kind == WildKind.Nest && (b.Squads.Count > 0 || site.SaveInstance is not null)) _wildCentres[b.Site] = b.Centre;
        if (!b.TopUp) SpawnKeptWild(world, b.Site, state);
        if (b.Layout is { IsEmpty: false } layout) StandCamp(world, site.Index, layout, site.Faction, b.Centre, site.Nest!.Id, -1);
        return true;
    }

    /// <summary>
    /// Makes one squad of a wild build in the world; a nest's wanders round it.
    /// </summary>
    void SpawnWildSquad(World world, BuiltWild b, BuiltSquad built)
    {
        var state = _wildStates[b.Site];
        var site = _wildSites![b.Site];
        var squad = SpawnSquad(world, built, null, 0);
        if (site.Kind == WildKind.Nest)
        {
            squad.HomeCentre = b.Centre;
            squad.HomeRadius = NestRadius(site.Nest!);
            _nestSquads[squad.Id] = site.Nest!;
        }
        else squad.HomeRadius = _settings.HomelessHomeRadius;
        state.Squads.Add(squad.Id);
        if (_wildLog.Count < 256)
        {
            _wildLog.Add(new WildSpawn(world.Tick, site.Kind, site.Kind == WildKind.Nest ? site.Nest!.Name : _data.Biomes!.Groups[site.Group].Name,
                squad.Name, squad.Members.Count));
        }
    }

    /// <summary>
    /// A wild build is done: the site is loaded; a nest that ran out of rolls may be wiped out for good.
    /// </summary>
    void EndWild(World world, BuiltWild b)
    {
        var state = _wildStates[b.Site];
        var site = _wildSites![b.Site];
        state.Status = SiteStatus.Loaded;
        if (!b.TopUp) state.UnloadAtTick = -1;
        if (!b.TopUp) state.Planned = 0;
        if (site.Kind == WildKind.Nest && b.Exhausted) CheckWipe(world, site, state);
    }

    /// <summary>
    /// The radius a nest's squads wander in: its town radius, at least 100.
    /// </summary>
    static float NestRadius(TownData nest) => Math.Max(nest.SizeRadius * nest.TownRadiusMult, 100);

    #endregion

    #region Building (pure given its arguments; any thread)

    /// <summary>
    /// How near a town a nest marker may not be used (<b>Verified</b>, 6,250,000 squared in <c>FUN_1408f6350</c>).
    /// </summary>
    const float MarkerTownGap = 2500;

    /// <summary>
    /// The number of homeless squads a sector loaded on its own reaches (6.2): ceil(P x amount).
    /// </summary>
    public static int HomelessCount(BiomeGroupData group, float populationMultiplier) =>
        (int)MathF.Ceiling(MathF.Max(0, populationMultiplier * group.HomelessSpawnAmount) - 1e-4f);

    /// <summary>
    /// The template weights of 6.2's homeless branch for one sector: <c>max(0, v0 - k x count)</c>, k = (sum of v0) / (P x amount), with
    /// <paramref name="counts"/> the squads of each entry already made; entries that are no template or are gated get 0.
    /// </summary>
    public static float[] HomelessWeights(BiomeGroupData group, float populationMultiplier, IReadOnlyList<int> counts, Func<string, bool> usable)
    {
        var entries = group.HomelessSpawns;
        var w = new float[entries.Count];
        float sum = 0;
        foreach (var e in entries) sum += Math.Max(0, e.V0);
        float target = populationMultiplier * group.HomelessSpawnAmount;
        if (target <= 0 || sum <= 0) return w;
        float k = sum / target;
        for (int i = 0; i < entries.Count; i++)
            w[i] = entries[i].V0 > 0 && usable(entries[i].Id) ? MathF.Max(0, entries[i].V0 - k * counts[i]) : 0;

        return w;
    }

    /// <summary>
    /// One roll over a weight table (the running sum that first exceeds roll x total), or -1 when every weight is 0.
    /// </summary>
    public static int Pick(IReadOnlyList<float> weights, ulong roll)
    {
        float total = 0;
        foreach (var w in weights) total += w;
        if (total <= 0) return -1;
        float pick = Rng.Float(roll) * total;
        int last = -1;
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0) continue;
            last = i;
            if ((pick -= weights[i]) < 0) return i;
        }

        return last;
    }

    /// <summary>
    /// Whether a squad template exists, is a template and its world states pass now.
    /// </summary>
    bool TemplateUsable(string id) =>
        _data.Db.Find(id) is { } r && SquadFactory.IsTemplate(r)
            && SquadTemplate.From(r).WorldState.All(w => IWorldStates.Passes(States, _data.Db, w.Id, w.V0));

    /// <summary>
    /// A sector's squads as it loads, or with <paramref name="topUp"/> ≥ 0 the one squad of the area tick's refill number <paramref name="topUp"/>
    /// (<c>PopulationHomeless.cs</c>).
    /// </summary>
    BuiltWild BuildSector(WildSite site, long loadId, ulong seed, IWalkability walk, Vector2[] players, WildHold? hold, int topUp = -1,
        int room = int.MaxValue)
    {
        var squads = new List<BuiltSquad>();
        var group = _data.Biomes!.Groups[site.Group];
        var (minX, minZ, maxX, maxZ) = WeatherAreas.CellRect(site.Zone.X, site.Zone.Y);
        var centre = new Vector2((minX + maxX) / 2, (minZ + maxZ) / 2);

        // A sector whose nearest town is the player's makes nothing (6.2).
        if (NearestTown(centre, out _) is { } nearest && _data.PlayerFaction >= 0 && _data.SiteFactions[nearest.Index] == _data.PlayerFaction)
            return new BuiltWild(site.Index, loadId, centre, squads, TopUp: topUp >= 0);
        // A sector whose own town is a nest with roaming squads makes none of the biome's: the original's maker then draws from the nest's
        // pool within its free budget (docs/game/factions-squads-towns.md "Homeless squads"), which Meitou leaves to the nest's own site.
        if (OwnNest(site, centre) is { RoamingSquads.Count: > 0 })
            return new BuiltWild(site.Index, loadId, centre, squads, TopUp: topUp >= 0);

        int n = HomelessCount(group, _settings.PopulationMultiplier);
        var counts = new int[group.HomelessSpawns.Count];

        // The save's homeless squads standing in this sector are of its count (and of their entry's).
        for (int i = 0; hold is not null && i < counts.Length; i++) counts[i] = hold.Templates.GetValueOrDefault(group.HomelessSpawns[i].Id);
        int first = hold?.Squads ?? 0;

        // At load no more than the island has room for (IslandRoom).
        if (topUp < 0 && room < n - first) n = first + Math.Max(0, room);

        // A refill's key follows the load's (n of them) and the refills before it.
        if (topUp >= 0) (first, n) = (n + topUp, n + topUp + 1);
        for (int s = first; s < n; s++)
        {
            ulong key = Rng.Mix(SeedSalts.HomelessSquad ^ (ulong)site.Zone.X << 40 ^ (ulong)site.Zone.Y << 24 ^ (uint)s);
            var weights = HomelessWeights(group, _settings.PopulationMultiplier, counts, TemplateUsable);
            int e = Pick(weights, Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.HomelessPick));
            if (e < 0) break;
            counts[e]++;
            var template = SquadTemplate.From(_data.Db.Find(group.HomelessSpawns[e].Id)!);
            int faction = _data.SpawnFactionOf(template);
            if (PlaceInSector(minX, minZ, maxX, maxZ, faction, seed, key, walk, players) is not { } spot) continue;
            if (BuildOne(template, faction, faction >= 0 ? _data.Factions[faction] : null, key, seed, spot) is { } one) squads.Add(one);
        }

        return topUp >= 0
            ? new BuiltWild(site.Index, loadId, centre, squads, Rolls: 1, TopUp: true)
            : new BuiltWild(site.Index, loadId, centre, squads);
    }

    /// <summary>
    /// The placed town nearest <paramref name="p"/> (no nest markers, points of interest or null towns), with its distance.
    /// </summary>
    TownSite? NearestTown(Vector2 p, out float distance) => NearestTown(_data, p, out distance);

    /// <summary>
    /// The nest that is a sector's own town (<c>FUN_1408f62e0</c>: the town nearest the sector's centre, when it stands in the sector): the nearest
    /// nest site when no level-data town is nearer and it stands in the sector, or the nearest level-data town when it is a nest standing in the
    /// sector, else null. A rolled nest counts at its first spot (it settles among its spots as it loads).
    /// </summary>
    TownData? OwnNest(WildSite sector, Vector2 centre)
    {
        var town = NearestTown(centre, out float nearest);
        WildSite? own = null;
        foreach (var s in _wildSites!)
        {
            if (s.Kind != WildKind.Nest || s.Spots.Count == 0) continue;
            float d = Vector2.Distance(centre, s.Spots[0]);
            if (d < nearest) (own, nearest) = (s, d);
        }

        if (own is not null) return own.Zone == sector.Zone ? own.Nest : null;
        return town is { Town.Type: TownType.Nest } && town.Zone == sector.Zone ? town.Town : null;
    }

    /// <summary>
    /// The placed town of <paramref name="data"/> nearest <paramref name="p"/> (no nest markers, points of interest or null towns), with its
    /// distance; null when there is none.
    /// </summary>
    static TownSite? NearestTown(PopulationData data, Vector2 p, out float distance)
    {
        TownSite? best = null;
        distance = float.MaxValue;
        foreach (var s in data.Sites)
        {
            if (s.Town.Type is TownType.NestMarker or TownType.PointOfInterest or TownType.Null) continue;
            float d = Vector2.Distance(p, new Vector2(s.Position.X, s.Position.Z));
            if (d < distance) (best, distance) = (s, d);
        }

        return best;
    }

    /// <summary>
    /// The spot of a homeless squad (6.2, <c>FUN_1408f50d0</c>): 40 seeded tries of a point of the sector, pushed out of towns and away from the
    /// player.
    /// </summary>
    Vector2? PlaceInSector(float minX, float minZ, float maxX, float maxZ, int faction, ulong seed, ulong key, IWalkability walk, Vector2[] players)
    {
        for (int t = 0; t < 40; t++)
        {
            ulong roll = Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.WildPlace + (ulong)t);
            var p = new Vector2(minX + Rng.Float(roll) * (maxX - minX), minZ + Rng.Float(Rng.Mix(roll)) * (maxZ - minZ));
            if (NearestTown(p, out _) is { } nearest) p = PushOut(p, nearest, 2);
            foreach (var s in _data.Sites)
            {
                if (s.Town.Type is TownType.NestMarker or TownType.PointOfInterest or TownType.Null) continue;
                if (_data.SiteFactions[s.Index] != faction) p = PushOut(p, s, 1);
            }

            bool tooClose = false;
            foreach (var at in players)
                if (Vector2.DistanceSquared(at, p) < _settings.HomelessPlayerDistance * _settings.HomelessPlayerDistance) { tooClose = true; break; }
            if (tooClose || !walk.IsWalkable(p.X, p.Y)) continue;
            return p;
        }

        return null;
    }

    /// <summary>
    /// Pushes <paramref name="p"/> out of the town's radius times <paramref name="share"/> (a point at the centre goes +X).
    /// </summary>
    static Vector2 PushOut(Vector2 p, TownSite town, float share)
    {
        var c = new Vector2(town.Position.X, town.Position.Z);
        float r = town.Town.SizeRadius * town.Town.TownRadiusMult * share;
        var d = p - c;
        float len = d.Length();
        if (len >= r || r <= 0) return p;
        return len < 1e-3f ? c + new Vector2(r, 0) : c + d / len * r;
    }

    /// <summary>
    /// Builds a nest's roaming squads as it loads: its centre (the budget's, else its first walkable spot) and the squads its pool lacks.
    /// </summary>
    BuiltWild BuildNest(WildSite site, long loadId, ulong seed, IWalkability walk, Vector2[] players, NestBudget budget)
    {
        var squads = new List<BuiltSquad>();
        var nest = site.Nest!;
        Vector2? centre = budget.Centre;
        if (centre is null)
        {
            foreach (var s in site.Spots)
                if (walk.IsWalkable(s.X, s.Y)) { centre = s; break; }
        }

        bool topUp = budget.Centre is not null;

        // no walkable spot: the nest is dormant this load
        if (centre is not { } c) return new BuiltWild(site.Index, loadId, site.Spots[0], squads, TopUp: topUp);
        int cap = budget.Cap;
        var entries = nest.RoamingSquads;
        var counts = (int[])budget.Counts.Clone();
        var templates = new SquadTemplate?[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].V0 > 0 && TemplateUsable(entries[i].Id)) templates[i] = SquadTemplate.From(_data.Db.Find(entries[i].Id)!);
        float radius = NestRadius(nest);

        // The members already of its pool (the save's squads, or the nest's in the world) count against the cap, their templates against the weights.
        int members = budget.Members;
        var weights = new float[entries.Count];
        bool exhausted = false;
        int rolls = 0;
        for (int s = 0; s < budget.Rolls && squads.Count < budget.Squads && (members < cap || topUp); s++)
        {
            // An entry fits the free room, or any size does while the pool is empty and more than 4 are free (FUN_1408f7e80).
            int free = cap - members;
            for (int i = 0; i < entries.Count; i++)
            {
                weights[i] = templates[i] is { } t && (SquadFactory.ExpectedSize(t, _settings.SquadSizeMultiplier) <= free || members == 0 && free > 4)
                    ? MathF.Max(0, entries[i].V0 - counts[i]) : 0;
            }

            ulong key = Rng.Mix(Rng.Mix(SeedSalts.NestSquad ^ site.Key) ^ (uint)(budget.First + s));
            rolls++;
            int e = Pick(weights, Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.NestPick));
            if (e < 0)
            {
                exhausted = true;
                break;
            }

            if (!budget.Place) break;
            counts[e]++;
            var template = templates[e]!;
            int faction = _data.SpawnFactionOf(template, site.Nest is not null ? site.Faction : null);
            Vector2? spot = null;
            for (int t = 0; t < 12 && spot is null; t++)
            {
                var p = Rng.PointInDisc(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.WildPlace + (ulong)t), c, radius * 0.6f);
                bool tooClose = false;
                foreach (var at in players)
                    if (Vector2.DistanceSquared(at, p) < _settings.NestPlayerDistance * _settings.NestPlayerDistance) { tooClose = true; break; }
                if (!tooClose && walk.IsWalkable(p.X, p.Y)) spot = p;
            }

            if (spot is null) continue;
            if (BuildOne(template, faction, faction >= 0 ? _data.Factions[faction] : null, key, seed, spot.Value) is not { } one) continue;
            squads.Add(one);
            members += one.Plan.TotalMembers;
        }

        // keyed by place: a save names its cages
        var layout = topUp ? null : NestFurniture.Scatter(_data.Db, nest, c, walk, seed, $"nest{(int)c.X},{(int)c.Y}:");
        return new BuiltWild(site.Index, loadId, c, squads, rolls, exhausted, topUp, layout);
    }

    #endregion
}
