using System.Diagnostics;
using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The seam's post chain for a backend whose targets are top-down: the game's exposure, tone map and grade
/// (<see cref="PostProcessShaders.Luminance"/>, <see cref="PostProcessShaders.Adapt"/>, <see cref="PostProcessShaders.Composite"/>),
/// the scene's fog volumes and SSAO, and the final FXAA and heat-haze passes. <see cref="PostProcess"/> stays the Vulkan path's
/// native chain; this is the part of it the Metal host runs today, with the same shaders and the same order.
/// </summary>
sealed class PostComposite : IDisposable
{
    const int LuminanceSize = 256, LuminanceLevels = 9;
    const float AdaptationRate = 0.5f;

    readonly IGpuContext ctx;
    readonly LegacyProgram composite, luminance, adapt;
    readonly UniformHandle exposure, useAo, dither, auto, tone, toneMix, grade, saturation, contrast, night, balance, sceneSize;
    readonly UniformHandle lumCell, adaptLevel, adaptBlend, adaptBand;
    readonly SamplerSlot sceneSlot, aoSlot, adaptedSlot, lumSceneSlot, lumSlot, lastSlot;

    readonly SsaoPass ssao;
    readonly BlurPass blur;
    readonly ApplyPass apply;
    readonly FogPass fog;
    readonly ShaftInjectPass shaftInjectP;
    readonly ShaftIntegratePass shaftIntegrateP;
    readonly ShaftScatterPass shaftScatterP;
    readonly ShaftApplyPass shaftApplyP;
    readonly FxaaPass fxaa;
    readonly SmaaPasses smaa;
    readonly HeatHazePass haze;
    readonly ParticleDepthPass particleDepthP;
    readonly ParticleCompositePass particleCompositeP;
    readonly ParticleCoveragePass particleCoverageP;
    readonly Dictionary<(GpuProgram Program, InternalFormat Format, DrawState State), GraphicsPipelineDesc> pipelines = [];
    readonly SampledTexture sceneSampler, luminanceSampler, plainSampler, pointSampler, linearMipSampler;

    GpuTexture? luminanceTarget, adaptA, adaptB;
    GpuTexture? ldr, ldrFxaa, smaaEdges, smaaWeights;
    GpuTexture? aoA, aoB;
    GpuTexture? shaftInjected, shaftLight, shaftScatter;
    GpuTexture? flowTexture, perturbationTexture;
    (Vector4 Grid, Vector4 Range, Vector2 Tan, float Darkening) shaftFrame;
    int sceneWidth, sceneHeight, displayWidth, displayHeight;
    long adaptClock;
    bool adaptedValid;
    int adaptedFrames;

    /// <summary>What <see cref="ApplyScene"/> and <see cref="Draw"/> need from the frame's camera.</summary>
    public readonly record struct SceneInputs(Vector3 Eye, Matrix4x4 View, float FieldOfView, float Aspect, float Near, float Far,
        bool CharactersDrawn = true, float? WaterHeight = null);

