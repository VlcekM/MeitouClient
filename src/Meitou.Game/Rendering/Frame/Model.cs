using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Foliage;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The viewer's vertex: Ogre's per-source buffers deinterleaved into one fixed layout.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Vertex
{
    /// <summary>
    /// The vertex position in the mesh's space.
    /// </summary>
    public Vector3 Position;

    /// <summary>
    /// The vertex normal.
    /// </summary>
    public Vector3 Normal;

    /// <summary>
    /// The texture coordinates of the first set.
    /// </summary>
    public Vector2 Uv;

    /// <summary>
    /// xyz tangent, w handedness (bitangent = cross(normal, tangent) * w).
    /// </summary>
    public Vector4 Tangent;

    /// <summary>
    /// Vertex colour RGBA 0–1 (white when the mesh has none).
    /// </summary>
    public Vector4 Colour;

    /// <summary>
    /// Four bone handles, one byte each.
    /// </summary>
    public uint Bones;

    /// <summary>
    /// The four bone weights matching <see cref="Bones"/>, normalised (all zero: not skinned).
    /// </summary>
    public Vector4 Weights;

    /// <summary>
    /// The struct's size in bytes.
    /// </summary>
    public static readonly int Size = Marshal.SizeOf<Vertex>();
}

/// <summary>
/// One submesh of a <see cref="Model"/>: its material and triangle list over its own vertices.
/// </summary>
public sealed class ModelPart
{
    /// <summary>
    /// The submesh's index in the Ogre mesh.
    /// </summary>
    public required int SubMeshIndex { get; init; }

    /// <summary>
    /// The material name the mesh gives the submesh.
    /// </summary>
    public required string MaterialName { get; init; }

    /// <summary>
    /// The submesh's vertices, deinterleaved into <see cref="Vertex"/>.
    /// </summary>
    public required Vertex[] Vertices { get; init; }

    /// <summary>
    /// The triangle list over <see cref="Vertices"/> (strips and fans converted).
    /// </summary>
    public required uint[] Indices { get; init; }

    /// <summary>
    /// Whether the vertex data has texture coordinates.
    /// </summary>
    public bool HasUv { get; init; }

    /// <summary>
    /// Whether the vertex data has tangents.
    /// </summary>
    public bool HasTangents { get; init; }

    /// <summary>
    /// Whether the vertex data has vertex colours.
    /// </summary>
    public bool HasColours { get; init; }

    /// <summary>
    /// Whether the vertices carry bone weights (built from the bone assignments for a skeleton).
    /// </summary>
    public bool Skinned { get; init; }

    /// <summary>
    /// The triangle list with the alpha-tested cards cut to their opaque outline (<see cref="FoliageCardTrimmer"/>), indexing <see cref="Vertices"/>
    /// (which then also hold the new corners after the originals); null: none.
    /// </summary>
    public uint[]? TrimmedIndices { get; init; }
}

/// <summary>
/// A mesh ready for upload: triangle lists per submesh, bounds, and skinning weights built from bone assignments.
/// </summary>
public sealed class Model
{
    /// <summary>
    /// The submeshes that are drawn, in mesh order (empty vertex data and unsupported operations left out).
    /// </summary>
    public List<ModelPart> Parts { get; } = [];

    /// <summary>
    /// The smallest corner of the vertices' bounds (the origin when there are no parts).
    /// </summary>
    public Vector3 Min { get; private set; }

    /// <summary>
    /// The largest corner of the vertices' bounds (1, 1, 1 when there are no parts).
    /// </summary>
    public Vector3 Max { get; private set; }

    /// <summary>
    /// What the build left out or could not read, one line each.
    /// </summary>
    public List<string> Warnings { get; } = [];

    /// <summary>
    /// The middle of the bounds.
    /// </summary>
    public Vector3 Center => (Min + Max) / 2;

    /// <summary>
    /// Half the bounds' diagonal (at least 0.001).
    /// </summary>
    public float Radius => Math.Max((Max - Min).Length() / 2, 1e-3f);

