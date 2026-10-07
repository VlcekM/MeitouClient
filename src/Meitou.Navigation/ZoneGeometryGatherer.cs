using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>BUILDING <c>path mode</c> (FCS PathMode).</summary>
public enum PathMode { Ignore = 0, Projected = 1, Obstacle = 2, Walkable = 3 }

/// <summary>
/// Gathers the navmesh input of one zone from the game data as the original's generator does (docs/game/pathfinding.md, "Inputs"; collision files and
/// groups in docs/formats/collision.md): terrain triangles with the water clamp, building and foliage collision placed and filtered by collision group,
/// per-triangle walkable areas, carvers, door painters and seed points. One instance per thread (it owns a heightmap handle); the collision cache may be shared.
/// </summary>
public sealed class ZoneGeometryGatherer : IDisposable
{
    /// <summary>Collision groups the original's generator includes (mask 0x809de40): foliage 6 and 14, building parts 9..12, stairs 15 and 16, furniture 19, unwalkable roofs 27.</summary>
    public const uint IncludedGroupMask = 0x809de40;

    /// <summary>Terrain slope limit (degrees) and the limit for walkable building triangles.</summary>
    public const float TerrainSlopeDegrees = 40, BuildingSlopeDegrees = 60;

    /// <summary>Vertical half-extent of a foliage cutter box (docs: ±100 units).</summary>
    public const float CutterHalfHeight = 100;

    readonly GameInstall install;
    readonly GameDatabase db;
    readonly WorldLevelData levels;
    readonly CollisionCache collision;
    readonly TerrainHeightmap map;
    readonly Dictionary<ZoneCoordinate, List<BuildingPlacement>> buildingsByZone = [];
    readonly List<Vector3>[]? seedsByZone;
    FoliageWorld? foliage;

    public ZoneGeometryGatherer(GameInstall install, GameDatabase db, WorldLevelData levels, CollisionCache collision)
    {
        this.install = install;
        this.db = db;
        this.levels = levels;
        this.collision = collision;
        map = TerrainHeightmap.Open(install);
        foreach (var b in levels.Buildings())
        {
            if (!buildingsByZone.TryGetValue(b.Zone, out var list)) buildingsByZone[b.Zone] = list = [];
            list.Add(b);
        }
        seedsByZone = NavSeeds.Load(install);
    }

    /// <summary>Whether foliage collision is gathered (it needs the biome data and costs a foliage placement per zone).</summary>
    public bool IncludeFoliage { get; set; } = true;

    /// <summary>Add a prism carver around every convex obstacle, so the ground inside a closed building volume is removed too (the original's cutting materials).</summary>
    public bool CarveConvexObstacles { get; set; } = true;

    public void Dispose()
    {
        map.Dispose();
        foliage?.Dispose();
    }

    public float TerrainHeight(double x, double z) => map.HeightAt(x, z);

    /// <summary>The seed points of a zone from seeds.def (docs: absolute positions, Y −99 when none was authored).</summary>
    public IReadOnlyList<Vector3> FileSeeds(ZoneCoordinate zone) =>
        seedsByZone is not null && zone.IsInsideGrid ? seedsByZone[zone.Y * WorldLayout.ZoneCount + zone.X] : [];

    public ZoneGeometry Gather(ZoneCoordinate zone, float margin = 72)
    {
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var g = new ZoneGeometry
        {
            Zone = zone,
            ZoneMin = new((float)ox, (float)oz),
            ZoneMax = new((float)ox + WorldLayout.ZoneSize, (float)oz + WorldLayout.ZoneSize),
            Margin = margin,
        };
        AddTerrain(g);
        AddBuildings(g);
        if (IncludeFoliage) AddFoliage(g);
        AddSeeds(g);
        g.Stats.MissingFiles = collision.Missing;
        return g;
    }

    // ---- terrain ----

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

    // ---- buildings ----


