using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Locks;

namespace Meitou.Navigation;

/// <summary>
/// The gate codes of a <see cref="NavWorld"/> (docs/game/pathfinding.md "Gate codes"; the original's gate-code pass floods its navmesh from the gates, <c>FUN_1402ed460</c>).
/// From each polygon beside a gate's door polygons a flood spreads over the walkable polygons without entering any gate's door; a flood that stays within
/// <see cref="OutsideExtent"/> of where it started, never reaches the edge of the loaded world and stays under <see cref="PolygonBudget"/> is a walled region and gets the
/// next code from 1; any other is the open world, 0, as is every polygon no flood reached. Gates are visited by instance id, so the same world gives the same codes.
/// Engine choices: the extent, the budget and the loaded world's edge stand in for the original's face limit ("FloodFill ... LIMIT REACHED"), whose value is <b>Unknown</b>.
/// Immutable once made; safe from any thread.
/// </summary>
public sealed class NavGateRegions : IGateRegions
{
    /// <summary>How far (X/Z) a region may reach from its first polygon and still be walled in (engine choice: above the largest walled town).</summary>
    public const float OutsideExtent = 4000;
    /// <summary>How many polygons a walled region may hold (engine choice).</summary>
    public const int PolygonBudget = 200_000;
    /// <summary>How far from a point the polygon it stands on is looked for.</summary>
    public const float FindDistance = 40;

    public static readonly NavGateRegions Empty = new(NavWorld.Empty, [], []);

    readonly NavWorld world;
    readonly Dictionary<long, int> codes;

    NavGateRegions(NavWorld world, Dictionary<long, int> codes, IReadOnlyList<GateLink> gates)
    {
        this.world = world;
        this.codes = codes;
        Gates = gates;
    }

    public IReadOnlyList<GateLink> Gates { get; }

    /// <summary>The number of walled regions (codes 1..n).</summary>
    public int RegionCount { get; private init; }

    public int CodeAt(Vector3 at) => world.TryFindPolygon(at, FindDistance, out var p, out _) ? codes.GetValueOrDefault(p.Key) : -1;

    /// <summary>Labels <paramref name="world"/>; <paramref name="isGate"/> tells which door owners (building instance ids) are gates.</summary>
    public static NavGateRegions Label(NavWorld world, Func<string, bool> isGate)
    {
        // The door polygons of gates, by gate.
        var gateOf = new Dictionary<long, string>();
        var gates = new SortedDictionary<string, List<NavRef>>(StringComparer.Ordinal);
        var known = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (int slot = 0; slot < world.ZoneCount; slot++)
        {
            var m = world.Mesh(slot);
            if (m.DoorOf is not { } doorOf || m.DoorIds is not { } ids) continue;
            for (int p = 0; p < m.PolygonCount; p++)
            {
                if (doorOf[p] < 0 || !m.Kept[p]) continue;
                string id = ids[doorOf[p]];
                if (!known.TryGetValue(id, out bool gate)) known[id] = gate = isGate(id);
                if (!gate) continue;
                var r = new NavRef(slot, p);
                gateOf[r.Key] = id;
                if (!gates.TryGetValue(id, out var list)) gates[id] = list = [];
                list.Add(r);
            }
        }
        var codes = new Dictionary<long, int>();
        if (gates.Count == 0) return new NavGateRegions(world, codes, []);
        var outside = new HashSet<long>();
        var links = new List<GateLink>();
        var visited = new List<NavRef>();
        var queue = new Queue<NavRef>();
        var seen = new HashSet<long>();
        int next = 1;
        foreach (var (id, doors) in gates)
        {
            var sides = new SortedSet<int>();
            foreach (var door in doors)
                foreach (var seed in Around(world, door))
                {
                    if (gateOf.ContainsKey(seed.Key) || !world.Mesh(seed.Zone).Kept[seed.Polygon]) continue;
                    if (codes.TryGetValue(seed.Key, out int code)) { sides.Add(code); continue; }
                    if (outside.Contains(seed.Key)) { sides.Add(0); continue; }
                    if (Flood(world, seed, gateOf, visited, queue, seen))
                    {
                        foreach (var r in visited) codes[r.Key] = next;
                        sides.Add(next++);
                    }
                    else
                    {
                        foreach (var r in visited) outside.Add(r.Key);
                        sides.Add(0);
                    }
                }
            links.Add(new GateLink(id, [.. sides]));
        }
        return new NavGateRegions(world, codes, links) { RegionCount = next - 1 };
    }

    /// <summary>
    /// Floods from <paramref name="seed"/> over kept polygons, not into gate doors; true when the region is walled in (bounded, under budget, not at the loaded world's edge).
    /// <paramref name="visited"/> holds what it reached (all of it when walled in; a part when not).
    /// </summary>
    static bool Flood(NavWorld world, NavRef seed, Dictionary<long, string> gateOf, List<NavRef> visited, Queue<NavRef> queue, HashSet<long> seen)
    {
        visited.Clear();
        queue.Clear();
        seen.Clear();
        var start = world.Mesh(seed.Zone).Centre(seed.Polygon);
        queue.Enqueue(seed);
        seen.Add(seed.Key);
        while (queue.Count > 0)
        {
            var r = queue.Dequeue();
            visited.Add(r);
            var m = world.Mesh(r.Zone);
            var c = m.Centre(r.Polygon);
            if (MathF.Abs(c.X - start.X) > OutsideExtent || MathF.Abs(c.Z - start.Z) > OutsideExtent || visited.Count > PolygonBudget || AtOpenEdge(world, m, r.Polygon))
                return false;
            foreach (var n in Around(world, r))
            {
                if (gateOf.ContainsKey(n.Key) || !seen.Add(n.Key) || !world.Mesh(n.Zone).Kept[n.Polygon]) continue;
                queue.Enqueue(n);
            }
        }
        return true;
    }

    /// <summary>The polygons next to <paramref name="r"/>: in its mesh, and across the zone borders.</summary>
    static IEnumerable<NavRef> Around(NavWorld world, NavRef r)
    {
        foreach (int q in world.Mesh(r.Zone).NeighboursOf(r.Polygon)) yield return new NavRef(r.Zone, q);
        foreach (var link in world.LinksOf(r).ToArray()) yield return link.To;
    }

    /// <summary>A polygon touching a border of its zone whose neighbour zone has no mesh loaded: the flood may go on beyond what is known.</summary>
    static bool AtOpenEdge(NavWorld world, ZoneNavMesh m, int polygon)
    {
        const float slack = 1;
        foreach (int v in m.Polygons[polygon])
        {
            var p = m.Vertices[v];
            if (p.X <= m.BoundsMin.X + slack && !world.Contains(new ZoneCoordinate(m.ZoneX - 1, m.ZoneZ))) return true;
            if (p.X >= m.BoundsMax.X - slack && !world.Contains(new ZoneCoordinate(m.ZoneX + 1, m.ZoneZ))) return true;
            if (p.Z <= m.BoundsMin.Y + slack && !world.Contains(new ZoneCoordinate(m.ZoneX, m.ZoneZ - 1))) return true;
            if (p.Z >= m.BoundsMax.Y - slack && !world.Contains(new ZoneCoordinate(m.ZoneX, m.ZoneZ + 1))) return true;
        }
        return false;
    }
}
