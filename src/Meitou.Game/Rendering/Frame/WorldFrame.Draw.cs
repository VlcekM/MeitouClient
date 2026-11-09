using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// <see cref="Draw"/>: one world frame, a method per profiler stage (<see cref="StageClock"/>), in the order they run.
/// </summary>
static partial class WorldFrame
{
    /// <summary>
    /// What the stages of one <see cref="Draw"/> share: its inputs, and what the earlier stages work out for the later ones.
    /// </summary>
    struct FrameState
    {
        /// <summary>
        /// The world's renderers.
        /// </summary>
        public WorldGpu Gpu;

        /// <summary>
        /// The loaded region.
        /// </summary>
        public WorldScene Scene;

        /// <summary>
        /// The camera.
        /// </summary>
        public WorldCamera Camera;

        /// <summary>
        /// What to draw (textures, objects, water, the debug views).
        /// </summary>
        public WorldRenderOptions Render;

        /// <summary>
        /// The post-processing chain the scene is drawn into (set by <see cref="BeginFrame"/>).
        /// </summary>
        public IWorldPostChain Post;

        /// <summary>
        /// The output's size in pixels.
        /// </summary>
        public int Width, Height;

        /// <summary>
        /// The time of day (game hours), the animation clock (seconds) and the haze distance.
        /// </summary>
        public float Hour, Time, FogDistance;

        /// <summary>
        /// The hour the weather schedule reads when it is not <see cref="Hour"/>; null: <see cref="Hour"/>.
        /// </summary>
        public float? WeatherHour;

        /// <summary>
        /// BeginFrame: the render size (smaller than the display with an upscaler).
        /// </summary>
        public int RenderWidth, RenderHeight;

        /// <summary>
        /// BeginFrame: the upscaler's jitter.
        /// </summary>
        public Vector2 Jitter;

        /// <summary>
        /// BeginFrame: the eye.
        /// </summary>
        public Vector3 Eye;

        /// <summary>
        /// UpdateTerrain and PrepareSky: the ground under the eye, and the floor (ground or water) under it.
        /// </summary>
        public float GroundUnderEye, Floor;

        /// <summary>
        /// PrepareSky: the sun's direction.
        /// </summary>
        public Vector3 Sun;

        /// <summary>
        /// PrepareSky: the sky's colours.
        /// </summary>
        public SkyColours Colours;

        /// <summary>
        /// PrepareSky: the sky's light.
        /// </summary>
        public WorldLighting Light;

        /// <summary>
        /// DrawReflection: whether the water reflects this frame.
        /// </summary>
        public bool Reflecting;

        /// <summary>
        /// DrawSky: the scene's pass host.
        /// </summary>
        public SceneHost Host;

        /// <summary>
        /// DrawSky: the output's aspect ratio.
        /// </summary>
        public float Aspect;

        /// <summary>
        /// DrawSky: the camera's view, and the view without its translation (the world to view rotation).
        /// </summary>
        public Matrix4x4 View, Rotation;

        /// <summary>
        /// DrawScene, for DrawParticles: the scene's views for the weather particles: the scene's view-projection, the jittered close one, and the
        /// unjittered pair for the low-resolution particles blended after the upscaler.
        /// </summary>
        public Matrix4x4 ParticleView, ParticleClose, ParticleLow, ParticleLowClose;

        /// <summary>
        /// DrawScene, for DrawParticles: the near plane splitting the close particles off (0 when no scene was drawn).
        /// </summary>
        public float ParticleNear;
    }

    #region Frame

    /// <summary>
    /// One world frame, in order: the updates (terrain, objects, foliage, characters; each a profiler stage, <see cref="StageClock"/>), then the sky,
    /// reflection, shadows, GI, the scene passes, post-processing and the overlays, into a <paramref name="width"/> × <paramref name="height"/>
    /// output.
    /// <paramref name="hour"/> is the time of day, <paramref name="time"/> the animation clock in seconds, <paramref name="weatherHour"/> the hour
    /// the weather schedule reads when it is not <paramref name="hour"/>. Render thread.
    /// </summary>
    public static void Draw(WorldGpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, int width, int height, float hour,
        float time, float fogDistance, float? weatherHour = null)
    {
        var f = new FrameState
        {
            Gpu = gpu,
            Scene = scene,
            Camera = camera,
            Render = render,
            Width = width,
            Height = height,
            Hour = hour,
            Time = time,
            FogDistance = fogDistance,
            WeatherHour = weatherHour,
        };
        if (!BeginFrame(ref f)) return;

        UpdateWorld(ref f);
        PrepareWorld(ref f);
        DrawPicture(ref f);
    }

    /// <summary>
    /// The frame's streamed and animated parts: terrain, objects, foliage and characters.
    /// </summary>
    static void UpdateWorld(ref FrameState f)
    {
        UpdateTerrain(ref f);
        StageClock.Lap(0);
        UpdateObjects(ref f);
        StageClock.Lap(1);
        UpdateFoliage(ref f);
        StageClock.Lap(2);
        UpdateCharacters(ref f);
        StageClock.Lap(StageClock.UpdChars);
    }

    /// <summary>
    /// The sky's state, the effects, the shadows, the lighting and the blood decals, all before anything is drawn.
    /// </summary>
    static void PrepareWorld(ref FrameState f)
    {
        PrepareSky(ref f);
        StageClock.Lap(3);
        UpdateEffects(ref f);
        StageClock.Lap(13);
        Shadows(ref f);
        StageClock.Lap(12);
        UpdateLighting(ref f);
        StageClock.Lap(14);
        RecordBlood(ref f);
        StageClock.Lap(StageClock.Blood, "blood decals");
    }

    /// <summary>
    /// Draws the reflection, the sky and the scene into the picture, then finishes it.
    /// </summary>
    static void DrawPicture(ref FrameState f)
    {
        DrawReflection(ref f);
        StageClock.Lap(4);
        DrawSky(ref f);
        StageClock.Lap(5);
        DrawScene(ref f);   // laps terrain, objects, foliage, characters and water
        FinishScene(ref f);   // laps the GI resolve, the shafts and the fog volumes
        DrawParticles(ref f);   // laps particles when there are any
        FinishPicture(ref f);
        StageClock.Lap(10);
    }

