using System.Diagnostics;
using Meitou.Content;
using Meitou.Data.Ogre;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Water;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// Builds the world frame's renderers (<see cref="WorldGpu"/>) for a loaded scene, step by step: terrain, sky, weather, particles, lamps,
/// water, objects, foliage, shadows, the crowd, GI and the memory guard.
/// </summary>
static class WorldGpuBuilder
{
    /// <summary>
    /// The weather scheduler's random seed (the default of <c>meitou-tools weather</c>, so the two agree).
    /// </summary>
    const int WeatherSeed = 1;

    /// <summary>
    /// The water reflection's range, a multiple of the haze distance (the game's own <c>reflection range</c> is 0.6; the viewer reflects further; the
    /// Tab slider moves it).
    /// </summary>
    const float ReflectionRange = 3;

    /// <summary>
    /// The renderer for <paramref name="scene"/>. <paramref name="step"/> is called before each of its steps (terrain, biomes, sky, water, objects,
    /// foliage, shadows) with the step's name: the game draws a loading frame there. <paramref name="wait"/> waits for the CPU work started on
    /// workers at the beginning (the terrain textures' and the water's files and bakes); the game draws loading frames while it waits.
    /// </summary>
    public static WorldGpu Create(IGpuContext context, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, bool interactive,
        Action<string>? step = null, Action<Task>? wait = null, bool terrainTextures = true, bool particlesEnabled = true, int objectLoadBudget = -1,
        Func<(ulong Used, ulong Budget)>? vramSample = null)
    {
        var b = new GpuBuild(context, install, scene, assets, o, step, wait);
        context.Samplers.MaxAnisotropy = o.Anisotropy;
        int layerSize = TerrainLayerSize(o);
        bool withTextures = !o.NoTextures && terrainTextures;

        // The files the terrain textures and the water read and bake (seconds of CPU work) are read on workers while the steps before them run.
        var colourEncode = withTextures && TerrainColourCache.Missing(install) ? Task.Run(() => EncodeColourTiles(install)) : null;
        var terrainSource = withTextures && scene.Database is { } textureDb ? Task.Run(() => TerrainTextures.Prepare(install, textureDb)) : null;
        var waterSource = !o.NoWater && scene.Database is { } waterDb ? Task.Run(() => WaterRenderer.Prepare(install, waterDb, assets,
            scene.Coarse)) : null;

        var (terrain, textures) = CreateTerrain(b, colourEncode, terrainSource, layerSize);
        var gpu = CreateSky(b, terrain);
        CreateWeather(b, gpu);
        if (particlesEnabled) CreateParticles(b, gpu, terrain);
        else b.Step("particles");
        CreateLamps(b, gpu);
        if (scene.Heightmap is not null)
            gpu.Streamer = new TerrainStreamer(install, terrain, textures, scene.Window.Step) { MaterialDistance = o.MaterialDistance };
        if (waterSource is not null) CreateWater(b, gpu, waterSource);
        if (scene.Objects is not null)
            CreateObjects(b, gpu, objectLoadBudget >= 0 ? objectLoadBudget : interactive ? 8 : 0);
        if (!o.NoFoliage && scene.Database is not null) CreateFoliage(b, gpu, terrain, interactive);
        CreateShadows(b, gpu);

        if (o.Crowd > 0 && scene.Database is not null)
            gpu.Characters = Characters.CrowdHarness.Create(context, install, scene, assets, o, interactive);
        if (gpu.Characters is { } crowd) (crowd.GeneratedLod, crowd.SkinShading) = (o.CharacterLod, o.SkinShading);
        gpu.DebugShadows = o.DebugShadows;

        b.Step("gi");
        if ((o.GiDebug > 0 || o.Gi) && context is GpuContext giContext) CreateGi(gpu, giContext, o);
        AttachGuard(gpu, context, vramSample);
        return gpu;
    }

