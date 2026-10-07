using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>A building of a zone with an interior to build: its placement and record.</summary>
public sealed record InteriorSite(BuildingPlacement Placement, GameRecord Record, Vector3 Position, bool Destroyed);

/// <summary>How a building's shapes are taken for its interior mesh (docs/game/pathfinding.md, "Interiors").</summary>
sealed record InteriorContext(string InstanceId, bool Destroyed, uint Mask, bool Own);

public sealed partial class ZoneGeometryGatherer
{
    /// <summary>Shape groups of an interior job: 9..13, 15..22 and 27; for a destroyed building only 9, 10, 13 and 19 (FUN_1403c79b0).</summary>
    public const uint InteriorGroupMask = 0x87fbe00, DestroyedInteriorGroupMask = 0x82600;

    /// <summary>How far outside the hull the clipping slabs reach (units); anything of the building beyond is cut away.</summary>
    const float SlabReach = 400;

    static Matrix4x4 NodeMatrix(GameRecord building, Vector3 position, Quaternion rotation)
    {
        float scale = building.GetFloat("scale", 1);
        if (scale <= 0) scale = 1;
        var q = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(position);
    }

    /// <summary>
    /// The buildings placed in a zone that have an interior to build: an <c>interior mask</c> part with collision, and not a gateway
    /// (the approximation of SHELL_WITH_INTERIOR used by the exterior carvers too).
    /// </summary>
    public IReadOnlyList<InteriorSite> InteriorSites(ZoneCoordinate zone)
    {
        var (ox, oz) = WorldLayout.ZoneOrigin(zone);
        var min = new Vector2((float)ox, (float)oz);
        var max = min + new Vector2(WorldLayout.ZoneSize);
        var list = new List<InteriorSite>();
        foreach (var (b, record, position, destroyed) in NearBuildings(zone, min, max, 0))
        {
            if (position.X < min.X || position.X >= max.X || position.Z < min.Y || position.Z >= max.Y) continue;
            if (record.GetBool("is gateway") || b.Zone != zone) continue;
            if (!MaskParts(record).Any()) continue;
            list.Add(new InteriorSite(b, record, position, destroyed));
        }
        return list;
    }

    IEnumerable<(GameRecord Part, string Path)> MaskParts(GameRecord building)
    {
        foreach (var r in building.GetReferences("interior mask"))
            if (db.Find(r.TargetStringId) is { Type: FcsRecordType.BUILDING_PART } mask && mask.GetPath("xml collision") is { Length: > 0 } path)
                yield return (mask, path);
    }

