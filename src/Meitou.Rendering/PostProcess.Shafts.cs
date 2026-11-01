using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Numerics;
using System.Runtime.InteropServices;

using Meitou.Rendering.Gpu;
using Vk = Silk.NET.Vulkan;

using Meitou.Rendering.Gpu.Core;

namespace Meitou.Rendering;

/// <summary>
/// The Meitou light shafts (the <c>shafts</c> switch; <see cref="LightShaftShaders"/>, docs/render-shafts.md): after the scene and the GI resolve, before the
/// fog volumes, a froxel grid's shadow samples go into an atlas (a full-screen pass), their sums front to back (and the air layer's scatter) into two more (one compute
/// kernel, <see cref="LightShaftShaders.Scan"/>, or two fragment passes), and the apply pass darkens the haze over the scene colour.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>The grid's uniforms every shaft pass has (<c>LightShaftShaders.Common</c>).</summary>
    abstract class ShaftPass : FullscreenProgram
    {
        public readonly UniformHandle Grid, Range, Eye, Right, Up, Back, Tan;
        protected ShaftPass(GpuContext gpu, string fragment, string name) : base(gpu, fragment, name)
        {
            (Grid, Range, Eye, Tan) = (P.Uniform("uShaftGrid"), P.Uniform("uShaftRange"), P.Uniform("uShaftEye"), P.Uniform("uShaftTan"));
            (Right, Up, Back) = (P.Uniform("uShaftRight"), P.Uniform("uShaftUp"), P.Uniform("uShaftBack"));
        }
    }

    sealed class ShaftInjectPass : ShaftPass
    {
        public readonly UniformHandle Samples;
        public ShaftInjectPass(GpuContext gpu) : base(gpu, LightShaftShaders.Inject, "post shafts inject") => Samples = P.Uniform("uShaftSamples");
    }

    sealed class ShaftResolvePass : ShaftPass
    {
        public readonly SamplerSlot Current, History;
        public readonly UniformHandle PrevRange, PrevEye, PrevRight, PrevUp, PrevBack, PrevTan, Blend;
        public ShaftResolvePass(GpuContext gpu) : base(gpu, LightShaftShaders.Resolve, "post shafts resolve")
        {
            (Current, History) = (P.Sampler("uShaftCurrent"), P.Sampler("uShaftHistory"));
            (PrevRange, PrevEye, PrevRight, PrevUp) = (P.Uniform("uShaftPrevRange"), P.Uniform("uShaftPrevEye"), P.Uniform("uShaftPrevRight"), P.Uniform("uShaftPrevUp"));
            (PrevBack, PrevTan, Blend) = (P.Uniform("uShaftPrevBack"), P.Uniform("uShaftPrevTan"), P.Uniform("uShaftBlend"));
        }
    }

    sealed class ShaftIntegratePass : ShaftPass
    {
        public readonly SamplerSlot Injected;
        public ShaftIntegratePass(GpuContext gpu) : base(gpu, LightShaftShaders.Integrate, "post shafts integrate") => Injected = P.Sampler("uShaftInjected");
    }

    sealed class ShaftApplyPass : ShaftPass
    {
        public readonly SamplerSlot NearDepth, FarDepth, Light, Scatter;
        public readonly UniformHandle NearPlanes, FarPlanes, WaterY, HasFar, Atlas, Params, Air;
        public ShaftApplyPass(GpuContext gpu) : base(gpu, LightShaftShaders.Apply, "post shafts apply")
        {
            (NearDepth, FarDepth, Light, Scatter) = (P.Sampler("uNearDepth"), P.Sampler("uFarDepth"), P.Sampler("uShaftLight"), P.Sampler("uShaftScatter"));
            Air = P.Uniform("uShaftAir");
            (NearPlanes, FarPlanes, WaterY, HasFar, Atlas, Params) = (P.Uniform("uNearPlanes"), P.Uniform("uFarPlanes"), P.Uniform("uWaterY"), P.Uniform("uHasFar"),
                P.Uniform("uShaftAtlas"), P.Uniform("uShaftParams"));
        }
    }

    /// <summary>The air layer (<see cref="LightShaftShaders.Scatter"/>).</summary>
    sealed class ShaftScatterPass : ShaftPass
    {
        public readonly SamplerSlot Injected;
        public readonly UniformHandle Air;
        public ShaftScatterPass(GpuContext gpu) : base(gpu, LightShaftShaders.Scatter, "post shafts scatter") => (Injected, Air) = (P.Sampler("uShaftInjected"), P.Uniform("uShaftAir"));
    }


    /// <summary>
    /// The integrate and scatter passes as one compute kernel walking each cell column's slices once (<see cref="LightShaftShaders.Scan"/>; <c>--ab shafts-scan</c>).
    /// Off, or where the device cannot write the atlases as storage images, the two fragment passes run.
    /// </summary>
    public bool ShaftScan { get; set; } = true;
    /// <summary>Whether the device can write the atlases as storage images (the scan kernel can run).</summary>
    public bool ShaftScanSupported => SupportsStorage(InternalFormat.R32f) && SupportsStorage(InternalFormat.Rgba16f);

    /// <summary>The scan kernel's uniform block (std140: every member takes a 16-byte slot).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ShaftScanParams
    {
        public Vector4 Grid, Range, Eye, Right, Up, Back, Tan, Air, AtmoParams, AtmoSimple, AtmoHaze, AtmoAltitude, AtmoFog, AtmoSun, SunLight, AtmoMaps;
    }

    GpuProgram? shaftScanProgram;
    GpuComputePipeline? shaftScanPipeline;

    static T ReadGlobal<T>(FrameGlobals g, string name) where T : unmanaged
    {
        T value = default;
        g.UniformValue(name)?.TryRead(MemoryMarshal.AsBytes(new Span<T>(ref value)));
        return value;
    }

    /// <summary>Integrate and scatter in one dispatch: the light atlas and, with the air layer on, the scatter atlas, from the injected atlas.</summary>
    void ShaftScanRun(Vector4 grid, Vector4 range, float tanX, float tanY, Vector4 air, bool withAir)
    {
        if (shaftScanProgram is null)
        {
            shaftScanProgram = Gpu.Shaders.Compute(LightShaftShaders.Scan, "post shafts scan");
            shaftScanPipeline = Gpu.Pipelines.Get(new ComputePipelineDesc(shaftScanProgram!.Value, "post shafts scan"));
        }
        var g = Gpu.Globals;
        var r = viewRotation;
        var sunLight = ReadGlobal<Vector3>(g, "uAtmoSunLight");
        var p = new ShaftScanParams
        {
            Grid = grid, Range = range, Eye = new Vector4(eyeNow, 0), Right = new Vector4(r.M11, r.M21, r.M31, 0), Up = new Vector4(r.M12, r.M22, r.M32, 0),
            Back = new Vector4(r.M13, r.M23, r.M33, 0), Tan = new Vector4(tanX, tanY, 0, 0), Air = withAir ? air : default,
            AtmoParams = ReadGlobal<Vector4>(g, "uAtmoParams"), AtmoSimple = ReadGlobal<Vector4>(g, "uAtmoSimple"), AtmoHaze = ReadGlobal<Vector4>(g, "uAtmoHaze"),
            AtmoAltitude = ReadGlobal<Vector4>(g, "uAtmoAltitude"), AtmoFog = ReadGlobal<Vector4>(g, "uAtmoFog"), AtmoSun = ReadGlobal<Vector4>(g, "uAtmoSun"),
            SunLight = new Vector4(sunLight, ReadGlobal<Vector4>(g, "uAtmoLight").W), AtmoMaps = ReadGlobal<Vector4>(g, "uAtmoMaps"),
        };
        var data = Gpu.Frame.Constants.Write(new ReadOnlySpan<ShaftScanParams>(in p), 256);
        var cube = g.Texture("uAtmoIrradiance")?.Invoke() ?? default;
        if (cube.IsNull) cube = Gpu.Dummy(FrameGlobals.Sampler2D("uAtmoIrradiance", cube: true));
        var cmd = Segment();
        var bindings = stackalloc GpuBinding[5];
        bindings[0] = GpuBinding.Sampled(0, new SampledTexture(Point(), shaftInjected!.Texture.View()));
        bindings[1] = GpuBinding.StorageImage(1, new SampledTexture(default, shaftLight!.Texture.View()));
        bindings[2] = GpuBinding.StorageImage(2, new SampledTexture(default, shaftScatter!.Texture.View()));
        bindings[3] = GpuBinding.Sampled(3, new SampledTexture(cube.Sampler, cube.View));
        bindings[4] = GpuBinding.Uniform(4, new GpuBufferBinding(data.Handle, data.Offset, (ulong)sizeof(ShaftScanParams)));
        cmd.Order();
        cmd.BindPipeline(shaftScanPipeline!.Value);
        cmd.BindProgramResources(shaftScanProgram!.Value, 0, new ReadOnlySpan<GpuBinding>(bindings, 5));
        cmd.Dispatch((uint)(((int)grid.X + 7) / 8), (uint)(((int)grid.Y + 7) / 8));
        cmd.Order();
    }
    static readonly bool ShaftStamps = Environment.GetEnvironmentVariable("MEITOU_SHAFT_STAMPS") == "1";
    ShaftInjectPass? shaftInject;
    ShaftResolvePass? shaftResolve;
    /// <summary>With a temporal upscaler: the injection's raw output and last frame's filtered atlas (<see cref="LightShaftShaders.Resolve"/>, into
    /// <see cref="shaftInjected"/>, the two swapped each frame), and last frame's camera and slices.</summary>
    Target2D? shaftRaw, shaftHistory;
    bool shaftHistoryValid;
    long shaftHistoryFrame = -10;
    (Vector4 Range, Vector3 Eye, Vector3 Right, Vector3 Up, Vector3 Back, Vector2 Tan) shaftPrevious;
    /// <summary>The share of this frame's sun share in the filtered one (0.1: about ten frames of jitter averaged).</summary>
    public const float ShaftTemporalBlend = 0.1f;
    ShaftIntegratePass? shaftIntegrate;
    ShaftScatterPass? shaftScatterPass;
    ShaftApplyPass? shaftApply;
    Target2D? shaftInjected, shaftLight, shaftScatter;

    /// <summary>This frame's grid for the fog volumes pass (<see cref="LightShaftShaders.FogVolumeShadow"/>): its uniforms and the darkening (0: no shafts this frame).</summary>
    (Vector4 Grid, Vector4 Range, Vector2 Tan, float Darkening) shaftFrame;

    /// <summary>What the light shafts did in the last frame (the stats line).</summary>
    public string ShaftsDescribe { get; private set; } = "off";

    /// <summary>The grid's cells across, down (from the render size's aspect) and slices this frame, and the atlas's size.</summary>
    (int Across, int Down, int Slices, int AtlasWidth, int AtlasHeight) ShaftLayout()
    {
        int across = Math.Clamp(Options.ShaftCells, 8, 1024), slices = Math.Clamp(Options.ShaftSlices, 8, 256);
        int down = Math.Max(1, (int)MathF.Round(across * height / (float)Math.Max(width, 1)));
        int columns = LightShaftShaders.AtlasColumns, rows = (slices + columns - 1) / columns;
        return (across, down, slices, across * columns, down * rows);
    }

    /// <summary>
    /// Darkens the haze where the sun is shadowed along the view ray (call after the GI resolve, before <see cref="RunFogVolumes"/>). <paramref name="darkening"/>:
    /// how much of the shadowed haze goes (the strength times the sun's share of the light; 0 skips the passes); <paramref name="far"/>: where the grid ends
    /// (beyond, the share stays the last slice's). Does nothing with the switch off.
    /// </summary>
    public void RunLightShafts(float darkening, float far, float groundY, float airByTime = 1)
    {
        var o = Options;
        shaftFrame.Darkening = 0;
        ShaftsDescribe = !o.LightShafts ? "off" : $"darkening {darkening:0.###} (none: no passes)";
        if (!o.LightShafts || darkening <= 0 || sceneColour is null || !haveNearSlice) return;
        var (across, down, slices, aw, ah) = ShaftLayout();
        ShaftsDescribe = $"darkening {darkening:0.###}, grid {across} x {down} x {slices} ({Options.ShaftSamples} samples), {PostOptions.ShaftNear:0} to {far:0} units, air {(o.ShaftAir > 0 ? $"x{o.ShaftAir:0.##} x{airByTime:0.##} by the hour from {groundY:0} (scale height {PostOptions.ShaftAirHeight:0}, g {PostOptions.ShaftAirPhase:0.##})" : "off")}";
        if (shaftInjected is null || shaftInjected.Width != aw || shaftInjected.Height != ah)
        {
            foreach (var t in new[] { shaftInjected, shaftLight, shaftScatter, shaftRaw, shaftHistory }.OfType<Target2D>()) t.Texture.Dispose();   // released after the frames in flight
            shaftRaw = shaftHistory = null;
            shaftHistoryValid = false;
            using var batch = Gpu.Uploads.Begin();
            // Linear: the fog volumes pass reads the sun's share (blue) between cells.
            shaftInjected = Make(batch, aw, ah, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post shafts injected");
            shaftLight = Make(batch, aw, ah, InternalFormat.R32f, TextureMinFilter.Linear, "post shafts light");
            shaftScatter = Make(batch, aw, ah, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post shafts scatter");
        }
        shaftInject ??= new ShaftInjectPass(Gpu);
        shaftIntegrate ??= new ShaftIntegratePass(Gpu);
        shaftApply ??= new ShaftApplyPass(Gpu);
        shaftScatterPass ??= new ShaftScatterPass(Gpu);
        // The air layer: density at the ground under the eye, its scale height, the phase's anisotropy (0 density: no layer, no pass).
        var air = new Vector4(MathF.Max(o.ShaftAir, 0) * MathF.Max(airByTime, 0) * PostOptions.ShaftAirDensity, groundY, MathF.Max(PostOptions.ShaftAirHeight, 1), Math.Clamp(PostOptions.ShaftAirPhase, 0, 0.95f));

        float near = Math.Clamp(PostOptions.ShaftNear, 1, far * 0.5f);
        var grid = new Vector4(across, down, slices, LightShaftShaders.AtlasColumns);
        // The jitter moves every frame only for a temporal upscaler to average; without one it stays put (a still pattern, not a shimmer).
        var range = new Vector4(near, far, MathF.Log(far / near), Temporal ? Gpu.Frame.Number % 4096 : 0);
        float tanY = MathF.Tan(fovNow * 0.5f);
        var r = viewRotation;
        void Common(ShaftPass s)
        {
            s.P.Set(s.Grid, grid);
            s.P.Set(s.Range, range);
            s.P.Set(s.Eye, eyeNow);
            s.P.Set(s.Tan, tanY * aspectNow, tanY);
            s.P.Set(s.Right, r.M11, r.M21, r.M31);
            s.P.Set(s.Up, r.M12, r.M22, r.M32);
            s.P.Set(s.Back, r.M13, r.M23, r.M33);
        }

        var i = shaftInject;
        Common(i);
        i.P.Set(i.Samples, Math.Clamp(Options.ShaftSamples, 1, 16));
        i.P.ApplyGlobals();   // the atmosphere's uniforms and the shadow receivers, through the frame globals
        if (Temporal)
        {
            // The jittered samples averaged over frames in the grid itself (LightShaftShaders.Resolve), not left to the upscaler.
            if (shaftRaw is null || shaftHistory is null)
            {
                using var batch = Gpu.Uploads.Begin();
                shaftRaw = Make(batch, aw, ah, InternalFormat.Rgba16f, TextureMinFilter.Nearest, "post shafts raw");
                shaftHistory = Make(batch, aw, ah, InternalFormat.Rgba16f, TextureMinFilter.Linear, "post shafts history");
                shaftHistoryValid = false;
            }
            Draw(i.P, shaftRaw);
            (shaftHistory, shaftInjected) = (shaftInjected, shaftHistory);   // last frame's filtered atlas becomes the history; this frame's goes over the older one
            var v = shaftResolve ??= new ShaftResolvePass(Gpu);
            Common(v);
            var prev = shaftPrevious;
            bool continuous = shaftHistoryValid && Gpu.Frame.Number - shaftHistoryFrame <= 2 && Vector3.Distance(prev.Eye, eyeNow) < 2000;
            Bind(v.P, v.Current, shaftRaw);
            Bind(v.P, v.History, continuous ? shaftHistory : shaftRaw);   // never an uninitialised history
            v.P.Set(v.PrevRange, prev.Range);
            v.P.Set(v.PrevEye, prev.Eye);
            v.P.Set(v.PrevRight, prev.Right);
            v.P.Set(v.PrevUp, prev.Up);
            v.P.Set(v.PrevBack, prev.Back);
            v.P.Set(v.PrevTan, prev.Tan);
            v.P.Set(v.Blend, continuous ? ShaftTemporalBlend : 1f);
            Draw(v.P, shaftInjected!);
            shaftHistoryValid = true;
            shaftHistoryFrame = Gpu.Frame.Number;
            shaftPrevious = (range, eyeNow, new Vector3(r.M11, r.M21, r.M31), new Vector3(r.M12, r.M22, r.M32), new Vector3(r.M13, r.M23, r.M33), new Vector2(tanY * aspectNow, tanY));
        }
        else
        {
            Draw(i.P, shaftInjected!);
            shaftHistoryValid = false;
        }
        if (ShaftStamps) Stamp("shafts-inject");

        if (ShaftScan && ShaftScanSupported)
        {
            ShaftScanRun(grid, range, tanY * aspectNow, tanY, air, air.X > 0);
            if (ShaftStamps) Stamp("shafts-scan");
        }
        else
        {
            var g = shaftIntegrate;
            Common(g);
            Bind(g.P, g.Injected, shaftInjected);
            Draw(g.P, shaftLight!);
            if (ShaftStamps) Stamp("shafts-integrate");

            if (air.X > 0)
            {
                var sc = shaftScatterPass;
                Common(sc);
                Bind(sc.P, sc.Injected, shaftInjected);
                sc.P.Set(sc.Air, air);
                sc.P.ApplyGlobals();   // the sun and the sky's ambient
                Draw(sc.P, shaftScatter!);
                if (ShaftStamps) Stamp("shafts-scatter");
            }
        }

        var parameters = new Vector4(Math.Clamp(darkening, 0, 1), PostOptions.ShaftSky, 0, 0);
        shaftFrame = (grid, range, new Vector2(tanY * aspectNow, tanY), Math.Clamp(darkening, 0, 1));
        if (MergeLayers)
        {
            // The apply is the merged pass's (ApplySsao).
            (shaftsPending, shaftArgs) = (true, (air, parameters));
            Stamp("shafts");
            CloseSegment();
            return;
        }
        var a = shaftApply;
        Common(a);
        Bind(a.P, a.NearDepth, sceneDepth);
        Bind(a.P, a.FarDepth, farSliceDrawn ? farDepth : null);
        Bind(a.P, a.Light, shaftLight);
        Bind(a.P, a.Scatter, shaftScatter);
        a.P.Set(a.Air, air);
        a.P.Set(a.NearPlanes, nearPlanes.X, nearPlanes.Y);
        a.P.Set(a.FarPlanes, farPlanes.X, farPlanes.Y);
        a.P.Set(a.WaterY, WaterHeight ?? float.MinValue);
        a.P.Set(a.HasFar, farSliceDrawn ? 1 : 0);
        a.P.Set(a.Atlas, aw, ah);
        a.P.Set(a.Params, parameters);
        a.P.ApplyGlobals();
        Draw(a.P, sceneColour!.Attachment, sceneColour.Format, width, height, width, height, FogState);
        Stamp("shafts");
        CloseSegment();
    }

    void DisposeShafts()
    {
        shaftInject?.P.Dispose();
        shaftResolve?.P.Dispose();
        shaftIntegrate?.P.Dispose();
        shaftApply?.P.Dispose();
        if (shaftScanProgram is { } scan) Gpu.Shaders.Dispose(scan);
        shaftScatterPass?.P.Dispose();
    }
}
