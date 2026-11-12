using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The foliage of a gather: the foliage objects of the zone and its neighbours, as cutters or collision shapes.
/// </summary>
internal sealed partial class ZoneGeometryGatherer
{
    #region Foliage

    /// <summary>
    /// How far (units) past the margin a foliage object may stand and still be used (engine choice; its collision reaches into the margin).
    /// </summary>
    const float FoliageReach = 200;

    /// <summary>
    /// The zone and its eight neighbours inside the grid: objects across the border reach into the margin.
    /// </summary>
    static List<ZoneCoordinate> FoliageRing(ZoneCoordinate zone)
    {
        var zones = new List<ZoneCoordinate>();
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                var z = new ZoneCoordinate(zone.X + dx, zone.Y + dz);
                if (z.IsInsideGrid) zones.Add(z);
            }
        }

        return zones;
    }

    /// <summary>
    /// Adds the foliage objects of the zone's ring that stand near the zone.
    /// </summary>
    void AddFoliage(ZoneGeometry g, FoliageObstacles.FoliageObstacle[][] all)
    {
        float margin = g.Margin;
        foreach (var list in all)
        {
            foreach (var o in list)
                AddFoliageObstacle(g, o, margin);
        }
    }

    /// <summary>
    /// Adds one foliage object near the zone: its cutter square, or its collision shapes.
    /// </summary>
    void AddFoliageObstacle(ZoneGeometry g, FoliageObstacles.FoliageObstacle o, float margin)
    {
        var p = o.Position;
        if (p.X < g.ZoneMin.X - margin - FoliageReach || p.X > g.ZoneMax.X + margin + FoliageReach
            || p.Z < g.ZoneMin.Y - margin - FoliageReach || p.Z > g.ZoneMax.Y + margin + FoliageReach)
            return;

        if (o.Collision is null)
        {
            float cutter = o.Cutter;
            g.Stats.FoliageCutters++;
            var corners = new[]
            {
                new Vector2(p.X - cutter, p.Z - cutter),
                new Vector2(p.X + cutter, p.Z - cutter),
                new Vector2(p.X + cutter, p.Z + cutter),
                new Vector2(p.X - cutter, p.Z + cutter),
            };
            g.Carvers.Add(new NavVolume(corners, p.Y - CutterHalfHeight, p.Y + CutterHalfHeight));
            return;
        }

        var prepared = _collision.Get(o.Collision);
        if (prepared is null) return;

        g.Stats.FoliageInstances++;
        int solids = g.Solids.Count;
        foreach (var shape in prepared.Shapes)
        {
            g.Stats.FoliageShapes++;
            if (!shape.IsConvex) g.Stats.FoliageMeshes++;

            // Walkable foliage meshes (swamp tree domes, open below) hold no ground under them in the original's tiles (docs/game/pathfinding.md,
            // "Map features").
            AddShape(g, shape, o.Transform, o.Walkable ? TerrainSlopeDegrees : 0, carve: CarveConvexObstacles && !o.Walkable, meshSolid: o.Walkable);
        }

        for (int k = solids; k < g.Solids.Count; k++)
            g.Solids[k].Feature = true;
    }

    #endregion
}
