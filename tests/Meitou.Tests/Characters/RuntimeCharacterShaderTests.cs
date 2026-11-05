using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Rendering.Gpu.Shaders;

namespace Meitou.Tests.Characters;

public class RuntimeCharacterShaderTests
{
    [Fact]
    public void Mixed_actor_prefixes_are_adjacent_to_their_bones_and_do_not_change_vanilla_offsets()
    {
        int total = 0;
        Assert.Equal(0, RuntimeCharacterPalette.Reserve(ref total, 7, 0));
        int custom = RuntimeCharacterPalette.Reserve(ref total, 3, 36);
        int ordinary = RuntimeCharacterPalette.Reserve(ref total, 2, 0);
        int next = RuntimeCharacterPalette.Reserve(ref total, 4, 2);
        Assert.Equal(43, custom);
        Assert.Equal(46, ordinary);
        Assert.Equal(50, next);
        Assert.Equal(54, total);
        var now = Enumerable.Repeat(Matrix4x4.Identity, total).ToArray();
        var previous = now.ToArray();
        var prefix = Enumerable.Range(1, 36).Select(n => Matrix4x4.CreateTranslation(n, 0, 0)).ToArray();
        var actor = new RuntimeCharacterPalette();
        actor.Update(new(prefix), null, snap: true);
        actor.Write(now.AsSpan(custom - actor.Count, actor.Count), previous.AsSpan(custom - actor.Count, actor.Count));
        Assert.Equal(prefix, now[7..43]);
        Assert.Equal(prefix, previous[7..43]);
        Assert.All(now[..7].Concat(now[43..]), value => Assert.Equal(Matrix4x4.Identity, value));
    }

    [Fact]
    public void Payload_is_immutable_preserves_opaque_bits_and_keeps_previous_values_without_a_bone_repose()
    {
        var opaque = Matrix4x4.Identity;
        opaque.M11 = BitConverter.UInt32BitsToSingle(0x7FC000A3);
        var input = new[] { opaque };
        var first = new RuntimeCharacterShaderData(input, 1u << 31);
        input[0] = Matrix4x4.Identity;
        Assert.Equal(0x7FC000A3u, BitConverter.SingleToUInt32Bits(first.Prefix[0].M11));
        Assert.Throws<NotSupportedException>(() => ((IList<Matrix4x4>)first.Prefix)[0] = Matrix4x4.Identity);
        var shaders = RuntimeCharacterShaderSources.Default();
        var state = new RuntimeCharacterPalette();
        state.Update(first, shaders, snap: true);
        var next = new RuntimeCharacterShaderData([Matrix4x4.CreateTranslation(2, 3, 4)]);
        state.Update(next, shaders, snap: false);
        var current = new Matrix4x4[1];
        var previous = new Matrix4x4[1];
        state.Write(current, previous);
        Assert.Equal(new Vector3(2, 3, 4), current[0].Translation);
        Assert.Equal(0x7FC000A3u, BitConverter.SingleToUInt32Bits(previous[0].M11));
        state.Update(next, shaders, snap: false);
        state.Write(current, previous);
        Assert.Equal(current, previous);
    }

    [Fact]
    public void A_new_shader_schema_length_or_motion_snap_uses_the_current_prefix_as_its_history()
    {
        var state = new RuntimeCharacterPalette();
        var shaders = RuntimeCharacterShaderSources.Default();
        state.Update(new([Matrix4x4.Identity]), shaders, snap: true);
        var changed = new RuntimeCharacterShaderData([Matrix4x4.CreateTranslation(7, 0, 0), Matrix4x4.CreateScale(2)]);
        state.Update(changed, shaders, snap: false);
        Assert.Same(changed, state.Previous);
        var moved = new RuntimeCharacterShaderData([Matrix4x4.CreateScale(3), Matrix4x4.CreateScale(4)]);
        state.Update(moved, RuntimeCharacterShaderSources.Default(), snap: false);
        Assert.Same(moved, state.Previous);
        state.Update(changed, shaders, snap: true);
        Assert.Same(changed, state.Previous);
        state.Update(null, null, snap: false);
        Assert.Equal(0, state.Count);
        Assert.Null(state.Current);
    }