    /// <summary>
    /// The input of one building's interior mesh: its own shapes and its furniture's, the door painters, the inner door markers as seeds, and the
    /// complement of the interior hull as carvers (the hull's inverted volume: everything outside it is removed). The box is a square of whole tiles.
    /// Null when the building has no usable hull.
    /// </summary>
    public ZoneGeometry? GatherInterior(InteriorSite site, float tileWorld, float pad = 12)
    {
        var node = NodeMatrix(site.Record, site.Position, site.Placement.Rotation);
        var points = new List<Vector3>();
        foreach (var (_, path) in MaskParts(site.Record))
        {
            if (collision.Get(path) is not { } prepared) continue;
            foreach (var shape in prepared.Shapes)
                foreach (var v in shape.Vertices) points.Add(Vector3.Transform(v, node));
        }
        if (points.Count < 4) return null;
        float yMin = points.Min(p => p.Y), yMax = points.Max(p => p.Y);
        var hull = NavGeometry.Footprint(points, yMin, yMax, 0);
        if (hull.Polygon.Length < 3) return null;

        var lo = new Vector2(hull.Polygon.Min(p => p.X), hull.Polygon.Min(p => p.Y)) - new Vector2(pad);
        var hi = new Vector2(hull.Polygon.Max(p => p.X), hull.Polygon.Max(p => p.Y)) + new Vector2(pad);
        float side = MathF.Ceiling(MathF.Max(hi.X - lo.X, hi.Y - lo.Y) / tileWorld) * tileWorld;
        var g = new ZoneGeometry { Zone = site.Placement.Zone, ZoneMin = lo, ZoneMax = lo + new Vector2(side), Margin = 8, InteriorHull = hull, InteriorOf = site.Placement.InstanceId };

        uint mask = site.Destroyed ? DestroyedInteriorGroupMask : InteriorGroupMask;
        AddBuilding(g, site.Record, site.Placement.InstanceId, site.Position, site.Placement.Rotation, site.Destroyed, new InteriorContext(site.Placement.InstanceId, site.Destroyed, mask, Own: true));

        // Other buildings standing inside the hull (furniture placed in the zone file): walkable ones give floor, the rest cut.
        var (ox, oz) = WorldLayout.ZoneOrigin(site.Placement.Zone);
        var zmin = new Vector2((float)ox, (float)oz);
        foreach (var (b, record, position, destroyed) in NearBuildings(site.Placement.Zone, zmin, zmin + new Vector2(WorldLayout.ZoneSize), 0))
        {
            if (b.InstanceId == site.Placement.InstanceId || position.Y < yMin - 30 || position.Y > yMax + 30) continue;
            if (!NavGeometry.InsideConvex(hull.Polygon, new Vector2(position.X, position.Z), 5)) continue;
            AddBuilding(g, record, b.InstanceId, position, b.Rotation, destroyed, new InteriorContext(site.Placement.InstanceId, destroyed, mask, Own: false));
        }

        // A destroyed building's "destroyed boundary" cuts its interior (fcs.def: navmesh cutter for a destroyed interior).
        if (site.Destroyed && site.Record.GetPath("destroyed boundary") is { Length: > 0 } boundary && collision.Get(boundary) is { } cutter)
            foreach (var shape in cutter.Shapes) AddShape(g, shape, node, 0, carve: false);

        AddOutsideCarvers(g, hull.Polygon);

        // The inner door markers were added without a height: put them on the floor.
        for (int i = 0; i < g.Seeds.Count; i++)
        {
            var s = g.Seeds[i];
            if (!float.IsNaN(s.Y)) continue;
            g.Seeds[i] = RayDown(g, s.X, s.Z, highest: false, yMin - 10, 0, g.TriangleCount, out float y) ? new Vector3(s.X, y, s.Z) : new Vector3(s.X, yMin, s.Z);
        }
        g.BuildingHash = BuildingHash(site.Placement.Zone);
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
            if (g.Painters.Count > before) g.Painters[^1].Owner = instanceId;
            AddInnerDoorSeed(g, owner, shape, node);
            return;
        }
        if ((ctx.Mask & (1u << group)) == 0) return;
        g.Stats.Shapes++;
        bool walkable = group != 27 && (ctx.Own || mode == PathMode.Walkable);
        AddShape(g, shape, node, walkable ? BuildingSlopeDegrees : 0, carve: false);
    }

    /// <summary>The door's inner marker: the one of the two points either side of the door that lies inside the interior hull (height resolved later).</summary>
    void AddInnerDoorSeed(ZoneGeometry g, GameRecord door, PreparedShape shape, Matrix4x4 node)
    {
        if (shape.Kind != Meitou.Data.Physics.CollisionShapeKind.Box || shape.Vertices.Length != 8 || g.InteriorHull is null) return;
        var v = shape.Vertices.Select(p => Vector3.Transform(p, node)).ToArray();
        int axis = Math.Clamp(door.GetInt("door navmesh axis"), 0, 2);
        var along = axis switch { 0 => v[1] - v[0], 1 => v[2] - v[0], _ => v[4] - v[0] };
        float half = along.Length() / 2;
        if (half < 1e-3f) return;
        var dir = along / (2 * half);
        var centre = Vector3.Zero;
        foreach (var p in v) centre += p / 8;
        var hullCentre = g.InteriorHull.Polygon.Aggregate(Vector2.Zero, (a, p) => a + p) / g.InteriorHull.Polygon.Length;
        Vector3? best = null;
        float bestScore = float.MaxValue;
        foreach (float side in new[] { -1f, 1f })
        {
            var at = centre + dir * side * (half + DoorSeedOffset);
            var xz = new Vector2(at.X, at.Z);
            float score = (NavGeometry.InsideConvex(g.InteriorHull.Polygon, xz, 0) ? 0 : 1000) + Vector2.Distance(xz, hullCentre);
            if (score < bestScore) { bestScore = score; best = at; }
        }
        if (best is { } p2) { g.Seeds.Add(new Vector3(p2.X, float.NaN, p2.Z)); g.Stats.DoorSeeds++; }
    }

    /// <summary>
    /// A vertical ray against the walkable triangles in [first, last): the highest hit, or the lowest one above <paramref name="minY"/>.
    /// </summary>
    public static bool RayDown(ZoneGeometry g, float x, float z, bool highest, float minY, int first, int last, out float y, bool anyArea = false)
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
            if (h < minY) continue;
            if (highest ? h > y : h < y) y = h;
            hit = true;
        }
        return hit;
    }
}
