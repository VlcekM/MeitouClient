using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Ogre;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The sky's textures (the starfield, the planets, the cloud layer's maps) and the game's lighting textures (the irradiance and specular cubes, the
/// ambient map), each null when its file was not found.
/// </summary>
sealed class SkyTextures : IDisposable
{
    /// <summary>
    /// The context the textures are made on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The sky's native textures with the GL sampler state their GL versions had (phase 8 stage 2): stars, cloud layer (colour, normal, tile and
    /// their smooth variants), irradiance and specularity cubes and the ambient map; null when the file was not found.
    /// </summary>
    SampledImage? _starsTexture, _cloudsTexture, _cloudsNormalTexture, _cloudsTileTexture, _cloudsSmooth, _cloudsTileSmooth, _irradianceCube,
        _specularCube, _ambientMap;

    /// <summary>
    /// The game's two planets' textures (<see cref="SkyPlanet.All"/> order); null when the file was not found.
    /// </summary>
    readonly SampledImage?[] _planetTextures = new SampledImage?[SkyPlanet.All.Length];

    /// <summary>
    /// A sky with no textures until <see cref="Load"/>.
    /// </summary>
    public SkyTextures(IGpuContext gpu) => _gpu = gpu;

    /// <summary>
    /// The planet's mean colour (<see cref="Meitou.Data.World.Planetshine.MeanAlbedo(Meitou.Data.Textures.DdsFile)"/>), read when the texture loads.
    /// </summary>
    public Vector3 PlanetAlbedo { get; private set; } = new(Meitou.Data.World.Planetshine.FallbackAlbedo);

    /// <summary>
    /// The irradiance cube (<c>mp_irradiance.dds</c>), or null.
    /// </summary>
    public SampledImage? Irradiance => _irradianceCube;

    /// <summary>
    /// The specular cube (<c>mp_specularity.dds</c>), or null.
    /// </summary>
    public SampledImage? Specular => _specularCube;

    /// <summary>
    /// The per-biome ambient map (<see cref="LoadAmbientMap"/>), or null.
    /// </summary>
    public SampledImage? Ambient => _ambientMap;

    /// <summary>
    /// The cloud layer's smoothed copy (<see cref="MeitouClouds.SmoothRed"/>), or null.
    /// </summary>
    public SampledImage? CloudsSmooth => _cloudsSmooth;

    /// <summary>
    /// The cloud tile's smoothed copy, or null.
    /// </summary>
    public SampledImage? CloudsTileSmooth => _cloudsTileSmooth;

    /// <summary>
    /// Which of the game's lighting textures were found (irradiance cube, specular cube, ambient map).
    /// </summary>
    public string LightingTextures => $"irradiance {YesNo(_irradianceCube)}, specularity {YesNo(_specularCube)}, ambient map {YesNo(_ambientMap)}";

    /// <summary>
    /// "yes" or "no", for the texture report: whether the texture was found.
    /// </summary>
    static string YesNo(object? found) => found is not null ? "yes" : "no";

    /// <summary>
    /// Loads the sky's textures from <paramref name="assets"/> and says which were found.
    /// </summary>
    public void Load(AssetLocator assets)
    {
        LoadTextures(assets);
        string starfield = YesNo(_starsTexture);
        string planets = string.Join(" ", SkyPlanet.All.Select((p, i) => $"{p.Material} {YesNo(_planetTextures[i])}"));
        string clouds = YesNo(_cloudsTexture);
        Console.WriteLine($"sky       textures: starfield {starfield}, planets {planets}, clouds {clouds}");
    }

