using System.Numerics;
using DotRecast.Recast;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The heights of each polygon's own surface (docs/game/pathfinding.md, "Heights"): sampled per tile from its compact heightfield, and kept on the
/// stitched zone mesh for the polygons whose surface leaves their plane.
/// </summary>
internal static class NavPolygonHeights
{
    /// <summary>
    /// Cells between two height samples of a polygon (engine choice: 4 units, under a stair tread).
    /// </summary>
    const int HeightSampleCells = 2;

    /// <summary>
    /// How far (units) a polygon's samples have to leave its plane for them to be kept (engine choice: most polygons are flat ground).
    /// </summary>
    const float HeightKeep = 2;

    /// <summary>
    /// How far (units) from a polygon's plane a span may be and still be its surface when the polygon has no region (engine choice: under half a
    /// storey).
    /// </summary>
    const float HeightReach = 25;

    /// <summary>
    /// How near (units) the heightmap every sample of a polygon has to be for the polygon to stand on the terrain
    /// (<see cref="HeightGrid.OnTerrain"/>; engine choice: the spans come from the terrain's 36-unit triangles, the heightmap's 18-unit samples
    /// differ from them by a few units on curved ground).
    /// </summary>
    const float TerrainMatch = 3;

    /// <summary>
    /// The heights of each polygon's own surface (docs/game/pathfinding.md, "Heights"): every <see cref="HeightSampleCells"/> cells over its box, the
    /// walkable span of the region it was made from, else the walkable span nearest its plane within <see cref="HeightReach"/>, NaN where there is
    /// none; at the stitched vertices' level (half a cell under the span's top). DotRecast leaves the region 0 on polygons merged across regions:
    /// their regions are those of the spans at their corners (<see cref="CornerRegions"/>), each sample taking the one of theirs nearest the plane,
    /// which over a large polygon on a slope lies far off it. A polygon is planar only between its corners, so over stairs, ramps and humps it lay up
    /// to ~40 off the surface. Recast's own detail mesh gives the same heights at a hundred times the cost.
    /// </summary>
    internal static HeightGrid?[] Sample(RcPolyMesh m, RcCompactHeightfield chf, int border)
    {
        var grids = new HeightGrid?[m.npolys];
        int nvp = m.nvp;
        float step = HeightSampleCells * chf.cs;
        var corner = new Vector3[nvp];
        var regions = new HashSet<int>();
        for (int p = 0; p < m.npolys; p++)
        {
            int reg = m.regs[p];
            regions.Clear();
            if (reg == 0) CornerRegions(m, chf, border, p, regions);
            else regions.Add(reg);

            int x0 = int.MaxValue, x1 = int.MinValue, z0 = int.MaxValue, z1 = int.MinValue;
            int count = 0;
            for (int k = 0; k < nvp && m.polys[p * nvp * 2 + k] != RcRecast.RC_MESH_NULL_IDX; k++, count++)
            {
                int v = m.polys[p * nvp * 2 + k] * 3;
                x0 = Math.Min(x0, m.verts[v]);
                x1 = Math.Max(x1, m.verts[v]);
                z0 = Math.Min(z0, m.verts[v + 2]);
                z1 = Math.Max(z1, m.verts[v + 2]);
                corner[k] = new Vector3(m.verts[v], m.verts[v + 1] - 0.5f, m.verts[v + 2]);
            }

            // The plane's height (in cells) at a cell: the fan triangle the point is in, else the one it is nearest to, extended.
            float Plane(float cx, float cz)
            {
                float best = float.MinValue, y = corner[0].Y;
                for (int k = 1; k + 1 < count; k++)
                {
                    if (!NavGeometry.Barycentric(cx, cz, corner[0], corner[k], corner[k + 1], out float u, out float v, out float w))
                        continue;

                    float inside = MathF.Min(u, MathF.Min(v, w));
                    if (inside <= best) continue;

                    best = inside;
                    y = u * corner[0].Y + v * corner[k].Y + w * corner[k + 1].Y;
                }

                return y;
            }

            // Cell centres from the box's first cell, one column past its last cell so the far edge lies between samples.
            int columns = (x1 - 1 - x0) / HeightSampleCells + 2, rows = (z1 - 1 - z0) / HeightSampleCells + 2;
            var values = new float[columns * rows];
            var own = new bool[columns * rows];
            SampleCells(chf, border, x0, z0, columns, regions, Plane, values, own);
            FillFromNeighbours(chf, border, x0, z0, columns, values);
            grids[p] = new HeightGrid(chf.bmin.X + (x0 + border + 0.5f) * chf.cs, chf.bmin.Z + (z0 + border + 0.5f) * chf.cs, step, columns, rows,
                values) { Own = own };
        }

        return grids;
    }

