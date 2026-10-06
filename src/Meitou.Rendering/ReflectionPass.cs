using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>
/// Planar reflection of the scene for the water (docs/formats/terrain.md, "Water"; the game does the same with a 512²
/// render target drawn by a mirrored camera). The sky, terrain and, through <see cref="SceneDraw"/>, objects (foliage
/// later) are drawn mirrored about the water plane into a half-resolution texture; <see cref="WaterRenderer"/> samples it
/// with the same view-projection, so no flip of the texture is needed. The mirror reverses triangle winding, which a
/// mirrored (negated) clip X puts right again, so no renderer has to change its culling. Nothing below the water leaks
/// into the picture: the projection's near plane is replaced by the water plane (oblique near-plane clipping).
/// </summary>
public sealed unsafe class ReflectionPass : IDisposable
{
    /// <summary>Draws the scene's other geometry (objects, foliage) with the given matrix, eye and frustum planes of one depth slice.</summary>
    public delegate void SceneDraw(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum);

    /// <summary>Size of the texture relative to the picture (the game's is a fixed 512²).</summary>
    public const float Scale = 0.5f;
    /// <summary>The reflection covers this much more than the picture in each direction (as a factor of the field of view tangent).</summary>
    public const float Margin = 1.12f;
    /// <summary>Samples per texel of the reflection (1 = off). Resolved into the plain texture the water samples.</summary>
    public const int Samples = 4;

    readonly IGl gl;
    /// <summary>The native GPU API next to <c>gl</c> (docs/renderer-native.md 7.1 step 8); ports use it instead of looking it up.</summary>
    public GpuContext Gpu { get; }
    readonly uint[] queries = new uint[4];   // two slots of (start, end) timestamps
    readonly bool[] pending = new bool[2];
    readonly WorldRenderOptions options = new();
    uint fbo, colour, depth;
    uint msFbo, msColour, msDepth;   // multisampled twin the scene is drawn into, resolved into <colour>
    int samples;
    int width, height, slot, skipped, age;
    bool hasImage;
    Vector3 lastEye;
    float lastFov;
    Matrix4x4 lastView;
    readonly List<double> gpuSamples = [];
    readonly List<double> cpuSamples = [];

