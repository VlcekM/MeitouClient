using System.Numerics;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// The rocks' smoothing (docs/render-terrain.md, "Rocks"), a Meitou look the game does not have: the TERRAIN-mode meshes' facets. Many of them are
/// flat-shaded (every corner its face's normal), so the shading is welded at decode: a corner takes the faces round its position that turn less than
/// <see cref="CreaseDegrees"/> from its own, so ridges stay sharp. Each position also gets one geometry normal (all its faces), kept in the vertex's
/// tangent slot with w <see cref="Tag"/> (the terrain material reads no tangents); near the eye the meshes are tessellated and their triangles curved
/// to those normals (Phong tessellation), and as both sides of an edge curve it from the same two ends, nothing cracks.
/// </summary>
public static class RockSmoothing
{
    /// <summary>
    /// Read when a mesh is decoded (the welded normals are made then or not at all).
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the rocks are drawn tessellated (where the device has tessellation shaders); the Tab panel toggles it. Off by default (on from the
    /// Ultra preset): the welded normals already smooth the shading.
    /// </summary>
    public static bool TessellateActive { get; set; }

    /// <summary>
    /// The Phong curve's share (0: the flat triangles, 1: the full curve; the viewer's <c>--relief Roundness=</c>).
    /// </summary>
    public static float Roundness { get; set; } = 0.75f;

    /// <summary>
    /// Faces turning more than this from a corner's own keep their edge in the shading.
    /// </summary>
    public const float CreaseDegrees = 60;

    /// <summary>
    /// The tangent's w that marks its xyz as the geometry normal (a real tangent's w is ±1).
    /// </summary>
    public const float Tag = 2;

    /// <summary>
    /// Welds <paramref name="model"/>'s normals in place: the shading normals where a position's corners disagree, and every vertex's geometry
    /// normal.
    /// </summary>
    public static void Apply(Model model)
    {
        var ids = new Dictionary<Vector3, int>();
        var vertexIds = new int[model.Parts.Count][];
        var faceNormals = new Vector3[model.Parts.Count][];
        IndexPositions(model, ids, vertexIds, faceNormals);
        var around = FacesAround(model, ids.Count, vertexIds, faceNormals);
        var first = new Vector3?[ids.Count];
        var agree = new bool[ids.Count];
        Array.Fill(agree, true);
        CornerAgreement(model, vertexIds, first, agree);
        var curve = GeometryNormals(ids.Count, around, agree, first);
        WeldCorners(model, vertexIds, around, agree, curve);
    }

    /// <summary>
    /// Numbers the distinct positions (<paramref name="ids"/>) and gives each part its vertices' position ids and its faces' normals.
    /// </summary>
    static void IndexPositions(Model model, Dictionary<Vector3, int> ids, int[][] vertexIds, Vector3[][] faceNormals)
    {
        // Positions shared across the parts, so a seam between submeshes curves alike from both sides.
        int Id(Vector3 p)
        {
            if (!ids.TryGetValue(p, out int id)) ids[p] = id = ids.Count;
            return id;
        }
        for (int k = 0; k < model.Parts.Count; k++)
        {
            var part = model.Parts[k];
            var vid = vertexIds[k] = new int[part.Vertices.Length];
            for (int i = 0; i < vid.Length; i++) vid[i] = Id(part.Vertices[i].Position);
            var idx = part.Indices;
            var fn = faceNormals[k] = new Vector3[idx.Length / 3];
            for (int t = 0; t < fn.Length; t++)
            {
                Vector3 a = part.Vertices[idx[3 * t]].Position, b = part.Vertices[idx[3 * t + 1]].Position,
                    c = part.Vertices[idx[3 * t + 2]].Position;
                fn[t] = Vector3.Cross(b - a, c - a);   // twice the area long: larger faces count more
            }
        }
    }

    /// <summary>
    /// The non-degenerate face normals round each position.
    /// </summary>
    static List<Vector3>[] FacesAround(Model model, int positionCount, int[][] vertexIds, Vector3[][] faceNormals)
    {
        // The faces round each position.
        var around = new List<Vector3>[positionCount];
        for (int i = 0; i < around.Length; i++) around[i] = [];
        for (int k = 0; k < model.Parts.Count; k++)
        {
            var idx = model.Parts[k].Indices;
            for (int t = 0; t < faceNormals[k].Length; t++)
            {
                var n = faceNormals[k][t];
                if (!(n.LengthSquared() > 1e-20f)) continue;
                around[vertexIds[k][idx[3 * t]]].Add(n);
                around[vertexIds[k][idx[3 * t + 1]]].Add(n);
                around[vertexIds[k][idx[3 * t + 2]]].Add(n);
            }
        }
        return around;
    }

    /// <summary>
    /// Clears <paramref name="agree"/> where a position's corner normals disagree, and records its first usable normal.
    /// </summary>
    static void CornerAgreement(Model model, int[][] vertexIds, Vector3?[] first, bool[] agree)
    {
        // Whether a position's corners already agree (an authored smooth mesh keeps its normals), and the geometry normal.
        foreach (var (part, vid) in model.Parts.Zip(vertexIds))
            for (int i = 0; i < vid.Length; i++)
            {
                var n = part.Vertices[i].Normal;
                if (!Usable(n))
                {
                    agree[vid[i]] = false;
                    continue;
                }
                n = Vector3.Normalize(n);
                if (first[vid[i]] is not { } f) first[vid[i]] = n;
                else if (Vector3.Dot(f, n) < 0.9995f) agree[vid[i]] = false;
            }
    }

    /// <summary>
    /// Each position's geometry normal: the sum of its faces, or its first corner normal where that is unusable.
    /// </summary>
    static Vector3[] GeometryNormals(int positionCount, List<Vector3>[] around, bool[] agree, Vector3?[] first)
    {
        var curve = new Vector3[positionCount];
        for (int id = 0; id < curve.Length; id++)
        {
            var sum = Vector3.Zero;
            foreach (var n in around[id]) sum += n;
            curve[id] = agree[id] && first[id] is { } f ? f : Usable(sum) ? Vector3.Normalize(sum) : first[id] ?? Vector3.UnitY;
        }
        return curve;
    }

    /// <summary>
    /// Sets every vertex's tangent to its position's geometry normal, and the normals of the corners that disagree to the faces within the crease.
    /// </summary>
    static void WeldCorners(Model model, int[][] vertexIds, List<Vector3>[] around, bool[] agree, Vector3[] curve)
    {
        float crease = MathF.Cos(CreaseDegrees * MathF.PI / 180);
        foreach (var (part, vid) in model.Parts.Zip(vertexIds))
            for (int i = 0; i < vid.Length; i++)
            {
                ref var v = ref part.Vertices[i];
                int id = vid[i];
                if (!agree[id])
                {
                    // The corner's own direction: its authored normal, or where that is unusable the position's geometry normal.
                    var own = Usable(v.Normal) ? Vector3.Normalize(v.Normal) : curve[id];
                    var sum = Vector3.Zero;
                    foreach (var n in around[id])
                        if (Vector3.Dot(Vector3.Normalize(n), own) >= crease) sum += n;
                    v.Normal = Usable(sum) ? Vector3.Normalize(sum) : own;
                }
                v.Tangent = new Vector4(curve[id], Tag);
            }
    }

    /// <summary>
    /// A normal that can be normalised (not zero, not infinite or NaN).
    /// </summary>
    static bool Usable(Vector3 n) => n.LengthSquared() is > 1e-12f and < float.PositiveInfinity;
}