using System.Reflection;
using Meitou.Rendering;
using Meitou.Rendering.Vulkan.Shaders;

namespace Meitou.Tests.Vulkan;

public class ShaderCompilerTests
{
    static readonly GlslProgramCompiler Compiler = new(new ShaderCompileOptions { UseDiskCache = false });

    static string Private(Type type, string name)
    {
        var f = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static) ?? throw new InvalidOperationException($"{type.Name}.{name} not found");
        return (string)f.GetValue(null)!;
    }

    /// <summary>Every pair the renderers link (WorldGl.Program calls), built the way the renderers build them.</summary>
    public static IEnumerable<(string Name, string Vertex, string Fragment)> Pairs()
    {
        yield return ("terrain patch", TerrainShaders.PatchVertex, TerrainShaders.Fragment);
        yield return ("terrain mesh", TerrainShaders.MeshVertex, TerrainShaders.MeshFragment);
        yield return ("terrain patch depth", TerrainShaders.PatchVertex, ShadowShaders.DepthFragment);
        yield return ("terrain mesh depth", TerrainShaders.MeshInstancedDepthVertex, ShadowShaders.DepthFragment);
        yield return ("mesh", Shaders.MeshVertex, Shaders.MeshFragment);
        yield return ("line", Shaders.LineVertex, Shaders.LineFragment);
        foreach (var f in new[] { nameof(PostProcessShaders.Ssao), nameof(PostProcessShaders.SsaoBlur), nameof(PostProcessShaders.Composite),
                     nameof(PostProcessShaders.Luminance), nameof(PostProcessShaders.Adapt), nameof(PostProcessShaders.Fxaa) })
            yield return ("post " + f, PostProcessShaders.Vertex, Private(typeof(PostProcessShaders), f));
        yield return ("sky simple", Private(typeof(SkyRenderer), "Vertex"), Private(typeof(SkyRenderer), "SimpleFragment"));
        yield return ("sky", Private(typeof(SkyRenderer), "Vertex"), Private(typeof(SkyRenderer), "SkyFragment"));
        yield return ("water", Private(typeof(WaterRenderer), "Vertex"), Private(typeof(WaterRenderer), "Fragment"));
        yield return ("debug overlay", Private(typeof(DebugOverlay), "Vertex"), Private(typeof(DebugOverlay), "Fragment"));
        yield return ("foliage mesh", FoliageShaders.MeshVertex(), FoliageShaders.MeshFragment());
        yield return ("foliage mesh depth", FoliageShaders.MeshVertex(), ShadowShaders.MeshDepthFragment);
        yield return ("grass", FoliageShaders.GrassVertex, FoliageShaders.GrassFragment);
        yield return ("building lod", BuildingLodShaders.Vertex(), BuildingLodShaders.Fragment());
        yield return ("building lod depth", BuildingLodShaders.Vertex(), ShadowShaders.MeshDepthFragment);
        yield return ("shadow debug", ShadowShaders.FullscreenVertex, ShadowShaders.DebugFragment);
        yield return ("shadow atlas", ShadowShaders.FullscreenVertex, ShadowShaders.AtlasFragment);
    }

    public static TheoryData<string> PairNames()
    {
        var data = new TheoryData<string>();
        foreach (var p in Pairs()) data.Add(p.Name);
        return data;
    }

    static (string Vertex, string Fragment) Find(string name)
    {
        var p = Pairs().First(p => p.Name == name);
        return (p.Vertex, p.Fragment);
    }

    [Fact]
    public void AllRendererPairsAreCovered()
    {
        Assert.Equal(Pairs().Count(), Pairs().Select(p => p.Name).Distinct().Count());
        Assert.True(Pairs().Count() >= 23);   // every pair the renderers compile (bloom's three removed)
    }

    [Theory]
    [MemberData(nameof(PairNames))]
    public void PairCompilesAndInterfacesMatch(string name)
    {
        var (v, f) = Find(name);
        var program = Compiler.Compile(v, f);

        // every fragment input has a vertex output of the same name at the same location, size and kind
        foreach (var input in program.Fragment.Inputs)
        {
            var output = program.Vertex.Outputs.SingleOrDefault(o => o.Name == input.Name);
            Assert.True(output is not null, $"{name}: fragment input '{input.Name}' has no vertex output");
            Assert.Equal(output!.Location, input.Location);
            Assert.Equal(output.Slots, input.Slots);
            Assert.Equal(output.Kind, input.Kind);
        }
        Assert.Equal(program.Vertex.Outputs.Count, program.Vertex.Outputs.Select(o => o.Location).Distinct().Count());

        // the stages' resources do not collide
        var vd = program.Vertex.DefaultBlock;
        var fd = program.Fragment.DefaultBlock;
        if (vd is not null) Assert.InRange(vd.Binding, 0, 31);
        if (fd is not null) Assert.InRange(fd.Binding, 32, 63);
        foreach (var b in program.Fragment.Blocks) Assert.InRange(b.Binding, 32, 63);
        foreach (var b in program.Vertex.Blocks) Assert.InRange(b.Binding, 0, 31);
        foreach (var s in program.Fragment.Samplers) Assert.InRange(s.Binding, 32, 63);
        foreach (var s in program.Vertex.Samplers) Assert.InRange(s.Binding, 0, 31);
        var all = program.Vertex.Blocks.Select(b => b.Binding).Concat(program.Vertex.Samplers.Select(s => s.Binding))
            .Concat(program.Fragment.Blocks.Select(b => b.Binding)).Concat(program.Fragment.Samplers.Select(s => s.Binding)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(program.Vertex.Blocks.Concat(program.Fragment.Blocks), b => Assert.Equal(0, b.Set));

        // fragment outputs numbered from 0
        Assert.All(program.Fragment.Outputs, o => Assert.True(o.Location >= 0));
    }

    [Fact]
    public void VertexAttributesStayAsDeclared()
    {
        var (v, f) = Find("mesh");
        var program = Compiler.Compile(v, f);
        Assert.Equal(0, program.Vertex.Inputs.Single(i => i.Name == "aPosition").Location);
        var bones = program.Vertex.Inputs.Single(i => i.Name == "aBones");
        Assert.Equal(5, bones.Location);
        Assert.Equal(ScalarKind.UInt, bones.Kind);
        Assert.Equal(4, bones.Rows);
    }

    [Fact]
    public void TerrainPatchReflection()
    {
        var program = Compiler.Compile(TerrainShaders.PatchVertex, TerrainShaders.Fragment);
        var block = program.Vertex.DefaultBlock;
        Assert.NotNull(block);
        Assert.Equal("gl_DefaultUniformBlock", block.Name);
        Assert.Equal(BlockKind.Uniform, block.Kind);
        var vp = block.Members.Single(m => m.Name == "uViewProjection");
        Assert.Equal(4, vp.Columns);
        Assert.Equal(4, vp.Rows);
        Assert.Equal(ScalarKind.Float, vp.Kind);
        Assert.Equal(16, vp.MatrixStride);
        Assert.Equal(64, vp.Size);
        Assert.Equal(0, vp.ArrayLength);
        Assert.True(block.Size >= vp.Offset + vp.Size);
        Assert.Equal(0, block.Size % 16);
        Assert.NotEmpty(program.Fragment.Samplers);
        Assert.Equal(program.Vertex.DefaultBlock!.Binding + 32, program.Fragment.DefaultBlock!.Binding);
    }

    [Fact]
    public void MeshReflectionHasBoneArrayAndBool()
    {
        var program = Compiler.Compile(Shaders.MeshVertex, Shaders.MeshFragment);
        var block = program.Vertex.DefaultBlock!;
        var bones = block.Members.Single(m => m.Name == "uBones");
        Assert.Equal(128, bones.ArrayLength);
        Assert.Equal(64, bones.ArrayStride);
        Assert.Equal(128 * 64, bones.Size);
        Assert.Equal(4, bones.Columns);
        Assert.Equal(ScalarKind.UInt, block.Members.Single(m => m.Name == "uSkinned").Kind); // bool is stored as a uint
        var diffuse = program.Fragment.FindSampler("uDiffuse");
        Assert.NotNull(diffuse);
        Assert.Equal(SamplerDimension.Dim2D, diffuse.Dimension);
        Assert.False(diffuse.Depth);
        Assert.Equal(ScalarKind.Float, diffuse.SampledKind);
    }

    [Fact]
    public void FindUniformUnderstandsGlNames()
    {
        var program = Compiler.Compile(Shaders.MeshVertex, Shaders.MeshFragment);
        var r = program.Vertex;
        var plain = r.FindUniform("uModel");
        Assert.NotNull(plain);
        Assert.Equal(0, plain.Value.Element);

        var first = r.FindUniform("uBones[0]");
        var third = r.FindUniform("uBones[3]");
        var whole = r.FindUniform("uBones");
        Assert.NotNull(first);
        Assert.NotNull(third);
        Assert.NotNull(whole);
        Assert.Equal(3, third.Value.Element);
        Assert.Equal(first.Value.Offset + 3 * 64, third.Value.Offset);
        Assert.Equal(whole.Value.Offset, first.Value.Offset);
        Assert.Same(first.Value.Member, third.Value.Member);
        Assert.Null(r.FindUniform("uBones[128]"));
        Assert.Null(r.FindUniform("uNoSuchThing"));
        Assert.Null(r.FindUniform(""));

        var (v, f) = program.FindUniform("uViewProjection");
        Assert.NotNull(v);
        Assert.Null(f);
    }

    [Fact]
    public void NamedBlocksAreReflectedByTypeName()
    {
        var program = Compiler.Compile(TerrainShaders.PatchVertex, ShadowShaders.DepthFragment);
        var block = program.Fragment.Blocks.Single(b => b.Name == ShadowShaders.CasterBlock);
        Assert.False(block.IsDefault);
        Assert.InRange(block.Binding, 32, 63);
        var bias = block.Members.Single(m => m.Name == "uShadowBias");
        Assert.Equal(4, bias.Rows);
        Assert.Equal(16, block.Size);
        Assert.NotNull(program.Fragment.FindUniform("uShadowBias"));
        Assert.NotNull(program.Fragment.FindUniform(ShadowShaders.CasterBlock + ".uShadowBias"));
    }

    [Fact]
    public void ShaderFailuresReportTheSource()
    {
        var ex = Assert.Throws<ShaderCompileException>(() => Compiler.Compile("#version 330 core\nvoid main() { gl_Position = nope; }\n", "#version 330 core\nout vec4 c;\nvoid main() { c = vec4(1.0); }\n"));
        Assert.Equal("vertex", ex.Stage);
        Assert.Contains("nope", ex.Message);
        Assert.Contains("    2: void main()", ex.Message);
    }

    [Fact]
    public void RemapClipDepthCompilesAndAppendsTheRemap()
    {
        var (v, f) = Find("mesh");
        var program = Compiler.Compile(v, f, new ShaderCompileOptions { RemapClipDepth = true, UseDiskCache = false });
        Assert.Contains("meitou_original_main", program.VertexSource);
        Assert.Contains("gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;", program.VertexSource);
        Assert.DoesNotContain("meitou_original_main", program.FragmentSource);
        var plain = Compiler.Compile(v, f);
        Assert.NotEqual(plain.VertexSpirv.Length, program.VertexSpirv.Length);
        Assert.NotEqual(plain.VertexSource, program.VertexSource);
    }

    [Fact]
    public void DiskCacheRoundTripAndCorruptFileIsRewritten()
    {
        string dir = Path.Combine(Path.GetTempPath(), "meitou-shader-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new ShaderCompileOptions { CacheDirectory = dir };
            var (v, f) = Find("water");
            var first = new GlslProgramCompiler().Compile(v, f, options);
            Assert.Equal(ProgramSource.Compiled, first.Source);
            var file = Assert.Single(Directory.GetFiles(dir, "*.spvpair"));

            var second = new GlslProgramCompiler().Compile(v, f, options);
            Assert.Equal(ProgramSource.Disk, second.Source);
            Assert.Equal(first.VertexSpirv, second.VertexSpirv);
            Assert.Equal(first.FragmentSpirv, second.FragmentSpirv);

            var compiler = new GlslProgramCompiler();
            Assert.Equal(ProgramSource.Disk, compiler.Compile(v, f, options).Source);
            Assert.Equal(ProgramSource.Memory, compiler.Compile(v, f, options).Source);

            // different options are a different key
            var other = new GlslProgramCompiler().Compile(v, f, options with { RemapClipDepth = true });
            Assert.Equal(ProgramSource.Compiled, other.Source);
            Assert.Equal(2, Directory.GetFiles(dir, "*.spvpair").Length);

            // corrupt: flip a byte in the middle, then truncate
            byte[] data = File.ReadAllBytes(file);
            data[data.Length / 2] ^= 0xFF;
            File.WriteAllBytes(file, data);
            var healed = new GlslProgramCompiler().Compile(v, f, options);
            Assert.Equal(ProgramSource.Compiled, healed.Source);
            Assert.Equal(ProgramSource.Disk, new GlslProgramCompiler().Compile(v, f, options).Source);

            File.WriteAllBytes(file, [1, 2, 3]);
            Assert.Equal(ProgramSource.Compiled, new GlslProgramCompiler().Compile(v, f, options).Source);
            Assert.Equal(ProgramSource.Disk, new GlslProgramCompiler().Compile(v, f, options).Source);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