    /// <summary>
    /// Gives the polygons whose surface leaves their plane by more than <see cref="HeightKeep"/> their height samples
    /// (<see cref="ZoneNavMesh.Heights"/>). The zone's polygons are the tiles' in tile order (<see cref="NavTileStitcher"/>).
    /// </summary>
    internal static void AddDetail(ZoneNavMesh mesh, RcPolyMesh?[] meshes, HeightGrid?[]?[] details, HeightWindow? ground)
    {
        var heights = new HeightGrid?[mesh.PolygonCount];
        bool any = false;
        int polygon = 0;
        for (int tile = 0; tile < meshes.Length; tile++)
        {
            if (meshes[tile] is not { } m) continue;

            for (int p = 0; p < m.npolys; p++, polygon++)
            {
                if (details[tile]?[p] is not { } grid) continue;

                grid.Own = null;   // only the stitch reads it
                if (KeptHeights(mesh, polygon, grid, ground) is not { } kept) continue;

                heights[polygon] = kept;
                any = true;
            }
        }

        if (any) mesh.Heights = heights;
    }

    /// <summary>
    /// The heights a polygon keeps: none when its samples stay within <see cref="HeightKeep"/> of its plane, <see cref="HeightGrid.OnTerrain"/>
    /// when every sample inside it is on the terrain, else its samples.
    /// </summary>
    static HeightGrid? KeptHeights(ZoneNavMesh mesh, int polygon, HeightGrid grid, HeightWindow? ground)
    {
        float off = 0;
        bool onTerrain = ground is not null;
        for (int j = 0; j < grid.Rows; j++)
        {
            for (int i = 0; i < grid.Columns; i++)
            {
                float y = grid.Values[j * grid.Columns + i];
                float x = grid.X0 + i * grid.Step, z = grid.Z0 + j * grid.Step;
                if (float.IsNaN(y) || !mesh.Contains(polygon, x, z)) continue;

                off = MathF.Max(off, MathF.Abs(y - mesh.HeightAt(polygon, x, z)));
                if (onTerrain && !(ground!.TryHeightAt(x, z, out float t) && MathF.Abs(MathF.Max(t, WorldWater.Height) - y) <= TerrainMatch))
                    onTerrain = false;
            }
        }

        if (off <= HeightKeep) return null;

        return onTerrain ? HeightGrid.OnTerrain : grid;
    }

    /// <summary>
    /// Each sample of a polygon's box (<see cref="Sample"/>): the walkable span of its regions nearest its <paramref name="planeAt"/> (in cells),
    /// else the one nearest the plane within <see cref="HeightReach"/>; NaN where there is none.
    /// </summary>
    static void SampleCells(RcCompactHeightfield chf, int border, int x0, int z0, int columns, HashSet<int> regions,
        Func<float, float, float> planeAt, float[] values, bool[] own)
    {
        for (int j = 0, rows = values.Length / columns; j < rows; j++)
        {
            for (int i = 0; i < columns; i++)
            {
                int cx = x0 + i * HeightSampleCells + border, cz = z0 + j * HeightSampleCells + border;
                float y = float.NaN;
                if (cx < chf.width && cz < chf.height)
                {
                    var cell = chf.cells[cx + cz * chf.width];
                    float plane = planeAt(cx - border + 0.5f, cz - border + 0.5f), nearest = HeightReach / chf.ch, nearestOwn = float.MaxValue;
                    for (int s = cell.index, end = cell.index + cell.count; s < end; s++)
                    {
                        if (chf.areas[s] == NavArea.Null) continue;

                        float top = chf.spans[s].y - 0.5f;
                        float off = MathF.Abs(top - plane);
                        if (regions.Contains(chf.spans[s].reg))
                        {
                            if (off >= nearestOwn) continue;

                            (nearestOwn, y) = (off, top);
                            own[j * columns + i] = true;
                        }
                        else if (!own[j * columns + i] && off < nearest)
                        {
                            (nearest, y) = (off, top);
                        }
                    }

                    if (!float.IsNaN(y)) y = chf.bmin.Y + y * chf.ch;
                }

                values[j * columns + i] = y;
            }
        }
    }

