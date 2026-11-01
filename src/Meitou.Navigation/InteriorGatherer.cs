using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>A building with an interior to build in <paramref name="Zone"/> (its own zone, or a neighbour its interior reaches over the border): its placement and record.</summary>
internal sealed record InteriorSite(BuildingPlacement Placement, GameRecord Record, Vector3 Position, bool Destroyed, ZoneCoordinate Zone);

/// <summary>How a building's shapes are taken for its interior mesh (docs/game/pathfinding.md, "Interiors").</summary>
sealed record InteriorContext(string InstanceId, bool Destroyed, uint Mask, bool Own);

internal sealed partial class ZoneGeometryGatherer
{
    /// <summary>Shape groups of an interior job: 9..13, 15..22 and 27; for a destroyed building only 9, 10, 13 and 19 (FUN_1403c79b0).</summary>
    public const uint InteriorGroupMask = 0x87fbe00, DestroyedInteriorGroupMask = 0x82600;

    /// <summary>How far outside the hull the clipping slabs reach (units); anything of the building beyond is cut away.</summary>
    const float SlabReach = 400;

    /// <summary>A building's <c>scale</c> (a missing or non-positive one is 1).</summary>
    static float BuildingScale(GameRecord building)
    {
        float scale = building.GetFloat("scale", 1);
        return scale <= 0 ? 1 : scale;
    }

    /// <summary>The building node's transform: its scale, then its (normalised) rotation, then its position.</summary>
    static Matrix4x4 NodeMatrix(GameRecord building, Vector3 position, Quaternion rotation) =>
        WorldObjectLayout.InstanceTransform(position, rotation, new Vector3(BuildingScale(building)));

    /// <summary>A part's collision file: the destroyed one for a destroyed building when it has one, else the intact one (empty when none).</summary>
    static string CollisionPathOf(GameRecord part, bool destroyed) =>
        destroyed && part.GetPath("destroyed collision") is { Length: > 0 } dc ? dc : part.GetPath("xml collision");

    /// <summary>
    /// The buildings whose interior reaches into a zone: shells with an interior (<see cref="IsShellWithInterior"/>), with the mask's box over the zone's. A building over a zone border is built in each zone it
    /// reaches, clipped at the border (<see cref="GatherInterior"/>).
    /// </summary>
    public IReadOnlyList<InteriorSite> InteriorSites(ZoneCoordinate zone)
    {
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var min = new Vector2((float)ox, (float)oz);
        var max = min + new Vector2(WorldLayout.ZoneSize);
        var list = new List<InteriorSite>();
        foreach (var (b, record, position, destroyed) in NearBuildings(zone, min, max, 0))
        {
            if (!IsShellWithInterior(record)) continue;
            var points = MaskPoints(record, position, b.Rotation);
            if (points.Count < 4) continue;
            if (points.Max(p => p.X) < min.X || points.Min(p => p.X) >= max.X || points.Max(p => p.Z) < min.Y || points.Min(p => p.Z) >= max.Y) continue;
            list.Add(new InteriorSite(b, record, position, destroyed, zone));
        }
        return list;
    }

    /// <summary>
    /// Whether a building is a shell with an interior: its interior mask carves the exterior mesh and its inside is built apart (docs/game/pathfinding.md,
    /// "Generator", carvers and interiors). The original's rule: its <c>interior</c> list is real (<see cref="InteriorOf"/>), it is a plain building (class
    /// SHELL_WITH_INTERIOR; the interior job leaves out only walls and gates, and no record with a real interior is one), and it has an interior mask with
    /// collision, the hull both use.
    /// </summary>
    public bool IsShellWithInterior(GameRecord building) =>
        InteriorOf(building) == InteriorKind.Real && IsPlainBuilding(building) && MaskParts(building).Any();

    /// <summary>What a building's <c>interior</c> list makes of it (docs/game/pathfinding.md, "Generator", building shapes).</summary>
    public enum InteriorKind { None, Blank, Real }

    /// <summary>
    /// None without an <c>interior</c> list; Blank when the list's first part has no <c>phs or mesh</c> and no <c>parts</c> (the "blank interior" parts of
    /// walls, gates, the Ancient Factory, Cannibal Shack); Real otherwise.
    /// </summary>
    public InteriorKind InteriorOf(GameRecord building)
    {
        var list = building.GetReferences("interior");
        if (list.Count == 0) return InteriorKind.None;
        return db.Find(list[0].TargetStringId) is { } first
            && (first.GetPath("phs or mesh").Length > 0 || first.GetString("phs or mesh").Length > 0 || first.GetReferences("parts").Count > 0)
            ? InteriorKind.Real : InteriorKind.Blank;
    }