    /// <summary>
    /// Starts the post chain's frame and reads the render size, jitter and eye; false when out of video memory (no world this frame).
    /// </summary>
    static bool BeginFrame(ref FrameState f)
    {
        var gpu = f.Gpu;
        gpu.Terrain.Activate();
        var post = f.Post = gpu.PostChain ?? throw new InvalidOperationException("the world frame needs a post-processing chain");
        if (gpu.Surfaces is { } surfaces) surfaces.GameTime = f.Time;

        // Everything is drawn into the post-processing chain's HDR framebuffer (before the reflection pass, which restores whatever is bound).
        // The GI resolve while the probes light the world: the scene targets carry the albedo target, the lit fragments write it.
        bool giResolve = gpu.GiResolve > 0 && gpu.GiActive && gpu.Probes is { Enabled: true, Shade: true };
        if (gpu.Post is { } giPost) giPost.GiResolve = giResolve ? gpu.GiResolve : 0;
        if (gpu.Probes is { } giProbes) giProbes.Resolve = giResolve;
        post.Begin(f.Width, f.Height);
        if (post is PostProcess { Unallocated: true })
        {
            gpu.Guard?.Tick();
            return false;
        }

        // The scene is drawn at the render size (smaller than the display with an upscaler), its projection jittered by the upscaler.
        (f.RenderWidth, f.RenderHeight) = (post.RenderWidth, post.RenderHeight);

        // The terrain LOD measures its error in the pixels actually rendered (after the upscaler's render scale).
        f.Render.TerrainPixelScale = TerrainLod.ProjectionScale(f.RenderHeight, f.Camera.FieldOfView);
        if (gpu.Terrain.TextureSet is { } nearRange) nearRange.PixelScale = f.Render.TerrainPixelScale;   // the terrain layers' near ranges
        f.Jitter = post.JitterPixels;
        f.Eye = f.Camera.Eye;
        gpu.Guard?.Tick();
        return true;
    }

    #endregion

    #region Per-frame updates

    /// <summary>
    /// The "upd-terrain" stage: what the streamers share (areas, look-ahead, kept points, the eye's lift), then the terrain's streaming.
    /// </summary>
    static void UpdateTerrain(ref FrameState f)
    {
        var gpu = f.Gpu;
        var eye = f.Eye;
        var camera = f.Camera;

        var kept = ObjectStreamer.MergeKept(gpu.Kept, eye);
        long areasNow = Environment.TickCount64;
        if (1 + kept.Count >= gpu.Areas || areasNow - gpu.AreasAt > AreasHoldMs)
            (gpu.Areas, gpu.AreasAt) = (1 + kept.Count, areasNow);
        if (gpu.Objects is { } areaObjects) areaObjects.Areas = gpu.Areas;
        if (gpu.Foliage is { } areaFoliage) areaFoliage.Areas = gpu.Areas;
        if (gpu.Characters is { } areaCharacters) areaCharacters.Areas = gpu.Areas;
        if (gpu.Streamer is { } areaTerrain) areaTerrain.Areas = gpu.Areas;

        if (gpu.Streamer is { } aheadTerrain)
            (aheadTerrain.Ahead, aheadTerrain.Kept, aheadTerrain.Guard) = (gpu.Ahead, kept, gpu.Guard);
        if (gpu.Objects is { } aheadObjects)
            (aheadObjects.Ahead, aheadObjects.Kept, aheadObjects.KeptViewDistance) = (gpu.Ahead, kept, camera.Distance);
        if (gpu.Foliage is { } aheadFoliage)
            (aheadFoliage.Ahead, aheadFoliage.Kept, aheadFoliage.KeptViewDistance) = (gpu.Ahead, kept, camera.Distance);
        if (gpu.Characters is { } aheadCharacters)
            (aheadCharacters.Ahead, aheadCharacters.Kept, aheadCharacters.KeptViewDistance) = (gpu.Ahead, kept, camera.Distance);

        // The loading counts the eye's height over the ground beyond the game's cameras (EyeLift): terrain textures, object zones, foliage,
        // characters. In flight the weather fog is off.
        float groundUnderEye = f.GroundUnderEye = gpu.Terrain.HeightAt(eye.X, eye.Z);
        if (gpu.Streamer is { } liftedTerrain) liftedTerrain.GroundUnderEye = groundUnderEye;
        if (gpu.Objects is { } liftedObjects) liftedObjects.GroundUnderEye = groundUnderEye;
        if (gpu.Characters is { } liftedCharacters) liftedCharacters.GroundUnderEye = groundUnderEye;
        gpu.Sky.SetFlightHeight(eye.Y - groundUnderEye);

        gpu.Streamer?.Update(eye);
    }

    /// <summary>
    /// The "upd-objects" stage: the views the texture streaming measures pixels in, then the objects' streaming and LOD.
    /// </summary>
    static void UpdateObjects(ref FrameState f)
    {
        var gpu = f.Gpu;

        // the generated object levels are placed by the pixel size too
        if (gpu.Objects is { } lodObjects) lodObjects.LodPixelsPerRadian = f.Render.TerrainPixelScale;

        // The object textures' mip streaming measures pixels at the render size.
        gpu.Objects?.SetView(f.RenderWidth, f.RenderHeight, f.Camera.FieldOfView);
        gpu.Foliage?.SetView(f.RenderWidth, f.RenderHeight, f.Camera.FieldOfView);
        gpu.Characters?.SetView(f.RenderWidth, f.RenderHeight, f.Camera.FieldOfView);

        gpu.Objects?.Update(f.Eye);
    }

    /// <summary>
    /// The "upd-foliage" stage.
    /// </summary>
    static void UpdateFoliage(ref FrameState f)
    {
        if (f.Gpu.Foliage is not { } foliage) return;
        foliage.LodPixelsPerRadian = f.Render.TerrainPixelScale;   // the generated mesh levels measure their deviation in rendered pixels
        foliage.GroundUnderEye = f.GroundUnderEye;
        foliage.Update(f.Eye);
    }

    /// <summary>
    /// The "upd-chars" stage: the characters' update (fill, textures, gather, pose, materials), then from its poses the stuck bolts, the squad
    /// portraits, the hover masks and the item icons; each a part of the stage (<see cref="StageClock.PartNames"/>).
    /// </summary>
    static void UpdateCharacters(ref FrameState f)
    {
        var gpu = f.Gpu;
        var camera = f.Camera;
        var render = f.Render;

        if (gpu.Characters is { } shown)
        {
            // The view the characters show in (CharacterRenderer.Shown): out to past their draw distance, the water's mirror image when the
            // reflection draws them.
            float aspect = f.RenderWidth / (float)Math.Max(f.RenderHeight, 1);
            shown.ShownView = WorldCamera.FrustumPlanes(camera.View * camera.Projection(aspect, camera.ClipNear, Math.Max(camera.ViewDistance,
                2 * shown.DrawDistance)));
            shown.ShownMirror = render.Water && render.Reflections && gpu.Water is not null && gpu.Reflection is { Level: >= 2 }
                ? WorldWater.Height : null;
        }
        gpu.Characters?.Update(f.Eye);

        if (gpu.Characters is { } posed && gpu.Debris is { } boltMeshes)
        {
            posed.CollectBolts(gpu.BoltPieces);

            // loads a mesh the first time (a held limb's with its look)
            foreach (var bolt in gpu.BoltPieces) boltMeshes.Extent(bolt.Mesh, null, bolt.Limb);
        }
        StageClock.Part(StageClock.PartBolts);

        gpu.Characters?.DrawPortraits();   // the squad portraits, from this update's poses
        StageClock.Part(StageClock.PartPortraits);

        // Before the colour pass, whose statistics stand.
        gpu.Characters?.DrawHoverMasks(gpu.HoverOutline is null ? [] : gpu.HoverKeys, f.Width, f.Height, camera.View, camera.FieldOfView, f.Eye);
        StageClock.Part(StageClock.PartHover);

        gpu.Characters?.DrawIcons();   // the item icons being made, from the same update
        gpu.Objects?.DrawBuildingIcons();   // the build window's pictures being made
        StageClock.Part(StageClock.PartIcons);
    }

