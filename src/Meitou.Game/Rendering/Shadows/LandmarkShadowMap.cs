using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Shadows;

/// <summary>
/// The landmark shadow map's state (docs/formats/shadows.md, "Landmark shadows"): the map, the box it was fitted to, and what it was drawn for (the
/// set of landmarks, the sun), so it is drawn again only when either changes. <see cref="ShadowPass"/> draws it.
/// </summary>
sealed class LandmarkShadowMap : IDisposable
{
    /// <summary>
    /// The context the map is made on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The map, its comparison sampling, and whether the receivers read it yet.
    /// </summary>
    GpuTexture? _landmarkMap;

    /// <summary>
    /// The map with its linear comparison sampler.
    /// </summary>
    SampledTexture _landmarkSampled;

    /// <summary>
    /// The receivers read the map (from the frame it was first published).
    /// </summary>
    bool _landmarkPublished;

    /// <summary>
    /// The key of the landmark set and the sun the map was drawn for.
    /// </summary>
    long _landmarkKey;

    /// <summary>
    /// The sun's direction the map was drawn for.
    /// </summary>
    Vector3 _landmarkSun;

    /// <summary>
    /// The landmarks' bounding spheres, collected each frame.
    /// </summary>
    readonly List<Vector4> _landmarkSpheres = [];

    /// <summary>
    /// A landmark map on <paramref name="gpu"/>, made when first drawn.
    /// </summary>
    public LandmarkShadowMap(IGpuContext gpu) => _gpu = gpu;

    /// <summary>
    /// The box the map was fitted to, or null while there is none to read.
    /// </summary>
    public ShadowCascade? Box { get; private set; }

    /// <summary>
    /// How often the landmark map was drawn, and the landmarks in it the last time (for the log).
    /// </summary>
    public int Draws { get; private set; }

    /// <summary>
    /// The landmarks in the landmark map the last time it was drawn (for the log).
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// What the receivers sample (<c>uShadowLandmark</c>): the map once published, else the stand-in.
    /// </summary>
    public SampledTexture Published => _landmarkPublished ? _landmarkSampled : default;

    /// <summary>
    /// The receivers read the map from now on (the Meitou block that places it is published).
    /// </summary>
    public void MarkPublished() => _landmarkPublished = true;

    /// <summary>
    /// The box to draw the map into this frame (<see cref="MeitouShadowFit.FitLandmarks"/>), with the landmark set's <paramref name="key"/>; null when
    /// the map stays as it is (none: <see cref="Box"/> is cleared; else the same set and sun).
    /// </summary>
    public ShadowCascade? Plan(Vector3 toSun, ShadowPass.LandmarkCasters? landmarks, out long key)
    {
        key = 0;
        if (landmarks is not { } l)
        {
            Box = null;
            return null;
        }

        key = l.Collect(_landmarkSpheres);
        if (_landmarkSpheres.Count == 0)
        {
            Box = null;
            return null;
        }

        bool sameSun = Vector3.Dot(toSun, _landmarkSun) > MathF.Cos(TerrainShadowMap.RebuildAngle);
        if (Box is not null && key == _landmarkKey && sameSun) return null;

        return MeitouShadowFit.FitLandmarks(toSun, _landmarkSpheres, ShadowPass.LandmarkMapSize)!;
    }

    /// <summary>
    /// Makes the landmark map and its sampler on first use: a map of its own (4096², larger than a cascade layer), read through its own sampler.
    /// </summary>
    public GpuTexture EnsureMap()
    {
        if (_landmarkMap is not null) return _landmarkMap;

        // A map of its own (4096², larger than a cascade layer), read through its own sampler (uShadowLandmark), not a layer of the cascade array.
        _landmarkMap = _gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, ShadowPass.LandmarkMapSize, ShadowPass.LandmarkMapSize,
            Use: TextureUse.Sampled | TextureUse.DepthTarget, Name: "landmark shadow map"));
        _landmarkSampled = ShadowPass.Sampled(_gpu, _landmarkMap, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge,
            compare: true);
        return _landmarkMap;
    }

    /// <summary>
    /// The map was drawn into <paramref name="box"/> for the landmark set <paramref name="key"/> and the sun <paramref name="toSun"/>.
    /// </summary>
    public void Drawn(ShadowCascade box, long key, Vector3 toSun)
    {
        (Box, _landmarkKey, _landmarkSun) = (box, key, toSun);
        Draws++;
        Count = _landmarkSpheres.Count;
    }

    /// <summary>
    /// The landmark map's texel size in units for the log, or "off".
    /// </summary>
    public string Texel() => Box is { } b ? $"{b.Texel:0} units per texel" : "off";

    /// <summary>
    /// Frees the map; the receivers stop reading it.
    /// </summary>
    public void Dispose()
    {
        _landmarkPublished = false;
        _landmarkMap?.Dispose();
    }
}
