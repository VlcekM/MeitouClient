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

    /// <summary>Ray marches and filters this view's clouds before opening its scene host.</summary>
    public void PrepareClouds(Matrix4x4 viewProjection, int width, int height, Vector3 eye, bool reflection = false)
    {
        volumeImage = default;
        if (!VolumeEnabled) { volume?.Invalidate(); return; }
        volume ??= new VolumetricCloudRenderer(Gpu);
        var ambient = state.CloudAmbient;
        float grey = MeitouClouds.AmbientGreyAt(CloudDensity);
        float luma = Vector3.Dot(ambient, new Vector3(0.2126f, 0.7152f, 0.0722f));
        ambient = Vector3.Lerp(ambient, new Vector3(luma), grey);
        volumeImage = volume.Render(viewProjection, eye, width, height, CloudDensity, CloudLayer.TextureShift(cloudOffset),
            state.CloudKey, state.CloudKeyDirection, ambient, reflection);
    }
}