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

/// <summary>
/// The ground march in a pass of its own before the resolve (docs/render-terrain.md "Relief pass"): the resolve reads its result, so the march's
/// registers do not weigh on the shading. On Metal by default, where it saves time; Vulkan marches in the resolve (measured on a Radeon: the pass
/// costs more there). <c>MEITOU_TERRAIN_RELIEF_PASS=0</c> or <c>1</c> forces it off or on, for comparing.
/// </summary>
public static class TerrainReliefPass
{
    /// <summary>
    /// The march runs in its own pass (true) or in the resolve's program (false); null: the renderer's default (TerrainRenderer.ReliefPassWanted).
    /// </summary>
    public static bool? Enabled { get; set; } = Environment.GetEnvironmentVariable("MEITOU_TERRAIN_RELIEF_PASS") switch
    {
        "0" => false,
        "1" => true,
        _ => null,
    };
}
