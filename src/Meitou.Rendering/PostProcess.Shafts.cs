using System.Numerics;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The Meitou light shafts (the <c>shafts</c> switch; <see cref="LightShaftShaders"/>, docs/render-shafts.md): after the scene and the GI resolve, before the
/// fog volumes, three full-screen passes darken the haze where the sun is shadowed along the view ray: a froxel grid's shadow samples into an atlas,
/// their sums front to back, and the apply pass over the scene colour.
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

    sealed class ShaftIntegratePass : ShaftPass
    {
        public readonly SamplerSlot Injected;
        public ShaftIntegratePass(GpuContext gpu) : base(gpu, LightShaftShaders.Integrate, "post shafts integrate") => Injected = P.Sampler("uShaftInjected");
    }

    sealed class ShaftApplyPass : ShaftPass
    {
        public readonly SamplerSlot NearDepth, FarDepth, Light;
        public readonly UniformHandle NearPlanes, FarPlanes, WaterY, HasFar, Atlas, Params;
        public ShaftApplyPass(GpuContext gpu) : base(gpu, LightShaftShaders.Apply, "post shafts apply")
        {
            (NearDepth, FarDepth, Light) = (P.Sampler("uNearDepth"), P.Sampler("uFarDepth"), P.Sampler("uShaftLight"));
            (NearPlanes, FarPlanes, WaterY, HasFar, Atlas, Params) = (P.Uniform("uNearPlanes"), P.Uniform("uFarPlanes"), P.Uniform("uWaterY"), P.Uniform("uHasFar"),
                P.Uniform("uShaftAtlas"), P.Uniform("uShaftParams"));
        }
    }

    ShaftInjectPass? shaftInject;
    ShaftIntegratePass? shaftIntegrate;
    ShaftApplyPass? shaftApply;
    Target2D? shaftInjected, shaftLight;

    /// <summary>What the light shafts did in the last frame (the stats line).</summary>
    public string ShaftsDescribe { get; private set; } = "off";

    /// <summary>The grid's cells across, down (from the render size's aspect) and slices this frame, and the atlas's size.</summary>
    (int Across, int Down, int Slices, int AtlasWidth, int AtlasHeight) ShaftLayout()
    {
        var o = Options;
        int across = Math.Clamp(o.ShaftCells, 8, 1024), slices = Math.Clamp(o.ShaftSlices, 8, 256);
        int down = Math.Max(1, (int)MathF.Round(across * height / (float)Math.Max(width, 1)));
        int columns = LightShaftShaders.AtlasColumns, rows = (slices + columns - 1) / columns;
        return (across, down, slices, across * columns, down * rows);
    }

    /// <summary>
    /// Darkens the haze where the sun is shadowed along the view ray (call after the GI resolve, before <see cref="RunFogVolumes"/>). <paramref name="darkening"/>:
    /// how much of the shadowed haze goes (the strength times the sun's share of the light; 0 skips the passes); <paramref name="far"/>: where the grid ends
    /// (beyond, the share stays the last slice's). Does nothing with the switch off.
    /// </summary>
    public void RunLightShafts(float darkening, float far)
    {
        var o = Options;
        ShaftsDescribe = !o.LightShafts ? "off" : $"darkening {darkening:0.###} (none: no passes)";
        if (!o.LightShafts || darkening <= 0 || sceneColour is null || !haveNearSlice) return;
        var (across, down, slices, aw, ah) = ShaftLayout();
        ShaftsDescribe = $"darkening {darkening:0.###}, grid {across} x {down} x {slices} ({o.ShaftSamples} samples), {o.ShaftNear:0} to {far:0} units";
        if (shaftInjected is null || shaftInjected.Width != aw || shaftInjected.Height != ah)
        {
            foreach (var t in new[] { shaftInjected, shaftLight }.OfType<Target2D>()) t.Texture.Dispose();   // released after the frames in flight
            using var batch = Gpu.Uploads.Begin();
            shaftInjected = Make(batch, aw, ah, InternalFormat.RG16f, TextureMinFilter.Nearest, "post shafts injected");
            shaftLight = Make(batch, aw, ah, InternalFormat.R32f, TextureMinFilter.Linear, "post shafts light");
        }
        shaftInject ??= new ShaftInjectPass(Gpu);
        shaftIntegrate ??= new ShaftIntegratePass(Gpu);
        shaftApply ??= new ShaftApplyPass(Gpu);

        float near = Math.Clamp(o.ShaftNear, 1, far * 0.5f);
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
        i.P.Set(i.Samples, Math.Clamp(o.ShaftSamples, 1, 16));
        i.P.ApplyGlobals();   // the atmosphere's uniforms and the shadow receivers, through the frame globals
        Draw(i.P, shaftInjected!);

        var g = shaftIntegrate;
        Common(g);
        Bind(g.P, g.Injected, shaftInjected);
        Draw(g.P, shaftLight!);

        var a = shaftApply;
        Common(a);
        Bind(a.P, a.NearDepth, sceneDepth);
        Bind(a.P, a.FarDepth, farSliceDrawn ? farDepth : null);
        Bind(a.P, a.Light, shaftLight);
        a.P.Set(a.NearPlanes, nearPlanes.X, nearPlanes.Y);
        a.P.Set(a.FarPlanes, farPlanes.X, farPlanes.Y);
        a.P.Set(a.WaterY, WaterHeight ?? float.MinValue);
        a.P.Set(a.HasFar, farSliceDrawn ? 1 : 0);
        a.P.Set(a.Atlas, aw, ah);
        a.P.Set(a.Params, Math.Clamp(darkening, 0, 1), o.ShaftSky, o.ShaftDebug ? 1 : 0, 0);
        a.P.ApplyGlobals();
        Draw(a.P, sceneColour!.Attachment, sceneColour.Format, width, height, width, height, FogState);
        Stamp("shafts");
        CloseSegment();
    }

    void DisposeShafts()
    {
        shaftInject?.P.Dispose();
        shaftIntegrate?.P.Dispose();
        shaftApply?.P.Dispose();
    }
}
