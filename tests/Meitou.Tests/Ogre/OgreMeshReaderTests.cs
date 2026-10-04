using System.Numerics;
using System.Text;
using Meitou.Content;
using Meitou.Data.Ogre;

namespace Meitou.Tests.Ogre;

public class OgreMeshReaderTests
{
    /// <summary>Writes Ogre serializer chunks: id, total length (including the 6-byte header), body.</summary>
    sealed class Builder
    {
        readonly MemoryStream ms = new();
        readonly BinaryWriter w;
        readonly Stack<long> open = new();

        public Builder(string version)
        {
            w = new BinaryWriter(ms);
            w.Write((ushort)0x1000);
            String(version);
        }

        public Builder Begin(OgreMeshChunk id)
        {
            w.Write((ushort)id);
            open.Push(ms.Position);
            w.Write(0u);
            return this;
        }

        public Builder End()
        {
            long at = open.Pop(), end = ms.Position;
            ms.Position = at;
            w.Write((uint)(end - at + 2));
            ms.Position = end;
            return this;
        }

        public Builder Bool(bool v) { w.Write(v); return this; }
        public Builder U16(ushort v) { w.Write(v); return this; }
        public Builder U32(uint v) { w.Write(v); return this; }
        public Builder F32(params float[] v) { foreach (var f in v) w.Write(f); return this; }
        public Builder String(string s) { w.Write(Encoding.UTF8.GetBytes(s + "\n")); return this; }
        public byte[] ToArray() => ms.ToArray();
    }

    static Builder Triangle(string version) =>
        new Builder(version)
            .Begin(OgreMeshChunk.Mesh).Bool(true)
                .Begin(OgreMeshChunk.SubMesh).String("rock_mat").Bool(false).U32(3).Bool(false).U16(0).U16(1).U16(2)
                    .Begin(OgreMeshChunk.Geometry).U32(3)
                        .Begin(OgreMeshChunk.GeometryVertexDeclaration)
                            .Begin(OgreMeshChunk.GeometryVertexElement).U16(0).U16((ushort)OgreVertexElementType.Float3).U16((ushort)OgreVertexSemantic.Position).U16(0).U16(0).End()
                            .Begin(OgreMeshChunk.GeometryVertexElement).U16(0).U16((ushort)OgreVertexElementType.Float2).U16((ushort)OgreVertexSemantic.TextureCoordinates).U16(12).U16(0).End()
                        .End()
                        .Begin(OgreMeshChunk.GeometryVertexBuffer).U16(0).U16(20)
                            .Begin(OgreMeshChunk.GeometryVertexBufferData).F32(0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1).End()
                        .End()
                    .End()
                    .Begin(OgreMeshChunk.SubMeshOperation).U16((ushort)OgreOperationType.TriangleList).End()
                    .Begin(OgreMeshChunk.SubMeshBoneAssignment).U32(1).U16(3).F32(0.5f).End()
                .End()
                .Begin(OgreMeshChunk.MeshSkeletonLink).String("rock.skeleton").End()
                .Begin(OgreMeshChunk.MeshBounds).F32(0, 0, 0, 1, 1, 0, 1.5f).End()
                .Begin(OgreMeshChunk.SubMeshNameTable)
                    .Begin(OgreMeshChunk.SubMeshNameTableElement).U16(0).String("body").End()
                .End();

    [Fact]
    public void Reads_geometry_submesh_skeleton_bounds_and_names()
    {
        var mesh = OgreMeshReader.Read(Triangle(OgreMeshReader.Version1_100).End().ToArray());

        Assert.True(mesh.SkeletallyAnimated);
        Assert.Equal("rock.skeleton", mesh.SkeletonName);
        Assert.Equal(new OgreBounds(Vector3.Zero, new Vector3(1, 1, 0), 1.5f), mesh.Bounds);
        Assert.Equal("body", mesh.SubMeshNames[0]);

        var sub = Assert.Single(mesh.SubMeshes);
        Assert.Equal("rock_mat", sub.MaterialName);
        Assert.Equal([0u, 1, 2], sub.Indices.Indices);
        Assert.Equal(new OgreBoneAssignment(1, 3, 0.5f), Assert.Single(sub.BoneAssignments));
        Assert.Equal([Vector3.Zero, new(1, 0, 0), new(0, 1, 0)], sub.VertexData!.ReadPositions()!);
        Assert.Equal([0f, 0, 1, 0, 0, 1], sub.VertexData.ReadFloats(sub.VertexData.Find(OgreVertexSemantic.TextureCoordinates)!));
    }

    [Fact]
    public void Reads_generated_lod_in_both_layouts()
    {
        var v1100 = OgreMeshReader.Read(Triangle(OgreMeshReader.Version1_100)
            .Begin(OgreMeshChunk.MeshLodLevel).String("distance").U16(2)
                .Begin(OgreMeshChunk.MeshLodGenerated).F32(100).U32(3).U32(0).U32(uint.MaxValue).Bool(false).U32(3).U16(2).U16(1).U16(0).End()
            .End().End().ToArray());
        var level = Assert.Single(v1100.Lod!.Levels);
        Assert.Equal(100f, level.UserValue);
        Assert.Equal([2u, 1, 0], Assert.Single(level.Faces).Buffer!.Indices);

        var v18 = OgreMeshReader.Read(Triangle(OgreMeshReader.Version1_8)
            .Begin(OgreMeshChunk.MeshLodLevel).String("distance").U16(2).Bool(false)
                .Begin(OgreMeshChunk.MeshLodUsage).F32(50)
                    .Begin(OgreMeshChunk.MeshLodGenerated).U32(3).Bool(false).U16(0).U16(2).U16(1).End()
                .End()
            .End().End().ToArray());
        Assert.Equal([0u, 2, 1], Assert.Single(Assert.Single(v18.Lod!.Levels).Faces).Buffer!.Indices);
    }

    [Fact]
    public void Rejects_unknown_version_and_truncated_files()
    {
        Assert.Throws<OgreFormatException>(() => OgreMeshReader.Read(new Builder("[MeshSerializer_v1.30]").ToArray()));
        var bytes = Triangle(OgreMeshReader.Version1_100).End().ToArray();
        Assert.Throws<OgreFormatException>(() => OgreMeshReader.Read(bytes[..^9]));
    }

    /// <summary>
    /// Every base-game mesh parses to the end, indices stay within their vertex count, and every vertex
    /// position lies inside the mesh's stored bounds (which checks the vertex layout decoding).
    /// </summary>
    [Fact]
    public void Reads_every_base_game_mesh()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");

        var files = Directory.EnumerateFiles(install!.DataDirectory, "*.mesh", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        Parallel.ForEach(files, file =>
        {
            var mesh = OgreMeshReader.ReadFile(file);
            var bounds = mesh.Bounds ?? throw new Xunit.Sdk.XunitException($"{file}: no bounds");
            float tolerance = 1e-3f * (1 + (bounds.Max - bounds.Min).Length());
            foreach (var sub in mesh.SubMeshes)
            {
                var vertices = sub.UseSharedVertices ? mesh.SharedVertexData! : sub.VertexData!;
                Assert.All(sub.Indices.Indices, i => Assert.True(i < vertices.VertexCount, file));
                foreach (var p in vertices.ReadPositions() ?? [])
                    Assert.True(
                        Vector3.Min(p, bounds.Min - new Vector3(tolerance)) == bounds.Min - new Vector3(tolerance) &&
                        Vector3.Max(p, bounds.Max + new Vector3(tolerance)) == bounds.Max + new Vector3(tolerance),
                        $"{file}: {p} outside {bounds}");
            }
        });
    }
}
