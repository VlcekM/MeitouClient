using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gi;

/// <summary>
/// The probe GI (docs/render-gi.md "Probes"): <see cref="Cascades"/> camera-centred grids of <see cref="Columns"/> × <see cref="Columns"/> probe
/// columns, each <see cref="Layers"/> probes high, standing on the terrain. Every frame each probe traces <see cref="Rays"/> rays into the
/// <see cref="GiScene"/> and blends them into its tiles of an octahedral irradiance atlas and distance atlas. The world shaders' lighting
/// (<c>kenshiLight</c>) takes its ambient from them instead of the sky's flat irradiance while <see cref="Enabled"/>. The grids scroll in whole
/// cells: a probe's tile is its world column modulo the grid (toroidal), so nothing is copied, and a probe that moved starts over. Render thread only.
/// </summary>
internal sealed unsafe class GiProbes : IDisposable
{
    public const int Columns = 32, Layers = 8, IrradianceTexels = 8, DistanceTexels = 16, Cascades = 2;
    /// <summary>Rays per probe by default (32 or 64: the trace's workgroup size).</summary>
    public const int DefaultRays = 32;
    /// <summary>The most rays per probe (the ray buffer's stride).</summary>
    public const int MaxRays = 64;
    public const int ProbesPerCascade = Columns * Columns * Layers, Probes = ProbesPerCascade * Cascades;
    /// <summary>Per cascade: the spacing across, the spacing up, the rays' length.</summary>
    static readonly (float Spacing, float SpacingY, float RayLength)[] Grid = [(128, 96, 4000), (512, 384, 16000)];
    bool interiorGrid;
    int interiorLevel;
    (float Spacing, float SpacingY, float RayLength) Layout(int cascade) =>
        cascade == 0 && interiorGrid ? (32, 24, Grid[0].RayLength) : Grid[cascade];

    readonly GpuContext ctx;
    readonly GpuProgram invalidateProgram;
    readonly GpuComputePipeline invalidatePipeline;
    // The trace and blend programs by the rays per probe (the workgroup size): the default, and the other one once the bench's gi-rays switch asks for it.
    readonly Dictionary<int, (GpuProgram Trace, GpuProgram Blend, GpuComputePipeline TracePipeline, GpuComputePipeline BlendPipeline)> variants = [];
    readonly GpuTexture irradiance, distance;
    readonly GpuTexture[] bases;
    readonly DeviceBuffer rays, state;
    // Per frame slot: the probes this frame traces and blends, written by the CPU (GiFogHide): the probe index, bit 31 a hidden probe's slow update.
    readonly BufferAllocation[] lists;
    readonly byte[] hiddenLayers = new byte[Cascades * Columns * Columns];   // per tile (cascade, toroidal row, toroidal column): the layers the fog hides
    (object? Block, float Radius, float Weather) hiddenState;
    Vector3 hiddenEye;
    bool hiddenValid;
    /// <summary>How far the eye may move before the hidden layers are worked out again (the reach boxes are widened by twice that).</summary>
    const float HiddenSlack = 96;
    readonly GpuSampler linear, nearest;
    readonly Vector4[] grid = new Vector4[Cascades * 2];
    readonly float[] baseHeights = new float[Columns * Columns * Cascades];
    readonly (int X, int Z)[] placed = new (int, int)[Cascades];
    readonly bool[] basesPlaced = new bool[Cascades];
    object? placedHeights;
    readonly Random random = new(1);
    int slot;
    bool updated;

