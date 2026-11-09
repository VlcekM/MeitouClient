using System.Diagnostics;
using System.Numerics;
using DotRecast.Recast;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// Builds a zone's navmesh from its gathered geometry with Recast (DotRecast): per tile rasterise the triangles with their areas, cut carvers, paint
/// doors, partition, contour and triangulate (<see cref="NavTileBuilder"/>); then stitch the tiles (<see cref="NavTileStitcher"/>), and keep only
/// the regions near a seed (docs/game/pathfinding.md, Generator "Seeds" and `regionPruningSettings`). Radius 0: clearance is a query-time check.
/// </summary>
internal static class ZoneNavMeshBuilder
{
    /// <summary>
    /// The triangles and the volumes of a zone that touch each tile (or its border), by tile.
    /// </summary>
    sealed class TileBuckets
    {
        /// <summary>
        /// Triangle indices per tile.
        /// </summary>
        public readonly List<int>[] Triangles;

        /// <summary>
        /// Carver indices per tile.
        /// </summary>
        public readonly List<int>[] Carvers;

        /// <summary>
        /// Door painter indices per tile.
        /// </summary>
        public readonly List<int>[] Painters;

        /// <summary>
        /// Solid indices per tile.
        /// </summary>
        public readonly List<int>[] Solids;

        /// <summary>
        /// Mesh solid indices per tile.
        /// </summary>
        public readonly List<int>[] MeshSolids;

        /// <summary>
        /// The zone's geometry.
        /// </summary>
        readonly ZoneGeometry _g;

        /// <summary>
        /// Tiles along a zone side.
        /// </summary>
        readonly int _tilesPerSide;

        /// <summary>
        /// A tile's side in world units.
        /// </summary>
        readonly float _tileWorld;

        /// <summary>
        /// The tile border's width in world units.
        /// </summary>
        readonly float _pad;

        /// <summary>
        /// Buckets the zone's triangles (<paramref name="verts"/>, <paramref name="indices"/>) and volumes by tile.
        /// </summary>
        public TileBuckets(ZoneGeometry g, NavBuildSettings s, int tilesPerSide, float tileWorld, float[] verts, int[] indices)
        {
            _g = g;
            _tilesPerSide = tilesPerSide;
            _tileWorld = tileWorld;
            _pad = NavTileBuilder.TileBorderCells * s.CellSize;
            Triangles = new List<int>[tilesPerSide * tilesPerSide];
            Carvers = new List<int>[tilesPerSide * tilesPerSide];
            Painters = new List<int>[tilesPerSide * tilesPerSide];
            Solids = new List<int>[tilesPerSide * tilesPerSide];
            MeshSolids = new List<int>[tilesPerSide * tilesPerSide];
            for (int i = 0; i < Triangles.Length; i++)
            {
                Triangles[i] = [];
                Carvers[i] = [];
                Painters[i] = [];
                Solids[i] = [];
                MeshSolids[i] = [];
            }

            AddTriangles(verts, indices);
            AddVolumes();
        }

        /// <summary>
        /// Buckets each triangle by its box.
        /// </summary>
        void AddTriangles(float[] verts, int[] indices)
        {
            for (int t = 0; t < indices.Length; t += 3)
            {
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                for (int k = 0; k < 3; k++)
                {
                    float x = verts[indices[t + k] * 3], z = verts[indices[t + k] * 3 + 2];
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    z0 = Math.Min(z0, z);
                    z1 = Math.Max(z1, z);
                }

                Add(x0, z0, x1, z1, Triangles, t / 3);
            }
        }

