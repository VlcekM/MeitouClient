using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using GpuTexture = Meitou.Rendering.Gpu.Texture;

namespace Meitou.Rendering;

/// <summary>
/// Planar reflection of the scene for the water (docs/formats/terrain.md, "Water"; the game does the same with a 512Â²
/// render target drawn by a mirrored camera). The sky, terrain and, through <see cref="SceneDraw"/>, objects (foliage
/// later) are drawn mirrored about the water plane into a half-resolution texture; <see cref="WaterRenderer"/> samples it
/// with the same view-projection, so no flip of the texture is needed. The mirror reverses triangle winding, which a
/// mirrored (negated) clip X puts right again, so no renderer has to change its culling. Nothing below the water leaks
/// into the picture: the projection's near plane is replaced by the water plane (oblique near-plane clipping).
/// </summary>
public sealed class ReflectionPass : IDisposable
{
    /// <summary>Draws the scene's other geometry (objects, foliage) with the given matrix, eye and frustum planes of one depth slice.</summary>
    public delegate void SceneDraw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum);

    /// <summary>Size of the texture relative to the picture (the game's is a fixed 512Â²).</summary>
    public const float Scale = 0.5f;
    /// <summary>The reflection covers this much more than the picture in each direction (as a factor of the field of view tangent).</summary>
    public const float Margin = 1.12f;
    /// <summary>Samples per texel of the reflection (1 = off). Resolved into the plain texture the water samples.</summary>
    public const int DefaultSamples = 4;
    /// <summary>Samples per texel of the reflection in use (1 = off; a change rebuilds the targets). Default <see cref="DefaultSamples"/>; <c>--reflection-samples</c>, <c>--ab refl-msaa</c>.</summary>
    public int Samples { get; set; } = DefaultSamples;
    /// <summary>Side B of <c>--ab refl-msaa</c> (<c>MEITOU_REFL_AB_SAMPLES</c>, default 1).</summary>
    public int AbSamples { get; set; } = 1;
    /// <summary>
    /// Meitou: the mirrored scene is drawn without the sun shadows (the receivers see a lit surface: the frame's shadow block is swapped for the
    /// shadows-off one while the pass records; <c>--ab refl-shadows</c> flips it). A half-resolution, rippled mirror image hides the shadows' detail,
    /// and the receiver is a good part of the pass's shading. <see cref="FaithfulShadows"/> keeps them.
    /// </summary>
    public bool NoShadows { get; set; } = true;
    /// <summary>Set by the frame loop while the shadows switch is Faithful: the reflection then keeps the shadows as before (what the game does is Unknown).</summary>
    public bool FaithfulShadows { get; set; }
    FrameBlock? unshadowed;

    /// <summary>The native GPU API (all native since phase 8 stage 3: the pass is the guests' host, docs/renderer-native.md 8.9).</summary>
    public GpuContext Gpu { get; }
    readonly PassTimer timer;
    readonly WorldRenderOptions options = new();
    // The texture the water samples, and the depth beside it when the scene is drawn without multisampling; else the multisampled twins the
    // scene is drawn into, resolved into the colour.
    GpuTexture? colour, depth, msColour, msDepth;
    int samples, builtFor;   // builtFor: the Samples the targets were made for
    int width, height, skipped, age;
    bool hasImage;
    Vector3 lastEye;
    float lastFov;
    Matrix4x4 lastView;
    readonly List<double> gpuSamples = [];
    readonly List<double> cpuSamples = [];

    public ReflectionPass(GpuContext gpu)
    {
        Gpu = gpu;
        // Tuning knobs for experiments (MEITOU_REFL_OBJECTS, _FOLIAGE, _LOD, _AGE); the defaults are what the viewer ships with.
        static float Env(string n, float d) => float.TryParse(Environment.GetEnvironmentVariable("MEITOU_REFL_" + n), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
        ObjectDistance = Env("OBJECTS", ObjectDistance);
        FoliageDistance = Env("FOLIAGE", FoliageDistance);
        ObjectLodBias = Env("LOD", ObjectLodBias);
        MaxAge = (int)Env("AGE", MaxAge);
        AbSamples = (int)Env("AB_SAMPLES", AbSamples);
        TerrainLodScale = Env("TLOD", TerrainLodScale);
        timer = new PassTimer(gpu);
    }

    /// <summary>The game's <c>water reflection</c> when the key is missing, and <c>reflection range</c> (docs/formats/settings.md).</summary>
    public const int DefaultLevel = 2;
    public const float DefaultRange = 0.6f;
    /// <summary>
    /// The game's <c>water reflection</c> 0..4: what the mirrored scene holds. 0 no pass (the water shows the sky colour, the viewer's
    /// counterpart of the shader's NO_REFLECTION branch), 1 sky and terrain, 2 adds the characters (none in the viewer yet), 3 adds
    /// buildings (here every placed object), 4 everything (also foliage). The frame loop reads it for what to draw.
    /// </summary>
    public int Level { get; set; } = DefaultLevel;
    /// <summary>The game's <c>reflection range</c>: the far clip is the haze distance (the game's view distance Ã— 10) times this; the frame loop sets <see cref="MaxDistance"/> from it.</summary>
    public float Range { get; set; } = DefaultRange;
    /// <summary>Furthest distance reflected: mountains beyond it are not mirrored (the sea there is mostly haze anyway).</summary>
    public float MaxDistance { get; set; } = 150000;
    /// <summary>Objects (buildings, features) further than this from the eye are left out of the reflection.</summary>
    public float ObjectDistance { get; set; } = 2000;
    /// <summary>Beyond this distance the mirrored terrain takes the cheap ground colour instead of the biome textures.</summary>
    public float MaterialDistance { get; set; } = 6000;
    /// <summary>Foliage (trees, bushes, rocks; no grass) further than this is left out of the reflection (the game's layers reach 4 times as far in the picture).</summary>
    public float FoliageDistance { get; set; } = 2200;
    /// <summary>Multiplies the distance the objects' LOD level is chosen by (above 1: coarser levels sooner; the half-resolution, rippled image cannot show the detail).</summary>
    public float ObjectLodBias { get; set; } = 3;
    /// <summary>Frames a finished reflection may be reused when the camera has hardly moved (0 = draw every frame).</summary>
    public int MaxAge { get; set; } = 3;
    /// <summary>Multiplies the terrain LOD's pixel scale for the mirrored terrain (its target is half the picture's size, so 0.5 measures the error in its own pixels; below: coarser).</summary>
    public float TerrainLodScale { get; set; } = 0.5f;

    /// <summary>
    /// Meitou: the reflection leaves out what cannot be seen through any water (<see cref="MayShow"/>), what is under <see cref="MinTexels"/>,
    /// and keeps the shorter <see cref="ObjectDistance"/> and <see cref="FoliageDistance"/>; with <see cref="CropToWater"/> it is the whole of
    /// the cheaper reflection (<c>--ab refl-cull</c> flips it). Off: the reach the pass had before (3000 units).
    /// </summary>
    public bool CullToWater { get; set; } = true;
    /// <summary>The object range in use: <see cref="ObjectDistance"/>, or with <see cref="CullToWater"/> off at least the old 3000.</summary>
    public float ObjectReach => CullToWater ? ObjectDistance : Math.Max(ObjectDistance, 3000);
    /// <summary>The foliage range in use, as <see cref="ObjectReach"/>.</summary>
    public float FoliageReach => CullToWater ? FoliageDistance : Math.Max(FoliageDistance, 3000);
    /// <summary>Meitou: draw only the part of the reflection the water on the screen looks up (<see cref="WaterRect"/>), and nothing with no water in view.</summary>
    public bool CropToWater { get; set; } = true;
    /// <summary>Extra ground (units) around a mirrored thing's footprint tested for water: the waves' ripples shift the lookup, and the terrain grid is coarse.</summary>
    public float WaterPad { get; set; } = 60;
    /// <summary>The distance from the eye beyond which the weather's fog hides every surface this frame (<see cref="FogVolumes.AtmosphereDistance"/>), set by the frame loop; null: none.</summary>
    public float? HideDistance { get; set; }
    /// <summary>Things whose bounding sphere is under this many texels of radius in the reflection are left out of it.</summary>
    public float MinTexels { get; set; } = 1.5f;
    HeightSnapshot heights;
    Vector3 realEye;
    float texelScale;

    /// <summary>
    /// Whether a thing (a bounding sphere in world space, above the water) can show in the water's reflection: the ray from the eye to
    /// its mirror image crosses the water plane at ground that has to lie below the water level, else the picture there is land and the
    /// mirror image is never looked at. Conservative: the footprint of the sphere's image on the plane (a stretched disc, padded by
    /// <see cref="WaterPad"/>) is sampled, and one sample on or near water keeps the thing. Always true when culling is off.
    /// </summary>
    public bool MayShow(Vector3 centre, float radius)
    {
        if (!CullToWater) return true;
        float plane = WorldWater.Height;
        float he = realEye.Y - plane, hc = centre.Y - plane;
        if (hc + radius <= -WaterPad) return false;   // wholly below the water: the oblique clip removes it
        // Too small to leave a mark: under MinTexels texels of radius in the half-resolution image (seen from the mirrored eye).
        float ddx = centre.X - realEye.X, ddz = centre.Z - realEye.Z, up = he + hc;
        if (radius * radius * texelScale * texelScale < MinTexels * MinTexels * (ddx * ddx + ddz * ddz + up * up)) return false;
        // Image points of the sphere's points P (height hp above the water) land at eye + t (P - eye) with t = he / (he + hp), so the
        // footprint lies in discs of radius t * r along the segment between the highest and the lowest point's t.
        float tMin = he / (he + hc + radius), tMax = he / (he + Math.Max(hc - radius, 0));
        float ex = realEye.X, ez = realEye.Z, dx = centre.X - ex, dz = centre.Z - ez;
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
            if (heights.HeightAt(qx, qz) < level) return true;
            // A grid over the footprint's square, a point every 100 units or so (at most 9 by 9), so a pond inside a big footprint is found.
            int n = Math.Clamp((int)MathF.Ceiling(rr / 100), 1, 4);
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                    if (heights.HeightAt(qx + rr * i / n, qz + rr * j / n) < level) return true;
        }
        return false;
    }

    /// <summary>Whether this frame has a reflection to sample (not when the eye is under the water).</summary>
    public bool Valid { get; private set; }
    /// <summary>Maps a point on the water to the texture: clip.xy / clip.w * 0.5 + 0.5.</summary>
    public Matrix4x4 ViewProjection { get; private set; }
    /// <summary>
    /// (Phase 8 stage 2.) The reflection as the water samples it: the native colour with the sampler its GL name was given until then (linear,
    /// clamped to the edge, no mips, so no LOD bias; <see cref="SamplerDesc.FromGl"/>, R at GL's default). Default before the first pass.
    /// </summary>
    public SampledTexture Sampled => colour is null ? default
        : new(Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0)), colour.View(), colour.Image);
    public int Width => width;
    public int Height => height;
    /// <summary>Time the CPU spent recording the last reflection pass, and what the GPU spent on it (a few frames late).</summary>
    public double CpuMs { get; private set; }
    public double GpuMs { get; private set; }
    public int DrawnChunks { get; private set; }
    /// <summary>What the last scene callback drew (objects, foliage), for the log.</summary>
    public string SceneStats { get; set; } = "";
    public long DrawnTriangles { get; private set; }

    void Resize(int w, int h)
    {
        if (w == width && h == height && colour is not null && builtFor == Samples) return;
        Free();
        (width, height, builtFor) = (w, h, Samples);
        colour = GpuTexture.Create(Gpu, new TextureDesc(Format.R16G16B16A16Sfloat, w, h, Use: TextureUse.Sampled | TextureUse.ColourTarget | TextureUse.TransferDst, Name: "reflection colour"));

        // The picture is drawn multisampled and resolved: the mirrored shoreline, fences and rooflines are hard edges, and
        // without it each texel of the half-resolution image is a visible stair step that the wave distortion then smears.
        samples = Math.Min(Samples, MaxSamples());
        if (samples <= 1) samples = 0;
        GpuTexture Target(Format format, TextureUse use, int count, string name) =>
            GpuTexture.Create(Gpu, new TextureDesc(format, w, h, Samples: Math.Max(count, 1), Use: use, Name: name));
        if (samples == 0)
        {
            depth = Target(Format.D32Sfloat, TextureUse.DepthTarget | TextureUse.Sampled, 1, "reflection depth");
            return;
        }
        msColour = Target(Format.R16G16B16A16Sfloat, TextureUse.ColourTarget | TextureUse.TransferSrc, samples, "reflection colour msaa");
        msDepth = Target(Format.D32Sfloat, TextureUse.DepthTarget | TextureUse.Sampled, samples, "reflection depth msaa");
    }

    /// <summary>The most samples a colour and depth target may have on this device (VkGl answered GL's MAX_SAMPLES with 8).</summary>
    int MaxSamples()
    {
        var l = Gpu.Device.Limits;
        uint both = (uint)(l.FramebufferColorSampleCounts & l.FramebufferDepthSampleCounts);
        int max = 1;
        while (max < 64 && (both & (uint)(max * 2)) != 0) max *= 2;
        return max;
    }

    void Free()
    {
        if (colour is null) return;
        // Released after the frames in flight.
        colour.Dispose();
        depth?.Dispose();
        msColour?.Dispose();
        msDepth?.Dispose();
        (colour, depth, msColour, msDepth) = (null, null, null, null);
    }

    /// <summary>Collects finished GPU timings: native timestamps, read once their frame's slot comes round. Nothing waits, also with
    /// <paramref name="wait"/> (kept for callers; the GL queries could block): a pass only a frame old has no time yet.</summary>
    public void Poll(bool wait = false)
    {
        _ = wait;
        if (timer.Poll(gpuSamples) is { } ms) GpuMs = ms;
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
        if (eye.Y <= plane + 1 || fullWidth < 8 || fullHeight < 8) { hasImage = false; return; }   // under the water there is nothing to reflect
        var watch = Stopwatch.StartNew();
        Poll();
        lapAt = Stopwatch.GetTimestamp();
        Array.Clear(LastPhaseMs);
        if (CanReuse(camera, fullWidth, fullHeight)) { Valid = true; skipped++; age++; return; }
        age = 0;
        lastEye = eye;
        lastView = camera.View;
        lastFov = camera.FieldOfView;
        heights = terrain.Snapshot();
        realEye = eye;
        // Where the water is on the screen, as a rectangle of the reflection's clip space: what is outside it is never looked up.
        var rect = new Vector4(-1, -1, 1, 1);
        if (CropToWater && WaterRect(camera, (float)fullWidth / fullHeight) is { } wet) rect = wet;
        else if (CropToWater) { Valid = hasImage = false; return; }   // no water in view: nothing to reflect (the water pass is not drawn either, or shows the sky)

        Resize(Math.Max((int)(fullWidth * Scale), 64), Math.Max((int)(fullHeight * Scale), 64));
        timer.Begin();

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
        var cmd = Gpu.BeginNative("reflection");
        bool unlit = NoShadows && !FaithfulShadows;
        if (unlit) Gpu.Globals.OverrideBlock(ShadowShaders.ReceiverBlock, (unshadowed ??= new FrameBlock(Gpu, ShadowPass.ReceiverBytes)).Binding);   // zeros: shadows off
        var target = msColour is not null ? PassTargets.Of(msColour, msDepth) : PassTargets.Of(colour, depth);
        // Wave 4 (docs/renderer-native.md 6): the guests' segments are secondaries, recorded on the job threads when the pass ends.
        bool secondaries = Recording.Secondaries;
        cmd.BeginRendering(new RenderingDesc(
            target.Colour with { Load = AttachmentLoadOp.Clear, Clear = new ClearValue(new ClearColorValue(light.FogColour.X, light.FogColour.Y, light.FogColour.Z, 1)) },
            target.Depth with { Load = AttachmentLoadOp.Clear, Clear = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)) },
            target.Width, target.Height), secondaries);
        // What the guests draw with: the scene's state (depth tested with less-or-equal and written, no culling, blending or clamp, every channel).
        // The sky turns the depth test off for itself.
        Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
        if (secondaries) Gpu.Frame.Parallel.Begin(cmd, target.Formats, ReflectionStage);
        Lap(4);
        var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
        sky.Draw(rotation * Perspective(camera.FieldOfView, aspect, 1, 1000), colours);
        Lap(0);

        options.Textures = render.Textures;
        options.NormalMaps = false;
        options.Objects = render.Objects;
        options.Water = render.Water;
        options.Wireframe = 0;
        options.Debug = 0;
        (options.TerrainPixelError, options.TerrainFarPixelError, options.TerrainRampStart) = (render.TerrainPixelError, render.TerrainFarPixelError, render.TerrainRampStart);
        options.TerrainPixelScale = render.TerrainPixelScale * TerrainLodScale;
        options.MaterialDistance = Math.Min(render.MaterialDistance, MaterialDistance);

        terrain.BeginFrame();
        texelScale = 0.5f * height / (MathF.Tan(camera.FieldOfView / 2) * Margin);   // texels of the half-resolution image per unit of (size / distance)
        bool first = true;
        Matrix4x4 mapped = default, nearest = default;
        foreach (var (near, far0) in camera.Slices())
        {
            float far = Math.Min(far0, MaxDistance);
            if (far <= near * 1.5f) continue;
            if (!first) Gpu.ClearDepth(cmd, 1f, new Rect2D(new Offset2D(0, 0), new Extent2D((uint)target.Width, (uint)target.Height)));
            first = false;
            var projection = Oblique(Perspective(camera.FieldOfView, aspect, near, far), view, clip);
            var viewProjection = view * projection;
            nearest = viewProjection;   // the slices run far to near: the last one's depth stays in the target
            mapped = view * Perspective(camera.FieldOfView, aspect, near, far);
            var frustum = Frustum(viewProjection, rect);
            terrain.Draw(viewProjection, mirroredEye, frustum, options, light, plane - 1, secondary: true);
            Lap(1);
            if (near <= camera.Near && render.Objects) drawObjects?.Invoke(viewProjection, mirroredEye, frustum);
        }
        if (unlit) Gpu.Globals.OverrideBlock(ShadowShaders.ReceiverBlock, null);
        (DrawnChunks, DrawnTriangles) = (terrain.DrawnChunks, terrain.DrawnTriangles);
        ViewProjection = mapped;   // x and y do not depend on the near plane
        Valid = hasImage = !first;

        if (Gpu.Frame.Parallel.Open) Gpu.Frame.Parallel.End();
        cmd.EndRendering();
        if (msColour is not null)
        {
            // The multisampled colour into the texture the water samples (a resolve, as the framebuffer blit was), between full barriers.
            cmd.Barrier(BarrierBatch.Full);
            cmd.Resolve(msColour, colour!);
        }
        Gpu.EndHostPass(cmd);
        // What lies beyond the near slice (20000 units on) is fogged as the sky, to the far clip.
        if (FogVolumes && hasImage) RunFog(cmd, nearest, mirroredEye);
        Gpu.EndNative(cmd);
        timer.End();
        CpuMs = watch.Elapsed.TotalMilliseconds;
        cpuSamples.Add(CpuMs);
    }

    /// <summary>
    /// Whether last frame's image still serves: the water samples it with the matrix it was drawn with, so the only error is
    /// the parallax of the eye having moved, and a turn that brings new sky or ground in at the picture's edge.
    /// </summary>
    bool CanReuse(WorldCamera camera, int fullWidth, int fullHeight)
    {
        if (age >= MaxAge || !hasImage || colour is null || msColour is null && samples != 0) return false;
        if (width != Math.Max((int)(fullWidth * Scale), 64) || height != Math.Max((int)(fullHeight * Scale), 64)) return false;
        var view = camera.View;
        float shift = Vector3.Distance(camera.Eye, lastEye);
        // About a texel of parallax for things 3000 units away, and no more than a tenth of a degree of turn.
        float turn = Vector3.Dot(new Vector3(view.M13, view.M23, view.M33), new Vector3(lastView.M13, lastView.M23, lastView.M33));
        float up = Vector3.Dot(new Vector3(view.M12, view.M22, view.M32), new Vector3(lastView.M12, lastView.M22, lastView.M32));
        return camera.FieldOfView == lastFov && shift <= 3 + 0.004f * (camera.Eye.Y - WorldWater.Height) && turn > 0.999998f && up > 0.999998f;
    }

    /// <summary>The <see cref="StageClock"/> stage of the reflection's recording jobs.</summary>
    const int ReflectionStage = 4;

    /// <summary>CPU time of the parts of the pass (sky, terrain, objects, foliage), summed over the passes drawn; the scene callback adds its own.</summary>
    public readonly double[] PhaseMs = new double[5];
    long lapAt;
    public void Lap(int phase) { long now = Stopwatch.GetTimestamp(); double ms = (now - lapAt) * 1000.0 / Stopwatch.Frequency; PhaseMs[phase] += ms; LastPhaseMs[phase] += ms; lapAt = now; }
    /// <summary>The same for the last pass alone.</summary>
    public readonly double[] LastPhaseMs = new double[5];
    public string DescribeLast() => $"setup {LastPhaseMs[4]:0.0}, sky {LastPhaseMs[0]:0.0}, terrain {LastPhaseMs[1]:0.0}, objects {LastPhaseMs[2]:0.0}, foliage {LastPhaseMs[3]:0.0}";

    /// <summary>Mean, 95th percentile and maximum of the reflection's GPU and CPU time over the passes drawn so far (for <c>--fly-benchmark</c>).</summary>
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
        int frames = cpuSamples.Count + skipped;
        double perFrame(IEnumerable<double> v) => v.Skip(1).Sum() / Math.Max(frames - 1, 1);
        return $"{cpuSamples.Count} passes drawn, {skipped} frames reused; per frame mean cpu {perFrame(cpuSamples):0.00} ms, gpu {perFrame(gpuSamples):0.00} ms; per pass: gpu {One(gpuSamples)}, cpu {One(cpuSamples)} ms (cpu mean by part: terrain {PhaseMs[1] / Math.Max(cpuSamples.Count, 1):0.00}, objects {PhaseMs[2] / Math.Max(cpuSamples.Count, 1):0.00}, foliage {PhaseMs[3] / Math.Max(cpuSamples.Count, 1):0.00})";
    }

    /// <summary>OpenGL perspective (depth âˆ’1..1) with clip X negated: the mirror's reversed winding turns back to counter-clockwise.</summary>
    static Matrix4x4 Perspective(float fov, float aspect, float near, float far)
    {
        float f = 1 / MathF.Tan(fov / 2) / Margin;   // a little wider than the picture, so the distorted lookups near its edge still hit the image
        return new Matrix4x4(
            -f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, 2 * far * near / (near - far), 0);
    }

    /// <summary>
    /// Replaces the projection's near plane by <paramref name="plane"/> (world space, kept side positive), after Lengyel's
    /// oblique near-plane clipping: only the third clip row changes, so x and y stay as they were.
    /// </summary>
    static Matrix4x4 Oblique(Matrix4x4 p, Matrix4x4 view, Vector4 plane)
    {
        if (!Matrix4x4.Invert(view, out var viewInverse) || !Matrix4x4.Invert(p, out var projectionInverse)) return p;
        var c = Vector4.Transform(plane, Matrix4x4.Transpose(viewInverse));   // the plane in view space
        var q = Vector4.Transform(new Vector4(MathF.Sign(c.X), MathF.Sign(c.Y), 1, 1), projectionInverse);
        var w = new Vector4(p.M14, p.M24, p.M34, p.M44);
        float denominator = Vector4.Dot(c, q);
        if (MathF.Abs(denominator) < 1e-12f) return p;
        var z = c * (2 * Vector4.Dot(w, q) / denominator) - w;
        p.M13 = z.X;
        p.M23 = z.Y;
        p.M33 = z.Z;
        p.M43 = z.W;
        return p;
    }

    /// <summary>The six planes (normals inwards) of an OpenGL-depth matrix; <see cref="WorldCamera.FrustumPlanes"/> assumes depth 0..1.</summary>
    static Vector4[] Frustum(Matrix4x4 m, Vector4 rect)
    {
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        // The sides are those of the rectangle (x0, y0, x1, y1 in clip space) of the picture: x / w >= x0 is x - x0 w >= 0.
        return [c1 - rect.X * c4, rect.Z * c4 - c1, c2 - rect.Y * c4, rect.W * c4 - c2, c4 + c3, c4 - c3];
    }

    /// <summary>
    /// The rectangle (x0, y0, x1, y1) of the reflection's clip space that the water on the screen looks up, or null with no water in view:
    /// a grid of screen points (every 20 pixels or so) whose ray down to the water plane meets ground below the water, each projected
    /// with the reflection's matrix (the water does the same with its own position); padded by the ripples' largest shift of the lookup
    /// (0.04 of the texture, 0.08 in clip space) and a cell.
    /// </summary>
    Vector4? WaterRect(WorldCamera camera, float aspect)
    {
        float plane = WorldWater.Height, he = realEye.Y - plane;
        if (!Matrix4x4.Invert(camera.View, out var inverse)) return new Vector4(-1, -1, 1, 1);
        var view = new Matrix4x4(1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 2 * plane, 0, 1) * camera.View;
        var mapped = view * Perspective(camera.FieldOfView, aspect, 1, 1000);
        float tan = MathF.Tan(camera.FieldOfView / 2), level = plane + WaterRise;
        int nx = Math.Clamp((int)(aspect * 54), 8, 160), ny = 54;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                var dir = Vector3.TransformNormal(new Vector3(((i + 0.5f) / nx * 2 - 1) * tan * aspect, ((j + 0.5f) / ny * 2 - 1) * tan, -1), inverse);
                if (dir.Y > -1e-4f) continue;
                var p = realEye + dir * (he / -dir.Y);
                if (dir.Length() * (he / -dir.Y) > MaxDistance || heights.HeightAt(p.X, p.Z) >= level) continue;
                var c = Vector4.Transform(new Vector4(p, 1), mapped);
                if (c.W <= 1e-3f) continue;
                float x = c.X / c.W, y = c.Y / c.W;
                (x0, y0, x1, y1) = (MathF.Min(x0, x), MathF.Min(y0, y), MathF.Max(x1, x), MathF.Max(y1, y));
            }
        if (x0 > x1) return null;
        float padX = 0.08f + 2f / nx + 0.02f, padY = 0.08f + 2f / ny + 0.02f;
        return new Vector4(Math.Max(x0 - padX, -1), Math.Max(y0 - padY, -1), Math.Min(x1 + padX, 1), Math.Min(y1 + padY, 1));
    }

    /// <summary>How far above the water level ground still holds water: the breakers' run-up on the beach, and the waves (units).</summary>
    public float WaterRise { get; set; } = 25;

    /// <summary>
    /// Meitou: the placed fog volumes along the reflected rays (docs/render-water.md "Fog in the reflection"; the frame loop sets it while the Meitou water
    /// draws and volumes are in view). The game's reflection draws no volumes (queue 82 is past its queue 60), so in fog the mirror showed the open sky
    /// the eye cannot see. Each texel's ray from the mirrored eye crosses the water plane and runs on as the real reflected ray to what the texel
    /// shows; the volumes along that part are laid over the texel (the main fog pass already fogs the part from the eye to the water).
    /// </summary>
    public bool FogVolumes { get; set; }
    LegacyProgram? fogProgram;
    int fogProgramSamples = -1;
    GraphicsPipeline? fogPipeline;

    /// <summary>The fog pass over the reflection: the fog volume functions with the texel's distance rebuilt from the reflection's depth
    /// (<paramref name="multisampled"/>: its first sample) through the inverse of the near slice's mirrored, oblique view-projection.</summary>
    internal static string FogFragment(bool multisampled) => "#version 330 core\n" + AtmosphereShaders.Functions + FogVolumeShaders.Functions + $$"""

        in vec2 vUv;
        out vec4 fragColour;
        uniform {{(multisampled ? "sampler2DMS" : "sampler2D")}} uDepth;
        uniform mat4 uInverse;        // clip (GL depth -1..1) to world: the near slice's mirrored, oblique view-projection inverted
        uniform vec3 uMirroredEye;
        uniform float uWaterY;

        void main()
        {
            float d = texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r;
            bool sky = d >= 1.0;
            // A point of the texel's ray (the world point it shows, or for the sky any point: only the direction counts).
            vec4 h = uInverse * vec4(vUv * 2.0 - 1.0, (sky ? 0.5 : d) * 2.0 - 1.0, 1.0);
            vec3 p = h.xyz / h.w, toP = p - uMirroredEye;
            float total = length(toP);
            vec3 dir = toP / max(total, 1e-6);
            // From under the water the ray climbs to the plane, where it is the real reflected ray from then on.
            if (dir.y < 1e-5) { fragColour = vec4(0.0, 0.0, 0.0, 1.0); return; }
            float tw = (uWaterY - uMirroredEye.y) / dir.y;
            vec3 add;
            float trans;
            fogVolumesAccumulate(uMirroredEye + dir * tw, dir, sky ? 1e9 : max(total - tw, 0.0), add, trans);
            fragColour = vec4(add, trans);
        }
        """;

    void RunFog(CommandList cmd, Matrix4x4 nearViewProjection, Vector3 mirroredEye)
    {
        var depthTexture = msDepth ?? depth;
        if (depthTexture is null || colour is null || !Matrix4x4.Invert(nearViewProjection, out var inverse)) return;
        if (fogProgram is null || fogProgramSamples != samples)
        {
            fogProgram?.Dispose();
            fogProgram = LegacyProgram.Create(Gpu, PostProcessShaders.Vertex, FogFragment(samples > 0), "reflection fog volumes");
            fogProgramSamples = samples;
            // The fog volumes pass's blend: scene Ã— transmittance (alpha) + the fog's light, colour only.
            var state = new DrawState(CullModeFlags.None, GlConventions.FrontFace(FrontFaceDirection.Ccw), false, false, CompareOp.LessOrEqual, false, 0, 0,
                new BlendState(true, BlendFactor.One, BlendFactor.SrcAlpha), DrawState.Rgb, PolygonMode.Fill, false, false);
            fogPipeline = Gpu.Pipelines.Get(state.Pipeline(fogProgram.Program, fogProgram.VertexLayout([]), PrimitiveTopology.TriangleList,
                new AttachmentFormats(colour.Desc.Format, Format.Undefined), fogProgram.Name));
        }
        var p = fogProgram;
        p.Bind(p.Sampler("uDepth"), new SampledTexture(Gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0)), depthTexture.View(), depthTexture.Image));
        p.Set(p.Uniform("uInverse"), in inverse);
        p.Set(p.Uniform("uMirroredEye"), mirroredEye.X, mirroredEye.Y, mirroredEye.Z);
        p.Set(p.Uniform("uWaterY"), WorldWater.Height);
        p.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        cmd.Barrier(BarrierBatch.Full);
        cmd.BeginRendering(new RenderingDesc(PassTargets.Of(colour, null).Colour, default, width, height));
        cmd.SetViewport(new Viewport(0, 0, width, height, 0, 1));
        cmd.SetScissor(new Rect2D(default, new Extent2D((uint)width, (uint)height)));
        cmd.SetRaster(CullModeFlags.None, GlConventions.FrontFace(FrontFaceDirection.Ccw));
        cmd.SetDepth(false, false, CompareOp.LessOrEqual);
        cmd.SetDepthBias(false, 0, 0);
        cmd.BindPipeline(fogPipeline!);
        p.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
    }

    public void Dispose()
    {
        Free();
        fogProgram?.Dispose();
    }
}