    [Fact]
    public void Addon_flags_cannot_overwrite_native_visibility_and_default_geometry_has_no_extra_layout()
    {
        var ordinary = new RuntimeCharacterGeometry();
        Assert.Null(ordinary.Shaders);
        Assert.Equal(0u, ordinary.BodyFlags);
        Assert.False(ordinary.PreserveRawBodyVertexChannels);
        Assert.Equal(0xC0000FFEu, ~RuntimeCharacterShaderData.ReservedInstanceFlags);
        var allowed = new RuntimeCharacterShaderData([], 0xC0000FFEu);
        Assert.Equal(0xC0000FFEu, allowed.Flags);
        foreach (uint bit in new[] { 1u, 1u << 12, 1u << 17, 1u << 28, 1u << 29 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeCharacterShaderData([], bit));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RuntimeCharacterGeometry(bodyFlags: bit));
        }
    }

    [Fact]
    public void Raw_runtime_vertices_keep_layer_tangents_and_float_colours_while_native_upload_stays_packed()
    {
        var source = Triangle();
        var raw = Model.Build(source, 1, gameBinormals: true, preserveRawVertexChannels: true).Parts.Single().Vertices;
        var native = Model.Build(source, 1, gameBinormals: true).Parts.Single().Vertices;
        Assert.Equal(new Vector4(12, -8, 4, 35), raw[0].Tangent);
        Assert.Equal(new Vector4(.25f, 6.75f, 2.5f, .8f), raw[0].Colour);
        Assert.Equal(1, native[0].Tangent.W);
        byte[] bytes = GpuVertex.PackCharacter(raw, preserveRawChannels: true);
        Assert.Equal(raw.Length * Vertex.Size, bytes.Length);
        Assert.Equal(MemoryMarshal.AsBytes(raw.AsSpan()).ToArray(), bytes);
        Assert.Equal(raw.Length * GpuVertex.CharacterSize, GpuVertex.PackCharacter(raw).Length);
        Assert.Equal(35, BitConverter.ToSingle(bytes, 44));
        Assert.Equal(6.75f, BitConverter.ToSingle(bytes, 52));
    }

    [Fact]
    public void Program_cache_uses_source_identity_and_retirement_waits_for_the_backends_release_callback()
    {
        int compiled = 0, disposed = 0;
        var deferred = new Queue<Action>();
        var cache = new RuntimeCharacterProgramCache<object>(_ => { compiled++; return new(); }, _ => deferred.Enqueue(() => disposed++));
        var a = RuntimeCharacterShaderSources.Default();
        var b = RuntimeCharacterShaderSources.Default();
        var first = cache.Get(a);
        Assert.Same(first, cache.Get(a));
        Assert.NotSame(first, cache.Get(b));
        Assert.Equal(2, compiled);
        Assert.True(cache.Retain(new HashSet<RuntimeCharacterShaderSet>(ReferenceEqualityComparer.Instance) { b }));
        Assert.Equal(0, disposed);
        Assert.Single(deferred);
        deferred.Dequeue()();
        Assert.Equal(1, disposed);
        Assert.False(cache.Retain(new HashSet<RuntimeCharacterShaderSet>(ReferenceEqualityComparer.Instance) { b }));
        cache.Clear();
        Assert.Equal(1, disposed);
        deferred.Dequeue()();
        Assert.Equal(2, disposed);
    }

    [Theory, Slow]
    [InlineData("colour")]
    [InlineData("depth")]
    [InlineData("motion")]
    public void Public_shader_sources_compile_for_every_character_pass_with_the_hosts_colour_wrapper(string pass)
    {
        var source = RuntimeCharacterShaderSources.Default(lateDepth: true);
        var (vertex, fragment) = pass switch
        {
            "colour" => (source.ColourVertex, CharacterRenderer.RuntimeColourFragment(source.ColourFragment)),
            "depth" => (source.DepthVertex, source.DepthFragment),
            _ => (source.MotionVertex, source.MotionFragment),
        };
        var compiler = new GlslProgramCompiler(new ShaderCompileOptions { UseDiskCache = false });
        Assert.NotEmpty(compiler.CompileNative(vertex, fragment: false));
        Assert.NotEmpty(compiler.CompileNative(fragment, fragment: true));
    }

    static OgreMesh Triangle()
    {
        var vertices = new Vertex[3];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = new()
        {
            Position = i == 0 ? Vector3.Zero : i == 1 ? Vector3.UnitX : Vector3.UnitZ,
            Normal = Vector3.UnitY, Tangent = new(12, -8, 4, 35), Colour = new(.25f, 6.75f, 2.5f, .8f), Weights = new(1, 0, 0, 0),
        };
        var data = new OgreVertexData { VertexCount = 3 };
        data.Elements.AddRange([
            new(0, OgreVertexElementType.Float3, OgreVertexSemantic.Position, 0, 0),
            new(0, OgreVertexElementType.Float3, OgreVertexSemantic.Normal, 12, 0),
            new(0, OgreVertexElementType.Float2, OgreVertexSemantic.TextureCoordinates, 24, 0),
            new(0, OgreVertexElementType.Float4, OgreVertexSemantic.Tangent, 32, 0),
            new(0, OgreVertexElementType.Float4, OgreVertexSemantic.Diffuse, 48, 0),
        ]);
        data.Buffers[0] = new((ushort)Vertex.Size, MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray());
        var sub = new OgreSubMesh { VertexData = data, Indices = new(false, [0, 1, 2]) };
        for (uint i = 0; i < 3; i++) sub.BoneAssignments.Add(new(i, 0, 1));
        var mesh = new OgreMesh();
        mesh.SubMeshes.Add(sub);
        return mesh;
    }
}
