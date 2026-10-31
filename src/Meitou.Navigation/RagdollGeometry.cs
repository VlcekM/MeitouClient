using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Ragdolls;

namespace Meitou.Navigation;

/// <summary>
/// What ragdolls fall onto in the world: the terrain heightmap, and the collision of the buildings and map features (<see cref="ZoneGeometryGatherer.RagdollTriangles"/>), gathered per zone on a
/// worker the first time a ragdoll moves near it and kept in 32-unit cells (<see cref="RagdollPhysics"/>' tiles). Until a zone is gathered its buildings are reported not ready, and
/// the tile asks again.
/// </summary>
public sealed class RagdollGeometry : IRagdollEnvironment, IDisposable
{
    readonly ZoneGeometryGatherer gatherer;
    readonly Func<float, float, float> terrain;
    readonly ConcurrentDictionary<ZoneCoordinate, Task<Dictionary<(int, int), List<Vector3>>>> zones = new();
    readonly object gatherLock = new();
    /// <summary>Zones kept at most; the least recently asked for goes first (engine choice).</summary>
    const int MaxZones = 16;
    /// <summary>How far a building's collision may reach from its placement (the navmesh gatherer's reach).</summary>
    const float Reach = 600;
    readonly ConcurrentDictionary<ZoneCoordinate, long> used = new();
    long clock;

    /// <summary>Heightmap samples along a block of the ground cache, and blocks kept at most (65 × 65 samples each, about 8 KB; a block is 1152 units across).</summary>
    const int BlockSamples = 64, MaxBlocks = 256;
    readonly TerrainHeightmap? map;
    readonly Dictionary<(int, int), HeightWindow> blocks = [];

    /// <param name="terrainHeight">The simulation's ground height, used only where the full heightmap cannot be opened: it is the coarse world grid away from the loaded area.</param>
    public RagdollGeometry(GameInstall install, GameDatabase db, WorldLevelData levels, Func<float, float, float> terrainHeight)
    {
        gatherer = new ZoneGeometryGatherer(install, db, levels, new CollisionCache(install)) { IncludeFoliage = false };
        terrain = terrainHeight;
        try { map = TerrainHeightmap.Open(install); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The terrain at full resolution, from blocks of the heightmap read as they are needed (the simulation's own height is the coarse grid, a sample in every 144 units, outside
    /// the area the game loaded at the start: a body would meet a ground metres off the one the character stands on and drawn terrain). Called from the simulation thread only.
    /// </summary>
    public float GroundHeight(float x, float z, float nearY)
    {
        if (map is null) return terrain(x, z);
        var (column, row) = WorldLayout.ToSample(x, z);
        int bx = (int)Math.Floor(column / BlockSamples), bz = (int)Math.Floor(row / BlockSamples);
        if (!blocks.TryGetValue((bx, bz), out var block))
        {
            if (blocks.Count >= MaxBlocks) blocks.Clear();
            blocks[(bx, bz)] = block = map.ReadWindow(bx * BlockSamples, bz * BlockSamples, BlockSamples + 1, BlockSamples + 1);
        }
        return block.TryHeightAt(x, z, out float height) ? height : terrain(x, z);
    }

    public bool Buildings(Vector3 min, Vector3 max, List<Vector3> triangles)
    {
        bool ready = true;
        int cx0 = Cell(min.X), cx1 = Cell(max.X - 1e-3f), cz0 = Cell(min.Z), cz1 = Cell(max.Z - 1e-3f);
        // A building's triangles are kept with the zone it is placed in and may reach into the next one: the zones within a building's reach of the box.
        var first = WorldLayout.ZoneOf(min.X - Reach, min.Z - Reach);
        var last = WorldLayout.ZoneOf(max.X + Reach, max.Z + Reach);
        for (int zy = first.Y; zy <= last.Y; zy++)
            for (int zx = first.X; zx <= last.X; zx++)
            {
                var zone = new ZoneCoordinate(zx, zy);
                if (!zone.IsInsideGrid) continue;
                used[zone] = Interlocked.Increment(ref clock);
                var task = zones.GetOrAdd(zone, z => Task.Run(() => Gather(z)));
                if (!task.IsCompletedSuccessfully) { ready &= !task.IsFaulted && task.IsCompleted; continue; }
                for (int x = cx0; x <= cx1; x++)
                    for (int z = cz0; z <= cz1; z++)
                        if (task.Result.TryGetValue((x, z), out var cell)) triangles.AddRange(cell);
            }
        Evict();
        return ready;
    }

    static int Cell(float v) => (int)MathF.Floor(v / RagdollSystem.TileSize);

    Dictionary<(int, int), List<Vector3>> Gather(ZoneCoordinate zone)
    {
        var all = new List<Vector3>();
        lock (gatherLock) gatherer.RagdollTriangles(zone, all);
        var cells = new Dictionary<(int, int), List<Vector3>>();
        for (int t = 0; t + 2 < all.Count; t += 3)
        {
            Vector3 a = all[t], b = all[t + 1], c = all[t + 2];
            var lo = Vector3.Min(a, Vector3.Min(b, c));
            var hi = Vector3.Max(a, Vector3.Max(b, c));
            for (int x = Cell(lo.X); x <= Cell(hi.X); x++)
                for (int z = Cell(lo.Z); z <= Cell(hi.Z); z++)
                {
                    if (!cells.TryGetValue((x, z), out var list)) cells[(x, z)] = list = [];
                    list.Add(a); list.Add(b); list.Add(c);
                }
        }
        return cells;
    }

    void Evict()
    {
        if (zones.Count <= MaxZones) return;
        foreach (var (zone, _) in used.OrderBy(u => u.Value).Take(zones.Count - MaxZones).ToList())
            if (zones.TryGetValue(zone, out var task) && task.IsCompleted && zones.TryRemove(zone, out _)) used.TryRemove(zone, out _);
    }

    public void Dispose()
    {
        foreach (var task in zones.Values) try { task.Wait(); } catch (AggregateException) { }
        map?.Dispose();
        lock (gatherLock) gatherer.Dispose();
    }
}
