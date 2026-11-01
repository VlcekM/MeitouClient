using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// The interior join sweep (docs/game/pathfinding.md "Join sweep"): per original interior group, whether the original connects it to the exterior (mesh
/// connections in its streaming set, docs/formats/havok-tagfile.md "Interior joins") and whether ours, the interior whose hull holds most of its faces, is
/// reached from the street.
/// </summary>
internal static class InteriorJoinSweep
{
    /// <summary>One of our interiors in a zone: its building key and record name, whether it is a ruin, and its hull.</summary>
    public sealed record Site(string Key, string Name, bool Destroyed, NavVolume Hull);

    /// <summary>An original group against ours: <paramref name="Site"/> null when no interior of ours holds its faces.</summary>
    public sealed record Join(OriginalInterior Group, Site? Site, bool Original, bool Ours)
    {
        /// <summary>Connected in the original but not reached in ours.</summary>
        public bool Sealed => Original && Site is not null && !Ours;
    }

    /// <summary>The zone's interiors with a hull.</summary>
    public static List<Site> Sites(ZoneGeometryGatherer gatherer, ZoneCoordinate zone, NavBuildSettings settings) =>
        gatherer.InteriorSites(zone)
            .Select(s => (s, gatherer.GatherInterior(s, NavMeshPipeline.InteriorTileStep * settings.CellSize)?.InteriorHull))
            .Where(h => h.InteriorHull is not null)
            .Select(h => new Site(h.s.Placement.Key, h.s.Record.Name, h.s.Destroyed, h.InteriorHull!)).ToList();

    /// <summary>How many of the group's face centres lie in the site's hull (within 2 in plan, 20 in height).</summary>
    public static int Inside(OriginalInterior group, Site site) =>
        Centres(group).Count(v => NavGeometry.InsideConvex(site.Hull.Polygon, new Vector2(v.X, v.Z), 2) && v.Y >= site.Hull.YMin - 20 && v.Y <= site.Hull.YMax + 20);

    /// <summary>The site holding most of the group's face centres, or null when none holds any.</summary>
    public static Site? Owner(OriginalInterior group, IReadOnlyList<Site> sites)
    {
        if (sites.Count == 0) return null;
        var site = sites.MaxBy(s => Inside(group, s))!;
        return Inside(group, site) > 0 ? site : null;
    }

    static IEnumerable<Vector3> Centres(OriginalInterior group) =>
        group.Polygons.Where(p => p.Length >= 3).Select(p => p.Aggregate(Vector3.Zero, (a, i) => a + group.Vertices[i]) / p.Length);

    /// <summary>
    /// The interiors of a zone's build reached from the street: the zone's kept set resolved alone (its seeds and the border rule, as with no neighbour
    /// loaded) or with its neighbours (<paramref name="resolved"/>), walked over kept polygons, joins and links from its exterior's kept polygons; an
    /// interior counts when a polygon its own pruning kept is reached (any of it when that kept nothing: a doorway strip kept only through a join is not the
    /// room). Keys are the interiors' building keys (<see cref="InteriorMesh.Key"/>).
    /// </summary>
    public static HashSet<string> Reached(ZoneCoordinate zone, ZoneNavMesh full, IReadOnlyList<InteriorMesh> inner, NavBuildSettings settings, bool[]? resolved = null)
    {
        var keep = resolved ?? ZoneKeptClosure.Resolve(new Dictionary<ZoneCoordinate, ZoneNavMesh> { [zone] = full }, settings)[zone];
        int exterior = full.PolygonCount - inner.Sum(m => m.Mesh.PolygonCount);
        var reached = new bool[full.PolygonCount];
        var queue = new Queue<int>();
        for (int p = 0; p < exterior; p++) if (keep[p]) { reached[p] = true; queue.Enqueue(p); }
        while (queue.Count > 0)
            foreach (int n in full.NeighboursOf(queue.Dequeue()))
                if (keep[n] && !reached[n]) { reached[n] = true; queue.Enqueue(n); }
        var result = new HashSet<string>();
        int offset = exterior;
        for (int k = 0; k < inner.Count; k++)
        {
            var own = inner[k].Mesh.Kept;
            bool any = own.Any(x => x);
            for (int i = 0; i < own.Length; i++)
                if (reached[offset + i] && (!any || own[i]) && inner[k].Key is { } key) { result.Add(key); break; }
            offset += own.Length;
        }
        return result;
    }

    /// <summary>
    /// Scores each group: <paramref name="joined"/> holds the interiors this zone's build reaches; one not in it is looked up in the eight neighbours'
    /// builds, each resolved alone (its door may lie over the border), built once on the first such lookup.
    /// </summary>
    public static List<Join> Compare(ZoneGeometryGatherer gatherer, ZoneCoordinate zone, IReadOnlyList<OriginalInterior> groups, IReadOnlyList<Site> sites,
        HashSet<string> joined, NavBuildSettings settings)
    {
        HashSet<string>? neighbours = null;
        bool JoinedAnywhere(string key)
        {
            if (joined.Contains(key)) return true;
            if (neighbours is null)
            {
                neighbours = [];
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        var n = new ZoneCoordinate(zone.X + dx, zone.Y + dz);
                        var built = NavMeshPipeline.BuildZone(gatherer, gatherer.Gather(n), settings, out _, out var nInner, interiors: true);
                        neighbours.UnionWith(Reached(n, built, nInner, settings));
                    }
            }
            return neighbours.Contains(key);
        }
        return groups.Select(group =>
        {
            var site = Owner(group, sites);
            return new Join(group, site, group.Connections > 0, site is not null && JoinedAnywhere(site.Key));
        }).ToList();
    }
}
