namespace Meitou.Rendering;

/// <summary>
/// The terrain's cliff weight threshold (docs/render-terrain.md, "Layer weight threshold"): the cliff layer (two projections, diffuse and normal, 4 of the
/// 11 layer samples of a pixel) is not sampled where its weight is small. The shader drops weights up to <see cref="CliffEps"/> and ramps the next
/// <see cref="CliffEps"/> in (<c>w * smoothstep(eps, 2 eps, w)</c>), so there is no seam where the layer starts to count; above twice the threshold the
/// picture is the unchanged one. The switch is the A/B <c>terrain-layer-skip</c> ; the terrain patches
/// only (the TERRAIN-mode rocks and their bake keep every weight).
/// </summary>
public static class TerrainLayerSkip
{
    public static bool Enabled { get; set; } = true;

    /// <summary>The threshold.</summary>
    public const float CliffEps = 0.03f;
}
