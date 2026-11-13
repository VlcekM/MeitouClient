using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data.Fcs;
using Meitou.Data.Physics;

namespace Meitou.Data.World;

/// <summary>
/// The indoor volume of a placed building (docs/formats/zones.md, "Interior view"): the collision of its <c>interior mask</c> parts placed with the
/// building's node (position, rotation, <c>scale</c>), kept as the convex footprint of the hull on the ground plane and its height range, and per
/// trigger shape its own footprint and height range. A point inside a shape's footprint and height range is indoors.
/// </summary>
/// <param name="PlacementId">The building's <see cref="BuildingKey"/> (what <see cref="PlacedMesh.PlacementId"/> carries for its parts).</param>
/// <param name="Footprint">Convex polygon of the hull in world X/Z, counter-clockwise.</param>
public sealed record InteriorVolume(string PlacementId, Vector2[] Footprint, float YMin, float YMax)
{
    /// <summary>
    /// The building's <c>interior ambience</c> (enum <c>InteriorSound</c>: 0 None, 1 Metal, 2 Stone, 3 Wicker, 4 Wood).
    /// </summary>
    public int Ambience { get; init; }

    /// <summary>
    /// The building's <c>interior integrity</c> (0..100): the <c>Building_Integrity</c> game parameter while inside.
    /// </summary>
    public float Integrity { get; init; } = 100;

    /// <summary>
    /// The building's <c>cavernous</c> flag: <c>Enter_Building_Big</c> (the big reverb) instead of <c>Enter_Building</c>.
    /// </summary>
    public bool Cavernous { get; init; }

    /// <summary>
    /// A gateway's hull: the original builds the interior object of any building with an <c>interior</c> list (<c>FUN_140551b20</c>, no gateway test)
    /// and the audio ray hits its hull, but it is no room (no roof hiding, no sight, no view).
    /// </summary>
    public bool IsGateway { get; init; }

    /// <summary>
    /// The <c>interior mask</c> parts' render meshes (record paths, <c>phs or mesh</c>), placed with <see cref="Node"/>: what the game draws into its
    /// interior depth range to cut the terrain and foliage inside (docs/render-terrain.md "Interior cut"). Not the trigger hull above.
    /// </summary>
    public IReadOnlyList<string> MaskMeshes { get; init; } = [];

    /// <summary>
    /// The building's node (position, rotation, <c>scale</c>) the mask meshes are placed with.
    /// </summary>
    public Matrix4x4 Node { get; init; } = Matrix4x4.Identity;