    /// <summary>
    /// Gives the samples <see cref="SampleCells"/> left without a span the walkable span within a climb a cell of a sampled neighbour's height,
    /// spreading from sample to sample until none is added (engine choice). A polygon is one connected surface, so where the ground leaves its plane
    /// by more than <see cref="HeightReach"/> in a region none of its corners touches, its height carries on from its neighbours (Observed zone 20.32
    /// at -50695,805, docs/game/pathfinding.md "Polygon heights"). Without it the plane, ~40 over the ground, was taken there and walkers could not
    /// step onto it.
    /// </summary>
    static void FillFromNeighbours(RcCompactHeightfield chf, int border, int x0, int z0, int columns, float[] values)
    {
        int rows = values.Length / columns;
        float reach = chf.walkableClimb * HeightSampleCells * chf.ch;
        var queue = new Queue<int>();
        for (int k = 0; k < values.Length; k++)
        {
            if (!float.IsNaN(values[k])) queue.Enqueue(k);
        }

        while (queue.TryDequeue(out int k))
        {
            int i = k % columns, j = k / columns;
            Spread(i - 1, j, values[k]);
            Spread(i + 1, j, values[k]);
            Spread(i, j - 1, values[k]);
            Spread(i, j + 1, values[k]);
        }

        void Spread(int i, int j, float from)
        {
            if (i < 0 || j < 0 || i >= columns || j >= rows || !float.IsNaN(values[j * columns + i])) return;

            int cx = x0 + i * HeightSampleCells + border, cz = z0 + j * HeightSampleCells + border;
            if (cx >= chf.width || cz >= chf.height) return;

            var cell = chf.cells[cx + cz * chf.width];
            float y = float.NaN, nearest = reach;
            for (int s = cell.index, end = cell.index + cell.count; s < end; s++)
            {
                if (chf.areas[s] == NavArea.Null) continue;

                float top = chf.bmin.Y + (chf.spans[s].y - 0.5f) * chf.ch;
                if (MathF.Abs(top - from) > nearest) continue;

                (nearest, y) = (MathF.Abs(top - from), top);
            }

            if (float.IsNaN(y)) return;

            values[j * columns + i] = y;
            queue.Enqueue(j * columns + i);
        }
    }

    /// <summary>
    /// The regions (not the tile border's) of the walkable spans within the climb of a polygon's corners, in the four cells round each.
    /// </summary>
    static void CornerRegions(RcPolyMesh m, RcCompactHeightfield chf, int border, int p, HashSet<int> regions)
    {
        int nvp = m.nvp;
        for (int k = 0; k < nvp && m.polys[p * nvp * 2 + k] != RcRecast.RC_MESH_NULL_IDX; k++)
        {
            int v = m.polys[p * nvp * 2 + k] * 3;
            int vx = m.verts[v] + border, vy = m.verts[v + 1], vz = m.verts[v + 2] + border;
            for (int cz = vz - 1; cz <= vz; cz++)
            {
                for (int cx = vx - 1; cx <= vx; cx++)
                {
                    if (cx < 0 || cz < 0 || cx >= chf.width || cz >= chf.height) continue;

                    var cell = chf.cells[cx + cz * chf.width];
                    for (int s = cell.index, end = cell.index + cell.count; s < end; s++)
                    {
                        int r = chf.spans[s].reg;
                        bool own = r != 0 && (r & RcRecast.RC_BORDER_REG) == 0 && chf.areas[s] != NavArea.Null;
                        if (own && Math.Abs(chf.spans[s].y - vy) <= chf.walkableClimb) regions.Add(r);
                    }
                }
            }
        }
    }
}
