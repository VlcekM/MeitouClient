using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// Where a ray cast straight down meets a surface: its height and the surface's normal, turned up.
/// </summary>
public readonly record struct GroundHit(float Y, Vector3 Normal);

/// <summary>
/// What the original reads of a placed building's own parts, as it stands or as a ruin: the physics group of each part, the highest surface over a
/// point that a ground ray (mask <c>0x204</c>: the terrain and group 9) meets of it first, and the radius of its bounding box, the merged world boxes
/// of its part entities (<c>FUN_140549480</c>, <c>Ogre::Aabb::getRadius</c>). Its doors are buildings of their own and do not count; a ruin has no
/// <c>interior</c> parts.
/// </summary>
public static class BuildingShape
{
    /// <summary>
    /// The group of the ordinary floor-0 building parts (floor 4 and up too), the only building group a ground ray of mask <c>0x204</c> meets.
    /// </summary>
    public const int GroundGroup = 9;

    /// <summary>
    /// The physics collision group of a building part (<c>FUN_14055f6e0</c>, docs/formats/collision.md; later tests win): 9 + floor for floors 0..3
    /// (else 9), unwalkable roof 27, stairs 15 + floor for floors 0..3 (else unchanged), then a passable part or an IGNORE owner
    /// (<paramref name="ignored"/>) 23 + object floor, else a furniture owner's 19 + object floor (the object floor 0..3, else 0). A furniture
    /// owner's base and stairs groups use the placed object's floor (<paramref name="objectFloor"/>, its state's <c>floornum</c>), any other part
    /// its own <c>building floor</c>, unclamped.
    /// </summary>
    public static int CollisionGroup(GameRecord part, bool ignored, bool furniture, int objectFloor)
    {
        int floor = furniture ? objectFloor : part.GetInt("building floor");
        bool inRange = floor is >= 0 and <= 3, objectInRange = objectFloor is >= 0 and <= 3;
        int group = inRange ? 9 + floor : 9;
        if (part.GetBool("is unwalkable roof")) group = 27;
        if (part.GetBool("is stairs") && inRange) group = 15 + floor;
        if (ignored || part.GetBool("passable")) return 23 + (objectInRange ? objectFloor : 0);

        return furniture ? 19 + (objectInRange ? objectFloor : 0) : group;
    }

    /// <summary>
    /// The world triangles (three corners each) of the building's group-9 parts (<see cref="GroundGroup"/>; placed on floor 0): what a ground ray
    /// can meet of it.
    /// </summary>
    public static Vector3[] GroundTriangles(GameDatabase db, GameRecord building, string placementId, Vector3 position, Quaternion rotation,
        bool destroyed, InteriorCollisions collision)
    {
        float scale = building.GetFloat("scale", 1);
        var node = WorldObjectLayout.InstanceTransform(position, rotation, new Vector3(scale > 0 ? scale : 1));
        bool ignored = building.GetInt("path mode", 2) == 0;
        bool furniture = building.GetBool("is exterior furniture") || building.GetBool("is interior furniture");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var corners = new List<Vector3>();
        foreach (var part in OwnParts(db, building, placementId, position, rotation, destroyed))
        {
            if (CollisionGroup(part.Source, ignored, furniture, 0) != GroundGroup) continue;

            string path = part.CollisionPath(destroyed);
            if (!seen.Add(path) || collision.Mesh(path) is not { } mesh) continue;

            foreach (int i in mesh.Indices) corners.Add(Vector3.Transform(mesh.Vertices[i], node));
        }

        return [.. corners];
    }

    /// <summary>
    /// The highest of <paramref name="corners"/>' triangles straight over <paramref name="at"/> (X, Z), what a ray cast straight down from height
    /// <paramref name="from"/> meets of them first (one above it is not met), with its normal turned up; null when none lies under it.
    /// </summary>
    public static GroundHit? TopOf(Vector3[] corners, Vector2 at, float from = float.PositiveInfinity)
    {
        GroundHit? top = null;
        for (int t = 0; t + 2 < corners.Length; t += 3)
        {
            var (a, b, c) = (corners[t], corners[t + 1], corners[t + 2]);
            if (HeightOver(a, b, c, at) is not { } y || y > from || top is { } best && y <= best.Y) continue;

            var normal = Vector3.Cross(b - a, c - a);
            if (normal.Y < 0) normal = -normal;
            top = new GroundHit(y, normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY);
        }

        return top;
    }

    /// <summary>
    /// The radius of the building's bounding box: half the diagonal of the box round its drawn parts' render-mesh bounds placed in the world (a
    /// collision-only part makes no entity, <c>FUN_14055f6e0</c> mode 2); 0 when no part's bounds can be read.
    /// </summary>
    public static float BoundsRadius(GameDatabase db, GameRecord building, string placementId, Vector3 position, Quaternion rotation,
        bool destroyed, InteriorCollisions meshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var part in OwnParts(db, building, placementId, position, rotation, destroyed))
        {
            if (part.RenderHidden || meshes.RenderBounds(part.MeshPath) is not { } b) continue;

            for (int corner = 0; corner < 8; corner++)
            {
                var local = new Vector3((corner & 1) == 0 ? b.Min.X : b.Max.X, (corner & 2) == 0 ? b.Min.Y : b.Max.Y, (corner & 4) == 0 ? b.Min.Z : b.Max.Z);
                var world = Vector3.Transform(local, part.Transform);
                min = Vector3.Min(min, world);
                max = Vector3.Max(max, world);
            }
        }

        return min.X > max.X ? 0 : (max - min).Length() / 2;
    }

    /// <summary>
    /// The building's own parts as it stands (with its <c>interior</c> parts) or as a ruin; not its doors'.
    /// </summary>
    static IEnumerable<PlacedMesh> OwnParts(GameDatabase db, GameRecord building, string placementId, Vector3 position, Quaternion rotation,
        bool destroyed)
    {
        var state = new BuildingState(destroyed) { WithInterior = !destroyed };
        return WorldObjectLayout.Building(db, building, placementId, position, rotation, state).Where(p => p.Owner == building);
    }

    /// <summary>
    /// The height of triangle <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/> straight over <paramref name="at"/>; null when the
    /// point lies outside it seen from above, or the triangle stands on edge.
    /// </summary>
    static float? HeightOver(Vector3 a, Vector3 b, Vector3 c, Vector2 at)
    {
        float area = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
        if (MathF.Abs(area) < 1e-6f) return null;

        float u = ((b.X - at.X) * (c.Z - at.Y) - (b.Z - at.Y) * (c.X - at.X)) / area;
        float v = ((c.X - at.X) * (a.Z - at.Y) - (c.Z - at.Y) * (a.X - at.X)) / area;
        float w = 1 - u - v;
        if (u < 0 || v < 0 || w < 0) return null;

        return u * a.Y + v * b.Y + w * c.Y;
    }
}