    /// <summary>
    /// Whether the volume is straight below or above the ground point (<paramref name="x"/>, <paramref name="z"/>): a shape's footprint contains
    /// it, whatever the height. What a ray cast down from far above hits (the game's building test for the interior ambience).
    /// </summary>
    public bool ContainsGround(float x, float z)
    {
        if (Footprint.Length < 3) return false;
        var q = new Vector2(x, z);
        if (!_edges.Holds(q, 0)) return false;
        if (_pieces.Length == 0) return true;
        foreach (var piece in _pieces)
            if (piece.Edges.Holds(q, 0)) return true;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="p"/> is inside a shape's footprint and height range, both widened by <paramref name="margin"/> (a volume made by
    /// hand: its one footprint and range). A shape is tested on its own: the hull of all of them also takes in what lies between them, as the yard in
    /// an L-shaped house's corner, and a roof shape's footprint at ground level.
    /// </summary>
    public bool Contains(Vector3 p, float margin = 0)
    {
        if (p.Y < YMin - margin || p.Y > YMax + margin || Footprint.Length < 3) return false;
        var q = new Vector2(p.X, p.Z);
        if (!_edges.Holds(q, margin)) return false;
        if (_pieces.Length == 0) return true;
        foreach (var piece in _pieces)
            if (piece.Holds(q, p.Y, margin)) return true;
        return false;
    }

    /// <summary>
    /// How far <paramref name="p"/> is from the volume's box (footprint box by height range): a point nearer than that to it is still outside
    /// (<see cref="Contains"/>, no margin).
    /// </summary>
    public float Distance(Vector3 p)
    {
        var e = _edges;
        float dx = MathF.Max(MathF.Max(e.Min.X - p.X, p.X - e.Max.X), 0), dz = MathF.Max(MathF.Max(e.Min.Y - p.Z, p.Z - e.Max.Y), 0);
        float dy = MathF.Max(MathF.Max(YMin - p.Y, p.Y - YMax), 0);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Contains's footprint, worked out once: the most asked of it (every character and furniture piece near a building, each frame) are far outside.
    /// </summary>
    readonly FootprintEdges _edges = FootprintEdges.Of(Footprint);

    /// <summary>
    /// The trigger's shapes, each with its own footprint and height range; empty for a volume made by hand.
    /// </summary>
    Piece[] _pieces = [];

    /// <summary>
    /// One shape of the trigger hull (a box, capsule or convex mesh): the convex footprint of its corners and their height range.
    /// </summary>
    sealed record Piece(FootprintEdges Edges, float YMin, float YMax)
    {
        /// <summary>
        /// Whether the ground point <paramref name="q"/> at height <paramref name="y"/> is inside, both widened by <paramref name="margin"/>.
        /// </summary>
        public bool Holds(Vector2 q, float y, float margin) => y >= YMin - margin && y <= YMax + margin && Edges.Holds(q, margin);

        /// <summary>
        /// The piece of the world-space corners <paramref name="corners"/>, or null when their footprint has no area.
        /// </summary>
        public static Piece? Of(IReadOnlyList<Vector3> corners)
        {
            var hull = ConvexHull2([.. corners.Select(c => new Vector2(c.X, c.Z))]);
            if (hull.Length < 3) return null;
            return new Piece(FootprintEdges.Of(hull), corners.Min(c => c.Y), corners.Max(c => c.Y));
        }
    }

    /// <summary>
    /// The footprint's edges that are not degenerate (a point on each and its inward unit normal), their box, and how far past the box the footprint
    /// widened by a margin reaches, per unit of margin: the most a corner moves (1 / cos of half the turn between the normals; √2 at a right angle).
    /// </summary>
    sealed class FootprintEdges
    {
        /// <summary>
        /// Per edge, a point on it and its inward unit normal.
        /// </summary>
        public required Vector2[] Points, Normals;
        /// <summary>
        /// The footprint's box.
        /// </summary>
        public Vector2 Min, Max;
        /// <summary>
        /// How far past the box the footprint widened by a margin reaches, per unit of margin.
        /// </summary>
        public float Reach;

        /// <summary>
        /// Whether <paramref name="q"/> is inside the footprint widened by <paramref name="margin"/>: within the widened box, then to the left of every
        /// edge (counter-clockwise).
        /// </summary>
        public bool Holds(Vector2 q, float margin)
        {
            float reach = margin > 0 ? margin * Reach : 0;
            if (q.X < Min.X - reach || q.X > Max.X + reach || q.Y < Min.Y - reach || q.Y > Max.Y + reach) return false;
            for (int i = 0; i < Points.Length; i++)
                if (Vector2.Dot(Normals[i], q - Points[i]) < -margin) return false;
            return true;
        }

        /// <summary>
        /// The edges of <paramref name="footprint"/>, their box and reach.
        /// </summary>
        public static FootprintEdges Of(Vector2[] footprint)
        {
            var points = new List<Vector2>();
            var normals = new List<Vector2>();
            for (int i = 0; i < footprint.Length; i++)
            {
                var a = footprint[i];
                var e = footprint[(i + 1) % footprint.Length] - a;
                float len = e.Length();
                if (len < 1e-4f) continue;
                points.Add(a);
                normals.Add(new Vector2(-e.Y, e.X) / len);
            }
            var edges = new FootprintEdges { Points = [.. points], Normals = [.. normals] };
            // Fewer than three edges bound nothing (every point passes them), so the box may not either.
            if (points.Count < 3 || footprint.Length < 3)
                (edges.Min, edges.Max, edges.Reach) = (new(float.NegativeInfinity), new(float.PositiveInfinity), 0);
            else
            {
                (edges.Min, edges.Max) = (footprint[0], footprint[0]);
                foreach (var v in footprint) (edges.Min, edges.Max) = (Vector2.Min(edges.Min, v), Vector2.Max(edges.Max, v));
                edges.Reach = 1;
                for (int i = 0; i < normals.Count; i++)
                {
                    float turn = 1 + Vector2.Dot(normals[i], normals[(i + 1) % normals.Count]);
                    edges.Reach = Math.Max(edges.Reach, turn < 1e-6f ? float.PositiveInfinity : MathF.Sqrt(2 / turn));
                }
            }
            return edges;
        }
    }

    /// <summary>
    /// The building's floor, roof and underside surfaces, made the first time they are asked for; null for a volume made by hand.
    /// </summary>
    Lazy<FloorSurfaces>? _floors;

    /// <summary>
    /// How far above a point the search for something over it starts (the floor it stands on is not its cover).
    /// </summary>
    public const float CoverClearance = 5;

    /// <summary>
    /// Whether <paramref name="p"/> has the building over it: a floor, stairs or roof part of its collision straight above, from
    /// <see cref="CoverClearance"/> up (<see cref="FloorSurfaces.LowestAbove"/>). False on a walkable roof and in a roofless part, which the hull's
    /// height range still holds: Meitou's indoor lighting asks this, the floor view and the sounds ask <see cref="Contains"/> as the game does.
    /// </summary>
    public bool Covered(Vector3 p, int maxFloor = int.MaxValue) =>
        _floors?.Value.LowestAbove(p.X, p.Z, p.Y + CoverClearance, maxFloor) is not null;

    /// <summary>
    /// The height of the ceiling over <paramref name="p"/> (from <see cref="CoverClearance"/> up), or null when nothing is over it
    /// (<see cref="FloorSurfaces.CeilingAbove"/>).
    /// </summary>
    public float? CeilingAbove(Vector3 p) => _floors?.Value.CeilingAbove(p.X, p.Z, p.Y + CoverClearance);

    /// <summary>
    /// The floor (0..3) a character standing at <paramref name="feet"/> in this building is on, from the building's floor collision under it (made on
    /// first use; 0 when nothing is hit). See <see cref="FloorSurfaces.FloorAt"/>.
    /// </summary>
    public int FloorAt(Vector3 feet) => _floors?.Value.FloorAt(feet) ?? 0;

    /// <summary>
    /// The interior mask collision's triangles in world space (three corners each): the indoor space itself, concave where the building is.
    /// </summary>
    Vector3[] _maskCorners = [];

    /// <summary>
    /// Where a ray first meets the indoor space (the <c>interior mask</c> collision) within <paramref name="maxDistance"/>, or null: what hides the
    /// characters and items inside or behind a building seen from outside from the pointer.
    /// </summary>
    public float? MaskHit(Vector3 origin, Vector3 direction, float maxDistance)
    {
        if (_maskCorners.Length == 0) return null;
        var min = new Vector3(Footprint.Min(p => p.X), YMin, Footprint.Min(p => p.Y));
        var max = new Vector3(Footprint.Max(p => p.X), YMax, Footprint.Max(p => p.Y));
        if (!FloorSurfaces.RayBox(origin, direction, min, max, maxDistance)) return null;
        float? best = null;
        for (int i = 0; i + 2 < _maskCorners.Length; i += 3)
        {
            if (FloorSurfaces.RayTriangle(origin, direction, _maskCorners[i], _maskCorners[i + 1], _maskCorners[i + 2]) is { } t && t <= maxDistance
                && (best is null || t < best))
                best = t;
        }
        return best;
    }

    /// <summary>
    /// Whether the building's own collision (its floors, stairs, walls, roofs and slab undersides, <see cref="FloorSurfaces.Cuts"/>) cuts into
    /// <paramref name="prism"/> by more than <paramref name="tolerance"/>; false for a volume made by hand.
    /// </summary>
    public bool StructureCuts(FootprintPrism prism, float tolerance) => _floors?.Value.Cuts(prism, tolerance) == true;

    /// <summary>
    /// The floor surface's upward normal under <paramref name="point"/> on floors up to <paramref name="floor"/>
    /// (<see cref="FloorSurfaces.NormalBelow"/>); null for none.
    /// </summary>
    public Vector3? FloorNormal(Vector3 point, int floor, float reach) => _floors?.Value.NormalBelow(point, floor, reach);

    /// <summary>
    /// A movement pick on this building's collision at or below the viewed floor.
    /// </summary>
    public (float Distance, Vector3 Position)? MoveHit(Vector3 origin, Vector3 direction, int floor, float maxDistance)
    {
        if (Footprint.Length < 3 || _floors is null) return null;
        var min = new Vector3(Footprint.Min(p => p.X), YMin, Footprint.Min(p => p.Y));
        var max = new Vector3(Footprint.Max(p => p.X), YMax, Footprint.Max(p => p.Y));
        return FloorSurfaces.RayBox(origin, direction, min, max, maxDistance)
            ? _floors.Value.MoveHit(origin, direction, floor, maxDistance) : null;
    }

    /// <summary>
    /// The volume of a building placed at <paramref name="position"/> / <paramref name="rotation"/>, or null when it has no <c>interior mask</c> part
    /// with a readable collision file, or is a gateway and <paramref name="gateways"/> is false (a gateway is walked under, not entered: no room, but
    /// its hull still answers the audio ray, <see cref="IsGateway"/>). <paramref name="collision"/> loads a record's collision path (shared, cached).
    /// </summary>
    public static InteriorVolume? For(GameDatabase db, GameRecord building, string placementId, Vector3 position, Quaternion rotation,
        InteriorCollisions collision, bool gateways = false)
    {
        bool gateway = building.GetBool("is gateway");
        if (gateway && !gateways) return null;
        float scale = building.GetFloat("scale", 1);
        var node = WorldObjectLayout.InstanceTransform(position, rotation, new Vector3(scale > 0 ? scale : 1));
        var points = new List<Vector2>();
        var meshes = new List<string>();
        var corners = new List<Vector3>();
        var pieces = new List<Piece>();
        float yMin = float.MaxValue, yMax = float.MinValue;
        foreach (var r in building.GetReferences("interior mask"))
        {
            if (db.Find(r.TargetStringId) is not { Type: FcsRecordType.BuildingPart } mask) continue;
            if (mask.GetPath("phs or mesh") is { } meshPath && meshPath.Contains(".mesh", StringComparison.OrdinalIgnoreCase)) meshes.Add(meshPath);
            if (collision.Mesh(mask.GetPath("xml collision")) is not { } shape) continue;
            foreach (int i in shape.Indices) corners.Add(Vector3.Transform(shape.Vertices[i], node));
            pieces.AddRange(PiecesOf(shape, node));
            foreach (var v in shape.Vertices)
            {
                var w = Vector3.Transform(v, node);
                points.Add(new Vector2(w.X, w.Z));
                yMin = MathF.Min(yMin, w.Y);
                yMax = MathF.Max(yMax, w.Y);
            }
        }
        if (points.Count < 3) return null;
        var hull = ConvexHull2(points);
        if (hull.Length < 3) return null;
        return new InteriorVolume(placementId, hull, yMin, yMax)
        {
            Ambience = building.GetInt("interior ambience"),
            Integrity = building.GetFloat("interior integrity", 100),
            Cavernous = building.GetBool("cavernous"),
            IsGateway = gateway,
            MaskMeshes = meshes,
            Node = node,
            _maskCorners = [.. corners],
            _pieces = [.. pieces],
            _floors = new Lazy<FloorSurfaces>(() => FloorSurfaces.For(db, building, placementId, position, rotation, node, collision)),
        };
    }

    /// <summary>
    /// The trigger's shapes in <paramref name="mesh"/> placed with <paramref name="node"/>, one piece each (a mesh without its shapes' starts: one
    /// piece of all of it).
    /// </summary>
    static IEnumerable<Piece> PiecesOf(CollisionMesh mesh, Matrix4x4 node)
    {
        int[] starts = mesh.ShapeStarts.Length > 0 ? mesh.ShapeStarts : [0];
        for (int s = 0; s < starts.Length; s++)
        {
            int end = s + 1 < starts.Length ? starts[s + 1] : mesh.Vertices.Length;
            var placed = mesh.Vertices[starts[s]..end].Select(v => Vector3.Transform(v, node)).ToArray();
            if (Piece.Of(placed) is { } piece) yield return piece;
        }
    }

    /// <summary>
    /// Andrew's monotone chain: the convex hull of <paramref name="points"/>, counter-clockwise (X right, Y = world Z).
    /// </summary>
    public static Vector2[] ConvexHull2(List<Vector2> points)
    {
        var p = points.Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToArray();
        if (p.Length < 3) return p;
        var h = new Vector2[p.Length * 2];
        int k = 0;
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        for (int i = 0; i < p.Length; i++)
        {
            while (k >= 2 && Cross(h[k - 2], h[k - 1], p[i]) <= 0) k--;
            h[k++] = p[i];
        }
        for (int i = p.Length - 2, t = k + 1; i >= 0; i--)
        {
            while (k >= t && Cross(h[k - 2], h[k - 1], p[i]) <= 0) k--;
            h[k++] = p[i];
        }
        return h[..(k - 1)];
    }
}

/// <summary>
/// The floor collision of a building in the interior view, in world space, to tell which floor a character stands on (docs/formats/zones.md "Interior
/// view"): the shapes of its ordinary parts (collision groups 9..12, floor 0..3) and its stairs (15..18), the groups the game's downward ray tests.
/// </summary>
public sealed partial class FloorSurfaces
{
    /// <summary>
    /// One part's triangles (three corners each) with its floor, whether it is stairs, its height range, its X/Z box and its triangles binned by X/Z
    /// (<see cref="TriangleGrid"/>).
    /// </summary>
    sealed record Surface(int Floor, bool Stairs, Vector3[] Corners, float YMin, float YMax, Vector2 Min, Vector2 Max)
    {
        /// <summary>
        /// The part's triangles binned by X/Z.
        /// </summary>
        public TriangleGrid Grid { get; } = new(Corners, Min, Max);
    }

    /// <summary>
    /// The floor parts.
    /// </summary>
    readonly List<Surface> _surfaces = [];
    /// <summary>
    /// The <c>is unwalkable roof</c> parts: no floor, only cover (<see cref="LowestAbove"/>).
    /// </summary>
    readonly List<Surface> _roofs = [];
    /// <summary>
    /// The solids' flat undersides left out of the floors: a slab's bottom, what a ceiling ghost hangs from (<see cref="CeilingAbove"/>).
    /// </summary>
    readonly List<Surface> _undersides = [];

    /// <summary>
    /// How far above the feet the downward ray starts (game: 10 units above the character).
    /// </summary>
    public const float RayStart = 10;

    /// <summary>
    /// The share of a stairs shape's height from the top within which a hit counts as the floor above (game: the top 30 %).
    /// </summary>
    public const float StairsTop = 0.3f;

    /// <summary>
    /// The surfaces of a building placed with <paramref name="node"/> (position, rotation, scale; part collision is in building space): its parts
    /// with their <c>interior</c> list, leaving out doors, <c>passable</c> parts, <c>is unwalkable roof</c> parts, a <c>path mode</c> IGNORE
    /// building's parts and furniture (the groups the ray does not test). Parts without a mesh are not laid out, so their collision is missing
    /// (engine limit). A <paramref name="destroyed"/> building (a ruin) is laid out as the navmesh lays it out: its parts' <c>destroyed collision</c>
    /// where they have one, no <c>interior</c> list.
    /// </summary>
    public static FloorSurfaces For(GameDatabase db, GameRecord building, string placementId, Vector3 position, Quaternion rotation, Matrix4x4 node,
        InteriorCollisions collision, bool destroyed = false)
    {
        var result = new FloorSurfaces();
        // IGNORE: no floor groups
        if (building.GetInt("path mode", 2) == 0 || building.GetBool("is exterior furniture") || building.GetBool("is interior furniture"))
            return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var state = new BuildingState(destroyed) { WithInterior = !destroyed };
        foreach (var p in WorldObjectLayout.Building(db, building, placementId, position, rotation, state))
        {
            var part = p.Source;
            if (p.Owner != building || part.GetBool("passable") || !seen.Add(part.StringId)) continue;
            if (collision.Mesh(p.CollisionPath(destroyed)) is not { } mesh) continue;
            // A solid's flat underside (outward and facing down: a slab's or steps' bottom) is no floor; a wall hit dropped onto it put the goal
            // under the building.
            bool roof = part.GetBool("is unwalkable roof");
            var kept = new List<Vector3>(mesh.Indices.Length);
            var under = new List<Vector3>();
            for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
            {
                var a = Vector3.Transform(mesh.Vertices[mesh.Indices[t]], node);
                var b = Vector3.Transform(mesh.Vertices[mesh.Indices[t + 1]], node);
                var c = Vector3.Transform(mesh.Vertices[mesh.Indices[t + 2]], node);
                var normal = Vector3.Cross(b - a, c - a);
                bool underside = !roof && mesh.Outward[t / 3] && normal.Y < 0 && -normal.Y >= normal.Length() * MathF.Cos(40 * MathF.PI / 180);
                var list = underside ? under : kept;
                list.Add(a); list.Add(b); list.Add(c);
            }
            int floor = Math.Clamp(part.GetInt("building floor"), 0, 3);
            if (Of(floor, part.GetBool("is stairs"), kept) is { } surface) (roof ? result._roofs : result._surfaces).Add(surface);
            if (Of(floor, false, under) is { } bottom) result._undersides.Add(bottom);
        }
        return result;
    }

    /// <summary>
    /// The surface of <paramref name="kept"/>'s triangles (three corners each) with its height range and X/Z box; null when there are none.
    /// </summary>
    static Surface? Of(int floor, bool stairs, List<Vector3> kept)
    {
        if (kept.Count == 0) return null;
        var corners = kept.ToArray();
        var min = new Vector2(corners.Min(c => c.X), corners.Min(c => c.Z));
        var max = new Vector2(corners.Max(c => c.X), corners.Max(c => c.Z));
        return new Surface(floor, stairs, corners, corners.Min(c => c.Y), corners.Max(c => c.Y), min, max);
    }

    /// <summary>
    /// The floor under <paramref name="feet"/>: a ray from <see cref="RayStart"/> above straight down, the highest hit. An ordinary part gives its
    /// floor; stairs give the floor above theirs when the hit is in their top <see cref="StairsTop"/>, else their own (game: hits on stairs lower
    /// down give nothing and the character keeps its floor; here, without a kept floor, their own). Nothing hit: 0.
    /// </summary>
    public int FloorAt(Vector3 feet)
    {
        float top = feet.Y + RayStart, best = float.NegativeInfinity;
        int floor = 0;
        var q = new Vector2(feet.X, feet.Z);
        foreach (var s in _surfaces)
        {
            if (q.X < s.Min.X || q.Y < s.Min.Y || q.X > s.Max.X || q.Y > s.Max.Y || s.YMin > top) continue;
            foreach (int i in s.Grid.At(q))
            {
                if (HeightAt(s.Corners[i], s.Corners[i + 1], s.Corners[i + 2], q) is not { } y || y > top || y <= best) continue;
                best = y;
                floor = !s.Stairs ? s.Floor
                    : Math.Min(3, s.Floor + (s.YMax - s.YMin > 1e-3f && (s.YMax - y) <= StairsTop * (s.YMax - s.YMin) ? 1 : 0));
            }
        }
        return floor;
    }

    /// <summary>
    /// Whether a triangle of the parts (ordinary, stairs, unwalkable roof or a slab's underside: every part with its walls) cuts into
    /// <paramref name="prism"/> by more than <paramref name="tolerance"/> (<see cref="FootprintPrism.Cuts"/>).
    /// </summary>
    public bool Cuts(FootprintPrism prism, float tolerance)
    {
        foreach (var s in _surfaces.Concat(_roofs).Concat(_undersides))
        {
            if (!prism.Reaches(s.Min, s.Max, s.YMin, s.YMax)) continue;

            for (int i = 0; i + 2 < s.Corners.Length; i += 3)
                if (prism.Cuts(s.Corners[i], s.Corners[i + 1], s.Corners[i + 2], tolerance)) return true;
        }

        return false;
    }

    /// <summary>
    /// The height of triangle (a, b, c) above <paramref name="q"/> (X/Z), or null when q is outside it or the triangle is vertical.
    /// </summary>
    static float? HeightAt(Vector3 a, Vector3 b, Vector3 c, Vector2 q)
    {
        float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(d) < 1e-6f) return null;
        float u = ((b.Z - c.Z) * (q.X - c.X) + (c.X - b.X) * (q.Y - c.Z)) / d;
        float v = ((c.Z - a.Z) * (q.X - c.X) + (a.X - c.X) * (q.Y - c.Z)) / d;
        float w = 1 - u - v;
        const float e = -1e-4f;
        if (u < e || v < e || w < e) return null;
        return u * a.Y + v * b.Y + w * c.Y;
    }
}

/// <summary>
/// A collision file's shapes triangulated in Kenshi's Y-up building space: vertices and three indices per triangle. <paramref name="Outward"/> per
/// triangle: wound outward (a box, capsule or convex hull, <see cref="CollisionTriangulator"/>); a triangle mesh's keeps the file's winding and
/// is false.
/// </summary>
public sealed record CollisionMesh(Vector3[] Vertices, int[] Indices, bool[] Outward)
{
    /// <summary>
    /// Per shape of the file, in file order, the index of its first vertex in <see cref="Vertices"/>.
    /// </summary>
    public int[] ShapeStarts { get; init; } = [];
}

/// <summary>
/// The collision files that building parts name (the <c>interior mask</c> hulls, the floor parts), read and triangulated once each; safe from several
/// threads.
/// </summary>
public sealed class InteriorCollisions(GameInstall install)
{
    /// <summary>
    /// The collision files read, by record path (null for a missing or unreadable one).
    /// </summary>
    readonly ConcurrentDictionary<string, CollisionMesh?> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every triangulated vertex of the file's shapes, or null when the path is empty or the file is missing or unreadable.
    /// </summary>
    public Vector3[]? Vertices(string recordPath) => Mesh(recordPath)?.Vertices;