    /// <summary>
    /// The "sky-prepare" stage: the weather at the camera, the sky's tables, sun and ambient light for this sun and eye height, the exposure band,
    /// the heat haze, the water's waves and the clouds; then the view distance the haze allows.
    /// </summary>
    static void PrepareSky(ref FrameState f)
    {
        var gpu = f.Gpu;
        var eye = f.Eye;
        var post = f.Post;
        var render = f.Render;

        float floor = gpu.Terrain.HeightAt(eye.X, eye.Z);
        if (render.Water) floor = Math.Max(floor, WorldWater.Height);
        f.Floor = floor;
        f.Camera.EyeClearance = Math.Max(eye.Y - floor, 1);

        // Above the game's camera heights the haze (and the water's glint) move to the viewer's own altitude-aware forms
        // (SkyRenderer.AltitudeWeight).
        gpu.Sky.SetEye(eye, gpu.Terrain.HeightAt, render.Water ? WorldWater.Height : float.NegativeInfinity);

        // The atmosphere (SkyRenderer): sky tables, sun and ambient light for this sun and eye height. Thinner air higher up: the
        // haze takes longer to close in the higher the eye.
        var sun = f.Sun = f.Scene.Clock.SunDirection(f.Hour);
        long skyStep = Stopwatch.GetTimestamp();
        bool held = post.InstantAdaptation;
        UpdateWeather(ref f, held, ref skyStep);
        SetNightSky(ref f);
        (f.Colours, f.Light) = gpu.Sky.Prepare(sun, eye.Y, f.FogDistance + 3 * Math.Max(eye.Y, 0));
        SkyStep("sky tables", ref skyStep);

        // The game's exposure (0.55 over the mean luminance, clamped to its band) goes with the game's sky and light.
        post.AutoExposure = (gpu.Sky.MinLuminance, gpu.Sky.MaxLuminance);
        post.WaterHeight = render.Water && gpu.Water is not null ? WorldWater.Height : null;
        UpdateHeatHaze(gpu, post, sun.Y);

        // The Meitou water's waves on the game clock (the heat haze's, held still for a still picture), led by the wind at the camera.
        gpu.Water?.Animate(gpu.GameHours ?? gpu.HeatHazeHours, gpu.WeatherState.WindDirection, gpu.WeatherState.WindSpeed);
        gpu.Water?.Track(eye, gpu.Terrain.Snapshot());
        SkyStep("water", ref skyStep);

        // The clouds drift on the frame clock (game-speed seconds, 0 while paused), held still for a still picture.
        if (gpu.Weather is { } drift) gpu.Sky.StepClouds(drift.Times.Game);
        else gpu.Sky.StepClouds(held);
        gpu.Sky.PrepareCloudShadows();

        // Far enough that the haze is complete before the far plane and the water quad (1.5 × view distance wide) end,
        // so a high eye sees the sea fade into the sky instead of a cut-off edge.
        var camera = f.Camera;
        camera.ViewDistance = Math.Min(Math.Max(camera.MinViewDistance, f.Light.FogDistance / 0.7f), gpu.ViewCap ?? float.MaxValue);
    }

    /// <summary>
    /// The weather at the camera, before the sky's tables are prepared (the colour multiplier feeds them), and the wet surfaces it leaves.
    /// A held frame (a screenshot, <paramref name="held"/>) has dt 0; the first frame snaps.
    /// </summary>
    static void UpdateWeather(ref FrameState f, bool held, ref long skyStep)
    {
        var gpu = f.Gpu;
        if (gpu.Weather is { } weather)
        {
            float scheduleHour = f.WeatherHour ?? f.Hour;
            weather.Update(f.Eye, scheduleHour, f.Scene.Clock.SunDirection(scheduleHour).Y, held);
            SkyStep("weather update", ref skyStep);
            weather.Apply(gpu.Sky);
            SkyStep("weather apply", ref skyStep);
        }
        gpu.Surfaces?.Apply(gpu.WeatherState);
        SkyStep("surfaces", ref skyStep);
    }

    /// <summary>
    /// The night sky's clocks: the starfield shifts with the game hours since the load, the planets turn with the day and hour.
    /// </summary>
    static void SetNightSky(ref FrameState f)
    {
        var gpu = f.Gpu;
        var sky = gpu.Sky;
        sky.StarHours = gpu.GameHours ?? gpu.HeatHazeHours;
        sky.Day = gpu.Weather?.Day ?? 0;
        sky.Hour = f.Hour;
        sky.Clock = f.Scene.Clock;
        sky.StarSeconds = (float)(gpu.HeatHazeHours / HeatHaze.HoursPerSecond % 3600);   // the twinkle's clock: still for a picture
        sky.Eye = f.Eye;
    }

    /// <summary>
    /// The "particles" stage's update (its draw is <see cref="DrawParticles"/>): the weather's camera particles, the building effects once read, the
    /// effects on the caller's time (1/600 s units; the game's clock, so still while paused; constant, so still, in pictures), then the fog volumes
    /// in view.
    /// </summary>
    static void UpdateEffects(ref FrameState f)
    {
        var gpu = f.Gpu;
        var camera = f.Camera;

        UpdateParticles(gpu, camera);
        TakeBuildingEffects(gpu);
        if (gpu.Particles is { } updated)
        {
            (updated.WeatherParticles, updated.World.Density) = (gpu.WeatherParticles, gpu.ParticleDensity);
            updated.Update(f.Time * SecondsPerTimeUnit, camera);
        }

        // The placed fog volumes in view and the effects' fog spheres (after the particles moved), for every world shader (FogVolumes).
        if (gpu.FogVolumes is { } fogVolumes)
        {
            gpu.Particles?.CollectFogVolumes(gpu.EffectFogs);
            float aspect = f.RenderWidth / (float)Math.Max(f.RenderHeight, 1);
            fogVolumes.Update(f.Eye, camera.Forward, camera.FieldOfView, aspect, gpu.Sky.HazeDistance, f.Sun.Y, true, gpu.EffectFogs,
                gpu.Sky.FogCullDistance);
            gpu.Sky.CloudShadowScale = fogVolumes.CloudShadowScale;
        }
    }

