using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Navigation;

internal sealed partial class ZoneGeometryGatherer
{
    // ---- map features ----

    MapFeatureFile? features;

    /// <summary>
    /// The collision of the map features (<c>features.dat</c>: rocks, cliffs, the Rebirth statue, gravel piles, trestle bridges) placed in the zone and its eight
    /// neighbours, walkable up to the terrain's slope like walkable foliage (docs/game/pathfinding.md, "Map features"): an owner-less collision object, group 14, material −1 to the
    /// original's generator, cutting and walkable. A closed convex shape is a <see cref="NavSolid"/>; a triangle mesh a
    /// <see cref="NavMeshSolid"/>, so the ground inside the rock is removed (Observed: Rebirth, zone 21.19, the statue's foundation and the gravel piles).
    /// </summary>
    void AddMapFeatures(ZoneGeometry g)
    {
        var features = Features();
        foreach (var zone in FoliageRing(g.Zone))
        {
            int index = zone.Y * WorldLayout.ZoneCount + zone.X;
            if (index >= features.Zones.Count) continue;
            for (int i = 0; i < features.Zones[index].Count; i++)
            {
                var f = features.Zones[index][i];
                if (db.Find(f.StringId) is not { Type: FcsRecordType.MAP_FEATURES } record || record.GetPath("collision") is not { Length: > 0 } path) continue;
                if (collision.Get(path) is not { } prepared) continue;
                var node = WorldObjectLayout.InstanceTransform(f.Position, f.Rotation, f.Scale);
                int solids = g.Solids.Count, meshSolids = g.MeshSolids.Count;
                foreach (var shape in prepared.Shapes) AddShape(g, shape, node, TerrainSlopeDegrees, carve: false, meshSolid: true);
                if (g.Solids.Count > solids || g.MeshSolids.Count > meshSolids) g.Stats.MapFeatures++;
                for (int k = solids; k < g.Solids.Count; k++) { g.Solids[k].Owner = $"feature {record.Name} [{f.StringId}]"; g.Solids[k].Feature = true; }
                for (int k = meshSolids; k < g.MeshSolids.Count; k++) g.MeshSolids[k].Owner = $"feature {record.Name} [{f.StringId}]";
            }
        }
    }

    MapFeatureFile Features() =>
        features ??= File.Exists(Path.Combine(install.DataDirectory, MapFeatureFile.RelativePath)) ? MapFeatureFile.Open(install) : new MapFeatureFile();

    /// <summary>
    /// The collision triangles of the map features that ragdolls land on (docs/game/ragdolls.md "Collision groups": an outdoor body, group 3, hits group 14, the
    /// features' group), from the placements in the zone and its neighbours: each triangle once, in the zone its centre lies in (a feature can be thousands of units across, more
    /// than a building's reach). Three points per triangle, world space.
    /// </summary>
    internal void AddRagdollFeatures(ZoneCoordinate zone, List<Vector3> output)
    {
        var features = Features();
        foreach (var near in FoliageRing(zone))
        {
            int index = near.Y * WorldLayout.ZoneCount + near.X;
            if (index >= features.Zones.Count) continue;
            foreach (var f in features.Zones[index])
            {
                if (db.Find(f.StringId) is not { Type: FcsRecordType.MAP_FEATURES } record || record.GetPath("collision") is not { Length: > 0 } path) continue;
                if (collision.Get(path) is not { } prepared) continue;
                var node = WorldObjectLayout.InstanceTransform(f.Position, f.Rotation, f.Scale);
                foreach (var shape in prepared.Shapes)
                    for (int t = 0; t + 2 < shape.Indices.Length; t += 3)
                    {
                        Vector3 a = Vector3.Transform(shape.Vertices[shape.Indices[t]], node), b = Vector3.Transform(shape.Vertices[shape.Indices[t + 1]], node), c = Vector3.Transform(shape.Vertices[shape.Indices[t + 2]], node);
                        var centre = (a + b + c) / 3;
                        if (WorldLayout.ZoneOf(centre.X, centre.Z) != zone) continue;
                        output.Add(a); output.Add(b); output.Add(c);
                    }
            }
        }
    }

    /// <summary>The triangles of a placed triangle mesh as a <see cref="NavMeshSolid"/> (null when none faces up or down).</summary>
    static NavMeshSolid? MeshSolid(Vector3[] pts, int[] idx, Vector3 lo, Vector3 hi)
    {
        var tris = new List<Vector3>(idx.Length);
        for (int t = 0; t < idx.Length; t += 3)
        {
            Vector3 a = pts[idx[t]], b = pts[idx[t + 1]], c = pts[idx[t + 2]];
            if (MathF.Abs((b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X)) < 1e-6f) continue; // vertical: no height over a cell
            tris.Add(a); tris.Add(b); tris.Add(c);
        }
        return tris.Count == 0 ? null : new NavMeshSolid([.. tris], new Vector2(lo.X, lo.Z), new Vector2(hi.X, hi.Z));
    }
}
