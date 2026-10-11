using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

public sealed unsafe partial class SkyRenderer
{
    VolumetricCloudRenderer? volume;
    SampledTexture volumeImage;
    bool volumeShadowsReady;
    bool VolumeEnabled => LitClouds && Physical && state.Valid && CloudDensity > 0;
    bool VolumeShadows => VolumeEnabled && CloudShadows && volumeShadowsReady && volume is not null;

    /// <summary>Records the volume's sun-transmission map before any world or reflection pass.</summary>
    public void PrepareCloudShadows()
    {
        volumeShadowsReady = false;
        if (!VolumeEnabled || !CloudShadows || state.Sun.Y <= 0.03f) return;
        volume ??= new VolumetricCloudRenderer(Gpu);
        volume.RenderShadow(Eye, CloudDensity, CloudLayer.TextureShift(cloudOffset), state.Sun);
        volumeShadowsReady = true;
    }

    /// <summary>
    /// The fog early-out of the volumetric clouds (Meitou, docs/render-clouds.md "Fog early-out"; <c>MEITOU_CLOUD_FOG_SKIP=0</c> starts with it off,
    /// <c>--ab cloud-fog-skip</c>): no march where the fog hides the sky anyway. The whole pass when the weather fog covers the sky (both views),
    /// and in the main view each texel the placed fog volumes hide completely.
    /// </summary>
    public bool CloudFogSkip { get; set; } = Environment.GetEnvironmentVariable("MEITOU_CLOUD_FOG_SKIP") != "0";

    /// <summary>
    /// The sky's fog-volume early-out (Meitou, docs/render-clouds.md "Fog early-out"; <c>MEITOU_SKY_FOG_SKIP=0</c> starts with it off, <c>--ab sky-fog-skip</c>):
    /// the frame loop leaves the main view's sky pass and cloud march out when the placed fog block hides the sky in every direction of the view
    /// (<see cref="FogVolumes.SkyHidden"/>); the scene's clear colour stands in, and the volume pass covers it.
    /// </summary>
    public bool SkyFogSkip { get; set; } = Environment.GetEnvironmentVariable("MEITOU_SKY_FOG_SKIP") != "0";

    /// <summary>Whether the main view's sky pass was left out in the last frame (<see cref="SkyFogSkip"/>; for the stats line).</summary>
    public bool SkySkipped { get; private set; }

    /// <summary>The main view's sky and clouds left out this frame (<see cref="SkyFogSkip"/>): no march, the history starts again when the sky shows.</summary>
    public void SkipMainSky()
    {
        SkySkipped = true;
        volumeImage = default;
        volume?.InvalidateMain();
    }

    /// <summary>Whether the last <see cref="PrepareClouds"/> left the march out because the weather fog covers the sky (for the stats line).</summary>
    public bool CloudsFogged { get; private set; }

    /// <summary>
    /// True when the sky pass's weather fog (the game's fog over pixels with no geometry: the ease-in-out curve of the far clip over the fog distance,
    /// times the weather's weight) is at least <see cref="FogComplete"/>, as in <see cref="FogCullDistanceOf"/>: the sky then shows the fog colour to
    /// 0.0002 whatever is drawn under it, clouds included. The sky shader applies that term after the clouds in either view, so it holds for both.
    /// </summary>
    public static bool WeatherFogHidesSky(in AtmosphereUniforms u)
    {
        if (!(u.Fog.Z > 0) || !(u.Haze.W > 0)) return false;
        float amount = Math.Clamp(u.Fog.W * u.Haze.W, 0f, 1f);
        float curve = (amount < 0.5f ? 2f * amount * amount : 1f - 2f * (amount - 1f) * (amount - 1f)) * u.Fog.Z;
        return curve >= FogComplete;
    }

    /// <summary>
    /// Ray marches and filters this view's clouds before opening its scene host. <paramref name="fogVolumes"/>: the fog volume pass will run over
    /// this view's finished sky (the main view with volumes in view), so texels it hides completely need no march.
    /// </summary>
    public void PrepareClouds(Matrix4x4 viewProjection, int width, int height, Vector3 eye, bool reflection = false, bool fogVolumes = false)
    {
        volumeImage = default;
        if (!reflection) CloudsFogged = SkySkipped = false;
        if (!VolumeEnabled) { volume?.Invalidate(); return; }
        if (CloudFogSkip && WeatherFogHidesSky(Uniforms()))
        {
            // The sky shader falls back to the flat layer, which the same fog covers; the history starts again when the fog thins.
            if (!reflection) CloudsFogged = true;
            volume?.Invalidate();
            return;
        }
        volume ??= new VolumetricCloudRenderer(Gpu);
        var ambient = state.CloudAmbient;
        float grey = MeitouClouds.AmbientGreyAt(CloudDensity);
        float luma = Vector3.Dot(ambient, new Vector3(0.2126f, 0.7152f, 0.0722f));
        ambient = Vector3.Lerp(ambient, new Vector3(luma), grey);
        volumeImage = volume.Render(viewProjection, eye, width, height, CloudDensity, CloudLayer.TextureShift(cloudOffset),
            state.CloudKey, state.CloudKeyDirection, ambient, reflection, CloudFogSkip && fogVolumes && !reflection);
    }
}