    /// <param name="flipY">The backend's render targets are not stored flipped (Metal's are not: the present flips the picture,
    /// where Vulkan's negative viewport does the flip into the target itself), so the fullscreen passes sample the scene bottom-up.</param>
    public PostComposite(IGpuContext ctx, bool flipY = false)
    {
        this.ctx = ctx;
        var vertex = flipY
            ? PostProcessShaders.Vertex.Replace("vUv = p * 0.5 + 0.5;", "vUv = vec2(p.x * 0.5 + 0.5, 0.5 - p.y * 0.5);")
            : PostProcessShaders.Vertex;
        composite = LegacyProgram.Create(ctx, vertex, PostProcessShaders.Composite, "post composite");
        luminance = LegacyProgram.Create(ctx, vertex, PostProcessShaders.Luminance, "post luminance");
        adapt = LegacyProgram.Create(ctx, vertex, PostProcessShaders.Adapt, "post exposure adapted");
        ssao = new SsaoPass(ctx, vertex);
        blur = new BlurPass(ctx, vertex);
        apply = new ApplyPass(ctx, vertex);
        fog = new FogPass(ctx, vertex);
        shaftInjectP = new ShaftInjectPass(ctx, vertex);
        shaftIntegrateP = new ShaftIntegratePass(ctx, vertex);
        shaftScatterP = new ShaftScatterPass(ctx, vertex);
        shaftApplyP = new ShaftApplyPass(ctx, vertex);
        fxaa = new FxaaPass(ctx, vertex);
        smaa = new SmaaPasses(ctx, vertex);
        haze = new HeatHazePass(ctx, vertex);
        particleDepthP = new ParticleDepthPass(ctx, vertex);
        particleCompositeP = new ParticleCompositePass(ctx, vertex);
        particleCoverageP = new ParticleCoveragePass(ctx, vertex);
        var c = composite;
        exposure = c.Uniform("uExposure");
        useAo = c.Uniform("uUseAo");
        dither = c.Uniform("uDither");
        auto = c.Uniform("uAuto");
        tone = c.Uniform("uTone");
        toneMix = c.Uniform("uToneMix");
        grade = c.Uniform("uGrade");
        saturation = c.Uniform("uSaturation");
        contrast = c.Uniform("uContrast");
        night = c.Uniform("uNight");
        balance = c.Uniform("uBalance");
        sceneSize = c.Uniform("uSceneSize");
        sceneSlot = c.Sampler("uScene");
        aoSlot = c.Sampler("uAo");
        adaptedSlot = c.Sampler("uAdapted");
        lumSceneSlot = luminance.Sampler("uScene");
        lumCell = luminance.Uniform("uCell");
        lumSlot = adapt.Sampler("uLuminance");
        lastSlot = adapt.Sampler("uLast");
        adaptLevel = adapt.Uniform("uLevel");
        adaptBlend = adapt.Uniform("uBlend");
        adaptBand = adapt.Uniform("uBand");
        sceneSampler = Sample(TextureMinFilter.Linear, TextureMagFilter.Linear);
        luminanceSampler = Sample(TextureMinFilter.LinearMipmapNearest, TextureMagFilter.Linear);
        plainSampler = Sample(TextureMinFilter.Linear, TextureMagFilter.Linear);
        pointSampler = Sample(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        linearMipSampler = Sample(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear);
    }

    /// <summary>The game's shared <c>heatHaze</c> this frame (the caller moves it towards the weather's target); 0 skips the pass.</summary>
    public float HeatHazeAmount { get; set; }
    /// <summary>The game's <c>gameTime</c>: game hours since the load, which the haze's layers cycle on (× 100).</summary>
    public double HeatHazeHours { get; set; }
    /// <summary>The game's far clip D, which its G-buffer depth is divided by.</summary>
    public float HeatHazeFarClip { get; set; } = 10 * Meitou.Data.World.HeatHaze.ViewDistanceSetting;
    /// <summary>Whether the heat-haze textures were found (else the pass never runs).</summary>
    public bool HasHeatHaze => flowTexture is not null && perturbationTexture is not null;

    /// <summary>The heat-haze textures the game uses (<c>materials/FlowHAZE.dds</c>, <c>Perturber.dds</c>).</summary>
    public void LoadHeatHaze(AssetLocator assets)
    {
        try
        {
            if (assets.Find("FlowHAZE.dds") is not { } flow || assets.Find("Perturber.dds") is not { } perturbation)
            {
                Console.WriteLine("warning   heat haze: FlowHAZE.dds or Perturber.dds not found, no heat haze");
                return;
            }
            flowTexture = HazeTexture(DdsReader.ReadFile(flow), "post heat haze flow");
            perturbationTexture = HazeTexture(DdsReader.ReadFile(perturbation), "post heat haze perturbation");
        }
        catch (Exception e) when (e is DdsFormatException or IOException)
        {
            Console.WriteLine($"warning   heat haze textures: {e.Message}");
        }
    }

    /// <summary>The placed fog volumes, then SSAO, over the finished scene, before the temporal resolve. A separate native segment
    /// or the caller's command list may record it; the passes load the scene colour instead of clearing it.</summary>
    public void ApplyScene(IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, in SceneInputs inputs, bool volumes, bool ssaoOn)
    {
        EnsureScene(scene.Desc.Width, scene.Desc.Height);
        if (volumes) RunFogVolumes(cmd, scene, depth, inputs);
        if (ssaoOn) RunSsao(cmd, scene, depth, inputs);
    }

    /// <summary>Draws the scene through exposure and the tone map, then FXAA and the heat haze, into <paramref name="target"/>.</summary>
    public void Draw(IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, GpuTexture target, PostOptions o, (float Min, float Max) band,
        in SceneInputs inputs, bool temporal = false)
    {
        EnsureDisplay(target.Desc.Width, target.Desc.Height);
        MakeTargets(o, temporal);
        // The luminance: 16 taps of the scene into 256², then the mip chain down to 1×1.
        luminance.Bind(lumSceneSlot, sceneSampler with { View = scene.View() });
        luminance.Set(lumCell, 1f / LuminanceSize, 1f / LuminanceSize);
        Fullscreen(cmd, luminance, luminanceTarget!, LuminanceSize, LuminanceSize);
        cmd.GenerateMips(luminanceTarget!);
        cmd.Order();

        // The adaptation: this frame's mean against the last output, blended at the game's rate (instant for a still)
        // inside the band. adaptB is the target written last frame; the swap below makes it adaptA.
        var previous = adaptB;
        (adaptA, adaptB) = (adaptB, adaptA);
        long now = Stopwatch.GetTimestamp();
        float dt = adaptClock == 0 ? 0 : (float)Stopwatch.GetElapsedTime(adaptClock, now).TotalSeconds;
        adaptClock = now;
        // Both history targets must be seeded before the smoothing starts: the second ping-pong target is read on
        // the next frame, and an undefined texture there makes the exposure alternate dark/bright as it converges.
        float blend = !adaptedValid || adaptedFrames < 2 ? 1 : 1 - MathF.Exp(-dt * AdaptationRate);
        adapt.Bind(lumSlot, luminanceSampler with { View = luminanceTarget!.View() });
        adapt.Bind(lastSlot, previous is { } last ? plainSampler with { View = last.View() } : ctx.Dummy(adapt.SamplerInfo(lastSlot)));
        adapt.Set(adaptLevel, MathF.Log2(LuminanceSize));
        adapt.Set(adaptBlend, blend);
        adapt.Set(adaptBand, band.Min, band.Max);
        Fullscreen(cmd, adapt, adaptB!, 1, 1);
        adaptedValid = true;
        adaptedFrames = Math.Min(adaptedFrames + 1, 2);

        // The composite: auto exposure (0.55 / adapted), the game's tone map, grade and dither.
        bool smaaOn = o.Smaa && !temporal && ldr is not null;
        bool fxaaOn = o.Fxaa && !smaaOn && !temporal && ldr is not null;
        bool hazeOn = o.HeatHaze && HasHeatHaze && HeatHazeAmount * o.HeatHazeScale > 0 && ldr is not null;
        var compositeTarget = fxaaOn || smaaOn || hazeOn ? ldr! : target;
        var p = composite;
        p.Bind(sceneSlot, sceneSampler with { View = scene.View() });
        p.Bind(aoSlot, ctx.Dummy(p.SamplerInfo(aoSlot)));
        p.Bind(adaptedSlot, plainSampler with { View = adaptB!.View() });
        p.Set(exposure, o.Exposure);
        p.Set(useAo, 0);
        p.Set(dither, o.Dither ? 1 : 0);
        p.Set(auto, 1);
        p.Set(tone, (int)o.ToneMapUsed);
        p.Set(toneMix, Math.Clamp(o.ToneMix, 0, 1));
        p.Set(grade, o.GradeUsed ? 1 : 0);
        p.Set(saturation, PostOptions.Saturation);
        p.Set(contrast, PostOptions.Contrast);
        p.Set(night, Math.Clamp(o.NightGradeAmount, 0, 1));
        p.Set(balance, o.Balance);
        p.Set(sceneSize, 0f, 0f);
        Fullscreen(cmd, composite, compositeTarget, compositeTarget.Desc.Width, compositeTarget.Desc.Height);

        GpuTexture picture = compositeTarget;
        if (fxaaOn)
        {
            var f = fxaa;
            f.P.Bind(f.Image, plainSampler with { View = picture.View() });
            f.P.Set(f.Texel, 1f / target.Desc.Width, 1f / target.Desc.Height);
            var output = hazeOn ? ldrFxaa! : target;
            Fullscreen(cmd, f.P, output, output.Desc.Width, output.Desc.Height);
            picture = output;
        }
        if (smaaOn)
        {
            RunSmaa(cmd, target, hazeOn);
            picture = hazeOn ? ldrFxaa! : target;
        }
        if (hazeOn)
        {
            var h = haze;
            h.P.Bind(h.Flow, linearMipSampler with { View = flowTexture!.View() });
            h.P.Bind(h.Perturbation, linearMipSampler with { View = perturbationTexture!.View() });
            h.P.Bind(h.Depth, pointSampler with { View = depth.View() });
            h.P.Bind(h.Image, plainSampler with { View = picture.View() });
            double phase = HeatHazeHours * 100;
            h.P.Set(h.Phase, (float)(phase - Math.Floor(phase)));
            h.P.Set(h.Amount, HeatHazeAmount * o.HeatHazeScale);
            float tanY = MathF.Tan(inputs.FieldOfView * 0.5f);
            h.P.Set(h.Tan, tanY * inputs.Aspect, tanY);
            h.P.Set(h.NearFar, inputs.Near, inputs.Far);
            h.P.Set(h.FarClip, HeatHazeFarClip);
            h.P.Set(h.HasDepth, 1f);
            h.P.Set(h.ClipDepthZeroToOne, ctx.ClipDepthZeroToOne ? 1 : 0);
            Fullscreen(cmd, h.P, target, target.Desc.Width, target.Desc.Height);
        }
    }

    /// <summary>SMAA on the composite's LDR picture: edges, blending weights, then the blend into the heat haze's input or the target.</summary>
    void RunSmaa(IGpuCommandList cmd, GpuTexture target, bool haze)
    {
        var s = smaa;
        s.Edges.Bind(s.EdgesImage, plainSampler with { View = ldr!.View() });
        Fullscreen(cmd, s.Edges, smaaEdges!, displayWidth, displayHeight);
        s.Weights.Bind(s.WeightsEdges, pointSampler with { View = smaaEdges!.View() });
        Fullscreen(cmd, s.Weights, smaaWeights!, displayWidth, displayHeight);
        s.Blend.Bind(s.BlendImage, plainSampler with { View = ldr!.View() });
        s.Blend.Bind(s.BlendWeights, pointSampler with { View = smaaWeights!.View() });
        var output = haze ? ldrFxaa! : target;
        Fullscreen(cmd, s.Blend, output, target.Desc.Width, target.Desc.Height);
    }

    /// <summary>
    /// The low-resolution particles' coverage into <paramref name="target"/>'s <paramref name="mask"/> channels: the
    /// accumulation's opacity or brightest channel (no gain: rain and fire are always full size), upsampled by depth so a near edge does not mark
    /// itself reactive. The full-size particles write their own coverage; <see cref="PostTaa"/> calls this for the
    /// low-resolution accumulations after that pass.
    /// </summary>
    public void ParticleCoverage(IGpuCommandList cmd, GpuTexture accum, GpuTexture lowDepth, GpuTexture sceneDepth, GpuTexture target,
        int divisor, float near, float far, ColorMask mask)
    {
        var c = particleCoverageP;
        c.P.Bind(c.Accum, plainSampler with { View = accum.View() });
        c.P.Bind(c.LowDepth, pointSampler with { View = lowDepth.View() });
        c.P.Bind(c.Depth, pointSampler with { View = sceneDepth.View() });
        c.P.Set(c.LowSize, (float)accum.Desc.Width, (float)accum.Desc.Height);
        c.P.Set(c.NearPlanes, near, far);
        c.P.Set(c.Div, (float)divisor);
        c.P.Set(c.FullSize, (float)target.Desc.Width, (float)target.Desc.Height);
        c.P.Set(c.ClipDepthZeroToOne, ctx.ClipDepthZeroToOne ? 1 : 0);
        c.P.Set(c.Limit, mask == ColorMask.A ? 2f : 1f);   // the TAA takes half the motion target's alpha, so 2 is "all current frame"
        var formats = new AttachmentFormats(target.Desc.Format, InternalFormat.None);
        // The MetalFX reactive mask must stay in [0, 1]; several particle accumulations/overlaps must not add past
        // that. TAA's motion alpha keeps the additive 2 = all-current-frame convention.
        var blend = mask == ColorMask.R
            ? new BlendState(true, BlendingFactor.One, BlendingFactor.One, BlendOperation.Max, BlendOperation.Max)
            : new BlendState(true, BlendingFactor.One, BlendingFactor.One);
        var state = DrawState.For(formats, false) with { Blend = blend, ColourMask = mask };
        Fullscreen(cmd, c.P, target, target.Desc.Width, target.Desc.Height, state, LoadOp.Load);
    }

    /// <summary>
    /// The Meitou light shafts (docs/render-shafts.md): the shadow samples in a froxel grid, their sums front to back and
    /// the air layer's scatter, then the darkening of the haze over the scene. The fragment integrate/scatter path runs here;
    /// the compute scan is Vulkan-only.
    /// </summary>
    public void RunLightShafts(IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, SceneInputs i, PostOptions o,
        float darkening, float far, float groundY, float airByTime, bool temporal)
    {
        shaftFrame = default;
        if (!o.LightShafts || darkening <= 0) return;
        int across = Math.Clamp(o.ShaftCells, 8, 1024), slices = Math.Clamp(o.ShaftSlices, 8, 256);
        int down = Math.Max(1, (int)MathF.Round(across * scene.Desc.Height / (float)Math.Max(scene.Desc.Width, 1)));
        int columns = LightShaftShaders.AtlasColumns, rows = (slices + columns - 1) / columns;
        int aw = across * columns, ah = down * rows;
        if (shaftInjected is null || shaftInjected.Desc.Width != aw || shaftInjected.Desc.Height != ah)
        {
            shaftInjected?.Dispose();
            shaftLight?.Dispose();
            shaftScatter?.Dispose();
            shaftInjected = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, aw, ah,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post shafts injected"));
            shaftLight = ctx.CreateTexture(new TextureDesc(InternalFormat.R32f, aw, ah,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post shafts light"));
            shaftScatter = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, aw, ah,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post shafts scatter"));
        }
        float near = Math.Clamp(PostOptions.ShaftNear, 1, far * 0.5f);
        var grid = new Vector4(across, down, slices, columns);
        var range = new Vector4(near, far, MathF.Log(far / near), temporal ? ctx.Frame.Number % 4096 : 0);
        float tanY = MathF.Tan(i.FieldOfView * 0.5f);
        var tan = new Vector2(tanY * i.Aspect, tanY);
        var air = new Vector4(MathF.Max(o.ShaftAir, 0) * MathF.Max(airByTime, 0) * PostOptions.ShaftAirDensity,
            groundY, MathF.Max(PostOptions.ShaftAirHeight, 1), Math.Clamp(PostOptions.ShaftAirPhase, 0, 0.95f));
        var r = i.View with { M41 = 0, M42 = 0, M43 = 0 };

        void Common(ShaftPass s)
        {
            s.P.Set(s.Grid, grid);
            s.P.Set(s.Range, range);
            s.P.Set(s.Eye, i.Eye);
            s.P.Set(s.Tan, tan);
            s.P.Set(s.Right, r.M11, r.M21, r.M31);
            s.P.Set(s.Up, r.M12, r.M22, r.M32);
            s.P.Set(s.Back, r.M13, r.M23, r.M33);
        }

        var inject = shaftInjectP;
        Common(inject);
        inject.P.Set(inject.Samples, Math.Clamp(o.ShaftSamples, 1, 16));
        inject.P.ApplyGlobals();
        Fullscreen(cmd, inject.P, shaftInjected!, aw, ah);

        var integrate = shaftIntegrateP;
        Common(integrate);
        integrate.P.Bind(integrate.Injected, pointSampler with { View = shaftInjected!.View() });
        Fullscreen(cmd, integrate.P, shaftLight!, aw, ah);

        bool withAir = air.X > 0;
        if (withAir)
        {
            var scatter = shaftScatterP;
            Common(scatter);
            scatter.P.Bind(scatter.Injected, pointSampler with { View = shaftInjected!.View() });
            scatter.P.Set(scatter.Air, air);
            scatter.P.ApplyGlobals();
            Fullscreen(cmd, scatter.P, shaftScatter!, aw, ah);
        }

        var apply = shaftApplyP;
        Common(apply);
        apply.P.Bind(apply.NearDepth, pointSampler with { View = depth.View() });
        apply.P.Bind(apply.FarDepth, ctx.Dummy(apply.P.SamplerInfo(apply.FarDepth)));
        apply.P.Bind(apply.Light, plainSampler with { View = shaftLight!.View() });
        apply.P.Bind(apply.Scatter, withAir ? plainSampler with { View = shaftScatter!.View() } : ctx.Dummy(apply.P.SamplerInfo(apply.Scatter)));
        apply.P.Set(apply.Air, air);
        apply.P.Set(apply.NearPlanes, i.Near, i.Far);
        apply.P.Set(apply.FarPlanes, i.Near, i.Far);
        apply.P.Set(apply.WaterY, i.WaterHeight ?? float.MinValue);
        apply.P.Set(apply.HasFar, 0);
        apply.P.Set(apply.Atlas, aw, ah);
        apply.P.Set(apply.Params, Math.Clamp(darkening, 0, 1), PostOptions.ShaftSky, 0f, 0f);
        apply.P.ApplyGlobals();
        var state = DrawState.For(new AttachmentFormats(scene.Desc.Format, InternalFormat.None), false) with
        {
            Blend = new BlendState(true, BlendingFactor.One, BlendingFactor.SrcAlpha),
            ColourMask = DrawState.Rgb,
        };
        Fullscreen(cmd, apply.P, scene, scene.Desc.Width, scene.Desc.Height, state, LoadOp.Load);
        shaftFrame = (grid, range, tan, Math.Clamp(darkening, 0, 1));
    }

    void RunFogVolumes(IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, in SceneInputs i)
    {
        var f = fog;
        f.P.Bind(f.NearDepth, pointSampler with { View = depth.View() });
        f.P.Bind(f.FarDepth, ctx.Dummy(f.P.SamplerInfo(f.FarDepth)));
        f.P.Set(f.NearPlanes, i.Near, i.Far);
        f.P.Set(f.FarPlanes, i.Near, i.Far);
        float tanY = MathF.Tan(i.FieldOfView * 0.5f);
        f.P.Set(f.Tan, tanY * i.Aspect, tanY);
        var r = i.View with { M41 = 0, M42 = 0, M43 = 0 };
        f.P.Set(f.Right, r.M11, r.M21, r.M31);
        f.P.Set(f.Up, r.M12, r.M22, r.M32);
        f.P.Set(f.Back, r.M13, r.M23, r.M33);
        f.P.Set(f.WaterY, i.WaterHeight ?? float.MinValue);
        f.P.Set(f.HasFar, 0);
        var shaft = shaftFrame;
        bool injected = shaft.Darkening > 0 && shaftInjected is not null;
        f.P.Bind(f.ShaftInjected, injected ? plainSampler with { View = shaftInjected!.View() } : ctx.Dummy(f.P.SamplerInfo(f.ShaftInjected)));
        f.P.Set(f.ShaftGrid, shaft.Grid);
        f.P.Set(f.ShaftRange, shaft.Range);
        f.P.Set(f.ShaftTan, shaft.Tan);
        f.P.Set(f.ShaftRight, r.M11, r.M21, r.M31);
        f.P.Set(f.ShaftUp, r.M12, r.M22, r.M32);
        f.P.Set(f.ShaftBack, r.M13, r.M23, r.M33);
        f.P.Set(f.ShaftFog, shaft.Darkening, shaftInjected?.Desc.Width ?? 1, shaftInjected?.Desc.Height ?? 1, 0f);
        f.P.ApplyGlobals();
        var state = DrawState.For(new AttachmentFormats(scene.Desc.Format, InternalFormat.None), false) with
        {
            Blend = new BlendState(true, BlendingFactor.One, BlendingFactor.SrcAlpha),
            ColourMask = DrawState.Rgb,
        };
        Fullscreen(cmd, f.P, scene, scene.Desc.Width, scene.Desc.Height, state, LoadOp.Load);
    }

    void RunSsao(IGpuCommandList cmd, GpuTexture scene, GpuTexture depth, in SceneInputs i)
    {
        int w = Math.Max((scene.Desc.Width + 1) / 2, 1), h = Math.Max((scene.Desc.Height + 1) / 2, 1);
        if (aoA is null || aoA.Desc.Width != w || aoA.Desc.Height != h)
        {
            aoA?.Dispose();
            aoB?.Dispose();
            aoA = ctx.CreateTexture(new TextureDesc(InternalFormat.RG16f, w, h,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post ssao"));
            aoB = ctx.CreateTexture(new TextureDesc(InternalFormat.RG16f, w, h,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post ssao"));
        }
        var s = ssao;
        s.P.Bind(s.Depth, pointSampler with { View = depth.View() });
        s.P.Bind(s.Mask, i.CharactersDrawn ? sceneSampler with { View = scene.View() } : ctx.Dummy(s.P.SamplerInfo(s.Mask)));
        s.P.Set(s.CharacterOccluder, i.CharactersDrawn ? PostOptions.SsaoCharacterOccluder : 1f);
        float tanY = MathF.Tan(i.FieldOfView * 0.5f);
        s.P.Set(s.Tan, tanY * i.Aspect, tanY);
        s.P.Set(s.NearFar, i.Near, i.Far);
        s.P.Set(s.Size, (float)scene.Desc.Width, (float)scene.Desc.Height);
        s.P.Set(s.Radius, PostOptions.SsaoRadius);
        s.P.Set(s.Strength, PostOptions.SsaoStrength);
        s.P.Set(s.FadeStart, PostOptions.SsaoFadeStart);
        s.P.Set(s.FadeEnd, PostOptions.SsaoFadeEnd);
        var r = i.View with { M41 = 0, M42 = 0, M43 = 0 };
        s.P.Set(s.Right, r.M11, r.M21, r.M31);
        s.P.Set(s.Up, r.M12, r.M22, r.M32);
        s.P.Set(s.Back, r.M13, r.M23, r.M33);
        s.P.Set(s.Eye, i.Eye);
        s.P.Set(s.WaterY, i.WaterHeight ?? float.MinValue);
        s.P.Set(s.ClipDepthZeroToOne, ctx.ClipDepthZeroToOne ? 1 : 0);
        s.P.ApplyGlobals();
        Fullscreen(cmd, s.P, aoA, w, h);

        var b = blur;
        b.P.Bind(b.Ao, plainSampler with { View = aoA.View() });
        b.P.Bind(b.Mask, ctx.Dummy(b.P.SamplerInfo(b.Mask)));
        b.P.Set(b.Step, 1f / w, 0f);
        b.P.Set(b.CharacterAo, 1f);
        Fullscreen(cmd, b.P, aoB!, w, h);
        b.P.Bind(b.Ao, plainSampler with { View = aoB!.View() });
        b.P.Bind(b.Mask, i.CharactersDrawn ? sceneSampler with { View = scene.View() } : ctx.Dummy(b.P.SamplerInfo(b.Mask)));
        b.P.Set(b.Step, 0f, 1f / h);
        b.P.Set(b.CharacterAo, i.CharactersDrawn ? PostOptions.SsaoCharacterStrength : 1f);
        Fullscreen(cmd, b.P, aoA, w, h);

        var a = apply;
        a.P.Bind(a.Ao, plainSampler with { View = aoA.View() });
        var state = DrawState.For(new AttachmentFormats(scene.Desc.Format, InternalFormat.None), false) with
        {
            Blend = new BlendState(true, BlendingFactor.Zero, BlendingFactor.SrcColor),
            ColourMask = DrawState.Rgb,
        };
        Fullscreen(cmd, a.P, scene, scene.Desc.Width, scene.Desc.Height, state, LoadOp.Load);
    }

    SampledTexture Sample(TextureMinFilter min, TextureMagFilter mag) =>
        new(ctx.Samplers.Get(SamplerDesc.FromGl(min, mag, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.Repeat,
            false, DepthFunction.Lequal, false, 1, false, 0)), default);

    void EnsureScene(int w, int h)
    {
        if (w == sceneWidth && h == sceneHeight) return;
        sceneWidth = w;
        sceneHeight = h;
        // SSAO targets are made at the first ApplyScene/End for this size.
        aoA?.Dispose();
        aoB?.Dispose();
        aoA = aoB = null;
    }

    void EnsureDisplay(int w, int h)
    {
        if (w == displayWidth && h == displayHeight) return;
        displayWidth = w;
        displayHeight = h;
        ldr?.Dispose();
        ldrFxaa?.Dispose();
        smaaEdges?.Dispose();
        smaaWeights?.Dispose();
        ldr = ldrFxaa = smaaEdges = smaaWeights = null;
    }

    void MakeTargets(PostOptions o, bool temporal)
    {
        if (luminanceTarget is null)
        {
            luminanceTarget = ctx.CreateTexture(new TextureDesc(InternalFormat.R32f, LuminanceSize, LuminanceSize, LuminanceLevels,
                Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.TransferSrc | TextureUse.TransferDst, Name: "post luminance"));
            adaptA = ctx.CreateTexture(new TextureDesc(InternalFormat.RG32f, 1, 1, Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.TransferSrc, Name: "post exposure adapted"));
            adaptB = ctx.CreateTexture(new TextureDesc(InternalFormat.RG32f, 1, 1, Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.TransferSrc, Name: "post exposure adapted"));
            (adaptedValid, adaptedFrames) = (false, 0);
        }
        if (displayWidth <= 0 || displayHeight <= 0) return;
        // LDR and FXAA targets only exist when the composite actually needs them: a temporal upscaler bypasses both, and SMAA is not allocated
        // unless it is the selected anti-aliasing path. This keeps MetalFX from carrying ~60 MiB of never-bound display-size textures.
        bool wantLdr = o.HeatHaze || (!temporal && (o.Fxaa || o.Smaa));
        if (wantLdr && ldr is null)
            ldr = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, displayWidth, displayHeight,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post ldr"));
        if (wantLdr && !temporal && (o.Fxaa || o.Smaa) && ldrFxaa is null)
            ldrFxaa = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, displayWidth, displayHeight,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post ldr fxaa"));
        if (!temporal && o.Smaa && smaaEdges is null)
        {
            smaaEdges = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, displayWidth, displayHeight,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post smaa edges"));
            smaaWeights = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, displayWidth, displayHeight,
                Use: TextureUse.ColourTarget | TextureUse.Sampled, Name: "post smaa weights"));
        }
    }

    GpuTexture HazeTexture(DdsFile dds, string name)
    {
        bool bc1 = dds.Format == DdsFormat.Bc1;
        var top = dds.Surface(0, 0);
        var format = bc1 ? InternalFormat.CompressedRgbaS3TCDxt1Ext : InternalFormat.Rgba8;
        using var batch = ctx.Uploads.Begin();
        var texture = batch.Create(new TextureDesc(format, top.Width, top.Height, dds.MipCount, Name: name));
        for (int level = 0; level < dds.MipCount; level++)
        {
            if (bc1)
            {
                var s = dds.Surface(0, level);
                batch.Write(texture, level, 0, new PixelRect(0, 0, (uint)s.Width, (uint)s.Height), dds.Data.AsSpan(s.Offset, s.Length));
            }
            else
            {
                var img = DdsDecoder.Decode(dds, 0, level);
                batch.Write(texture, level, 0, new PixelRect(0, 0, (uint)img.Width, (uint)img.Height), img.Pixels);
            }
        }
        return texture;
    }

    /// <summary>One fullscreen triangle of <paramref name="program"/> into <paramref name="target"/> (its uniforms and samplers set before).</summary>
    void Fullscreen(IGpuCommandList cmd, LegacyProgram program, GpuTexture target, int w, int h, DrawState? state = null, LoadOp load = LoadOp.DontCare)
    {
        var formats = new AttachmentFormats(target.Desc.Format, InternalFormat.None);
        var s = state ?? DrawState.For(formats, false);
        if (!pipelines.TryGetValue((program.Program, formats.Colour, s), out var desc))
        {
            pipelines[(program.Program, formats.Colour, s)] = desc = s.Pipeline(program.Program, program.VertexLayout([]), PrimitiveType.Triangles, formats, program.Name);
        }
        cmd.BeginRendering(new RenderingDesc(new RenderTarget(target.Attachment(), load, default), default, w, h));
        cmd.SetViewport(new PixelViewport(0, 0, w, h, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)w, (uint)h));
        cmd.SetRaster(CullMode.None, FrontFaceDirection.Ccw);
        cmd.SetDepth(false, false, DepthFunction.Always);
        cmd.SetDepthBias(false, 0, 0);
        cmd.BindPipeline(ctx.Pipelines.Get(desc));
        program.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        cmd.Order();
    }

    /// <summary>
    /// The low-resolution particles' depth pass: the scene's depth reduced to the target's size. The caller opens the
    /// native segment and gives the target; this draws a fullscreen triangle with no colour attachment.
    /// </summary>
    public void ParticleDepth(IGpuCommandList cmd, GpuTexture sceneDepth, GpuTexture target, int fullWidth, int fullHeight, int divisor,
        GpuTexture? colour = null)
    {
        var d = particleDepthP;
        d.P.Bind(d.Depth, pointSampler with { View = sceneDepth.View() });
        d.P.Set(d.FullSize, (float)fullWidth, (float)fullHeight);
        d.P.Set(d.Div, (float)divisor);
        FullscreenDepth(cmd, d.P, target, colour, target.Desc.Width, target.Desc.Height);
    }

    /// <summary>The low-resolution accumulation upsampled over the scene (depth-aware, as the Vulkan chain).</summary>
    public void ParticleComposite(IGpuCommandList cmd, GpuTexture accum, GpuTexture lowDepth, GpuTexture sceneDepth, GpuTexture scene,
        int divisor, float near, float far)
    {
        var c = particleCompositeP;
        c.P.Bind(c.Accum, plainSampler with { View = accum.View() });
        c.P.Bind(c.LowDepth, pointSampler with { View = lowDepth.View() });
        c.P.Bind(c.Depth, pointSampler with { View = sceneDepth.View() });
        c.P.Set(c.LowSize, (float)accum.Desc.Width, (float)accum.Desc.Height);
        c.P.Set(c.NearPlanes, near, far);
        c.P.Set(c.Div, (float)divisor);
        c.P.Set(c.ClipDepthZeroToOne, ctx.ClipDepthZeroToOne ? 1 : 0);
        c.P.Set(c.Scale, 1f);   // over the scene, at its size
        c.P.Set(c.Footprint, 0);
        var formats = new AttachmentFormats(scene.Desc.Format, InternalFormat.None);
        var state = DrawState.For(formats, false) with
        {
            Blend = new BlendState(true, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha),
            ColourMask = DrawState.Rgb,
        };
        Fullscreen(cmd, c.P, scene, scene.Desc.Width, scene.Desc.Height, state, LoadOp.Load);
    }

    /// <summary>A depth-only fullscreen triangle (the low-resolution particle depth pass); a colour target keeps Metal's pipeline valid (the shader writes black).</summary>
    void FullscreenDepth(IGpuCommandList cmd, LegacyProgram program, GpuTexture target, GpuTexture? colour, int w, int h)
    {
        var formats = new AttachmentFormats(colour?.Desc.Format ?? InternalFormat.None, target.Desc.Format);
        var state = DrawState.For(formats, false, depthTest: true, depthWrite: true, compare: DepthFunction.Always);
        if (!pipelines.TryGetValue((program.Program, formats.Colour, state), out var desc))
            pipelines[(program.Program, formats.Colour, state)] = desc = state.Pipeline(program.Program, program.VertexLayout([]),
                PrimitiveType.Triangles, formats, program.Name);
        cmd.BeginRendering(new RenderingDesc(colour is null ? default : new RenderTarget(colour.Attachment(), LoadOp.Clear, default),
            new RenderTarget(target.Attachment(), LoadOp.Clear, default, 1f), w, h));
        cmd.SetViewport(new PixelViewport(0, 0, w, h, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)w, (uint)h));
        cmd.SetRaster(CullMode.None, FrontFaceDirection.Ccw);
        cmd.SetDepth(true, true, DepthFunction.Always);
        cmd.SetDepthBias(false, 0, 0);
        cmd.BindPipeline(ctx.Pipelines.Get(desc));
        program.Flush(cmd);
        cmd.Draw(3);
        cmd.EndRendering();
        cmd.Order();
    }

    public void Dispose()
    {
        luminanceTarget?.Dispose();
        adaptA?.Dispose();
        adaptB?.Dispose();
        ldr?.Dispose();
        ldrFxaa?.Dispose();
        smaaEdges?.Dispose();
        smaaWeights?.Dispose();
        aoA?.Dispose();
        aoB?.Dispose();
        shaftInjected?.Dispose();
        shaftLight?.Dispose();
        shaftScatter?.Dispose();
        flowTexture?.Dispose();
        perturbationTexture?.Dispose();
        composite.Dispose();
        luminance.Dispose();
        adapt.Dispose();
        ssao.P.Dispose();
        blur.P.Dispose();
        apply.P.Dispose();
        fog.P.Dispose();
        shaftInjectP.P.Dispose();
        shaftIntegrateP.P.Dispose();
        shaftScatterP.P.Dispose();
        shaftApplyP.P.Dispose();
        fxaa.P.Dispose();
        smaa.Dispose();
        haze.P.Dispose();
        particleDepthP.P.Dispose();
        particleCompositeP.P.Dispose();
        particleCoverageP.P.Dispose();
    }

    abstract class Pass(IGpuContext ctx, string vertex, string fragment, string name)
    {
        public readonly LegacyProgram P = LegacyProgram.Create(ctx, vertex, fragment, name);
    }

    sealed class SsaoPass : Pass
    {
        public readonly SamplerSlot Depth, Mask;
        public readonly UniformHandle Tan, NearFar, Size, Radius, Strength, FadeStart, FadeEnd, Right, Up, Back, Eye, CharacterOccluder, WaterY, ClipDepthZeroToOne;
        public SsaoPass(IGpuContext ctx, string vertex) : base(ctx, vertex, PostProcessShaders.Ssao, "post ssao")
        {
            ClipDepthZeroToOne = P.Uniform("uClipDepthZeroToOne");
            (Depth, Mask, CharacterOccluder, WaterY) = (P.Sampler("uDepth"), P.Sampler("uMask"), P.Uniform("uCharacterOccluder"), P.Uniform("uWaterY"));
            (Tan, NearFar, Size, Radius, Strength, FadeStart, FadeEnd) = (P.Uniform("uTan"), P.Uniform("uNearFar"), P.Uniform("uSize"),
                P.Uniform("uRadius"), P.Uniform("uStrength"), P.Uniform("uFadeStart"), P.Uniform("uFadeEnd"));
            (Right, Up, Back, Eye) = (P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"), P.Uniform("uSsaoEye"));
        }
    }

    sealed class BlurPass : Pass
    {
        public readonly SamplerSlot Ao, Mask;
        public readonly UniformHandle Step, CharacterAo;
        public BlurPass(IGpuContext ctx, string vertex) : base(ctx, vertex, PostProcessShaders.SsaoBlur, "post ssao blur") =>
            (Ao, Mask, Step, CharacterAo) = (P.Sampler("uAo"), P.Sampler("uMask"), P.Uniform("uStep"), P.Uniform("uCharacterAo"));
    }

    sealed class ApplyPass : Pass
    {
        public readonly SamplerSlot Ao;
        public ApplyPass(IGpuContext ctx, string vertex) : base(ctx, vertex, PostProcessShaders.SsaoApply, "post ssao apply") =>
            Ao = P.Sampler("uAo");
    }

    sealed class FogPass : Pass
    {
        public readonly SamplerSlot NearDepth, FarDepth, ShaftInjected;
        public readonly UniformHandle NearPlanes, FarPlanes, Tan, Right, Up, Back, WaterY, HasFar;
        public readonly UniformHandle ShaftGrid, ShaftRange, ShaftTan, ShaftRight, ShaftUp, ShaftBack, ShaftFog;
        public FogPass(IGpuContext ctx, string vertex) : base(ctx, vertex, PostProcessShaders.FogVolumes, "post fog volumes")
        {
            (NearDepth, FarDepth) = (P.Sampler("uNearDepth"), P.Sampler("uFarDepth"));
            (NearPlanes, FarPlanes, Tan, Right, Up, Back, WaterY, HasFar) = (P.Uniform("uNearPlanes"), P.Uniform("uFarPlanes"), P.Uniform("uTan"),
                P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"), P.Uniform("uWaterY"), P.Uniform("uHasFar"));
            ShaftInjected = P.Sampler("uShaftInjected");
            (ShaftGrid, ShaftRange, ShaftTan, ShaftFog) = (P.Uniform("uShaftGrid"), P.Uniform("uShaftRange"), P.Uniform("uShaftTan"), P.Uniform("uShaftFog"));
            (ShaftRight, ShaftUp, ShaftBack) = (P.Uniform("uShaftRight"), P.Uniform("uShaftUp"), P.Uniform("uShaftBack"));
        }
    }

    abstract class ShaftPass : Pass
    {
        public readonly UniformHandle Grid, Range, Eye, Tan, Right, Up, Back;
        protected ShaftPass(IGpuContext ctx, string vertex, string fragment, string name) : base(ctx, vertex, fragment, name)
        {
            (Grid, Range, Eye, Tan) = (P.Uniform("uShaftGrid"), P.Uniform("uShaftRange"), P.Uniform("uShaftEye"), P.Uniform("uShaftTan"));
            (Right, Up, Back) = (P.Uniform("uShaftRight"), P.Uniform("uShaftUp"), P.Uniform("uShaftBack"));
        }
    }

    sealed class ShaftInjectPass : ShaftPass
    {
        public readonly UniformHandle Samples;
        public ShaftInjectPass(IGpuContext ctx, string vertex) : base(ctx, vertex, LightShaftShaders.Inject, "post shafts inject") =>
            Samples = P.Uniform("uShaftSamples");
    }

    sealed class ShaftIntegratePass : ShaftPass
    {
        public readonly SamplerSlot Injected;
        public ShaftIntegratePass(IGpuContext ctx, string vertex) : base(ctx, vertex, LightShaftShaders.Integrate, "post shafts integrate") =>
            Injected = P.Sampler("uShaftInjected");
    }

    sealed class ShaftScatterPass : ShaftPass
    {
        public readonly SamplerSlot Injected;
        public readonly UniformHandle Air;
        public ShaftScatterPass(IGpuContext ctx, string vertex) : base(ctx, vertex, LightShaftShaders.Scatter, "post shafts scatter") =>
            (Injected, Air) = (P.Sampler("uShaftInjected"), P.Uniform("uShaftAir"));
    }

    sealed class ShaftApplyPass : ShaftPass
    {
        public readonly SamplerSlot NearDepth, FarDepth, Light, Scatter;
        public readonly UniformHandle NearPlanes, FarPlanes, WaterY, HasFar, Atlas, Params, Air;
        public ShaftApplyPass(IGpuContext ctx, string vertex) : base(ctx, vertex, LightShaftShaders.Apply, "post shafts apply")
        {
            (NearDepth, FarDepth, Light, Scatter) = (P.Sampler("uNearDepth"), P.Sampler("uFarDepth"), P.Sampler("uShaftLight"), P.Sampler("uShaftScatter"));
            (NearPlanes, FarPlanes, WaterY, HasFar, Atlas, Params) = (P.Uniform("uNearPlanes"), P.Uniform("uFarPlanes"), P.Uniform("uWaterY"),
                P.Uniform("uHasFar"), P.Uniform("uShaftAtlas"), P.Uniform("uShaftParams"));
            Air = P.Uniform("uShaftAir");
        }
    }

    sealed class FxaaPass : Pass
    {
        public readonly SamplerSlot Image;
        public readonly UniformHandle Texel;
        public FxaaPass(IGpuContext ctx, string vertex) : base(ctx, vertex, PostProcessShaders.Fxaa, "post fxaa") =>
            (Image, Texel) = (P.Sampler("uImage"), P.Uniform("uTexel"));
    }

    /// <summary>SMAA's three passes, with the seam's flipped vertex shader on Metal.</summary>
    sealed class SmaaPasses
    {
        public readonly LegacyProgram Edges, Weights, Blend;
        public readonly SamplerSlot EdgesImage, WeightsEdges, BlendImage, BlendWeights;
        public SmaaPasses(IGpuContext ctx, string vertex)
        {
            Edges = LegacyProgram.Create(ctx, vertex, PostProcessShaders.SmaaEdges, "post smaa edges");
            Weights = LegacyProgram.Create(ctx, vertex, PostProcessShaders.SmaaWeights, "post smaa weights");
            Blend = LegacyProgram.Create(ctx, vertex, PostProcessShaders.SmaaBlend, "post smaa blend");
            (EdgesImage, WeightsEdges) = (Edges.Sampler("uImage"), Weights.Sampler("uEdges"));
            (BlendImage, BlendWeights) = (Blend.Sampler("uImage"), Blend.Sampler("uWeights"));
        }
        public void Dispose() { Edges.Dispose(); Weights.Dispose(); Blend.Dispose(); }
    }

    sealed class HeatHazePass : Pass
    {
        public readonly SamplerSlot Flow, Perturbation, Depth, Image;
        public readonly UniformHandle Phase, Amount, Tan, NearFar, FarClip, HasDepth, ClipDepthZeroToOne;
        public HeatHazePass(IGpuContext ctx, string vertex) : base(ctx, vertex, PostProcessShaders.HeatHaze, "post heat haze")
        {
            ClipDepthZeroToOne = P.Uniform("uClipDepthZeroToOne");
            (Flow, Perturbation, Depth, Image) = (P.Sampler("uFlow"), P.Sampler("uPerturbation"), P.Sampler("uDepth"), P.Sampler("uImage"));
            (Phase, Amount, Tan, NearFar, FarClip, HasDepth) = (P.Uniform("uPhase"), P.Uniform("uAmount"), P.Uniform("uTan"), P.Uniform("uNearFar"),
                P.Uniform("uFarClip"), P.Uniform("uHasDepth"));
        }
    }

    sealed class ParticleDepthPass : Pass
    {
        public readonly SamplerSlot Depth;
        public readonly UniformHandle FullSize, Div;
        public ParticleDepthPass(IGpuContext ctx, string vertex) : base(ctx, vertex, PostProcessShaders.ParticleDepth, "post particle depth") =>
            (Depth, FullSize, Div) = (P.Sampler("uDepth"), P.Uniform("uFullSize"), P.Uniform("uDiv"));
    }

    sealed class ParticleCompositePass : Pass
    {
        public readonly SamplerSlot Accum, LowDepth, Depth;
        public readonly UniformHandle LowSize, NearPlanes, Div, ClipDepthZeroToOne, Scale, Footprint;
        public ParticleCompositePass(IGpuContext ctx, string vertex) : base(ctx, vertex, PostProcessShaders.ParticleComposite, "post particle composite")
        {
            (Accum, LowDepth, Depth) = (P.Sampler("uAccum"), P.Sampler("uLowDepth"), P.Sampler("uDepth"));
            (LowSize, NearPlanes, Div) = (P.Uniform("uLowSize"), P.Uniform("uNearPlanes"), P.Uniform("uDiv"));
            (ClipDepthZeroToOne, Scale, Footprint) = (P.Uniform("uClipDepthZeroToOne"), P.Uniform("uScale"), P.Uniform("uFootprint"));
        }
    }

    sealed class ParticleCoveragePass : Pass
    {
        public readonly SamplerSlot Accum, LowDepth, Depth;
        public readonly UniformHandle LowSize, NearPlanes, Div, FullSize, ClipDepthZeroToOne, Limit;
        public ParticleCoveragePass(IGpuContext ctx, string vertex) : base(ctx, vertex, PostProcessShaders.ParticleCoverage, "post particle coverage")
        {
            (Accum, LowDepth, Depth) = (P.Sampler("uAccum"), P.Sampler("uLowDepth"), P.Sampler("uDepth"));
            (LowSize, NearPlanes, Div, FullSize, Limit) = (P.Uniform("uLowSize"), P.Uniform("uNearPlanes"), P.Uniform("uDiv"),
                P.Uniform("uFullSize"), P.Uniform("uLimit"));
            ClipDepthZeroToOne = P.Uniform("uClipDepthZeroToOne");
        }
    }
}
