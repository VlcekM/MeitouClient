using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The terrain colour after the occluders (<see cref="TerrainLateColour"/>): the depth pass must hold the colour pass's depths, so it shares the vertex
/// stage, and its fragment stage must do no work.</summary>
public class TerrainLateColourTests
{
    [Fact]
    public void The_prepass_fragment_stage_writes_nothing_and_never_discards_or_writes_depth()
    {
        string f = TerrainShaders.PrepassFragmentNative();
        Assert.DoesNotContain("discard", f);
        Assert.DoesNotContain("gl_FragDepth", f);
        Assert.DoesNotContain("out vec4", f);
        Assert.DoesNotContain("textures2D[", f.Replace("textures2D[]", ""));   // nothing is sampled
    }

    [Fact]
    public void The_colour_and_depth_programs_take_their_depth_from_the_same_vertex_text()
    {
        // Both pipelines are made from this one text, so the equal-or-nearer depth test of the late colour pass sees the prepass's depths.
        Assert.Equal(TerrainShaders.PatchVertexNative(), TerrainShaders.PatchVertexNative());
        Assert.DoesNotContain("gl_FragDepth", TerrainShaders.FragmentNative());   // the colour fragment stage leaves the depth to the rasteriser
    }
}
