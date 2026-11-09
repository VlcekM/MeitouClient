using System.Numerics;
using DotRecast.Recast;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Removes the walkable spans of a tile's compact heightfield that lie inside solids, inside map feature meshes or under the terrain
/// (docs/game/pathfinding.md, "Solids", "Map features").
/// </summary>
internal static class NavSolidCarving
{
    /// <summary>
    /// How far below an upward face of a solid a span's floor has to be to count as inside (engine choice: one height cell). Recast rounds a span's
    /// top up to the highest point of its surface over the cell, so a span on the solid's own top is on or above that face and stays.
    /// </summary>
    internal const float SolidTopInset = 1;

    /// <summary>
    /// How far (cells) from where a map feature's surface runs inside a building's solid that surface is removed (engine choice: two cells, so the
    /// building's walkway and the feature's surface are not neighbours; docs/game/pathfinding.md, "Map features").
    /// </summary>
    const int FeatureSeamCells = 2;

    /// <summary>
    /// How far (units) a span's floor may be over a feature triangle's height at the cell's middle and still stand on it (engine choice).
    /// </summary>
    const float FeatureSurfaceReach = 2;

    /// <summary>
    /// The height over a span's floor its enclosure rays run at (engine choice: just over the ground, under a gravel pile's skirt).
    /// </summary>
    const float EncloseRayHeight = 1;

    /// <summary>
    /// How many of the <see cref="EncloseDirections"/> must hit the mesh for a span to be walled in (engine choice).
    /// </summary>
    const int EncloseHits = 4;

    /// <summary>
    /// The eight horizontal directions of the enclosure rays.
    /// </summary>
    static readonly Vector3[] EncloseDirections =
        [.. Enumerable.Range(0, 8).Select(k => new Vector3(MathF.Cos(k * MathF.PI / 4), 0, MathF.Sin(k * MathF.PI / 4)))];

