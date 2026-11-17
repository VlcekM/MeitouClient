namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// The terrain's colour after the occluders (docs/render-terrain.md, "Terrain colour after the occluders"): the main view draws the terrain's depth
/// first (patches only, no fragment work), then the objects and foliage, and the terrain colour last with the depth test on and no depth write, so
/// the material and lighting run only where the terrain is what is seen (a third of the shaded terrain used to be drawn over by buildings, trees,
/// rocks and grass). The A/B <c>terrain-late-colour</c> (B: the colour drawn first, as before).
/// </summary>
public static class TerrainLateColour
{
    /// <summary>
    /// The terrain colour is drawn after the occluders (true, the default); false draws it first.
    /// </summary>
    public static bool Enabled { get; set; } = true;
}

/// <summary>
/// The resolved colour in two programs (docs/render-terrain.md "Resolved colour"; <see cref="TerrainShaders.ResolvePart"/>): the pixels the ground
/// march cannot reach are shaded without its code. The A/B is the console's <c>terrainsplit 0|1</c>.
/// </summary>
public static class TerrainResolveSplit
{
    /// <summary>
    /// The resolve is split (true, the default); false shades every pixel with the one full program.
    /// </summary>
    public static bool Enabled { get; set; } = true;
}
