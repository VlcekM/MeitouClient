namespace Meitou.Rendering.Impostors;

/// <summary>
/// The depth prepass for the billboards (docs/render-foliage.md "Foliage cost in the orbit"), the foliage meshes' prepass (<see cref="FoliagePrepass"/>) for the trees
/// and bushes beyond the mesh distance: in the main colour view the billboards are drawn first with a fragment stage that only decides whether the pixel exists
/// (<see cref="ImpostorShaders.PrepassFragment"/>, colour mask 0), then the lighting with no <c>discard</c> and the depth test EQUAL
/// (<see cref="ImpostorShaders.FragmentAfterPrepass"/>), so a pixel is lit once however many billboards overlap it. The A/B <c>impostor-prepass</c>;
/// It needs the main view's prepass gate (<see cref="FoliagePrepass.Enabled"/>).
/// </summary>
public static class ImpostorPrepass
{
    public static bool Enabled { get; set; } = true;
}