    /// <summary>
    /// The buildings' particle effects, handed to the particles once their background read finishes.
    /// </summary>
    static void TakeBuildingEffects(WorldGpu gpu)
    {
        if (gpu.BuildingEffects is not { IsCompleted: true } effectsLoad) return;
        gpu.BuildingEffects = null;
        if (!effectsLoad.IsCompletedSuccessfully)
        {
            Console.WriteLine($"building effects not read: {effectsLoad.Exception?.InnerException?.Message}");
            return;
        }
        if (gpu.Particles is not { } particles) return;
        foreach (var byBuilding in effectsLoad.Result.GroupBy(e => e.PlacementId)) particles.SetBuildingEffects(byBuilding.Key, byBuilding.ToList());
        var (buildings, groups) = particles.BuildingEffectCount;
        Console.WriteLine($"particles {effectsLoad.Result.Count} building effects on {buildings} buildings in {groups} groups");
    }

    /// <summary>
    /// The "shadows" stage: the cascades end where the weather fog hides everything (or at the view cap), or where the placed fog block the eye is in
    /// hides the whole view; then the shadow maps.
    /// </summary>
    static void Shadows(ref FrameState f)
    {
        var gpu = f.Gpu;
        if (gpu.Shadow is not { } shadow) return;
        shadow.RangeCap = gpu.ViewCap is { } cap ? Math.Min(cap, gpu.FogVolumes?.AtmosphereDistance ?? cap) : gpu.FogVolumes?.AtmosphereDistance;
        shadow.SetFogHide(gpu.FogVolumes?.ShadowHideDistance, gpu.FogVolumes?.ShadowHidePending == true);
        shadow.Temporal = f.Post.Temporal;
        DrawShadows(gpu, f.Camera, f.Render, f.Light, f.RenderWidth, f.RenderHeight, gpu.Sky.ShadowSunHeight);
    }

    /// <summary>
    /// The "gi" stage's update: the lamps (the lanterns characters carry among them), then the global illumination's traced scene and probes.
    /// </summary>
    static void UpdateLighting(ref FrameState f)
    {
        var gpu = f.Gpu;
        if (gpu.Lamps is { } lamps)
        {
            lamps.Moving.Clear();
            gpu.Characters?.CollectLamps(lamps.Moving);   // the lanterns characters carry, where this update posed them
        }
        gpu.Lamps?.Update(f.Eye, gpu.Objects, WorldLights.DaylightFactor(f.Hour, f.Scene.Clock.Sunrise, f.Scene.Clock.Sunset));

        // The traced scene of the global illumination: its acceleration structures for this eye (docs/render-gi.md).
        if (gpu.Probes is { } probesActive) probesActive.Active = gpu.GiActive;
        var objects = f.Render.Objects ? gpu.Objects : null;
        if (gpu.Gi is { } gi && gpu.GiActive)
        {
            var heights = gpu.Terrain.Snapshot();
            gi.Update(f.Eye, heights, objects, gpu.Foliage is { Enabled: true } giFoliage ? giFoliage : null, gpu.Lamps);
            StageClock.Sub("gi scene");
            gpu.Probes?.Update(gi, f.Eye, heights, gpu.Lamps, gpu.FogVolumes, gpu.Objects?.GiInteriorFocus(f.Camera.Target));
            StageClock.Sub("gi blend");
        }
        if (gpu.ProbeGi is { } probeGi)
        {
            probeGi.Active = gpu.GiActive;
            if (gpu.GiActive)
            {
                probeGi.Update(f.Eye, gpu.Terrain.Snapshot(), objects, gpu.Lamps, gpu.FogVolumes);
                StageClock.Sub("gi metal");
            }
        }
    }

    /// <summary>
    /// The "blood" stage's ground part: the blood on the ground near the eye (docs/game/blood-decals.md), before any terrain draw reads it.
    /// </summary>
    static void RecordBlood(ref FrameState f)
    {
        var gpu = f.Gpu;
        if (gpu.Decals is { } decals && gpu.DecalStore is { } decalStore) decals.Record(decalStore, f.Eye);
        gpu.Terrain.RecordMaterialCache(f.Eye);
        gpu.Terrain.Decals = gpu.DecalStore is null ? null : gpu.Decals;
    }

    #endregion

    #region Reflection and sky

    /// <summary>
    /// The "reflection" stage: the mirrored scene into the water reflection's own framebuffer (it restores the bound one), before the main pass;
    /// then the water's FFT, before the scene's render pass (Metal records compute in the same command buffer).
    /// </summary>
    static void DrawReflection(ref FrameState f)
    {
        var gpu = f.Gpu;
        var render = f.Render;
        var light = f.Light;

        // Level 0: no pass, the water shows the sky colour.
        bool reflecting = f.Reflecting = render.Water && render.Reflections && gpu.Water is not null && gpu.Reflection is { Level: > 0 };
        if (gpu.Reflection is { } setup)
        {
            setup.FaithfulShadows = gpu.Shadow is { Meitou: false };
            setup.MaxDistance = gpu.Sky.HazeDistance * setup.Range;
            setup.HideDistance = gpu.FogVolumes?.AtmosphereDistance;
            setup.WaterDistance = gpu.Sky.HazeCompleteDistance;
            setup.FogVolumes = gpu.Water is { Meitou: true } && gpu.FogVolumes is { UsedData: > 0 };
        }

        // The mirrored scene keeps the flat ambient: the probes' light is not worth a second view's reads in a blurred reflection
        // (docs/render-gi.md).
        if (gpu.Probes is { } reflectedProbes) reflectedProbes.InReflection = reflecting && !reflectedProbes.Reflected;
        if (reflecting)
        {
            bool anything = gpu.Objects is not null || gpu.Characters is not null || gpu.Foliage is not null;
            gpu.Reflection!.Render(f.Camera, f.RenderWidth, f.RenderHeight, gpu.Sky, f.Colours, light, gpu.Terrain, render,
                anything ? (vp, e, frustum) => DrawReflectedScene(gpu, render, light, vp, e, frustum) : null);
        }
        if (gpu.Probes is not null) gpu.Probes.InReflection = false;

        // What the reflection's rect learnt for the water pass: the nearest water in view, or NaN when no pass ran to find out.
        if (gpu.Water is { } gated) gated.NearestWater = reflecting ? gpu.Reflection!.NearestWater : float.NaN;
        gpu.Water?.PrepareFrame();
    }

