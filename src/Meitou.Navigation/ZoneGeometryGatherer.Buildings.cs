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
                // Runtime placements replace town houses by global key, or append newly placed buildings.
                foreach (var b in BuildingPlacements.Overlay(buildingsByZone.GetValueOrDefault(z) ?? [], runtime?.In(z) ?? []))
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
            if (StateOf(b) is { } state) hash = Combine(hash, (uint)Stable(state.GetString("exterior layout name") + "|" + state.GetString("interior layout name")));
        }
        return hash;
    }

    void AddBuildings(ZoneGeometry g, uint? knownHash)
    {
        g.BuildingFirstTriangle = g.TriangleCount;
        foreach (var (b, record, position, destroyed) in NearBuildings(g.Zone, g.ZoneMin, g.ZoneMax, g.Margin))
        {
            g.Stats.Buildings++;
            var kind = InteriorOf(record);
            bool open = kind != InteriorKind.Real;
            bool upper = kind == InteriorKind.Blank && !record.GetBool("is wall gate") && !record.GetBool("links together");
            AddBuilding(g, record, b.Key, position, b.Rotation, destroyed, open: open, upper: upper);
            foreach (var (item, at, rotation) in LayoutObjects(b, record, position, destroyed))
                AddBuilding(g, item.Building, $"{b.Key}/{item.InstanceId}", at, rotation, destroyed: false, floor: item.Floor, open: open, upper: upper);
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

    /// <summary>The state record of a placement in its zone file (null when it has none).</summary>
    GameRecord? StateOf(BuildingPlacement b) => b.StateId is not null && levels.Zones.TryGetValue(b.Zone, out var zoneDb) ? zoneDb.Find(b.StateId) : null;

    /// <summary>
    /// The objects of a building's layouts placed in the world (docs/formats/zones.md, "Building layouts"): the signs and stalls of its exterior layout and the
    /// furniture of its interior layout, as the renderer lays them out. None for a destroyed building, whose layouts are not drawn either.
    /// </summary>
    IEnumerable<(LayoutItem Item, Vector3 Position, Quaternion Rotation)> LayoutObjects(BuildingPlacement b, GameRecord record, Vector3 position, bool destroyed)
    {
        if (destroyed || StateOf(b) is not { } state) yield break;
        foreach (bool exterior in (bool[])[true, false])
        {
            if (state.GetString(exterior ? "exterior layout name" : "interior layout name") is not { Length: > 0 } name || layouts.Find(record, name, exterior) is not { } layout) continue;
            foreach (var item in layouts.Items(layout))
            {
                var (at, rotation) = BuildingLayouts.Place(position, b.Rotation, item);
                yield return (item, at, rotation);
            }
        }
    }

    /// <summary>Shape groups of a building's upper-floor contents: stairs on floor 2 (17), floor-3 stairs (18) and furniture on floors 1..3 (20..22).</summary>
    public const uint UpperContentsMask = 0x760000;

    /// <param name="floor">For a layout object: the floor it stands on (its state's <c>floornum</c>), which takes the place of its parts' own <c>building floor</c>.</param>
    /// <param name="open">
    /// Exterior gather: the placed building (for a layout object, its building) has no real interior (<see cref="InteriorOf"/>), so every shape of a WALKABLE
    /// building is walkable, not only its floor-0 parts and furniture (docs/game/pathfinding.md, "Generator", building shapes; Observed: Heft's Shield Bridge,
    /// an `is stairs` part, was an obstacle, issue #141).
    /// </param>
    /// <param name="upper">Exterior gather: its interior is blank and it is no wall or gate, so its upper-floor contents (<see cref="UpperContentsMask"/>) are gathered too.</param>
    void AddBuilding(ZoneGeometry g, GameRecord building, string placementId, Vector3 position, Quaternion rotation, bool destroyed, InteriorContext? interior = null, int? floor = null, bool open = false, bool upper = false)
    {
        var node = NodeMatrix(building, position, rotation);

        // The `interior` list is part of the physical building as drawn (interior stairs: Storm House, docs/game/pathfinding.md "Interiors").
        var placed = WorldObjectLayout.Building(db, building, placementId, position, rotation, new BuildingState(destroyed) { WithInterior = !destroyed });
        var doors = building.GetReferences("doors").Select(r => r.TargetStringId).ToHashSet();
        foreach (var mesh in placed)
        {
            var part = mesh.Source;
            string path = CollisionPathOf(part, destroyed);
            if (path.Length == 0) continue;
            // Every part of a building placed from a `doors` list is a door, flagged or not (Observed: the level 4 and 5 gates' door parts lack `is door`).
            bool isDoor = part.GetBool("is door") || mesh.Owner.GetBool("is door") || doors.Contains(mesh.Owner.StringId);
            AddPart(g, mesh.Owner, part, path, node, isDoor, interiorMask: false, instanceId: placementId, interior, floor, open, upper);
        }
        // The interior mask part is created apart from the parts list; its collision is the indoor hull.
        // Only a shell carves with its hull (wall gates such as the Hub's gates and Heft's Ancient Lab Ramp have a mask over a passage, issue #140).
        if (interior is null && IsShellWithInterior(building))
        foreach (var r in building.GetReferences("interior mask"))
            if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BUILDING_PART } mask && mask.GetPath("xml collision") is { Length: > 0 } maskPath)
                AddPart(g, building, mask, maskPath, node, isDoor: false, interiorMask: true, instanceId: placementId);
    }

    void AddPart(ZoneGeometry g, GameRecord owner, GameRecord part, string path, Matrix4x4 node, bool isDoor, bool interiorMask, string instanceId, InteriorContext? interior = null, int? floorOverride = null, bool open = false, bool upper = false)
    {
        var prepared = collision.Get(path);
        if (prepared is null) return;
        g.Stats.PartsWithCollision++;
        var mode = (PathMode)owner.GetInt("path mode", (int)PathMode.Obstacle);
        int floor = floorOverride ?? Math.Clamp(part.GetInt("building floor"), 0, 3);
        int group;
        if (isDoor) group = 5;
        else if (interiorMask) group = 13;
        else if (mode == PathMode.Ignore || part.GetBool("passable")) group = 23 + floor;
        else if (part.GetBool("is unwalkable roof")) group = 27;
        else if (part.GetBool("is stairs")) group = 15 + floor;
        else if (owner.GetBool("is exterior furniture") || owner.GetBool("is interior furniture")) group = 19 + floor;
        else group = 9 + floor;

        int firstTriangle = g.TriangleCount, firstSolid = g.Solids.Count;
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
            if (((IncludedGroupMask | (upper ? UpperContentsMask : 0)) & (1u << group)) == 0) continue;
            g.Stats.Shapes++;
            bool walkable = mode == PathMode.Walkable && group != 27 && (open || group == 9 || group == 19);
            int shapeFirst = g.TriangleCount;
            AddShape(g, shape, node, walkable ? BuildingSlopeDegrees : 0, carve: CarveConvexObstacles && !walkable);
            if (group is 9 or 10 && g.TriangleCount > shapeFirst) g.FloorRanges.Add((shapeFirst, g.TriangleCount));
        }
        if (g.TrackParts && g.TriangleCount > firstTriangle) g.PartRanges.Add((owner.Name, part.Name, path, firstTriangle, g.TriangleCount));
        for (int i = firstSolid; i < g.Solids.Count; i++) g.Solids[i].Owner = $"{instanceId} {owner.Name} / {part.Name} (group {group}, mode {(int)mode})";
    }
    // ---- shape placement ----

    /// <summary>
    /// Places a prepared shape. <paramref name="walkableSlope"/> 0: a cutting obstacle (all its triangles are <see cref="NavArea.Null"/>);
    /// otherwise a triangle is walkable up to that slope. <paramref name="carve"/>: a convex obstacle also removes the floor inside its volume (<see cref="NavSolid"/>).
    /// </summary>
    /// <summary>A door painter reaches this far below its hull and is grown by <see cref="DoorPainterGrow"/> on each side (engine choice).</summary>
    const float DoorPainterDrop = 10, DoorPainterGrow = 3;

    /// <summary>
    /// How far below its hull an interior mask carver reaches (engine choice): the hull starts at the floor, and the ground 14..26 under a raised floor
    /// would otherwise stay as a crawlspace joined to the street (Observed: Blister Hill, residents walked under Old Storm Houses and Stephouses). The
    /// original's tiles have no polygon under these houses.
    /// </summary>
    const float CarverDrop = 60;

    /// <param name="meshSolid">A triangle mesh that encloses a volume (a map feature's rock, walkable foliage) also removes the floor inside it (<see cref="NavMeshSolid"/>).</param>
    void AddShape(ZoneGeometry g, PreparedShape shape, Matrix4x4 node, float walkableSlope, bool carve, bool meshSolid = false)
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
        // A convex obstacle is a solid too, not a prism over its footprint: a prism cut the ground under a leaning shape whose volume is in the air
        // (docs/game/pathfinding.md, "Solids"; Observed: Shark's Giant_BigWood, a tilted 2,000-unit box over a hemp farm, issue #155).
        if ((walkableSlope > 0 || carve) && shape.IsConvex && Solid(pts, shape.Indices, lo, hi) is { } solid)
        {
            g.Solids.Add(solid);
            g.Stats.Solids++;
        }
        if (meshSolid && !shape.IsConvex && MeshSolid(pts, shape.Indices, lo, hi) is { } mesh)
        {
            g.MeshSolids.Add(mesh);
            g.Stats.Solids++;
        }
    }

    /// <summary>The planes of a closed convex shape (its triangles' planes, turned away from its centre, duplicates merged); null for a flat one.</summary>
    static NavSolid? Solid(Vector3[] pts, int[] idx, Vector3 lo, Vector3 hi)
    {
        var centre = Vector3.Zero;
        foreach (var p in pts) centre += p;
        centre /= pts.Length;
        var planes = new List<Vector4>();
        for (int t = 0; t < idx.Length; t += 3)
        {
            var a = pts[idx[t]];
            var n = Vector3.Cross(pts[idx[t + 1]] - a, pts[idx[t + 2]] - a);
            float len = n.Length();
            if (len < 1e-6f) continue;
            n /= len;
            float d = -Vector3.Dot(n, a);
            if (Vector3.Dot(n, centre) + d > 0) { n = -n; d = -d; }
            if (Vector3.Dot(n, centre) + d > -0.5f) return null; // the centre on a face: no volume to speak of
            bool known = false;
            foreach (var q in planes)
                if (Vector3.Dot(n, new Vector3(q.X, q.Y, q.Z)) > 0.9999f && MathF.Abs(d - q.W) < 0.1f) { known = true; break; }
            if (!known) planes.Add(new Vector4(n, d));
        }
        return planes.Count < 4 ? null : new NavSolid([.. planes], new Vector2(lo.X, lo.Z), new Vector2(hi.X, hi.Z));
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
        target.Add(NavGeometry.Footprint(pts, yMin - (carver ? CarverDrop : DoorPainterDrop), yMax, carver ? 0 : DoorPainterGrow));
        if (carver) stats.Carvers++; else stats.Painters++;
    }
}

