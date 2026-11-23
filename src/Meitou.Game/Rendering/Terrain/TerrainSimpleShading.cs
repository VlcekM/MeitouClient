namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// The simple terrain material for weak GPUs (docs/render-terrain.md "Simple material"; Graphics > World "Simple terrain shading", on at the Low
/// preset and with <c>--low-end</c>): a blended patch pixel shades only its dominant biome, and in it the base layer, the strongest of grass, slope
/// and dirt, the road and the cliff's stronger projection, without normal maps. Its own compiled colour programs (<c>TERRAIN_SIMPLE</c>), so the
/// full blend pays nothing for it. Map features, the material cache's bake and the rock bake keep the full material. A/B <c>terrain-simple</c>.
/// </summary>
public static class TerrainSimpleShading
{
    /// <summary>
    /// The simple material is drawn (false: every biome and layer).
    /// </summary>
    public static bool Enabled { get; set; }
}