        /// <summary>
        /// Buckets the carvers, the door painters, the solids and the mesh solids by their boxes.
        /// </summary>
        void AddVolumes()
        {
            var g = _g;
            for (int c = 0; c < g.Carvers.Count; c++)
            {
                var (a, b) = Bounds(g.Carvers[c]);
                Add(a.X, a.Y, b.X, b.Y, Carvers, c);
            }

            for (int c = 0; c < g.Painters.Count; c++)
            {
                var (a, b) = Bounds(g.Painters[c]);
                Add(a.X, a.Y, b.X, b.Y, Painters, c);
            }

            for (int c = 0; c < g.Solids.Count; c++)
                Add(g.Solids[c].Min.X, g.Solids[c].Min.Y, g.Solids[c].Max.X, g.Solids[c].Max.Y, Solids, c);

            for (int c = 0; c < g.MeshSolids.Count; c++)
                Add(g.MeshSolids[c].Min.X, g.MeshSolids[c].Min.Y, g.MeshSolids[c].Max.X, g.MeshSolids[c].Max.Y, MeshSolids, c);
        }

        /// <summary>
        /// Adds <paramref name="id"/> to the buckets of the tiles its box touches.
        /// </summary>
        void Add(float x0, float z0, float x1, float z1, List<int>[] buckets, int id)
        {
            var g = _g;
            int tilesPerSide = _tilesPerSide;
            float tileWorld = _tileWorld;

            // A tile also takes what lies in its border (BuildTile).
            x0 -= _pad;
            z0 -= _pad;
            x1 += _pad;
            z1 += _pad;
            int tx0 = Math.Clamp((int)MathF.Floor((x0 - g.ZoneMin.X) / tileWorld), 0, tilesPerSide - 1);
            int tx1 = Math.Clamp((int)MathF.Floor((x1 - g.ZoneMin.X) / tileWorld), 0, tilesPerSide - 1);
            int tz0 = Math.Clamp((int)MathF.Floor((z0 - g.ZoneMin.Y) / tileWorld), 0, tilesPerSide - 1);
            int tz1 = Math.Clamp((int)MathF.Floor((z1 - g.ZoneMin.Y) / tileWorld), 0, tilesPerSide - 1);
            if (x1 < g.ZoneMin.X || x0 > g.ZoneMax.X || z1 < g.ZoneMin.Y || z0 > g.ZoneMax.Y) return;

            for (int tz = tz0; tz <= tz1; tz++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                    buckets[tz * tilesPerSide + tx].Add(id);
            }
        }
    }

    /// <summary>
    /// Builds the zone's mesh: its tiles, stitched, with their heights and doors, then pruned to the regions near a seed.
    /// </summary>
    public static ZoneNavMesh Build(ZoneGeometry g, NavBuildSettings s, out NavBuildTimes times)
    {
        var total = Stopwatch.StartNew();
        times = new NavBuildTimes();
        int tilesPerSide = TilesPerSide(g, s);
        float tileWorld = s.TileCells * s.CellSize;
        var verts = g.Vertices.ToArray();
        var indices = g.Indices.ToArray();
        var areas = g.Areas.Select(a => (int)a).ToArray();

        // Bucket the triangles and the volumes by tile.
        var buckets = new TileBuckets(g, s, tilesPerSide, tileWorld, verts, indices);
        var (meshes, details) = BuildTiles(g, s, verts, indices, areas, buckets, tilesPerSide, tileWorld, times);

        var watch = Stopwatch.StartNew();
        var mesh = NavTileStitcher.Stitch(g, s, meshes, tilesPerSide, tileWorld, details);
        NavPolygonHeights.AddDetail(mesh, meshes, details, g.Ground);
        NavDoorAssignment.Assign(mesh, g, s);
        times.Stitch = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        mesh = Prune(mesh, g, s);
        times.Prune = watch.Elapsed.TotalMilliseconds;

        times.Total = total.Elapsed.TotalMilliseconds;
        times.Polygons = mesh.PolygonCount;
        times.KeptPolygons = mesh.KeptCount;
        times.Vertices = mesh.Vertices.Length;
        return mesh;
    }

    /// <summary>
    /// Stitches the tiles' meshes into the zone's mesh (<see cref="NavTileStitcher.Stitch"/>).
    /// </summary>
    internal static ZoneNavMesh Stitch(
        ZoneGeometry g, NavBuildSettings s, RcPolyMesh?[] meshes, int tilesPerSide, float tileWorld, HeightGrid?[]?[]? details = null) =>
        NavTileStitcher.Stitch(g, s, meshes, tilesPerSide, tileWorld, details);

