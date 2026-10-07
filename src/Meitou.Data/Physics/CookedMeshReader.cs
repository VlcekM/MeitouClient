using System.Buffers.Binary;
using System.Numerics;

namespace Meitou.Data.Physics;

/// <summary>
/// Clean-room decoders of PhysX 2.8 cooked meshes as stored in the NxuStream XML files (layouts in docs/formats/collision.md):
/// the vertices and triangle indices of a triangle mesh, and the hull vertices of a convex mesh.
/// </summary>
public static class CookedMeshReader
{
    static ReadOnlySpan<byte> TriangleMagic => "NXS\u0001MESH"u8;
    static ReadOnlySpan<byte> ConvexMagic => "NXS\u0001CVXM"u8;
    static ReadOnlySpan<byte> HullTag => "ICE\u0001CVHL"u8;

    const int IndicesAre8Bit = 0x08, IndicesAre16Bit = 0x10;

    public static CookedMesh ReadTriangleMesh(ReadOnlySpan<byte> data)
    {
        if (!data.StartsWith(TriangleMagic)) throw new InvalidDataException("Not a cooked triangle mesh (NXS MESH)");
        int p = 8;
        int version = I32(data, ref p);
        if (version != 1) throw new InvalidDataException($"Cooked triangle mesh version {version}");
        int flags = I32(data, ref p);
        p += 4 + 4 + 4; // convex edge threshold, height field axis, height field extent
        int vertexCount = I32(data, ref p);
        int triangleCount = I32(data, ref p);
        if (vertexCount < 0 || triangleCount < 0 || vertexCount > 1 << 20 || triangleCount > 1 << 20) throw new InvalidDataException("Implausible mesh counts");
        var vertices = Vertices(data, ref p, vertexCount);
        int width = (flags & IndicesAre8Bit) != 0 ? 1 : (flags & IndicesAre16Bit) != 0 ? 2 : 4;
        if (p + (long)triangleCount * 3 * width > data.Length) throw new InvalidDataException("Cooked triangle mesh is truncated");
        var indices = new int[triangleCount * 3];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = width switch
            {
                1 => data[p],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(data[p..]),
                _ => BinaryPrimitives.ReadInt32LittleEndian(data[p..]),
            };
            p += width;
            if ((uint)indices[i] >= (uint)vertexCount) throw new InvalidDataException($"Triangle index {indices[i]} beyond {vertexCount} vertices");
        }
        return new CookedMesh(vertices, indices);
    }

    /// <summary>The convex mesh's hull vertices. The <c>CVHL</c> block is found by its tag (a <c>CLHL</c> block precedes it).</summary>
    public static CookedMesh ReadConvex(ReadOnlySpan<byte> data)
    {
        if (!data.StartsWith(ConvexMagic)) throw new InvalidDataException("Not a cooked convex mesh (NXS CVXM)");
        int at = data[8..].IndexOf(HullTag);
        if (at < 0) throw new InvalidDataException("Convex mesh has no CVHL block");
        int p = 8 + at + HullTag.Length;
        int version = I32(data, ref p);
        if (version != 5) throw new InvalidDataException($"Convex hull block version {version}");
        int count = I32(data, ref p);
        if (count < 0 || count > 1 << 16) throw new InvalidDataException("Implausible hull vertex count");
        p += 5 * 4; // edge, polygon and index counts, not needed
        return new CookedMesh(Vertices(data, ref p, count), []);
    }

    static Vector3[] Vertices(ReadOnlySpan<byte> data, ref int p, int count)
    {
        if (p + (long)count * 12 > data.Length) throw new InvalidDataException("Cooked mesh is truncated");
        var v = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            v[i] = new Vector3(F32(data, p), F32(data, p + 4), F32(data, p + 8));
            p += 12;
            if (!float.IsFinite(v[i].X) || !float.IsFinite(v[i].Y) || !float.IsFinite(v[i].Z)) throw new InvalidDataException("Non-finite mesh vertex");
        }
        return v;
    }

    static int I32(ReadOnlySpan<byte> d, ref int p)
    {
        if (p + 4 > d.Length) throw new InvalidDataException("Cooked mesh is truncated");
        int v = BinaryPrimitives.ReadInt32LittleEndian(d[p..]);
        p += 4;
        return v;
    }

    static float F32(ReadOnlySpan<byte> d, int p) => BinaryPrimitives.ReadSingleLittleEndian(d[p..]);
}
