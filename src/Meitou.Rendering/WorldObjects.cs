using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>What <see cref="WorldObjects.BuildZone"/> made of one zone.</summary>
sealed class ZoneObjects
{
    public required ZoneCoordinate Zone { get; init; }
    /// <summary>Building parts and map features.</summary>
    public List<PlacedMesh> Items { get; } = [];
    /// <summary>Distant meshes of buildings in towns that have no baked distant town mesh (<see cref="PlacedKind.BuildingDistant"/>).</summary>
    public List<PlacedMesh> Stand { get; } = [];
}

/// <summary>
/// The buildings (zone files) and map features (<c>features.dat</c>) of the whole world, ready to be laid out one zone at a time
/// (<see cref="BuildZone"/>, safe to call from worker threads) as the camera moves, and the towns that have a baked distant mesh.
/// </summary>
sealed class WorldObjects : IDisposable
{
    public required GameDatabase Database { get; init; }
    public required GameInstall Install { get; init; }
    public required WorldLevelData Levels { get; init; }
    public required BuildingTowns Towns { get; init; }
    public required MapFeatureFile Features { get; init; }
    /// <summary>Building layouts of <c>interiors.level</c>; the exterior ones (signs, banners) are drawn (docs/formats/zones.md, "Building layouts").</summary>
    public required BuildingLayouts Layouts { get; init; }
    /// <summary>Towns with a baked distant mesh (docs/formats/zones.md, "Distant towns").</summary>
    public required List<DistantTown> DistantTowns { get; init; }

    readonly HashSet<string> bakedTowns = new(StringComparer.Ordinal);
    readonly Dictionary<ZoneCoordinate, List<BuildingPlacement>> buildingsByZone = [];
    ThreadLocal<TerrainHeightmap>? heights;

    // Totals over the zones laid out so far (for the log line).
    int buildings, missing, fromState, destroyed, foliage, empty, features, stand, layouts, layoutObjects, layoutsNotFound;

    /// <summary>Zones that have placed buildings or features.</summary>
    public IEnumerable<ZoneCoordinate> PopulatedZones => buildingsByZone.Keys.Concat(
        Features.Zones.Select((l, i) => (l, i)).Where(t => t.l.Count > 0).Select(t => MapFeatureFile.ZoneOf(t.i))).Distinct();

    /// <summary>
    /// Reads the world's placement data once. The region arguments are not used: objects are laid out per zone around the camera
    /// by <see cref="BuildZone"/>.
    /// </summary>
    public static WorldObjects Load(GameInstall install, GameDatabase db, TerrainHeightmap map, double x0, double z0, double x1, double z1)
    {
        var watch = Stopwatch.StartNew();
        var levels = WorldLevelData.Load(install, includeInteriors: true);
        var objects = new WorldObjects
        {
            Database = db,
            Install = install,
            Levels = levels,
            Towns = new BuildingTowns(db, levels),
            Features = MapFeatureFile.Open(install),
            Layouts = new BuildingLayouts(db, levels.Interiors),
            DistantTowns = Meitou.Data.World.DistantTowns.Find(install, db, levels),
        };
        foreach (var t in objects.DistantTowns) objects.bakedTowns.Add(t.InstanceId);
        foreach (var b in levels.Buildings())
        {
            if (!objects.buildingsByZone.TryGetValue(b.Zone, out var list)) objects.buildingsByZone[b.Zone] = list = [];
            list.Add(b);
        }
        objects.heights = new ThreadLocal<TerrainHeightmap>(() => TerrainHeightmap.Open(install), trackAllValues: true);
        Console.WriteLine($"objects   {objects.buildingsByZone.Values.Sum(l => l.Count)} placed buildings and {objects.Features.All().Count()} map features in {objects.PopulatedZones.Count()} zones, " +
            $"{objects.DistantTowns.Count} towns with a distant mesh, {objects.Layouts.Count} building layouts ({watch.ElapsedMilliseconds} ms)");
        return objects;
    }

