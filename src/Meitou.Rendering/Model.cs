using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;

namespace Meitou.Rendering;

/// <summary>The viewer's vertex: Ogre's per-source buffers deinterleaved into one fixed layout.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    /// <summary>xyz tangent, w handedness (bitangent = cross(normal, tangent) * w).</summary>
    public Vector4 Tangent;
    /// <summary>Vertex colour RGBA 0–1 (white when the mesh has none).</summary>
    public Vector4 Colour;
    /// <summary>Four bone handles, one byte each.</summary>
    public uint Bones;
    public Vector4 Weights;

    public static readonly int Size = Marshal.SizeOf<Vertex>();
}

public sealed class ModelPart
{
    public required int SubMeshIndex { get; init; }
    public required string MaterialName { get; init; }
    public required Vertex[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public bool HasUv { get; init; }
    public bool HasTangents { get; init; }
    public bool HasColours { get; init; }
    public bool Skinned { get; init; }
    /// <summary>The triangle list with the alpha-tested cards cut to their opaque outline (<see cref="FoliageCardTrimmer"/>), indexing <see cref="Vertices"/> (which then also hold the new corners after the originals); null: none.</summary>
    public uint[]? TrimmedIndices { get; init; }
}

/// <summary>A mesh ready for upload: triangle lists per submesh, bounds, and skinning weights built from bone assignments.</summary>
public sealed class Model
{
    public List<ModelPart> Parts { get; } = [];
    public Vector3 Min { get; private set; }
    public Vector3 Max { get; private set; }
    public List<string> Warnings { get; } = [];

    public Vector3 Center => (Min + Max) / 2;
    public float Radius => Math.Max((Max - Min).Length() / 2, 1e-3f);

    /// <summary>Bounds of the vertices posed with <paramref name="skin"/> (as the vertex shader does), for framing an animated pose.</summary>
    public (Vector3 Min, Vector3 Max) PosedBounds(Matrix4x4[] skin)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var part in Parts)
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
        return Parts.Count == 0 ? (Min, Max) : (min, max);
    }

    /// <param name="boneCount">Bones in the skeleton the mesh will be skinned with, or null to drop skinning.</param>
    public static Model Build(OgreMesh mesh, int? boneCount)
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

            var part = BuildPart(s, sub, vd, assignments, boneCount, indices, model.Warnings);
            foreach (var v in part.Vertices)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            model.Parts.Add(part);
        }
        if (model.Parts.Count == 0) { min = Vector3.Zero; max = Vector3.One; }
        model.Min = min;
        model.Max = max;
        return model;
    }

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
                    if (i % 2 == 0) list.AddRange([src[i - 2], src[i - 1], src[i]]);
                    else list.AddRange([src[i - 1], src[i - 2], src[i]]);
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

    static ModelPart BuildPart(int s, OgreSubMesh sub, OgreVertexData vd, List<OgreBoneAssignment> assignments, int? boneCount, uint[] indices, List<string> warnings)
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
                else if (binormals is not null)
                {
                    var b = new Vector3(binormals[i * 3], binormals[i * 3 + 1], binormals[i * 3 + 2]);
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
        if (skinned)
        {
            int dropped = 0;
            foreach (var group in assignments.GroupBy(a => a.VertexIndex))
            {
                if (group.Key >= n) { dropped++; continue; }
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
            if (dropped > 0) warnings.Add($"submesh {s}: {dropped} bone assignments name bones past the skeleton's {boneCount} and were dropped");
        }

        return new ModelPart
        {
            SubMeshIndex = s, MaterialName = sub.MaterialName, Vertices = verts, Indices = indices,
            HasUv = uvs is not null, HasTangents = tangents is not null, HasColours = colours is not null, Skinned = skinned,
        };
    }

    static float[]? Floats(OgreVertexData vd, OgreVertexSemantic semantic, out int components)
    {
        components = 0;
        if (vd.Find(semantic) is not { } e) return null;
        components = e.Type.ComponentCount();
        return vd.ReadFloats(e);
    }
}
