using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Water;
using GpuTexture = Meitou.Game.Rendering.Gpu.GpuTexture;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// Planar reflection of the scene for the water (docs/formats/terrain.md, "Water"; the game does the same with a 512Â²
/// render target drawn by a mirrored camera). The sky, terrain and, through <see cref="SceneDraw"/>, characters, objects
/// and foliage are drawn mirrored about the water plane into a texture of the picture's size; <see cref="WaterRenderer"/> samples it
/// with the same view-projection, so no flip of the texture is needed. The mirror reverses triangle winding, which a
/// mirrored (negated) clip X puts right again, so no renderer has to change its culling. Nothing below the water leaks
/// into the picture: the projection's near plane is replaced by the water plane (oblique near-plane clipping).
/// </summary>
public sealed class ReflectionPass : IDisposable
{
    #region Settings

    /// <summary>
    /// Draws the scene's other geometry (characters, objects, foliage) with the given matrix, eye and frustum planes.
    /// </summary>
    public delegate void SceneDraw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum);

    /// <summary>
    /// Size of the texture relative to the picture (the game's is a fixed 512Â²). Full size: at a grazing angle the mirror image is seen about as large as the scene itself.
    /// </summary>
    public const float Scale = 1f;

    /// <summary>
    /// The reflection covers this much more than the picture in each direction (as a factor of the field of view tangent).
    /// </summary>
    public const float Margin = 1.12f;

    /// <summary>
    /// Samples per texel of the reflection (1 = off). Resolved into the plain texture the water samples.
    /// </summary>
    public const int DefaultSamples = 4;

    /// <summary>
    /// Samples per texel of the reflection in use (1 = off; a change rebuilds the targets). Default <see cref="DefaultSamples"/>;
    /// <c>--reflection-samples</c>.
    /// </summary>
    public int Samples { get; set; } = DefaultSamples;

    /// <summary>
    /// Side B of the reflection MSAA A/B (1).
    /// </summary>
    public int AbSamples => 1;

    /// <summary>
    /// Meitou: the mirrored scene is drawn without the sun shadows (the receivers see a lit surface: the frame's shadow block is swapped for the
    /// shadows-off one while the pass records; <c>--ab refl-shadows</c> flips it). A half-resolution, rippled mirror image hides the shadows' detail,
    /// and the receiver is a good part of the pass's shading. <see cref="FaithfulShadows"/> keeps them.
    /// </summary>
    public bool NoShadows { get; set; } = true;

    /// <summary>
    /// Set by the frame loop while the shadows switch is Faithful: the reflection then keeps the shadows as before (what the game does is Unknown).
    /// </summary>
    public bool FaithfulShadows { get; set; }
    FrameBlock? _unshadowed;

    /// <summary>
    /// The native GPU API (all native since phase 8 stage 3: the pass is the guests' host, docs/renderer-native.md 8.9).
    /// </summary>
    public IGpuContext Gpu { get; }
    readonly PassTimer _timer;
    readonly WorldRenderOptions _options = new();
    // The texture the water samples, and the depth beside it when the scene is drawn without multisampling; else the multisampled twins the
    // scene is drawn into, resolved into the colour.
    GpuTexture? _colour, _depth, _msColour, _msDepth;
    int _samples, _builtFor;   // builtFor: the Samples the targets were made for
    int _width, _height, _skipped, _age;
    bool _hasImage;
    Vector3 _lastEye;
    float _lastFov;
    Matrix4x4 _lastView;
    readonly List<double> _gpuSamples = [];
    readonly List<double> _cpuSamples = [];

    public ReflectionPass(IGpuContext gpu)
    {
        Gpu = gpu;
        _timer = new PassTimer(gpu);
    }

    /// <summary>
    /// The game's <c>water reflection</c> when the key is missing, and <c>reflection range</c> (docs/formats/settings.md).
    /// </summary>
    public const int DefaultLevel = 2;
    public const float DefaultRange = 0.6f;

    /// <summary>
    /// The game's <c>water reflection</c> 0..4: what the mirrored scene holds. 0 no pass (the water shows the sky colour, the viewer's
    /// counterpart of the shader's NO_REFLECTION branch), 1 sky and terrain, 2 adds the characters, 3 adds buildings (here every
    /// placed object), 4 everything (also foliage). The frame loop reads it for what to draw.
    /// </summary>
    public int Level { get; set; } = DefaultLevel;

    /// <summary>
    /// The game's <c>reflection range</c>: the far clip is the haze distance (the game's view distance Ã— 10) times this; the frame loop sets <see
    /// cref="MaxDistance"/> from it.
    /// </summary>
    public float Range { get; set; } = DefaultRange;

    /// <summary>
    /// Furthest distance reflected: mountains beyond it are not mirrored (the sea there is mostly haze anyway).
    /// </summary>
    public float MaxDistance { get; set; } = 150000;

    /// <summary>
    /// Objects (buildings, features) further than this from the eye are left out of the reflection.
    /// </summary>
    public float ObjectDistance { get; set; } = 2000;

    /// <summary>
    /// Beyond this distance the mirrored terrain takes the cheap ground colour instead of the biome textures.
    /// </summary>
    public float MaterialDistance { get; set; } = 6000;

    /// <summary>
    /// Foliage (trees, bushes, rocks; no grass) further than this is left out of the reflection (the game's layers reach 4 times as far in the
    /// picture).
    /// </summary>
    public float FoliageDistance { get; set; } = 2200;

    /// <summary>
    /// Multiplies the distance the objects' LOD level is chosen by (above 1: coarser levels sooner; the half-resolution, rippled image cannot show
    /// the detail).
    /// </summary>
    public float ObjectLodBias { get; set; } = 3;

    /// <summary>
    /// Frames a finished reflection may be reused when the camera has hardly moved (0 = draw every frame).
    /// </summary>
    public int MaxAge { get; set; } = 3;

    /// <summary>
    /// Multiplies the terrain LOD's pixel scale for the mirrored terrain (its target is half the picture's size, so 0.5 measures the error in its own
    /// pixels; below: coarser).
    /// </summary>
    public float TerrainLodScale { get; set; } = 0.5f;

    /// <summary>
    /// Meitou: the reflection leaves out what cannot be seen through any water (<see cref="MayShow"/>), what is under <see cref="MinTexels"/>,
    /// and keeps the shorter <see cref="ObjectDistance"/> and <see cref="FoliageDistance"/>; with <see cref="CropToWater"/> it is the whole of
    /// the cheaper reflection (<c>--ab refl-cull</c> flips it). Off: the reach the pass had before (3000 units).
    /// </summary>
    public bool CullToWater { get; set; } = true;

    /// <summary>
    /// The object range in use: <see cref="ObjectDistance"/>, or with <see cref="CullToWater"/> off at least the old 3000.
    /// </summary>
    public float ObjectReach => CullToWater ? ObjectDistance : Math.Max(ObjectDistance, 3000);

    /// <summary>
    /// The foliage range in use, as <see cref="ObjectReach"/>.
    /// </summary>
    public float FoliageReach => CullToWater ? FoliageDistance : Math.Max(FoliageDistance, 3000);

    /// <summary>
    /// Meitou: draw only the part of the reflection the water on the screen looks up (<see cref="WaterRect"/>), and nothing with no water in view.
    /// </summary>
    public bool CropToWater { get; set; } = true;

    /// <summary>
    /// The host's loading warm-up: every frame draws the whole mirrored view, water in view or not (no crop, no cull, no reuse), so that the
    /// pipelines it draws with are made under the loading cover. Made the first time water came into view, they held that frame 2-4 s with a cold
    /// driver cache (the first run after an install, an update or a new driver; docs/bench.md "Play benchmark").
    /// </summary>
    public bool Warm { get; set; }

    /// <summary>
    /// Extra ground (units) around a mirrored thing's footprint tested for water: the waves' ripples shift the lookup, and the terrain grid is
    /// coarse.
    /// </summary>
    public float WaterPad { get; set; } = 60;

    /// <summary>
    /// The distance from the eye beyond which the weather's fog hides every surface this frame (<see cref="FogVolumes.AtmosphereDistance"/>), set by
    /// the frame loop; null: none.
    /// </summary>
    public float? HideDistance { get; set; }

    /// <summary>
    /// Meitou: the distance beyond which the haze has stopped growing (<see cref="SkyRenderer.HazeCompleteDistance"/>), set by the frame loop; null:
    /// no cap. Water past it shows the haze whatever it reflects, so <see cref="WaterRect"/> leaves it out: the sea past the map's edge no longer
    /// keeps the pass alive at the Hub (docs/render-water.md).
    /// </summary>
    public float? WaterDistance { get; set; }

    /// <summary>
    /// The distance to the nearest water the last <see cref="WaterRect"/> found: positive infinity when it found none within the distances it tests,
    /// NaN when it did not run (the reflection off or under the water, <see cref="CropToWater"/> off), so the water pass gates nothing on it.
    /// </summary>
    public float NearestWater { get; private set; } = float.NaN;

    /// <summary>
    /// The last run found no water in view, at this picture size: the verdict is reused as an image would be (<see cref="CanReuse"/>).
    /// </summary>
    bool _noWater;
    int _noWaterWidth, _noWaterHeight;

    /// <summary>
    /// Things whose bounding sphere is under this many texels of radius in the reflection are left out of it.
    /// </summary>
    public float MinTexels { get; set; } = 1.5f;
    HeightSnapshot _heights;
    Vector3 _realEye;
    float _texelScale;

    #endregion

    #region Reflection queries

    /// <summary>
    /// Whether a thing (a bounding sphere in world space, above the water) can show in the water's reflection: the ray from the eye to
    /// its mirror image crosses the water plane at ground that has to lie below the water level, else the picture there is land and the
    /// mirror image is never looked at. Conservative: the footprint of the sphere's image on the plane (a stretched disc, padded by
    /// <see cref="WaterPad"/>) is sampled, and one sample on or near water keeps the thing. Always true when culling is off.
    /// </summary>
    public bool MayShow(Vector3 centre, float radius)
    {
        if (!CullToWater || Warm) return true;
        float plane = WorldWater.Height;
        float he = _realEye.Y - plane, hc = centre.Y - plane;
        if (hc + radius <= -WaterPad) return false;   // wholly below the water: the oblique clip removes it
        // Too small to leave a mark: under MinTexels texels of radius in the half-resolution image (seen from the mirrored eye).
        float ddx = centre.X - _realEye.X, ddz = centre.Z - _realEye.Z, up = he + hc;
        if (radius * radius * _texelScale * _texelScale < MinTexels * MinTexels * (ddx * ddx + ddz * ddz + up * up))
            return false;
        // Image points of the sphere's points P (height hp above the water) land at eye + t (P - eye) with t = he / (he + hp), so the
        // footprint lies in discs of radius t * r along the segment between the highest and the lowest point's t.
        float tMin = he / (he + hc + radius), tMax = he / (he + Math.Max(hc - radius, 0));
        float ex = _realEye.X, ez = _realEye.Z, dx = centre.X - ex, dz = centre.Z - ez;
        // Water further from the eye than the weather fog's distance is the fog colour whatever it reflects (WaterRenderer ends in atmoApply).
        if (HideDistance is { } hide)
        {
            float horizontal = MathF.Sqrt(dx * dx + dz * dz), near = MathF.Max(tMin * (horizontal - radius) - WaterPad, 0);
            if (near * near + he * he >= hide * hide) return false;
        }
        float level = plane + WaterRise;   // a metre or two of surf and shore slope
        int steps = tMax - tMin > 0.02f ? 3 : 1;
        for (int s = 0; s < steps; s++)
        {
            float t = steps == 1 ? 0.5f * (tMin + tMax) : tMin + (tMax - tMin) * s / (steps - 1);
            float qx = ex + t * dx, qz = ez + t * dz, rr = t * radius + WaterPad;
            if (_heights.HeightAt(qx, qz) < level) return true;
            // A grid over the footprint's square, a point every 100 units or so (at most 9 by 9), so a pond inside a big footprint is found.
            int n = Math.Clamp((int)MathF.Ceiling(rr / 100), 1, 4);
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                    if (_heights.HeightAt(qx + rr * i / n, qz + rr * j / n) < level) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether this frame has a reflection to sample (not when the eye is under the water).
    /// </summary>
    public bool Valid { get; private set; }

    /// <summary>
    /// Maps a point on the water to the texture: clip.xy / clip.w * 0.5 + 0.5.
    /// </summary>
    public Matrix4x4 ViewProjection { get; private set; }

    /// <summary>
    /// (Phase 8 stage 2.) The reflection as the water samples it: the native colour with the sampler its GL name was given until then (linear,
    /// clamped to the edge, no mips, so no LOD bias; <see cref="SamplerDesc.FromGl"/>, R at GL's default). Default before the first pass.
    /// </summary>
    public SampledTexture Sampled => _colour is null ? default
        : new(Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge,
                TextureWrapMode.ClampToEdge,
            TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0)), _colour.View());
    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// The samples the targets in use were made with (0: single-sample; otherwise the MSAA count).
    /// </summary>
    public int SamplesInUse => _samples;

    /// <summary>
    /// Time the CPU spent recording the last reflection pass, and what the GPU spent on it (a few frames late).
    /// </summary>
    public double CpuMs { get; private set; }
    public double GpuMs { get; private set; }
    public int DrawnChunks { get; private set; }

    /// <summary>
    /// What the last scene callback drew (objects, foliage), for the log.
    /// </summary>
    public string SceneStats { get; set; } = "";
    public long DrawnTriangles { get; private set; }

    #endregion

    #region Render targets

    void Resize(int w, int h)
    {
        if (w == _width && h == _height && _colour is not null && _builtFor == Samples) return;
        Free();
        (_width, _height, _builtFor) = (w, h, Samples);
        _colour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, w, h,
            Use: TextureUse.Sampled | TextureUse.ColourTarget | TextureUse.TransferDst, Name: "reflection colour"));

        // The picture is drawn multisampled and resolved: the mirrored shoreline, fences and rooflines are hard edges, and
        // without it each texel of the half-resolution image is a visible stair step that the wave distortion then smears.
        // The API's sample counts are powers of two; a requested odd count (or one above the device's) takes the next
        // lower supported count rather than making an invalid texture.
        int maxSamples = Gpu.MaxSampleCount;
        _samples = 1;
        while (_samples * 2 <= Samples && _samples * 2 <= maxSamples) _samples *= 2;
        if (_samples <= 1) _samples = 0;
        GpuTexture Target(InternalFormat format, TextureUse use, int count, string name, bool privateMemory) =>
            Gpu.CreateTexture(new TextureDesc(format, w, h, Samples: Math.Max(count, 1), Use: use, Name: name, Private: privateMemory));
        if (_samples == 0)
        {
            _depth = Target(InternalFormat.DepthComponent24, TextureUse.DepthTarget | TextureUse.Sampled, 1, "reflection depth", false);
            return;
        }
        // Multisample render targets are not CPU-visible on Metal: their storage is private (Vulkan ignores the hint).
        _msColour = Target(InternalFormat.Rgba16F, TextureUse.ColourTarget | TextureUse.TransferSrc, _samples, "reflection colour msaa", true);
        _msDepth = Target(InternalFormat.DepthComponent24, TextureUse.DepthTarget | TextureUse.Sampled, _samples, "reflection depth msaa", true);
    }

    void Free()
    {
        if (_colour is null) return;
        // Released after the frames in flight.
        _colour.Dispose();
        _depth?.Dispose();
        _msColour?.Dispose();
        _msDepth?.Dispose();
        (_colour, _depth, _msColour, _msDepth) = (null, null, null, null);
    }

    /// <summary>
    /// Collects finished GPU timings: native timestamps, read once their frame's slot comes round. Nothing waits, also with
    /// <paramref name="wait"/> (kept for callers; the GL queries could block): a pass only a frame old has no time yet.
    /// </summary>
    public void Poll(bool wait = false)
    {
        _ = wait;
        if (_timer.Poll(_gpuSamples) is { } ms) GpuMs = ms;
    }

    /// <summary>
    /// Draws the mirrored scene into the texture. Call after the haze and view distance of the frame are set and before the
    /// main pass (a native segment of its own).
    /// </summary>
    public void Render(WorldCamera camera, int fullWidth, int fullHeight, SkyRenderer sky, SkyColours colours, WorldLighting light,
        TerrainRenderer terrain, WorldRenderOptions render, SceneDraw? drawObjects)
    {
        Valid = false;
        var eye = camera.Eye;
        float plane = WorldWater.Height;
        // under the water there is nothing to reflect
        if (eye.Y <= plane + 1 || fullWidth < 8 || fullHeight < 8)
        {
            _hasImage = _noWater = false;
            NearestWater = float.NaN;
            return;
        }
        var watch = Stopwatch.StartNew();
        Poll();
        _lapAt = Stopwatch.GetTimestamp();
        Array.Clear(LastPhaseMs);
        // the image, or the verdict that there is no water in view
        if (!Warm && CanReuse(camera, fullWidth, fullHeight)) { Valid = _hasImage; _skipped++; _age++; return; }
        _age = 0;
        _lastEye = eye;
        _lastView = camera.View;
        _lastFov = camera.FieldOfView;
        _heights = terrain.Snapshot();
        _realEye = eye;
        // Where the water is on the screen, as a rectangle of the reflection's clip space: what is outside it is never looked up.
        var rect = new Vector4(-1, -1, 1, 1);
        NearestWater = float.NaN;
        _noWater = false;
        if (CropToWater && !Warm)
        {
            if (WaterRect(camera, (float)fullWidth / fullHeight, out float nearestWater) is { } wet) { rect = wet; NearestWater = nearestWater; }
            else
            {
                // No water in view: nothing to reflect, and nothing for the water pass's ocean and refraction to serve (the water itself is still
                // drawn: what is past WaterDistance shows the haze). The verdict is kept like an image, so the rays are not cast again next frame.
                NearestWater = float.PositiveInfinity;
                (_noWater, _noWaterWidth, _noWaterHeight) = (true, fullWidth, fullHeight);
                Valid = _hasImage = false;
                return;
            }
        }

        Resize(Math.Max((int)(fullWidth * Scale), 64), Math.Max((int)(fullHeight * Scale), 64));
        _timer.Begin();

        // The world mirrored about the water, seen from the real eye: the same as the unmirrored world seen from the
        // mirrored eye (which is also where distances for LOD, objects and haze are measured from).
        var mirror = new Matrix4x4(1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 2 * plane, 0, 1);
        var view = mirror * camera.View;
        var mirroredEye = new Vector3(eye.X, 2 * plane - eye.Y, eye.Z);
        float aspect = fullWidth / (float)fullHeight;
        // Keep a metre below the surface too, so the waves' troughs do not show a gap at the shore.
        var clip = new Vector4(0, 1, 0, -(plane - 1));

        // The native host (docs/renderer-native.md 4.5): the multisampled target's rendering instance is opened here, cleared by its load ops,
        // and the sky, terrain, objects and foliage record into it through BeginNativeInPass, with the targets and state handed over here
        // (GpuContext.CurrentTargets, CurrentState).
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        // The sky derives its rays with the backend's clip depths (DEPTH_NEAREST, DEPTH_SKY), so its matrix follows the scene convention.
        var skyProjection = rotation * DepthConvention.Apply(Perspective(camera.FieldOfView, aspect, 1, 1000));
        sky.PrepareClouds(skyProjection, _width, _height, mirroredEye, reflection: true);
        var cmd = Gpu.BeginNative("reflection");
        bool unlit = NoShadows && !FaithfulShadows;
        // zeros: shadows off
        if (unlit) Gpu.Globals.OverrideBlock(ShadowShaders.ReceiverBlock, (_unshadowed ??= new FrameBlock(Gpu, ShadowPass.ReceiverBytes)).Binding);
        var target = _msColour is not null ? PassTargets.Of(_msColour, _msDepth) : PassTargets.Of(_colour, _depth);
        // Wave 4 (docs/renderer-native.md 6): the guests' segments are secondaries, recorded on the job threads when the pass ends.
        bool secondaries = Gpu.RecordsSecondaries;
        cmd.BeginRendering(new RenderingDesc(
            target.Colour with { Load = LoadOp.Clear, Clear = new ClearColour(light.FogColour.X, light.FogColour.Y, light.FogColour.Z, 1) },
            target.Depth with { Load = LoadOp.Clear, DepthClear = DepthConvention.Clear },
            target.Width, target.Height), secondaries);
        // What the guests draw with: the scene's state (depth tested in the scene convention and written, no culling, blending or clamp,
        // every channel). The sky turns the depth test off for itself.
        Gpu.BeginHostPass(cmd, target, DrawState.SceneDepth(target.Formats));
        if (secondaries && Gpu is GpuContext vulkanGpu)
            vulkanGpu.Frame.Parallel.Begin((CommandList)cmd, target.Formats, ReflectionStage);
        Lap(4);
        sky.Draw(skyProjection, colours);
        Lap(0);

        _options.Textures = render.Textures;
        _options.NormalMaps = false;
        _options.Objects = render.Objects;
        _options.Water = render.Water;
        _options.Wireframe = 0;
        _options.Debug = 0;
        _options.TerrainPixelError = render.TerrainPixelError;
        _options.TerrainPixelScale = render.TerrainPixelScale * TerrainLodScale;
        _options.MaterialDistance = Math.Min(render.MaterialDistance, MaterialDistance);

        terrain.BeginFrame();
        // texels of the half-resolution image per unit of (size / distance)
        _texelScale = 0.5f * _height / (MathF.Tan(camera.FieldOfView / 2) * Margin);
        var drawn = DrawMirrored(camera, aspect, view, clip, rect, mirroredEye, terrain, light, drawObjects);
        if (unlit) Gpu.Globals.OverrideBlock(ShadowShaders.ReceiverBlock, null);
        (DrawnChunks, DrawnTriangles) = (terrain.DrawnChunks, terrain.DrawnTriangles);
        var (mapped, nearest) = drawn ?? default;
        ViewProjection = mapped;   // x and y do not depend on the near plane
        Valid = _hasImage = drawn is not null;

        if (Gpu is GpuContext parallelGpu && parallelGpu.Frame.Parallel.Open) parallelGpu.Frame.Parallel.End();
        cmd.EndRendering();
        if (_msColour is not null)
        {
            // The multisampled colour into the texture the water samples (a resolve, as the framebuffer blit was), between full barriers.
            cmd.Order();
            cmd.Resolve(_msColour, _colour!);
        }
        Gpu.EndHostPass(cmd);
        if (FogVolumes && _hasImage) RunFog(cmd, nearest, mapped, mirroredEye);
        Gpu.EndNative(cmd);
        _timer.End();
        CpuMs = watch.Elapsed.TotalMilliseconds;
        _cpuSamples.Add(CpuMs);
    }

    /// <summary>
    /// Draws the mirrored terrain and the scene callback in one pass, from the camera's near plane to its view distance (capped at
    /// <see cref="MaxDistance"/>), the near plane replaced by the water plane <paramref name="clip"/>. Returns the plain mirrored view-projection
    /// (what the water samples with) and the oblique one the depth was drawn with, or null when the range is too short to draw.
    /// </summary>
    (Matrix4x4 Mapped, Matrix4x4 Drawn)? DrawMirrored(WorldCamera camera, float aspect, Matrix4x4 view, Vector4 clip, Vector4 rect,
        Vector3 mirroredEye,
        TerrainRenderer terrain, WorldLighting light, SceneDraw? drawObjects)
    {
        float near = camera.Near, far = Math.Min(camera.ViewDistance, MaxDistance);
        if (far <= near * 1.5f) return null;

        // The oblique clip assumes the standard 0..1 near and far planes, so the convention is applied after it (a linear map of clip z).
        var standard = Perspective(camera.FieldOfView, aspect, near, far);
        var viewProjection = view * DepthConvention.Apply(Oblique(standard, view, clip));
        var frustum = Frustum(viewProjection, rect);
        terrain.Draw(viewProjection, mirroredEye, frustum, _options, light, WorldWater.Height - 1, secondary: true);
        Lap(1);
        drawObjects?.Invoke(viewProjection, mirroredEye, frustum);
        return (view * DepthConvention.Apply(standard), viewProjection);
    }

    /// <summary>
    /// Whether last frame's image still serves: the water samples it with the matrix it was drawn with, so the only error is
    /// the parallax of the eye having moved, and a turn that brings new sky or ground in at the picture's edge.
    /// </summary>
    bool CanReuse(WorldCamera camera, int fullWidth, int fullHeight)
    {
        if (_age >= MaxAge || _builtFor != Samples) return false;
        // An image at this size, or the no-water verdict at this size (heights stream in, so it is checked again after MaxAge frames like an image).
        int width = Math.Max((int)(fullWidth * Scale), 64);
        int height = Math.Max((int)(fullHeight * Scale), 64);
        bool stale = _hasImage
            ? _colour is null || _msColour is null && _samples != 0 || _width != width || _height != height
            : !_noWater || fullWidth != _noWaterWidth || fullHeight != _noWaterHeight;
        if (stale) return false;
        var view = camera.View;
        float shift = Vector3.Distance(camera.Eye, _lastEye);
        // About a texel of parallax for things 3000 units away, and no more than a tenth of a degree of turn.
        float turn = Vector3.Dot(new Vector3(view.M13, view.M23, view.M33), new Vector3(_lastView.M13, _lastView.M23, _lastView.M33));
        float up = Vector3.Dot(new Vector3(view.M12, view.M22, view.M32), new Vector3(_lastView.M12, _lastView.M22, _lastView.M32));
        return camera.FieldOfView == _lastFov && shift <= 3 + 0.004f * (camera.Eye.Y - WorldWater.Height) && turn > 0.999998f && up > 0.999998f;
    }

    #endregion

    #region Statistics

    /// <summary>
    /// The <see cref="StageClock"/> stage of the reflection's recording jobs.
    /// </summary>
    const int ReflectionStage = 4;

    /// <summary>
    /// CPU time of the parts of the pass (sky, terrain, objects, foliage), summed over the passes drawn; the scene callback adds its own.
    /// </summary>
    public readonly double[] PhaseMs = new double[5];
    long _lapAt;
    public void Lap(int phase)
    {
        long now = Stopwatch.GetTimestamp();
        double ms = (now - _lapAt) * 1000.0 / Stopwatch.Frequency;
        PhaseMs[phase] += ms;
        LastPhaseMs[phase] += ms;
        _lapAt = now;
    }

    /// <summary>
    /// The same for the last pass alone.
    /// </summary>
    public readonly double[] LastPhaseMs = new double[5];
    public string DescribeLast() =>
        $"setup {LastPhaseMs[4]:0.0}, sky {LastPhaseMs[0]:0.0}, terrain {LastPhaseMs[1]:0.0}, "
        + $"objects {LastPhaseMs[2]:0.0}, foliage {LastPhaseMs[3]:0.0}";

    /// <summary>
    /// Mean, 95th percentile and maximum of the reflection's GPU and CPU time over the passes drawn so far (for <c>--fly-benchmark</c>).
    /// </summary>
    public string DescribeStats()
    {
        Poll();
        // The first pass compiles shaders and warms up; it is left out.
        static string One(IEnumerable<double> values)
        {
            var s = values.Skip(1).OrderBy(x => x).ToList();
            if (s.Count == 0) return "none";
            return $"mean {s.Average():0.00}, p95 {s[Math.Min((int)(s.Count * 0.95), s.Count - 1)]:0.00}, max {s[^1]:0.00}";
        }
        int frames = _cpuSamples.Count + _skipped;
        double PerFrame(IEnumerable<double> v) => v.Skip(1).Sum() / Math.Max(frames - 1, 1);
        return $"{_cpuSamples.Count} passes drawn, {_skipped} frames reused, {(_samples == 0 ? "single-sample" : $"{_samples}x samples")}; "
            + $"per frame mean cpu {PerFrame(_cpuSamples):0.00} ms, gpu {PerFrame(_gpuSamples):0.00} ms; "
            + $"per pass: gpu {One(_gpuSamples)}, cpu {One(_cpuSamples)} ms (cpu mean by part: "
            + $"terrain {PhaseMs[1] / Math.Max(_cpuSamples.Count, 1):0.00}, objects {PhaseMs[2] / Math.Max(_cpuSamples.Count, 1):0.00}, "
            + $"foliage {PhaseMs[3] / Math.Max(_cpuSamples.Count, 1):0.00}); nearest water {NearestWater:0} units";
    }

    #endregion

    #region Projection math

    /// <summary>
    /// The mirror's perspective: clip X negated (the mirror's reversed winding turns back to counter-clockwise) and widened by <see cref="Margin"/>;
    /// depth standard 0..1, reversed by the caller (<see cref="DepthConvention.Apply"/>) after the oblique clip.
    /// </summary>
    static Matrix4x4 Perspective(float fov, float aspect, float near, float far)
    {
        // A little wider than the picture, so the distorted lookups near its edge still hit the image.
        var p = Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, near, far);
        p.M11 = -p.M11 / Margin;
        p.M22 /= Margin;
        return p;
    }

    /// <summary>
    /// Replaces the projection's near plane by <paramref name="plane"/> (world space, kept side positive), after Lengyel's oblique near-plane
    /// clipping in its 0..1 depth form: the third clip column becomes a multiple of the plane, scaled so the far corner keeps z = w; x and y stay
    /// as they were. Takes the standard 0..1 matrix; call before <see cref="DepthConvention.Apply"/>.
    /// </summary>
    static Matrix4x4 Oblique(Matrix4x4 p, Matrix4x4 view, Vector4 plane)
    {
        if (!Matrix4x4.Invert(view, out var viewInverse) || !Matrix4x4.Invert(p, out var projectionInverse)) return p;
        var c = Vector4.Transform(plane, Matrix4x4.Transpose(viewInverse));   // the plane in view space
        var q = Vector4.Transform(new Vector4(MathF.Sign(c.X), MathF.Sign(c.Y), 1, 1), projectionInverse);
        var w = new Vector4(p.M14, p.M24, p.M34, p.M44);
        float denominator = Vector4.Dot(c, q);
        if (MathF.Abs(denominator) < 1e-12f) return p;
        var z = c * (Vector4.Dot(w, q) / denominator);
        p.M13 = z.X;
        p.M23 = z.Y;
        p.M33 = z.Z;
        p.M43 = z.W;
        return p;
    }

    /// <summary>
    /// The six planes (normals inwards) of a 0..1-depth matrix, standard or reversed: the clip volume is 0 &lt;= z &lt;= w either way.
    /// </summary>
    static Vector4[] Frustum(Matrix4x4 m, Vector4 rect)
    {
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        // The sides are those of the rectangle (x0, y0, x1, y1 in clip space) of the picture: x / w >= x0 is x - x0 w >= 0.
        return [c1 - rect.X * c4, rect.Z * c4 - c1, c2 - rect.Y * c4, rect.W * c4 - c2, c3, c4 - c3];
    }

    /// <summary>
    /// The rectangle (x0, y0, x1, y1) of the reflection's clip space that the water on the screen looks up, or null with no water in view:
    /// a grid of screen points (every 20 pixels or so) whose ray down to the water plane meets ground below the water, each projected
    /// with the reflection's matrix (the water does the same with its own position); padded by the ripples' largest shift of the lookup
    /// (0.04 of the texture, 0.08 in clip space) and a cell. Water past <see cref="MaxDistance"/>, and past
    /// <see cref="WaterDistance"/>, does not count. <paramref name="nearest"/>: the distance to the nearest sample on water (infinity with none; NaN
    /// when the view could not be inverted).
    /// </summary>
    Vector4? WaterRect(WorldCamera camera, float aspect, out float nearest)
    {
        float plane = WorldWater.Height, he = _realEye.Y - plane;
        nearest = float.NaN;
        if (!Matrix4x4.Invert(camera.View, out var inverse)) return new Vector4(-1, -1, 1, 1);
        nearest = float.PositiveInfinity;
        float far = WaterDistance is { } cap ? MathF.Min(MaxDistance, cap) : MaxDistance;
        var view = new Matrix4x4(1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 2 * plane, 0, 1) * camera.View;
        var mapped = view * Perspective(camera.FieldOfView, aspect, 1, 1000);
        float tan = MathF.Tan(camera.FieldOfView / 2), level = plane + WaterRise;
        int nx = Math.Clamp((int)(aspect * 54), 8, 160), ny = 54;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                var dir = Vector3.TransformNormal(new Vector3(((i + 0.5f) / nx * 2 - 1) * tan * aspect, ((j + 0.5f) / ny * 2 - 1) * tan, -1),
                    inverse);
                if (dir.Y > -1e-4f) continue;
                var p = _realEye + dir * (he / -dir.Y);
                float distance = dir.Length() * (he / -dir.Y);
                if (distance > far || _heights.HeightAt(p.X, p.Z) >= level) continue;
                nearest = MathF.Min(nearest, distance);
                var c = Vector4.Transform(new Vector4(p, 1), mapped);
                if (c.W <= 1e-3f) continue;
                float x = c.X / c.W, y = c.Y / c.W;
                (x0, y0, x1, y1) = (MathF.Min(x0, x), MathF.Min(y0, y), MathF.Max(x1, x), MathF.Max(y1, y));
            }
        if (x0 > x1) return null;
        float padX = 0.08f + 2f / nx + 0.02f, padY = 0.08f + 2f / ny + 0.02f;
        return new Vector4(Math.Max(x0 - padX, -1), Math.Max(y0 - padY, -1), Math.Min(x1 + padX, 1), Math.Min(y1 + padY, 1));
    }

    #endregion

    #region Fog

    /// <summary>
    /// How far above the water level ground still holds water: the breakers' run-up on the beach, and the waves (units).
    /// </summary>
    public float WaterRise { get; set; } = 25;

    /// <summary>
    /// Meitou: the placed fog volumes along the reflected rays (docs/render-water.md "Fog in the reflection"; the frame loop sets it while the Meitou
    /// water draws and volumes are in view). The game's reflection draws no volumes (queue 82 is past its queue 60), so in fog the mirror showed the
    /// open sky the eye cannot see. Each texel's ray from the mirrored eye crosses the water plane and runs on as the real reflected ray to what the
    /// texel shows; the volumes along that part are laid over the texel (the main fog pass already fogs the part from the eye to the water).
    /// </summary>
    public bool FogVolumes { get; set; }
    LegacyProgram? _fogProgram;
    int _fogProgramSamples = -1;
    GpuPipeline? _fogPipeline;

    /// <summary>
    /// The fog pass over the reflection: the fog volume functions with the texel's distance rebuilt from the reflection's depth
    /// (<paramref name="multisampled"/>: its first sample) through the inverse of the mirrored, oblique view-projection it was drawn with, and the
    /// ray's direction through the plain one's.
    /// </summary>
    internal static string FogFragment(bool multisampled) => "#version 330 core\n" + AtmosphereShaders.Functions + FogVolumeShaders.Functions + $$"""

        in vec2 vUv;
        out vec4 fragColour;
        uniform {{(multisampled ? "sampler2DMS" : "sampler2D")}} uDepth;
        uniform mat4 uInverse;        // clip (the stored depth, scene convention) to world: the mirrored, oblique view-projection inverted
        uniform mat4 uRayInverse;     // the same without the oblique near plane: its depth is no surface's, but its rays all point forwards
        uniform vec3 uMirroredEye;
        uniform float uWaterY;
        {{DepthConvention.Glsl}}

        void main()
        {
            vec2 ndc = vUv * 2.0 - 1.0;
            float d = texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r;
            bool sky = depthIsSky(d);
            // The texel's ray from the plain projection: the oblique one's skewed far plane puts the mid-depth point of grazing rays (the mirrored
            // horizon, where the water reflects most) behind the eye. Any clip depth between the planes gives a point on the ray.
            vec4 r = uRayInverse * vec4(ndc, 0.5, 1.0);
            vec3 dir = normalize(r.xyz / r.w - uMirroredEye);
            // From under the water the ray climbs to the plane, where it is the real reflected ray from then on.
            if (dir.y < 1e-5) { fragColour = vec4(0.0, 0.0, 0.0, 1.0); return; }
            float tw = (uWaterY - uMirroredEye.y) / dir.y;
            // The surface the texel shows, by the depth the oblique projection wrote (the sky: the far clip).
            vec4 h = uInverse * vec4(ndc, d, 1.0);
            float total = sky ? 1e9 : length(h.xyz / h.w - uMirroredEye);
            vec3 add;
            float trans;
            fogVolumesAccumulate(uMirroredEye + dir * tw, dir, max(total - tw, 0.0), add, trans);
            fragColour = vec4(add, trans);
        }
        """;

    void RunFog(IGpuCommandList cmd, Matrix4x4 nearViewProjection, Matrix4x4 nearRays, Vector3 mirroredEye)
    {
        var depthTexture = _msDepth ?? _depth;
        if (depthTexture is null || _colour is null || !Matrix4x4.Invert(nearViewProjection, out var inverse) || !Matrix4x4.Invert(nearRays,
                out var rayInverse))
            return;
        if (_fogProgram is null || _fogProgramSamples != _samples)
        {
            _fogProgram?.Dispose();
            _fogProgram = LegacyProgram.Create(Gpu, PostProcessShaders.Vertex, FogFragment(_samples > 0), "reflection fog volumes");
            _fogProgramSamples = _samples;
            // The fog volumes pass's blend: scene Ã— transmittance (alpha) + the fog's light, colour only.
            var state = new DrawState(CullMode.None, FrontFaceDirection.Ccw, false, false, DepthFunction.Lequal, false, 0, 0,
                new BlendState(true, BlendingFactor.One, BlendingFactor.SrcAlpha), DrawState.Rgb, PolygonKind.Fill, false, false);
            _fogPipeline = Gpu.Pipelines.Get(state.Pipeline(_fogProgram.Program, _fogProgram.VertexLayout([]), PrimitiveType.Triangles,
                new AttachmentFormats(_colour.Desc.Format, InternalFormat.None), _fogProgram.Name));
        }
        var p = _fogProgram;
        p.Bind(p.Sampler("uDepth"), new SampledTexture(Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), depthTexture.View()));
        p.Set(p.Uniform("uInverse"), in inverse);
        p.Set(p.Uniform("uRayInverse"), in rayInverse);
        p.Set(p.Uniform("uMirroredEye"), mirroredEye.X, mirroredEye.Y, mirroredEye.Z);
        p.Set(p.Uniform("uWaterY"), WorldWater.Height);
        p.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        cmd.Order();
        cmd.BeginRendering(new RenderingDesc(PassTargets.Of(_colour, null).Colour, default, _width, _height));
        cmd.SetViewport(new PixelViewport(0, 0, _width, _height, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)_width, (uint)_height));
        cmd.SetRaster(CullMode.None, FrontFaceDirection.Ccw);
        cmd.SetDepth(false, false, DepthFunction.Lequal);
        cmd.SetDepthBias(false, 0, 0);
        cmd.BindPipeline(_fogPipeline!.Value);
        p.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
    }

    #endregion

    #region Disposal

    public void Dispose()
    {
        Free();
        _fogProgram?.Dispose();
    }

    #endregion
}