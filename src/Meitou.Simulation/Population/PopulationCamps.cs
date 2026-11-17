using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Economy;
using Meitou.Data.World;
using Meitou.Simulation.Perception;

namespace Meitou.Simulation.Population;

/// <summary>
/// The camps (docs/game/factions-squads-towns.md "Nest furniture"): what a nest, or a town with <c>spawn in town centre</c>, scatters at its place
/// (<see cref="NestFurniture"/>). A nest's stands while its zone is loaded; a town's once it is first made, for good (its fixtures stay registered as
/// the town's do). The items go down once per camp, and only in a zone the loaded save has no file of (the original lays them only in a zone loaded
/// fresh).
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>
    /// A camp's buildings and meshes go up (the old ones of the camp come down first), their cages and locks registered as its faction's, and its
    /// items laid when due: <paramref name="fresh"/> (laid only in a zone the save has no file of) and the camp's first time.
    /// <paramref name="furniture"/> is what its pieces give the homes they stand round.
    /// </summary>
    void StandCamp(World world, int camp, NestLayout layout, int owner, Vector2 centre, string record, int site, bool fresh,
        IReadOnlyDictionary<string, List<HomeSpot>>? furniture = null)
    {
        FoldCamp(world, camp);
        world.NestBuildings.Set(camp, layout, owner, centre, record, furniture);
        foreach (var piece in layout.Pieces) world.Fixtures.RegisterNestBuilding(world, _data, piece, owner, site, camp >= 0 ? camp : -1);
        if (layout.Drops.Count > 0 && fresh && world.NestBuildings.Stock(camp)) LayItems(world, camp, layout.Drops, owner, centre);
    }

    /// <summary>
    /// A camp's buildings and meshes come down (a nest's when it unloads; it stands them up again when it loads).
    /// </summary>
    static void FoldCamp(World world, int camp)
    {
        var pieces = world.NestBuildings.Of(camp);
        if (pieces.Count > 0) world.Fixtures.UnregisterNestBuildings(world, pieces);
        world.NestBuildings.Remove(camp);
    }

    /// <summary>
    /// A town's camp, stood up when the town is made and again only when its TOWN record changed since: a town with <c>spawn in town centre</c>
    /// scatters at its own position (<c>Town::setup</c> queues it), and every town stands its residents' nests round their homes
    /// (<see cref="ResidentNests"/>). The items go down once, in fresh zones only.
    /// </summary>
    void StandTownCamp(World world, TownSite site)
    {
        var town = site.Town;
        int camp = NestBuildings.TownCamp(site.Index);
        if (world.NestBuildings.RecordOf(camp) == town.Id) return;

        var at = new Vector2(site.Position.X, site.Position.Z);
        int owner = site.Index < _data.SiteFactions.Count ? _data.SiteFactions[site.Index] : -1;
        var layout = town.SpawnInTownCentre
            ? NestFurniture.Scatter(_data.Db, town, at, world.Walkability, world.Seed, $"town{site.Index}:")
            : new NestLayout([], [], []);
        if (world.SavedZones.Contains(WorldLayout.ZoneOf(at.X, at.Y))) layout.Drops.Clear();

        var furniture = ResidentNests(world, site, layout);
        if (!layout.IsEmpty) StandCamp(world, camp, layout, owner, at, town.Id, site.Index, fresh: true, furniture);
    }

    /// <summary>
    /// The TOWN each home's resident template names as its <c>nest</c>, scattered round that home, building by building, into the town's camp
    /// <paramref name="layout"/> (docs/formats/zones.md "Nest debris and loot"): its buildings and meshes every time (<b>Observed</b>: the Hub
    /// squatters' ruins hold a campfire, stools and a camp bed in the original after a load), its items (<c>loot spawn</c>, <c>debris</c> with
    /// physics) only round a home in a zone the save has no file of (<b>Observed</b> in original saves: within the nest's <c>size radius</c> of a
    /// home of the template, not round the town's centre). A building of the player's lays none: the original lays a home's nest when housing sets
    /// the home up (<c>FUN_14057f1f0</c> hands the town the nest with the home), and housing passes over the player's buildings. Returns the
    /// furniture the pieces give each home (staying home takes it).
    /// </summary>
    Dictionary<string, List<HomeSpot>> ResidentNests(World world, TownSite site, NestLayout layout)
    {
        var furniture = new Dictionary<string, List<HomeSpot>>(StringComparer.Ordinal);
        var housed = new HashSet<string>(StringComparer.Ordinal);
        int player = world.Player.Faction >= 0 ? world.Player.Faction : _data.PlayerFaction;
        var players = player >= 0 ? world.Fixtures.PlayerOwnedIn(_data, site.Index, player) : [];
        foreach (var home in site.Buildings)
        {
            string templateId = world.Fixtures.ResidentsTemplate(home);
            housed.Add(templateId);
            housed.Add(home.ResidentsTemplate);
            if (players.Contains(home.Key) || _data.Db.Find(templateId) is not { } template) continue;

            var at = new Vector2(home.Position.X, home.Position.Z);
            bool fresh = !world.SavedZones.Contains(home.Zone);
            var ray = HomeRayOf(world, site, home, layout);
            foreach (var link in template.GetReferences("nest"))
            {
                var nest = NestAt(world, at, link.TargetStringId, $"town{site.Index}:{home.Key}:{link.TargetStringId}:", ray);
                if (nest is not null) Merge(layout, nest, fresh, home, HomeFurniture(furniture, home.Key));
            }
        }

        layout.Drops.AddRange(HomelessNestDrops(world, site, housed));
        return furniture;
    }

    /// <summary>
    /// The furniture list of home <paramref name="key"/> in <paramref name="furniture"/>, made when it has none yet.
    /// </summary>
    static List<HomeSpot> HomeFurniture(Dictionary<string, List<HomeSpot>> furniture, string key)
    {
        if (!furniture.TryGetValue(key, out var spots)) furniture[key] = spots = [];
        return spots;
    }

    /// <summary>
    /// Adds a home's nest scatter to the town's camp: its pieces (marked as <paramref name="home"/>'s) and meshes, its items when
    /// <paramref name="fresh"/>, and each piece that is a seat or a workstation to the home's <paramref name="furniture"/>.
    /// </summary>
    void Merge(NestLayout layout, NestLayout nest, bool fresh, TownBuilding home, List<HomeSpot> furniture)
    {
        layout.Pieces.AddRange(nest.Pieces.Select(p => p with { Home = home.Key }));
        layout.Meshes.AddRange(nest.Meshes.Select(m => m with { Home = home.Key }));
        if (fresh) layout.Drops.AddRange(nest.Drops);

        foreach (var piece in nest.Pieces)
        {
            if (_data.Db.Find(piece.Record) is not { } record) continue;
            if (TownBuildings.FurnitureSpot(_data.Db, record, piece.Position, TownBuildings.YawOf(piece.Rotation)) is { } spot) furniture.Add(spot);
        }
    }

    #region Placement ray

    /// <summary>
    /// How far from its placement a building's collision can reach (2003 in the base game, Large Dock Wreck02; docs/formats/camera.md): buildings
    /// placed further from a spot cannot lie over it.
    /// </summary>
    const float RayReach = 2100;

    /// <summary>
    /// The placement ray of the pieces of <paramref name="home"/>'s nest (<see cref="HomeRay"/>): besides the home it may meet the town's other
    /// buildings, the player's, the pieces laid before (this town's camp so far, <paramref name="town"/>, and the other camps standing).
    /// </summary>
    HomeRay HomeRayOf(World world, TownSite site, TownBuilding home, NestLayout town)
    {
        var others = world.NestBuildings.PiecesBesides(NestBuildings.TownCamp(site.Index)).ToList();
        return (spot, earlier) => HomeHit(world, home, spot, town.Pieces.Concat(others).Concat(earlier));
    }

    /// <summary>
    /// Where the original's placement ray (<c>FUN_140930120</c>: straight down from 10000 over the spot, mask <c>0x204</c>, the terrain and group 9)
    /// meets <paramref name="home"/>, as a ruin or standing as it is now, when the home is what it meets first; null when it meets the terrain or
    /// another building or piece first, or misses the home.
    /// </summary>
    GroundHit? HomeHit(World world, TownBuilding home, Vector2 spot, IEnumerable<NestPiece> pieces)
    {
        if (_data.Sight is not { } sight) return null;
        if (sight.TopAt(home, world.Fixtures.IsDestroyed(home), spot) is not { } hit) return null;
        if (hit.Y < world.Walkability.TerrainHeight(spot.X, spot.Y)) return null;

        bool covered = TownAbove(world, sight, home, spot, hit.Y)
            || SitesAbove(world, sight, spot, hit.Y)
            || PiecesAbove(sight, spot, hit.Y, pieces);
        return covered ? null : hit;
    }

    /// <summary>
    /// Whether a town building other than <paramref name="home"/> (any town's) has a group-9 surface over <paramref name="spot"/> above
    /// <paramref name="y"/>.
    /// </summary>
    bool TownAbove(World world, ISightSource sight, TownBuilding home, Vector2 spot, float y)
    {
        foreach (var site in _data.Sites)
        {
            foreach (var b in site.Buildings)
            {
                if (b.Key == home.Key || !Near(b.Position, spot)) continue;
                if (sight.TopAt(b, world.Fixtures.IsDestroyed(b), spot) is { } other && other.Y > y) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a player building of its own (not a town house's rebuild) has a group-9 surface over <paramref name="spot"/> above
    /// <paramref name="y"/>: one under construction joins group 9 once its progress reaches its target times its visual threshold
    /// (<c>FUN_14055f6e0</c> adds 14 to the group below it; clamp(<c>build threshold</c> / 100, 0, 0.95), docs/game/buildings-production.md).
    /// </summary>
    bool SitesAbove(World world, ISightSource sight, Vector2 spot, float y)
    {
        foreach (var s in world.Buildings.All)
        {
            if (s.LevelPlacement is not null || !Near(s.Position, spot) || !Solid(s)) continue;
            var other = sight.TopAt(s.Record, s.Id, s.Position, s.Rotation, s.Destroyed, spot);
            if (other?.Y > y) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a site's parts are in their ground groups: complete, or built past its visual threshold.
    /// </summary>
    bool Solid(Construction.BuildSite s)
    {
        if (s.Complete) return true;
        if (_data.Db.Find(s.Record) is not { } record) return false;

        float threshold = Math.Clamp(Meitou.Data.Gameplay.Construction.BuildingBuildData.From(record).BuildThreshold / 100, 0, 0.95f);
        return s.Progress >= s.Target * threshold;
    }

    /// <summary>
    /// Whether one of <paramref name="pieces"/> has a group-9 surface over <paramref name="spot"/> above <paramref name="y"/>.
    /// </summary>
    static bool PiecesAbove(ISightSource sight, Vector2 spot, float y, IEnumerable<NestPiece> pieces)
    {
        foreach (var p in pieces)
        {
            if (!Near(p.Position, spot)) continue;
            var other = sight.TopAt(p.Record, p.Id, p.Position, p.Rotation, false, spot);
            if (other?.Y > y) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a building placed at <paramref name="position"/> may lie over <paramref name="spot"/> (within <see cref="RayReach"/>, X/Z).
    /// </summary>
    static bool Near(Vector3 position, Vector2 spot) => Vector2.DistanceSquared(new Vector2(position.X, position.Z), spot) < RayReach * RayReach;

    #endregion

    /// <summary>
    /// The nests of the town's resident templates that none of its buildings houses (<paramref name="housed"/>: the templates housed), rolled
    /// round the town's centre (<b>engine choice</b>: where the original lays these is Unknown); none in a zone the save has a file of.
    /// </summary>
    List<NestDrop> HomelessNestDrops(World world, TownSite site, HashSet<string> housed)
    {
        var drops = new List<NestDrop>();
        var at = new Vector2(site.Position.X, site.Position.Z);
        if (world.SavedZones.Contains(WorldLayout.ZoneOf(at.X, at.Y))) return drops;

        var nests = site.Town.Residents
            .Where(r => !housed.Contains(r.Id))
            .Select(r => _data.Db.Find(r.Id))
            .OfType<Data.GameRecord>()
            .SelectMany(t => t.GetReferences("nest"))
            .Select(n => n.TargetStringId)
            .Distinct(StringComparer.Ordinal);
        foreach (string id in nests) drops.AddRange(NestAt(world, at, id, $"town{site.Index}:{id}:")?.Drops ?? []);

        return drops;
    }

    /// <summary>
    /// What the nest <paramref name="nestId"/> scatters round <paramref name="at"/>, its pieces placed by <paramref name="homeRay"/> when it is a
    /// home's; null when it is no TOWN.
    /// </summary>
    NestLayout? NestAt(World world, Vector2 at, string nestId, string idPrefix, HomeRay? homeRay = null)
    {
        if (_data.Db.Find(nestId) is not { Type: Meitou.Data.Fcs.FcsRecordType.Town } nest) return null;

        ulong seed = world.Seed ^ Rng.StableHash(nestId);
        return NestFurniture.Scatter(_data.Db, TownData.From(nest), at, world.Walkability, seed, idPrefix, homeRay);
    }

    /// <summary>
    /// The town leaves the world (<see cref="Unload"/>): the camp pieces round the houses the player bought there go, as the original's town takes
    /// its nest buildings down with it and lays none round such a house again (the pieces standing on one went at its purchase).
    /// </summary>
    void FoldBoughtCamp(World world, int site)
    {
        int player = world.Player.Faction >= 0 ? world.Player.Faction : _data.PlayerFaction;
        if (player < 0) return;

        var gone = world.NestBuildings.DropHomes(NestBuildings.TownCamp(site), world.Fixtures.PlayerOwnedIn(_data, site, player));
        if (gone.Count > 0) world.Fixtures.UnregisterNestBuildings(world, gone);
    }

    /// <summary>
    /// The camp's items on the ground: a <c>debris</c> item as it is, a <c>loot spawn</c> entry rolled from its VENDOR_LIST as an item slot's is
    /// (<see cref="Items.ItemSlots.Roll"/>, with the camp's faction for the grade). Loose, owned by nobody, tagged as the camp's (kept past the
    /// dropped-item limit, saved with its <c>persist</c> handle).
    /// </summary>
    void LayItems(World world, int camp, List<NestDrop> drops, int owner, Vector2 centre)
    {
        ulong key = Rng.Mix(SeedSalts.NestFurniture ^ (ulong)(uint)(int)centre.X ^ (ulong)(uint)(int)centre.Y << 32 ^ 0x1007UL);
        var rng = new RandomStream(world.Seed, key, RngPurpose.Loot);

        foreach (var drop in drops)
        {
            var item = drop.List ? Loot(world, drop, owner, ref rng) : _data.Items.Create(drop.Record, 1, 0);
            if (item is not null) world.GroundItems.Place(item, drop.Position, drop.Yaw, null, -1, camp);
        }
    }

    /// <summary>
    /// A <c>loot spawn</c> entry's item, rolled from its list's table, fetched for the entry from the session's list cache
    /// (<c>FUN_140938440</c>, <b>Verified</b>, decompile); null for a missing list or no item.
    /// </summary>
    Items.ItemInstance? Loot(World world, NestDrop drop, int owner, ref RandomStream rng)
    {
        if (_data.Db.Find(drop.Record) is not { } record) return null;

        var list = VendorList.From(_data.Db, record);
        return Items.ItemSlots.Roll(_data, list, world.VendorTables.Fetch(_data, list, owner), owner, ref rng).Item;
    }
}
