using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The sky's values a cloud pass reads: the slab's base height, the cloud density c and the wind's texture shift (<see
/// cref="CloudLayer.TextureShift"/>).
/// </summary>
readonly record struct CloudSlab(float Bottom, float Density, Vector2 Shift);

/// <summary>
/// The Meitou volumetric clouds of the sky (docs/render-clouds.md): the cloud volume with this frame's march image and shadow map, and the frame's
/// skip switches and flags. <see cref="SkyRenderer"/> decides from its settings and this frame's state what runs; this class owns the volume and
/// runs it.
/// </summary>
public sealed class SkyClouds : IDisposable
{
    /// <summary>
    /// The context the volume is made on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The cloud volume, made with the first frame that could show it (<see cref="EnsureVolume"/>); null before.
    /// </summary>
    VolumetricCloudRenderer? _volume;

    /// <summary>
    /// The view's marched and filtered clouds, or null when this view has none (<see cref="BeginView"/>).
    /// </summary>
    SampledTexture _volumeImage;

    /// <summary>
    /// Whether this frame's sun-transmission map was recorded (<see cref="RenderShadow"/>).
    /// </summary>
    bool _volumeShadowsReady;

    /// <summary>
    /// Clouds for the sky made on <paramref name="gpu"/>; the volume itself comes with the first frame that could show it.
    /// </summary>
    public SkyClouds(IGpuContext gpu) => _gpu = gpu;

    /// <summary>
    /// This frame's main-view volumetric clouds for the motion vectors, or null (no volume, or not marched this frame).
    /// </summary>
    public CloudMotion? CloudMotion => _volume?.MainMotion is { } m && m.Frame == _gpu.Frame.Number ? m : null;

    /// <summary>
    /// The host's loading warm-up: the volume and its shadow map are drawn whatever the weather and the hour (a clear sky draws them empty), so that
    /// their pipelines are made under the loading cover and not when the first clouds come (seconds with a cold driver cache; docs/bench.md "Play
    /// benchmark").
    /// </summary>
    public bool Warm { get; set; }

    /// <summary>
    /// The fog early-out of the volumetric clouds (Meitou, docs/render-clouds.md "Fog early-out";
    /// <c>--ab cloud-fog-skip</c>): no march where the fog hides the sky anyway. The whole pass when the weather fog covers the sky (both views),
    /// and in the main view each texel the placed fog volumes hide completely.
    /// </summary>
    public bool CloudFogSkip { get; set; } = true;

    /// <summary>
    /// The sky's fog-volume early-out (Meitou, docs/render-clouds.md "Fog early-out"; <c>--ab sky-fog-skip</c>): the frame loop leaves the main
    /// view's sky pass and cloud march out when the placed fog block hides the sky in every direction of the view (<see
    /// cref="FogVolumes.SkyHidden"/>); the scene's clear colour stands in, and the volume pass covers it.
    /// </summary>
    public bool SkyFogSkip { get; set; } = true;

    /// <summary>
    /// Whether the main view's sky pass was left out in the last frame (<see cref="SkyFogSkip"/>; for the stats line).
    /// </summary>
    public bool SkySkipped { get; private set; }

    /// <summary>
    /// Whether the last <see cref="SkyRenderer.PrepareClouds"/> left the march out because the weather fog covers the sky (for the stats line).
    /// </summary>
    public bool CloudsFogged { get; private set; }

    /// <summary>
    /// The view's marched clouds for the sky pass, or null (none prepared for this view).
    /// </summary>
    internal SampledTexture VolumeImage => _volumeImage;

    /// <summary>
    /// Whether this frame's sun-transmission map is there to shade the land with.
    /// </summary>
    internal bool ShadowsReady => _volumeShadowsReady && _volume is not null;

    /// <summary>
    /// The shadow map's world origin (valid while <see cref="ShadowsReady"/>).
    /// </summary>
    internal Vector2 ShadowOrigin => _volume!.ShadowOrigin;

    /// <summary>
    /// The sun-transmission map (valid while <see cref="ShadowsReady"/>).
    /// </summary>
    internal SampledTexture ShadowMap => _volume!.ShadowMap;

