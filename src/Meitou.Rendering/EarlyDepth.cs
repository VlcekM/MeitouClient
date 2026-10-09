namespace Meitou.Rendering;

/// <summary>
/// The switch for the objects', rocks' and foliage meshes' early depth test (docs/render-objects.md "Early depth test"): a fragment shader that can
/// <c>discard</c> gets its depth test after it runs, so every hidden fragment is shaded. With this on, the LOD cross-fade dither's <c>discard</c> (and
/// the cut-out's, where the material has none) lives only in the programs for instances that are fading or cut out; the rest use programs without a
/// <c>discard</c>. Off is the single program each draw used before (<c>--ab early-depth</c>: A on, B off).
/// </summary>
public static class EarlyDepth
{
    public static bool Enabled { get; set; } = true;
}