    /// <summary>
    /// The reflection's scene by its level (docs/formats/settings.md): 2 characters, 3 buildings (and the map features, which are not told apart),
    /// 4 everything else (foliage). Characters still reflect when the debug object switch is off.
    /// </summary>
    static void DrawReflectedScene(WorldGpu gpu, WorldRenderOptions render, WorldLighting light, Matrix4x4 vp, Vector3 e, Vector4[] frustum)
    {
        var reflection = gpu.Reflection!;
        if (reflection.Level >= 3 && render.Objects && gpu.Objects is { } objects)
        {
            float distance = objects.ObjectDistance, landmarks = objects.LandmarkDistance;
            float bias = objects.LodBias;
            objects.LodBias = bias * reflection.ObjectLodBias;
            objects.ObjectDistance = Math.Min(distance, reflection.ObjectReach);
            objects.LandmarkDistance = Math.Min(landmarks,
                reflection.ObjectReach);   // the mirrored scene has no landmarks beyond the reflection's own object range
            objects.MirrorCull = reflection.CullToWater ? reflection.MayShow : null;
            objects.AllFloors = true;   // the game shows every floor in the water reflection (docs/formats/zones.md "Interior view")

            objects.Draw(vp, e, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain);

            objects.AllFloors = false;
            objects.MirrorCull = null;
            reflection.Lap(2);
            StageClock.Sub("refl up to objects");
            objects.ObjectDistance = distance;
            objects.LandmarkDistance = landmarks;
            objects.LodBias = bias;
        }

        if (reflection.Level >= 4 && render.Objects)
        {
            gpu.Foliage?.Draw(vp, e, frustum, render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain, grass: false,
                    maxRange: reflection.FoliageReach);
        }

        reflection.Lap(3);
        StageClock.Sub("refl foliage");

        // Level 2 is the game's characters layer. They are drawn after the opaque objects and foliage as in the
        // main view, and without the main view's fog cull (the reflection's own range/HideDistance handles that).
        if (reflection.Level >= 2 && gpu.Characters is { } characters)
        {
            characters.FogCull = null;
            characters.InteriorObjects = null;
            characters.Draw(vp, e, frustum, light.SunDirection, light.FogColour, light.FogDistance);
            reflection.Lap(2);
            StageClock.Sub("refl characters");
        }

        var reflectedObjects = reflection.Level >= 3 && render.Objects
            ? $"{gpu.Objects?.DrawnInstances ?? 0} objects ({gpu.Objects?.DrawnTriangles ?? 0:N0} triangles, {gpu.Objects?.DrawCalls ?? 0} calls)"
            : "no objects (level < 3)";
        var reflectedFoliage = reflection.Level >= 4 && render.Objects
            ? $"{gpu.Foliage?.DrawnInstances ?? 0} foliage meshes ({gpu.Foliage?.DrawCalls ?? 0} calls)" : "";
        var reflectedPeople = reflection.Level >= 2 ? $"{gpu.Characters?.DrawnCharacters ?? 0} characters" : "no characters (level < 2)";
        reflection.SceneStats = $"{reflectedPeople}, {reflectedObjects}{(reflectedFoliage.Length > 0 ? $", {reflectedFoliage}" : "")}";
    }

    /// <summary>
    /// The "sky-draw" stage: the scene's pass host opened on its targets, cleared to the fog colour and the scene depth's clear (the rendering's load
    /// ops, <see cref="DepthConvention.Clear"/>), the interior masks, and the sky with its clouds.
    /// </summary>
    static void DrawSky(ref FrameState f)
    {
        var gpu = f.Gpu;
        var post = f.Post;
        var camera = f.Camera;

        // The scene's passes are a native host that hands its guests the targets and state (phase 8 stage 3); wave 4 (docs/renderer-native.md 6):
        // its rendering takes secondaries, the guests' segments recorded on the job threads when it ends.
        var host = f.Host = gpu.Scene ??= new SceneHost(post.Context);
        float aspect = f.Aspect = f.Width / (float)Math.Max(f.Height, 1);
        var view = f.View = camera.View;

        // Rotation only: with the eye's world position in the matrix, the directions rebuilt from it lose float
        // precision far from the origin and the sky blurs.
        var rotation = f.Rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        var skyProjection = rotation * Jitter.Apply(camera.Projection(aspect, 1, 1000), f.Jitter, f.RenderWidth, f.RenderHeight);

        // The fog volume pass runs over this view's sky exactly when volumes are in view (RunFogVolumes below), so the march may leave out what they
        // hide. With the placed fog block hiding the sky in every direction of the view, neither the sky nor its clouds can show: the clear colour
        // stands in.
        bool skyHidden = gpu.Sky.Clouds.SkyFogSkip && gpu.FogVolumes is { SkyHidden: true };
        if (skyHidden) gpu.Sky.Clouds.SkipMainSky();
        else gpu.Sky.PrepareClouds(skyProjection, f.RenderWidth, f.RenderHeight, gpu.Sky.Eye, fogVolumes: gpu.FogVolumes is { UsedData: > 0 });

        RecordInteriorMask(ref f);

        // Alpha 0: no character (the SSAO mask).
        var fog = f.Light.FogColour;
        host.Open(5, post.WithShadingRate(post.SceneTargets), new ClearColour(fog.X, fog.Y, fog.Z, 0), clearDepth: true);
        if (!skyHidden) gpu.Sky.Draw(skyProjection, f.Colours);
    }

    #endregion

    #region Scene

    /// <summary>
    /// The interior masks with the scene's camera, before the scene's passes (the terrain reads them).
    /// </summary>
    static void RecordInteriorMask(ref FrameState f)
    {
        var gpu = f.Gpu;
        var post = f.Post;
        var camera = f.Camera;
        if (f.Render.Objects && gpu.Objects is { } objects)
        {
            gpu.InteriorMask ??= new InteriorMaskRenderer(post.Context, objects);
            var projection = Jitter.Apply(camera.Projection(f.Aspect, camera.ClipNear, camera.ViewDistance), f.Jitter, f.RenderWidth, f.RenderHeight);
            gpu.InteriorMask.Record(f.View * projection, f.Eye, post.RenderWidth, post.RenderHeight);
            objects.Masks = gpu.InteriorMask;
        }
        else gpu.InteriorMask?.Record(default, f.Eye, 0, 0);
    }

    /// <summary>
    /// The scene's camera: its range, and its projections (plain, jittered by the upscaler, with the view).
    /// </summary>
    readonly record struct SceneView(float Near, float Far, Matrix4x4 Projection, Matrix4x4 Jittered, Matrix4x4 ViewProjection, Vector4[] Frustum);

