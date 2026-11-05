using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Rendering;

namespace Meitou.Tests.Characters;

public class ModelRawSkinningTests
{
    [Fact]
    public void Dense_ordered_raw_rigid_vertices_normalize_finite_positive_weights_without_changing_their_bones()
    {
        var mesh = Mesh(3, [new(0, 3, .25f), new(1, 1, 7), new(2, 2, float.Epsilon)]);
        var raw = Model.Build(mesh, 4, preserveRawVertexChannels: true);
        var native = Model.Build(mesh, 4);
        Assert.Empty(raw.Warnings);
        Assert.True(raw.Parts.Single().Skinned);
        Assert.Equal(new uint[] { 3, 1, 2 }, raw.Parts.Single().Vertices.Select(vertex => vertex.Bones));
        Assert.All(raw.Parts.Single().Vertices, vertex => Assert.Equal(new Vector4(1, 0, 0, 0), vertex.Weights));
        Assert.Equal(Bytes(native), Bytes(raw));
    }

    public static IEnumerable<object[]> GeneralAssignments()
    {
        yield return [new OgreBoneAssignment[] { new(2, 2, 2), new(1, 1, 1), new(0, 0, .5f) }, 4]; // Unordered rigid input.
        yield return [new OgreBoneAssignment[] { new(0, 0, 1), new(0, 2, 3), new(1, 1, 1) }, 4]; // Duplicate and missing vertex.
        yield return [new OgreBoneAssignment[] { new(1, 2, .5f) }, 4]; // Sparse input retains the lowest-bone fallback.
        yield return [new OgreBoneAssignment[] { new(0, 0, 1), new(0, 2, 3), new(1, 1, 1), new(2, 3, 2) }, 4];
        yield return [new OgreBoneAssignment[] { new(0, 0, 1), new(1, 8, 1), new(2, 2, 1) }, 4];
        yield return [new OgreBoneAssignment[] { new(0, 0, 1), new(1, 256, 1), new(2, 2, 1) }, 300];
        yield return [new OgreBoneAssignment[] { new(0, 0, 1), new(1, 1, 1), new(3, 2, 1) }, 4];
        foreach (float weight in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            yield return [new OgreBoneAssignment[] { new(0, 0, 1), new(1, 1, weight), new(2, 2, 1) }, 4];
        yield return [Array.Empty<OgreBoneAssignment>(), 4];
    }

    [Theory]
    [MemberData(nameof(GeneralAssignments))]
    public void Other_raw_assignment_maps_keep_the_general_compilers_exact_outputs_and_warnings(OgreBoneAssignment[] assignments, int bones)
    {
        var mesh = Mesh(3, assignments);
        var expected = Model.Build(mesh, bones);
        var raw = Model.Build(mesh, bones, preserveRawVertexChannels: true);
        Assert.Equal(expected.Warnings, raw.Warnings);
        Assert.Equal(expected.Parts.Single().Skinned, raw.Parts.Single().Skinned);
        Assert.Equal(Bytes(expected), Bytes(raw));
    }

    [Fact]
    public void Dense_raw_skinning_allocation_is_bounded_by_vertex_decoding_without_per_vertex_sorting_objects()
    {
        const int count = 6000;
        var ordered = Enumerable.Range(0, count).Select(i => new OgreBoneAssignment((uint)i, (ushort)(i % 4), .25f)).ToArray();
        var dense = Mesh(count, ordered);
        var general = Mesh(count, ordered.Reverse().ToArray());
        _ = Model.Build(dense, 4, preserveRawVertexChannels: true);
        _ = Model.Build(general, 4, preserveRawVertexChannels: true);
        long optimized = Allocated(dense), fallback = Allocated(general);
        Assert.InRange(optimized, 0, count * 128L + 16_384);
        Assert.True(fallback >= optimized * 2, $"Dense raw decode allocated {optimized:N0} bytes versus {fallback:N0} for the equivalent general path.");
        Assert.Equal(Bytes(Model.Build(general, 4, preserveRawVertexChannels: true)), Bytes(Model.Build(dense, 4, preserveRawVertexChannels: true)));
    }

    static long Allocated(OgreMesh mesh)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = Model.Build(mesh, 4, preserveRawVertexChannels: true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(result);
        return allocated;
    }

    static byte[] Bytes(Model model) => MemoryMarshal.AsBytes(model.Parts.Single().Vertices.AsSpan()).ToArray();

    static OgreMesh Mesh(int count, OgreBoneAssignment[] assignments)
    {
        var positions = Enumerable.Range(0, count).Select(i => new Vector3(i % 3, i / 3, 0)).ToArray();
        var data = new OgreVertexData { VertexCount = (uint)count };
        data.Elements.Add(new(0, OgreVertexElementType.Float3, OgreVertexSemantic.Position, 0, 0));
        data.Buffers[0] = new(12, MemoryMarshal.AsBytes(positions.AsSpan()).ToArray());
        var part = new OgreSubMesh { VertexData = data, Indices = new(true, Enumerable.Range(0, count).Select(i => (uint)i).ToArray()) };
        part.BoneAssignments.AddRange(assignments);
        var mesh = new OgreMesh();
        mesh.SubMeshes.Add(part);
        return mesh;
    }
}
