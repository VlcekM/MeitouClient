using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// The live kept sets of the loaded zones. Each cached mesh is pruned by its own seeds only (so it does not depend on what else was loaded); when the loaded
/// set changes, every zone's kept set is resolved again: its own, the regions a walker stands on (<see cref="NavMeshService.KeepAt"/>), the regions reaching
/// its border at ground level toward a side whose neighbour is not loaded (<see cref="SeedPruner.ExtendBorder"/>), and what the loaded neighbours' kept
/// border edges reach, until nothing grows. The result is the same in any load order (docs/game/pathfinding.md, "Zone borders"; the original's generator
/// takes the midpoints of already built neighbours' border edges as seeds, order dependent, but ships prebuilt tiles).
/// </summary>
internal static class ZoneKeptClosure
{
    static readonly (int Dx, int Dz)[] Sides = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    /// <summary>The kept set of every zone in <paramref name="full"/> (meshes with their pruned polygons); <paramref name="standing"/>: points whose region is kept too.</summary>
    public static Dictionary<ZoneCoordinate, bool[]> Resolve(IReadOnlyDictionary<ZoneCoordinate, ZoneNavMesh> full, NavBuildSettings s, IReadOnlyDictionary<ZoneCoordinate, List<Vector3>>? standing = null)
    {
        var keep = full.ToDictionary(kv => kv.Key, kv => (bool[])kv.Value.Kept.Clone());
        foreach (var (zone, mesh) in full)
        {
            if (standing?.GetValueOrDefault(zone) is { Count: > 0 } points) SeedPruner.Extend(mesh, keep[zone], points, s);
            foreach (var (dx, dz) in Sides)
                if (!full.ContainsKey(new ZoneCoordinate(zone.X + dx, zone.Y + dz))) SeedPruner.ExtendBorder(mesh, keep[zone], dx, dz);
        }
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (a, ma) in full)
                foreach (var (dx, dz) in Sides)
                {
                    var b = new ZoneCoordinate(a.X + dx, a.Y + dz);
                    if (!full.TryGetValue(b, out var mb)) continue;
                    grew |= SeedPruner.Extend(mb, keep[b], BorderSeeds(ma, keep[a], dx, dz), s);
                }
        }
        return keep;
    }

    /// <summary>The ends (moved in by a unit) and the middle of each kept edge of <paramref name="mesh"/> on its border facing (dx, dz).</summary>
    static List<Vector3> BorderSeeds(ZoneNavMesh mesh, bool[] keep, int dx, int dz)
    {
        var seeds = new List<Vector3>();
        foreach (var (p, k) in mesh.BorderEdges(dx, dz, skipLinked: true, keep))
        {
            var poly = mesh.Polygons[p];
            var va = mesh.Vertices[poly[k]];
            var vb = mesh.Vertices[poly[(k + 1) % poly.Length]];
            float length = Vector3.Distance(va, vb);
            if (length < 1e-3f) continue;
            float t = Math.Min(0.5f, 1 / length);
            seeds.Add(Vector3.Lerp(va, vb, t));
            seeds.Add(Vector3.Lerp(va, vb, 0.5f));
            seeds.Add(Vector3.Lerp(va, vb, 1 - t));
        }
        return seeds;
    }
}
