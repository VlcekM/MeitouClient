using Meitou.Rendering;

namespace Meitou.ModelViewer;

static partial class WorldApp
{
    /// <summary>The A/B switches of the terrain stage's optimisations (docs/render-terrain.md, "Cutting the terrain's cost"); A = on.</summary>
    static void RegisterTerrainAb()
    {
        AbToggles.Register("terrain-layer-skip", () => TerrainLayerSkip.Enabled, v => TerrainLayerSkip.Enabled = v, "terrain cliff layer not sampled at small weights, the weight ramped in smoothly (MEITOU_TERRAIN_CLIFF_EPS, default 0.03; B: every nonzero weight)");
    }
}