    /// <summary>
    /// The scene from the near plane to the view distance over one depth (<see cref="DepthConvention"/>): terrain, objects, foliage, the terrain's
    /// late colour and the debris, the surface blood, the characters, the water; each its own stage. The jobs recorded and run at the end.
    /// </summary>
    static void DrawScene(ref FrameState f)
    {
        var s = BeginScene(ref f);
        var gpu = f.Gpu;

        // The blood on walls, buildings and objects (the bloodsurfaces switch) needs the depths the terrain and then the static scene wrote, to leave
        // the terrain's pixels to its own blood.
        bool surfaceBlood = gpu.Decals is { Surfaces: true, SurfaceCount: > 0 } bloodDecals
            && gpu.DecalStore is not null && bloodDecals.SurfacesNear(f.Eye);

        DrawSceneTerrain(ref f, s);
        if (surfaceBlood) CaptureDepth(ref f, 7, gpu.Decals!.CaptureTerrainDepth);
        DrawSceneObjects(ref f, s);
        if (surfaceBlood) CaptureDepth(ref f, 8, gpu.Decals!.CaptureStaticDepth);
        DrawSceneFoliage(ref f, s);
        DrawSceneLate(ref f, s);
        if (surfaceBlood) DrawSurfaceBlood(ref f, s);
        DrawSceneCharacters(ref f, s);
        DrawWater(ref f, s.Near, s.Far, s.ViewProjection);
        KeepParticleViews(ref f, s);
        CloseScene(f.Host);
    }

    /// <summary>
    /// The scene's projection to the post chain (after its camera: the reprojection needs both) and its views, with the motion vectors attached. The
    /// host <see cref="DrawSky"/> opened stays open: one depth, no second pass.
    /// </summary>
    static SceneView BeginScene(ref FrameState f)
    {
        var post = f.Post;
        var camera = f.Camera;
        float near = camera.ClipNear, far = camera.ViewDistance;
        post.SetCamera(f.Eye, f.View, camera.FieldOfView, f.Aspect);
        post.BeginScene(near, far, camera.FieldOfView, f.Aspect);
        f.Gpu.Terrain.BeginFrame();

        var projection = camera.Projection(f.Aspect, near, far);
        var jittered = Jitter.Apply(projection, f.Jitter, f.RenderWidth, f.RenderHeight);
        var viewProjection = f.View * jittered;
        var s = new SceneView(near, far, projection, jittered, viewProjection, WorldCamera.FrustumPlanes(viewProjection));
        AttachMotion(ref f, s.ViewProjection, f.View * projection, s.Frustum, f.Rotation * jittered, f.Rotation * projection);
        return s;
    }

    /// <summary>
    /// Records the scene's jobs and executes them: the render thread's share (the fork-join) counts as "water", the last stage of the host.
    /// </summary>
    static void CloseScene(SceneHost host)
    {
        if (!host.IsOpen) return;
        host.Close();
        StageClock.Lap(9);
    }

    /// <summary>
    /// Closes the pass for <paramref name="capture"/> of the scene depth so far, and opens it again counting towards <paramref name="stage"/>.
    /// </summary>
    static void CaptureDepth(ref FrameState f, int stage, Action<GpuTexture> capture)
    {
        f.Host.Close();
        capture(f.Post.SceneDepth);
        f.Host.Open(stage, f.Post.WithShadingRate(f.Post.SceneTargets));
    }

    /// <summary>
    /// The terrain; its colour comes after the foliage (<see cref="DrawSceneLate"/>) when late colour is on.
    /// </summary>
    static void DrawSceneTerrain(ref FrameState f, in SceneView s)
    {
        var terrain = f.Gpu.Terrain;
        f.Host.Stage(6);
        terrain.FogCull = f.Gpu.FogVolumes;
        terrain.InteriorCut = f.Gpu.InteriorMask;
        terrain.Draw(s.ViewProjection, f.Eye, s.Frustum, f.Render, f.Light, defer: TerrainLateColour.Enabled);
        terrain.InteriorCut = null;
        terrain.FogCull = null;
        StageClock.Lap(6);
    }

    /// <summary>
    /// The objects, culled by the fog and the interior masks.
    /// </summary>
    static void DrawSceneObjects(ref FrameState f, in SceneView s)
    {
        var gpu = f.Gpu;
        var light = f.Light;
        f.Host.Stage(7);
        if (gpu.Objects is { } objects)
        {
            objects.FogCull = gpu.FogVolumes;
            objects.InteriorCut = gpu.InteriorMask;
            objects.Selected = gpu.HoverOutline is null ? [] : gpu.OutlinedBuildings;   // their draws kept for the outline's masks
            if (f.Render.Objects)
                objects.Draw(s.ViewProjection, f.Eye, s.Frustum, f.Render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain);
            objects.FogCull = null;
            objects.InteriorCut = null;
            objects.Selected = [];
        }
        StageClock.Lap(7);
    }

    /// <summary>
    /// The foliage (it reaches 32000+ units at the default x4).
    /// </summary>
    static void DrawSceneFoliage(ref FrameState f, in SceneView s)
    {
        var gpu = f.Gpu;
        var light = f.Light;
        f.Host.Stage(8);
        if (gpu.Foliage is { } foliage)
        {
            foliage.FogCull = gpu.FogVolumes;
            foliage.Occlusion = f.Post.OcclusionFor(f.Eye);
            foliage.InteriorCut = gpu.InteriorMask;
            foliage.PrepassView = true;
            foliage.Draw(s.ViewProjection, f.Eye, s.Frustum, f.Render, light.SunDirection, light.FogColour, light.FogDistance, gpu.Terrain);
            foliage.PrepassView = false;
            foliage.FogCull = null;
            foliage.Occlusion = default;
            foliage.InteriorCut = null;
        }
        StageClock.Lap(8);
    }

    /// <summary>
    /// The terrain colour where nothing nearer covers it (the depth is in from the draw above), and the debris and stuck bolts.
    /// </summary>
    static void DrawSceneLate(ref FrameState f, in SceneView s)
    {
        var gpu = f.Gpu;
        f.Host.Stage(6);
        gpu.Terrain.DrawDeferred();
        if (gpu.Debris is { } debris)
        {
            debris.Draw(CollectionsMarshal.AsSpan(gpu.DebrisPieces), s.ViewProjection, f.Eye, s.Frustum, f.Light,
                look => gpu.Characters is { } chars && chars.SkinOn(look.Race, look.SkinDefault) ? chars.SkinTable : 0);
            if (gpu.BirdPieces.Count > 0)
                debris.Draw(CollectionsMarshal.AsSpan(gpu.BirdPieces), s.ViewProjection, f.Eye, s.Frustum, f.Light);
            if (gpu.BoltPieces.Count > 0)
            {
                debris.Draw(CollectionsMarshal.AsSpan(gpu.BoltPieces), s.ViewProjection, f.Eye, s.Frustum, f.Light,
                    look => gpu.Characters is { } chars && chars.SkinOn(look.Race, look.SkinDefault) ? chars.SkinTable : 0);
            }
        }
        StageClock.Lap(6);
    }

