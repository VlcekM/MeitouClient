using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.Particles;
using Meitou.Data.World;

using Meitou.Game.Rendering.Bench;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Water;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The world frame: loading the region, creating the renderers, and drawing a frame (shadows, reflection, sky, the scene,
/// water, post-processing). Shared by the game (<c>meitou</c>) and the viewer (<c>meitou-viewer --world</c>).
/// </summary>
static partial class WorldFrame
{
    #region Scene loading

    /// <summary>
    /// Heightmap step of the whole-world height grid behind the loaded region (2049² samples, 144 units apart).
    /// </summary>
    public const int CoarseStep = 8;

    /// <summary>
    /// How long <see cref="WorldGpu.Areas"/> holds its highest count before it may fall.
    /// </summary>
    public const int AreasHoldMs = 30000;

    /// <summary>
    /// The region round the options' place (<c>--at</c>, <c>--zone</c> or <c>--town</c>; the origin without one): its heights, the game data and its
    /// objects. Null when no town matches.
    /// </summary>
    public static WorldScene? Load(GameInstall install, WorldOptions o, GameDatabase? preloaded = null)
    {
        var watch = Stopwatch.StartNew();
        var map = TerrainHeightmap.Open(install);
        GameDatabase? db = null;
        if (o.Town is not null || !o.NoTextures || !o.NoObjects)
        {
            db = preloaded ?? GameDatabase.Load(LoadOrder.FromInstall(install));
            Console.WriteLine($"game data {db.Records.Count} records ({watch.ElapsedMilliseconds} ms)");
            LogIssues("game data", db.Issues);
        }

        if (StartPoint(install, o, db) is not { } start)
        {
            map.Dispose();
            return null;
        }
        var (x, z, y) = start;

        var (window, cells, step) = ReadWindow(map, o, x, z);
        var coarse = map.Downsample(CoarseStep, out int coarseSize);
        var (x0, z0) = window.WorldOf(0, 0);
        var (x1, z1) = window.WorldOf(cells, cells);
        var focus = new Vector3((float)x, y ?? map.HeightAt(x, z), (float)z);
        Console.WriteLine($"terrain   {cells}x{cells} cells, step {step} ({window.Spacing} units), "
            + $"world grid {coarseSize}² every {CoarseStep * WorldLayout.SampleSpacing} units; "
            + $"X {x0:0}..{x1:0}, Z {z0:0}..{z1:0} (zones {WorldLayout.ZoneOf(x0, z0)} .. {WorldLayout.ZoneOf(x1 - 1, z1 - 1)}) "
            + $"({watch.ElapsedMilliseconds} ms)");
        Console.WriteLine($"focus     {focus.X:0}, {focus.Y:0}, {focus.Z:0} in zone {WorldLayout.ZoneOf(focus.X, focus.Z)}");

        var scene = new WorldScene
        {
            Heightmap = map,
            Window = window,
            Coarse = coarse,
            CoarseSize = coarseSize,
            Focus = focus,
            X0 = x0,
            Z0 = z0,
            X1 = x1,
            Z1 = z1,
            Database = db,
        };
        if (db is not null) scene.Clock = SkyClock.FromDatabase(db);
        if (!o.NoObjects && db is not null) scene.Objects = WorldObjects.Load(install, db, map, x0, z0, x1, z1);
        Console.WriteLine($"loaded in {watch.ElapsedMilliseconds} ms");
        return scene;
    }

    /// <summary>
    /// Where the region is centred: <c>--at</c>, a <c>--zone</c>'s middle, or the placed town whose name contains <c>--town</c> (the shortest such
    /// name;
    /// its height too); the origin without any. Null when no town matches.
    /// </summary>
    static (double X, double Z, float? Y)? StartPoint(GameInstall install, WorldOptions o, GameDatabase? db)
    {
        if (o.X is { } ax && o.Z is { } az) return (ax, az, null);
        if (o.Zone is { } zone)
        {
            var (zx, zz) = WorldLayout.ZoneOrigin(zone);
            return (zx + WorldLayout.ZoneSize / 2.0, zz + WorldLayout.ZoneSize / 2.0, null);
        }
        if (o.Town is not { } name) return (0, 0, null);

        var levels = WorldLevelData.Load(install);
        var towns = levels.Towns().Select(t => (Place: t, Record: db!.Find(t.TownId)))
            .Where(t => t.Record is not null && t.Record.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Record!.Name.Length).ToList();
        if (towns.Count == 0)
        {
            Console.Error.WriteLine($"No placed town matches '{name}'.");
            return null;
        }

        foreach (var t in towns.Take(8))
            Console.WriteLine($"town      '{t.Record!.Name}' ({t.Record.StringId}) at {t.Place.Position.X:0}, {t.Place.Position.Z:0}");
        var pick = towns[0].Place.Position;
        return (pick.X, pick.Z, pick.Y);
    }

    /// <summary>
    /// The heights of the window round (<paramref name="x"/>, <paramref name="z"/>), <c>--radius</c> zones each way: a whole number of 64-cell blocks
    /// per side, every <c>step</c>-th sample, the smallest power of two that keeps the window to 2048 cells a side.
    /// </summary>
    static (HeightWindow Window, int Cells, int Step) ReadWindow(TerrainHeightmap map, WorldOptions o, double x, double z)
    {
        const int chunkCells = 64;
        double half = o.Radius * WorldLayout.ZoneSize;
        int cellsWanted = (int)Math.Ceiling(2 * half / WorldLayout.SampleSpacing);
        int step = 1;
        while (cellsWanted / step > 2048) step *= 2;
        int cells = (cellsWanted / step + chunkCells - 1) / chunkCells * chunkCells;

        var (sc, sr) = WorldLayout.ToSample(x - half, z - half);
        int c0 = (int)Math.Floor(sc / step) * step, r0 = (int)Math.Floor(sr / step) * step;
        return (map.ReadWindow(c0, r0, cells + 1, cells + 1, step), cells, step);
    }

    /// <summary>
    /// A load's data problems (a missing mod, a record defined twice or with another type, a level file left out...) to the log: a count by kind,
    /// then the first few.
    /// </summary>
    internal static void LogIssues(string what, List<GameDataIssue> issues)
    {
        if (issues.Count == 0) return;
        Console.WriteLine($"{what} {issues.Count} problems: {string.Join(", ", issues.GroupBy(i => i.Kind).Select(g => $"{g.Key} {g.Count()}"))}");
        const int shown = 20;
        foreach (var issue in issues.Take(shown))
            Console.WriteLine($"  {issue.Kind}: {issue}");
        if (issues.Count > shown) Console.WriteLine($"  ... and {issues.Count - shown} more");
    }

    /// <summary>
    /// A flat world for the game's animation sandbox: the whole terrain at one height (a 256² window of heights round the origin and a flat
    /// whole-world grid), no heightmap file, no objects. The caller turns textures, objects, foliage and water off in <paramref name="o"/>.
    /// </summary>
    public static WorldScene LoadFlat(WorldOptions o, GameDatabase db, ushort raw = 3344)
    {
        LogIssues("game data", db.Issues);
        const int cells = 256;   // 4608 units: one zone
        var (c0, r0) = ((int)Math.Round(WorldLayout.ToSample(0, 0).Column) - cells / 2, (int)Math.Round(WorldLayout.ToSample(0, 0).Row) - cells / 2);
        var flat = new ushort[(cells + 1) * (cells + 1)];
        Array.Fill(flat, raw);
        var window = new HeightWindow(c0, r0, 1, cells + 1, cells + 1, flat);
        int coarseSize = (WorldLayout.HeightmapSize - 1) / CoarseStep + 1;
        var coarse = new ushort[coarseSize * coarseSize];
        Array.Fill(coarse, raw);
        var (x0, z0) = window.WorldOf(0, 0);
        var (x1, z1) = window.WorldOf(cells, cells);
        float height = WorldLayout.RawToHeight(raw);
        return new WorldScene { Window = window, Coarse = coarse, CoarseSize = coarseSize, FlatHeight = height, Focus = new Vector3(0, height,
            0), X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, Database = db, Clock = SkyClock.FromDatabase(db) };
    }

    /// <summary>
    /// The starting camera round the scene's focus (<c>--yaw</c>, <c>--pitch</c>, <c>--distance</c>, or a camera code) and the render options the
    /// command line asks for.
    /// </summary>
    public static (WorldCamera, WorldRenderOptions) Setup(WorldScene scene, WorldOptions o)
    {
        float radius = o.Radius * WorldLayout.ZoneSize;
        var camera = new WorldCamera
        {
            Target = scene.Focus,
            Yaw = (o.Yaw ?? 30) * MathF.PI / 180,
            Pitch = (o.Pitch ?? 35) * MathF.PI / 180,
            Distance = o.Distance ?? radius,
        };
        if (o.CameraPose is { } pose) WorldCameraCode.Apply(pose, camera);
        float terrainError = o.TerrainErrorFor(o.MeitouReach);
        var render = new WorldRenderOptions
        {
            Textures = !o.NoTextures,
            Objects = !o.NoObjects,
            Water = !o.NoWater,
            Wireframe = o.Wireframe ? 1 : 0,
            Debug = o.Debug,
            MaterialDistance = o.MaterialDistance,
            TerrainPixelError = terrainError
        };
        return (camera, render);
    }

    #endregion

    #region Loading completion

    /// <summary>
    /// The end of loading: the GPU finishes the uploads queued so far (Vulkan records them all into the next frame: 1.7 GB at the
    /// benchmark start, a 300 ms stall two frames later), and one full, compacting collection runs now instead of a blocking gen2 collection a few
    /// frames into play (300+ ms measured), then gen2 collections only in the background while the world runs (DECISIONS 12).
    /// </summary>
    public static void FinishLoading(IGpuContext context)
    {
        context.Finish();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        Console.WriteLine($"gc        {GcName()}, latency mode {System.Runtime.GCSettings.LatencyMode}");
    }

    /// <summary>
    /// Which collector runs: the runtime's server or workstation GC (docs/engine.md).
    /// </summary>
    public static string GcName() => System.Runtime.GCSettings.IsServerGC ? "server" : "workstation";

    #endregion

    #region Particles and GPU build

    /// <summary>
    /// Feeds the particle renderer the weather state's effect list, strength and wind (the renderer is made the first time a weather has effects; a
    /// weather without any empties it again). The effect groups are rebuilt only when the weather's list changes.
    /// </summary>
    static void UpdateParticles(WorldGpu gpu, WorldCamera camera)
    {
        if (gpu.Weather is not { } weather || gpu.MakeParticles is null) return;

        // In flight (KenshiCamera.FlightHeight, the platoons' gate) the weather's effects stop, as its fog does (SkyRenderer.WeatherFogScale).
        var input = gpu.Sky.WeatherFogScale == 0 ? WeatherEffectInput.None : weather.EffectInput();
        if (input.Effects.Count == 0 && gpu.Particles is null) return;
        var particles = gpu.Particles ??= gpu.MakeParticles();

        // The effects are placed in the camera's weather region: the area follows the cell the camera is in.
        if (gpu.WeatherAreas is { } areas)
        {
            var (cx, cz) = WeatherAreas.CellOf(camera.Eye.X, camera.Eye.Z);
            if (WeatherAreas.InMap(cx, cz) && areas.ColourOf(cx, cz) is var colour && colour != gpu.ParticleAreaColour)
            {
                gpu.ParticleAreaColour = colour;
                particles.World.Area = RegionArea.At(areas, camera.Eye.X, camera.Eye.Z);
            }
        }
        particles.SetWeather(input);
    }

    /// <summary>
    /// The particle renderer (reads the scripts and materials, makes the pass's program) if there is none yet; for whoever feeds it a weather's
    /// effect list.
    /// </summary>
    public static ParticleRenderer EnsureParticles(WorldGpu gpu, IGpuContext context, AssetLocator assets)
    {
        if (gpu.Particles is { } existing) return existing;
        return gpu.Particles = ParticleRenderer.Create(context, assets);
    }

    /// <summary>
    /// The renderer for <paramref name="scene"/>, built step by step (<see cref="WorldGpuBuilder.Create"/>).
    /// </summary>
    public static WorldGpu CreateGpu(IGpuContext context, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, bool interactive,
        Action<string>? step = null, Action<Task>? wait = null, bool terrainTextures = true, bool particlesEnabled = true, int objectLoadBudget = -1,
        Func<(ulong Used, ulong Budget)>? vramSample = null) =>
        WorldGpuBuilder.Create(context, install, scene, assets, o, interactive, step, wait, terrainTextures, particlesEnabled, objectLoadBudget,
            vramSample);

    #endregion

    #region Settings labels

    /// <summary>
    /// The upscaler sliders' labels (the game keeps command-line upscaler options over the saved ones). The anti-aliasing slider's label changes
    /// with its numbering ("0 off" 2026-10-09, SMAA 2026-10-19), so a value saved under an old one is not misread.
    /// </summary>
    public static readonly string[] UpscalerSliders = ["Anti-aliasing: 0 off 1 FXAA 2 SMAA 3 TAA 4 FSR 5 DLSS", "Render scale (upscaler)",
        "Upscaler sharpness"];

    /// <summary>
    /// DLSS's render preset (<see cref="UpscaleOptions.DlssPresets"/>); not an upscaler option of the command line, so the saved one always applies.
    /// </summary>
    public const string DlssPresetSlider = "DLSS preset";

    /// <summary>
    /// The anisotropic filtering slider's label.
    /// </summary>
    public const string FilteringSlider = "Anisotropic filtering (texture filtering)";

    /// <summary>
    /// The texture quality slider's label (the game's <c>texture resolution gimping</c>, 0-4).
    /// </summary>
    public const string TextureQualitySlider = "Texture quality 0-4 (game)";

    /// <summary>
    /// The rows that were command-line options only (docs/launch-options.md); the water's grid and FFT size are made once with the water
    /// (<c>Program</c> reads the saved ones before the world loads), the rest apply live. Their labels are the <c>meitou.user.json</c> keys.
    /// </summary>
    public const string WaterGridSlider = "Water grid segments", WaterOceanSlider = "Water wave resolution (FFT size)",
        ReflectionSamplesSlider = "Reflection samples", HazeDistanceSlider = "Full haze distance",
        MaterialDistanceSlider = "Terrain material distance", ImpostorBudgetSlider = "Impostor memory budget (MB)";

    /// <summary>
    /// The impostor budget slider's positions below this (MB) are the automatic rule.
    /// </summary>
    public const float ImpostorBudgetFloor = 8;

    /// <summary>
    /// The parts of Advanced materials (<c>chars2</c>), plain on / off rows under it.
    /// </summary>
    public const string MaterialHairSwitch = "Hair highlights", MaterialClothSwitch = "Cloth sheen", MaterialSkinSwitch = "Skin sheen and glow",
        MaterialEyesSwitch = "Eye catchlights", MaterialMetalSwitch = "Metal on characters", MaterialBeadsSwitch = "Water beads";

    /// <summary>
    /// Grass keeps its cover at a distance (<c>FoliageRenderer.GrassFarCover</c>), a plain on / off row.
    /// </summary>
    public const string GrassCoverSwitch = "Far grass cover";

    /// <summary>
    /// The world's surfaces show their wetness (<c>WeatherSurfaces.WetWorld</c>), a plain on / off row.
    /// </summary>
    public const string WetWorldSwitch = "Rain drops and puddles";

    /// <summary>
    /// The traced sun shadows' grass (<see cref="PostOptions.TracedGrass"/>), a plain on / off row under them.
    /// </summary>
    public const string TracedGrassSwitch = "Traced grass shadows";

    /// <summary>
    /// The hybrid GI's parts as switches (<see cref="PostOptions.GiRelocate"/>, <see cref="PostOptions.GiCloudShadows"/>,
    /// <see cref="PostOptions.GiCache"/>).
    /// </summary>
    public const string GiRelocateSwitch = "GI probes out of walls", GiCloudShadowsSwitch = "GI cloud shadows", GiCacheSwitch = "GI radiance cache";

    /// <summary>
    /// The weather particle sliders' labels: the particles' resolution and their density.
    /// </summary>
    public static readonly string[] ParticleSliders = ["Weather particles: 0 off 1 quarter 2 half 3 auto 4 full", "Particle density x"];

    /// <summary>
    /// The Tab panel sliders' labels: the meitou.user.json keys, and the launcher's settings schema names them (keep them).
    /// </summary>
    public const string ObjectDistanceSlider = "Object draw distance", GrassDensitySlider = "Grass density x",
        WaterReflectionSlider = "Water reflection 0-4 (game)", ShadowDistanceSlider = "Shadow distance (game 1k-9k, 0 off)",
        ShadowMapSlider = "Shadow map size (game quality 0-2)", GroundReliefQualitySlider = "Ground relief quality",
        ShaftQualitySlider = "Light shafts quality", GtaoQualitySlider = "Ambient occlusion quality (GTAO)";

    /// <summary>
    /// More of the Tab panel sliders' labels, kept as saved and as the launcher names them.
    /// </summary>
    public const string
        LandmarkDistanceSlider = "Landmark distance (Meitou)",
        DistantTownsSlider = "Distant towns (zones)",
        CharacterDistanceSlider = "Character draw distance",
        ObjectLodSlider = "Object LOD distance x",
        FoliageDistanceSlider = "Foliage draw distance x",
        FoliageLodSlider = "Foliage LOD distance x (lod Meitou)",
        LargeFoliageRangeSlider = "Large foliage range (F6 Meitou)",
        ImpostorDistanceSlider = "Impostor distance (F7 Meitou)",
        LargeImpostorDistanceSlider = "Large impostor distance (F7 Meitou)",
        MediumFoliageRangeSlider = "Medium foliage range",
        SmallFoliageRangeSlider = "Small foliage range",
        GrassDistanceSlider = "Grass draw distance x",
        ReflectionRangeSlider = "Reflection range x (game 0.6)",
        WaterClaritySlider = "Water clarity x (Meitou)",
        FarWaterClaritySlider = "Far water clarity (Meitou)",
        WaterClearDistanceSlider = "Water see-through distance (Meitou, game 4000)",
        TerrainErrorSlider = "Terrain detail: error px (less = finer)",
        GroundReliefDepthSlider = "Ground relief depth (cm, typical ground)",
        GroundDisplaceDistanceSlider = "Ground displacement distance (m)",
        GroundParallaxDistanceSlider = "Ground parallax distance (m)",
        GroundParallaxDepthSlider = "Ground parallax depth (% of relief depth)",
        BloodDrawsSlider = "Blood decals drawn",
        ShadowFilterSlider = "Shadow filter 0-2 (Meitou; 0 low-end)",
        HazeStrengthSlider = "Haze strength (1 = game)",
        PlanetshineStrengthSlider = "Planetshine strength (x physical)",
        CloudHeightSlider = "Cloud height (Meitou)",
        AmbientStrengthSlider = "Ambient light strength (1 = game)",
        NightAmbientStrengthSlider = "Night ambient light strength (1 = 1.2x game)",
        ToneMapSlider = "Tone map (0 = game)",
        ToneMixSlider = "Hybrid tone map: ACES share",
        GradingSlider = "Grading (0 = game)",
        HeatHazeStrengthSlider = "Heat haze strength (1 = game)",
        BloomStrengthSlider = "Bloom strength (0 = game)",
        DofApertureSlider = "Depth of field aperture (f-number)",
        NightGradeStrengthSlider = "Night grading strength (0 = game)",
        ShaftStrengthSlider = "Light shafts strength",
        ShaftAirSlider = "Light shafts air (0 = none)",
        ShaftAirDawnSlider = "Light shafts air at dawn and dusk (x)";

    /// <summary>
    /// The Tab panel switches that are not Faithful / Meitou ones (saved as <c>Meitou: &lt;label&gt;</c> like those).
    /// </summary>
    public const string
        FifthCascadeSwitch = "Shadow cascade 5 (Meitou)",
        BloomSwitch = "Bloom",
        DepthOfFieldSwitch = "Depth of field",
        CameraEffectsSwitch = "Camera effects in the strategy camera",
        // The ground relief's Faithful / Meitou switches (Enhancements "parallax", "displace"), by name.
        GroundParallaxSwitch = "Ground parallax",
        GroundDisplaceSwitch = "Ground displacement (tessellation)",
        RockSmoothingSwitch = "Rock smoothing (tessellation)",
        OutlineRefineSwitch = "Terrain outline refinement (tessellation)";

    /// <summary>
    /// The mutually exclusive techniques as one choice each (docs/native-ui.md "Settings"), over the switches they stand for (which stay the saved
    /// state).
    /// </summary>
    public const string SunShadowsChoice = "Sun shadows", OcclusionChoice = "Ambient occlusion type", GiChoice = "Global illumination";

    /// <summary>
    /// The <see cref="SunShadowsChoice"/>, <see cref="OcclusionChoice"/> and <see cref="GiChoice"/> keys.
    /// </summary>
    public static readonly string[] ChoiceSliders = [SunShadowsChoice, OcclusionChoice, GiChoice];

    /// <summary>
    /// The switches the <see cref="ChoiceSliders"/> replace on screen.
    /// </summary>
    public static readonly string[] ChoiceDrivenSwitches =
        ["Shadows", "Traced sun shadows", "Ambient occlusion", "Ground-truth occlusion", "Indirect light", "Hybrid GI"];

    /// <summary>
    /// The weather particles slider's position for the renderer's state: 0 off, 1 quarter, 2 half, 3 by sprite size (auto), 4 full size.
    /// </summary>
    public static int WeatherParticlesSlider(bool weatherOn, PostOptions o) =>
        !weatherOn ? 0 : !o.LowResParticles || o.ParticleDivisor == 1 ? 4 : o.ParticleDivisor switch { 4 => 1, 2 => 2, _ => 3 };

    /// <summary>
    /// The anti-aliasing slider's value for the options: 0 none, 1 FXAA, 2 SMAA, 3.. the upscaler kinds (TAA, FSR, DLSS).
    /// </summary>
    public static int AntiAliasingSlider(PostOptions o) => o.Upscale.Kind != UpscalerKind.Off ? (int)o.Upscale.Kind + 2 : o.Smaa ? 2 : o.Fxaa ? 1 : 0;

    /// <summary>
    /// The upscaler a value of the anti-aliasing slider picks (<see cref="UpscalerKind.Off"/> for none, FXAA and SMAA).
    /// </summary>
    public static UpscalerKind AntiAliasingKind(float v) =>
        (int)MathF.Round(v) is var i && i >= 3 ? (UpscalerKind)Math.Min(i - 2, (int)UpscalerKind.Dlss) : UpscalerKind.Off;

    #endregion

    #region Settings panel

    /// <summary>
    /// Whether the device traces rays (the traced sun and lamps, the GI, reflections); set when the settings panel is made.
    /// </summary>
    public static bool RayTracingAvailable { get; private set; }

    /// <summary>
    /// The Faithful / Meitou switches over the live renderer (the game's Shift+F keys, the viewer's F keys, the Tab checkboxes): they change the
    /// options and, once made, the renderer's parts; the distances follow the mode's defaults unless a Tab slider moved them.
    /// </summary>
    internal static IReadOnlyList<Enhancement> LiveSwitches(WorldOptions o, Func<WorldGpu?> gpu, Func<WorldCamera?> camera,
        Func<WorldRenderOptions?> render) =>
        Enhancements.Create(o.Post, () => gpu()?.Sky.MeitouHaze ?? o.MeitouHaze, v => { o.MeitouHaze = v; if (gpu() is { } g) g.Sky.MeitouHaze = v; },
            () => gpu()?.Shadow?.Meitou ?? o.MeitouShadows, v =>
            {
                // The shadow distance follows the mode's default unless the Tab slider moved it.
                bool was = o.MeitouShadows;
                o.MeitouShadows = v;
                if (gpu()?.Shadow is not { } s) return;
                if (MathF.Abs(s.Settings.Range - o.ShadowRangeFor(was)) < 1) s.Settings = s.Settings with { Range = o.ShadowRangeFor(v) };
                s.Meitou = v;
            },
            () => gpu()?.Foliage?.MeitouRange ?? o.MeitouRange, v => { o.MeitouRange = v; if (gpu()?.Foliage is { } f) f.MeitouRange = v; },
            () => gpu()?.Foliage?.Impostors ?? o.Impostors, v => { o.Impostors = v; if (gpu()?.Foliage is { } f) f.Impostors = v; },
            () => o.MeitouReach, v =>
            {
                // The draw distances follow the mode's defaults unless a Tab slider (or an option) moved them.
                bool was = o.MeitouReach;
                o.MeitouReach = v;
                if (gpu()?.Objects is { } ob)
                {
                    if (MathF.Abs(ob.ObjectDistance - o.ObjectDistanceFor(was)) < 1) ob.ObjectDistance = o.ObjectDistanceFor(v);
                    if (MathF.Abs(ob.LandmarkDistance - o.LandmarkDistanceFor(was)) < 1) ob.LandmarkDistance = o.LandmarkDistanceFor(v);
                }
                if (gpu()?.Objects is { } sortObjects) sortObjects.SortNearestFirst = v;
                if (render() is { } r && MathF.Abs(r.TerrainPixelError - o.TerrainErrorFor(was)) < 1e-3f) r.TerrainPixelError = o.TerrainErrorFor(v);
            },
            () => gpu()?.Water?.Meitou ?? o.MeitouWater, v => { o.MeitouWater = v; if (gpu()?.Water is { } w) w.Meitou = v; },
            () => gpu()?.Foliage?.Lod ?? o.FoliageLod, v => { o.FoliageLod = v; if (gpu()?.Foliage is { } f) f.Lod = v; },
            // Without probes (--no-gi, no ray queries) the switch reads off: the picture has the flat ambient whatever it is set to.
            () => gpu() is { } giGpu ? giGpu.Probes?.Enabled ?? giGpu.ProbeGi?.Enabled ?? false : o.GiProbes,
                v => { o.GiProbes = v; if (gpu()?.Probes is { } p) p.Enabled = v; if (gpu()?.ProbeGi is { } m) m.Enabled = v; },
            () => gpu()?.Sky.NightAir ?? o.NightAir, v => { o.NightAir = v; if (gpu() is { } g) g.Sky.NightAir = v; },
            () => gpu()?.Sky.Planetshine ?? o.Planetshine, v => { o.Planetshine = v; if (gpu() is { } g) g.Sky.Planetshine = v; },
            () => gpu()?.Sky.MeitouStars ?? o.MeitouStars, v => { o.MeitouStars = v; if (gpu() is { } stars) stars.Sky.MeitouStars = v; },
            () => gpu()?.Sky.LitClouds ?? o.MeitouClouds, v => { o.MeitouClouds = v; if (gpu() is { } g) g.Sky.LitClouds = v; },
            () => gpu()?.Sky.CloudShadows ?? o.CloudShadows, v => { o.CloudShadows = v; if (gpu() is { } g) g.Sky.CloudShadows = v; },
            () => gpu()?.Sky.CloudFog ?? o.CloudFog, v => { o.CloudFog = v; if (gpu() is { } g) g.Sky.CloudFog = v; },
            () => gpu()?.Probes?.InteriorEnabled ?? gpu()?.ProbeGi?.InteriorEnabled ?? o.InteriorGi,
                v => { o.InteriorGi = v; if (gpu()?.Probes is { } p) p.InteriorEnabled = v; if (gpu()?.ProbeGi is { } m) m.InteriorEnabled = v; },
            () => gpu()?.Characters?.GeneratedLod ?? o.CharacterLod, v => { o.CharacterLod = v; if (gpu()?.Characters is { } c) c.GeneratedLod = v; },
            () => gpu()?.Characters?.SkinShading ?? o.SkinShading, v => { o.SkinShading = v; if (gpu()?.Characters is { } c) c.SkinShading = v; },
            () => gpu()?.Lamps?.InteriorAllDay ?? o.InteriorLamps, v => { o.InteriorLamps = v; if (gpu()?.Lamps is { } l) l.InteriorAllDay = v; },
            () => gpu()?.Characters?.EnhancedIcons ?? o.MeitouIcons, v => { o.MeitouIcons = v; if (gpu()?.Characters is { } c) c.EnhancedIcons = v; },
            () => gpu()?.Decals?.Meitou ?? o.MeitouBlood, v => { o.MeitouBlood = v; if (gpu()?.Decals is { } d) d.Meitou = v; },
            () => gpu()?.Decals?.Surfaces ?? o.MeitouBloodSurfaces, v => { o.MeitouBloodSurfaces = v; if (gpu()?.Decals is { } d) d.Surfaces = v; },
            () => o.MeitouTracers, v => o.MeitouTracers = v,
            () => gpu()?.Characters?.Chars2 ?? o.Chars2, v => { o.Chars2 = v; if (gpu()?.Characters is { } c) c.Chars2 = v; },
            () => o.MeitouFootprints, v => o.MeitouFootprints = v);

    /// <summary>
    /// The Tab settings panel over <paramref name="g"/>'s renderers: the sliders (the viewer's clock, window and the render settings), the Faithful /
    /// Meitou switches as checkboxes, the cache actions, the texture column and the weather choices.
    /// </summary>
    public static SettingsPanel CreateSettingsPanel(DebugOverlay ui, WorldGpu g, WorldRenderOptions r, Func<float>? getHour = null,
        Action<float>? setHour = null,
        Func<bool>? getVSync = null, Action<bool>? setVSync = null, IReadOnlyList<Enhancement>? switches = null,
        Func<float>? getTimeSpeed = null, Action<float>? setTimeSpeed = null, Func<bool>? getFullScreen = null, Action<bool>? setFullScreen = null,
        IReadOnlyList<PanelChoice>? extraChoices = null)
    {
        var sliders = new List<Slider>();
        AddViewerSliders(sliders, getHour, setHour, getTimeSpeed, setTimeSpeed, getVSync, setVSync, getFullScreen, setFullScreen);
        if (g.Objects is { } objects) AddObjectSliders(sliders, objects);
        if (g.Characters is { } characters) sliders.Add(CharacterDistance(characters));
        if (g.Foliage is { } foliage) AddFoliageSliders(sliders, foliage);
        AddWaterSliders(sliders, g);
        AddTerrainSliders(sliders, g, r);
        var fifthCascade = g.Shadow is { } shadow ? AddShadowSliders(sliders, shadow) : null;
        AddSkySliders(sliders, g.Sky, g.Options);
        if (g.Post is { } post) AddPostSliders(sliders, g, post);

        // Anisotropic filtering (docs/render-post.md "Texture filtering"): the most any texture is sampled with; the world's textures ask 8x,
        // so 8x and 16x look the same there.
        var samplers = g.Sky.Gpu.Samplers;
        sliders.Add(new Slider(FilteringSlider, 0, 4, () => MathF.Log2(samplers.MaxAnisotropy),
            v => samplers.MaxAnisotropy = 1 << (int)MathF.Round(v), "0",
            Text: v => (1 << (int)MathF.Round(v)) + "x" + (MathF.Round(v) >= 3 ? " (as the game)" : "")));

        var toggles = SettingsToggles(g, switches, fifthCascade);
        RayTracingAvailable = g.Sky.Gpu.HasRayQuery;
        AddChoiceSliders(sliders, toggles, g);
        var actions = SettingsActions(g);
        var side = TextureSliders();

        // The camera region's weathers of its season (the current one marked): a click starts one now.
        var choices = new List<PanelChoice>(extraChoices ?? []);
        if (g.Weather is { } weather)
            choices.Add(new PanelChoice(weather.ChoiceLabel, weather.Choices, weather.Current, weather.Choose));
        return new SettingsPanel(ui, "Settings   (Tab hides this)", sliders, toggles, "Meitou improvements (unticked: as the game)", actions, side,
            choices);
    }

    /// <summary>
    /// Sun shadows (Off / Faithful cascades / Meitou cascades / Traced), ambient occlusion (Off / SSAO / GTAO) and global illumination (Off /
    /// Probes / Probes + hybrid) as choices: each reads and writes the Faithful / Meitou switches and the shadow distance it stands for, so a
    /// settings file of the switches still loads. Traced needs the indirect light (the rays use its scene), so choosing it turns the GI on, and the
    /// GI off leaves the cascades.
    /// </summary>
    static void AddChoiceSliders(List<Slider> sliders, List<Toggle> toggles, WorldGpu g)
    {
        if (sliders.FirstOrDefault(s => s.Label == ShadowDistanceSlider) is { } distance && g.Shadow is { } shadow
            && ChoiceToggle(toggles, "Shadows") is not null)
        {
            sliders.Add(new Slider(SunShadowsChoice, 0, 3, () => SunShadowsMode(toggles, shadow),
                v => SetSunShadowsMode(toggles, shadow, distance, ChoiceIndex(v)), "0"));
        }

        if (ChoiceToggle(toggles, "Ambient occlusion") is not null)
            sliders.Add(new Slider(OcclusionChoice, 0, 2, () => OcclusionMode(toggles), v => SetOcclusionMode(toggles, ChoiceIndex(v)), "0"));
        if (ChoiceToggle(toggles, "Indirect light") is not null)
            sliders.Add(new Slider(GiChoice, 0, 2, () => GiMode(toggles), v => SetGiMode(toggles, ChoiceIndex(v)), "0"));
    }

    /// <summary>
    /// The switch row labelled <paramref name="label"/>, null when the panel has none.
    /// </summary>
    static Toggle? ChoiceToggle(List<Toggle> toggles, string label) => toggles.FirstOrDefault(t => t.Label == label);

    /// <summary>
    /// Whether the switch row labelled <paramref name="label"/> is on (false when the panel has none).
    /// </summary>
    static bool ChoiceOn(List<Toggle> toggles, string label) => ChoiceToggle(toggles, label)?.Get() == true;

    /// <summary>
    /// Turns the switch row labelled <paramref name="label"/> on or off, if the panel has it and it is not so already.
    /// </summary>
    static void ChoiceSet(List<Toggle> toggles, string label, bool on)
    {
        if (ChoiceToggle(toggles, label) is { } t && t.Get() != on) t.Set(on);
    }

    /// <summary>
    /// A choice slider's value as the index of its choice.
    /// </summary>
    static int ChoiceIndex(float v) => (int)MathF.Round(v);

    /// <summary>
    /// The <see cref="SunShadowsChoice"/> the switches stand for: 0 off, 1 Faithful cascades, 2 Meitou cascades, 3 traced.
    /// </summary>
    static float SunShadowsMode(List<Toggle> toggles, ShadowPass shadow)
    {
        if (!shadow.Enabled) return 0;
        if (ChoiceOn(toggles, "Traced sun shadows")) return 3;

        return ChoiceOn(toggles, "Shadows") ? 2 : 1;
    }

    /// <summary>
    /// Sets the switches and the shadow distance for the <see cref="SunShadowsChoice"/> <paramref name="mode"/>.
    /// </summary>
    static void SetSunShadowsMode(List<Toggle> toggles, ShadowPass shadow, Slider distance, int mode)
    {
        ChoiceSet(toggles, "Traced sun shadows", mode == 3);
        if (mode == 0)
        {
            distance.Set(distance.Min);
            return;
        }

        // Back on at the range it had (Off keeps it), else the game's or Meitou's default.
        if (!shadow.Enabled)
        {
            float range = shadow.Settings.Range >= KenshiShadows.MinRange ? shadow.Settings.Range
                : mode == 1 ? 5000
                : Enhancements.MeitouShadowRange;
            distance.Set(range);
        }

        ChoiceSet(toggles, "Shadows", mode >= 2);
        if (mode == 3) ChoiceSet(toggles, "Indirect light", true);
    }

    /// <summary>
    /// The <see cref="OcclusionChoice"/> the switches stand for: 0 off, 1 SSAO, 2 GTAO.
    /// </summary>
    static float OcclusionMode(List<Toggle> toggles)
    {
        if (!ChoiceOn(toggles, "Ambient occlusion")) return 0;

        return ChoiceOn(toggles, "Ground-truth occlusion") ? 2 : 1;
    }

    /// <summary>
    /// Sets the switches for the <see cref="OcclusionChoice"/> <paramref name="mode"/>.
    /// </summary>
    static void SetOcclusionMode(List<Toggle> toggles, int mode)
    {
        ChoiceSet(toggles, "Ambient occlusion", mode > 0);
        if (mode > 0) ChoiceSet(toggles, "Ground-truth occlusion", mode == 2);
    }

    /// <summary>
    /// The <see cref="GiChoice"/> the switches stand for: 0 off, 1 probes, 2 probes and hybrid.
    /// </summary>
    static float GiMode(List<Toggle> toggles)
    {
        if (!ChoiceOn(toggles, "Indirect light")) return 0;

        return ChoiceOn(toggles, "Hybrid GI") ? 2 : 1;
    }

    /// <summary>
    /// Sets the switches for the <see cref="GiChoice"/> <paramref name="mode"/>: off also leaves the traced sun.
    /// </summary>
    static void SetGiMode(List<Toggle> toggles, int mode)
    {
        if (mode == 0) ChoiceSet(toggles, "Traced sun shadows", false);
        ChoiceSet(toggles, "Indirect light", mode > 0);
        ChoiceSet(toggles, "Hybrid GI", mode == 2);
    }

    /// <summary>
    /// The viewer's own rows; the game passes none of them (its clock, vsync and window mode are its own settings).
    /// </summary>
    static void AddViewerSliders(List<Slider> sliders, Func<float>? getHour, Action<float>? setHour, Func<float>? getTimeSpeed,
        Action<float>? setTimeSpeed, Func<bool>? getVSync, Action<bool>? setVSync, Func<bool>? getFullScreen, Action<bool>? setFullScreen)
    {
        // The time of day (the `--time` option and the , / . keys), to the minute.
        if (getHour is not null && setHour is not null)
            sliders.Add(new Slider("Time of day", 0, 24 - 1 / 60f, getHour, v => setHour(MathF.Round(v * 60) / 60), Text: TimeText));

        // How fast the clock runs (`--time-speed`): game hours per real minute, 0 stopped.
        if (getTimeSpeed is not null && setTimeSpeed is not null)
        {
            sliders.Add(new Slider("Time speed", 0, 240, getTimeSpeed, v => setTimeSpeed(v < 10 ? MathF.Round(v * 10) / 10 : MathF.Round(v)),
                Text: v => v <= 0 ? "stopped" : $"{v:0.#} game h / min"));
        }

        // The window's vsync (off: MAILBOX, else IMMEDIATE; the frame rate is uncapped).
        if (getVSync is not null && setVSync is not null)
        {
            sliders.Add(new Slider("VSync", 0, 1, () => getVSync() ? 1 : 0, v => setVSync(v >= 0.5f),
                    Text: v => v >= 0.5f ? "on" : "off (uncapped)"));
        }

        // Borderless full screen (also Alt+Enter).
        if (getFullScreen is not null && setFullScreen is not null)
        {
            sliders.Add(new Slider("Full screen", 0, 1, () => getFullScreen() ? 1 : 0, v =>
            {
                if (v >= 0.5f != getFullScreen()) setFullScreen(v >= 0.5f);
            }, Text: v => v >= 0.5f ? "borderless" : "window"));
        }
    }

    /// <summary>
    /// The objects' and landmarks' draw distances, the distant towns' range and the object LOD.
    /// </summary>
    static void AddObjectSliders(List<Slider> sliders, WorldObjectRenderer objects)
    {
        sliders.Add(new Slider(ObjectDistanceSlider, 1000, 400000, () => objects.ObjectDistance, v => objects.ObjectDistance = v, "0",
            Logarithmic: true,
            Faithful: Enhancements.FaithfulObjectDistance, Meitou: Enhancements.MeitouObjectDistance));
        if (objects.HasLandmarks)
        {
            sliders.Add(new Slider(LandmarkDistanceSlider, 1000, 400000, () => objects.LandmarkDistance,
                    v => objects.LandmarkDistance = MathF.Round(v / 100) * 100,
                "0", Logarithmic: true, Faithful: 0, Meitou: Enhancements.MeitouLandmarkDistance));
        }

        sliders.Add(new Slider(DistantTownsSlider, 0, ObjectRanges.MaxDistantTownRangeZones * 10,
            () => objects.DistantRange / WorldLayout.ZoneSize, v => objects.DistantRange = MathF.Round(v) * WorldLayout.ZoneSize, "0"));
        sliders.Add(new Slider(ObjectLodSlider, 0.25f, 4, () => 1 / objects.LodBias, v => objects.LodBias = 1 / v, "0.00", Logarithmic: true));
    }

    /// <summary>
    /// The game's NPC View Range: characters farther than this from the eye are not drawn, in steps of 50 (also the Metal panel's).
    /// </summary>
    internal static Slider CharacterDistance(Characters.CharacterRenderer characters) =>
        new(CharacterDistanceSlider, Characters.CharacterRenderer.MinDrawDistance, Characters.CharacterRenderer.MaxDrawDistance,
            () => characters.DrawDistance, v => characters.DrawDistance = MathF.Round(v / 50) * 50, "0", Step: 50);

    /// <summary>
    /// The foliage's distance, LOD, class ranges, impostor distances and the grass's distance and density.
    /// </summary>
    static void AddFoliageSliders(List<Slider> sliders, FoliageRenderer foliage)
    {
        static float Round50(float v) => MathF.Round(v / 50) * 50;

        sliders.Add(new Slider(FoliageDistanceSlider, 0.25f, 80, () => foliage.RangeSetting, v => foliage.RangeSetting = v, "0.00",
            Logarithmic: true));
        // The generated levels switch at a pixel tolerance; the distance where each level takes over scales with its inverse.
        sliders.Add(new Slider(FoliageLodSlider, 0.25f, 8, () => FoliageRenderer.DefaultLodTolerance / foliage.LodTolerance,
            v => foliage.LodTolerance = FoliageRenderer.DefaultLodTolerance / v, Logarithmic: true,
            Text: v => string.Create(CultureInfo.InvariantCulture, $"{v:0.00} ({FoliageRenderer.DefaultLodTolerance / v:0.0} px)")));

        // The range switch's class ranges (Meitou; the slider above then only moves the FAR layers' large meshes).
        sliders.Add(new Slider(LargeFoliageRangeSlider, 1000, 120000, () => foliage.LargeRange, v => foliage.LargeRange = Round50(v), "0",
            Logarithmic: true));
        sliders.Add(new Slider(ImpostorDistanceSlider, 500, 40000, () => foliage.ImpostorDistance, v => foliage.ImpostorDistance = Round50(v), "0",
            Logarithmic: true));
        sliders.Add(new Slider(LargeImpostorDistanceSlider, 500, 40000, () => foliage.LargeImpostorDistance,
            v => foliage.LargeImpostorDistance = Round50(v), "0",
            Logarithmic: true));
        sliders.Add(ImpostorBudget(foliage));
        sliders.Add(new Slider(MediumFoliageRangeSlider, 400, 80000, () => foliage.MediumRange, v => foliage.MediumRange = Round50(v), "0",
            Logarithmic: true));
        sliders.Add(new Slider(SmallFoliageRangeSlider, 200, 40000, () => foliage.SmallRange, v => foliage.SmallRange = Round50(v), "0",
            Logarithmic: true));

        sliders.Add(new Slider(GrassDistanceSlider, 0.25f, 80, () => foliage.GrassRangeSetting, v => foliage.GrassRangeSetting = v, "0.00",
            Logarithmic: true));
        sliders.Add(new Slider(GrassDensitySlider, 0.1f, 2, () => foliage.GrassDensitySetting, v => foliage.GrassDensitySetting = v, "0.00"));
    }

    /// <summary>
    /// The water's reflection and clarity.
    /// </summary>
    static void AddWaterSliders(List<Slider> sliders, WorldGpu g)
    {
        // The game's `water reflection` (0 off .. 4 everything) and `reflection range` (x the haze distance), docs/formats/settings.md.
        if (g.Reflection is { } reflection)
        {
            // The label is the user config key: keep it.
            sliders.Add(new Slider(WaterReflectionSlider, 0, 4, () => reflection.Level, v => reflection.Level = (int)MathF.Round(v), "0",
                Text: v => (int)MathF.Round(v) switch { 0 => "0 off", 1 => "1 landscape", 2 => "2 characters",
                    3 => "3 buildings", _ => "4 everything" }));
            sliders.Add(new Slider(ReflectionRangeSlider, 0.1f, 50, () => reflection.Range, v => reflection.Range = v, "0.00", Logarithmic: true));
            sliders.Add(ReflectionSamples(reflection, g.Options));
        }
        if (g.Options is { } options) AddWaterSizeSliders(sliders, options);

        // How far the Meitou water lets the floor show through (docs/render-water.md "Clarity"); the Faithful water keeps the game's alpha.
        if (g.Water is { } water)
        {
            sliders.Add(new Slider(WaterClaritySlider, 0.25f, 8, () => water.Clarity, v => water.Clarity = v, "0.00", Logarithmic: true));
            sliders.Add(new Slider(FarWaterClaritySlider, 0, 1, () => water.FarClarity, v => water.FarClarity = v, "0.00",
                Text: v => v <= 0 ? "0 (true slant)" : v.ToString("0.00", CultureInfo.InvariantCulture)));
            sliders.Add(new Slider(WaterClearDistanceSlider, 1000, 100000, () => water.ClearDistance,
                v => water.ClearDistance = MathF.Round(v / 100) * 100, "0", Logarithmic: true));
        }
    }

    /// <summary>
    /// The haze distance row (<c>--fog</c>): the options' value goes to every frame's draw. Also the Metal panel's.
    /// </summary>
    internal static Slider HazeDistance(WorldOptions options) => new(HazeDistanceSlider, 10000, 1000000, () => options.FogDistance,
        v => options.FogDistance = MathF.Round(v / 1000) * 1000, "0", Logarithmic: true);

    /// <summary>
    /// The material distance row (<c>--material-distance</c>): beyond it the terrain shows the biomes' ground colour. The draw's value and the
    /// texture streamer's are read each frame. Also the Metal panel's.
    /// </summary>
    internal static Slider MaterialDistance(WorldRenderOptions r, TerrainStreamer? streamer, WorldOptions? options) =>
        new(MaterialDistanceSlider, 2000, 100000, () => r.MaterialDistance, v =>
        {
            r.MaterialDistance = MathF.Round(v / 500) * 500;
            if (streamer is not null) streamer.MaterialDistance = r.MaterialDistance;
            if (options is not null) options.MaterialDistance = r.MaterialDistance;
        }, "0", Logarithmic: true);

    /// <summary>
    /// The impostor memory budget row (<c>--impostor-budget</c>), read at each scan: the lowest positions are the automatic rule (docs/impostors.md).
    /// Also the Metal panel's.
    /// </summary>
    internal static Slider ImpostorBudget(FoliageRenderer foliage) => new(ImpostorBudgetSlider, 0, 2048,
        () => (float)(foliage.ImpostorBudgetMb ?? 0),
        v => foliage.ImpostorBudgetMb = v < ImpostorBudgetFloor ? null : MathF.Round(v / 16) * 16, "0", Step: 16,
        Text: v => v < ImpostorBudgetFloor ? "Automatic" : string.Create(CultureInfo.InvariantCulture, $"{MathF.Round(v / 16) * 16:0} MB"));

    /// <summary>
    /// The reflection's samples per texel as a power of two, 1 (off) to 8 (docs/render-water.md "Shading and reflection"): the targets are made
    /// again at the new count on the next frame.
    /// </summary>
    internal static Slider ReflectionSamples(ReflectionPass reflection, WorldOptions? options) => new(ReflectionSamplesSlider, 0, 3,
        () => Math.Clamp((int)MathF.Round(MathF.Log2(Math.Max(reflection.Samples, 1))), 0, 3), v =>
        {
            reflection.Samples = 1 << Math.Clamp((int)MathF.Round(v), 0, 3);
            if (options is not null) options.ReflectionSamples = reflection.Samples;
        }, "0", Text: v => (int)MathF.Round(v) is var i and > 0 ? $"{1 << i}x" : "off");

    /// <summary>
    /// The Meitou water's grid segments and FFT size: made once with the water, so a new value takes effect at the next start (<c>Program</c> reads
    /// the saved ones before the world loads).
    /// </summary>
    internal static void AddWaterSizeSliders(List<Slider> sliders, WorldOptions options)
    {
        static int Segments(float v) => Math.Clamp((int)MathF.Round(v / 64) * 64, 64, 1024);
        static int FftIndex(float v) => Math.Clamp((int)MathF.Round(v), 0, 2);

        sliders.Add(new Slider(WaterGridSlider, 64, 1024, () => options.WaterGrid, v => options.WaterGrid = Segments(v), "0", Logarithmic: true,
            Step: 64));
        sliders.Add(new Slider(WaterOceanSlider, 0, 2, () => MathF.Log2(Math.Max(options.WaterOcean, 64) / 64f),
            v => options.WaterOcean = 64 << FftIndex(v), "0", Text: v => (64 << FftIndex(v)).ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The terrain's pixel error, the ground relief (ours, docs/render-terrain.md "Ground relief": the march's quality, the depth and where each
    /// ends) and the blood decals drawn on it.
    /// </summary>
    static void AddTerrainSliders(List<Slider> sliders, WorldGpu g, WorldRenderOptions r)
    {
        sliders.Add(new Slider(TerrainErrorSlider, 4, 32, () => r.TerrainPixelError, v => r.TerrainPixelError = v, "0.0", Logarithmic: true,
            Faithful: WorldRenderOptions.DefaultTerrainPixelError, Meitou: Enhancements.MeitouTerrainPixelError));
        sliders.Add(MaterialDistance(r, g.Streamer, g.Options));

        // The depth drives parallax and displacement; 0 is off.
        sliders.Add(new Slider(GroundReliefQualitySlider, 0, 2, () => GroundRelief.Quality,
            v => GroundRelief.Quality = Math.Clamp((int)MathF.Round(v), 0, 2), "0", Text: v => GroundRelief.QualityName((int)MathF.Round(v))));
        sliders.Add(new Slider(GroundReliefDepthSlider, 0, 80, () => GroundRelief.Depth * 10, v => GroundRelief.Depth = MathF.Round(v * 2) / 20,
            "0.0"));
        if (g.Sky.Gpu.HasTessellation)
        {
            sliders.Add(new Slider(GroundDisplaceDistanceSlider, 10, 200, () => GroundRelief.DisplaceDistance / 10,
                v => GroundRelief.DisplaceDistance = MathF.Round(v) * 10, "0"));
        }

        sliders.Add(new Slider(GroundParallaxDepthSlider, 10, 200, () => GroundRelief.ParallaxScale * 100,
            v => GroundRelief.ParallaxScale = MathF.Round(v / 5) / 20, "0"));
        sliders.Add(new Slider(GroundParallaxDistanceSlider, 10, 150, () => GroundRelief.ParallaxDistance / 10,
            v => GroundRelief.ParallaxDistance = MathF.Round(v) * 10, "0"));

        // Blood on surfaces and footprints share one cap a frame, the nearest drawn (docs/game/blood-decals.md "Bloody footprints").
        sliders.Add(new Slider(BloodDrawsSlider, TerrainDecalRenderer.MinSurfaceDraws, TerrainDecalRenderer.MaxSurfaceDraws,
            () => TerrainDecalRenderer.SurfaceDraws, v => TerrainDecalRenderer.SurfaceDraws = (int)MathF.Round(v / 64) * 64, "0", Step: 64,
            Meitou: TerrainDecalRenderer.DefaultSurfaceDraws));
    }

    /// <summary>
    /// The shadows' range, map size and filter, and the Meitou fifth cascade's switch (returned for the toggles). The game's <c>Shadow Range</c>
    /// slider goes 1000 to 9000; the viewer allows more (the cascades stretch over it), and its left end (0) turns the shadows off (low-end GPUs).
    /// </summary>
    static Toggle? AddShadowSliders(List<Slider> sliders, ShadowPass shadow)
    {
        const float off = KenshiShadows.MinRange * 0.9f;
        sliders.Add(new Slider(ShadowDistanceSlider, off, 200000, () => shadow.Enabled ? shadow.Settings.Range : off, v =>
        {
            shadow.Enabled = v >= KenshiShadows.MinRange;
            if (shadow.Enabled) shadow.Settings = shadow.Settings with { Range = MathF.Round(v / 100) * 100 };
        }, "0", Logarithmic: true, Text: v => shadow.Enabled ? v.ToString("0", CultureInfo.InvariantCulture) : "0 (off)",
            Faithful: 5000, Meitou: Enhancements.MeitouShadowRange));

        // The game's `shadow quality` (--shadow-quality): the atlas is made again at the new side on the next frame.
        sliders.Add(new Slider(ShadowMapSlider, 0, 2, () => Array.IndexOf(KenshiShadows.QualitySizes, shadow.Settings.MapSize),
            v => shadow.Settings = shadow.Settings with { MapSize = KenshiShadows.MapSize((int)MathF.Round(v)) }, "0",
            Text: v => KenshiShadows.MapSize((int)MathF.Round(v)).ToString(CultureInfo.InvariantCulture) + "²"));

        if (!shadow.Meitou) return null;
        sliders.Add(new Slider(ShadowFilterSlider, 0, 2, () => shadow.FilterQuality, v => shadow.FilterQuality = (int)MathF.Round(v), "0"));
        return new Toggle(FifthCascadeSwitch, () => shadow.MeitouCascades >= 5, v => shadow.MeitouCascades = v ? 5 : 4);
    }

    /// <summary>
    /// The sky's haze, planetshine, cloud height and ambient light.
    /// </summary>
    static void AddSkySliders(List<Slider> sliders, SkyRenderer sky, WorldOptions? options)
    {
        // A viewer option, not the game's: 1 is the game's haze (docs/formats/sky.md).
        sliders.Add(new Slider(HazeStrengthSlider, 0, 3, () => sky.HazeStrength, v => sky.HazeStrength = v, "0.00", Faithful: 1,
            Meitou: Enhancements.MeitouHazeStrength));
        if (options is not null) sliders.Add(HazeDistance(options));
        // Ours (docs/formats/sky.md "Planetshine"): the planet's light at night, a multiple of the physical value.
        sliders.Add(new Slider(PlanetshineStrengthSlider, 0, 200, () => sky.PlanetshineStrength, v => sky.PlanetshineStrength = MathF.Round(v), "0"));
        sliders.Add(new Slider(CloudHeightSlider, VolumetricClouds.MinBottom, VolumetricClouds.MaxBottom, () => sky.CloudHeight,
            v => sky.CloudHeight = MathF.Round(v / 100) * 100, "0", Logarithmic: true));
        // Ours: the sky's ambient light (shade, nights, interiors), a multiple of the game's.
        sliders.Add(new Slider(AmbientStrengthSlider, 0, 3, () => sky.AmbientStrength, v => sky.AmbientStrength = MathF.Round(v * 20) / 20, "0.00"));

        // Ours: a further multiple by night (the exposure sits at its night floor there, so it is not adapted away).
        sliders.Add(new Slider(NightAmbientStrengthSlider, 0, 5, () => sky.NightAmbientStrength,
            v => sky.NightAmbientStrength = MathF.Round(v * 20) / 20, "0.00"));
    }

    /// <summary>
    /// The post chain's rows: anti-aliasing and upscaling, the weather's particles, then the look (tone map, grade, haze, bloom, night grade,
    /// shafts).
    /// </summary>
    static void AddPostSliders(List<Slider> sliders, WorldGpu g, PostProcess post)
    {
        // Upscaling (docs/engine.md "Upscaling"): FSR and DLSS fall back to TAA where their library or backend is missing.
        var up = post.Options.Upscale;
        sliders.Add(new Slider(UpscalerSliders[0], 0, 5, () => AntiAliasingSlider(post.Options), v =>
        {
            up.Kind = AntiAliasingKind(v);
            int i = (int)MathF.Round(v);
            post.Options.Smaa = i == 2;
            post.Options.Fxaa = i >= 1 && i != 2;   // 0: no anti-aliasing at all (FXAA off, no upscaler); FXAA stays the fallback of the upscalers
            if (up.Kind != UpscalerKind.Off) up.Preferred = up.Kind;   // what the Meitou anti-aliasing switch turns back on
        }, "0"));
        sliders.Add(new Slider(UpscalerSliders[1], 0.33f, 1, () => up.EffectiveScale, v => up.Scale = MathF.Round(v * 100) / 100, "0.00", Text: v =>
        {
            string scale = v.ToString("0.00", CultureInfo.InvariantCulture);
            return up.Kind == UpscalerKind.Off && v < 1 ? scale + " (no upscaler: plain scaling)" : scale;
        }));
        sliders.Add(new Slider(UpscalerSliders[2], 0, 1, () => up.Sharpness, v => up.Sharpness = v, "0.00"));
        sliders.Add(DlssPresetSliderFor(up));

        // The weather's particles (docs/render-post.md "Particles"): off, forced to a quarter or half of the render size, by sprite size (the Meitou
        // default), or all full size (as the game).
        sliders.Add(new Slider(ParticleSliders[0], 0, 4, () => WeatherParticlesSlider(g.WeatherParticles, post.Options), v =>
        {
            int i = (int)MathF.Round(v);
            g.WeatherParticles = i > 0;
            if (i > 0)
            {
                (post.Options.LowResParticles, post.Options.ParticleDivisor) = i switch { 1 => (true, 4), 2 => (true, 2), 3 => (true, 0), _ => (false,
                        0) };
            }
        }, "0"));
        sliders.Add(new Slider(ParticleSliders[1], 0.1f, 1, () => g.ParticleDensity, v => g.ParticleDensity = MathF.Round(v * 20) / 20, "0.00"));

        AddLookSliders(sliders, post.Options);
    }

    /// <summary>
    /// The DLSS preset as a slider over <see cref="UpscaleOptions.DlssPresets"/>; a number not among them (a hand-edited setting) shows as the
    /// default.
    /// </summary>
    static Slider DlssPresetSliderFor(UpscaleOptions up)
    {
        var presets = UpscaleOptions.DlssPresets;
        int At(float v) => Math.Clamp((int)MathF.Round(v), 0, presets.Length - 1);

        return new Slider(DlssPresetSlider, 0, presets.Length - 1, () => Math.Max(Array.FindIndex(presets, p => p.Number == up.DlssPreset), 0),
            v => up.DlssPreset = presets[At(v)].Number, "0", Text: v => presets[At(v)].Name);
    }

    /// <summary>
    /// Ours, not the game's (docs/render-post.md "Tone map and grading"): the Meitou tone curve (the tone map switch's Faithful side is the game's
    /// clamp), the saturation / contrast grade, scales on the heat haze, bloom and night grade, and the light shafts.
    /// </summary>
    static void AddLookSliders(List<Slider> sliders, PostOptions options)
    {
        static float Round20(float v) => MathF.Round(v * 20) / 20;

        sliders.Add(new Slider(ToneMapSlider, 1, 3, () => (int)options.ToneMap, v => options.ToneMap = (ToneMapOperator)(int)MathF.Round(v), "0",
            Text: v => (ToneMapOperator)(int)MathF.Round(v) switch
            {
                ToneMapOperator.Shoulder => "shoulder",
                ToneMapOperator.Aces => "ACES",
                ToneMapOperator.Hybrid => "hybrid (clamp + ACES)",
                _ => "clamp (as the game)",
            }));
        sliders.Add(new Slider(ToneMixSlider, 0, 1, () => options.ToneMix, v => options.ToneMix = Round20(v), "0.00"));
        sliders.Add(new Slider(GradingSlider, 0, 1, () => options.Grade ? 1 : 0, v => options.Grade = v >= 0.5f,
            Text: v => v >= 0.5f ? $"saturation {PostOptions.Saturation:0.00}, contrast {PostOptions.Contrast:0.00}" : "off (as the game)"));

        sliders.Add(new Slider(HeatHazeStrengthSlider, 0, 3, () => options.HeatHazeStrength, v => options.HeatHazeStrength = Round20(v), "0.00",
            Faithful: 1, Meitou: Enhancements.MeitouHeatHazeStrength));
        sliders.Add(new Slider(BloomStrengthSlider, 0, 0.25f, () => options.BloomStrength, v => options.BloomStrength = MathF.Round(v * 200) / 200,
            "0.000", Faithful: 0));
        sliders.Add(new Slider(DofApertureSlider, 0.25f, 22, () => options.DofAperture, v => options.DofAperture = MathF.Round(v * 20) / 20, "0.00",
            Logarithmic: true));
        sliders.Add(new Slider(NightGradeStrengthSlider, 0, 1, () => options.NightGradeStrength, v => options.NightGradeStrength = Round20(v),
            "0.00"));

        sliders.Add(new Slider(ShaftStrengthSlider, 0, 1, () => options.ShaftStrength, v => options.ShaftStrength = Round20(v), "0.00"));
        sliders.Add(new Slider(ShaftAirSlider, 0, 4, () => options.ShaftAir, v => options.ShaftAir = Round20(v), "0.00"));
        sliders.Add(new Slider(ShaftAirDawnSlider, 0, 6, () => options.ShaftAirDawn, v => options.ShaftAirDawn = MathF.Round(v * 10) / 10, "0.0"));
        sliders.Add(new Slider(ShaftQualitySlider, 0, 2, () => options.ShaftQuality, v => options.ShaftQuality = Math.Clamp((int)MathF.Round(v), 0,
            2), "0",
            Text: v => (int)MathF.Round(v) switch { 0 => "Low", 1 => "Medium", _ => "High" }));
        sliders.Add(new Slider(GtaoQualitySlider, 0, 2, () => options.GtaoQuality, v => options.GtaoQuality = Math.Clamp((int)MathF.Round(v), 0, 2),
            "0", Text: v => (int)MathF.Round(v) switch { 0 => "Low", 1 => "Medium", _ => "High" }));
    }

    /// <summary>
    /// The Faithful / Meitou switches as checkboxes (ticked: Meitou), the F-key toggles' state (the displacement switch only where the device has
    /// tessellation shaders), then the optional effects: the fifth cascade, bloom (off in both looks, docs/render-post.md "Bloom"; a look preset may
    /// turn it on), and with tessellation the rock smoothing and outline refinement.
    /// </summary>
    static List<Toggle> SettingsToggles(WorldGpu g, IReadOnlyList<Enhancement>? switches, Toggle? fifthCascade)
    {
        bool tessellation = g.Sky.Gpu.HasTessellation;
        var toggles = switches?
            .Where(e => e.Id != "displace" || tessellation)
            .Select(e => new Toggle(e.Name, () => e.IsMeitou, v => e.IsMeitou = v, () => e.IsMeitou ? e.Meitou : e.Faithful, Remaster: true,
                OnOff: e.Faithful is "off" or "none", Optional: e.Optional))
            .ToList() ?? [];
        if (fifthCascade is not null) toggles.Add(fifthCascade);

        if (g.Post is { } post) AddPostToggles(toggles, post);
        if (g.Foliage is { } farGrass)
            toggles.Add(new Toggle(GrassCoverSwitch, () => farGrass.GrassFarCover, v => farGrass.GrassFarCover = v, Remaster: true, OnOff: true));
        if (g.Surfaces is { } wetWorld)
        {
            toggles.Add(new Toggle(WetWorldSwitch, () => wetWorld.WetWorld, v => wetWorld.WetWorld = v, Remaster: true, OnOff: true,
                Optional: true));
        }

        if (g.Characters is not null && g.Options is { } materials) AddMaterialToggles(toggles, g, materials);
        if (tessellation)
        {
            toggles.Add(new Toggle(RockSmoothingSwitch, () => RockSmoothing.TessellateActive, v => RockSmoothing.TessellateActive = v, Remaster: true,
                OnOff: true));
            toggles.Add(new Toggle(OutlineRefineSwitch, () => GroundRelief.RefinePixels > 0,
                v => GroundRelief.RefinePixels = v ? GroundRelief.DefaultRefinePixels : 0,
                Remaster: true, OnOff: true));
        }
        return toggles;
    }

    /// <summary>
    /// The post chain's switches: bloom, the camera effects, the hybrid GI's parts and the traced sun shadows' grass.
    /// </summary>
    static void AddPostToggles(List<Toggle> toggles, PostProcess post)
    {
        toggles.Add(new Toggle(BloomSwitch, () => post.Options.Bloom, v => post.Options.Bloom = v, Remaster: true, OnOff: true, Optional: true));
        toggles.Add(new Toggle(CameraEffectsSwitch, () => post.Options.CameraEffectsEverywhere, v => post.Options.CameraEffectsEverywhere = v,
            Remaster: true, OnOff: true, Optional: true));

        // The hybrid GI's parts (--gi-hybrid), plain on / off rows; they act while the Hybrid GI choice is on.
        toggles.Add(new Toggle(GiRelocateSwitch, () => post.Options.GiRelocate, v => post.Options.GiRelocate = v));
        toggles.Add(new Toggle(GiCloudShadowsSwitch, () => post.Options.GiCloudShadows, v => post.Options.GiCloudShadows = v));
        toggles.Add(new Toggle(GiCacheSwitch, () => post.Options.GiCache, v => post.Options.GiCache = v));

        // The traced sun shadows' grass, a plain on / off row; it acts while the sun's shadows are traced.
        toggles.Add(new Toggle(TracedGrassSwitch, () => post.Options.TracedGrass, v => post.Options.TracedGrass = v));
    }

    /// <summary>
    /// The Advanced materials' parts (<see cref="WorldOptions.Chars2Parts"/>), plain on / off rows; they act while it is on.
    /// </summary>
    static void AddMaterialToggles(List<Toggle> toggles, WorldGpu g, WorldOptions materials)
    {
        var parts = new[]
        {
            (MaterialHairSwitch, Characters.CharacterRenderer.SurfaceHair), (MaterialClothSwitch, Characters.CharacterRenderer.SurfaceCloth),
            (MaterialSkinSwitch, Characters.CharacterRenderer.SurfaceSkin), (MaterialEyesSwitch, Characters.CharacterRenderer.SurfaceEyes),
            (MaterialMetalSwitch, Characters.CharacterRenderer.SurfaceMetal), (MaterialBeadsSwitch, Characters.CharacterRenderer.SurfaceBeads),
        };
        foreach (var (label, bit) in parts)
        {
            toggles.Add(new Toggle(label, () => (materials.Chars2Parts & bit) != 0, v =>
            {
                materials.Chars2Parts = v ? materials.Chars2Parts | bit : materials.Chars2Parts & ~bit;
                if (g.Characters is { } c) c.SurfaceParts = materials.Chars2Parts;
            }));
        }
    }

    /// <summary>
    /// The billboard (impostor atlas) disk cache's delete: deleted files are baked again when next needed; the resident atlases stay until evicted.
    /// </summary>
    static List<PanelAction> SettingsActions(WorldGpu g)
    {
        var actions = new List<PanelAction>();
        if (g.Foliage?.ImpostorDiskCache is not { } cache) return actions;
        actions.Add(new PanelAction("Delete billboard cache", () =>
        {
            var (files, bytes) = cache.Measure();
            return $"Delete {files} billboard atlases ({bytes / 1048576.0:0} MB) in {cache.Root}?";
        }, () =>
        {
            var (files, bytes) = cache.Clear();
            Console.WriteLine($"impostors deleted {files} cached atlases ({bytes / 1048576.0:0} MB) from {cache.Root}");
            return $"Deleted {files} atlases ({bytes / 1048576.0:0} MB); they are baked again when next needed.";
        }));
        return actions;
    }

    /// <summary>
    /// The third column: textures. The game's <c>texture resolution gimping</c> (docs/formats/settings.md): the object, foliage and character
    /// textures are loaded again at the new level as the slider moves; the terrain's layer arrays keep the size they were made with
    /// (--texture-quality at start).
    /// </summary>
    static List<Slider> TextureSliders()
    {
        int terrainLevel = Data.Textures.TextureQuality.Level;
        var labels = Data.Textures.TextureQuality.Labels;
        return
        [
            new(TextureQualitySlider, 0, Data.Textures.TextureQuality.Maximum, () => Data.Textures.TextureQuality.Level,
                v => Data.Textures.TextureQuality.Level = (int)MathF.Round(v), "0",
                Text: v => (int)MathF.Round(v) is var level && level != terrainLevel ? $"{labels[level]} (terrain at start)" : labels[level]),
        ];
    }

    #endregion

    #region Sky and shadows

    /// <summary>
    /// Whether the frame keeps the detailed statistics (the shadow cascades' and the foliage's per-step times); the viewer turns it on.
    /// </summary>
    public static bool DetailedStats;

    /// <summary>
    /// The profiler's phase label of each shadow cascade.
    /// </summary>
    static readonly string[] CascadeLabels = [.. Enumerable.Range(0, ShadowSettings.MaxCascades).Select(i => $"shadow c{i}")];

    /// <summary>
    /// Each cascade's character casting spec for <see cref="ShadowCasters.Prepare"/> (reused, cleared after each frame).
    /// </summary>
    static readonly (int, Vector4[], float, float)[] CasterSpecs = new (int, Vector4[], float, float)[ShadowSettings.MaxCascades];

    /// <summary>
    /// An hour as <c>HH:MM</c>.
    /// </summary>
    public static string TimeText(float hour)
    {
        int minutes = (int)MathF.Round(hour * 60) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    /// <summary>
    /// The unit of <c>Draw</c>'s <c>time</c> argument: seconds divided by 600 (the viewer's real seconds, the game's game seconds).
    /// </summary>
    const double SecondsPerTimeUnit = 600;

    /// <summary>
    /// Per-cascade and per-step CPU times in the stats strings (ShadowPass.CasterStats, FoliageRenderer's details); the viewer's screenshots and
    /// benchmarks turn it on.
    /// </summary>

    /// <summary>
    /// Sky-only loading frames, with the existing sky and post chain and without touching world streaming or character content.
    /// </summary>
    public static void DrawLoadingSky(WorldGpu gpu, WorldScene scene, WorldCamera camera, int width, int height, float hour, float seconds,
        float fogDistance)
    {
        gpu.Terrain.Activate();
        var post = gpu.PostChain ?? throw new InvalidOperationException("the sky frame needs the post-processing chain");
        post.GiResolve = 0;
        if (gpu.Post is { } vulkanPost) vulkanPost.SsrSurfaces = false;
        post.Begin(width, height);
        if (post is PostProcess { Unallocated: true }) return;
        float aspect = width / (float)Math.Max(height, 1);
        var eye = camera.Eye;
        var view = camera.View;
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        var vp = rotation * Jitter.Apply(camera.Projection(aspect, 1, 1000), post.JitterPixels, post.RenderWidth, post.RenderHeight);
        gpu.Sky.SetEye(eye, gpu.Terrain.HeightAt, float.NegativeInfinity);
        gpu.Sky.Eye = eye;
        gpu.Sky.Clock = scene.Clock;
        gpu.Sky.Hour = hour;
        gpu.Sky.StarSeconds = seconds;
        var (colours, _) = gpu.Sky.Prepare(scene.Clock.SunDirection(hour), eye.Y, fogDistance);
        post.AutoExposure = (gpu.Sky.MinLuminance, gpu.Sky.MaxLuminance);
        post.SetCamera(eye, view, camera.FieldOfView, aspect);
        post.BeginScene(1, 1000, camera.FieldOfView, aspect);
        var host = gpu.Scene ??= new SceneHost(post.Context);
        host.Open(5, post.SceneTargets, new ClearColour(0, 0, 0, 0), clearDepth: true);
        gpu.Sky.Draw(vp, colours);
        host.Close();

        // Motion and particle callbacks belong to the world that is being replaced.
        var motion = gpu.PostChain?.ObjectMotion;
        var particles = post.Particles;
        gpu.PostChain?.ObjectMotion = null;
        post.Particles = null;
        post.SetCloudMotion(null);
        post.Options.CameraEffectsHeld = true;
        try
        {
            post.End();
        }
        finally
        {
            post.Options.CameraEffectsHeld = false;
            gpu.PostChain?.ObjectMotion = motion;
            post.Particles = particles;
        }
    }

    /// <summary>
    /// --log-spikes: a step of the sky-prepare stage over 5 ms is noted (<c>slow sky &lt;step&gt;</c>), the time since <paramref name="since"/>,
    /// which moves on.
    /// </summary>
    static void SkyStep(string step, ref long since)
    {
        long now = Stopwatch.GetTimestamp();
        double ms = (now - since) * 1000.0 / Stopwatch.Frequency;
        since = now;
        if (ms > 5) SpikeLog.Note($"slow sky {step}", ms: ms);
    }

    /// <summary>
    /// The air layer's density by the time of day (docs/render-shafts.md "Air layer"): <c>ShaftAirDawn</c> at sunrise and at sunset, easing
    /// (smoothstep) to
    /// <c>ShaftAirDay</c> over <c>ShaftAirRamp</c> hours after sunrise and before sunset; at night the dawn value (the shafts need the sun
    /// anyway).
    /// </summary>
    internal static float ShaftAirByTime(PostOptions o, SkyClock clock, float hour)
    {
        hour = ((hour % 24) + 24) % 24;
        float fromEvent = hour < clock.Sunrise || hour > clock.Sunset ? 0 : Math.Min(hour - clock.Sunrise, clock.Sunset - hour);
        float t = Math.Clamp(fromEvent / PostOptions.ShaftAirRamp, 0, 1);
        return PostOptions.ShaftAirDay + (o.ShaftAirDawn - PostOptions.ShaftAirDay) * (1 - t * t * (3 - 2 * t));
    }

    /// <summary>
    /// How much of the haze in shadow the light shafts take off (docs/render-shafts.md): the strength times the sun's share of the light the air
    /// scatters (the sun's colour against the sky's ambient, by luminance), faded out as the sun sets, when the sky's light is all there is.
    /// </summary>
    internal static float ShaftDarkening(PostOptions o, WorldLighting light, float sunY)
    {
        static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        float sun = Luminance(light.SunColour), sky = Luminance(light.AmbientSky);
        float share = sun / Math.Max(sun + sky, 1e-4f);
        float t = Math.Clamp((sunY + 0.02f) / 0.07f, 0, 1);
        return o.ShaftStrength * share * t * t * (3 - 2 * t);
    }

    /// <summary>
    /// How overcast the sky is, 0..1, for what fades under clouds (the light shafts, the Meitou water's glint): the sky's cloud density c, or the
    /// weather's <c>rainAmount</c> (saturate(rain / 50)) where that is more, since rain falls from a covered sky whatever c the weather lists
    /// (<c>swamp rain no wind</c> has c 0.5 and rain 40).
    /// </summary>
    internal static float Overcast(float cloudDensity, float rain) => MathF.Max(Math.Clamp(cloudDensity, 0, 1), WeatherSurfaces.RainAmount(rain));

    /// <summary>
    /// <see cref="Overcast(float, float)"/> of the sky's cloud density and the weather surfaces' rain.
    /// </summary>
    static float Overcast(WorldGpu gpu) => Overcast(gpu.Sky.CloudDensity, gpu.Surfaces?.Rain ?? 0);

    /// <summary>
    /// What is left of the light shafts under the cloud layer (docs/render-shafts.md "Cloud cover"): the game's sun shines through any weather, but
    /// crisp shafts under a full overcast in the rain read wrong, so the darkening loses <c>ShaftCloudShade</c> of itself as the cloud density c goes
    /// from <c>ShaftCloudFrom</c> to 1 (smoothstep).
    /// </summary>
    internal static float ShaftCloudFade(float cloudDensity)
    {
        float t = Math.Clamp((cloudDensity - PostOptions.ShaftCloudFrom) / Math.Max(1 - PostOptions.ShaftCloudFrom, 1e-3f), 0, 1);
        return 1 - Math.Clamp(PostOptions.ShaftCloudShade, 0, 1) * t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The game's <c>heatHaze</c> (docs/formats/weather.md "Heat haze"): the target is the weather's <c>heat haze</c>
    /// × strength × saturate(6 · sunY), with strength 1 (a forced weather has no scheduler's roll); the value
    /// moves towards it at 1/3 per second of real time (game speed 1), and jumps there for still pictures. The animation's
    /// <c>gameTime</c> is the caller's game clock when it has one, else real time at game speed 1 (held still for pictures).
    /// </summary>
    static void UpdateHeatHaze(WorldGpu gpu, IWorldPostChain post, float sunY)
    {
        float dt = (float)Math.Min(gpu.HeatHazeClock.Elapsed.TotalSeconds, 0.25);
        bool first = !gpu.HeatHazeClock.IsRunning;
        gpu.HeatHazeClock.Restart();

        // With the scheduler the value is the state's own (the weather's field × the strength × the sun factor, ramped on the settling time, snapped
        // on a teleport); without a scheduler (no database) it is the old forced path at strength 1, ramped here.
        var weather = gpu.Weather;
        float field = weather?.State.Weather.HeatHaze ?? gpu.Sky.Weather.HeatHaze;
        gpu.HeatHazeTarget = HeatHaze.Target(field, weather?.State.Strength ?? 1, sunY);
        if (weather is not null) post.HeatHazeAmount = weather.State.HeatHaze;
        else post.HeatHazeAmount = first || post.InstantAdaptation ? gpu.HeatHazeTarget : HeatHaze.Step(post.HeatHazeAmount, gpu.HeatHazeTarget,
                weather?.Times.Settling ?? dt);
        if (!post.InstantAdaptation) gpu.HeatHazeHours += dt * HeatHaze.HoursPerSecond;
        post.HeatHazeHours = gpu.GameHours ?? gpu.HeatHazeHours;

        // the heat haze's D follows the install's `view distance`, not the sky haze's constant
        post.HeatHazeFarClip = KenshiHaze.FarDistance(HeatHaze.ViewDistanceSetting);
    }

    // DrawShadows's cascades for the characters (render thread).

    /// <summary>
    /// The sun's shadow cascades (ShadowPass, docs/formats/shadows.md): fitted to this camera, their casters drawn by the renderers'
    /// depth-only paths (terrain, objects, foliage meshes; detail chosen from the camera's eye), before the reflection and the main pass.
    /// The characters of all the cascades drawn are gathered first, in one walk.
    /// </summary>
    /// <param name="sunHeight">The real sun's height: the map is drawn along the lighting direction while the sun still has a colour (as the
    /// game).</param>
    public static void DrawShadows(WorldGpu gpu, WorldCamera camera, WorldRenderOptions render, WorldLighting light, int width, int height,
        float? sunHeight = null,
        bool? temporal = null)
    {
        var shadow = gpu.Shadow!;

        // The caller's post chain owns the temporal decision (Metal's PostTaa/MetalFX is outside gpu.Post).
        shadow.Temporal = temporal ?? gpu.PostChain?.Temporal == true;
        var v = camera.View;
        var up = Vector3.Normalize(new Vector3(v.M12, v.M22, v.M32));
        var view = new ShadowView(camera.Eye, camera.Forward, up, camera.FieldOfView, width / (float)Math.Max(height, 1), camera.Near,
            camera.Distance);
        gpu.Terrain.DepthTriangles = 0;

        var casters = new ShadowCasters(gpu, shadow, render);
        var landmarks = render.Objects && gpu.Objects is { HasLandmarks: true } lo
            ? new ShadowPass.LandmarkCasters(spheres => lo.LandmarkCasters(camera.Eye, spheres),
                (_, worldToClip, planes, lodEye) => lo.DrawLandmarksDepth(worldToClip, lodEye, planes, render, gpu.Terrain))
            : (ShadowPass.LandmarkCasters?)null;
        shadow.CameraFlying = gpu.CameraFlying;
        shadow.TracedWhole = gpu.Traced;   // the rays take the sun at every depth, characters included (Gi.TracedCharacters)
        shadow.Render(view, light.SunDirection, casters.Draw, sunHeight, landmarks, casters.Prepare);
        shadow.CasterStats = casters.Stats();
    }

    #endregion

    #region Shadow casters

    /// <summary>
    /// One <see cref="DrawShadows"/>'s casters: each cascade's terrain, objects, characters and foliage by the renderers' depth-only paths, timed per
    /// kind.
    /// </summary>
    sealed class ShadowCasters(WorldGpu gpu, ShadowPass shadow, WorldRenderOptions render)
    {
        /// <summary>
        /// The object instances and foliage meshes drawn over the cascades.
        /// </summary>
        int _objects, _foliage;

        /// <summary>
        /// The cascades' detail line (null unless <see cref="DetailedStats"/>).
        /// </summary>
        readonly System.Text.StringBuilder? _cascades = DetailedStats ? new() : null;

        /// <summary>
        /// The characters of every cascade drawn this frame, gathered in one walk before the first (CharacterRenderer.PrepareDepth).
        /// </summary>
        public void Prepare(ReadOnlySpan<ShadowPass.CasterView> views)
        {
            if (gpu.Characters is not { } characters || views.IsEmpty) return;
            var specs = CasterSpecs.AsSpan(0, views.Length);
            for (int k = 0; k < views.Length; k++)
            {
                var (floor, minRadius) = CharacterCasting(views[k].Cascade);
                specs[k] = (views[k].Cascade.Index, views[k].Planes, floor, minRadius);
            }
            characters.PrepareDepth(views[0].LodEye, specs);
            specs.Clear();
            StageClock.Sub("characters");
        }

        /// <summary>
        /// One cascade's casters; a far cascade (ShadowLod) takes coarser ones.
        /// </summary>
        public void Draw(ShadowCascade cascade, Matrix4x4 worldToClip, Vector4[] planes, Vector3 lodEye)
        {
            var terrain = gpu.Terrain;
            long t0 = Stopwatch.GetTimestamp();
            long triangles = terrain.DepthTriangles;
            float coarse = shadow.Meitou && ShadowLod.Applies(cascade.Index) ? (float)cascade.Texel : 0;
            terrain.DrawDepth(worldToClip, lodEye, planes, render, coarse);
            StageClock.Sub("terrain");

            long t1 = Stopwatch.GetTimestamp();
            int objectInstances = 0, objectCalls = 0, foliageInstances = 0, foliageCalls = 0;
            if (render.Objects && gpu.Objects is { } o)
            {
                o.DrawDepth(worldToClip, lodEye, planes, render, terrain, coarse);
                _objects += o.DrawnInstances;
                (objectInstances, objectCalls) = (o.DrawnInstances, o.DrawCalls);
            }
            StageClock.Sub("objects");
            var (characterFloor, characterMinRadius) = CharacterCasting(cascade);
            gpu.Characters?.DrawDepth(worldToClip, lodEye, planes, characterFloor, characterMinRadius, cascade.Index);
            StageClock.Sub("characters");

            long t2 = Stopwatch.GetTimestamp();
            if (gpu.Foliage is { } f)
            {
                f.DrawDepth(worldToClip, lodEye, planes, render, terrain, maxRange: shadow.EffectiveRange * 1.2f,
                    minSize: shadow.MinFoliageCaster(cascade),
                    texel: (float)cascade.Texel, coarse: coarse > 0);
                _foliage += f.DrawnInstances;
                (foliageInstances, foliageCalls) = (f.DrawnInstances, f.DrawCalls);
            }
            StageClock.Phase(CascadeLabels[cascade.Index]);

            long t3 = Stopwatch.GetTimestamp();
            double ms = 1000.0 / Stopwatch.Frequency;
            shadow.PhaseMs[0] += (t1 - t0) * ms;
            shadow.PhaseMs[1] += (t2 - t1) * ms;
            shadow.PhaseMs[2] += (t3 - t2) * ms;
            _cascades?.Append($" [c{cascade.Index}: texel {cascade.Texel:0.0}, "
                + $"terrain {(terrain.DepthTriangles - triangles) / 1000}k tri {(t1 - t0) * ms:0.00} ms, "
                + $"objects {objectInstances} in {objectCalls} calls {(t2 - t1) * ms:0.00} ms, "
                + $"foliage {foliageInstances} in {foliageCalls} calls {(t3 - t2) * ms:0.00} ms ({gpu.Foliage?.DepthDetail})]");
        }

        /// <summary>
        /// The casters drawn over the cascades and their CPU time per kind, for the statistics.
        /// </summary>
        public string Stats()
        {
            var phase = shadow.PhaseMs;
            return $"{gpu.Terrain.DepthTriangles:N0} terrain triangles, {_objects} objects, {_foliage} foliage meshes (over the cascades); "
                + $"cpu terrain {phase[0]:0.00}, objects {phase[1]:0.00}, foliage {phase[2]:0.00} ms;{_cascades}"
                + (shadow.Meitou ? $"; meitou: {shadow.DescribeMeitou()}" : "");
        }

        /// <summary>
        /// Characters take the far cascades' floor in both shadow modes: their levels are Meitou's own, and every character is in every cascade that
        /// holds the crowd. In Meitou's coarse cascades a part under a texel across casts nothing (ShadowPass.MinCharacterCasterTexels).
        /// </summary>
        (float LodFloor, float MinRadius) CharacterCasting(ShadowCascade cascade)
        {
            bool far = ShadowLod.Applies(cascade.Index);
            float coarse = shadow.Meitou && far ? (float)cascade.Texel : 0;
            float floor = ShadowLod.ObjectLodFloor(far ? (float)cascade.Texel : 0, render.TerrainPixelScale);
            return (floor, ShadowPass.CharacterCasterCull ? coarse * ShadowPass.MinCharacterCasterTexels * 0.5f : 0);
        }
    }

    #endregion
}