using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;

namespace Meitou.Tests.Navigation;

/// <summary>
/// Interiors joined to the street, on zones of the install built alone (docs/game/pathfinding.md "Door painters", "Join sweep", "Inner seams"): door
/// painters swept into the hull and the polygon winding in double give the Sho-Battai Longhouses, Storm House Lagoon and Heft's bar their door joins,
/// the seed ray rule on floor-0/1 parts keeps the Hive village's ground, and the seam rule joins Storm House 27.22's ramp to its door landing.
/// </summary>
[Slow]
public class DoorJoinTests
{
    static readonly object Gate = new();
    static readonly Dictionary<ZoneCoordinate, Built> BuiltCache = [];

    /// <summary>
    /// A zone's build: the combined mesh before its kept set is resolved, its interiors in the order they were appended,
    /// and each interior site's gathered painters and kind.
    /// </summary>
    sealed record Built(ZoneNavMesh Full, IReadOnlyList<InteriorMesh> Inner, Dictionary<string, (int Painters, bool Doorless, bool Destroyed)> Sites);

    static Built Zone(int x, int z)
    {
        var zone = new ZoneCoordinate(x, z);
        var install = InstallData.Install!;
        lock (Gate)
        {
            if (BuiltCache.TryGetValue(zone, out var b)) return b;
            using var gatherer = new ZoneGeometryGatherer(install, InstallData.FullLoadOrder!, InstallData.LevelsWithInteriors!,
                new CollisionCache(install));
            var settings = new NavBuildSettings();
            var full = NavMeshPipeline.BuildZone(gatherer, gatherer.Gather(zone), settings, out _, out var inner, interiors: true);
            var sites = new Dictionary<string, (int, bool, bool)>();
            foreach (var site in gatherer.InteriorSites(zone))
            {
                var step = NavMeshPipeline.InteriorTileStep * settings.CellSize;
                if (gatherer.GatherInterior(site, step, agentHeight: settings.AgentHeight) is { InteriorOf: { } key } g)
                {
                    sites[key] = (g.Painters.Count, g.InteriorDoorless, g.InteriorDestroyed);
                }
            }
            return BuiltCache[zone] = new Built(full, inner, sites);
        }
    }

    /// <summary>
    /// The zone with its kept set resolved as the game publishes a zone loaded alone, for path queries.
    /// </summary>
    static NavQuery Query(Built b) => new(NavWorld.Empty.With(ZoneKeptClosure.ResolveAlone(b.Full, new NavBuildSettings())));

    /// <summary>
    /// Each interior's polygon range in the combined mesh (the exterior's polygons come first).
    /// </summary>
    static IEnumerable<(InteriorMesh Interior, int First)> Ranges(Built b)
    {
        int first = b.Full.PolygonCount - b.Inner.Sum(i => i.Mesh.PolygonCount);
        foreach (var interior in b.Inner)
        {
            yield return (interior, first);
            first += interior.Mesh.PolygonCount;
        }
    }

    static readonly (int X, int Z, int Painted, string[] DoorToDoor)[] DoorZones =
    [
        (43, 11, 21, ["43.11:12-Newwworld-INGAME", "43.11:5563-Newwworld-INGAME", "43.11:5565-Newwworld-INGAME", "43.11:2-Newwworld-INGAME"]),
        (47, 16, 10, ["47.16:241-Newwworld-INGAME"]),
        (48, 45, 6, ["48.45:9-rebirth-INGAME"]),
    ];

    [Fact]
    public void Every_intact_interior_with_a_door_painter_is_joined_to_the_exterior()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        Assert.All(DoorZones, d => AllJoined(d.X, d.Z, d.Painted, d.DoorToDoor));
    }

    static void AllJoined(int x, int z, int painted, string[] doorToDoor)
    {
        var b = Zone(x, z);
        var mesh = b.Full;
        int exterior = mesh.PolygonCount - b.Inner.Sum(i => i.Mesh.PolygonCount);
        var expected = b.Sites
            .Where(s => s.Value.Painters > 0 && !s.Value.Doorless && !s.Value.Destroyed)
            .Select(s => s.Key)
            .ToHashSet();
        Assert.Equal(painted, expected.Count);
        Assert.Superset(doorToDoor.ToHashSet(), expected);
        var unjoined = new List<string>();
        var doorJoined = new HashSet<string>();
        foreach (var (interior, first) in Ranges(b))
        {
            if (interior.Key is not { } key || !expected.Remove(key)) continue;
            bool joined = false;
            for (int p = first; p < first + interior.Mesh.PolygonCount; p++)
            {
                var links = mesh.LinksOf(p);
                for (int i = 1; i < links.Length; i += 2)
                {
                    if (links[i] >= exterior) continue;
                    joined = true;
                    if (mesh.Areas[p] == NavArea.Door && mesh.Areas[links[i]] == NavArea.Door) doorJoined.Add(key);
                }
            }
            if (!joined) unjoined.Add(key);
        }
        // A painted interior that built no polygon is not in the build at all.
        Assert.True(expected.Count == 0, $"{x}.{z}: interiors with a door painter missing from the build: {string.Join(", ", expected)}");
        Assert.True(unjoined.Count == 0, $"{x}.{z}: interiors with a door painter joining nothing: {string.Join(", ", unjoined)}");
        // The interiors the painter sweep and the winding fix mended join door polygon to door polygon
        // (not only through the doorway or approach fallbacks).
        var notDoorJoined = doorToDoor.Where(k => !doorJoined.Contains(k));
        Assert.True(doorToDoor.All(doorJoined.Contains), $"{x}.{z}: no door-to-door join for {string.Join(", ", notDoorJoined)}");
    }

    [Fact]
    public void A_path_from_the_street_reaches_the_upper_floor_of_hefts_bar()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        // Heft's bar, the Outpost s-III 47.16:241-Newwworld-INGAME (issue #130): its door leaf stands outside the hull;
        // the street south of its door, the upper floor at 1187 over the ground floor at 1141.
        var path = Query(Zone(47, 16)).FindPath(new Vector3(70150, 1104, -70800), new Vector3(70200, 1187, -70660));
        Assert.True(path.Found, "no path from the street to the bar's upper floor");
        Assert.InRange(path.Points[^1].Y, 1185f, 1189f);
        Assert.InRange(path.Points[0].Y, 1100f, 1108f);
    }
}
