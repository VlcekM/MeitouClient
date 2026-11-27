using System.Buffers.Binary;
using System.Numerics;
using Meitou.Data.Ogre;

namespace Meitou.Tests.Ogre;

public class OgreMeshArmourFitTests
{
    /// <summary>
    /// Builds an in-memory Ogre mesh from a list of vertices (position + normal + UV) and triangle indices.
    /// </summary>
    static OgreMesh MakeMesh(Vector3[] positions, Vector3[] normals, uint[] indices, IReadOnlyList<OgreBoneAssignment>? bones = null)
    {
        const int stride = 32;
        var data = new byte[positions.Length * stride];
        for (int i = 0; i < positions.Length; i++)
        {
            int o = i * stride;
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(o + 0, 4), positions[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(o + 4, 4), positions[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(o + 8, 4), positions[i].Z);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(o + 12, 4), normals[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(o + 16, 4), normals[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(o + 20, 4), normals[i].Z);
        }

        var vertexData = new OgreVertexData { VertexCount = (uint)positions.Length };
        vertexData.Elements.Add(new OgreVertexElement(0, OgreVertexElementType.Float3, OgreVertexSemantic.Position, 0, 0));
        vertexData.Elements.Add(new OgreVertexElement(0, OgreVertexElementType.Float3, OgreVertexSemantic.Normal, 12, 0));
        vertexData.Elements.Add(new OgreVertexElement(0, OgreVertexElementType.Float2, OgreVertexSemantic.TextureCoordinates, 24, 0));
        vertexData.Buffers[0] = new OgreVertexBuffer(stride, data);

        var sub = new OgreSubMesh
        {
            MaterialName = "test_mat",
            UseSharedVertices = false,
            Indices = new OgreIndexBuffer(false, indices),
            VertexData = vertexData,
        };
        if (bones is not null) sub.BoneAssignments.AddRange(bones);

        var mesh = new OgreMesh();
        mesh.SubMeshes.Add(sub);
        return mesh;
    }

    /// <summary>
    /// Reads the current vertex positions from a single-submesh test mesh.
    /// </summary>
    static Vector3[] ReadPositions(OgreMesh mesh) => mesh.SubMeshes[0].VertexData!.ReadPositions()!;

    /// <summary>
    /// Builds a triangulated open cylinder with an explicit UV seam at angle 0 (duplicate first/last column).
    /// </summary>
    static (Vector3[] Positions, Vector3[] Normals, uint[] Indices) Cylinder(
        float radius, float yMin, float yMax, int rings, int sectors, Func<int, int, float>? radiusOverride = null)
    {
        int cols = sectors + 1;
        var positions = new Vector3[(rings + 1) * cols];
        var normals = new Vector3[positions.Length];
        for (int iy = 0; iy <= rings; iy++)
        {
            float y = yMin + (yMax - yMin) * iy / rings;
            for (int isec = 0; isec <= sectors; isec++)
            {
                float ang = 2f * MathF.PI * (isec % sectors) / sectors;
                float r = radiusOverride?.Invoke(iy, isec % sectors) ?? radius;
                float cx = MathF.Cos(ang);
                float sz = MathF.Sin(ang);
                int idx = iy * cols + isec;
                positions[idx] = new Vector3(cx * r, y, sz * r);
                normals[idx] = new Vector3(cx, 0, sz);
            }
        }

        var indices = new List<uint>(rings * sectors * 6);
        for (int iy = 0; iy < rings; iy++)
        {
            for (int isec = 0; isec < sectors; isec++)
            {
                uint a = (uint)(iy * cols + isec);
                uint b = a + 1;
                uint c = (uint)((iy + 1) * cols + isec);
                uint d = c + 1;
                indices.Add(a);
                indices.Add(c);
                indices.Add(b);
                indices.Add(b);
                indices.Add(c);
                indices.Add(d);
            }
        }

        return (positions, normals, indices.ToArray());
    }

    [Fact]
    public void PushOutFromBody_ClearsPenetrationAndKeepsSeamsWelded()
    {
        var (bp, bn, bi) = Cylinder(1.0f, 0f, 10f, rings: 20, sectors: 32);
        var body = MakeMesh(bp, bn, bi);

        var (ap, an, ai) = Cylinder(1.1f, 2f, 8f, rings: 12, sectors: 32,
            radiusOverride: (iy, sec) => iy is >= 4 and <= 8 && sec is <= 4 or >= 28 ? 0.85f : 1.1f);
        var bones = new[] { new OgreBoneAssignment(0, 1, 1.0f) };
        var armour = MakeMesh(ap, an, ai, bones);

        int moved = OgreMeshArmourFit.PushOutFromBody(armour, body);

        Assert.True(moved > 0);
        Assert.Equal((uint)ap.Length, armour.SubMeshes[0].VertexData!.VertexCount);
        Assert.Equal(ai.Length, armour.SubMeshes[0].Indices.Indices.Length);
        Assert.Single(armour.SubMeshes[0].BoneAssignments);

        var fitted = ReadPositions(armour);
        const int cols = 33;
        for (int i = 0; i < fitted.Length; i++)
        {
            float r = MathF.Sqrt(fitted[i].X * fitted[i].X + fitted[i].Z * fitted[i].Z);
            Assert.True(r >= 1.04f, $"Vertex {i} stayed inside body at radius {r:F4}");
        }

        // Duplicate UV-seam columns (0 and 32) must move identically so the seam never tears.
        for (int iy = 0; iy <= 12; iy++)
        {
            var a = fitted[iy * cols];
            var b = fitted[iy * cols + 32];
            Assert.True(Vector3.Distance(a, b) < 1e-4f, $"UV seam tore on ring {iy}: {a} vs {b}");
        }
    }

    [Fact]
    public void PushOutFromBody_LeavesClearArmourUntouched()
    {
        var (bp, bn, bi) = Cylinder(1.0f, 0f, 10f, rings: 16, sectors: 24);
        var body = MakeMesh(bp, bn, bi);

        var (ap, an, ai) = Cylinder(1.3f, 2f, 8f, rings: 10, sectors: 24);
        var armour = MakeMesh(ap, an, ai);

        int moved = OgreMeshArmourFit.PushOutFromBody(armour, body);

        Assert.Equal(0, moved);
        Assert.Equal(ap, ReadPositions(armour));
    }

    [Fact]
    public void TuckUnderCoat_CollapsesOuterPlateIslandAndTucksBaseCloth()
    {
        var (bp, bn, bi) = Cylinder(1.0f, 0f, 15f, rings: 30, sectors: 32);
        var body = MakeMesh(bp, bn, bi);

        // Long coat covering Y 3..15 (YMin <= 9.0 triggers tuck).
        var (cp, cn, ci) = Cylinder(1.5f, 3f, 15f, rings: 30, sectors: 48);
        var coat = MakeMesh(cp, cn, ci);

        // Base cloth cylinder (radius 1.48, Y 0..12) + detached outer hip-plate island (radius 1.85, Y 6..9).
        var (baseP, baseN, baseI) = Cylinder(1.48f, 0f, 12f, rings: 24, sectors: 32);
        var (plateP, plateN, plateI) = Cylinder(1.85f, 6f, 9f, rings: 6, sectors: 16);

        var combinedP = new Vector3[baseP.Length + plateP.Length];
        var combinedN = new Vector3[baseN.Length + plateN.Length];
        Array.Copy(baseP, 0, combinedP, 0, baseP.Length);
        Array.Copy(plateP, 0, combinedP, baseP.Length, plateP.Length);
        Array.Copy(baseN, 0, combinedN, 0, baseN.Length);
        Array.Copy(plateN, 0, combinedN, baseN.Length, plateN.Length);

        var combinedI = new uint[baseI.Length + plateI.Length];
        Array.Copy(baseI, 0, combinedI, 0, baseI.Length);
        for (int i = 0; i < plateI.Length; i++) combinedI[baseI.Length + i] = plateI[i] + (uint)baseP.Length;

        var legs = MakeMesh(combinedP, combinedN, combinedI);

        int moved = OgreMeshArmourFit.TuckUnderCoat(legs, coat, body);

        Assert.True(moved > 0);
        var fitted = ReadPositions(legs);

        // Detached outer plate island must be collapsed to a single zero-area point.
        var anchor = fitted[baseP.Length];
        for (int i = baseP.Length + 1; i < fitted.Length; i++)
            Assert.True(Vector3.Distance(fitted[i], anchor) < 1e-4f, $"Plate vertex {i} was not collapsed");

        // Base cloth inside the coat band must tuck inward while respecting body clearance.
        for (int i = 0; i < baseP.Length; i++)
        {
            float r = MathF.Sqrt(fitted[i].X * fitted[i].X + fitted[i].Z * fitted[i].Z);
            Assert.True(r >= 1.05f, $"Base vertex {i} pushed too close to body at r={r:F4}");
            if (fitted[i].Y is >= 4.5f and <= 10.5f)
                Assert.True(r < 1.45f, $"Base vertex {i} at Y={fitted[i].Y:F2} did not tuck under coat (r={r:F4})");
        }
    }

    [Fact]
    public void TuckUnderCoat_SkipsShortVest()
    {
        var (bp, bn, bi) = Cylinder(1.0f, 0f, 15f, rings: 20, sectors: 24);
        var body = MakeMesh(bp, bn, bi);

        // Short vest above Y = 9.0 (YMin = 10.0) should not trigger leg tuck.
        var (cp, cn, ci) = Cylinder(1.5f, 10f, 15f, rings: 10, sectors: 24);
        var vest = MakeMesh(cp, cn, ci);

        var (lp, ln, li) = Cylinder(1.6f, 0f, 12f, rings: 16, sectors: 24);
        var legs = MakeMesh(lp, ln, li);

        int moved = OgreMeshArmourFit.TuckUnderCoat(legs, vest, body);

        Assert.Equal(0, moved);
        Assert.Equal(lp, ReadPositions(legs));
    }
}