    /// <summary>
    /// Bounds of the vertices posed with <paramref name="skin"/> (as the vertex shader does), for framing an animated pose.
    /// </summary>
    public (Vector3 Min, Vector3 Max) PosedBounds(Matrix4x4[] skin)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var part in Parts)
        {
            foreach (var v in part.Vertices)
            {
                var p = v.Position;
                if (part.Skinned && v.Weights != Vector4.Zero)
                {
                    p = Vector3.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = k switch { 0 => v.Weights.X, 1 => v.Weights.Y, 2 => v.Weights.Z, _ => v.Weights.W };
                        int bone = (int)((v.Bones >> (8 * k)) & 0xFF);
                        if (w > 0 && bone < skin.Length) p += Vector3.Transform(v.Position, skin[bone]) * w;
                    }
                }

                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        return Parts.Count == 0 ? (Min, Max) : (min, max);
    }

    /// <summary>
    /// The model of <paramref name="mesh"/>: a part per drawable submesh with its vertices and indices, and the bounds of them all.
    /// </summary>
    /// <param name="boneCount">Bones in the skeleton the mesh will be skinned with, or null to drop skinning.</param>
    /// <param name="gameBinormals">Characters: the tangent's w makes cross(normal, tangent) × w the game's binormal, the mesh's BINORMAL × the
    /// stored w (1 when the tangent has three components; character.hlsl, skin.hlsl's skinning; docs/characters.md "Shading, poses and LOD").
    /// Otherwise the stored w is kept (objects).</param>
    /// <param name="tangentSign">Inventory icons (rtticons.hlsl): the tangent's own w, 1 when it has three components, the stored binormals ignored
    /// (the shader takes cross(tangent, normal) x w).</param>
    public static Model Build(OgreMesh mesh, int? boneCount, bool gameBinormals = false, bool tangentSign = false)
    {
        var model = new Model();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int s = 0; s < mesh.SubMeshes.Count; s++)
        {
            var sub = mesh.SubMeshes[s];
            var vd = sub.UseSharedVertices ? mesh.SharedVertexData : sub.VertexData;
            if (vd is null || vd.VertexCount == 0) continue;
            var assignments = sub.UseSharedVertices ? mesh.BoneAssignments : sub.BoneAssignments;
            var indices = Triangulate(sub, model.Warnings, s);
            if (indices is null) continue;

            var part = BuildPart(s, sub, vd, assignments, boneCount, indices, model.Warnings, gameBinormals, tangentSign);
            foreach (var v in part.Vertices)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            model.Parts.Add(part);
        }

        if (model.Parts.Count == 0)
        {
            min = Vector3.Zero;
            max = Vector3.One;
        }

        model.Min = min;
        model.Max = max;
        return model;
    }

    /// <summary>
    /// Submesh <paramref name="s"/>'s indices as a triangle list: a list as it is, a strip or fan converted; null (with a warning) for another
    /// operation.
    /// </summary>
    static uint[]? Triangulate(OgreSubMesh sub, List<string> warnings, int s)
    {
        var src = sub.Indices.Indices;
        switch (sub.Operation)
        {
            case OgreOperationType.TriangleList:
                return src;
            case OgreOperationType.TriangleStrip:
                {
                    var list = new List<uint>();
                    for (int i = 2; i < src.Length; i++)
                    {
                        if (i % 2 == 0) list.AddRange([src[i - 2], src[i - 1], src[i]]);
                        else list.AddRange([src[i - 1], src[i - 2], src[i]]);
                    }

                    return [.. list];
                }
            case OgreOperationType.TriangleFan:
                {
                    var list = new List<uint>();
                    for (int i = 2; i < src.Length; i++) list.AddRange([src[0], src[i - 1], src[i]]);
                    return [.. list];
                }
            default:
                warnings.Add($"submesh {s}: operation {sub.Operation} not drawn");
                return null;
        }
    }

    /// <summary>
    /// Submesh <paramref name="s"/>'s vertices read into <see cref="Vertex"/> and skinned from <paramref name="assignments"/>, with its triangle list
    /// <paramref name="indices"/>.
    /// </summary>
    static ModelPart BuildPart(int s, OgreSubMesh sub, OgreVertexData vd, List<OgreBoneAssignment> assignments, int? boneCount, uint[] indices,
        List<string> warnings, bool gameBinormals, bool tangentSign)
    {
        int n = (int)vd.VertexCount;
        var verts = new Vertex[n];
        var pos = vd.ReadPositions()!;
        float[]? normals = Floats(vd, OgreVertexSemantic.Normal, out int nc);
        float[]? uvs = Floats(vd, OgreVertexSemantic.TextureCoordinates, out int uc);
        float[]? tangents = Floats(vd, OgreVertexSemantic.Tangent, out int tc);
        float[]? binormals = Floats(vd, OgreVertexSemantic.Binormal, out _);
        var colourElement = vd.Find(OgreVertexSemantic.Diffuse);
        float[]? colours = colourElement is null ? null : vd.ReadFloats(colourElement);
        for (int i = 0; i < n; i++)
        {
            ref var v = ref verts[i];
            v.Position = pos[i];
            v.Normal = normals is null ? Vector3.UnitY : Vector3.Normalize(new Vector3(normals[i * nc], normals[i * nc + 1], normals[i * nc + 2]));
            if (float.IsNaN(v.Normal.X)) v.Normal = Vector3.UnitY;
            v.Uv = uvs is null ? Vector2.Zero : new Vector2(uvs[i * uc], uc > 1 ? uvs[i * uc + 1] : 0);
            if (tangents is not null)
            {
                var t = new Vector3(tangents[i * tc], tangents[i * tc + 1], tangents[i * tc + 2]);
                float w = 1;
                if (tc == 4) w = tangents[i * tc + 3] < 0 ? -1 : 1;
                if (binormals is not null && !tangentSign && (tc != 4 || gameBinormals))
                {
                    var b = new Vector3(binormals[i * 3], binormals[i * 3 + 1], binormals[i * 3 + 2]) * w;
                    w = Vector3.Dot(Vector3.Cross(v.Normal, t), b) < 0 ? -1 : 1;
                }
                v.Tangent = new Vector4(t, w);
            }
            if (colours is not null)
            {
                // ARGB (and the deprecated generic colour) is stored B, G, R, A in memory; ABGR is R, G, B, A.
                float c0 = colours[i * 4] / 255f, c1 = colours[i * 4 + 1] / 255f, c2 = colours[i * 4 + 2] / 255f, a = colours[i * 4 + 3] / 255f;
                v.Colour = colourElement!.Type == OgreVertexElementType.ColourAbgr ? new Vector4(c0, c1, c2, a) : new Vector4(c2, c1, c0, a);
            }
            else v.Colour = Vector4.One;
        }

        bool skinned = boneCount is not null && assignments.Count > 0;
        if (skinned) Skin(verts, assignments, boneCount, warnings, s);

        return new ModelPart
        {
            SubMeshIndex = s,
            MaterialName = sub.MaterialName,
            Vertices = verts,
            Indices = indices,
            HasUv = uvs is not null,
            HasTangents = tangents is not null,
            HasColours = colours is not null,
            Skinned = skinned,
        };
    }

    /// <summary>
    /// The four heaviest bone assignments of each vertex, normalised; assignments past the skeleton's <paramref name="boneCount"/> bones are
    /// dropped (with a warning), and a vertex left without any follows the lowest bone used.
    /// </summary>
    static void Skin(Vertex[] verts, List<OgreBoneAssignment> assignments, int? boneCount, List<string> warnings, int s)
    {
        int n = verts.Length;
        int dropped = 0;
        foreach (var group in assignments.GroupBy(a => a.VertexIndex))
        {
            if (group.Key >= n)
            {
                dropped++;
                continue;
            }

            var top = group.Where(a => a.BoneIndex < boneCount!.Value && a.BoneIndex < 256 && a.Weight > 0)
                .OrderByDescending(a => a.Weight).Take(4).ToArray();
            dropped += group.Count(a => a.BoneIndex >= boneCount!.Value);
            float sum = top.Sum(a => a.Weight);
            if (sum <= 0) continue;
            ref var v = ref verts[group.Key];
            uint packed = 0;
            var w = new float[4];
            for (int k = 0; k < top.Length; k++)
            {
                packed |= (uint)top[k].BoneIndex << (8 * k);
                w[k] = top[k].Weight / sum;
            }

            v.Bones = packed;
            v.Weights = new Vector4(w[0], w[1], w[2], w[3]);
        }

        if (dropped > 0)
            warnings.Add($"submesh {s}: {dropped} bone assignments name bones past the skeleton's {boneCount} and were dropped");

        // A vertex without assignments follows blend index 0 fully (Ogre's Mesh::compileBoneAssignments): the lowest bone the vertex data's
        // assignments use (docs/formats/ogre-mesh.md "Bone assignments"), so a hair's unweighted cards move with its head.
        int lowest = assignments
            .Where(a => a.BoneIndex < boneCount!.Value && a.BoneIndex < 256)
            .Select(a => (int)a.BoneIndex)
            .DefaultIfEmpty(-1)
            .Min();
        if (lowest < 0) return;
        for (int i = 0; i < n; i++)
        {
            if (verts[i].Weights == Vector4.Zero)
                (verts[i].Bones, verts[i].Weights) = ((uint)lowest, new Vector4(1, 0, 0, 0));
        }
    }

    /// <summary>
    /// The floats of <paramref name="semantic"/>'s element in <paramref name="vd"/> and its component count; null (0 components) when the data has
    /// none.
    /// </summary>
    static float[]? Floats(OgreVertexData vd, OgreVertexSemantic semantic, out int components)
    {
        components = 0;
        if (vd.Find(semantic) is not { } e) return null;
        components = e.Type.ComponentCount();
        return vd.ReadFloats(e);
    }
}