    /// <summary>
    /// The main view's sky and clouds left out this frame (<see cref="SkyFogSkip"/>): no march, the history starts again when the sky shows.
    /// </summary>
    public void SkipMainSky()
    {
        SkySkipped = true;
        _volumeImage = default;
        _volume?.InvalidateMain();
    }

    /// <summary>
    /// Makes the volume when there is none yet.
    /// </summary>
    internal void EnsureVolume() => _volume ??= new VolumetricCloudRenderer(_gpu);

    /// <summary>
    /// Forgets this frame's shadow map, before the sky decides whether to record one.
    /// </summary>
    internal void DropShadows() => _volumeShadowsReady = false;

    /// <summary>
    /// Records the volume's sun-transmission map for the <paramref name="sun"/> over <paramref name="slab"/> round <paramref name="eye"/>.
    /// </summary>
    internal void RenderShadow(Vector3 eye, CloudSlab slab, Vector3 sun)
    {
        _volume ??= new VolumetricCloudRenderer(_gpu);
        _volume.Bottom = slab.Bottom;
        _volume.RenderShadow(eye, slab.Density, slab.Shift, sun);
        _volumeShadowsReady = true;
    }

    /// <summary>
    /// Starts a view's clouds with none prepared; the main view (not <paramref name="reflection"/>) clears the skip flags of the stats line.
    /// </summary>
    internal void BeginView(bool reflection)
    {
        _volumeImage = default;
        if (!reflection) CloudsFogged = SkySkipped = false;
    }

    /// <summary>
    /// Drops the volume's histories (no march this view: the volume is off).
    /// </summary>
    internal void Invalidate() => _volume?.Invalidate();

    /// <summary>
    /// The weather fog covers the sky: no march, flagged for the stats line in the main view, the histories dropped.
    /// </summary>
    internal void FogCovers(bool reflection)
    {
        if (!reflection) CloudsFogged = true;
        _volume?.Invalidate();
    }

    /// <summary>
    /// Ray marches and filters a view's clouds over <paramref name="slab"/>, lit by <paramref name="s"/>'s key light and ambient (the ambient
    /// greyed by the cover). <paramref name="fogVolumes"/>: the fog volume pass runs over this view's sky, so the texels it hides need no march.
    /// </summary>
    internal void Render(in SkyRenderer.State s, Matrix4x4 viewProjection, Vector3 eye, int width, int height, CloudSlab slab, bool reflection,
        bool fogVolumes)
    {
        _volume ??= new VolumetricCloudRenderer(_gpu);
        _volume.Bottom = slab.Bottom;
        var ambient = s.CloudAmbient;
        float grey = MeitouClouds.AmbientGreyAt(slab.Density);
        float luma = Vector3.Dot(ambient, new Vector3(0.2126f, 0.7152f, 0.0722f));
        ambient = Vector3.Lerp(ambient, new Vector3(luma), grey);
        _volumeImage = _volume.Render(viewProjection, eye, width, height, slab.Density, slab.Shift,
            s.CloudKey, s.CloudKeyDirection, ambient, reflection, CloudFogSkip && fogVolumes && !reflection);
    }

    /// <summary>
    /// The cloud part of <see cref="SkyRenderer.DescribeCost"/>: the sky's skip, the fog's, or the clouds' GPU times (<paramref name="enabled"/>:
    /// the volume is on).
    /// </summary>
    internal string CostText(bool enabled) =>
        SkySkipped ? "; sky and clouds not drawn (a fog block hides the sky)"
        : _volume is null || !enabled ? ""
        : CloudsFogged ? $"; clouds not marched (the weather fog covers the sky), shadow map {_volume.ShadowGpuMs:0.00} ms"
        : $"; GPU clouds {_volume.MainGpuMs:0.00}, reflection {_volume.ReflectionGpuMs:0.00}, shadow map {_volume.ShadowGpuMs:0.00} ms";

    /// <summary>
    /// Frees the volume.
    /// </summary>
    public void Dispose()
    {
        _volumeShadowsReady = false;
        _volume?.Dispose();
        _volume = null;
    }
}
