using System.Reflection;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Shaders;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Water;

namespace Meitou.Tests.Vulkan;

public class ShaderCompilerTests
{
    static readonly GlslProgramCompiler Compiler = new(new ShaderCompileOptions { UseDiskCache = false });

    static string Private(Type type, string name)
    {
        var f = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"{type.Name}.{name} not found");
        return (string)f.GetValue(null)!;
    }

    /// <summary>
    /// Every pair the renderers link (WorldGl.Program calls), built the way the renderers build them.
    /// </summary>
    public static IEnumerable<(string Name, string Vertex, string Fragment)> Pairs()
    {
        var postShaders = new[] { nameof(PostProcessShaders.Ssao), nameof(PostProcessShaders.SsaoBlur), nameof(PostProcessShaders.SsaoApply),
            nameof(PostProcessShaders.Composite), nameof(PostProcessShaders.Luminance), nameof(PostProcessShaders.Adapt),
            nameof(PostProcessShaders.Fxaa), nameof(PostProcessShaders.HeatHaze) };
        foreach (var f in postShaders) yield return ("post " + f, PostProcessShaders.Vertex, Private(typeof(PostProcessShaders), f));
        yield return ("cloud march", PostProcessShaders.Vertex, VolumetricCloudShaders.March);
        yield return ("cloud history", PostProcessShaders.Vertex, VolumetricCloudShaders.Resolve);
        yield return ("cloud shadow map", PostProcessShaders.Vertex, VolumetricCloudShaders.Shadow);
        yield return ("sky simple", Private(typeof(SkyRenderer), "Vertex"), Private(typeof(SkyRenderer), "SimpleFragment"));
        yield return ("sky", Private(typeof(SkyRenderer), "Vertex"), Private(typeof(SkyRenderer), "SkyFragment"));
        yield return ("water", Private(typeof(WaterRenderer), "Vertex"), Private(typeof(WaterRenderer), "Fragment"));
        yield return ("water meitou", Private(typeof(WaterRenderer), "MeitouVertex"), Private(typeof(WaterRenderer), "MeitouFragment"));
        yield return ("debug overlay", Private(typeof(DebugOverlay), "Vertex"), Private(typeof(DebugOverlay), "Fragment"));
        yield return ("reflection fog", PostProcessShaders.Vertex, ReflectionPass.FogFragment(false));
        yield return ("reflection fog multisampled", PostProcessShaders.Vertex, ReflectionPass.FogFragment(true));
        yield return ("shadow debug", ShadowShaders.FullscreenVertex, ShadowShaders.DebugFragment);
        yield return ("shadow atlas", ShadowShaders.FullscreenVertex, ShadowShaders.AtlasFragment);
        yield return ("meitou shadow terrain sweep", ShadowShaders.FullscreenVertex, MeitouShadowShaders.SweepFragment);
        yield return ("meitou shadow blockers", ShadowShaders.FullscreenVertex, MeitouShadowShaders.BlockerFragment);
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

    [Theory]
    [MemberData(nameof(PairNames))]
    [Slow]
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
}
