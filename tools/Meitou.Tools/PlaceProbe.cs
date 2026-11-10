using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

/// <summary>
/// <c>meitou-tools objects &lt;x&gt; &lt;z&gt; &lt;radius&gt;</c>: what stands near a world point under the install's load order: the towns that keep
/// foliage away (with their <c>no-foliage range</c>), the foliage meshes the placer puts in the point's zone, and the level's building list entries.
/// Tells a foliage rock from a level-placed one when a report shows something in the way.
/// </summary>
static class PlaceProbe
{
    /// <summary>
    /// How far round the point towns are listed (their ranges reach past the radius asked for).
    /// </summary>
    const float TownReach = 6000;

    /// <summary>
    /// The command line: the point and the radius. Returns 0, or 2 for a bad command line.
    /// </summary>
    public static int Run(GameInstall install, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("meitou-tools objects <x> <z> <radius>");
            return 2;
        }

        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var at = new Vector2(float.Parse(args[0]), float.Parse(args[1]));
        float radius = float.Parse(args[2]);
        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var world = WorldLevelData.Load(install, includeInteriors: false);
        PrintTowns(db, world, at);
        PrintFoliage(install, db, world, at, radius);
        PrintBuildings(db, world, at, radius);
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
    /// The foliage meshes the placer puts within <paramref name="radius"/> in the point's zone.
    /// </summary>
    static void PrintFoliage(GameInstall install, GameDatabase db, WorldLevelData world, Vector2 at, float radius)
    {
        using var foliage = new FoliageWorld(install, db, world);
        foreach (var i in foliage.Zone(WorldLayout.ZoneOf(at.X, at.Y)).Instances)
        {
            float distance = Vector2.Distance(new(i.Position.X, i.Position.Z), at);
            if (distance >= radius) continue;

            Console.WriteLine($"foliage {i.Mesh.StringId} '{i.Mesh.Name}' avoidTowns={i.Mesh.AvoidTowns} at {i.Position:F0} scale={i.Scale:F2} "
                + $"distance={distance:F0}");
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
