using Meitou.Rendering;

namespace Meitou.ModelViewer;

static partial class WorldApp
{
    /// <summary>The A/B switches of the terrain stage's optimisations (docs/render-terrain.md, "Cutting the terrain's cost"); A = on.</summary>
    static void RegisterTerrainAb()
    {
        AbToggles.Register("terrain-layer-skip", () => TerrainLayerSkip.Enabled, v => TerrainLayerSkip.Enabled = v, "terrain cliff layer not sampled at small weights, the weight ramped in smoothly (MEITOU_TERRAIN_CLIFF_EPS, default 0.03; B: every nonzero weight)");
        AbToggles.Register("terrain-late-colour", () => TerrainLateColour.Enabled, v => TerrainLateColour.Enabled = v, "main view: the terrain colour drawn after the objects and foliage over a depth-only terrain pass, so it is shaded only where it is seen (B: the colour first, as before)");
    }
}
