using Meitou.Rendering.Gpu.Shaders;

namespace Meitou.Tests.Vulkan;

/// <summary>Reflection on small hand-written programs: bindings, unused resources.</summary>
public class ShaderReflectionTests
{
    static readonly GlslProgramCompiler Compiler = new(new ShaderCompileOptions { UseDiskCache = false });
    const string Vs = "#version 330 core\nuniform vec4 uA[3];\nvoid main() { gl_Position = uA[2]; }\n";

    [Fact]
    [Slow]
    public void UnusedSamplersAndBlocksAreInactiveNotBound()
    {
        string fs = "#version 330 core\nlayout(std140) uniform Blk { vec4 x; };\nuniform sampler2D used;\nuniform samplerCube unusedA, unusedB;\nuniform usampler2D alsoUnused;\nout vec4 c;\nvoid main() { c = texture(used, vec2(0.5)); }\n";
        var p = Compiler.Compile(Vs, fs);
        Assert.Equal(["used"], p.Fragment.Samplers.Select(s => s.Name).ToArray());
        Assert.Equal(32, p.Fragment.Samplers[0].Binding);
        Assert.Empty(p.Fragment.Blocks);
        Assert.Equal(["Blk", "alsoUnused", "unusedA", "unusedB"], p.Fragment.Inactive.Order(StringComparer.Ordinal).ToArray());
        Assert.Null(p.Fragment.FindUniform("x"));
    }

    [Fact]
    [Slow]
    public void UsedThroughAFunctionCountsAsActive()
    {
        string fs = "#version 330 core\nuniform samplerCube a;\nuniform sampler2DShadow s;\nuniform usampler2D u;\nuniform float k[2];\nout vec4 c;\nvec4 f() { return texture(a, vec3(k[1])); }\nvoid main() { c = f() + vec4(texture(s, vec3(0.5)), float(texture(u, vec2(0.5)).x), 0.0, 0.0); }\n";
        var p = Compiler.Compile(Vs, fs);
        Assert.Empty(p.Fragment.Inactive);
        var a = p.Fragment.FindSampler("a")!;
        Assert.Equal(SamplerDimension.Cube, a.Dimension);
        Assert.True(p.Fragment.FindSampler("s")!.Depth);
        var u = p.Fragment.FindSampler("u")!;
        Assert.Equal(ScalarKind.UInt, u.SampledKind);
        var k = p.Fragment.FindUniform("k[1]");
        Assert.NotNull(k);
        Assert.Equal(2, k.Value.Member.ArrayLength);
        Assert.True(k.Value.Member.ArrayStride >= 4); // whatever glslang laid out; offsets come from the module
        Assert.Equal(k.Value.Member.Offset + k.Value.Member.ArrayStride, k.Value.Offset);
    }

    [Fact]
    [Slow]
    public void VertexUniformArraysAreReported()
    {
        var p = Compiler.Compile(Vs, "#version 330 core\nout vec4 c;\nvoid main() { c = vec4(1.0); }\n");
        var a = p.Vertex.DefaultBlock!.Members.Single();
        Assert.Equal("uA", a.Name);
        Assert.Equal(3, a.ArrayLength);
        Assert.Equal(48, a.Size);
        Assert.Equal(0, p.Vertex.DefaultBlock.Binding);
        Assert.Null(p.Fragment.DefaultBlock);
    }
}