    /// <summary>
    /// The blood on walls, buildings and objects (the bloodsurfaces switch): over the finished opaque scene, under the characters and the water.
    /// </summary>
    static void DrawSurfaceBlood(ref FrameState f, in SceneView s)
    {
        var post = f.Post;
        f.Host.Close();
        float tanY = MathF.Tan(f.Camera.FieldOfView * 0.5f);
        var jitterClip = new Vector2(2 * f.Jitter.X / f.RenderWidth, 2 * f.Jitter.Y / f.RenderHeight);
        f.Gpu.Decals!.RecordSurfaces(post.SceneColour, post.SceneDepth, s.Near, s.Far, f.Rotation, tanY * f.Aspect, tanY, f.Eye, jitterClip);
        f.Host.Open(6, post.WithShadingRate(post.SceneTargets));
        StageClock.Lap(StageClock.Blood, "blood surfaces");
    }

    /// <summary>
    /// Characters after the opaque geometry: they alone write the scene's alpha (SSAO's character mask), so nothing drawn later may cover them
    /// but water. Their view-projection is the rotation-only one, for positions relative to the eye.
    /// </summary>
    static void DrawSceneCharacters(ref FrameState f, in SceneView s)
    {
        var gpu = f.Gpu;
        var light = f.Light;
        if (gpu.Characters is { } fogCharacters)
        {
            fogCharacters.FogCull = gpu.FogVolumes;
            fogCharacters.InteriorObjects = gpu.Objects;
        }

        f.Post.CharactersDrawn = false;   // none this frame unless some are drawn below
        if (gpu.Characters is not { } characters) return;
        f.Host.Stage(StageClock.Characters);
        characters.Draw(s.ViewProjection, f.Eye, s.Frustum, light.SunDirection, light.FogColour, light.FogDistance, f.Rotation * s.Jittered);
        f.Post.CharactersDrawn = characters.DrawnCharacters > 0;
        StageClock.Lap(StageClock.Characters);   // the colour draw; the motion draw (post chain, upscalers only) laps itself in
    }

    /// <summary>
    /// The scene's views for the weather particles, which come last, after the fog volumes (FinishScene): the game draws them in queue 84, the
    /// volumes in 82. The unjittered pair is for low-resolution particles blended after the upscaler (its output is unjittered).
    /// </summary>
    static void KeepParticleViews(ref FrameState f, in SceneView s)
    {
        // The close particles are those nearer than the camera's near rule (WorldCamera.Near), not the scene's clip plane (ClipNear).
        float near = f.Camera.Near;
        float close = Math.Min(0.5f, near * 0.5f);
        var closeProjection = f.Camera.Projection(f.Aspect, close, near);
        f.ParticleView = s.ViewProjection;
        f.ParticleClose = f.View * Jitter.Apply(closeProjection, f.Jitter, f.RenderWidth, f.RenderHeight);
        f.ParticleLow = f.View * s.Projection;
        f.ParticleLowClose = f.View * closeProjection;
        f.ParticleNear = near;
    }

    #endregion

    #region Water and picture

    /// <summary>
    /// The scene's motion vectors: the grass's sway, and the characters' with their view-projection for positions relative to the eye (the rotation
    /// only, exact: the world matrix, with the eye's world position in its translation, has lost the digits far from the origin:
    /// CharacterRenderer.DrawView).
    /// </summary>
    static void AttachMotion(ref FrameState f, Matrix4x4 viewProjection, Matrix4x4 unjittered, Vector4[] frustum, Matrix4x4 relative,
        Matrix4x4 relativeUnjittered)
    {
        var gpu = f.Gpu;
        var post = f.Post;
        if (post.Temporal && gpu.Foliage is { } swaying)
        {
            post.ObjectMotion ??= swaying.DrawGrassMotion;
            swaying.SetMotionCamera(viewProjection, unjittered, f.Eye, frustum);
        }
        gpu.Characters?.AttachMotion(post, gpu.Foliage, viewProjection, unjittered, f.Eye, frustum, relative, relativeUnjittered);
    }

    /// <summary>
    /// The "water" stage: transparent water refracts the scene and measures its depth through it (water that needs neither takes no copy or pass
    /// restart), then the water itself.
    /// </summary>
    static void DrawWater(ref FrameState f, float near, float far, Matrix4x4 viewProjection)
    {
        var gpu = f.Gpu;
        var post = f.Post;
        var host = f.Host;
        bool water = f.Render.Water;
        if (water && gpu.Water is { } capturing && capturing.NeedsCapture(near))
        {
            host.Close();
            capturing.CaptureScene(post.SceneColour, post.SceneDepth, near, far);
            host.Open(9, post.WithShadingRate(post.SceneTargets));
        }

        host.Stage(9);
        if (water && gpu.Water is { } drawn)
        {
            drawn.Overcast = Overcast(gpu);
            drawn.Draw(viewProjection, f.Eye, f.Light, f.Colours, f.Time, f.Camera.ViewDistance * 1.5f, f.Reflecting ? gpu.Reflection : null);
        }
        StageClock.Lap(9);
    }

    /// <summary>
    /// After the scene's passes, over the finished scene: the GI resolve, the Meitou light shafts (docs/render-shafts.md: the haze darkened where the
    /// sun is shadowed along the view, before the fog volumes blend over it), the placed fog volumes, then the SSAO.
    /// </summary>
    static void FinishScene(ref FrameState f)
    {
        var gpu = f.Gpu;
        var post = f.Post;
        var camera = f.Camera;

        post.RunGiResolve();
        StageClock.Lap(14,
            "gi resolve");   // the resolve is the gi stage's (its time was the particles stage's before, with the shafts and the fog volumes)

        if (gpu.Shadow is { Enabled: true })
        {
            float darkening = ShaftDarkening(post.Options, f.Light, f.Sun.Y) * ShaftCloudFade(Overcast(gpu));
            float distance = Math.Min(camera.ViewDistance, gpu.Sky.HazeCompleteDistance ?? gpu.Sky.HazeDistance);
            post.RunLightShafts(darkening, distance, f.Floor, ShaftAirByTime(post.Options, f.Scene.Clock, f.Hour));
            StageClock.Lap(StageClock.Shafts);
        }
        if (gpu.DebugShadows >= 2 && gpu.Shadow is not null)
            gpu.Shadow.CaptureDepth(post.SceneDepth, f.RenderWidth, f.RenderHeight);

        // The placed fog volumes over the finished scene (opaque, water, sky; the haze is in the shaders), one pass reading the depth, as the game's
        // queue 82 does.
        post.RunFogVolumes(gpu.FogVolumes is { UsedData: > 0 });
        post.BuildShadingRate(f.Eye);   // the next frame's fog shading rate (fog-vrs), from this frame's depth
        StageClock.Lap(StageClock.Fog, "fog volumes");

        // The SSAO onto the scene before the particles: they are not occluded by the surfaces behind them (the post chain leaves it out of its
        // composite).
        post.ApplySsao();
    }