    /// <summary>Lays out one zone's buildings and map features as the game builds them (docs/formats/zones.md).</summary>
    public ZoneObjects BuildZone(ZoneCoordinate zone)
    {
        var result = new ZoneObjects { Zone = zone };
        var map = heights!.Value!;
        var db = Database;
        if (buildingsByZone.TryGetValue(zone, out var placements))
            foreach (var b in placements)
            {
                if (b.Position == Vector3.Zero) continue;
                if (db.Find(b.BuildingId) is not { } record) { Interlocked.Increment(ref missing); continue; }
                // Y: the state's absolute "world Y pos" when there is one, else terrain height + the instance's Y (zones.md).
                float y = b.WorldY ?? map.HeightAt(b.Position.X, b.Position.Z) + b.Position.Y;
                if (b.WorldY is not null) Interlocked.Increment(ref fromState);
                var state = b.StateId is not null && Levels.Zones.TryGetValue(b.Zone, out var zoneDb) ? zoneDb.Find(b.StateId) : null;
                bool isDestroyed = state?.GetBool("destroyed") ?? false;
                if (isDestroyed) Interlocked.Increment(ref destroyed);
                var town = Towns.TownOf(state, b.Position);
                GameRecord? townMaterial;
                lock (Towns) townMaterial = Towns.MaterialOf(town);
                var position = new Vector3(b.Position.X, y, b.Position.Z);
                var parts = WorldObjectLayout.Building(db, record, b.InstanceId, position, b.Rotation, new BuildingState(isDestroyed, townMaterial));
                // Resource buildings ("is foliage", no parts) are drawn by the foliage system as a FOLIAGE_MESH naming them as "building type".
                if (parts.Count == 0 && record.GetBool("is foliage")) Interlocked.Increment(ref foliage);
                else if (parts.Count == 0) Interlocked.Increment(ref empty);
                result.Items.AddRange(parts);
                Interlocked.Increment(ref buildings);
                // The state's exterior layout: signs and banners placed relative to the building. Skipped for a destroyed one (Unknown).
                if (!isDestroyed && state?.GetString("exterior layout name") is { Length: > 0 } layoutName)
                {
                    if (Layouts.Find(record, layoutName) is not { } layout) Interlocked.Increment(ref layoutsNotFound);
                    else
                    {
                        Interlocked.Increment(ref layouts);
                        foreach (var item in Layouts.Items(layout))
                        {
                            var (at, rotation) = BuildingLayouts.Place(position, b.Rotation, item);
                            var placed = WorldObjectLayout.Building(db, item.Building, $"{b.InstanceId}/{item.InstanceId}", at, rotation,
                                new BuildingState(false, townMaterial));
                            result.Items.AddRange(placed);
                            if (placed.Count > 0) Interlocked.Increment(ref layoutObjects);
                        }
                    }
                }
                // Towns without a baked distant mesh: the building's own distant mesh stands in for it far away (what the game's
                // "generate distant towns" would batch).
                if (!isDestroyed && (town is null || !bakedTowns.Contains(town.InstanceId)) &&
                    WorldObjectLayout.DistantMesh(record, b.InstanceId, position, b.Rotation) is { } distant)
                {
                    result.Stand.Add(distant);
                    Interlocked.Increment(ref stand);
                }
            }
        int featureIndex = zone.Y * WorldLayout.ZoneCount + zone.X;
        var list = featureIndex < Features.Zones.Count ? Features.Zones[featureIndex] : [];
        for (int i = 0; i < list.Count; i++)
            if (WorldObjectLayout.Feature(db, list[i], $"feature {zone} {i}") is { } placed)
            {
                result.Items.Add(placed);
                Interlocked.Increment(ref features);
            }
        return result;
    }

    /// <summary>A line for the log: what has been laid out so far.</summary>
    public string Describe() =>
        $"{buildings} buildings ({fromState} at their state's world Y, {destroyed} destroyed, {missing} without a record, {foliage} foliage resources and {empty} others without meshes), " +
        $"{layouts} exterior layouts ({layoutObjects} objects, {layoutsNotFound} layout names not found), {features} map features, {stand} distant stand-ins";

    public void Dispose()
    {
        if (heights is null) return;
        foreach (var h in heights.Values) h.Dispose();
        heights.Dispose();
        heights = null;
    }
}