    /// <summary>
    /// Tiles along a zone side; the zone's side has to be a whole number of tiles.
    /// </summary>
    static int TilesPerSide(ZoneGeometry g, NavBuildSettings s)
    {
        float zoneSize = g.ZoneMax.X - g.ZoneMin.X;
        int cellsPerSide = (int)MathF.Round(zoneSize / s.CellSize);
        if (cellsPerSide % s.TileCells != 0)
            throw new ArgumentException($"{cellsPerSide} cells per zone side is not a multiple of {s.TileCells}");

        return cellsPerSide / s.TileCells;
    }

    /// <summary>
    /// Builds every tile with triangles, in parallel, and records the tiles' times.
    /// </summary>
    static (RcPolyMesh?[] Meshes, HeightGrid?[]?[] Details) BuildTiles(ZoneGeometry g, NavBuildSettings s, float[] verts, int[] indices,
        int[] areas, TileBuckets buckets, int tilesPerSide, float tileWorld, NavBuildTimes times)
    {
        var ticks = new NavTileBuilder.TileTicks();
        var meshes = new RcPolyMesh?[tilesPerSide * tilesPerSide];
        var details = new HeightGrid?[]?[meshes.Length];
        var workers = s.Threads > 0 ? s.Threads : Math.Max(1, Environment.ProcessorCount - 1);
        var watch = Stopwatch.StartNew();
        Parallel.For(0, meshes.Length, new ParallelOptions { MaxDegreeOfParallelism = workers }, tile =>
        {
            if (buckets.Triangles[tile].Count == 0) return;

            int tx = tile % tilesPerSide, tz = tile / tilesPerSide;
            meshes[tile] = NavTileBuilder.BuildTile(g, s, verts, indices, areas, buckets.Triangles[tile], buckets.Carvers[tile],
                buckets.Painters[tile], buckets.Solids[tile], buckets.MeshSolids[tile], ticks, g.ZoneMin.X + tx * tileWorld,
                g.ZoneMin.Y + tz * tileWorld, tileWorld, out details[tile]);
        });

        times.Tiles = watch.Elapsed.TotalMilliseconds;
        times.TileCount = meshes.Count(m => m is not null);
        double ms = 1000.0 / Stopwatch.Frequency;
        times.CpuRaster = ticks.Raster * ms;
        times.CpuCompact = ticks.Compact * ms;
        times.CpuRegions = ticks.Regions * ms;
        times.CpuContours = ticks.Contours * ms;
        times.CpuMesh = ticks.Mesh * ms;
        times.CpuDetail = ticks.Detail * ms;
        return (meshes, details);
    }

    /// <summary>
    /// Keeps the regions near a seed (<see cref="SeedPruner"/>), and drops a ruin's floor outside its destroyed boundary.
    /// </summary>
    static ZoneNavMesh Prune(ZoneNavMesh mesh, ZoneGeometry g, NavBuildSettings s)
    {
        SeedPruner.Prune(mesh, g, s);

        // A ruin's floor outside its destroyed boundary lies under walls without collision: dropped, so no border closure or hull join keeps it
        // again.
        if (g.DestroyedOutline is { } outline)
        {
            for (int p = 0; p < mesh.PolygonCount; p++)
            {
                if (mesh.Kept[p])
                {
                    var c = mesh.Centre(p);
                    mesh.Kept[p] = NavGeometry.InsideOutline(outline, new Vector2(c.X, c.Z));
                }
            }

            mesh = mesh.WithoutPruned();
        }

        return mesh;
    }

    /// <summary>
    /// The box of a volume's outline.
    /// </summary>
    static (Vector2 Min, Vector2 Max) Bounds(NavVolume v)
    {
        var lo = new Vector2(float.MaxValue);
        var hi = new Vector2(float.MinValue);
        foreach (var p in v.Polygon)
        {
            lo = Vector2.Min(lo, p);
            hi = Vector2.Max(hi, p);
        }

        return (lo, hi);
    }
}
