using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;

namespace Meitou.Navigation;

internal sealed partial class ZoneGeometryGatherer
{
    /// <summary>The zone's heightmap samples at full resolution, one sample past each edge.</summary>
    public HeightWindow ZoneHeights(ZoneCoordinate zone)
    {
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var (c0, r0) = WorldLayout.ToSample(ox, oz);
        return map.ReadWindow((int)Math.Floor(c0) - 1, (int)Math.Floor(r0) - 1, WorldLayout.SamplesPerZone + 3, WorldLayout.SamplesPerZone + 3);
    }

    /// <summary>Heightmap samples per terrain quad edge.</summary>
    const int TerrainSampleStep = 2;

    static int FloorTo(int value, int step) => (int)Math.Floor(value / (double)step) * step;

    void AddTerrain(ZoneGeometry g)
    {
        // Every second sample (36 units), on even samples, as the original's generator (docs/game/pathfinding.md, terrain grid).
        int step = TerrainSampleStep;
        int c0 = FloorTo((int)Math.Floor(WorldLayout.ToSample(g.ZoneMin.X - g.Margin, g.ZoneMin.Y - g.Margin).Column), step);
        int r0 = FloorTo((int)Math.Floor(WorldLayout.ToSample(g.ZoneMin.X - g.Margin, g.ZoneMin.Y - g.Margin).Row), step);
        int c1 = (int)Math.Ceiling(WorldLayout.ToSample(g.ZoneMax.X + g.Margin, g.ZoneMax.Y + g.Margin).Column);
        int r1 = (int)Math.Ceiling(WorldLayout.ToSample(g.ZoneMax.X + g.Margin, g.ZoneMax.Y + g.Margin).Row);
        int cols = (c1 - c0 + step - 1) / step + 1, rows = (r1 - r0 + step - 1) / step + 1;
        var win = map.ReadWindow(c0, r0, cols, rows, step);
        float water = WorldWater.Height;
        var index = new int[cols * rows];
        var depth = new float[cols * rows];   // under the water level; 0 or less: dry
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
            {
                var p = win.Position(i, j);
                depth[j * cols + i] = water - p.Y;
                if (p.Y < water) p.Y = water; // the water surface replaces the ground under it
                index[j * cols + i] = g.AddVertex(p);
            }
        float cosTerrain = MathF.Cos(TerrainSlopeDegrees * MathF.PI / 180);
        var v = g.Vertices;
        for (int j = 0; j < rows - 1; j++)
            for (int i = 0; i < cols - 1; i++)
            {
                int a = j * cols + i, b = a + 1, c = a + cols, d = c + 1;
                // Cut from the cell's (i, j) corner to (i + 1, j + 1): the original's outlines follow this diagonal (docs/game/pathfinding.md, "Terrain").
                Tri(a, c, d);
                Tri(a, d, b);
            }

        void Tri(int p, int q, int r)
        {
            int n = (depth[p] >= 0 ? 1 : 0) + (depth[q] >= 0 ? 1 : 0) + (depth[r] >= 0 ? 1 : 0);
            byte area;
            // Banded by its shallowest corner (engine choice: a triangle across a band edge costs the cheaper band).
            if (n >= 2) { area = NavArea.OfWetState(Speed.WetStateOfDepth(MathF.Min(depth[p], MathF.Min(depth[q], depth[r])))); g.Stats.WaterTriangles++; }
            else
            {
                int ia = index[p] * 3, ib = index[q] * 3, ic = index[r] * 3;
                var e0 = new Vector3(v[ib] - v[ia], v[ib + 1] - v[ia + 1], v[ib + 2] - v[ia + 2]);
                var e1 = new Vector3(v[ic] - v[ia], v[ic + 1] - v[ia + 1], v[ic + 2] - v[ia + 2]);
                var normal = Vector3.Normalize(Vector3.Cross(e0, e1));
                area = normal.Y >= cosTerrain ? NavArea.Ground : NavArea.Null;
                g.Stats.TerrainTriangles++;
            }
            g.AddTriangle(index[p], index[q], index[r], area);
        }
    }
}
