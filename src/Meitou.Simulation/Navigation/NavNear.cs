using System.Numerics;
using System.Text;
using System.Text.Json;
using Meitou.Data.World;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// A debugging read of the navmesh round a point (the agent control channel's <c>navnear</c>): the polygons of the loaded zones that reach into
/// the square of half size <c>radius</c> round it, and the closest walkable point the town refit's snap would take for that point
/// (<see cref="WalkableSpots.Closest"/> with <see cref="TownRefit.SnapReach"/>, the query <see cref="TownRefit"/> makes).
/// </summary>
public static class NavNear
{
    /// <summary>
    /// The default half size of the square when the command gives none.
    /// </summary>
    public const float DefaultRadius = 200;

    /// <summary>
    /// The JSON of the navmesh round (<paramref name="x"/>, <paramref name="z"/>): <c>query</c> (the point and radius), <c>zones</c> (the zones under
    /// the square, each with whether its navmesh is loaded; an unloaded zone answers with open ground, as the refit does), <c>snap</c> (the refit's
    /// closest-point result: whether the point itself is walkable, the point found, its distance, its height) and <c>faces</c> (zone, polygon index,
    /// area, <c>walkable</c> = kept by the seed pass, so a walkability query can stand on it, and the vertices as [x, y, z]).
    /// </summary>
    public static string Json(IWalkability walk, float x, float z, float radius)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            WriteQuery(w, x, z, radius);
            WriteZones(w, walk, x, z, radius);
            WriteSnap(w, walk, x, z);
            WriteFaces(w, walk, x, z, radius);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The queried point and the half size of the square.
    /// </summary>
    static void WriteQuery(Utf8JsonWriter w, float x, float z, float radius)
    {
        w.WriteStartObject("query");
        w.WriteNumber("x", R2(x));
        w.WriteNumber("z", R2(z));
        w.WriteNumber("radius", R2(radius));
        w.WriteEndObject();
    }

    /// <summary>
    /// The zones under the square and whether each has a loaded navmesh.
    /// </summary>
    static void WriteZones(Utf8JsonWriter w, IWalkability walk, float x, float z, float radius)
    {
        var nav = (walk as NavmeshWalkability)?.World;
        var low = WorldLayout.ZoneOf(x - radius, z - radius);
        var high = WorldLayout.ZoneOf(x + radius, z + radius);

        w.WriteStartArray("zones");
        for (int zx = low.X; zx <= high.X; zx++)
        {
            for (int zz = low.Y; zz <= high.Y; zz++)
            {
                w.WriteStartObject();
                w.WriteNumber("x", zx);
                w.WriteNumber("z", zz);
                w.WriteBoolean("loaded", nav?.Contains(new ZoneCoordinate(zx, zz)) == true);
                w.WriteEndObject();
            }
        }

        w.WriteEndArray();
    }

    /// <summary>
    /// What <see cref="WalkableSpots.Closest"/> gives for the point (the refit's snap), with the height the refit puts the centre at.
    /// </summary>
    static void WriteSnap(Utf8JsonWriter w, IWalkability walk, float x, float z)
    {
        var at = new Vector2(x, z);
        bool found = WalkableSpots.Closest(walk, at, TownRefit.SnapReach, out var spot);

        w.WriteStartObject("snap");
        w.WriteBoolean("pointWalkable", walk.IsWalkable(x, z));
        w.WriteBoolean("found", found);
        w.WriteNumber("reach", TownRefit.SnapReach);
        if (found)
        {
            w.WriteNumber("x", R2(spot.X));
            w.WriteNumber("z", R2(spot.Y));
            w.WriteNumber("y", R2(walk.GroundHeight(spot.X, spot.Y)));
            w.WriteNumber("distance", R2(Vector2.Distance(at, spot)));
        }

        w.WriteEndObject();
    }

    /// <summary>
    /// The polygons of the loaded meshes whose box meets the square.
    /// </summary>
    static void WriteFaces(Utf8JsonWriter w, IWalkability walk, float x, float z, float radius)
    {
        w.WriteStartArray("faces");
        if (walk is NavmeshWalkability nav)
        {
            var world = nav.World;
            foreach (var zone in world.Zones.OrderBy(c => c.X).ThenBy(c => c.Y))
            {
                var mesh = world.Find(zone)!;
                for (int p = 0; p < mesh.PolygonCount; p++)
                    if (Reaches(mesh, p, x, z, radius)) WriteFace(w, mesh, p);
            }
        }

        w.WriteEndArray();
    }

    /// <summary>
    /// Whether the polygon's box meets the square of half size <paramref name="radius"/> round (<paramref name="x"/>, <paramref name="z"/>).
    /// </summary>
    static bool Reaches(ZoneNavMesh mesh, int polygon, float x, float z, float radius)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (int i in mesh.Polygons[polygon])
        {
            var v = mesh.Vertices[i];
            minX = MathF.Min(minX, v.X);
            maxX = MathF.Max(maxX, v.X);
            minZ = MathF.Min(minZ, v.Z);
            maxZ = MathF.Max(maxZ, v.Z);
        }

        return maxX >= x - radius && minX <= x + radius && maxZ >= z - radius && minZ <= z + radius;
    }

    /// <summary>
    /// One polygon: zone, index, area, walkable flag and vertices.
    /// </summary>
    static void WriteFace(Utf8JsonWriter w, ZoneNavMesh mesh, int polygon)
    {
        w.WriteStartObject();
        w.WriteString("zone", $"{mesh.ZoneX},{mesh.ZoneZ}");
        w.WriteNumber("polygon", polygon);
        w.WriteNumber("area", mesh.Areas[polygon]);
        w.WriteBoolean("walkable", mesh.Kept[polygon]);
        w.WriteStartArray("vertices");
        foreach (int i in mesh.Polygons[polygon])
        {
            var v = mesh.Vertices[i];
            w.WriteStartArray();
            w.WriteNumberValue(R2(v.X));
            w.WriteNumberValue(R2(v.Y));
            w.WriteNumberValue(R2(v.Z));
            w.WriteEndArray();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>
    /// A number rounded to two decimals.
    /// </summary>
    static double R2(double v) => Math.Round(v, 2);
}
