using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>
/// A mineable rock (docs/game/buildings-production.md "Resources"): where it stands, its <c>world resource mining</c>, its radius and its BUILDING
/// record; with its mesh's <paramref name="Shape"/>, placed by <paramref name="Transform"/>, a click hits the rock as drawn.
/// </summary>
public readonly record struct ResourceNode(
    Vector3 Position, int Resource, float Radius, string Record = "", Vector3? Centre = null, RockShape? Shape = null,
    Matrix4x4 Transform = default)
{
    /// <summary>
    /// The ray distance to the rock (a unit <paramref name="direction"/>): its mesh's triangles, or without them a sphere of its radius round its
    /// centre; null: missed.
    /// </summary>
    public float? RayHit(Vector3 origin, Vector3 direction)
    {
        // The local ray is the world one mapped by the inverse transform, so its parameter is the world distance.
        if (Shape is { } shape && Matrix4x4.Invert(Transform, out var inverse))
            return shape.Hit(Vector3.Transform(origin, inverse), Vector3.TransformNormal(direction, inverse));
        var to = (Centre ?? Position) - origin;
        float along = Vector3.Dot(to, direction);
        float miss = to.LengthSquared() - along * along, r2 = Radius * Radius;
        if (miss > r2) return null;
        float t = along - MathF.Sqrt(r2 - miss);
        return t >= 0 ? t : null;
    }
}

/// <summary>
/// A rock mesh's triangles in mesh space (three corners each) and their bounds box.
/// </summary>
public sealed class RockShape(Vector3[] corners, Vector3 min, Vector3 max)
{
    public Vector3 Min => min;
    public Vector3 Max => max;

    /// <summary>
    /// The ray parameter of the nearest triangle the ray meets (either side), null: none.
    /// </summary>
    public float? Hit(Vector3 origin, Vector3 direction)
    {
        if (!FloorSurfaces.RayBox(origin, direction, min, max, float.MaxValue)) return null;
        float? best = null;
        for (int i = 0; i + 2 < corners.Length; i += 3)
            if (FloorSurfaces.RayTriangle(origin, direction, corners[i], corners[i + 1], corners[i + 2]) is { } t && (best is null || t < best))
                best = t;
        return best;
    }

    Vector2[]? _outline;

    /// <summary>
    /// The mesh's footprint: the convex hull of its corners' X/Z, counter-clockwise (made on first use).
    /// </summary>
    public Vector2[] Outline => _outline ??= Hull([.. corners.Select(c => new Vector2(c.X, c.Z))]);

    /// <summary>
    /// Whether the mesh-space point lies inside the footprint.
    /// </summary>
    public bool InsideXz(Vector2 p)
    {
        var hull = Outline;
        if (hull.Length < 3) return false;
        for (int i = 0; i < hull.Length; i++)
            if (Cross(hull[i], hull[(i + 1) % hull.Length], p) < 0) return false;
        return true;
    }

    /// <summary>
    /// The footprint's point nearest the mesh-space point: itself when inside.
    /// </summary>
    public Vector2 NearestXz(Vector2 p)
    {
        var hull = Outline;
        if (hull.Length == 0 || InsideXz(p)) return p;
        Vector2 best = hull[0];
        float bestDistance = float.MaxValue;
        for (int i = 0; i < hull.Length; i++)
        {
            Vector2 a = hull[i], b = hull[(i + 1) % hull.Length], ab = b - a;
            float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            var q = a + t * ab;
            float d = Vector2.DistanceSquared(p, q);
            if (d < bestDistance) (best, bestDistance) = (q, d);
        }
        return best;
    }

    static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>
    /// Andrew's monotone chain: the convex hull, counter-clockwise, without collinear points.
    /// </summary>
    static Vector2[] Hull(Vector2[] points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (sorted.Length < 3) return sorted;
        var hull = new Vector2[2 * sorted.Length];
        int k = 0;
        foreach (var p in sorted)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p) <= 0) k--;
            hull[k++] = p;
        }
        for (int i = sorted.Length - 2, lower = k + 1; i >= 0; i--)
        {
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], sorted[i]) <= 0) k--;
            hull[k++] = sorted[i];
        }
        return hull[..(k - 1)];
    }

    /// <summary>
    /// The triangle lists of <paramref name="mesh"/>'s submeshes; null when it has none.
    /// </summary>
    public static RockShape? Of(OgreMesh mesh)
    {
        var corners = new List<Vector3>();
        foreach (var sub in mesh.SubMeshes)
        {
            if (sub.Operation != OgreOperationType.TriangleList
                || (sub.UseSharedVertices ? mesh.SharedVertexData : sub.VertexData)?.ReadPositions() is not { } positions)
                continue;
            var indices = sub.Indices.Indices;
            for (int i = 0; i + 2 < indices.Length; i += 3)
                if (indices[i] < positions.Length && indices[i + 1] < positions.Length && indices[i + 2] < positions.Length)
                    corners.AddRange([positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]]]);
        }
        if (corners.Count == 0) return null;
        Vector3 min = corners[0], max = corners[0];
        foreach (var c in corners) (min, max) = (Vector3.Min(min, c), Vector3.Max(max, c));
        return new RockShape([.. corners], min, max);
    }
}

