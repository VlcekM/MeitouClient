using System.Numerics;
using Meitou.Content;

namespace Meitou.Data.World;

/// <summary>
/// Everything the foliage placer reads, opened once, so a zone's foliage is one <see cref="Zone"/> call
/// (docs/formats/foliage.md): the FOLIAGE catalog, the heightmap (its own handle), the blend file's per-zone biome list,
/// <c>biomemap.png</c>, the overlay tiles (cached) and the towns with their <c>no-foliage range</c>.
/// Not thread-safe: use one instance per worker thread.
/// </summary>
public sealed class FoliageWorld : IDisposable
{
    readonly GameInstall _install;
    readonly GameDatabase _db;
    readonly WorldLevelData _world;
    readonly FoliageLayoutCache? _cache;
    // Opened on the first zone that is actually placed: a zone read from the layout cache needs none of them.
    TerrainHeightmap? _map;
    BlendInfoFile? _blend;
    FoliageBiomeMap? _biomes;
    List<FoliageTown>? _towns;
    readonly Dictionary<(int, int), byte[]> _tiles = [];
    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), Lazy<byte[]>>? _sharedTiles;

    public FoliageWorld(GameInstall install, GameDatabase db, WorldLevelData world, FoliageCatalog? catalog = null,
        System.Collections.Concurrent.ConcurrentDictionary<(int, int), Lazy<byte[]>>? sharedTiles = null, FoliageLayoutCache? cache = null)
    {
        this._sharedTiles = sharedTiles;
        this._install = install;
        this._db = db;
        this._world = world;
        this._cache = cache;
        Catalog = catalog ?? FoliageCatalog.Load(db);
    }

    public FoliageCatalog Catalog { get; }

    /// <summary>
    /// The towns the player's faction owned when the world was loaded, by <see cref="TownKey"/> (set by the host after a load or a new game; a snapshot: a town founded or captured later changes
    /// the placement of resource rocks only when its zone is placed again, docs/formats/foliage.md "Towns").
    /// </summary>
    public static IReadOnlySet<(int X, int Z)> PlayerTowns { get; set; } = new HashSet<(int, int)>();

    /// <summary>A town's key in <see cref="PlayerTowns"/>: its X/Z rounded to whole units.</summary>
    public static (int X, int Z) TownKey(float x, float z) => ((int)MathF.Round(x), (int)MathF.Round(z));

    /// <summary>The towns that keep foliage away (not nest markers): X/Z and <c>no-foliage range</c>.</summary>
    public static List<FoliageTown> ReadTowns(GameDatabase db, WorldLevelData world)
    {
        var list = new List<FoliageTown>();
        foreach (var t in world.Towns())
            if (db.Find(t.TownId) is { } record && record.GetInt("type") != BuildingTowns.NestMarkerType)
                list.Add(new FoliageTown(new Vector2(t.Position.X, t.Position.Z), record.GetFloat("no-foliage range", 2000), PlayerTowns.Contains(TownKey(t.Position.X, t.Position.Z))));
        return list;
    }

    /// <summary>Opens the files the placer reads (the first time a zone is placed).</summary>
    void Open()
    {
        if (_map is not null) return;
        _towns = ReadTowns(_db, _world);
        _blend = BlendInfoFile.Open(_install);
        _biomes = FoliageBiomeMap.Open(_install);
        _map = TerrainHeightmap.Open(_install);
    }

    /// <summary>The biome colours of a zone, in blend slot order: the slots with weight anywhere in the zone's box.</summary>
    public List<uint> ZoneBiomes(ZoneCoordinate zone)
    {
        Open();
        var (x0, z0) = WorldLayout.ZoneOrigin(zone);
        var (cx, cz) = _blend!.CellOf(x0 + WorldLayout.ZoneSize / 2.0, z0 + WorldLayout.ZoneSize / 2.0);
        double size = _blend.CellSize;
        double u0 = (x0 + WorldLayout.HalfWorldSize - cx * size) / size, v0 = (z0 + WorldLayout.HalfWorldSize - cz * size) / size;
        double du = WorldLayout.ZoneSize / size;
        const double inset = 0.001;
        byte mask = _blend.SlotMask(cx, cz, u0 + inset, v0 + inset, u0 + du - inset, v0 + du - inset);
        var list = new List<uint>();
        for (int s = 0; s < BlendInfoFile.SlotsPerCell; s++)
            if ((BlendInfoFile.MaskSlots(mask) & (1 << s)) != 0 && _blend.Slot(cx, cz, s) is var c && c != 0 && !list.Contains(c)) list.Add(c);
        return list;
    }

    FoliageTown? NearestTown(float x, float z)
    {
        var at = new Vector2(x, z);
        FoliageTown? best = null;
        float bestDistance = float.MaxValue;
        foreach (var t in _towns!)
        {
            float d = Vector2.DistanceSquared(t.At, at);
            if (d < bestDistance) (bestDistance, best) = (d, t);
        }
        return best;
    }

    /// <summary>The foliage of one zone: placed meshes and grass patches. Zones outside the grid are empty.</summary>
    public FoliageZone Zone(ZoneCoordinate zone) => Load(zone).Zone;

    /// <summary>The foliage of one zone and the ground it was placed on (for grass blades); null ground outside the grid.
    /// <paramref name="farOnly"/>: only the far layers when that is exact (<see cref="FoliageLayout.Place"/>).
    /// With a layout cache a zone placed before (same data and code) is read back instead.</summary>
    public (FoliageZone Zone, FoliageGround? Ground) Load(ZoneCoordinate zone, bool farOnly = false)
    {
        if (!zone.IsInsideGrid) return (new FoliageZone { Zone = zone }, null);
        if (_cache?.TryLoad(zone, farOnly) is { } hit) return (hit.Zone, hit.Ground);
        return Place(zone, farOnly);
    }

    /// <summary>
    /// <see cref="Load"/> with the instances grouped for the renderer (<see cref="GroupedFoliageZone"/>, whose zone has no instance list): a layout read back
    /// from the cache is grouped straight from the file's records, a placed one from its list (which is then emptied).
    /// </summary>
    public GroupedFoliageZone LoadGrouped(ZoneCoordinate zone, bool farOnly = false)
    {
        if (!zone.IsInsideGrid) return FoliageGrouping.Group(new FoliageZone { Zone = zone }, null);
        if (_cache?.TryLoadGrouped(zone, farOnly) is { } hit) return hit;
        var (laid, ground) = Place(zone, farOnly);
        var grouped = FoliageGrouping.Group(laid, ground);
        laid.Instances.Clear();
        laid.Instances.TrimExcess();
        return grouped;
    }

    (FoliageZone Zone, FoliageGround? Ground) Place(ZoneCoordinate zone, bool farOnly)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        Open();
        int zonesPerTile = WorldLayout.ZoneCount / TerrainMaps.OverlayTiles;
        var key = (zone.X / zonesPerTile, zone.Y / zonesPerTile);
        byte[] tile;
        if (_sharedTiles is not null) tile = _sharedTiles.GetOrAdd(key, _ => new Lazy<byte[]>(() => FoliageOverlay.ReadTile(_install, zone))).Value;
        else if (!_tiles.TryGetValue(key, out tile!))
        {
            if (_tiles.Count >= 4) _tiles.Clear();
            _tiles[key] = tile = FoliageOverlay.ReadTile(_install, zone);
        }
        var ground = FoliageGround.Read(_map!, zone);
        var input = new FoliageZoneInput
        {
            Zone = zone,
            Ground = ground,
            Overlay = new FoliageOverlay(zone, tile),
            Biomes = ZoneBiomes(zone),
            BiomeAt = _biomes!.At,
            NearestTown = NearestTown,
        };
        var laid = FoliageLayout.Place(input, Catalog, farOnly);
        _cache?.Save(zone, farOnly, laid, ground, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return (laid, ground);
    }

    public void Dispose() => _map?.Dispose();
}