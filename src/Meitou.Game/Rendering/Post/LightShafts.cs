using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Sky;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Target2D = Meitou.Game.Rendering.Post.PostProcess.Target2D;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The Meitou light shafts (the <c>shafts</c> switch; <see cref="LightShaftShaders"/>, docs/render-shafts.md): after the scene and the GI resolve,
/// before the fog volumes, a froxel grid's shadow samples go into an atlas (a full-screen pass), their sums front to back (and the air layer's
/// scatter) into two more (one compute kernel, <see cref="LightShaftShaders.Scan"/>, or two fragment passes), and the apply pass darkens the haze
/// over the scene colour. <see cref="PostProcess"/> owns the targets' frame and the camera; this keeps the grid, its atlases and its history.
/// </summary>
sealed unsafe class LightShafts(PostProcess post)
{
    #region Passes

    /// <summary>
    /// The grid's uniforms every shaft pass has (<c>LightShaftShaders.Common</c>).
    /// </summary>
    abstract class ShaftPass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// The grid, its slices, the eye, the view's axes and half-fov tangents.
        /// </summary>
        public readonly UniformHandle Grid, Range, Eye, Right, Up, Back, Tan;

        /// <summary>
        /// Compiles the shared vertex shader with <paramref name="fragment"/> and resolves the grid's shared uniforms.
        /// </summary>
        protected ShaftPass(GpuContext gpu, string fragment, string name) : base(gpu, fragment, name)
        {
            (Grid, Range, Eye, Tan) = (P.Uniform("uShaftGrid"), P.Uniform("uShaftRange"), P.Uniform("uShaftEye"), P.Uniform("uShaftTan"));
            (Right, Up, Back) = (P.Uniform("uShaftRight"), P.Uniform("uShaftUp"), P.Uniform("uShaftBack"));
        }
    }

    /// <summary>
    /// The shadow samples of each cell into the injected atlas (<see cref="LightShaftShaders.Inject"/>).
    /// </summary>
    sealed class ShaftInjectPass : ShaftPass
    {
        /// <summary>
        /// The shadow samples per cell.
        /// </summary>
        public readonly UniformHandle Samples;

        /// <summary>
        /// Compiles the program (<paramref name="clouds"/>: the variant whose samples read the cloud shadows) and resolves its handles.
        /// </summary>
        public ShaftInjectPass(GpuContext gpu, bool clouds)
            : base(gpu, clouds ? LightShaftShaders.InjectClouded : LightShaftShaders.Inject, "post shafts inject") =>
            Samples = P.Uniform("uShaftSamples");
    }

    /// <summary>
    /// The temporal filter of the injected atlas (<see cref="LightShaftShaders.Resolve"/>).
    /// </summary>
    sealed class ShaftResolvePass : ShaftPass
    {
        /// <summary>
        /// This frame's raw atlas and last frame's filtered one.
        /// </summary>
        public readonly SamplerSlot Current, History;

        /// <summary>
        /// Last frame's slices and camera, and the share of this frame's samples.
        /// </summary>
        public readonly UniformHandle PrevRange, PrevEye, PrevRight, PrevUp, PrevBack, PrevTan, Blend;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public ShaftResolvePass(GpuContext gpu) : base(gpu, LightShaftShaders.Resolve, "post shafts resolve")
        {
            (Current, History) = (P.Sampler("uShaftCurrent"), P.Sampler("uShaftHistory"));
            (PrevRange, PrevEye, PrevRight, PrevUp) = (P.Uniform("uShaftPrevRange"), P.Uniform("uShaftPrevEye"), P.Uniform("uShaftPrevRight"),
                P.Uniform("uShaftPrevUp"));
            (PrevBack, PrevTan, Blend) = (P.Uniform("uShaftPrevBack"), P.Uniform("uShaftPrevTan"), P.Uniform("uShaftBlend"));
        }
    }

    /// <summary>
    /// The sun's share summed front to back into the light atlas (<see cref="LightShaftShaders.Integrate"/>).
    /// </summary>
    sealed class ShaftIntegratePass : ShaftPass
    {
        /// <summary>
        /// The injected atlas.
        /// </summary>
        public readonly SamplerSlot Injected;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public ShaftIntegratePass(GpuContext gpu) : base(gpu,
            LightShaftShaders.Integrate, "post shafts integrate") => Injected = P.Sampler("uShaftInjected");
    }

    /// <summary>
    /// The haze darkened over the scene colour (<see cref="LightShaftShaders.Apply"/>).
    /// </summary>
    sealed class ShaftApplyPass : ShaftPass
    {
        /// <summary>
        /// The scene depth, the light atlas and the scatter atlas.
        /// </summary>
        public readonly SamplerSlot NearDepth, Light, Scatter;

        /// <summary>
        /// The depth planes, the water height, the atlas size, the darkening and sky share, and the air layer.
        /// </summary>
        public readonly UniformHandle NearPlanes, WaterY, Atlas, Params, Air;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public ShaftApplyPass(GpuContext gpu) : base(gpu, LightShaftShaders.Apply, "post shafts apply")
        {
            (NearDepth, Light, Scatter) = (P.Sampler("uNearDepth"), P.Sampler("uShaftLight"), P.Sampler("uShaftScatter"));
            Air = P.Uniform("uShaftAir");
            (NearPlanes, WaterY, Atlas, Params) = (P.Uniform("uNearPlanes"), P.Uniform("uWaterY"), P.Uniform("uShaftAtlas"),
                P.Uniform("uShaftParams"));
        }
    }

    /// <summary>
    /// The air layer (<see cref="LightShaftShaders.Scatter"/>).
    /// </summary>
    sealed class ShaftScatterPass : ShaftPass
    {
        /// <summary>
        /// The injected atlas.
        /// </summary>
        public readonly SamplerSlot Injected;

        /// <summary>
        /// The air layer's density, ground, scale height and phase.
        /// </summary>
        public readonly UniformHandle Air;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public ShaftScatterPass(GpuContext gpu) : base(gpu, LightShaftShaders.Scatter, "post shafts scatter") => (Injected,
            Air) = (P.Sampler("uShaftInjected"), P.Uniform("uShaftAir"));
    }

    #endregion

    #region Grid and atlases

    /// <summary>
    /// <c>MEITOU_SHAFT_STAMPS=1</c>: GPU timestamps between the shaft passes.
    /// </summary>
    static readonly bool ShaftStamps = Environment.GetEnvironmentVariable("MEITOU_SHAFT_STAMPS") == "1";

    /// <summary>
    /// The share of this frame's sun share in the filtered one (0.1: about ten frames of jitter averaged).
    /// </summary>
    public const float TemporalBlend = 0.1f;

    /// <summary>
    /// The injection passes, by whether the samples read the cloud shadows (the gihybrid switch's clouds part), each made when first used.
    /// </summary>
    readonly ShaftInjectPass?[] _injects = new ShaftInjectPass?[2];

    /// <summary>
    /// This frame's injection pass (set by <see cref="Run"/>).
    /// </summary>
    ShaftInjectPass? _inject;

    /// <summary>
    /// The temporal resolve, made the first time a temporal upscaler runs.
    /// </summary>
    ShaftResolvePass? _resolve;

    /// <summary>
    /// The fragment integrate pass (without the scan kernel).
    /// </summary>
    ShaftIntegratePass? _integrate;

    /// <summary>
    /// The fragment air layer pass (without the scan kernel).
    /// </summary>
    ShaftScatterPass? _scatterPass;

    /// <summary>
    /// The apply pass (without <see cref="PostProcess.MergeLayers"/>).
    /// </summary>
    ShaftApplyPass? _apply;

    /// <summary>
    /// Whether <see cref="History"/> holds last frame's filtered atlas.
    /// </summary>
    bool _historyValid;

    /// <summary>
    /// The frame <see cref="History"/> was filtered in.
    /// </summary>
    long _historyFrame = -10;

    /// <summary>
    /// Last frame's slices and camera, for the temporal resolve.
    /// </summary>
    (Vector4 Range, Vector3 Eye, Vector3 Right, Vector3 Up, Vector3 Back, Vector2 Tan) _previous;

    /// <summary>
    /// This frame's grid for the fog volumes pass (<see cref="LightShaftShaders.FogVolumeShadow"/>): its uniforms and the darkening (0: no shafts
    /// this frame).
    /// </summary>
    public (Vector4 Grid, Vector4 Range, Vector2 Tan, float Darkening) Frame;

    /// <summary>
    /// With <see cref="PostProcess.MergeLayers"/>: the shafts ran this frame and their apply waits for the merged pass.
    /// </summary>
    public bool Pending;

    /// <summary>
    /// The pending apply's air layer and parameters (<see cref="Pending"/>).
    /// </summary>
    public (Vector4 Air, Vector4 Params) Args;

    /// <summary>
    /// The injected atlas: the grid's shadow samples (with a temporal upscaler, filtered over frames).
    /// </summary>
    public Target2D? Injected { get; private set; }

    /// <summary>
    /// The light atlas: the sun's share summed front to back.
    /// </summary>
    public Target2D? Light { get; private set; }

    /// <summary>
    /// The air layer's scatter atlas.
    /// </summary>
    public Target2D? Scatter { get; private set; }

    /// <summary>
    /// With a temporal upscaler: the injection's raw output (<see cref="LightShaftShaders.Resolve"/> filters it into <see cref="Injected"/>).
    /// </summary>
    public Target2D? Raw { get; private set; }

    /// <summary>
    /// With a temporal upscaler: last frame's filtered atlas (swapped with <see cref="Injected"/> each frame).
    /// </summary>
    public Target2D? History { get; private set; }

    /// <summary>
    /// What the light shafts did in the last frame (the stats line).
    /// </summary>
    public string Describe { get; private set; } = "off";

    /// <summary>
    /// The grid's uniforms of one frame: the grid, its slices, the half-fov tangent and aspect, the view rotation and the eye.
    /// </summary>
    readonly record struct GridView(Vector4 Grid, Vector4 Range, float TanY, float Aspect, Matrix4x4 R, Vector3 Eye);

    /// <summary>
    /// The atlases are gone (the post targets were freed): made again by the next <see cref="Run"/>.
    /// </summary>
    public void Free()
    {
        Injected = Light = Scatter = Raw = History = null;
        _historyValid = false;
    }

    /// <summary>
    /// The next frame starts without a history (a cut).
    /// </summary>
    public void ResetHistory() => _historyValid = false;

    /// <summary>
    /// Darkens the haze where the sun is shadowed along the view ray (call after the GI resolve, before <see cref="PostProcess.RunFogVolumes"/>).
    /// <paramref name="darkening"/>: how much of the shadowed haze goes (the strength times the sun's share of the light; 0 skips the passes);
    /// <paramref name="far"/>: where the grid ends (beyond, the share stays the last slice's). Does nothing with the switch off.
    /// </summary>
    public void Run(float darkening, float far, float groundY, float airByTime = 1)
    {
        var o = post.Options;
        Frame.Darkening = 0;
        Describe = !o.LightShafts ? "off" : $"darkening {darkening:0.###} (none: no passes)";
        if (!o.LightShafts || darkening <= 0 || post.SceneColourTarget is null || !post.HaveScene) return;

        var (across, down, slices, aw, ah) = Layout();
        Describe = $"darkening {darkening:0.###}, grid {across} x {down} x {slices} ({o.ShaftSamples} samples), " +
            $"{PostOptions.ShaftNear:0} to {far:0} units, air {(o.ShaftAir > 0
                ? $"x{o.ShaftAir:0.##} x{airByTime:0.##} by the hour from {groundY:0} " +
                    $"(scale height {PostOptions.ShaftAirHeight:0}, g {PostOptions.ShaftAirPhase:0.##})"
                : "off")}";
        EnsureAtlases(aw, ah);
        var gpu = post.Gpu;
        bool clouds = o.GiCloudShadowing;
        _inject = _injects[clouds ? 1 : 0] ??= new ShaftInjectPass(gpu, clouds);
        _integrate ??= new ShaftIntegratePass(gpu);
        _apply ??= new ShaftApplyPass(gpu);
        _scatterPass ??= new ShaftScatterPass(gpu);

        // The air layer: density at the ground under the eye, its scale height, the phase's anisotropy (0 density: no layer, no pass).
        var air = new Vector4(MathF.Max(o.ShaftAir, 0) * MathF.Max(airByTime, 0) * PostOptions.ShaftAirDensity, groundY,
            MathF.Max(PostOptions.ShaftAirHeight, 1), Math.Clamp(PostOptions.ShaftAirPhase, 0, 0.95f));

        float near = Math.Clamp(PostOptions.ShaftNear, 1, far * 0.5f);
        var grid = new Vector4(across, down, slices, LightShaftShaders.AtlasColumns);

        // The jitter moves every frame only for a temporal upscaler to average; without one it stays put (a still pattern, not a shimmer).
        var range = new Vector4(near, far, MathF.Log(far / near), post.Temporal ? gpu.Frame.Number % 4096 : 0);
        float tanY = MathF.Tan(post.FovNow * 0.5f);
        var view = new GridView(grid, range, tanY, post.AspectNow, post.ViewRotation, post.EyeNow);

        Inject(in view, aw, ah);
        Integrate(in view, air);

        var parameters = new Vector4(Math.Clamp(darkening, 0, 1), PostOptions.ShaftSky, 0, 0);
        Frame = (grid, range, new Vector2(tanY * view.Aspect, tanY), Math.Clamp(darkening, 0, 1));
        if (post.MergeLayers)
        {
            // The apply is the merged pass's (ApplySsao).
            (Pending, Args) = (true, (air, parameters));
            post.Stamp("shafts");
            post.CloseSegment();
            return;
        }
        Apply(in view, air, parameters, aw, ah);
    }

    /// <summary>
    /// Every shaft pass's grid and camera uniforms.
    /// </summary>
    static void Common(ShaftPass s, in GridView v)
    {
        var r = v.R;
        s.P.Set(s.Grid, v.Grid);
        s.P.Set(s.Range, v.Range);
        s.P.Set(s.Eye, v.Eye);
        s.P.Set(s.Tan, v.TanY * v.Aspect, v.TanY);
        s.P.Set(s.Right, r.M11, r.M21, r.M31);
        s.P.Set(s.Up, r.M12, r.M22, r.M32);
        s.P.Set(s.Back, r.M13, r.M23, r.M33);
    }

    /// <summary>
    /// The grid's cells across, down (from the render size's aspect) and slices this frame, and the atlas's size.
    /// </summary>
    (int Across, int Down, int Slices, int AtlasWidth, int AtlasHeight) Layout()
    {
        var o = post.Options;
        int across = Math.Clamp(o.ShaftCells, 8, 1024), slices = Math.Clamp(o.ShaftSlices, 8, 256);
        int down = Math.Max(1, (int)MathF.Round(across * post.RenderHeight / (float)Math.Max(post.RenderWidth, 1)));
        int columns = LightShaftShaders.AtlasColumns, rows = (slices + columns - 1) / columns;
        return (across, down, slices, across * columns, down * rows);
    }

    /// <summary>
    /// The three atlases at the grid's size, made again (the history with them) when it changed.
    /// </summary>
    void EnsureAtlases(int aw, int ah)
    {
        if (Injected is not null && Injected.Width == aw && Injected.Height == ah) return;

        // released after the frames in flight
        foreach (var t in new[] { Injected, Light, Scatter, Raw, History }.OfType<Target2D>()) t.Texture.Dispose();
        Raw = History = null;
        _historyValid = false;
        using var batch = post.Gpu.Uploads.Begin();

        // Linear: the fog volumes pass reads the sun's share (blue) between cells.
        Injected = post.Make(batch, aw, ah, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post shafts injected");
        Light = post.Make(batch, aw, ah, InternalFormat.R32F, TextureMinFilter.Linear, "post shafts light");
        Scatter = post.Make(batch, aw, ah, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post shafts scatter");
    }

    #endregion

    #region Inject

    /// <summary>
    /// The grid's shadow samples into the injected atlas.
    /// </summary>
    void Inject(in GridView view, int aw, int ah)
    {
        var i = _inject!;
        Common(i, in view);
        i.P.Set(i.Samples, Math.Clamp(post.Options.ShaftSamples, 1, 16));
        i.P.ApplyGlobals();   // the atmosphere's uniforms and the shadow receivers, through the frame globals
        if (post.Temporal) InjectTemporal(in view, aw, ah);
        else
        {
            post.Draw(i.P, Injected!);
            _historyValid = false;
        }
        if (ShaftStamps) post.Stamp("shafts-inject");
    }

    /// <summary>
    /// The jittered samples averaged over frames in the grid itself (<see cref="LightShaftShaders.Resolve"/>), not left to the upscaler: the
    /// injection into its raw atlas, then filtered with last frame's into <see cref="Injected"/> (the two swapped each frame).
    /// </summary>
    void InjectTemporal(in GridView view, int aw, int ah)
    {
        var gpu = post.Gpu;
        if (Raw is null || History is null)
        {
            using var batch = gpu.Uploads.Begin();
            Raw = post.Make(batch, aw, ah, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post shafts raw");
            History = post.Make(batch, aw, ah, InternalFormat.Rgba16F, TextureMinFilter.Linear, "post shafts history");
            _historyValid = false;
        }
        post.Draw(_inject!.P, Raw);

        // last frame's filtered atlas becomes the history; this frame's goes over the older one
        (History, Injected) = (Injected, History);
        var v = _resolve ??= new ShaftResolvePass(gpu);
        Common(v, in view);
        var prev = _previous;
        bool continuous = _historyValid && gpu.Frame.Number - _historyFrame <= 2 && Vector3.Distance(prev.Eye, view.Eye) < 2000;
        post.Bind(v.P, v.Current, Raw);
        post.Bind(v.P, v.History, continuous ? History : Raw);   // never an uninitialised history
        v.P.Set(v.PrevRange, prev.Range);
        v.P.Set(v.PrevEye, prev.Eye);
        v.P.Set(v.PrevRight, prev.Right);
        v.P.Set(v.PrevUp, prev.Up);
        v.P.Set(v.PrevBack, prev.Back);
        v.P.Set(v.PrevTan, prev.Tan);
        v.P.Set(v.Blend, continuous ? TemporalBlend : 1f);
        post.Draw(v.P, Injected!);

        _historyValid = true;
        _historyFrame = gpu.Frame.Number;
        var r = view.R;
        _previous = (view.Range, view.Eye, new Vector3(r.M11, r.M21, r.M31), new Vector3(r.M12, r.M22, r.M32), new Vector3(r.M13, r.M23, r.M33),
            new Vector2(view.TanY * view.Aspect, view.TanY));
    }

    #endregion

    #region Integrate and apply

    /// <summary>
    /// The light atlas (and with the air layer on, the scatter atlas) from the injected one: the scan kernel where it can run, else two fragment
    /// passes.
    /// </summary>
    void Integrate(in GridView view, Vector4 air)
    {
        if (Scan && ScanSupported)
        {
            ScanRun(in view, air, air.X > 0);
            if (ShaftStamps) post.Stamp("shafts-scan");
            return;
        }

        var g = _integrate!;
        Common(g, in view);
        post.Bind(g.P, g.Injected, Injected);
        post.Draw(g.P, Light!);
        if (ShaftStamps) post.Stamp("shafts-integrate");
        if (!(air.X > 0)) return;

        var sc = _scatterPass!;
        Common(sc, in view);
        post.Bind(sc.P, sc.Injected, Injected);
        sc.P.Set(sc.Air, air);
        sc.P.ApplyGlobals();   // the sun and the sky's ambient
        post.Draw(sc.P, Scatter!);
        if (ShaftStamps) post.Stamp("shafts-scatter");
    }

    /// <summary>
    /// The apply pass on its own (without <see cref="PostProcess.MergeLayers"/>): the haze darkened over the scene colour.
    /// </summary>
    void Apply(in GridView view, Vector4 air, Vector4 parameters, int aw, int ah)
    {
        var a = _apply!;
        var planes = post.Planes;
        Common(a, in view);
        post.Bind(a.P, a.NearDepth, post.SceneDepthTarget);
        post.Bind(a.P, a.Light, Light);
        post.Bind(a.P, a.Scatter, Scatter);
        a.P.Set(a.Air, air);
        a.P.Set(a.NearPlanes, planes.X, planes.Y);
        a.P.Set(a.WaterY, post.WaterHeight ?? float.MinValue);
        a.P.Set(a.Atlas, aw, ah);
        a.P.Set(a.Params, parameters);
        a.P.ApplyGlobals();

        var scene = post.SceneColourTarget!;
        int w = post.RenderWidth;
        int h = post.RenderHeight;
        post.Draw(a.P, scene.Attachment, scene.Format, w, h, w, h, PostProcess.FogState);
        post.Stamp("shafts");
        post.CloseSegment();
    }

    #endregion

    #region Scan kernel

    /// <summary>
    /// The scan kernel's program, made the first time it runs.
    /// </summary>
    GpuProgram? _scanProgram;

    /// <summary>
    /// The scan kernel's pipeline, made with its program.
    /// </summary>
    GpuComputePipeline? _scanPipeline;

    /// <summary>
    /// The integrate and scatter passes as one compute kernel walking each cell column's slices once (<see cref="LightShaftShaders.Scan"/>; <c>--ab
    /// shafts-scan</c>). Off, or where the device cannot write the atlases as storage images, the two fragment passes run.
    /// </summary>
    public bool Scan { get; set; } = true;

    /// <summary>
    /// Whether the device can write the atlases as storage images (the scan kernel can run).
    /// </summary>
    public bool ScanSupported => post.SupportsStorage(InternalFormat.R32F) && post.SupportsStorage(InternalFormat.Rgba16F);

    /// <summary>
    /// The scan kernel's uniform block (std140: every member takes a 16-byte slot).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ShaftScanParams
    {
        /// <summary>
        /// The block's members in order, each a vec4.
        /// </summary>
        public Vector4 Grid, Range, Eye, Right, Up, Back, Tan, Air, AtmoParams, AtmoSimple, AtmoHaze, AtmoAltitude, AtmoFog, AtmoSun, SunLight,
            AtmoMaps;
    }

    /// <summary>
    /// A frame global's value as <typeparamref name="T"/> (default when it is not set).
    /// </summary>
    static T ReadGlobal<T>(FrameGlobals g, string name) where T : unmanaged
    {
        T value = default;
        g.UniformValue(name)?.TryRead(MemoryMarshal.AsBytes(new Span<T>(ref value)));
        return value;
    }

    /// <summary>
    /// Integrate and scatter in one dispatch: the light atlas and, with the air layer on, the scatter atlas, from the injected atlas.
    /// </summary>
    void ScanRun(in GridView view, Vector4 air, bool withAir)
    {
        var gpu = post.Gpu;
        if (_scanProgram is null)
        {
            _scanProgram = gpu.Shaders.Compute(LightShaftShaders.Scan, "post shafts scan");
            _scanPipeline = gpu.Pipelines.Get(new ComputePipelineDesc(_scanProgram!.Value, "post shafts scan"));
        }
        var g = gpu.Globals;
        var p = ScanParams(g, in view, withAir ? air : default);
        var data = gpu.Frame.Constants.Write(new ReadOnlySpan<ShaftScanParams>(in p), 256);
        var cube = g.Texture("uAtmoIrradiance")?.Invoke() ?? default;
        if (cube.IsNull) cube = gpu.Dummy(FrameGlobals.Sampler2D("uAtmoIrradiance", cube: true));

        var cmd = post.Segment();
        var bindings = stackalloc GpuBinding[5];
        bindings[0] = GpuBinding.Sampled(0, new SampledTexture(post.Point(), Injected!.Texture.View()));
        bindings[1] = GpuBinding.StorageImage(1, new SampledTexture(default, Light!.Texture.View()));
        bindings[2] = GpuBinding.StorageImage(2, new SampledTexture(default, Scatter!.Texture.View()));
        bindings[3] = GpuBinding.Sampled(3, new SampledTexture(cube.Sampler, cube.View));
        bindings[4] = GpuBinding.Uniform(4, new GpuBufferBinding(data.Handle, data.Offset, (ulong)sizeof(ShaftScanParams)));
        cmd.Order();
        cmd.BindPipeline(_scanPipeline!.Value);
        cmd.BindProgramResources(_scanProgram!.Value, 0, new ReadOnlySpan<GpuBinding>(bindings, 5));
        cmd.Dispatch((uint)(((int)view.Grid.X + 7) / 8), (uint)(((int)view.Grid.Y + 7) / 8));
        cmd.Order();
    }

    /// <summary>
    /// The scan kernel's uniforms: the grid, the camera and the atmosphere's frame globals.
    /// </summary>
    static ShaftScanParams ScanParams(FrameGlobals g, in GridView view, Vector4 air)
    {
        var r = view.R;
        var sunLight = ReadGlobal<Vector3>(g, "uAtmoSunLight");
        return new ShaftScanParams
        {
            Grid = view.Grid,
            Range = view.Range,
            Eye = new Vector4(view.Eye, 0),
            Right = new Vector4(r.M11, r.M21, r.M31, 0),
            Up = new Vector4(r.M12, r.M22, r.M32, 0),
            Back = new Vector4(r.M13, r.M23, r.M33, 0),
            Tan = new Vector4(view.TanY * view.Aspect, view.TanY, 0, 0),
            Air = air,
            AtmoParams = ReadGlobal<Vector4>(g, "uAtmoParams"),
            AtmoSimple = ReadGlobal<Vector4>(g, "uAtmoSimple"),
            AtmoHaze = ReadGlobal<Vector4>(g, "uAtmoHaze"),
            AtmoAltitude = ReadGlobal<Vector4>(g, "uAtmoAltitude"),
            AtmoFog = ReadGlobal<Vector4>(g, "uAtmoFog"),
            AtmoSun = ReadGlobal<Vector4>(g, "uAtmoSun"),
            SunLight = new Vector4(sunLight, ReadGlobal<Vector4>(g, "uAtmoLight").W),
            AtmoMaps = ReadGlobal<Vector4>(g, "uAtmoMaps"),
        };
    }

    #endregion

    #region Lifetime

    /// <summary>
    /// The passes' programs and the scan kernel (the atlases go with the post targets).
    /// </summary>
    public void Dispose()
    {
        foreach (var inject in _injects) inject?.P.Dispose();
        _resolve?.P.Dispose();
        _integrate?.P.Dispose();
        _apply?.P.Dispose();
        if (_scanProgram is { } scan) post.Gpu.Shaders.Dispose(scan);
        _scatterPass?.P.Dispose();
    }

    #endregion
}
