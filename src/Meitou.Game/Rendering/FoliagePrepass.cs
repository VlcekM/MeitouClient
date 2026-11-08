namespace Meitou.Rendering;

/// <summary>
/// The depth prepass for the foliage meshes (docs/render-foliage.md "Depth prepass"): in the main view the trees' and bushes' depth is drawn first with
/// a fragment stage that only does the fade dither and the cut-out's alpha test (<see cref="FoliageShaders.MeshPrepassFragment"/>, colour mask 0, or an
/// empty stage for fully visible instances without a cut-out), then the colour with no <c>discard</c> at all and the depth test EQUAL, so the lighting runs about
/// once per visible pixel instead of once per layer of leaves. The A/B <c>foliage-prepass</c> turns it off (the colour drawn once, as before).
/// </summary>
public static class FoliagePrepass
{
    public static bool Enabled { get; set; } = true;
}
