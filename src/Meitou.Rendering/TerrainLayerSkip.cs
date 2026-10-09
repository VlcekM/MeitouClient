namespace Meitou.Rendering;

/// <summary>
/// The terrain's cliff weight threshold (docs/render-terrain.md, "Layer weight threshold"): the cliff layer (two projections, diffuse and normal, 4 of the
/// 11 layer samples of a pixel) is not sampled where its weight is small. The shader drops weights up to <see cref="CliffEps"/> and ramps the next
/// <see cref="CliffEps"/> in (<c>w * smoothstep(eps, 2 eps, w)</c>), so there is no seam where the layer starts to count; above twice the threshold the
/// picture is the unchanged one. The switch is the A/B <c>terrain-layer-skip</c> (<c>MEITOU_TERRAIN_LAYER_SKIP=0</c> starts it off); the terrain patches
/// only (the TERRAIN-mode rocks and their bake keep every weight).
/// </summary>
public static class TerrainLayerSkip
{
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("MEITOU_TERRAIN_LAYER_SKIP") != "0";

    /// <summary>The threshold (<c>MEITOU_TERRAIN_CLIFF_EPS</c>).</summary>
    public static float CliffEps { get; set; } =
        float.TryParse(Environment.GetEnvironmentVariable("MEITOU_TERRAIN_CLIFF_EPS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) && v >= 0 ? v : 0.03f;
}