    /// <summary>
    /// The "particles" stage's draw: blended over the scene and tested against its depth (they are not fogged; docs/formats/fogfeatures.md);
    /// in Meitou the big alpha and additive sprites into low-resolution targets (half, then quarter size), each over the scene with a depth-aware
    /// upsample.
    /// </summary>
    static void DrawParticles(ref FrameState f)
    {
        var gpu = f.Gpu;
        var post = f.Post;
        var host = f.Host;
        if (gpu.Particles is not { } particles || f.ParticleNear <= 0) return;

        particles.RangeFade = particles.Lit = post.Options.MeitouFog;
        particles.SunShade = ShaftDarkening(post.Options, f.Light, f.Sun.Y) * ShaftCloudFade(Overcast(gpu));
        float pixelsPerUnit = post.ParticlePixelsPerUnit(f.Camera.Projection(f.Aspect, 1, 10).M22);
        bool late = post.ParticlesAfterUpscale;
        particles.Prepare(f.ParticleView, f.ParticleClose, f.ParticleNear, f.View, f.Eye, f.Sun, pixelsPerUnit,
            (float)post.RenderWidth * post.RenderHeight,
            post.Options.ParticleDivisor, lowViewProjection: late ? f.ParticleLow : null, lowCloseViewProjection: late ? f.ParticleLowClose : null);

        if (particles.FullCount > 0)
        {
            post.PrepareSoftParticleDepth();
            host.Open(13, post.SceneTargetsForParticles);
            particles.Record(1, post.SoftParticleDepth(1));
            host.Close();
        }

        if (post.LowResParticles)
        {
            foreach (int divisor in (ReadOnlySpan<int>)[2, 4])
            {
                if (particles.CountFor(divisor) == 0) continue;
                post.BeginParticlesLow(divisor);
                host.Open(13, post.ParticleTargets(divisor));
                particles.Record(divisor, post.SoftParticleDepth(divisor));
                host.Close();

                // Again against each block's farthest depth, where the chain keeps both (the depth-aware upsample takes each pixel's own surface from
                // either).
                if (post.ParticleTargetsFar(divisor) is { } farTargets)
                {
                    host.Open(13, farTargets);
                    particles.Record(divisor, post.SoftParticleDepthFar(divisor));
                    host.Close();
                }
                post.MarkParticleDraws();
                post.CompositeParticlesLow(divisor);
            }
        }

        StageClock.Lap(13);
    }

    /// <summary>
    /// The "post" stage: the post chain's end (SSAO, upscaler, exposure, tone map, FXAA into the chain's target), then what goes over the finished
    /// picture, sorted against the scene's depth: the movement marker and stealth arrows, the navmesh view, the hover outline, the shadow and GI
    /// debug views.
    /// </summary>
    static void FinishPicture(ref FrameState f)
    {
        var gpu = f.Gpu;
        var post = f.Post;
        var camera = f.Camera;
        var eye = f.Eye;
        int width = f.Width, height = f.Height;

        post.MarkParticles();
        post.Particles = gpu.Particles;
        post.SetCloudMotion(gpu.Sky.Clouds.CloudMotion);
        post.End();

        // The scene's projection, asked only by an overlay that draws.
        float aspect = f.Aspect;
        var view = f.View;
        (float Near, float Far, Matrix4x4 Projection) SceneProjection() =>
            (camera.ClipNear, camera.ViewDistance, camera.Projection(aspect, camera.ClipNear, camera.ViewDistance));

        // The movement marker and stealth arrows, in their materials' colours, like the game's debug_marker.
        if (gpu.Markers is { Active: true } marker && f.ParticleNear > 0 && post.Target is { } picture)
        {
            marker.Record(picture, width, height, post.SceneDepth, post.RenderWidth, post.RenderHeight, view * SceneProjection().Projection, camera,
                    f.Sun);
        }
        else gpu.Markers?.Idle();

        // The navmesh debug view (Meitou only).
        if (gpu.NavmeshOverlay is { Visible: true } navOverlay && post.Target is { } navPicture)
        {
            navOverlay.Record(navPicture, width, height, post.SceneDepth, post.RenderWidth, post.RenderHeight, view * SceneProjection().Projection,
                    eye);
        }

        // The pick & collision inspector's hulls over the finished picture (Meitou only).
        if (gpu.InspectorOverlay is { Visible: true } debugOverlay && post.Target is { } debugPicture)
        {
            debugOverlay.Record(debugPicture, width, height, post.SceneDepth, post.RenderWidth, post.RenderHeight,
                    view * SceneProjection().Projection, eye);
        }

        // The hover outline (Meitou only): the characters' masks drawn after the portraits, the outlined buildings' from what the scene drew of them.
        if (gpu.HoverOutline is { } outline && post.Target is { } hoverPicture)
        {
            gpu.Objects?.DrawSelectionMasks(gpu.OutlinedBuildings, width, height, view, camera.FieldOfView, eye, f.Render);
            if (OutlineMasks(gpu) is { Count: > 0 } masks)
            {
                var (near, far, projection) = SceneProjection();
                outline.Record(hoverPicture, width, height, masks, post.SceneDepth, post.RenderWidth, post.RenderHeight, near, far,
                    view * projection);
            }
        }

        if (gpu.DebugShadows > 0 && gpu.Shadow is not null)
            gpu.Shadow.DrawDebug(gpu.DebugShadows, post.Target!, width, height, view, SceneProjection().Projection, eye);

        if (gpu.GiDebug > 0 && gpu.GiActive && gpu.Gi is { } traced && gpu.GiDebugPass is { } giDebug && post.Target is { } giTarget)
        {
            // The scene depth back to positions relative to the eye: the rotation-only view and the scene's projection, inverted.
            Matrix4x4.Invert(f.Rotation * SceneProjection().Projection, out var inverse);
            giDebug.Run(gpu.GiDebug, traced, post.SceneDepth, giTarget, post.RenderWidth, post.RenderHeight, inverse, eye, f.Light);
        }
    }

    /// <summary>
    /// The hover outline's masks: the characters', then the outlined buildings' (a list made only when both have some).
    /// </summary>
    static IReadOnlyList<HoverMask> OutlineMasks(WorldGpu gpu)
    {
        IReadOnlyList<HoverMask> characters = gpu.Characters?.HoverMasks ?? [], building = gpu.Objects?.SelectionMasks ?? [];
        if (building.Count == 0) return characters;
        return characters.Count == 0 ? building : [.. characters, .. building];
    }

    #endregion
}