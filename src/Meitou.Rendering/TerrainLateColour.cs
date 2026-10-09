namespace Meitou.Rendering;

/// <summary>
/// The terrain's colour after the occluders (docs/render-terrain.md, "Terrain colour after the occluders"): the main view draws the terrain's depth first
/// (patches only, no fragment work), then the objects and foliage, and the terrain colour last with the depth test on and no depth write, so the material
/// and lighting run only where the terrain is what is seen (a third of the shaded terrain used to be drawn over by buildings, trees, rocks and grass).
/// The A/B <c>terrain-late-colour</c>; <c>MEITOU_TERRAIN_LATE=0</c> starts it off (the colour drawn first, as before).
/// </summary>
public static class TerrainLateColour
{
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("MEITOU_TERRAIN_LATE") != "0";
}