    /// <summary>
    /// What the steps of one <see cref="Create"/> share: its inputs, the clock its log lines quote, and the wait for work started on workers.
    /// </summary>
    sealed class GpuBuild(IGpuContext context, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions options, Action<string>? step,
        Action<Task>? wait)
    {
        /// <summary>
        /// The device the world's renderers are made on.
        /// </summary>
        public IGpuContext Context => context;

        /// <summary>
        /// The game install the assets are read from.
        /// </summary>
        public GameInstall Install => install;

        /// <summary>
        /// The loaded region the renderers draw.
        /// </summary>
        public WorldScene Scene => scene;

        /// <summary>
        /// Where the meshes, textures and materials are found.
        /// </summary>
        public AssetLocator Assets => assets;

        /// <summary>
        /// The options the world was started with.
        /// </summary>
        public WorldOptions Options => options;

        /// <summary>
        /// Started with the build; its time is quoted in the build's log lines.
        /// </summary>
        public Stopwatch Watch { get; } = Stopwatch.StartNew();

        /// <summary>
        /// Names the next step (the game draws a loading frame).
        /// </summary>
        public void Step(string name) => step?.Invoke(name);

        /// <summary>
        /// The result of work started on a worker, the game drawing loading frames while it waits.
        /// </summary>
        public T Ready<T>(Task<T> work)
        {
            wait?.Invoke(work);
            return work.GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// The game's texture quality, set before any texture loads (the world's texture caches read it as they decode), and the terrain layers' size:
    /// their arrays have one size, so the Landscape group's drop (the game's textures are 2048²) lowers it instead of dropping mips per file.
    /// </summary>
    static int TerrainLayerSize(WorldOptions o)
    {
        Data.Textures.TextureQuality.Level = o.TextureQuality;
        int layerSize = Math.Max(2048 >> Data.Textures.TextureQuality.LevelsToDrop("terrain.dds", "Landscape"), 16);
        if (layerSize != 2048)
        {
            string label = Data.Textures.TextureQuality.Labels[o.TextureQuality];
            Console.WriteLine($"textures  quality {o.TextureQuality} ({label}): terrain layers {layerSize}² instead of 2048²");
        }
        return layerSize;
    }

    /// <summary>
    /// The terrain colour tiles BC3-encoded for the colour window, once (TerrainColourCache): a first start, or one after a tile changed, encodes
    /// them on workers.
    /// </summary>
    static (int Tiles, long Ms) EncodeColourTiles(GameInstall install)
    {
        var watch = Stopwatch.StartNew();
        int tiles = TerrainColourCache.EncodeMissing(install);
        return (tiles, watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// The terrain's heights, then (with <paramref name="source"/>, the files read on a worker) its biome textures.
    /// </summary>
    static (TerrainRenderer Terrain, TerrainTextures? Textures) CreateTerrain(GpuBuild b, Task<(int Tiles, long Ms)>? colourEncode,
        Task<TerrainTextures.Source>? source, int layerSize)
    {
        var scene = b.Scene;
        b.Step("terrain");
        var terrain = new TerrainRenderer(b.Context, scene.Coarse, scene.CoarseSize, scene.Window);
        Console.WriteLine($"uploaded  terrain heights: {terrain.LevelCount} LOD levels, "
            + $"finest {terrain.FinestSpacing:0.#} units ({b.Watch.ElapsedMilliseconds} ms)");

        if (colourEncode is not null)
        {
            if (!colourEncode.IsCompleted) b.Step("colourmap");
            var (tiles, ms) = b.Ready(colourEncode);
            Console.WriteLine($"colour    {tiles} terrain colour tiles encoded to BC3 and cached in {ms} ms");
        }

        if (source is null || scene.Database is null) return (terrain, null);
        b.Step("biomes");
        var textures = TerrainTextures.Create(b.Context, b.Install, scene.Database, b.Assets, layerSize, source: b.Ready(source));
        foreach (var m in textures.Messages.Take(20)) Console.WriteLine($"warning   {m}");
        Console.WriteLine($"biomes    {textures.TotalBiomes} in the world, {textures.TotalPairs} texture pairs, "
            + $"{textures.Capacity} near slots of {layerSize}² and "
            + $"{textures.FarSlots} far of {textures.FarSize}², BC3+BC1, {textures.ArrayBytes / 1048576} MB ({b.Watch.ElapsedMilliseconds} ms)");
        terrain.SetTextures(textures);
        return (terrain, textures);
    }

    /// <summary>
    /// The frame's state with the terrain, the post chain (Vulkan's; Metal supplies its own), the sky and the placed fog volumes.
    /// </summary>
    static WorldGpu CreateSky(GpuBuild b, TerrainRenderer terrain)
    {
        var o = b.Options;
        b.Step("sky");

        // WorldFrame.Draw runs a post chain through IWorldPostChain. Vulkan's PostProcess is complete here; Metal supplies its
        // PostComposite/PostTaa slice as a host-constructed chain and leaves the Vulkan-only reflection and GI paths null.
        var post = b.Context is GpuContext vulkanPost ? new PostProcess(vulkanPost, o.Post) : null;
        var gpu = new WorldGpu { Terrain = terrain, Sky = new SkyRenderer(b.Context,
            b.Assets) { CloudCoverage = o.Clouds }, Post = post, PostChain = post };
        post?.LoadHeatHaze(b.Assets);

        // The placed fog volumes (docs/formats/fogfeatures.md): the swamp's fog and the like, part of the game's look.
        // Always made: the weather's fog spheres go through it too, with or without a fogfeatures.dat.
        gpu.FogVolumes = new FogVolumes(b.Context, o.NoFogFeatures ? [] : FogFeatures.Load(b.Install));
        if (post is not null) post.FogVrs = true;   // takes effect where the device supports fragment shading rates

        var sky = gpu.Sky;
        sky.HazeStrength = o.HazeStrength;
        sky.MeitouHaze = o.MeitouHaze;
        sky.NightAir = o.NightAir;
        sky.Planetshine = o.Planetshine;
        sky.PlanetshineStrength = o.PlanetshineStrength;
        sky.MeitouStars = o.MeitouStars;
        sky.LitClouds = o.MeitouClouds;
        sky.CloudShadows = o.CloudShadows;
        sky.CloudFog = o.CloudFog;
        return gpu;
    }

    /// <summary>
    /// The sky's world data and the weather scheduler (docs/formats/weather.md): auto, or one forced record through the same path.
    /// </summary>
    static void CreateWeather(GpuBuild b, WorldGpu gpu)
    {
        var o = b.Options;
        b.Step("weather");
        if (b.Scene.Database is not { } skyDb) return;

        gpu.Sky.LoadWorld(b.Install, skyDb);   // the ambient map and the CONSTANTS exposure band (docs/formats/lighting.md)

        // Created on day 0; WorldWeather replays the schedule up to --day.
        var world = WeatherWorld.Create(skyDb, b.Install, WeatherSeed, WeatherTime.At(0));
        gpu.Weather = new WorldWeather(world, skyDb) { Day = o.Day ?? 0 };
        if (!o.AutoWeather)
        {
            if (!world.ForceWeather(o.Weather))
            {
                throw new ArgumentException($"no weather named '{o.Weather}'; "
                    + $"known: {string.Join(", ", SkyWeather.Names(skyDb).Distinct().Take(12))} ...");
            }

            gpu.Sky.Weather = SkyWeather.Find(skyDb, o.Weather) ?? SkyWeather.Default;
        }
        Console.WriteLine($"weather   {(o.AutoWeather ? $"scheduler, seed {WeatherSeed}, from day {o.Day ?? 0}" : $"forced {o.Weather}")}");

        // Wetness, dust and rain for the surface shaders come from the weather state every frame (Draw).
        gpu.Surfaces = new WeatherSurfaces(b.Context, b.Assets);
    }

    /// <summary>
    /// The particles: the weather's effects (rain, ash...) follow the weather state (UpdateParticles); the map's effect placers are always on
    /// (docs/formats/weather.md "Effect placers on the map"), so the particle renderer exists from the start whatever the weather.
    /// </summary>
    static void CreateParticles(GpuBuild b, WorldGpu gpu, TerrainRenderer terrain)
    {
        var o = b.Options;
        b.Step("particles");
        if (o.NoParticles || b.Scene.Database is not { } effectsDb) return;

        var watch = Stopwatch.StartNew();
        var particles = WorldFrame.EnsureParticles(gpu, b.Context, b.Assets);
        particles.PrewarmSeconds = o.ParticlePrewarm;
        gpu.ParticleDensity = o.ParticleDensity;
        particles.World.GroundHeight = terrain.HeightAt;   // main thread only: the height grid swaps as the terrain streams
        // for a weather warmed on a worker: the grid as it is when it starts
        particles.World.FreezeGroundHeight = () => terrain.Snapshot().HeightAt;
        gpu.WeatherAreas = WeatherAreas.Load(b.Install);
        particles.World.Area = RegionArea.At(gpu.WeatherAreas, b.Scene.Focus.X, b.Scene.Focus.Z);

        var placers = MapEffectPlacers.Find(effectsDb, MapFeatureFile.Open(b.Install));
        particles.SetPlacers(placers);
        gpu.MakeParticles = () => particles;
        Console.WriteLine($"particles {particles.Library.Systems.Count} systems read; {placers.Count} map placers "
            + $"in {particles.PlacerGroups.Count} groups ({watch.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// The lamps (docs/render-lights.md), including room lights, and the buildings' effects, read on workers from one level load.
    /// </summary>
    static void CreateLamps(GpuBuild b, WorldGpu gpu)
    {
        if (b.Scene.Database is not { } lampDb || b.Scene.Heightmap is not { } lampMap) return;
        var install = b.Install;
        var levels = new Lazy<(WorldLevelData Levels, BuildingLayouts Layouts)>(() =>
        {
            var l = WorldLevelData.Load(install, includeInteriors: true);
            var layouts = new BuildingLayouts(lampDb, l.Interiors);
            l.ApplyBuildingSwaps(lampDb, layouts);   // as the scene's objects: a swapped building or piece of furniture brings its own lamps
            ResidentRuins.Apply(lampDb, l, layouts);
            return (l, layouts);
        });

        gpu.Lamps = new WorldLamps(b.Context, () => WorldLights.ForWorld(lampDb, levels.Value.Levels, lampMap.HeightAt, levels.Value.Layouts))
        {
            InteriorAllDay = b.Options.InteriorLamps,
        };
        if (gpu.MakeParticles is not null)
            gpu.BuildingEffects = Task.Run(() => BuildingEffects.ForWorld(lampDb, levels.Value.Levels, lampMap.HeightAt, levels.Value.Layouts));
    }

    /// <summary>
    /// The water and its reflection pass, from the files read on a worker (<paramref name="source"/>).
    /// </summary>
    static void CreateWater(GpuBuild b, WorldGpu gpu, Task<WaterRenderer.Source> source)
    {
        var o = b.Options;
        var messages = new List<string>();
        b.Step("water");

        // The Meitou water's FFT, shore field and reflection are seam-neutral. Metal packs the two biome
        // parameter maps into one array and drops refraction (its scene copy is still Vulkan-only), which
        // leaves the Meitou fragment exactly at Metal's 16-sampler stage limit.
        // Vulkan always makes the Meitou water, so the Faithful / Meitou switch can turn it on while the game runs; another backend makes
        // it only when the run starts with it.
        bool meitouWater = o.MeitouWater, makeMeitou = meitouWater || b.Context is GpuContext;
        gpu.Water = WaterRenderer.Create(b.Context, b.Install, b.Scene.Database!, b.Assets, gpu.Sky, messages, b.Scene.Coarse, o.WaterGrid,
            o.WaterOcean,
            b.Ready(source), meitouWaves: makeMeitou, shoreField: makeMeitou);
        gpu.Water.Meitou = meitouWater;

        b.Step("reflections");
        gpu.Reflection = new ReflectionPass(b.Context) { Level = o.WaterReflection, Range = ReflectionRange, Samples = o.ReflectionSamples };
        foreach (var m in messages) Console.WriteLine($"warning   {m}");
        Console.WriteLine($"water     at height {WorldWater.Height} ({b.Watch.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// The objects. Landmarks (huge objects, drawn to their own distance) are kept apart from the zones only when the viewer starts with them
    /// (Meitou reach, distance above 0).
    /// </summary>
    static void CreateObjects(GpuBuild b, WorldGpu gpu, int loadBudget)
    {
        var o = b.Options;
        b.Step("objects");
        float landmarks = o.LandmarkDistanceFor(o.MeitouReach);
        gpu.Objects = new WorldObjectRenderer(b.Context, b.Assets, b.Scene.Objects!, landmarks: landmarks > 0)
        {
            ObjectDistance = o.ObjectDistanceFor(o.MeitouReach),
            LandmarkDistance = landmarks,
            DistantRange = o.DistantZones * WorldLayout.ZoneSize,
            NoDistant = o.DistantZones <= 0,
            LoadBudget = loadBudget,
            SortNearestFirst = o.MeitouReach,
            LodBias = 1 / o.ObjectLod,
        };
        Console.WriteLine($"objects   GPU ready ({b.Watch.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// The foliage, with its ranges, LOD, impostors and grass density from the options.
    /// </summary>
    static void CreateFoliage(GpuBuild b, WorldGpu gpu, TerrainRenderer terrain, bool interactive)
    {
        var o = b.Options;
        b.Step("foliage");
        var levels = b.Scene.Objects?.Levels ?? WorldLevelData.Load(b.Install);
        var f = gpu.Foliage = new FoliageRenderer(b.Context, b.Install, b.Scene.Database!, levels, b.Assets);
        if (!interactive) f.SwaySeconds = 0;   // offscreen pictures and benchmarks: the grass holds still, so a picture repeats exactly
        f.Terrain = terrain;
        f.MeitouRange = o.MeitouRange;

        // --range-large moves the medium and small ranges with it, at their default ratios to the large one.
        if (o.LargeRange is { } largeRange)
        {
            float k = largeRange / f.LargeRange;
            (f.SmallRange, f.MediumRange, f.LargeRange) = (f.SmallRange * k, f.MediumRange * k, largeRange);
        }

        f.Lod = o.FoliageLod;
        (f.Impostors, f.ImpostorDistance, f.ImpostorBudgetMb) = (o.Impostors, o.ImpostorDistance ?? f.ImpostorDistance,
            o.ImpostorBudgetMb ?? f.ImpostorBudgetMb);
        if (o.LargeImpostorDistance is { } largeImpostor) f.LargeImpostorDistance = largeImpostor;
        if (o.GrassDensity is { } grassDensity) f.GrassDensitySetting = grassDensity;
        Console.WriteLine($"foliage   catalog and shaders ready ({f.LoadMs:0} ms)");
    }

    /// <summary>
    /// The game's CSM mode (docs/formats/shadows.md): four cascades in one atlas of the <c>shadow quality</c> side, out to <c>Shadow Range</c>.
    /// </summary>
    static void CreateShadows(GpuBuild b, WorldGpu gpu)
    {
        var o = b.Options;
        b.Step("shadows");
        var settings = new ShadowSettings(KenshiShadows.MapSize(o.ShadowQuality), o.ShadowRangeFor(o.MeitouShadows));
        var shadow = gpu.Shadow = new ShadowPass(b.Context, b.Assets)
        {
            Settings = settings,
            Meitou = o.MeitouShadows,
            FilterQuality = o.ShadowFilter,
            Enabled = !o.ShadowsOff,
        };
        if (!shadow.HasNoise)
            Console.WriteLine($"warning   shadows: {KenshiShadows.NoiseTexture} not found, the receiver's jitter is a hash");
        shadow.SetTerrain(b.Scene.Coarse, b.Scene.CoarseSize);   // the Meitou shadows' terrain shadow beyond the range

        string layout = shadow.Meitou ? "Meitou: a layer each" : "Faithful: tiles of one layer";
        Console.WriteLine($"shadows   {settings.MapSize}² atlas, {shadow.CascadeCount} cascades of {settings.TileSize}² "
            + $"({layout}), range {settings.Range:0}");
    }

    /// <summary>
    /// The ray-traced global illumination: its scene, the debug view and the probes, on a device with ray queries.
    /// </summary>
    static void CreateGi(WorldGpu gpu, GpuContext context, WorldOptions o)
    {
        if (!context.Device.HasRayQuery)
        {
            Console.WriteLine("gi        off: the device has no ray queries (VK_KHR_ray_query)");
            return;
        }

        gpu.Gi = new Gi.GiScene(context);
        if (o.GiDebug > 0)
        {
            gpu.GiDebugPass = new Gi.GiDebugPass(context);
            gpu.GiDebug = o.GiDebug;
            Console.WriteLine($"gi        debug view {o.GiDebug}");
        }
        if (o.Gi)
        {
            gpu.Probes = new Gi.GiProbes(context) { Enabled = o.GiProbes, InteriorEnabled = o.InteriorGi };
            Console.WriteLine($"gi        {gpu.Probes.Describe()}");
        }
    }

    /// <summary>
    /// The memory-pressure guard (VramGuard), shared by the renderers that stream: MEITOU_VRAM_GUARD=0 leaves it off. Vulkan samples its allocator;
    /// another backend supplies the sample (Metal's device allocation and conservative budget).
    /// </summary>
    static void AttachGuard(WorldGpu gpu, IGpuContext context, Func<(ulong Used, ulong Budget)>? vramSample)
    {
        if (Environment.GetEnvironmentVariable("MEITOU_VRAM_GUARD") == "0") return;
        VramGuard? guard = context is GpuContext vulkanGuard
            ? new VramGuard(vulkanGuard.Device.VideoMemory) { Allocator = vulkanGuard.Device.Allocator }
            : vramSample is not null ? new VramGuard(vramSample) : null;
        if (guard is null) return;

        gpu.Guard = guard;
        if (gpu.Objects is not null) gpu.Objects.Guard = guard;
        if (gpu.Foliage is not null) gpu.Foliage.Guard = guard;
        if (gpu.Shadow is not null) gpu.Shadow.Guard = guard;
        if (gpu.Characters is not null) gpu.Characters.Guard = guard;
    }
}
