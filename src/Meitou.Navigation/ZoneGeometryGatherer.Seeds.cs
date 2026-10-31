using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Navigation;

internal sealed partial class ZoneGeometryGatherer
{
    /// <summary>
    /// The seed points either side of a door: the door shape's centre moved across its width axis past its faces, then dropped to the ground
    /// (docs: the original takes the outer marker as an exterior seed and the inner one as the interior's). Only box doors are handled.
    /// </summary>
    void AddDoorSeeds(ZoneGeometry g, GameRecord door, PreparedShape shape, Matrix4x4 node)
    {
        if (!DoorMarkers(door, shape, node, out var behind, out var ahead)) return;
        float bottom = shape.Vertices.Min(v => Vector3.Transform(v, node).Y);
        foreach (var at in new[] { behind, ahead })
        {
            if (at.X < g.ZoneMin.X || at.X > g.ZoneMax.X || at.Z < g.ZoneMin.Y || at.Z > g.ZoneMax.Y) continue;
            g.DoorMarkers.Add((new Vector2(at.X, at.Z), bottom));
            g.Stats.DoorSeeds++;
        }
    }

    /// <summary>How far above a door's base the surface its seed stands on may be (engine choice: the agent's climb).</summary>
    public const float DoorSeedRise = 5;

    /// <summary>
    /// A door marker's seed stands on the highest walkable surface under it at most <see cref="DoorSeedRise"/> above the door's base, as the original drops the
    /// marker with a ray; the terrain (or the water) where there is none. The terrain under a raised doorstep would seed the ground inside the building's
    /// foundation (docs/game/pathfinding.md, "Entrance seeds").
    /// </summary>
    void AddDoorMarkerSeeds(ZoneGeometry g)
    {
        foreach (var (at, bottom) in g.DoorMarkers)
        {
            float y = RayDown(g, at.X, at.Y, highest: true, float.MinValue, 0, g.TriangleCount, out float hit, maxY: bottom + DoorSeedRise)
                ? hit : Math.Max(map.HeightAt(at.X, at.Y), WorldWater.Height);
            g.Seeds.Add(new Vector3(at.X, y, at.Y));
        }
    }

    /// <summary>How far past a door's face its seed markers sit (engine choice).</summary>
    public const float DoorSeedOffset = 10;

    /// <summary>
    /// The two marker points either side of a box door, in the order the callers try them (the side of the negative axis direction first, which decides ties):
    /// the shape's centre moved perpendicular to its width (<c>door navmesh axis</c>) past its faces. False for a shape that is not a box or is degenerate.
    /// </summary>
    static bool DoorMarkers(GameRecord door, PreparedShape shape, Matrix4x4 node, out Vector3 behind, out Vector3 ahead)
    {
        behind = ahead = default;
        if (shape.Kind != Meitou.Data.Physics.CollisionShapeKind.Box || shape.Vertices.Length != 8) return false;
        var v = shape.Vertices.Select(p => Vector3.Transform(p, node)).ToArray();
        int axis = Math.Clamp(door.GetInt("door navmesh axis"), 0, 2);
        var along = axis switch { 0 => v[1] - v[0], 1 => v[2] - v[0], _ => v[4] - v[0] };
        var normal = Vector3.Cross(along, Vector3.TransformNormal(Vector3.UnitY, node));
        if (normal.LengthSquared() < 1e-6f) return false;
        var dir = Vector3.Normalize(normal);
        var centre = Vector3.Zero;
        foreach (var p in v) centre += p / 8;
        float half = v.Max(p => MathF.Abs(Vector3.Dot(p - centre, dir)));
        behind = centre + dir * -1f * (half + DoorSeedOffset);
        ahead = centre + dir * 1f * (half + DoorSeedOffset);
        return true;
    }

    // ---- seeds ----

    /// <summary>A seeds.def point lower than this has no authored height (the file uses −99).</summary>
    const float NoHeightBelow = -50;

    /// <summary>How far (units) outside the zone box a linked wall's placement may be and still give seeds (engine choice).</summary>
    const float WallReach = 400;