    public ReflectionPass(IGl gl, GpuContext gpu)
    {
        this.gl = gl;
        Gpu = gpu;
        // Tuning knobs for experiments (MEITOU_REFL_OBJECTS, _FOLIAGE, _LOD, _AGE); the defaults are what the viewer ships with.
        static float Env(string n, float d) => float.TryParse(Environment.GetEnvironmentVariable("MEITOU_REFL_" + n), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
        ObjectDistance = Env("OBJECTS", ObjectDistance);
        FoliageDistance = Env("FOLIAGE", FoliageDistance);
        ObjectLodBias = Env("LOD", ObjectLodBias);
        MaxAge = (int)Env("AGE", MaxAge);
        TerrainLodScale = Env("TLOD", TerrainLodScale);
        for (int i = 0; i < queries.Length; i++) queries[i] = gl.GenQuery();
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
    /// <summary>The game's <c>reflection range</c>: the far clip is the haze distance (the game's view distance × 10) times this; the frame loop sets <see cref="MaxDistance"/> from it.</summary>
    public float Range { get; set; } = DefaultRange;
    /// <summary>Furthest distance reflected: mountains beyond it are not mirrored (the sea there is mostly haze anyway).</summary>
    public float MaxDistance { get; set; } = 150000;
    /// <summary>Objects (buildings, features) further than this from the eye are left out of the reflection.</summary>
    public float ObjectDistance { get; set; } = 3000;
    /// <summary>Beyond this distance the mirrored terrain takes the cheap ground colour instead of the biome textures.</summary>
    public float MaterialDistance { get; set; } = 6000;
    /// <summary>Foliage (trees, bushes, rocks; no grass) further than this is left out of the reflection (the game's layers reach 4 times as far in the picture).</summary>
    public float FoliageDistance { get; set; } = 3000;
    /// <summary>Multiplies the distance the objects' LOD level is chosen by (above 1: coarser levels sooner; the half-resolution, rippled image cannot show the detail).</summary>
    public float ObjectLodBias { get; set; } = 3;
    /// <summary>Frames a finished reflection may be reused when the camera has hardly moved (0 = draw every frame).</summary>
    public int MaxAge { get; set; } = 3;
    /// <summary>The framebuffer (viewport: the whole picture) to go back to afterwards, when the caller knows it; otherwise it is queried.</summary>
    public uint? RestoreFramebuffer { get; set; }
    /// <summary>Multiplies the terrain LOD's pixel scale for the mirrored terrain (its target is half the picture's size, so 0.5 measures the error in its own pixels; below: coarser).</summary>
    public float TerrainLodScale { get; set; } = 0.5f;

    /// <summary>Whether this frame has a reflection to sample (not when the eye is under the water).</summary>
    public bool Valid { get; private set; }
    /// <summary>Maps a point on the water to the texture: clip.xy / clip.w * 0.5 + 0.5.</summary>
    public Matrix4x4 ViewProjection { get; private set; }
    public uint Texture => colour;
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
        if (w == width && h == height && fbo != 0) return;
        Free();
        (width, height) = (w, h);
        colour = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, colour);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.HalfFloat, (void*)0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        depth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)w, (uint)h);
        fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colour, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new InvalidOperationException("Reflection framebuffer incomplete.");

        // The picture is drawn multisampled and resolved: the mirrored shoreline, fences and rooflines are hard edges, and
        // without it each texel of the half-resolution image is a visible stair step that the wave distortion then smears.
        gl.GetInteger(GLEnum.MaxSamples, out int maxSamples);
        samples = Math.Min(Samples, maxSamples);
        if (samples <= 1) { samples = 0; return; }
        msColour = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msColour);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, InternalFormat.Rgba16f, (uint)w, (uint)h);
        msDepth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msDepth);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, InternalFormat.DepthComponent24, (uint)w, (uint)h);
        msFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, msFbo);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, msColour);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, msDepth);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new InvalidOperationException("Reflection multisampled framebuffer incomplete.");
    }

    void Free()
    {
        if (fbo != 0) { gl.DeleteFramebuffer(fbo); gl.DeleteTexture(colour); gl.DeleteRenderbuffer(depth); fbo = 0; }
        if (msFbo != 0) { gl.DeleteFramebuffer(msFbo); gl.DeleteRenderbuffer(msColour); gl.DeleteRenderbuffer(msDepth); msFbo = 0; }
    }

    /// <summary>Collects finished GPU timings (<paramref name="wait"/>: block for them, for one-off measurements).</summary>
    public void Poll(bool wait = false)
    {
        for (int s = 0; s < 2; s++)
        {
            if (!pending[s]) continue;
            int available = 0;
            if (wait) available = 1;
            else gl.GetQueryObject(queries[s * 2 + 1], QueryObjectParameterName.ResultAvailable, out available);
            if (available == 0) continue;
            gl.GetQueryObject(queries[s * 2], QueryObjectParameterName.Result, out ulong start);
            gl.GetQueryObject(queries[s * 2 + 1], QueryObjectParameterName.Result, out ulong end);
            GpuMs = (end - start) / 1e6;
            gpuSamples.Add(GpuMs);
            pending[s] = false;
        }
    }

    /// <summary>
    /// Draws the mirrored scene into the texture. Call after the haze and view distance of the frame are set and before the
    /// main pass; the framebuffer and viewport that were bound are restored.
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

        // State queries make the driver wait for its own thread, so the caller that knows where the picture is drawn says so.
        int drawFbo = 0, readFbo = 0;
        int* viewport = stackalloc int[4];
        if (RestoreFramebuffer is { } known) { drawFbo = readFbo = (int)known; viewport[0] = viewport[1] = 0; viewport[2] = fullWidth; viewport[3] = fullHeight; }
        else
        {
            gl.GetInteger(GetPName.DrawFramebufferBinding, out drawFbo);
            gl.GetInteger(GetPName.ReadFramebufferBinding, out readFbo);
            gl.GetInteger(GetPName.Viewport, viewport);
        }
        Resize(Math.Max((int)(fullWidth * Scale), 64), Math.Max((int)(fullHeight * Scale), 64));
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, msFbo != 0 ? msFbo : fbo);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        int timed = -1;
        if (!pending[slot]) { timed = slot; gl.QueryCounter(queries[timed * 2], QueryCounterTarget.Timestamp); }

        // The world mirrored about the water, seen from the real eye: the same as the unmirrored world seen from the
        // mirrored eye (which is also where distances for LOD, objects and haze are measured from).
        var mirror = new Matrix4x4(1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1, 0, 0, 2 * plane, 0, 1);
        var view = mirror * camera.View;
        var mirroredEye = new Vector3(eye.X, 2 * plane - eye.Y, eye.Z);
        float aspect = fullWidth / (float)fullHeight;
        // Keep a metre below the surface too, so the waves' troughs do not show a gap at the shore.
        var clip = new Vector4(0, 1, 0, -(plane - 1));

        gl.ColorMask(true, true, true, true);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.ScissorTest);
        // The native host (docs/renderer-native.md 4.5): the multisampled target's rendering instance is opened here, cleared by its load ops,
        // and the sky, terrain, objects and foliage record into it through BeginNativeInPass. The GL framebuffer binding, viewport and state stay
        // what the guests read (CurrentTargets, CurrentState, GetInteger(Samples)).
        var interop = Gpu.Interop!;
        var cmd = interop.BeginNative("reflection");
        var target = interop.CurrentTargets();
        interop.BeginHostPass(cmd);
        // Wave 4 (docs/renderer-native.md 6): the guests' segments are secondaries, recorded on the job threads when the pass ends.
        bool secondaries = Recording.Secondaries;
        cmd.BeginRendering(new RenderingDesc(
            target.Colour with { Load = AttachmentLoadOp.Clear, Clear = new ClearValue(new ClearColorValue(light.FogColour.X, light.FogColour.Y, light.FogColour.Z, 1)) },
            target.Depth with { Load = AttachmentLoadOp.Clear, Clear = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0)) },
            target.Width, target.Height), secondaries);
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

        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        terrain.BeginFrame();
        bool first = true;
        Matrix4x4 mapped = default;
        foreach (var (near, far0) in camera.Slices())
        {
            float far = Math.Min(far0, MaxDistance);
            if (far <= near * 1.5f) continue;
            if (!first) Gpu.ClearDepth(cmd, 1f, new Rect2D(new Offset2D(0, 0), new Extent2D((uint)target.Width, (uint)target.Height)));
            first = false;
            var projection = Oblique(Perspective(camera.FieldOfView, aspect, near, far), view, clip);
            var viewProjection = view * projection;
            mapped = view * Perspective(camera.FieldOfView, aspect, near, far);
            var frustum = Frustum(viewProjection);
            terrain.Draw(viewProjection, mirroredEye, frustum, options, light, plane - 1, secondary: true);
            Lap(1);
            if (near <= camera.Near && render.Objects) drawObjects?.Invoke(viewProjection, mirroredEye, frustum);
        }
        (DrawnChunks, DrawnTriangles) = (terrain.DrawnChunks, terrain.DrawnTriangles);
        ViewProjection = mapped;   // x and y do not depend on the near plane
        Valid = hasImage = !first;

        if (Gpu.Frame.Parallel.Open) Gpu.Frame.Parallel.End();
        cmd.EndRendering();
        if (msFbo != 0)
        {
            // The multisampled colour into the texture the water samples (a resolve, as the framebuffer blit was), between full barriers.
            cmd.Barrier(BarrierBatch.Full);
            cmd.Resolve(target.Colour.Image, interop.Texture(colour).Image, width, height);
        }
        interop.EndHostPass(cmd);
        interop.EndNative(cmd);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)drawFbo);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)readFbo);
        gl.Viewport(viewport[0], viewport[1], (uint)viewport[2], (uint)viewport[3]);
        if (timed >= 0)
        {
            gl.QueryCounter(queries[timed * 2 + 1], QueryCounterTarget.Timestamp);
            pending[timed] = true;
            slot = (slot + 1) % 2;
        }
        CpuMs = watch.Elapsed.TotalMilliseconds;
        cpuSamples.Add(CpuMs);
    }

    /// <summary>
    /// Whether last frame's image still serves: the water samples it with the matrix it was drawn with, so the only error is
    /// the parallax of the eye having moved, and a turn that brings new sky or ground in at the picture's edge.
    /// </summary>
    bool CanReuse(WorldCamera camera, int fullWidth, int fullHeight)
    {
        if (age >= MaxAge || !hasImage || fbo == 0 || msFbo == 0 && samples != 0) return false;
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
        Poll(wait: true);
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

    /// <summary>OpenGL perspective (depth −1..1) with clip X negated: the mirror's reversed winding turns back to counter-clockwise.</summary>
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
    static Vector4[] Frustum(Matrix4x4 m)
    {
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        return [c4 + c1, c4 - c1, c4 + c2, c4 - c2, c4 + c3, c4 - c3];
    }

    public void Dispose()
    {
        Free();
        foreach (var q in queries) gl.DeleteQuery(q);
    }
}
