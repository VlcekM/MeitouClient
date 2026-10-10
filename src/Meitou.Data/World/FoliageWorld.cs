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
    readonly GameInstall install;
    readonly GameDatabase db;
    readonly WorldLevelData world;
    readonly FoliageLayoutCache? cache;
    // Opened on the first zone that is actually placed: a zone read from the layout cache needs none of them.
    TerrainHeightmap? map;
    BlendInfoFile? blend;
    FoliageBiomeMap? biomes;
    List<(Vector2 At, float Range)>? towns;
    readonly Dictionary<(int, int), byte[]> tiles = [];
    readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), Lazy<byte[]>>? sharedTiles;

    public FoliageWorld(GameInstall install, GameDatabase db, WorldLevelData world, FoliageCatalog? catalog = null,
        System.Collections.Concurrent.ConcurrentDictionary<(int, int), Lazy<byte[]>>? sharedTiles = null, FoliageLayoutCache? cache = null)
    {
        this.sharedTiles = sharedTiles;
        this.install = install;
        this.db = db;
        this.world = world;
        this.cache = cache;
        Catalog = catalog ?? FoliageCatalog.Load(db);
    }

    public FoliageCatalog Catalog { get; }

    /// <summary>The towns that keep foliage away (not nest markers): X/Z and <c>no-foliage range</c>.</summary>
    public static List<(Vector2 At, float Range)> ReadTowns(GameDatabase db, WorldLevelData world)
    {
        var list = new List<(Vector2, float)>();
        foreach (var t in world.Towns())
            if (db.Find(t.TownId) is { } record && record.GetInt("type") != BuildingTowns.NestMarkerType)
                list.Add((new Vector2(t.Position.X, t.Position.Z), record.GetFloat("no-foliage range", 2000)));
        return list;
    }

    /// <summary>Opens the files the placer reads (the first time a zone is placed).</summary>
    void Open()
    {
        if (map is not null) return;
        towns = ReadTowns(db, world);
        blend = BlendInfoFile.Open(install);
        biomes = FoliageBiomeMap.Open(install);
        map = TerrainHeightmap.Open(install);
    }

    /// <summary>The biome colours of a zone, in blend slot order: the slots with weight anywhere in the zone's box.</summary>
    public List<uint> ZoneBiomes(ZoneCoordinate zone)
    {
        Open();
        var (x0, z0) = WorldLayout.ZoneOrigin(zone);
        var (cx, cz) = blend!.CellOf(x0 + WorldLayout.ZoneSize / 2.0, z0 + WorldLayout.ZoneSize / 2.0);
        double size = blend.CellSize;
        double u0 = (x0 + WorldLayout.HalfWorldSize - cx * size) / size, v0 = (z0 + WorldLayout.HalfWorldSize - cz * size) / size;
        double du = WorldLayout.ZoneSize / size;
        const double inset = 0.001;
        byte mask = blend.SlotMask(cx, cz, u0 + inset, v0 + inset, u0 + du - inset, v0 + du - inset);
        var list = new List<uint>();
        for (int s = 0; s < BlendInfoFile.SlotsPerCell; s++)
            if ((BlendInfoFile.MaskSlots(mask) & (1 << s)) != 0 && blend.Slot(cx, cz, s) is var c && c != 0 && !list.Contains(c)) list.Add(c);
        return list;
    }

    (Vector2 At, float Range)? NearestTown(float x, float z)
    {
        var at = new Vector2(x, z);
        (Vector2, float)? best = null;
        float bestDistance = float.MaxValue;
        foreach (var t in towns!)
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
        if (cache?.TryLoad(zone, farOnly) is { } hit) return (hit.Zone, hit.Ground);
        return Place(zone, farOnly);
    }

    /// <summary>
    /// <see cref="Load"/> with the instances grouped for the renderer (<see cref="GroupedFoliageZone"/>, whose zone has no instance list): a layout read back
    /// from the cache is grouped straight from the file's records, a placed one from its list (which is then emptied).
    /// </summary>
    public GroupedFoliageZone LoadGrouped(ZoneCoordinate zone, bool farOnly = false)
    {
        if (!zone.IsInsideGrid) return FoliageGrouping.Group(new FoliageZone { Zone = zone }, null);
        if (cache?.TryLoadGrouped(zone, farOnly) is { } hit) return hit;
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
        if (sharedTiles is not null) tile = sharedTiles.GetOrAdd(key, _ => new Lazy<byte[]>(() => FoliageOverlay.ReadTile(install, zone))).Value;
        else if (!tiles.TryGetValue(key, out tile!))
        {
            if (tiles.Count >= 4) tiles.Clear();
            tiles[key] = tile = FoliageOverlay.ReadTile(install, zone);
        }
        var ground = FoliageGround.Read(map!, zone);
        var input = new FoliageZoneInput
        {
            Zone = zone,
            Ground = ground,
            Overlay = new FoliageOverlay(zone, tile),
            Biomes = ZoneBiomes(zone),
            BiomeAt = biomes!.At,
            NearestTown = NearestTown,
        };
        var laid = FoliageLayout.Place(input, Catalog, farOnly);
        cache?.Save(zone, farOnly, laid, ground, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return (laid, ground);
    }

    public void Dispose() => map?.Dispose();
}