    /// <summary>
    /// The starfield, the planets, the cloud layer's maps and the lighting cubes, each when its file is found.
    /// </summary>
    void LoadTextures(AssetLocator assets)
    {
        try
        {
            // The whole 4096² file (BC1, 13 levels), repeating: the dome's coordinates wrap as the starfield shifts. The game samples its top level
            // only (`filtering linear linear none`); the viewer filters trilinearly, which takes the top level at the game's screen sizes and keeps a
            // smaller view from sparkling.
            if (assets.Find("SkyX_Starfield.dds") is { } stars) _starsTexture = SampledImage.FromDds(_gpu, DdsReader.ReadFile(stars), repeat: true,
                    "sky stars");
            for (int i = 0; i < SkyPlanet.All.Length; i++)
                if (assets.Find(SkyPlanet.All[i].Texture) is { } planet)
                {
                    var dds = DdsReader.ReadFile(planet);
                    _planetTextures[i] = SampledImage.FromDds(_gpu, dds, repeat: true, $"sky planet {SkyPlanet.All[i].Material}");
                    // The planetshine's colour, from a low level of the map.
                    if (i == 0) PlanetAlbedo = Meitou.Data.World.Planetshine.MeanAlbedo(dds);
                }
            if (assets.Find("Clouds.dds") is { } clouds)
            {
                var img = TextureLoader.LoadFile(clouds, allMips: false).Levels[0];
                _cloudsTexture = SampledImage.Rgba8(_gpu, img, repeat: true, mipmaps: true, "sky clouds");
                _cloudsSmooth = SampledImage.Rgba8(_gpu,
                    new RgbaImage(img.Width, img.Height, MeitouClouds.SmoothRed(img.Pixels, img.Width, img.Height)), repeat: true,
                    mipmaps: true, "sky clouds smooth");
            }
            if (assets.Find("CloudsNormal.dds") is { } cloudsNormal)
            {
                var img = TextureLoader.LoadFile(cloudsNormal, allMips: false).Levels[0];
                _cloudsNormalTexture = SampledImage.Rgba8(_gpu, img, repeat: true, mipmaps: true, "sky clouds normal");
            }
            if (assets.Find("CloudsTile.dds") is { } cloudsTile)
            {
                var img = TextureLoader.LoadFile(cloudsTile, allMips: false).Levels[0];
                _cloudsTileTexture = SampledImage.Rgba8(_gpu, img, repeat: true, mipmaps: true, "sky clouds tile");
                _cloudsTileSmooth = SampledImage.Rgba8(_gpu,
                    new RgbaImage(img.Width, img.Height, MeitouClouds.SmoothRed(img.Pixels, img.Width, img.Height)), repeat: true,
                    mipmaps: true, "sky clouds tile smooth");
            }
            if (assets.Find("mp_irradiance.dds") is { } irradiance) _irradianceCube = SampledImage.Cube(_gpu, DdsReader.ReadFile(irradiance),
                    "sky irradiance");
            if (assets.Find("mp_specularity.dds") is { } specularity) _specularCube = SampledImage.Cube(_gpu, DdsReader.ReadFile(specularity),
                    "sky specularity");
        }
        catch (Exception e) when (e is DdsFormatException or IOException or InvalidOperationException)
        {
            Console.WriteLine($"warning   sky textures: {e.Message}");
        }
    }

    /// <summary>
    /// The ambient map, built the game's way from <c>biomemap.png</c> and the BIOMES records (<see cref="AmbientMap"/>).
    /// </summary>
    public void LoadAmbientMap(GameInstall install, GameDatabase db)
    {
        try
        {
            var biomes = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BiomeMap)));
            var map = AmbientMap.Build(db, biomes);
            _ambientMap?.Dispose();
            _ambientMap = SampledImage.Rgba8(_gpu, map, repeat: false, mipmaps: false, "sky ambient map");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException)
        {
            Console.WriteLine($"warning   ambient map: {e.Message}");
        }
    }

    /// <summary>
    /// Which of the textures the sky shader has (<c>uHas</c>: stars, planet 0, cloud textures (all three), planet 1).
    /// </summary>
    public void SetPresence(SkyProg program)
    {
        bool clouds = _cloudsTexture is not null && _cloudsNormalTexture is not null && _cloudsTileTexture is not null;
        float stars = _starsTexture is not null ? 1f : 0f;
        float planet0 = _planetTextures[0] is not null ? 1f : 0f, planet1 = _planetTextures[1] is not null ? 1f : 0f;
        program.P.Set(program.Has, stars, planet0, clouds ? 1f : 0f, planet1);
    }

    /// <summary>
    /// Binds the textures the sky has (a missing one reads the stand-in).
    /// </summary>
    public void Bind(SkyProg program)
    {
        var p = program.P;
        // A missing texture reads GL's stand-in, which an unbound sampler does too.
        if (_starsTexture is not null) p.Bind(program.Stars, _starsTexture.Sampled());
        if (_planetTextures[0] is { } planet0) p.Bind(program.Planet0, planet0.Sampled());
        if (_planetTextures[1] is { } planet1) p.Bind(program.Planet1, planet1.Sampled());
        if (_cloudsTexture is not null) p.Bind(program.Clouds, _cloudsTexture.Sampled());
        if (_cloudsNormalTexture is not null) p.Bind(program.CloudsNormal, _cloudsNormalTexture.Sampled());
        if (_cloudsTileTexture is not null) p.Bind(program.CloudsTile, _cloudsTileTexture.Sampled());
        if (_cloudsSmooth is not null) p.Bind(program.CloudsSmooth, _cloudsSmooth.Sampled());
        if (_cloudsTileSmooth is not null) p.Bind(program.CloudsTileSmooth, _cloudsTileSmooth.Sampled());
    }

    /// <summary>
    /// Frees every texture.
    /// </summary>
    public void Dispose()
    {
        var textures = new[]
        {
            _starsTexture, _cloudsTexture, _cloudsNormalTexture, _cloudsTileTexture, _cloudsSmooth, _cloudsTileSmooth, _irradianceCube,
            _specularCube, _ambientMap,
        };
        foreach (var t in textures) t?.Dispose();
        foreach (var t in _planetTextures) t?.Dispose();
    }
}