    /// <summary>
    /// Removes the walkable spans whose floor, at the middle of their cell, lies inside the solid (docs/game/pathfinding.md, "Solids").
    /// </summary>
    internal static void RemoveInside(NavSolid solid, RcCompactHeightfield chf)
    {
        float cs = chf.cs, ch = chf.ch;
        int x0 = Math.Max(0, (int)MathF.Floor((solid.Min.X - chf.bmin.X) / cs));
        int x1 = Math.Min(chf.width - 1, (int)MathF.Floor((solid.Max.X - chf.bmin.X) / cs));
        int z0 = Math.Max(0, (int)MathF.Floor((solid.Min.Y - chf.bmin.Z) / cs));
        int z1 = Math.Min(chf.height - 1, (int)MathF.Floor((solid.Max.Y - chf.bmin.Z) / cs));
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                var cell = chf.cells[x + z * chf.width];
                for (int i = cell.index, end = cell.index + cell.count; i < end; i++)
                {
                    if (chf.areas[i] == NavArea.Null) continue;

                    var p = new Vector3(chf.bmin.X + (x + 0.5f) * cs, chf.bmin.Y + chf.spans[i].y * ch, chf.bmin.Z + (z + 0.5f) * cs);
                    bool inside = true;
                    foreach (var plane in solid.Planes)
                    {
                        if (plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z + plane.W >= (plane.Y > 0 ? -SolidTopInset : 0))
                        {
                            inside = false;
                            break;
                        }
                    }

                    if (inside) chf.areas[i] = NavArea.Null;
                }
            }
        }
    }

    /// <summary>
    /// Removes the walkable spans inside a closed or bottom-open triangle mesh (<see cref="NavMeshSolid"/>): the nearest of its triangles above the
    /// span's floor, at the middle of the cell and more than <see cref="SolidTopInset"/> over it, faces up, and the mesh walls the span in
    /// (<see cref="Enclosed"/>; docs/game/pathfinding.md, "Map features").
    /// </summary>
    internal static void RemoveInside(NavMeshSolid solid, RcCompactHeightfield chf, List<NavSolid> allSolids, List<int> tileSolids)
    {
        float cs = chf.cs;
        int x0 = Math.Max(0, (int)MathF.Floor((solid.Min.X - chf.bmin.X) / cs));
        int x1 = Math.Min(chf.width - 1, (int)MathF.Floor((solid.Max.X - chf.bmin.X) / cs));
        int z0 = Math.Max(0, (int)MathF.Floor((solid.Min.Y - chf.bmin.Z) / cs));
        int z1 = Math.Min(chf.height - 1, (int)MathF.Floor((solid.Max.Y - chf.bmin.Z) / cs));
        if (x0 > x1 || z0 > z1) return;

        var hits = Crossings(solid, chf, x0, x1, z0, z1);
        var buried = Buried(chf, hits, allSolids, tileSolids, x0, x1, z0, z1);
        RemoveCovered(solid, chf, hits, buried, x0, x1, z0, z1);
    }

    /// <summary>
    /// The mesh's crossings of each cell's middle line in the box: height, and whether that triangle faces up.
    /// </summary>
    static List<(float Y, bool Up)>?[] Crossings(NavMeshSolid solid, RcCompactHeightfield chf, int x0, int x1, int z0, int z1)
    {
        float cs = chf.cs;
        int w = x1 - x0 + 1;
        var hits = new List<(float Y, bool Up)>?[w * (z1 - z0 + 1)];
        var t = solid.Triangles;
        for (int i = 0; i < t.Length; i += 3)
        {
            Vector3 a = t[i], b = t[i + 1], c = t[i + 2];
            float area = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);

            // Counter-clockwise seen from above (+Y) is a face whose normal points up.
            bool up = area < 0;
            int cx0 = Math.Max(x0, (int)MathF.Ceiling((MathF.Min(a.X, MathF.Min(b.X, c.X)) - chf.bmin.X) / cs - 0.5f));
            int cx1 = Math.Min(x1, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) - chf.bmin.X) / cs - 0.5f));
            int cz0 = Math.Max(z0, (int)MathF.Ceiling((MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - chf.bmin.Z) / cs - 0.5f));
            int cz1 = Math.Min(z1, (int)MathF.Floor((MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) - chf.bmin.Z) / cs - 0.5f));
            for (int z = cz0; z <= cz1; z++)
            {
                for (int x = cx0; x <= cx1; x++)
                {
                    float px = chf.bmin.X + (x + 0.5f) * cs, pz = chf.bmin.Z + (z + 0.5f) * cs;
                    if (!NavGeometry.Barycentric(px, pz, a, b, c, out float u, out float v, out float wt) || u < 0 || v < 0 || wt < 0)
                        continue;

                    ref var list = ref hits[(z - z0) * w + x - x0];
                    (list ??= []).Add((u * a.Y + v * b.Y + wt * c.Y, up));
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Where the feature's upward surface runs inside a building's solid (Mourn's tube through the defensive wall), it does not join the
    /// building's walkway: its own surface within FeatureSeamCells of there is removed. The cells of the box where it does; null when none does.
    /// </summary>
    static bool[]? Buried(RcCompactHeightfield chf, List<(float Y, bool Up)>?[] hits, List<NavSolid> allSolids, List<int> tileSolids, int x0, int x1,
        int z0, int z1)
    {
        float cs = chf.cs;
        int w = x1 - x0 + 1;
        var buried = new bool[hits.Length];
        bool anyBuried = false;
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (hits[(z - z0) * w + x - x0] is not { } list) continue;

                float px = chf.bmin.X + (x + 0.5f) * cs, pz = chf.bmin.Z + (z + 0.5f) * cs;
                foreach (int c in tileSolids)
                {
                    var b = allSolids[c];
                    if (b.Feature || px < b.Min.X || px > b.Max.X || pz < b.Min.Y || pz > b.Max.Y) continue;

                    if (FaceInside(b, list, px, pz))
                    {
                        buried[(z - z0) * w + x - x0] = anyBuried = true;
                        break;
                    }
                }
            }
        }

        return anyBuried ? buried : null;
    }

    /// <summary>
    /// Whether one of the upward crossings in <paramref name="list"/> lies inside the solid <paramref name="b"/> at (<paramref name="px"/>,
    /// <paramref name="pz"/>).
    /// </summary>
    static bool FaceInside(NavSolid b, List<(float Y, bool Up)> list, float px, float pz)
    {
        foreach (var (y, faceUp) in list)
        {
            if (!faceUp) continue;

            bool inside = true;
            foreach (var plane in b.Planes)
            {
                if (plane.X * px + plane.Y * (y - SolidTopInset) + plane.Z * pz + plane.W >= 0)
                {
                    inside = false;
                    break;
                }
            }

            if (inside) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a buried cell lies within <see cref="FeatureSeamCells"/> of cell (<paramref name="x"/>, <paramref name="z"/>).
    /// </summary>
    static bool NearBuried(bool[]? buried, int x, int z, int x0, int x1, int z0, int z1)
    {
        if (buried is null) return false;

        int w = x1 - x0 + 1;
        for (int dz = -FeatureSeamCells; dz <= FeatureSeamCells; dz++)
        {
            for (int dx = -FeatureSeamCells; dx <= FeatureSeamCells; dx++)
            {
                int bx = x + dx - x0, bz = z + dz - z0;
                if (bx >= 0 && bx < w && bz >= 0 && bz <= z1 - z0 && buried[bz * w + bx]) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes the walkable spans of the box whose nearest crossing above faces up and walls them in, and those on the feature's surface by a
    /// buried cell.
    /// </summary>
    static void RemoveCovered(NavMeshSolid solid, RcCompactHeightfield chf, List<(float Y, bool Up)>?[] hits, bool[]? buried, int x0, int x1,
        int z0, int z1)
    {
        float cs = chf.cs, ch = chf.ch;
        int w = x1 - x0 + 1;
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (hits[(z - z0) * w + x - x0] is not { } list) continue;

                bool seam = NearBuried(buried, x, z, x0, x1, z0, z1);
                var cell = chf.cells[x + z * chf.width];
                for (int i = cell.index, end = cell.index + cell.count; i < end; i++)
                {
                    if (chf.areas[i] == NavArea.Null) continue;

                    float floor = chf.bmin.Y + chf.spans[i].y * ch, nearest = float.MaxValue;
                    bool up = false, onSurface = false;
                    foreach (var (y, faceUp) in list)
                    {
                        if (y > floor + SolidTopInset && y < nearest)
                        {
                            nearest = y;
                            up = faceUp;
                        }

                        if (faceUp && floor >= y - SolidTopInset && floor - y <= FeatureSurfaceReach) onSurface = true;
                    }

                    // Under a single-sided top (a trestle bridge's deck) the ground stays walkable unless the mesh walls it in.
                    if (up && !Enclosed(solid.Triangles, new Vector3(
                            chf.bmin.X + (x + 0.5f) * cs, floor + EncloseRayHeight, chf.bmin.Z + (z + 0.5f) * cs)))
                        up = false;

                    if (up || seam && onSurface) chf.areas[i] = NavArea.Null;
                }
            }
        }
    }

    /// <summary>
    /// Whether a point is walled in by a triangle mesh: at least <see cref="EncloseHits"/> of eight horizontal rays from it hit one of its triangles.
    /// </summary>
    static bool Enclosed(Vector3[] t, Vector3 at)
    {
        int hits = 0;
        for (int d = 0; d < EncloseDirections.Length; d++)
        {
            if (RayHitsMesh(t, at, EncloseDirections[d])) hits++;

            if (hits >= EncloseHits) return true;

            if (hits + EncloseDirections.Length - 1 - d < EncloseHits) return false;
        }

        return false;
    }

    /// <summary>
    /// Whether the ray from <paramref name="from"/> along <paramref name="dir"/> crosses one of the mesh's triangles.
    /// </summary>
    static bool RayHitsMesh(Vector3[] t, Vector3 from, Vector3 dir)
    {
        for (int i = 0; i < t.Length; i += 3)
        {
            if (RayHits(from, dir, t[i], t[i + 1], t[i + 2])) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the ray from <paramref name="o"/> along <paramref name="d"/> crosses the triangle (a, b, c), Moller-Trumbore.
    /// </summary>
    static bool RayHits(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-6f) return false;

        float inv = 1 / det;
        Vector3 s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;

        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        return v >= 0 && u + v <= 1 && Vector3.Dot(e2, q) * inv > 0;
    }

    /// <summary>
    /// Removes the walkable spans more than <paramref name="depth"/> (the agent's height) under the terrain at the middle of their cell: the top of a
    /// building part buried in the ground, which has headroom under the terrain's surface (Observed: zone 24.27 by Bad Teeth's gate, a face at 120
    /// under ground at 159; docs/game/pathfinding.md, "Solids"). Only columns with more than one span can hold one; the exterior mesh only.
    /// </summary>
    internal static void RemoveUnderground(HeightWindow terrain, float depth, RcCompactHeightfield chf)
    {
        for (int z = 0; z < chf.height; z++)
        {
            for (int x = 0; x < chf.width; x++)
            {
                var cell = chf.cells[x + z * chf.width];
                if (cell.count < 2) continue;

                float ground = float.NaN;
                for (int i = cell.index, end = cell.index + cell.count; i < end; i++)
                {
                    if (chf.areas[i] == NavArea.Null) continue;

                    if (float.IsNaN(ground) && !terrain.TryHeightAt(chf.bmin.X + (x + 0.5f) * chf.cs, chf.bmin.Z + (z + 0.5f) * chf.cs, out ground))
                        break;

                    if (chf.bmin.Y + chf.spans[i].y * chf.ch < ground - depth) chf.areas[i] = NavArea.Null;
                }
            }
        }
    }
}