    /// <summary>
    /// The file's triangles, or null when the path is empty or the file is missing, unreadable or has no triangles.
    /// </summary>
    public CollisionMesh? Mesh(string recordPath) =>
        string.IsNullOrWhiteSpace(recordPath) ? null : _files.GetOrAdd(recordPath, Load);

    /// <summary>
    /// Every triangulated vertex of the file's shapes in world axes, with each triangle's outward flag (true for all but triangle meshes); null when
    /// the path does not resolve, the file does not read or it has no triangles.
    /// </summary>
    CollisionMesh? Load(string recordPath)
    {
        if (CollisionPaths.Resolve(install, recordPath) is not { } path) return null;
        CollisionFile file;
        try { file = CollisionFile.ReadFile(path); }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException or FormatException) { return null; }
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var outward = new List<bool>();
        var starts = new List<int>();
        foreach (var s in file.Shapes)
        {
            var v = new List<Vector3>();
            var i = new List<int>();
            int added = CollisionTriangulator.Triangulate(s, v, i);
            int offset = vertices.Count;
            if (v.Count > 0) starts.Add(offset);
            vertices.AddRange(v.Select(CollisionTriangulator.ToWorldAxes));
            indices.AddRange(i.Select(x => x + offset));
            outward.AddRange(Enumerable.Repeat(s.Kind != CollisionShapeKind.TriangleMesh, added));
        }
        return vertices.Count == 0 ? null : new CollisionMesh([.. vertices], [.. indices], [.. outward]) { ShapeStarts = [.. starts] };
    }
}