    void AddSeeds(ZoneGeometry g)
    {
        AddDoorMarkerSeeds(g);
        float water = WorldWater.Height;
        // The original's ray rule (FUN_1403d4dd0): a seed.def point without a height (Y -99) that a ray from above hits on a floor-0/1 building part (groups 9
        // and 10) is moved onto it, one with a height that hits one is dropped. A roof, an unwalkable roof (27), stairs or furniture does not count.
        bool HitsBuilding(float x, float z, out float y)
        {
            y = float.MinValue;
            bool hit = false;
            foreach (var (first, last) in g.FloorRanges)
                if (RayDown(g, x, z, highest: true, float.MinValue, first, last, out float h, anyArea: true) && h > y) { y = h; hit = true; }
            return hit;
        }
        var fileSeeds = FileSeeds(g.Zone);
        foreach (var s in fileSeeds)
        {
            bool noHeight = s.Y < NoHeightBelow;
            bool hit = HitsBuilding(s.X, s.Z, out float top);
            if (hit && !noHeight) { g.Stats.SeedsDropped++; continue; }
            float y = noHeight ? (hit ? top : Math.Max(map.HeightAt(s.X, s.Z), water)) : s.Y;
            g.Seeds.Add(new Vector3(s.X, y, s.Z));
        }
        if (fileSeeds.Count == 0)
        {
            // A zone with no seeds.def points gets a 3 x 3 grid of ground seeds (a ground seed is only added where nothing is hit).
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 3; i++)
                {
                    float x = g.ZoneMin.X + WorldLayout.ZoneSize * (2 * i + 1) / 6f, z = g.ZoneMin.Y + WorldLayout.ZoneSize * (2 * j + 1) / 6f;
                    if (HitsBuilding(x, z, out _)) continue;
                    g.Seeds.Add(new Vector3(x, Math.Max(map.HeightAt(x, z), water), z));
                }
        }
        AddLinkedWallSeeds(g, HitsBuilding);
    }

    delegate bool BuildingRay(float x, float z, out float y);

    /// <summary>
    /// Linked wall sections (BUILDING with <c>link length</c> above 0): three seeds on top of a WALKABLE wall where the ray down hits it, and for walls that leave
    /// the zone three ground seeds clamped into the zone box. The three positions along the wall are our choice (0.15, 0.5 and 0.85 of its length, the length
    /// taken from the collision footprint); the original's are Unknown.
    /// </summary>
    void AddLinkedWallSeeds(ZoneGeometry g, BuildingRay hits)
    {
        float water = WorldWater.Height;
        foreach (var (b, record, position, destroyed) in NearBuildings(g.Zone, g.ZoneMin, g.ZoneMax, 0))
        {
            if (record.GetFloat("link length") <= 0) continue;
            if (position.X < g.ZoneMin.X - WallReach || position.X > g.ZoneMax.X + WallReach || position.Z < g.ZoneMin.Y - WallReach || position.Z > g.ZoneMax.Y + WallReach) continue;
            var node = NodeMatrix(record, position, b.Rotation);
            var points = new List<Vector3>();
            foreach (var mesh in WorldObjectLayout.Building(db, record, b.InstanceId, position, b.Rotation, new BuildingState(destroyed)))
            {
                string path = CollisionPathOf(mesh.Source, destroyed);
                if (path.Length == 0 || collision.Get(path) is not { } prepared) continue;
                foreach (var shape in prepared.Shapes) foreach (var v in shape.Vertices) points.Add(Vector3.Transform(v, node));
            }
            if (points.Count < 2) continue;
            // The two farthest footprint points are the wall's ends.
            var hull = NavGeometry.Footprint(points, 0, 0, 0).Polygon;
            Vector2 a = hull[0], c = hull[0];
            float best = -1;
            foreach (var p in hull) foreach (var q in hull) { float d = Vector2.DistanceSquared(p, q); if (d > best) { best = d; a = p; c = q; } }
            bool walkable = (PathMode)record.GetInt("path mode", (int)PathMode.Obstacle) == PathMode.Walkable;
            bool leaves = !(InsideBox(g, a) && InsideBox(g, c));
            foreach (float t in new[] { 0.15f, 0.5f, 0.85f })
            {
                var at = Vector2.Lerp(a, c, t);
                if (walkable && InsideBox(g, at) && hits(at.X, at.Y, out float top)) { g.Seeds.Add(new Vector3(at.X, top, at.Y)); g.Stats.WallSeeds++; }
                if (leaves)
                {
                    var clamped = Vector2.Clamp(at, g.ZoneMin, g.ZoneMax);
                    if (!hits(clamped.X, clamped.Y, out _)) { g.Seeds.Add(new Vector3(clamped.X, Math.Max(map.HeightAt(clamped.X, clamped.Y), water), clamped.Y)); g.Stats.WallSeeds++; }
                }
            }
        }
    }

    static bool InsideBox(ZoneGeometry g, Vector2 p) => p.X >= g.ZoneMin.X && p.X <= g.ZoneMax.X && p.Y >= g.ZoneMin.Y && p.Y <= g.ZoneMax.Y;
}
