using Meitou.Game.Rendering.Bench;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Terrain;


namespace Meitou.ModelViewer;

/// <summary>
/// The world viewer's terrain A/B switches.
/// </summary>
static partial class WorldApp
{
    /// <summary>
    /// The A/B switches of the terrain stage's optimisations (docs/render-terrain.md, "Cutting the terrain's cost"); A = on.
    /// </summary>
    internal static void RegisterTerrainAb()
    {
        AbToggles.Register("terrain-layer-skip", () => TerrainLayerSkip.Enabled, v => TerrainLayerSkip.Enabled = v,
                "terrain cliff layer not sampled at small weights, the weight ramped in smoothly (B: every nonzero weight)");
        AbToggles.Register("lean-lighting", () => LeanLighting.Enabled, v => LeanLighting.Enabled = v,
                "without sun shadows and probe GI, objects, terrain and characters drawn with programs built without their lookups "
                + "(B: the full programs)");
        AbToggles.Register("terrain-simple", () => TerrainSimpleShading.Enabled, v => TerrainSimpleShading.Enabled = v,
                "the simple material: the dominant biome only, its base and strongest layer, no normal maps (B: the full blend)");
        AbToggles.Register("terrain-refine", () => GroundRelief.RefinePixels > 0, v => GroundRelief.RefinePixels = v ? 0.5f : 0,
                "every main-view patch tessellated, edges split where the terrain leaves them by over half a pixel (B: the plain patches)");
        AbToggles.Register("rock-smoothing", () => RockSmoothing.TessellateActive, v => RockSmoothing.TessellateActive = v,
                "TERRAIN-mode rocks Phong-tessellated (B: their flat triangles)");
        AbToggles.Register("terrain-material-cache", () => TerrainMaterialCache.Enabled, v => TerrainMaterialCache.Enabled = v,
                "patch pixels near the eye read the layered material from world-space levels baked round it "
                + "(B: every pixel shades the biomes' layers)");
        AbToggles.Register("terrain-late-colour", () => TerrainLateColour.Enabled, v => TerrainLateColour.Enabled = v,
                "main view: the terrain colour drawn after the objects and foliage over a depth-only terrain pass, "
                + "so it is shaded only where it is seen (B: the colour first, as before)");
    }
}
