using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.ModelViewer;

/// <summary>The buildings (zone files) and map features (<c>features.dat</c>) of a region, as meshes with world transforms.</summary>
sealed class WorldObjects
{
    public List<PlacedMesh> Items { get; } = [];
    public required GameDatabase Database { get; init; }
    public required GameInstall Install { get; init; }

    public static WorldObjects Load(GameInstall install, GameDatabase db, TerrainHeightmap map, double x0, double z0, double x1, double z1)
    {
        var watch = Stopwatch.StartNew();
        var objects = new WorldObjects { Database = db, Install = install };
        bool Inside(Vector3 p) => p.X >= x0 && p.X <= x1 && p.Z >= z0 && p.Z <= z1;

        var levels = WorldLevelData.Load(install);
        var towns = new BuildingTowns(db, levels);
        int buildings = 0, missing = 0, fromState = 0, destroyed = 0, foliage = 0, empty = 0;
        foreach (var b in levels.Buildings())
        {
            if (b.Position == Vector3.Zero || !Inside(b.Position)) continue;
            if (db.Find(b.BuildingId) is not { } record) { missing++; continue; }
            // Y: the state's absolute "world Y pos" when there is one, else terrain height + the instance's Y (zones.md).
            float y = b.WorldY ?? map.HeightAt(b.Position.X, b.Position.Z) + b.Position.Y;
            if (b.WorldY is not null) fromState++;
            var state = b.StateId is not null && levels.Zones.TryGetValue(b.Zone, out var zone) ? zone.Find(b.StateId) : null;
            bool isDestroyed = state?.GetBool("destroyed") ?? false;
            if (isDestroyed) destroyed++;
            var buildingState = new BuildingState(isDestroyed, towns.MaterialOf(towns.TownOf(state, b.Position)));
            var parts = WorldObjectLayout.Building(db, record, b.InstanceId, new Vector3(b.Position.X, y, b.Position.Z), b.Rotation, buildingState);
            // Resource buildings ("is foliage", no parts) are drawn by the foliage system as a FOLIAGE_MESH naming them as "building type".
            if (parts.Count == 0 && record.GetBool("is foliage")) foliage++;
            else if (parts.Count == 0) empty++;
            objects.Items.AddRange(parts);
            buildings++;
        }
        int features = 0;
        var file = MapFeatureFile.Open(install);
        foreach (var (zone, f) in file.All())
        {
            if (!Inside(f.Position)) continue;
            if (WorldObjectLayout.Feature(db, f, $"feature {zone} {features}") is { } placed)
            {
                objects.Items.Add(placed);
                features++;
            }
        }
        Console.WriteLine($"objects   {buildings} buildings ({fromState} at their state's world Y, {destroyed} destroyed, {missing} without a record, " +
            $"{foliage} foliage resources and {empty} others without meshes), {features} map features: " +
            $"{objects.Items.Count} meshes, {objects.Items.Select(i => i.MeshPath.ToLowerInvariant()).Distinct().Count()} distinct ({watch.ElapsedMilliseconds} ms)");
        return objects;
    }
}