    /// <summary>
    /// Whether the original makes the building a plain <c>Building</c> (class 0, which an <c>interior</c> list turns into SHELL_WITH_INTERIOR): not a wall gate
    /// (a gateway), not <c>links together</c> (a wall), no <c>farm data</c>, and its <c>functionality</c>'s <c>function</c> absent, BF_ANY or BF_FLUFF.
    /// </summary>
    bool IsPlainBuilding(GameRecord building)
    {
        if (building.GetBool("is wall gate") || building.GetBool("links together") || building.GetReferences("farm data").Count > 0) return false;
        var functionality = building.GetReferences("functionality");
        if (functionality.Count == 0) return true;
        int function = db.Find(functionality[0].TargetStringId)?.GetInt("function") ?? 0;
        return function is 0 or 18;
    }

    /// <summary>The world-space vertices of a building's interior mask shapes.</summary>
    List<Vector3> MaskPoints(GameRecord building, Vector3 position, Quaternion rotation)
    {
        var node = NodeMatrix(building, position, rotation);
        var points = new List<Vector3>();
        foreach (var (_, path) in MaskParts(building))
        {
            if (collision.Get(path) is not { } prepared) continue;
            foreach (var shape in prepared.Shapes)
                foreach (var v in shape.Vertices) points.Add(Vector3.Transform(v, node));
        }
        return points;
    }

