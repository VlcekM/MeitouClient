using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Navigation;

public sealed partial class ZoneGeometryGatherer
{

    void AddTerrain(ZoneGeometry g)
    {
        int step = 1;
        int c0 = (int)Math.Floor(WorldLayout.ToSample(g.ZoneMin.X - g.Margin, g.ZoneMin.Y - g.Margin).Column);
        int r0 = (int)Math.Floor(WorldLayout.ToSample(g.ZoneMin.X - g.Margin, g.ZoneMin.Y - g.Margin).Row);
        int c1 = (int)Math.Ceiling(WorldLayout.ToSample(g.ZoneMax.X + g.Margin, g.ZoneMax.Y + g.Margin).Column);
        int r1 = (int)Math.Ceiling(WorldLayout.ToSample(g.ZoneMax.X + g.Margin, g.ZoneMax.Y + g.Margin).Row);
        int cols = c1 - c0 + 1, rows = r1 - r0 + 1;
        var win = map.ReadWindow(c0, r0, cols, rows, step);
        float water = WorldWater.Height;
        var index = new int[cols * rows];
        var wet = new bool[cols * rows];
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < cols; i++)
            {
                var p = win.Position(i, j);
                wet[j * cols + i] = p.Y <= water;
                if (p.Y < water) p.Y = water; // the water surface replaces the ground under it
                index[j * cols + i] = g.AddVertex(p);
            }
        float cosTerrain = MathF.Cos(TerrainSlopeDegrees * MathF.PI / 180);
        var v = g.Vertices;
        for (int j = 0; j < rows - 1; j++)
            for (int i = 0; i < cols - 1; i++)
            {
                int a = j * cols + i, b = a + 1, c = a + cols, d = c + 1;
                Tri(a, c, b);
                Tri(b, c, d);
            }

        void Tri(int p, int q, int r)
        {
            int n = (wet[p] ? 1 : 0) + (wet[q] ? 1 : 0) + (wet[r] ? 1 : 0);
            byte area;
            if (n >= 2) { area = NavArea.Water; g.Stats.WaterTriangles++; }
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
