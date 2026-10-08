using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Navigation;

internal sealed partial class ZoneGeometryGatherer
{
    /// <summary>How far (units) a building reaches past the zone box: placements this near are gathered (engine choice).</summary>
    const float BuildingReach = 600;

    /// <summary>Placed buildings near the zone box (the box grown by the margin and a building's reach), with their records and states.</summary>
    internal IEnumerable<ResolvedBuilding> NearBuildings(ZoneCoordinate zone, Vector2 zoneMin, Vector2 zoneMax, float margin)
    {
        var lo = zoneMin - new Vector2(margin + BuildingReach);
        var hi = zoneMax + new Vector2(margin + BuildingReach);
        Func<double, double, float> terrain = map.HeightAt;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var z = new ZoneCoordinate(zone.X + dx, zone.Y + dz);
                if (!buildingsByZone.TryGetValue(z, out var list)) continue;
                foreach (var b in list)
                {
                    if (b.Position.X < lo.X || b.Position.X > hi.X || b.Position.Z < lo.Y || b.Position.Z > hi.Y) continue;
                    if (BuildingPlacements.Resolve(b, db, levels, terrain) is { } resolved) yield return resolved;
                }
            }
    }

    /// <summary>
    /// Hash of the buildings that shape the zone's mesh (id, position, rotation, destroyed) in the style of the original's zone hash
    /// (docs/game/pathfinding.md, "Hash"): the cache key of a built mesh. Changes when a mod adds, moves or removes a building.
    /// </summary>
    public uint BuildingHash(ZoneCoordinate zone, float margin = DefaultMargin)
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

    void AddBuildings(ZoneGeometry g, uint? knownHash)
    {
        g.BuildingFirstTriangle = g.TriangleCount;
        foreach (var (b, record, position, destroyed) in NearBuildings(g.Zone, g.ZoneMin, g.ZoneMax, g.Margin))
        {
            g.Stats.Buildings++;
            AddBuilding(g, record, b.InstanceId, position, b.Rotation, destroyed);
        }
        g.BuildingLastTriangle = g.TriangleCount;
        g.BuildingHash = knownHash ?? BuildingHash(g.Zone, g.Margin);
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

    void AddBuilding(ZoneGeometry g, GameRecord building, string placementId, Vector3 position, Quaternion rotation, bool destroyed, InteriorContext? interior = null)
    {
        var node = NodeMatrix(building, position, rotation);

        var placed = WorldObjectLayout.Building(db, building, placementId, position, rotation, new BuildingState(destroyed));
        foreach (var mesh in placed)
        {
            var part = mesh.Source;
            string path = CollisionPathOf(part, destroyed);
            if (path.Length == 0) continue;
            bool isDoor = part.GetBool("is door") || mesh.Owner.GetBool("is door");
            AddPart(g, mesh.Owner, part, path, node, isDoor, interiorMask: false, instanceId: placementId, interior);
        }
        // The interior mask part is created apart from the parts list; its collision is the indoor hull.
        // Gateways (is gateway: walk underneath) are not shells with an interior: their hull must not close the passage (Observed: the Hub's gates).
        if (interior is null && !building.GetBool("is gateway"))
        foreach (var r in building.GetReferences("interior mask"))
            if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BUILDING_PART } mask && mask.GetPath("xml collision") is { Length: > 0 } maskPath)
                AddPart(g, building, mask, maskPath, node, isDoor: false, interiorMask: true, instanceId: placementId);
    }

    void AddPart(ZoneGeometry g, GameRecord owner, GameRecord part, string path, Matrix4x4 node, bool isDoor, bool interiorMask, string instanceId, InteriorContext? interior = null)
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
            if (interior is not null)
            {
                AddInteriorShape(g, owner, group, mode, shape, node, instanceId, interior);
                continue;
            }
            if (group == 13)
            {
                // Hull of an interior mask on a building that has one (the original: SHELL_WITH_INTERIOR buildings): cut the exterior mesh.
                AddVolume(g.Carvers, shape, node, g.Stats, carver: true);
                continue;
            }
            if (group == 5)
            {
                int before = g.Painters.Count;
                AddVolume(g.Painters, shape, node, g.Stats, carver: false);
                if (g.Painters.Count > before) g.Painters[^1].Owner = instanceId;
                AddDoorSeeds(g, owner, shape, node);
                continue;
            }
            if ((IncludedGroupMask & (1u << group)) == 0) continue;
            g.Stats.Shapes++;
            bool walkable = mode == PathMode.Walkable && group != 27 && (group == 9 || group == 19);
            AddShape(g, shape, node, walkable ? BuildingSlopeDegrees : 0, carve: CarveConvexObstacles && !walkable);
        }
    }
    // ---- shape placement ----

    /// <summary>
    /// Places a prepared shape. <paramref name="walkableSlope"/> 0: a cutting obstacle (all its triangles are <see cref="NavArea.Null"/>);
    /// otherwise a triangle is walkable up to that slope.
    /// </summary>
    /// <summary>A door painter reaches this far below its hull and is grown by <see cref="DoorPainterGrow"/> on each side (engine choice).</summary>
    const float DoorPainterDrop = 10, DoorPainterGrow = 3;

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
        if (carve && shape.IsConvex)
        {
            g.Carvers.Add(NavGeometry.Footprint(pts, lo.Y, hi.Y, 0));
            g.Stats.Carvers++;
        }
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
        // A door painter needs some thickness to catch cells; a door's hull is only a few units deep (engine choice).
        target.Add(NavGeometry.Footprint(pts, carver ? yMin : yMin - DoorPainterDrop, yMax, carver ? 0 : DoorPainterGrow));
        if (carver) stats.Carvers++; else stats.Painters++;
    }
}
