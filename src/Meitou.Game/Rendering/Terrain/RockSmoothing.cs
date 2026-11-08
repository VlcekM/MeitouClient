using System.Numerics;

namespace Meitou.Rendering;

/// <summary>
/// The rocks' smoothing (docs/render-terrain.md, "Rocks"), a Meitou look the game does not have: the TERRAIN-mode meshes' facets. Many of them are
/// flat-shaded (every corner its face's normal), so the shading is welded at decode: a corner takes the faces round its position that turn less than
/// <see cref="CreaseDegrees"/> from its own, so ridges stay sharp. Each position also gets one geometry normal (all its faces), kept in the vertex's
/// tangent slot with w <see cref="Tag"/> (the terrain material reads no tangents); near the eye the meshes are tessellated and their triangles curved to
/// those normals (Phong tessellation), and as both sides of an edge curve it from the same two ends, nothing cracks.
/// </summary>
public static class RockSmoothing
{
    /// <summary>Read when a mesh is decoded (the welded normals are made then or not at all).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Whether the rocks are drawn tessellated (where the device has tessellation shaders); the Tab panel toggles it. Off by default (on from
    /// the Ultra preset): the welded normals already smooth the shading.</summary>
    public static bool TessellateActive { get; set; }

    /// <summary>The Phong curve's share (0: the flat triangles, 1: the full curve; the viewer's <c>--relief Roundness=</c>).</summary>
    public static float Roundness { get; set; } = 0.75f;

    /// <summary>Faces turning more than this from a corner's own keep their edge in the shading.</summary>
    public const float CreaseDegrees = 60;

    /// <summary>The tangent's w that marks its xyz as the geometry normal (a real tangent's w is ±1).</summary>
    public const float Tag = 2;

    /// <summary>Welds <paramref name="model"/>'s normals in place: the shading normals where a position's corners disagree, and every vertex's geometry normal.</summary>
    public static void Apply(Model model)
    {
        // Positions shared across the parts, so a seam between submeshes curves alike from both sides.
        var ids = new Dictionary<Vector3, int>();
        int Id(Vector3 p)
        {
            if (!ids.TryGetValue(p, out int id)) ids[p] = id = ids.Count;
            return id;
        }
        var vertexIds = new int[model.Parts.Count][];
        var faceNormals = new Vector3[model.Parts.Count][];
        for (int k = 0; k < model.Parts.Count; k++)
        {
            var part = model.Parts[k];
            var vid = vertexIds[k] = new int[part.Vertices.Length];
            for (int i = 0; i < vid.Length; i++) vid[i] = Id(part.Vertices[i].Position);
            var idx = part.Indices;
            var fn = faceNormals[k] = new Vector3[idx.Length / 3];
            for (int t = 0; t < fn.Length; t++)
            {
                Vector3 a = part.Vertices[idx[3 * t]].Position, b = part.Vertices[idx[3 * t + 1]].Position, c = part.Vertices[idx[3 * t + 2]].Position;
                fn[t] = Vector3.Cross(b - a, c - a);   // twice the area long: larger faces count more
            }
        }
        // The faces round each position.
        var around = new List<Vector3>[ids.Count];
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
        // Whether a position's corners already agree (an authored smooth mesh keeps its normals), and the geometry normal.
        var first = new Vector3?[ids.Count];
        var agree = new bool[ids.Count];
        Array.Fill(agree, true);
        foreach (var (part, vid) in model.Parts.Zip(vertexIds))
            for (int i = 0; i < vid.Length; i++)
            {
                var n = part.Vertices[i].Normal;
                if (!Usable(n)) { agree[vid[i]] = false; continue; }
                n = Vector3.Normalize(n);
                if (first[vid[i]] is not { } f) first[vid[i]] = n;
                else if (Vector3.Dot(f, n) < 0.9995f) agree[vid[i]] = false;
            }
        var curve = new Vector3[ids.Count];
        for (int id = 0; id < curve.Length; id++)
        {
            var sum = Vector3.Zero;
            foreach (var n in around[id]) sum += n;
            curve[id] = agree[id] && first[id] is { } f ? f : Usable(sum) ? Vector3.Normalize(sum) : first[id] ?? Vector3.UnitY;
        }
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

    static bool Usable(Vector3 n) => n.LengthSquared() is > 1e-12f and < float.PositiveInfinity;
}
