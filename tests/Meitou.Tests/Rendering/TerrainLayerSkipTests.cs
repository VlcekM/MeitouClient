using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The cliff threshold (<see cref="TerrainLayerSkip"/>): the shader ramps a small weight out smoothly, only for the terrain patches.</summary>
public class TerrainLayerSkipTests
{
    [Fact]
    public void The_shader_ramps_the_cliff_weight_from_the_threshold_to_twice_it()
    {
        string f = TerrainShaders.Fragment;
        Assert.Contains("x * smoothstep(eps, 2.0 * eps, x)", f);
        Assert.Contains("w.y = keepWeight(w.y, uCliffEps);", f);
        Assert.Contains("terrain.cliffEps", TerrainShaders.FragmentNative());
    }

    [Fact]
    public void The_default_threshold_is_small_and_a_zero_threshold_keeps_every_weight()
    {
        Assert.InRange(TerrainLayerSkip.CliffEps, 0.01f, 0.06f);
        Assert.Contains("eps > 0.0 ? ", TerrainShaders.Fragment);   // eps 0: the weight unchanged (the rocks and the bake)
    }
}