/// <summary>
/// Where the world's mineable rocks are.
/// </summary>
public interface IResourceNodes
{
    /// <summary>
    /// The rocks of <paramref name="resource"/> (0: of any) within <paramref name="range"/> (horizontally) of a point.
    /// </summary>
    IReadOnlyList<ResourceNode> Near(int resource, float x, float z, float range);
}

/// <summary>
/// The rocks the foliage system places (docs/formats/foliage.md, "building type"): instances whose FOLIAGE_MESH names a BUILDING, the rock's resource
/// being that building's functionality <c>world resource mining</c>. Zones are placed once and kept (through the foliage world's layout cache when it
/// has one). Thread-safe.
/// </summary>
public sealed class FoliageResourceNodes(FoliageWorld world, GameDatabase db, Func<string, string?>? findMesh = null) : IResourceNodes, IDisposable
{
    /// <summary>
    /// The smallest radius of a rock: the original takes the rock mesh's bounds and at least 25 (<c>uses_resources</c>); without the mesh file a rock
    /// is this.
    /// </summary>
    public const float NodeRadius = 25;

    readonly Dictionary<string, (Vector3 Centre, float Radius, RockShape Shape)?> _meshBounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A rock mesh's shape and its bounds sphere (the triangles' box middle, and its longest half-extent as the radius, before scaling); null without
    /// <c>findMesh</c>, the file or triangles.
    /// </summary>
    (Vector3 Centre, float Radius, RockShape Shape)? Bounds(string meshPath)
    {
        if (findMesh is null) return null;
        if (_meshBounds.TryGetValue(meshPath, out var known)) return known;
        RockShape? shape = null;
        if ((findMesh(meshPath) ?? findMesh(Path.GetFileName(meshPath.Replace('\\', '/')))) is { } path)
            try
            {
                shape = RockShape.Of(OgreMeshReader.ReadFile(path));
            }
            catch (Exception e) when (e is IOException or OgreFormatException or NotSupportedException) { }
        if (shape is null) return _meshBounds[meshPath] = null;
        // The longest half-extent of the bounds box, not its half-diagonal (a rock of 71 x 72 x 66 is 36 across, not 60: the diagonal made the radius
        // nearly twice the rock).
        var size = shape.Max - shape.Min;
        return _meshBounds[meshPath] = ((shape.Min + shape.Max) / 2, Math.Max(size.X, Math.Max(size.Y, size.Z)) / 2, shape);
    }

    readonly Dictionary<ZoneCoordinate, List<ResourceNode>> _zones = [];
    readonly Dictionary<string, int> _resourceOf = new(StringComparer.Ordinal);
    readonly Lock _gate = new();

    public IReadOnlyList<ResourceNode> Near(int resource, float x, float z, float range)
    {
        var found = new List<ResourceNode>();
        lock (_gate)
        {
            var first = WorldLayout.ZoneOf(x - range, z - range);
            var last = WorldLayout.ZoneOf(x + range, z + range);
            for (int zx = first.X; zx <= last.X; zx++)
                for (int zy = first.Y; zy <= last.Y; zy++)
                    foreach (var node in Zone(new ZoneCoordinate(zx, zy)))
                        if ((resource == 0 || node.Resource == resource)
                            && Vector2.DistanceSquared(new(node.Position.X, node.Position.Z), new(x, z)) <= range * range)
                            found.Add(node);
        }
        return found;
    }

    List<ResourceNode> Zone(ZoneCoordinate zone)
    {
        if (_zones.TryGetValue(zone, out var list)) return list;
        list = [];
        if (zone.IsInsideGrid)
            foreach (var instance in world.Zone(zone).Instances)
                if (instance.Mesh.BuildingType is { } building && ResourceOf(building) is > 0 and var resource)
                {
                    // The rock's size is its mesh's bounds scaled by the instance (at least NodeRadius), centred on the bounds' middle, not on the
                    // origin the mesh sits on; it is picked by
                    // the mesh's triangles where the instance places them.
                    var bounds = Bounds(instance.Mesh.MeshPath);
                    float radius = bounds is { } b ? Math.Max(NodeRadius, b.Radius * instance.Scale) : NodeRadius;
                    list.Add(bounds is { } c
                        ? new ResourceNode(instance.Position, resource, radius, building.StringId,
                            Vector3.Transform(c.Centre, instance.Transform), c.Shape, instance.Transform)
                        : new ResourceNode(instance.Position, resource, radius, building.StringId));
                }
        _zones[zone] = list;
        return list;
    }

    int ResourceOf(GameRecord building)
    {
        if (_resourceOf.TryGetValue(building.StringId, out int r)) return r;
        r = building.GetReferences("functionality") is [var link, ..] && db.Find(link.TargetStringId) is { } function
            ? function.GetInt("world resource mining")
            : 0;
        return _resourceOf[building.StringId] = r;
    }

    public void Dispose() => world.Dispose();
}