    /// <summary>Whether the world shaders take their ambient from the probes (the <c>gi</c> switch).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How much of the last value a probe keeps each frame (0.97: about 30 frames to settle).</summary>
    public float Hysteresis { get; set; } = 0.97f;
    /// <summary>The objects' albedo at a bounce (their textures are not read yet).</summary>
    public Vector3 ObjectAlbedo { get; set; } = new(0.35f, 0.32f, 0.28f);
    /// <summary>The probes are updated in this many interleaved shares, one share a frame (each probe every Phases-th frame; 1, 2, 4 or 8).</summary>
    public int Phases { get; set; } = 4;
    /// <summary>Rays each probe traces per update (32 or 64; the bench's gi-rays switch). The hysteresis is scaled with it so that the noise stays the same (<see cref="UpdateHysteresis"/>).</summary>
    public int Rays { get; set; } = DefaultRays;
    /// <summary>The probes of the far cascade are traced and blended on one of every this many of their rounds, with the hysteresis of that many updates in one (1: every round; the bench's gi-far switch).</summary>
    public int FarPeriod { get; set; } = 2;
    public long Updates { get; private set; }

    /// <summary>How much the probes replace the sky's light (1: fully, 0: not at all; <c>--gi-strength</c>).</summary>
    public float Strength { get; set; } = 1;

    /// <summary>Indoor surface indirect-light gain; applied once at display, without amplifying probe feedback.</summary>
    public float InteriorGain { get; set; } = 4;

    /// <summary>False: indoor surfaces keep the original ambient while outdoor surfaces still use GI.</summary>
    public bool InteriorEnabled { get; set; } = true;

    /// <summary>Render globals: x on/filled, y strength, z resolve, w indoor gain (0 bypasses indoor GI). The compute block carries hysteresis.</summary>
    Vector4 Params => new(Enabled && Active && Shade && !InReflection && updated ? 1 : 0, Strength, Resolve ? 1 : 0, InteriorEnabled ? MathF.Max(1, InteriorGain) : 0);
    /// <summary>True: the lit fragments write their albedo and the post pass adds the probes' light (<c>--gi-resolve</c>, docs/render-gi.md "Resolve").</summary>
    public bool Resolve { get; set; }
    /// <summary>True while the water reflection draws: the mirrored scene keeps the flat ambient (the frame block is re-read per segment).</summary>
    public bool InReflection { get; set; }
    /// <summary>True: the water reflection reads the probes too (off by default; the bench's gi-reflection switch).</summary>
    public bool Reflected { get; set; }
    /// <summary>
    /// A probe with no surface within reach of its cell (no pixel and no bounce weights it) sleeps: it is traced and blended only every
    /// <c>SleepPeriod</c>-th of its updates, to find out whether something came near (0: never sleeps; the bench's gi-sleep switch).
    /// </summary>
    public int SleepPeriod { get; set; } = 8;
    /// <summary>
    /// A probe that no surface the fog leaves visible can read (<see cref="GiFogHide"/>) is traced and blended only every <c>HiddenPeriod</c>-th of its
    /// updates, with the hysteresis of that many updates in one (0: every probe at its normal rate; the bench's gi-fog switch).
    /// </summary>
    public int HiddenPeriod { get; set; } = 16;
    /// <summary>Probes this frame's list holds, and those the fog hides (all of them, not only the share of this frame) at the last list.</summary>
    public int Listed { get; private set; }
    public int[] Hidden { get; } = new int[Cascades];
    /// <summary>How many times the hidden layers were worked out (statistics).</summary>
    public int Solves { get; private set; }
    public double SolveMs { get; private set; }
    /// <summary>False: the probes are updated but the world's shading does not read them (the bench's gi-shade switch, the reads' cost).</summary>
    public bool Shade { get; set; } = true;
    /// <summary>False while the frame does not update the probes (the bench's gi-scene switch): the shaders then do not read them.</summary>
    public bool Active { get; set; } = true;

