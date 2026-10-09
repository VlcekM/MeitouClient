using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The programs for fully visible instances (<see cref="EarlyDepth"/>) must have no <c>discard</c> anywhere, or the GPU tests depth late again;
/// the programs for the dithered and cut-out ones keep theirs.</summary>
public class EarlyDepthTests
{
    static bool Discards(string source) => source.Contains("discard", StringComparison.Ordinal);

    [Fact]
    public void The_objects_solid_program_has_no_discard_and_the_other_keeps_both()
    {
        Assert.False(Discards(BuildingLodShaders.Fragment(solid: true)));
        string full = BuildingLodShaders.Fragment();
        Assert.True(full.Split("discard").Length - 1 >= 2, "the fade's and the cut-out's discard");
    }

    [Fact]
    public void The_foliage_meshes_solid_program_has_no_discard_and_the_other_keeps_the_fade_and_the_cut_out()
    {
        string solid = FoliageShaders.MeshFragment(solid: true);
        Assert.False(Discards(solid));
        Assert.Contains("foliageCoverage", solid);   // the output still reads it (1: no cut-out)
        Assert.True(FoliageShaders.MeshFragment().Split("discard").Length - 1 >= 3);
    }

    [Fact]
    public void The_terrain_meshes_solid_program_has_no_discard_and_the_other_keeps_the_fade()
    {
        Assert.False(Discards(TerrainShaders.MeshFragmentSolid));
        Assert.Contains("discard", TerrainShaders.MeshFragment);
    }
}
