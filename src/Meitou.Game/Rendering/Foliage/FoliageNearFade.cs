using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// The near fade (Meitou, the "Near tree fade" row; docs/render-foliage.md "Near fade"): in the gameplay camera, large vegetation near the eye,
/// in the nearer part of the way to the camera's pivot, is dithered out round the middle of the view, so what the camera looks at stays visible.
/// The main view's cull marks the instances (<see cref="Mark"/>; laid out with the fading ones, so a trunk gets the program with the
/// <c>discard</c>), and the mesh fragment stages fade a marked instance's fragments by their distance from the eye and their angle off the view's axis (<see cref="FoliageShaders"/>).
/// The original draws every tree whole.
/// </summary>
public sealed class FoliageNearFade
{
    /// <summary>
    /// The packed fade a marked instance carries in row 0 w: drawn whole, as 2 (the mesh shaders test it between 1.1 and 1.4).
    /// </summary>
    public const float Mark = 1.25f;

    /// <summary>
    /// The chunk flag of a vegetation group (<see cref="FoliageSizes.IsVegetation"/>), whose instances the cull may mark.
    /// </summary>
    public const uint ChunkFlag = 32;

    /// <summary>
    /// The smallest placed bounding radius marked: the large size class's (<see cref="FoliageSizes.LargeFrom"/>; trees, not bushes).
    /// </summary>
    public const float MinRadius = FoliageSizes.LargeFrom;

    /// <summary>
    /// The share of the boom (eye to pivot) the fade reaches from the eye: trees in the nearer 30 % of the way fade.
    /// </summary>
    public const float BoomShare = 0.3f;

    /// <summary>
    /// The share of the reach over which the fade comes in towards the eye: whole beyond the reach, fully faded nearer than 60 % of it.
    /// </summary>
    public const float DepthBand = 0.4f;

    /// <summary>
    /// The opacity a marked tree keeps at the most faded (the share of its fragments the pattern keeps).
    /// </summary>
    public const float MinOpacity = 0.2f;

    /// <summary>
    /// The angles off the view's axis (radians): fully faded inside the inner one, whole outside the outer one.
    /// </summary>
    public const float InnerAngle = 0.14f, OuterAngle = 0.3f;

    /// <summary>
    /// The frame's <c>uFoliageNear</c>: xyz the view's forward axis, w the reach (0: no fade).
    /// </summary>
    Vector4 _uniform;

    /// <summary>
    /// Publishes <c>uFoliageNear</c> as a frame global.
    /// </summary>
    public FoliageNearFade(IGpuContext gpu) => gpu.Globals.PublishUniform("uFoliageNear", () => _uniform);

    /// <summary>
    /// The switch (Meitou, on by default; off: as the original).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the camera is the gameplay one (the strategy camera, not the free camera or a cinema shot); set by the game each frame, false in the
    /// viewer.
    /// </summary>
    public bool Gameplay { get; set; }

    /// <summary>
    /// The reach of this frame's main view (units from the eye; 0: no fade), set by <see cref="BeginView"/>.
    /// </summary>
    public float Reach { get; private set; }

    /// <summary>
    /// Before the main view's foliage: the reach and the view's axis from <paramref name="camera"/> while the fade is on and the camera is the
    /// gameplay one, else none.
    /// </summary>
    public void BeginView(WorldCamera camera)
    {
        Reach = Enabled && Gameplay ? camera.Distance * BoomShare : 0;
        _uniform = new Vector4(camera.Forward, Reach);
    }

    /// <summary>
    /// After the main view's foliage: no fade for the other views (the reflection, the shadow cascades).
    /// </summary>
    public void EndView()
    {
        Reach = 0;
        _uniform = default;
    }

    /// <summary>
    /// The cull's <c>ViewData.near</c> for a view with <paramref name="eyeY"/>: the eye's height, the reach and <see cref="MinRadius"/> (zero
    /// without a fade).
    /// </summary>
    public Vector4 CullVector(float eyeY) => Reach > 0 ? new Vector4(eyeY, Reach, MinRadius, 0) : default;
}