    public GiProbes(GpuContext ctx)
    {
        this.ctx = ctx;
        invalidateProgram = ctx.Shaders.Compute(GiShaders.ProbeInvalidate, "gi probe invalidate",
        [
            (0, DescriptorType.StorageBuffer), (1, DescriptorType.UniformBuffer), (2, DescriptorType.StorageImage), (3, DescriptorType.CombinedImageSampler),
        ]);
        invalidatePipeline = ctx.Pipelines.Get(new ComputePipelineDesc(invalidateProgram, "gi probe invalidate"));
        Variant(Rays);
        int rows = Cascades * Columns * Layers;
        irradiance = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, Columns * (IrradianceTexels + 2), rows * (IrradianceTexels + 2),
            Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst | TextureUse.TransferSrc, Name: "gi probe irradiance"));
        distance = ctx.CreateTexture(new TextureDesc(InternalFormat.RG16f, Columns * (DistanceTexels + 2), rows * (DistanceTexels + 2),
            Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst, Name: "gi probe distance"));
        // One per frame slot: the CPU writes the base heights of the frame being recorded while the one before may still read its own.
        bases = new GpuTexture[ctx.Device.Frames.Count];
        for (int i = 0; i < bases.Length; i++)
            bases[i] = ctx.CreateTexture(new TextureDesc(InternalFormat.R32f, Columns, Columns * Cascades, Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "gi probe bases"));
        rays = DeviceBuffer.Create(ctx, (ulong)Probes * MaxRays * 16, BufferUse.Storage, "gi probe rays");
        state = DeviceBuffer.Create(ctx, (ulong)Probes * 16, BufferUse.Storage | BufferUse.TransferDst, "gi probe state");
        lists = new BufferAllocation[ctx.Device.Frames.Count];
        for (int i = 0; i < lists.Length; i++)
            lists[i] = ctx.Device.Allocator.CreateBuffer((ulong)Probes * 4, BufferUsageFlags.StorageBufferBit, MemoryKind.Upload, "gi probe list");
        // Every probe starts invalid (alpha 0: no surface reads it, no bounce sees it) and unplaced (state w 0: its first blend starts over).
        var cmd = ctx.Device.BeginImmediate();
        ctx.Device.Vk.CmdFillBuffer(cmd, ctx.Device.BufferAt(state.Handle.Index), 0, state.Size, 0);
        var clear = new ClearColorValue(0f, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        ctx.Device.Vk.CmdClearColorImage(cmd, ctx.Device.NativeImage(irradiance), ImageLayout.General, &clear, 1, &range);
        ctx.Device.Vk.CmdClearColorImage(cmd, ctx.Device.NativeImage(distance), ImageLayout.General, &clear, 1, &range);
        ctx.Device.EndImmediate(cmd);
        linear = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
        nearest = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

        var g = ctx.Globals;
        g.PublishUniform("uGiParams", () => Params);
        g.PublishUniformArray("uGiGrid", grid, () => grid.Length);
        g.Publish("uGiIrradiance", () => new SampledTexture(linear, irradiance.View()));
        g.Publish("uGiDistance", () => new SampledTexture(linear, distance.View()));
        g.Publish("uGiBase", () => new SampledTexture(nearest, bases[slot].View()));
    }

    /// <summary>
    /// This frame's share of the probes (every <paramref name="phases"/>-th from <paramref name="phase"/>) into the slot's list. A probe the fog hides
    /// (<see cref="GiFogHide"/>) is listed only on every <see cref="HiddenPeriod"/>-th of its rounds, with <see cref="GiFogHide.SlowBit"/> set (the blend then uses
    /// the hysteresis of that many updates).
    /// </summary>
    void BuildList(int phases, int phase, FogVolumes? fog, Vector3 eye, bool scrolled)
    {
        bool hide = HiddenPeriod > 0 && fog is { CullActive: true };
        if (hide)
        {
            var now = fog!.CullState;
            // The cached answer holds while the eye has moved less than HiddenSlack and the hide distances have grown by no more than the margin left of
            // the boxes' widening (twice the slack, less the move): the nearest point of a hidden box was at least cached + widening from the old eye.
            float moved = Vector3.Distance(eye, hiddenEye), margin = 2 * HiddenSlack - moved;
            static bool Holds(float now, float cached, float margin) => now == 0 ? cached == 0 : cached > 0 && now <= cached + margin && now >= cached - 100;
            bool same = hiddenValid && !scrolled && now.Block == hiddenState.Block && moved <= HiddenSlack && Holds(now.Radius, hiddenState.Radius, margin) && Holds(now.Weather, hiddenState.Weather, margin);
            if (!same)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                Array.Clear(Hidden);
                // The boxes are widened by twice the slack (see Holds above).
                float nearest = MathF.Min(now.Radius > 0 ? now.Radius : float.MaxValue, now.Weather > 0 ? now.Weather : float.MaxValue);
                for (int c = 0; c < Cascades; c++)
                {
                    var (s, sy, _) = Layout(c);
                    float across = s + GiFogHide.LookupOffset * MathF.Min(s, sy) + 2 * HiddenSlack;
                    float x0 = grid[c * 2].X, z0 = grid[c * 2].Y;
                    int offX = (int)grid[c * 2 + 1].X, offZ = (int)grid[c * 2 + 1].Y;
                    for (int k = 0; k < Columns; k++)
                        for (int i = 0; i < Columns; i++)
                        {
                            int column = (c * Columns + (offZ + k) % Columns) * Columns + (offX + i) % Columns;
                            // Nearer than the nearest hide distance (in x, z alone, a lower bound of the box's distance): nothing there can be hidden.
                            float dx = MathF.Max(MathF.Abs(x0 + i * s - eye.X) - across, 0), dz = MathF.Max(MathF.Abs(z0 + k * s - eye.Z) - across, 0);
                            if (dx * dx + dz * dz < nearest * nearest) { hiddenLayers[column] = 0; continue; }
                            int layers = GiFogHide.HiddenLayers(fog.Covers, x0 + i * s, z0 + k * s, baseHeights[column], s, sy, Layers, fog.CullFloor, 2 * HiddenSlack);
                            hiddenLayers[column] = (byte)layers;
                            Hidden[c] += layers;
                        }
                }
                Solves++; SolveMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                hiddenState = now;
                hiddenEye = eye;
                hiddenValid = true;
            }
        }
        else hiddenValid = false;
        var list = new Span<uint>((void*)lists[slot].Mapped, Probes);
        Listed = GiFogHide.List(list, phases, phase, (uint)(Updates / phases), hide ? HiddenPeriod : 0, FarPeriod, hiddenLayers, [grid[1].X, grid[1].Y, grid[3].X, grid[3].Y]);
    }

    /// <summary>The trace and blend for <paramref name="rays"/> rays a probe (the workgroup size), made on first use.</summary>
    (GpuProgram Trace, GpuProgram Blend, GpuComputePipeline TracePipeline, GpuComputePipeline BlendPipeline) Variant(int rays)
    {
        if (variants.TryGetValue(rays, out var made)) return made;
        var trace = ctx.Shaders.Compute(GiShaders.ProbeTrace(rays), "gi probe trace " + rays,
        [
            (0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer), (2, DescriptorType.StorageBuffer), (3, DescriptorType.UniformBuffer),
            (4, DescriptorType.CombinedImageSampler), (5, DescriptorType.CombinedImageSampler), (6, DescriptorType.CombinedImageSampler),
            (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler), (9, DescriptorType.CombinedImageSampler),
            (10, DescriptorType.CombinedImageSampler), (11, DescriptorType.CombinedImageSampler), (12, DescriptorType.CombinedImageSampler),
            (13, DescriptorType.StorageBuffer), (14, DescriptorType.StorageBuffer),
        ], GpuSetLayout.Bindless);
        var blend = ctx.Shaders.Compute(GiShaders.ProbeBlend(rays), "gi probe blend " + rays,
        [
            (0, DescriptorType.StorageBuffer), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer),
            (3, DescriptorType.StorageImage), (4, DescriptorType.StorageImage), (5, DescriptorType.CombinedImageSampler), (6, DescriptorType.StorageBuffer),
        ]);
        return variants[rays] = (trace, blend, ctx.Pipelines.Get(new ComputePipelineDesc(trace, "gi probe trace " + rays)), ctx.Pipelines.Get(new ComputePipelineDesc(blend, "gi probe blend " + rays)));
    }

    /// <summary>
    /// The hysteresis of one update. <see cref="Hysteresis"/> is the per-frame figure for 64 rays; with fewer rays the same noise needs a longer memory: an exponential
    /// average of independent samples has a variance of (1 - h) / (1 + h) of one sample's, and a sample of n rays has the variance of 64 / n times that of 64 rays, so
    /// (1 - h) / (1 + h) is scaled by n / 64.
    /// </summary>
    float UpdateHysteresis(int phases)
    {
        float h = MathF.Pow(Hysteresis, phases);
        if (Rays >= MaxRays) return h;
        float k = (1 - h) / (1 + h) * Rays / MaxRays;
        return (1 - k) / (1 + k);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public Vector4 Params;
        public Vector4 Grid0, Grid1, Grid2, Grid3;
        public Matrix4x4 Rotation;
        public Vector4 Origin, SunLight, LightDir, Maps, Albedo, RayLength, LightGrid, Extra;
    }

    /// <summary>
    /// Places the grids around <paramref name="eye"/> (bases from <paramref name="heights"/>) and records this frame's probe trace and blend against
    /// <paramref name="scene"/>'s structure, after its <see cref="GiScene.Update"/>.
    /// </summary>
    public void Update(GiScene scene, Vector3 eye, HeightSnapshot heights, WorldLamps? lamps = null, FogVolumes? fog = null, Vector3? interiorFocus = null)
    {
        if (scene.Top is null) return;
        slot = ctx.Frame.Slot;
        var variant = Variant(Rays);
        int phases = Phases is 1 or 2 or 4 or 8 ? Phases : 4, phase = (int)(Updates % phases);
        bool moved = false;
        int level = interiorFocus is { } focus ? (int)MathF.Floor(focus.Y / 96) : 0;
        bool layoutChanged = interiorGrid != interiorFocus.HasValue || interiorLevel != level;
        interiorGrid = interiorFocus.HasValue;
        interiorLevel = level;
        for (int c = 0; c < Cascades; c++)
        {
            var (s, sy, _) = Layout(c);
            var centre = c == 0 ? interiorFocus ?? eye : eye;
            int ox = (int)MathF.Floor(centre.X / s) - Columns / 2, oz = (int)MathF.Floor(centre.Z / s) - Columns / 2;
            grid[c * 2] = new Vector4(ox * s, oz * s, s, sy);
            grid[c * 2 + 1] = new Vector4(Mod(ox, Columns), Mod(oz, Columns), 2 * s, 0);
            // The bases only change when the grid scrolls or the terrain's fine window is swapped.
            bool sameHeights = ReferenceEquals(placedHeights, heights.Fine) && (c != 0 || !layoutChanged);
            if (placed[c] == (ox, oz) && sameHeights) continue;
            // A column that stays in the window keeps its tile (toroidal) and its height (the snapshot is immutable): only the columns that scrolled in are sampled.
            var (oldX, oldZ) = placed[c];
            bool keep = sameHeights && basesPlaced[c];
            placed[c] = (ox, oz);
            basesPlaced[c] = true;
            moved = true;
            for (int k = 0; k < Columns; k++)
                for (int i = 0; i < Columns; i++)
                {
                    if (keep && (uint)(ox + i - oldX) < Columns && (uint)(oz + k - oldZ) < Columns) continue;
                    float x = (ox + i) * s, z = (oz + k) * s;
                    // The lowest ground under the column's cell, so that the bottom probe stands above it all.
                    float h = heights.HeightAt(x, z);
                    h = MathF.Min(h, MathF.Min(heights.HeightAt(x + s * 0.4f, z), heights.HeightAt(x - s * 0.4f, z)));
                    h = MathF.Min(h, MathF.Min(heights.HeightAt(x, z + s * 0.4f), heights.HeightAt(x, z - s * 0.4f)));
                    if (c == 0 && interiorGrid) h = MathF.Max(h, (interiorLevel - 1) * 96);
                    baseHeights[(c * Columns + Mod(oz + k, Columns)) * Columns + Mod(ox + i, Columns)] = h;
                }
        }
        placedHeights = heights.Fine;
        BuildList(phases, phase, fog, eye, moved);
        int listed = Listed;
        var bases = this.bases[slot];
        ctx.Uploads.Write(bases, 0, 0, new PixelRect(0, 0, Columns, Columns * Cascades), MemoryMarshal.AsBytes(baseHeights.AsSpan()));

        var g = ctx.Globals;
        var constants = new Constants
        {
            // The hysteresis per update, so that a probe settles in as many frames whatever the phases.
            Params = new Vector4(1, phase, phases, UpdateHysteresis(phases)),
            Grid0 = grid[0], Grid1 = grid[1], Grid2 = grid[2], Grid3 = grid[3],
            Rotation = RandomRotation(),
            Origin = new Vector4(scene.Origin, Updates),
            SunLight = new Vector4(Read<Vector3>(g, "uAtmoSunLight"), 0),
            LightDir = Read<Vector4>(g, "uAtmoLight"),
            Maps = Read<Vector4>(g, "uAtmoMaps"),
            RayLength = new Vector4(Grid[0].RayLength, Grid[1].RayLength, SleepPeriod, HiddenPeriod),
            LightGrid = lamps?.Grid ?? default,
            Extra = new Vector4(0, FarPeriod > 1 ? FarPeriod : 1, 0, 0),
        };
        var ground = g.Texture("uWeatherGround")?.Invoke() ?? default;
        constants.Albedo = new Vector4(ObjectAlbedo, ground.IsNull ? 0 : 1);
        var block = ctx.Frame.Constants.Write<Constants>(new ReadOnlySpan<Constants>(&constants, 1), 256);

        var cmd = (CommandList)ctx.BeginNative("gi probes");
        cmd.BeginLabel("gi probes");
        cmd.Barrier(BarrierBatch.Full);
        // With the pass meter the full barrier that starts the probe passes is timed apart (it waits for the top level's build).
        if (StageClock.OnClose is not null) { cmd.EndLabel(); ctx.EndNative(cmd); StageClock.Sub("gi probes barrier"); cmd = (CommandList)ctx.BeginNative("gi probes"); cmd.BeginLabel("gi probes"); }
        if (moved)
        {
            // The tiles whose probe moved hold another place's light until their update: hide them (GiShaders.ProbeInvalidate).
            var stateInfo = new DescriptorBufferInfo(ctx.Device.BufferAt(state.Handle.Index), 0, state.Size);
            var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(Constants));
            var images = stackalloc DescriptorImageInfo[2];
            images[0] = new DescriptorImageInfo(default, ctx.Device.ViewAt(irradiance.View().Index), ImageLayout.General);
            images[1] = new DescriptorImageInfo(ctx.Device.SamplerAt(nearest.Index), ctx.Device.ViewAt(bases.View().Index), ImageLayout.General);
            var writes = stackalloc WriteDescriptorSet[4];
            writes[0] = Buffer(0, DescriptorType.StorageBuffer, &stateInfo);
            writes[1] = Buffer(1, DescriptorType.UniformBuffer, &uniform);
            writes[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &images[0] };
            writes[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &images[1] };
            cmd.BindPipeline(invalidatePipeline);
            cmd.PushDescriptors(invalidateProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 4), GpuBindPoint.Compute);
            cmd.Dispatch((uint)(Probes / 64));
            cmd.Barrier(ComputeAfterCompute);
        }
        {
            var top = scene.Top.Handle;
            var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top };
            var records = new DescriptorBufferInfo(ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
            var rayInfo = new DescriptorBufferInfo(ctx.Device.BufferAt(rays.Handle.Index), 0, rays.Size);
            var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(Constants));
            var images = stackalloc DescriptorImageInfo[9];
            images[0] = new DescriptorImageInfo(ctx.Device.SamplerAt(linear.Index), ctx.Device.ViewAt(irradiance.View().Index), ImageLayout.General);
            images[1] = new DescriptorImageInfo(ctx.Device.SamplerAt(linear.Index), ctx.Device.ViewAt(distance.View().Index), ImageLayout.General);
            images[2] = new DescriptorImageInfo(ctx.Device.SamplerAt(nearest.Index), ctx.Device.ViewAt(bases.View().Index), ImageLayout.General);
            images[3] = Image(g, "uAtmoIrradiance", cube: true);
            images[4] = Image(g, "uAtmoAmbientMap", cube: false);
            images[5] = ground.IsNull ? images[2] : new DescriptorImageInfo(ctx.Device.SamplerAt(ground.Sampler.Index), ctx.Device.ViewAt(ground.View.Index), ImageLayout.General);
            // The lamps' textures (a stand-in while there are none: the shader reads them only when LightGrid.w is set).
            if (lamps is not null)
            {
                var (lampCells, lampIndex, lampData) = lamps.Current;
                images[6] = new DescriptorImageInfo(ctx.Device.SamplerAt(lamps.NearestSampler.Index), ctx.Device.ViewAt(lampCells.View().Index), ImageLayout.General);
                images[7] = new DescriptorImageInfo(ctx.Device.SamplerAt(lamps.NearestSampler.Index), ctx.Device.ViewAt(lampIndex.View().Index), ImageLayout.General);
                images[8] = new DescriptorImageInfo(ctx.Device.SamplerAt(lamps.NearestSampler.Index), ctx.Device.ViewAt(lampData.View().Index), ImageLayout.General);
            }
            else images[6] = images[7] = images[8] = images[2];
            var stateInfo = new DescriptorBufferInfo(ctx.Device.BufferAt(state.Handle.Index), 0, state.Size);
            var listInfo = new DescriptorBufferInfo(lists[slot].Buffer, 0, (ulong)Math.Max(listed, 1) * 4);
            var writes = stackalloc WriteDescriptorSet[15];
            writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
            writes[1] = Buffer(1, DescriptorType.StorageBuffer, &records);
            writes[2] = Buffer(2, DescriptorType.StorageBuffer, &rayInfo);
            writes[3] = Buffer(3, DescriptorType.UniformBuffer, &uniform);
            for (int i = 0; i < 9; i++)
                writes[4 + i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = (uint)(4 + i), DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &images[i] };
            writes[13] = Buffer(13, DescriptorType.StorageBuffer, &stateInfo);
            writes[14] = Buffer(14, DescriptorType.StorageBuffer, &listInfo);
            cmd.BindPipeline(variant.TracePipeline);
            cmd.PushDescriptors(variant.Trace, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 15), GpuBindPoint.Compute);
            // Set 1: the bindless table, for the objects' diffuse maps at the hits.
            cmd.BindBindlessTable(variant.Trace, 1, GpuBindPoint.Compute);
            if (listed > 0) cmd.Dispatch((uint)listed);
        }
        cmd.Barrier(ComputeAfterCompute);
        // Two segments, so that the pass meter (StageClock.Sub) times the trace and the blend apart.
        cmd.EndLabel();
        ctx.EndNative(cmd);
        StageClock.Sub("gi trace");
        cmd = (CommandList)ctx.BeginNative("gi blend");
        cmd.BeginLabel("gi blend");
        {
            var rayInfo = new DescriptorBufferInfo(ctx.Device.BufferAt(rays.Handle.Index), 0, rays.Size);
            var stateInfo = new DescriptorBufferInfo(ctx.Device.BufferAt(state.Handle.Index), 0, state.Size);
            var listInfo = new DescriptorBufferInfo(lists[slot].Buffer, 0, (ulong)Math.Max(listed, 1) * 4);
            var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(Constants));
            var images = stackalloc DescriptorImageInfo[3];
            images[0] = new DescriptorImageInfo(default, ctx.Device.ViewAt(irradiance.View().Index), ImageLayout.General);
            images[1] = new DescriptorImageInfo(default, ctx.Device.ViewAt(distance.View().Index), ImageLayout.General);
            images[2] = new DescriptorImageInfo(ctx.Device.SamplerAt(nearest.Index), ctx.Device.ViewAt(bases.View().Index), ImageLayout.General);
            var writes = stackalloc WriteDescriptorSet[7];
            writes[0] = Buffer(0, DescriptorType.StorageBuffer, &rayInfo);
            writes[1] = Buffer(1, DescriptorType.StorageBuffer, &stateInfo);
            writes[2] = Buffer(2, DescriptorType.UniformBuffer, &uniform);
            writes[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &images[0] };
            writes[4] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 4, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &images[1] };
            writes[5] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &images[2] };
            writes[6] = Buffer(6, DescriptorType.StorageBuffer, &listInfo);
            cmd.BindPipeline(variant.BlendPipeline);
            cmd.PushDescriptors(variant.Blend, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 7), GpuBindPoint.Compute);
            if (listed > 0) cmd.Dispatch((uint)listed);
        }
        cmd.Barrier(ComputeAfterCompute);
        cmd.EndLabel();
        ctx.EndNative(cmd);
        updated = true;
        Updates++;
    }

    /// <summary>
    /// The probe passes' writes (storage buffers and images) before the next pass's reads, in compute and in the world's shading: unlike a full
    /// barrier, the work of other passes before it need not drain.
    /// </summary>
    static BarrierBatch ComputeAfterCompute => new()
    {
        SrcStages = PipelineStageFlags2.ComputeShaderBit, SrcAccess = AccessFlags2.ShaderStorageWriteBit,
        DstStages = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit,
        DstAccess = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderSampledReadBit,
    };

    static WriteDescriptorSet Buffer(uint binding, DescriptorType type, DescriptorBufferInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PBufferInfo = info };

    DescriptorImageInfo Image(FrameGlobals g, string name, bool cube)
    {
        var t = g.Texture(name)?.Invoke() ?? default;
        if (!t.IsNull) return new DescriptorImageInfo(ctx.Device.SamplerAt(t.Sampler.Index), ctx.Device.ViewAt(t.View.Index), ImageLayout.General);
        // Not published yet (the game sky off): a stand-in the shader never reads (uAtmoMaps says the map is absent).
        var standIn = ctx.Dummy(FrameGlobals.Sampler2D(name, cube));
        return new DescriptorImageInfo(ctx.Device.SamplerAt(standIn.Sampler.Index), ctx.Device.ViewAt(standIn.View.Index), ImageLayout.General);
    }

    static T Read<T>(FrameGlobals g, string name) where T : unmanaged
    {
        T value = default;
        g.UniformValue(name)?.TryRead(MemoryMarshal.AsBytes(new Span<T>(ref value)));
        return value;
    }

    Matrix4x4 RandomRotation()
    {
        var axis = Vector3.Normalize(new Vector3(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1) + new Vector3(1e-4f));
        return Matrix4x4.CreateFromAxisAngle(axis, random.NextSingle() * MathF.Tau);
    }

    static int Mod(int a, int n) => ((a % n) + n) % n;

    public string Describe() => $"{Cascades} cascades of {Columns}×{Layers}×{Columns} probes ({Layout(0).Spacing:0} and {Grid[1].Spacing:0} apart), {Rays} rays each, 1/{Phases} a frame, {(Enabled ? "lighting" : "not lighting")}" + (hiddenValid ? $"; fog hides {Hidden[0]} + {Hidden[1]} probes (each updated every {HiddenPeriod}th round; solved {Solves} times, {SolveMs / Math.Max(Solves, 1):0.00} ms each), {Listed} listed this frame" : "");

    public void Dispose()
    {
        irradiance.Dispose();
        distance.Dispose();
        foreach (var b in bases) b.Dispose();
        foreach (var l in lists) ctx.Device.DeferFree(l);
        rays.Dispose();
        state.Dispose();
        foreach (var v in variants.Values) { ctx.Shaders.Dispose(v.Trace); ctx.Shaders.Dispose(v.Blend); }
        ctx.Shaders.Dispose(invalidateProgram);
    }
}
