using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.World;

/// <summary>
/// <c>meitou-tools objects &lt;x&gt; &lt;z&gt; &lt;radius&gt;</c>: what stands near a world point under the install's or a report's load order: the towns that keep
/// foliage away (with their <c>no-foliage range</c>), the foliage meshes the placer puts in the point's zone, the level's building list entries and
/// the map features.
/// Tells a foliage rock from a level-placed one when a report shows something in the way.
/// </summary>
static class PlaceProbe
{
    /// <summary>
    /// How far round the point towns are listed (their ranges reach past the radius asked for).
    /// </summary>
    const float TownReach = 6000;

    /// <summary>
    /// The command line: the point, the radius and the load order options (<see cref="ProbeLoadOrder"/>). Returns 0, or 2 for a bad command line.
    /// </summary>
    public static int Run(GameInstall install, string[] args)
    {
        var rest = new List<string>(args);
        var order = ProbeLoadOrder.Take(install, rest);
        if (rest.Count < 3)
        {
            Console.Error.WriteLine("meitou-tools objects <x> <z> <radius> [--load-order <file>] [--mod-dir <folder>]...");
            return 2;
        }

        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var at = new Vector2(float.Parse(rest[0]), float.Parse(rest[1]));
        float radius = float.Parse(rest[2]);
        var db = GameDatabase.Load(order);
        var world = WorldLevelData.Load(install, order, includeInteriors: false);
        PrintTowns(db, world, at);
        PrintFoliage(install, db, world, at, radius);
        PrintBuildings(db, world, at, radius);
        PrintFeatures(install, db, at, radius);
        return 0;
    }

    /// <summary>
    /// The towns within <see cref="TownReach"/> that keep foliage away.
    /// </summary>
    static void PrintTowns(GameDatabase db, WorldLevelData world, Vector2 at)
    {
        foreach (var t in world.Towns())
        {
            var place = new Vector2(t.Position.X, t.Position.Z);
            if (Vector2.Distance(place, at) >= TownReach || db.Find(t.TownId) is not { } record) continue;

            Console.WriteLine($"town {t.TownId} '{record.Name}' type={record.GetInt("type")} no-foliage range={record.GetFloat("no-foliage range", 2000)} "
                + $"at {place:F0} distance={Vector2.Distance(place, at):F0}");
        }
    }

    /// <summary>
    /// The foliage meshes the placer puts within <paramref name="radius"/>, from the point's zone and the eight round it: by where they stand or by
    /// how near their mesh's box comes (a boulder cluster reaches hundreds of units past its origin; <c>reach</c> is 0 when the box is over the point).
    /// </summary>
    static void PrintFoliage(GameInstall install, GameDatabase db, WorldLevelData world, Vector2 at, float radius)
    {
        using var foliage = new FoliageWorld(install, db, world);
        var centre = WorldLayout.ZoneOf(at.X, at.Y);
        var zones = from dx in Enumerable.Range(-1, 3)
                    from dy in Enumerable.Range(-1, 3)
                    select new ZoneCoordinate(centre.X + dx, centre.Y + dy);
        var bounds = new Dictionary<string, OgreBounds?>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in zones.SelectMany(z => foliage.Zone(z).Instances))
        {
            float distance = Vector2.Distance(new(i.Position.X, i.Position.Z), at);
            float reach = MeshBounds(install, i.Mesh.MeshPath, bounds) is { } b ? BoxDistance(WorldBox(b, i.Transform), at) : distance;
            if (Math.Min(distance, reach) >= radius) continue;

            Console.WriteLine($"foliage {i.Mesh.StringId} '{i.Mesh.Name}' avoidTowns={i.Mesh.AvoidTowns} at {i.Position:F0} scale={i.Scale:F2} "
                + $"distance={distance:F0} reach={reach:F0}");
        }
    }

    /// <summary>
    /// A foliage mesh's bounds box, read once per mesh; null when the file cannot be read.
    /// </summary>
    static OgreBounds? MeshBounds(GameInstall install, string meshPath, Dictionary<string, OgreBounds?> cache)
    {
        if (cache.TryGetValue(meshPath, out var known)) return known;

        var path = Path.Combine(install.Root, meshPath);
        OgreBounds? bounds = null;
        try
        {
            bounds = OgreMeshReader.TryReadBounds(path, out var quick) ? quick : OgreMeshReader.ReadFile(path).Bounds;
        }
        catch (Exception e) when (e is IOException or OgreFormatException)
        {
            Console.Error.WriteLine($"objects   {meshPath}: {e.Message}");
        }

        cache[meshPath] = bounds;
        return bounds;
    }

    /// <summary>
    /// The top-down box (min x, min z, max x, max z) round a mesh's bounds box placed by <paramref name="transform"/>.
    /// </summary>
    static (Vector2 Min, Vector2 Max) WorldBox(OgreBounds b, Matrix4x4 transform)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3((c & 1) == 0 ? b.Min.X : b.Max.X, (c & 2) == 0 ? b.Min.Y : b.Max.Y, (c & 4) == 0 ? b.Min.Z : b.Max.Z);
            var placed = Vector3.Transform(corner, transform);
            min = Vector2.Min(min, new(placed.X, placed.Z));
            max = Vector2.Max(max, new(placed.X, placed.Z));
        }

        return (min, max);
    }

    /// <summary>
    /// How far <paramref name="at"/> is from a top-down box; 0 inside it.
    /// </summary>
    static float BoxDistance((Vector2 Min, Vector2 Max) box, Vector2 at) => Vector2.Distance(Vector2.Clamp(at, box.Min, box.Max), at);

    /// <summary>
    /// The map features (<c>features.dat</c>: rocks, cliffs and other large pieces) within <paramref name="radius"/>.
    /// </summary>
    static void PrintFeatures(GameInstall install, GameDatabase db, Vector2 at, float radius)
    {
        foreach (var (zone, f) in MapFeatureFile.Open(install).All())
        {
            float distance = Vector2.Distance(new(f.Position.X, f.Position.Z), at);
            if (distance >= radius) continue;

            Console.WriteLine($"feature {f.StringId} '{db.Find(f.StringId)?.Name}' zone={zone} at {f.Position:F0} scale={f.Scale:F2} distance={distance:F0}");
        }
    }

    /// <summary>
    /// The level's building list entries within <paramref name="radius"/>.
    /// </summary>
    static void PrintBuildings(GameDatabase db, WorldLevelData world, Vector2 at, float radius)
    {
        foreach (var b in world.BuildingListEntries())
        {
            float distance = Vector2.Distance(new(b.Position.X, b.Position.Z), at);
            if (distance >= radius) continue;

            var record = db.Find(b.BuildingId);
            Console.WriteLine($"building {b.BuildingId} '{record?.Name}' type={record?.Type} instance={b.InstanceId} zone={b.Zone} at {b.Position:F0} "
                + $"distance={distance:F0}");
        }
    }
}