    IEnumerable<(GameRecord Part, string Path)> MaskParts(GameRecord building)
    {
        foreach (var r in building.GetReferences("interior mask"))
            if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BUILDING_PART } mask && mask.GetPath("xml collision") is { Length: > 0 } path)
                yield return (mask, path);
    }

    /// <summary>
    /// The input of one building's interior mesh in <see cref="InteriorSite.Zone"/>: its own shapes and its furniture's, the door painters, the inner door
    /// markers as seeds, and the complement of the interior hull as carvers (the hull's inverted volume: everything outside it is removed). The box is a
    /// square of whole tiles; where the hull crosses the zone's border the box ends on the border line, so the part beyond is the neighbour zone's and the
    /// two meet like the exteriors do (border links, <see cref="ZoneKeptClosure"/>). Null when the building has no usable hull in the zone.
    /// </summary>
    /// <param name="ownOnly">Debug: only the building's own shapes, no furniture (the debug tool's <c>--own-only</c>).</param>
    /// <param name="trackParts">Debug: record each part's triangle range (<see cref="ZoneGeometry.PartRanges"/>).</param>
    public ZoneGeometry? GatherInterior(InteriorSite site, float tileWorld, float pad = 12, float agentHeight = 18, bool ownOnly = false, bool trackParts = false)
    {
        var node = NodeMatrix(site.Record, site.Position, site.Placement.Rotation);
        var points = MaskPoints(site.Record, site.Position, site.Placement.Rotation);
        if (points.Count < 4) return null;
        float yMin = points.Min(p => p.Y), yMax = points.Max(p => p.Y);
        var hull = NavGeometry.Footprint(points, yMin, yMax, 0);
        if (hull.Polygon.Length < 3) return null;

        var lo = new Vector2(hull.Polygon.Min(p => p.X), hull.Polygon.Min(p => p.Y)) - new Vector2(pad);
        var hi = new Vector2(hull.Polygon.Max(p => p.X), hull.Polygon.Max(p => p.Y)) + new Vector2(pad);
        var (zx, zz) = WorldLayout.ZoneOrigin(site.Zone);
        var zoneMin = new Vector2((float)zx, (float)zz);
        var zoneMax = zoneMin + new Vector2(WorldLayout.ZoneSize);
        bool crossLowX = lo.X < zoneMin.X, crossLowZ = lo.Y < zoneMin.Y, crossHighX = hi.X > zoneMax.X, crossHighZ = hi.Y > zoneMax.Y;
        lo = Vector2.Max(lo, zoneMin);
        hi = Vector2.Min(hi, zoneMax);
        if (hi.X <= lo.X || hi.Y <= lo.Y) return null;
        float side = MathF.Ceiling(MathF.Max(hi.X - lo.X, hi.Y - lo.Y) / tileWorld) * tileWorld;
        // Over the high border the box is moved back to end on it (over the low one it starts on it already).
        var boxMin = new Vector2(crossHighX && !crossLowX ? hi.X - side : lo.X, crossHighZ && !crossLowZ ? hi.Y - side : lo.Y);
        // A destroyed building's "destroyed boundary" (fcs.def: navmesh cutter for a destroyed interior) is a ring of walls round the ruin's floor, its doorway
        // and steps; cut halfway up, it outlines the floor.
        var boundary = site.Destroyed && site.Record.GetPath("destroyed boundary") is { Length: > 0 } boundaryPath ? collision.Get(boundaryPath) : null;
        List<(Vector2 A, Vector2 B)>? outline = null;
        if (boundary is not null)
        {
            outline = [];
            foreach (var shape in boundary.Shapes)
            {
                var pts = shape.Vertices.Select(v => Vector3.Transform(v, node)).ToArray();
                outline.AddRange(NavGeometry.Slice(pts, shape.Indices, (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2));
            }
            if (outline.Count < 3) outline = null;
        }
        var g = new ZoneGeometry
        {
            Zone = site.Zone, ZoneMin = boxMin, ZoneMax = boxMin + new Vector2(side), Margin = 8, InteriorHull = hull, InteriorOf = site.Placement.Key, TrackParts = ownOnly || trackParts,
            ClippedAtBorder = crossLowX || crossLowZ || crossHighX || crossHighZ, InteriorDestroyed = site.Destroyed, DestroyedOutline = outline,
            InteriorDoorless = site.Destroyed || !site.Record.GetReferences("doors").Any(),
        };

        uint mask = site.Destroyed ? DestroyedInteriorGroupMask : InteriorGroupMask;
        AddBuilding(g, site.Record, site.Placement.Key, site.Position, site.Placement.Rotation, site.Destroyed, new InteriorContext(site.Placement.Key, site.Destroyed, mask, Own: true));
        // Its layouts' furniture: walkable pieces give floor, the rest cut. The first shopkeeper or general node is the seed when no door's is (SeedPruner).
        foreach (var (item, at, rotation) in ownOnly ? [] : LayoutObjects(site.Placement, site.Record, site.Position, site.Destroyed))
        {
            if (g.NodeSeeds.Count == 0 && item.Building.GetBool("is node") && item.Building.GetInt("node type") is 0 or 1) g.NodeSeeds.Add(at);
            AddBuilding(g, item.Building, $"{site.Placement.Key}/{item.InstanceId}", at, rotation, destroyed: false, new InteriorContext(site.Placement.Key, false, mask, Own: false), item.Floor);
        }

        // Other buildings standing inside the hull (furniture placed in the zone file): walkable ones give floor, the rest cut.
        var (ox, oz) = WorldLayout.ZoneOrigin(site.Placement.Zone);
        var zmin = new Vector2((float)ox, (float)oz);
        foreach (var (b, record, position, destroyed) in ownOnly ? [] : NearBuildings(site.Placement.Zone, zmin, zmin + new Vector2(WorldLayout.ZoneSize), 0))
        {
            if (b.Key == site.Placement.Key || position.Y < yMin - 30 || position.Y > yMax + 30) continue;
            if (!NavGeometry.InsideConvex(hull.Polygon, new Vector2(position.X, position.Z), 5)) continue;
            AddBuilding(g, record, b.Key, position, b.Rotation, destroyed, new InteriorContext(site.Placement.Key, destroyed, mask, Own: false));
        }

        // Its walls cut the interior too.
        if (boundary is not null)
            foreach (var shape in boundary.Shapes) AddShape(g, shape, node, 0, carve: false);

        AddOutsideCarvers(g, hull.Polygon);

        // The inner door markers were added without a height: put them on the floor.
        for (int i = 0; i < g.Seeds.Count; i++)
        {
            var s = g.Seeds[i];
            if (!float.IsNaN(s.Y)) continue;
            float bottom = yMin - 10;
            float height = yMin;
            // A raised platform may contain several upward faces. Recast discards a lower one with too little
            // headroom; seeding it would prune the room above instead of preserving its walkable floor.
            while (RayDown(g, s.X, s.Z, highest: false, bottom, 0, g.TriangleCount, out float y) && y <= yMax)
            {
                if (!RayDown(g, s.X, s.Z, highest: false, y + 0.1f, 0, g.TriangleCount, out float ceiling, anyArea: true) || ceiling - y >= agentHeight)
                {
                    height = y;
                    break;
                }
                bottom = y + 0.1f;
            }
            g.Seeds[i] = new Vector3(s.X, height, s.Z);
        }
        // No door and no node: the original drops a ray from above the building's origin onto its floor-0 part.
        if (g.Seeds.Count == 0 && g.NodeSeeds.Count == 0 && OriginRaySeed(g, site.Position, site.Placement.Rotation, out var originSeed))
            g.NodeSeeds.Add(originSeed);
        return g;
    }

    /// <summary>The inverted hull of an interior: one slab beyond every edge of the convex polygon, together removing everything outside it.</summary>
    public static void AddOutsideCarvers(ZoneGeometry g, Vector2[] poly)
    {
        float orientation = NavGeometry.SignedArea(poly) >= 0 ? 1 : -1;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            var u = b - a;
            float len = u.Length();
            if (len < 1e-3f) continue;
            u /= len;
            var outward = new Vector2(u.Y, -u.X) * orientation;
            g.Carvers.Add(new NavVolume([a - u * SlabReach, b + u * SlabReach, b + u * SlabReach + outward * SlabReach, a - u * SlabReach + outward * SlabReach], -100000, 100000));
        }
        g.Stats.Carvers += poly.Length;
    }

    void AddInteriorShape(ZoneGeometry g, GameRecord owner, int group, PathMode mode, PreparedShape shape, Matrix4x4 node, string instanceId, InteriorContext ctx)
    {
        if (group == 13) return; // the hull itself is the clipping volume
        if (group == 5)
        {
            if (!ctx.Own) return;
            int before = g.Painters.Count;
            AddVolume(g.Painters, shape, node, g.Stats, carver: false);
            if (g.Painters.Count > before)
            {
                g.Painters[^1] = ReachIntoHull(g, g.Painters[^1], owner, shape, node);
                g.Painters[^1].Owner = instanceId;
            }
            AddInnerDoorSeed(g, owner, shape, node);
            return;
        }
        if ((ctx.Mask & (1u << group)) == 0) return;
        g.Stats.Shapes++;
        bool walkable = group != 27 && (ctx.Own || mode == PathMode.Walkable);
        int first = g.TriangleCount;
        AddShape(g, shape, node, walkable ? BuildingSlopeDegrees : 0, carve: false);
        if (group == 9 && g.TriangleCount > first) g.FloorRanges.Add((first, g.TriangleCount));
    }

    /// <summary>How far above the building's origin, along its up axis, the original's last-resort interior seed ray starts (FUN_1403cbfa0).</summary>
    const float OriginRayRise = 1000;

    /// <summary>
    /// The original's interior seed when the building has no door and no shopkeeper or general node (a ruin, whose doors are removed, or a doorless interior):
    /// a ray from <see cref="OriginRayRise"/> above the building's origin along its down axis, the nearest hit on a floor-0 part (group 9) is the seed, raised
    /// to the water level when under it (FUN_1403cbfa0; docs/game/pathfinding.md "Interior seeds"). False when the ray hits nothing.
    /// </summary>
    static bool OriginRaySeed(ZoneGeometry g, Vector3 position, Quaternion rotation, out Vector3 seed)
    {
        var down = Vector3.Transform(-Vector3.UnitY, rotation);
        var start = position - down * OriginRayRise;
        seed = default;
        float best = float.MaxValue;
        var v = g.Vertices;
        foreach (var (first, last) in g.FloorRanges)
            for (int t = first; t < last; t++)
            {
                int ia = g.Indices[t * 3] * 3, ib = g.Indices[t * 3 + 1] * 3, ic = g.Indices[t * 3 + 2] * 3;
                var a = new Vector3(v[ia], v[ia + 1], v[ia + 2]);
                var e1 = new Vector3(v[ib], v[ib + 1], v[ib + 2]) - a;
                var e2 = new Vector3(v[ic], v[ic + 1], v[ic + 2]) - a;
                var p = Vector3.Cross(down, e2);
                float det = Vector3.Dot(e1, p);
                if (MathF.Abs(det) < 1e-9f) continue;
                var o = start - a;
                float u = Vector3.Dot(o, p) / det;
                if (u < 0 || u > 1) continue;
                var q = Vector3.Cross(o, e1);
                float w = Vector3.Dot(down, q) / det;
                if (w < 0 || u + w > 1) continue;
                float d = Vector3.Dot(e2, q) / det;
                if (d >= 0 && d < best) best = d;
            }
        if (best == float.MaxValue) return false;
        seed = start + down * best;
        seed.Y = MathF.Max(seed.Y, WorldWater.Height);
        return true;
    }

    /// <summary>How deep (units) a door painter must reach into the interior hull to paint the interior's cells by the door (engine choice: two cells).</summary>
    public const float DoorHullDepth = 4;

    /// <summary>How far (units) a door painter is swept toward the inside at most to reach <see cref="DoorHullDepth"/> into the hull (engine choice).</summary>
    public const float DoorHullReach = 12;

    /// <summary>
    /// A door painter whose leaf stands outside the interior hull, swept toward the door's inner marker until it reaches <see cref="DoorHullDepth"/> into the
    /// hull (at most <see cref="DoorHullReach"/>). Unswept, such a door paints no cell of the interior, so the interior has no door polygons and no door join
    /// (docs/game/pathfinding.md, "Door painters"; Heft's bar, the Outpost s-III `47.16:241-Newwworld-INGAME`, leaf 2..3 outside its hull). Unchanged when it
    /// reaches that deep already or the door has no markers.
    /// </summary>
    NavVolume ReachIntoHull(ZoneGeometry g, NavVolume painter, GameRecord door, PreparedShape shape, Matrix4x4 node)
    {
        var hull = g.InteriorHull!.Polygon;
        if (painter.Polygon.Any(p => NavGeometry.InsideConvex(hull, p, -DoorHullDepth)) || !DoorMarkers(door, shape, node, out var behind, out var ahead)) return painter;
        var centre = hull.Aggregate(Vector2.Zero, (a, p) => a + p) / hull.Length;
        Vector2 b = new(behind.X, behind.Z), a = new(ahead.X, ahead.Z);
        var inward = Vector2.Distance(a, centre) < Vector2.Distance(b, centre) ? a - b : b - a;
        if (inward.LengthSquared() < 1e-6f) return painter;
        inward = Vector2.Normalize(inward);
        for (float d = 1; d <= DoorHullReach; d++)
        {
            var moved = painter.Polygon.Select(p => p + inward * d).ToArray();
            if (!moved.Any(p => NavGeometry.InsideConvex(hull, p, -DoorHullDepth))) continue;
            var points = painter.Polygon.Concat(moved).Select(p => new Vector3(p.X, 0, p.Y)).ToList();
            return NavGeometry.Footprint(points, painter.YMin, painter.YMax, 0);
        }
        return painter;
    }

    /// <summary>The door's inner marker: the one of the two points either side of the door that lies inside the interior hull (height resolved later).</summary>
    void AddInnerDoorSeed(ZoneGeometry g, GameRecord door, PreparedShape shape, Matrix4x4 node)
    {
        if (g.InteriorHull is null || !DoorMarkers(door, shape, node, out var behind, out var ahead)) return;
        var hullCentre = g.InteriorHull.Polygon.Aggregate(Vector2.Zero, (a, p) => a + p) / g.InteriorHull.Polygon.Length;
        Vector3? best = null;
        float bestScore = float.MaxValue;
        foreach (var at in new[] { behind, ahead })
        {
            var xz = new Vector2(at.X, at.Z);
            float score = (NavGeometry.InsideConvex(g.InteriorHull.Polygon, xz, 0) ? 0 : 1000) + Vector2.Distance(xz, hullCentre);
            if (score < bestScore) { bestScore = score; best = at; }
        }
        if (best is { } p2) { g.Seeds.Add(new Vector3(p2.X, float.NaN, p2.Z)); g.Stats.DoorSeeds++; }
    }

    /// <summary>
    /// A vertical ray against the walkable triangles in [first, last): the highest hit, or the lowest one above <paramref name="minY"/>; hits above
    /// <paramref name="maxY"/> do not count.
    /// </summary>
    public static bool RayDown(ZoneGeometry g, float x, float z, bool highest, float minY, int first, int last, out float y, bool anyArea = false, float maxY = float.PositiveInfinity)
    {
        y = highest ? float.MinValue : float.MaxValue;
        bool hit = false;
        var v = g.Vertices;
        for (int t = first; t < last; t++)
        {
            if (!anyArea && g.Areas[t] != NavArea.Ground) continue;
            int ia = g.Indices[t * 3] * 3, ib = g.Indices[t * 3 + 1] * 3, ic = g.Indices[t * 3 + 2] * 3;
            if (!NavGeometry.Barycentric(x, z, new Vector3(v[ia], v[ia + 1], v[ia + 2]), new Vector3(v[ib], v[ib + 1], v[ib + 2]), new Vector3(v[ic], v[ic + 1], v[ic + 2]), out float l1, out float l2, out float l3)) continue;
            if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
            float h = l1 * v[ia + 1] + l2 * v[ib + 1] + l3 * v[ic + 1];
            if (h < minY || h > maxY) continue;
            if (highest ? h > y : h < y) y = h;
            hit = true;
        }
        return hit;
    }
}