    /// <summary>Placed buildings near the zone box (the box grown by the margin and a building's reach), with their records and states.</summary>
    IEnumerable<(BuildingPlacement Placement, GameRecord Record, Vector3 Position, bool Destroyed)> NearBuildings(ZoneCoordinate zone, Vector2 zoneMin, Vector2 zoneMax, float margin)
    {
        var lo = zoneMin - new Vector2(margin + 600);
        var hi = zoneMax + new Vector2(margin + 600);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var z = new ZoneCoordinate(zone.X + dx, zone.Y + dz);
                if (!buildingsByZone.TryGetValue(z, out var list)) continue;
                foreach (var b in list)
                {
                    if (b.Position == Vector3.Zero) continue;
                    if (b.Position.X < lo.X || b.Position.X > hi.X || b.Position.Z < lo.Y || b.Position.Z > hi.Y) continue;
                    if (db.Find(b.BuildingId) is not { } record) continue;
                    float y = b.WorldY ?? map.HeightAt(b.Position.X, b.Position.Z) + b.Position.Y;
                    var state = b.StateId is not null && levels.Zones.TryGetValue(b.Zone, out var zoneDb) ? zoneDb.Find(b.StateId) : null;
                    yield return (b, record, new Vector3(b.Position.X, y, b.Position.Z), state?.GetBool("destroyed") ?? false);
                }
            }
    }

    /// <summary>
    /// Hash of the buildings that shape the zone's mesh (id, position, rotation, destroyed) in the style of the original's zone hash
    /// (docs/game/pathfinding.md, "Hash"): the cache key of a built mesh. Changes when a mod adds, moves or removes a building.
    /// </summary>
    public uint BuildingHash(ZoneCoordinate zone, float margin = 72)
    {
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var min = new Vector2((float)ox, (float)oz);
        uint hash = 0x9e3779b9;
        foreach (var (b, _, _, destroyed) in NearBuildings(zone, min, min + new Vector2(WorldLayout.ZoneSize), margin))
        {
            hash = Combine(hash, (uint)(long)b.Position.X ^ 0xdeadbeef);
            hash = Combine(hash, (uint)(long)b.Position.Z ^ 0xdeadbeef);
            hash = Combine(hash, (uint)Stable(b.BuildingId));
            hash = Combine(hash, (uint)BitConverter.SingleToInt32Bits(b.Rotation.W));
            hash = Combine(hash, (uint)BitConverter.SingleToInt32Bits(b.Rotation.Y));
            hash = Combine(hash, destroyed ? 1u : 0u);
        }
        return hash;
    }

    void AddBuildings(ZoneGeometry g)
    {
        foreach (var (b, record, position, destroyed) in NearBuildings(g.Zone, g.ZoneMin, g.ZoneMax, g.Margin))
        {
            g.Stats.Buildings++;
            AddBuilding(g, record, b.InstanceId, position, b.Rotation, destroyed);
        }
        g.BuildingHash = BuildingHash(g.Zone, g.Margin);
    }

    static uint Combine(uint h, uint v) => h ^ (v + 0x9e3779b9 + (h << 6) + (h >> 2));

    static int Stable(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }

    void AddBuilding(ZoneGeometry g, GameRecord building, string placementId, Vector3 position, Quaternion rotation, bool destroyed)
    {
        float scale = building.GetFloat("scale", 1);
        if (scale <= 0) scale = 1;
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        var node = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(position);

        var placed = WorldObjectLayout.Building(db, building, placementId, position, rotation, new BuildingState(destroyed));
        foreach (var mesh in placed)
        {
            var part = mesh.Source;
            string path = destroyed && part.GetPath("destroyed collision") is { Length: > 0 } dc ? dc : part.GetPath("xml collision");
            if (path.Length == 0) continue;
            bool isDoor = part.GetBool("is door") || mesh.Owner.GetBool("is door");
            AddPart(g, mesh.Owner, part, path, node, isDoor, interiorMask: false);
        }
        // The interior mask part is created apart from the parts list; its collision is the indoor hull.
        // Gateways (is gateway: walk underneath) are not shells with an interior: their hull must not close the passage (Observed: the Hub's gates).
        if (!building.GetBool("is gateway"))
        foreach (var r in building.GetReferences("interior mask"))
            if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BUILDING_PART } mask && mask.GetPath("xml collision") is { Length: > 0 } maskPath)
                AddPart(g, building, mask, maskPath, node, isDoor: false, interiorMask: true);
    }

    void AddPart(ZoneGeometry g, GameRecord owner, GameRecord part, string path, Matrix4x4 node, bool isDoor, bool interiorMask)
    {
        var prepared = collision.Get(path);
        if (prepared is null) return;
        g.Stats.PartsWithCollision++;
        var mode = (PathMode)owner.GetInt("path mode", (int)PathMode.Obstacle);
        int floor = Math.Clamp(part.GetInt("building floor"), 0, 3);
        int group;
        if (isDoor) group = 5;
        else if (interiorMask) group = 13;
        else if (mode == PathMode.Ignore || part.GetBool("passable")) group = 23 + floor;
        else if (part.GetBool("is unwalkable roof")) group = 27;
        else if (part.GetBool("is stairs")) group = 15 + floor;
        else if (owner.GetBool("is exterior furniture") || owner.GetBool("is interior furniture")) group = 19 + floor;
        else group = 9 + floor;

        foreach (var shape in prepared.Shapes)
        {
            if (group == 13)
            {
                // Hull of an interior mask on a building that has one (the original: SHELL_WITH_INTERIOR buildings): cut the exterior mesh.
                AddVolume(g.Carvers, shape, node, g.Stats, carver: true);
                continue;
            }
            if (group == 5)
            {
                AddVolume(g.Painters, shape, node, g.Stats, carver: false);
                AddDoorSeeds(g, owner, shape, node);
                continue;
            }
            if ((IncludedGroupMask & (1u << group)) == 0) continue;
            g.Stats.Shapes++;
            bool walkable = mode == PathMode.Walkable && group != 27 && (group == 9 || group == 19);
            AddShape(g, shape, node, walkable ? BuildingSlopeDegrees : 0, carve: CarveConvexObstacles && !walkable);
        }
    }

    /// <summary>
    /// The seed points either side of a door: the door shape's centre moved along its local axis <c>door navmesh axis</c> past its faces, then dropped to the ground
    /// (docs: the original takes the outer marker as an exterior seed and the inner one as the interior's). Only box doors are handled.
    /// </summary>
    void AddDoorSeeds(ZoneGeometry g, GameRecord door, PreparedShape shape, Matrix4x4 node)
    {
        if (shape.Kind != Meitou.Data.Physics.CollisionShapeKind.Box || shape.Vertices.Length != 8) return;
        var v = shape.Vertices.Select(p => Vector3.Transform(p, node)).ToArray();
        int axis = Math.Clamp(door.GetInt("door navmesh axis"), 0, 2);
        var along = axis switch { 0 => v[1] - v[0], 1 => v[2] - v[0], _ => v[4] - v[0] };
        float half = along.Length() / 2;
        if (half < 1e-3f) return;
        var dir = along / (2 * half);
        var centre = Vector3.Zero;
        foreach (var p in v) centre += p / 8;
        foreach (float side in new[] { -1f, 1f })
        {
            var at = centre + dir * side * (half + DoorSeedOffset);
            if (at.X < g.ZoneMin.X || at.X > g.ZoneMax.X || at.Z < g.ZoneMin.Y || at.Z > g.ZoneMax.Y) continue;
            g.Seeds.Add(new Vector3(at.X, Math.Max(map.HeightAt(at.X, at.Z), WorldWater.Height), at.Z));
            g.Stats.DoorSeeds++;
        }
    }

    /// <summary>How far past a door's face its seed markers sit (engine choice).</summary>
    public const float DoorSeedOffset = 10;

    // ---- shape placement ----

    /// <summary>
    /// Places a prepared shape. <paramref name="walkableSlope"/> 0: a cutting obstacle (all its triangles are <see cref="NavArea.Null"/>);
    /// otherwise a triangle is walkable up to that slope.
    /// </summary>
    void AddShape(ZoneGeometry g, PreparedShape shape, Matrix4x4 node, float walkableSlope, bool carve)
    {
        var pts = new Vector3[shape.Vertices.Length];
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int i = 0; i < pts.Length; i++)
        {
            pts[i] = Vector3.Transform(shape.Vertices[i], node);
            lo = Vector3.Min(lo, pts[i]);
            hi = Vector3.Max(hi, pts[i]);
        }
        if (hi.X < g.ZoneMin.X - g.Margin || lo.X > g.ZoneMax.X + g.Margin || hi.Z < g.ZoneMin.Y - g.Margin || lo.Z > g.ZoneMax.Y + g.Margin) return;
        EmitTriangles(g, pts, shape.Indices, walkableSlope);
        if (carve && shape.IsConvex) g.Carvers.Add(Footprint(pts, lo.Y, hi.Y, 0));
        if (carve && shape.IsConvex) g.Stats.Carvers++;
    }

    void EmitTriangles(ZoneGeometry g, Vector3[] pts, int[] idx, float walkableSlope)
    {
        int baseIndex = g.Vertices.Count / 3;
        foreach (var p in pts) g.AddVertex(p);
        float cos = MathF.Cos(walkableSlope * MathF.PI / 180);
        for (int t = 0; t < idx.Length; t += 3)
        {
            var a = pts[idx[t]];
            var n = Vector3.Cross(pts[idx[t + 1]] - a, pts[idx[t + 2]] - a);
            float len = n.Length();
            byte area = NavArea.Null;
            if (walkableSlope > 0 && len > 1e-9f && n.Y / len >= cos) area = NavArea.Ground;
            if (area == NavArea.Null) g.Stats.CuttingTriangles++; else g.Stats.WalkableTriangles++;
            g.AddTriangle(baseIndex + idx[t], baseIndex + idx[t + 1], baseIndex + idx[t + 2], area);
        }
    }

    void AddVolume(List<NavVolume> target, PreparedShape shape, Matrix4x4 node, GatherStats stats, bool carver)
    {
        var pts = new Vector3[shape.Vertices.Length];
        float yMin = float.MaxValue, yMax = float.MinValue;
        for (int i = 0; i < pts.Length; i++)
        {
            pts[i] = Vector3.Transform(shape.Vertices[i], node);
            yMin = Math.Min(yMin, pts[i].Y);
            yMax = Math.Max(yMax, pts[i].Y);
        }
        // A door painter needs some thickness to catch cells; a door's hull is only a few units deep.
        target.Add(Footprint(pts, carver ? yMin : yMin - 10, yMax, carver ? 0 : 3));
        if (carver) stats.Carvers++; else stats.Painters++;
    }

    /// <summary>The convex footprint of the points on the XZ plane (grown by <paramref name="grow"/> on each side), counter-clockwise from above.</summary>
    public static NavVolume Footprint(IReadOnlyList<Vector3> points, float yMin, float yMax, float grow)
    {
        var list = new List<Vector2>(points.Count * (grow > 0 ? 4 : 1));
        foreach (var p in points)
        {
            if (grow > 0)
            {
                list.Add(new(p.X - grow, p.Z - grow)); list.Add(new(p.X + grow, p.Z - grow));
                list.Add(new(p.X - grow, p.Z + grow)); list.Add(new(p.X + grow, p.Z + grow));
            }
            else list.Add(new(p.X, p.Z));
        }
        return new NavVolume(ConvexHull2D(list), yMin, yMax);
    }

    /// <summary>Andrew's monotone chain; counter-clockwise on (X, Z) with X right and Z up in the plane's own axes.</summary>
    public static Vector2[] ConvexHull2D(List<Vector2> pts)
    {
        pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        if (pts.Count < 3) return [.. pts];
        var hull = new List<Vector2>();
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], pts[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(pts[i]);
        }
        hull.RemoveAt(hull.Count - 1);
        return [.. hull];
    }

    static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    // ---- foliage ----

    void AddFoliage(ZoneGeometry g)
    {
        foliage ??= new FoliageWorld(install, db, levels);
        float margin = g.Margin;
        // Own zone plus the eight neighbours, so objects across the border that reach into the margin are there too.
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var z = new ZoneCoordinate(g.Zone.X + dx, g.Zone.Y + dz);
                if (!z.IsInsideGrid) continue;
                var fz = foliage.Zone(z);
                foreach (var inst in fz.Instances)
                {
                    var p = inst.Position;
                    if (p.X < g.ZoneMin.X - margin - 200 || p.X > g.ZoneMax.X + margin + 200 || p.Z < g.ZoneMin.Y - margin - 200 || p.Z > g.ZoneMax.Y + margin + 200) continue;
                    var rec = inst.Mesh.Record;
                    string path = rec.GetPath("collision");
                    float cutter = rec.GetFloat("navmesh cutter");
                    if (path.Length == 0)
                    {
                        if (cutter > 0)
                        {
                            g.Stats.FoliageCutters++;
                            g.Carvers.Add(new NavVolume([new(p.X - cutter, p.Z - cutter), new(p.X + cutter, p.Z - cutter), new(p.X + cutter, p.Z + cutter), new(p.X - cutter, p.Z + cutter)],
                                p.Y - CutterHalfHeight, p.Y + CutterHalfHeight));
                        }
                        continue;
                    }
                    if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                    var prepared = collision.Get(path);
                    if (prepared is null) continue;
                    g.Stats.FoliageInstances++;
                    bool walkable = rec.GetBool("walkable");
                    foreach (var shape in prepared.Shapes)
                    {
                        g.Stats.FoliageShapes++;
                        AddShape(g, shape, inst.Transform, walkable ? TerrainSlopeDegrees : 0, carve: CarveConvexObstacles && !walkable);
                    }
                }
            }
    }

    // ---- seeds ----

    void AddSeeds(ZoneGeometry g)
    {
        float water = WorldWater.Height;
        var fileSeeds = FileSeeds(g.Zone);
        foreach (var s in fileSeeds)
        {
            float y = s.Y < -50 ? Math.Max(map.HeightAt(s.X, s.Z), water) : s.Y;
            g.Seeds.Add(new Vector3(s.X, y, s.Z));
        }
        if (fileSeeds.Count == 0)
        {
            // A zone with no seeds.def points gets a 3 x 3 grid of ground seeds.
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 3; i++)
                {
                    float x = g.ZoneMin.X + WorldLayout.ZoneSize * (2 * i + 1) / 6f, z = g.ZoneMin.Y + WorldLayout.ZoneSize * (2 * j + 1) / 6f;
                    g.Seeds.Add(new Vector3(x, Math.Max(map.HeightAt(x, z), water), z));
                }
        }
    }
}

/// <summary><c>navtiles/seeds.def</c>: float3 records, absolute positions (Kenshi units), bucketed by zone.</summary>
public static class NavSeeds
{
    public const string RelativePath = "newland/land/navtiles/seeds.def";

    public static List<Vector3>[]? Load(GameInstall install)
    {
        var path = Path.Combine(install.DataDirectory, RelativePath);
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var zones = new List<Vector3>[WorldLayout.ZoneCount * WorldLayout.ZoneCount];
        for (int i = 0; i < zones.Length; i++) zones[i] = [];
        for (int o = 0; o + 12 <= bytes.Length; o += 12)
        {
            var p = new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8));
            var z = WorldLayout.ZoneOf(p.X, p.Z);
            if (z.IsInsideGrid) zones[z.Y * WorldLayout.ZoneCount + z.X].Add(p);
        }
        return zones;
    }
}