internal sealed partial class ZoneGeometryGatherer
{
    /// <summary>
    /// The collision triangles of the buildings placed in <paramref name="zone"/> and of the map features whose triangles lie in it (<see cref="AddRagdollFeatures"/>) (each placement once, in its own zone; its triangles may reach past the zone) that ragdolls land
    /// on and hit (docs/game/ragdolls.md "Collision groups"): walls and floors, stairs, furniture and roofs; not doors, interior masks or passable parts. Three points per
    /// triangle, world space.
    /// </summary>
    public void RagdollTriangles(ZoneCoordinate zone, List<Vector3> output)
    {
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var min = new Vector2((float)ox, (float)oz);
        foreach (var (b, record, position, destroyed) in NearBuildings(zone, min, min + new Vector2(WorldLayout.ZoneSize), 0))
        {
            if (b.Zone != zone) continue;
            AddRagdollBuilding(record, b.Key, position, b.Rotation, destroyed, output);
            foreach (var (item, at, rotation) in LayoutObjects(b, record, position, destroyed))
                AddRagdollBuilding(item.Building, $"{b.Key}/{item.InstanceId}", at, rotation, false, output);
        }
        AddRagdollFeatures(zone, output);
    }

    void AddRagdollBuilding(GameRecord building, string placementId, Vector3 position, Quaternion rotation, bool destroyed, List<Vector3> output)
    {
        var node = NodeMatrix(building, position, rotation);
        var doors = building.GetReferences("doors").Select(r => r.TargetStringId).ToHashSet();
        foreach (var mesh in WorldObjectLayout.Building(db, building, placementId, position, rotation, new BuildingState(destroyed)))
        {
            var part = mesh.Source;
            string path = CollisionPathOf(part, destroyed);
            if (path.Length == 0 || part.GetBool("is door") || mesh.Owner.GetBool("is door") || doors.Contains(mesh.Owner.StringId)) continue;
            // Groups 23..26: a building that is ignored by paths, or a passable part (ragdolls do not collide with them).
            if ((PathMode)mesh.Owner.GetInt("path mode", (int)PathMode.Obstacle) == PathMode.Ignore || part.GetBool("passable")) continue;
            if (collision.Get(path) is not { } prepared) continue;
            foreach (var shape in prepared.Shapes)
                foreach (int i in shape.Indices) output.Add(Vector3.Transform(shape.Vertices[i], node));
        }
    }
}
