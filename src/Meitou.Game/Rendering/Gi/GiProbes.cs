using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The probe GI (docs/render-gi.md "Probes"): <see cref="Cascades"/> camera-centred grids of <see cref="Columns"/> × <see cref="Columns"/> probe
/// columns, each <see cref="Layers"/> probes high, standing on the terrain. Every frame each probe traces <see cref="Rays"/> rays into the
/// <see cref="GiScene"/> and blends them into its tiles of an octahedral irradiance atlas and distance atlas. The world shaders' lighting
/// (<c>kenshiLight</c>) takes its ambient from them instead of the sky's flat irradiance while <see cref="Enabled"/>. The grids scroll in whole
/// cells: a probe's tile is its world column modulo the grid (toroidal), so nothing is copied, and a probe that moved starts over. Render thread
/// only.
/// </summary>
internal sealed unsafe class GiProbes : IDisposable
{
    #region Constants and state

    /// <summary>
    /// The probe columns across a cascade's grid, the probes in a column, the texels across a probe's irradiance and distance tiles (borders not
    /// counted), and the cascades.
    /// </summary>
    public const int Columns = 32, Layers = 8, IrradianceTexels = 8, DistanceTexels = 16, Cascades = 2;

    /// <summary>
    /// Rays per probe by default (32 or 64: the trace's workgroup size).
    /// </summary>
    public const int DefaultRays = 32;

    /// <summary>
    /// The most rays per probe (the ray buffer's stride).
    /// </summary>
    public const int MaxRays = 64;

    /// <summary>
    /// The probes of a cascade, and of all cascades.
    /// </summary>
    public const int ProbesPerCascade = Columns * Columns * Layers, Probes = ProbesPerCascade * Cascades;

    /// <summary>
    /// Per cascade: the spacing across, the spacing up, the rays' length.
    /// </summary>
    static readonly (float Spacing, float SpacingY, float RayLength)[] Grid = [(128, 96, 4000), (512, 384, 16000)];

    /// <summary>
    /// How far the eye may move before the hidden layers are worked out again (the reach boxes are widened by twice that).
    /// </summary>
    const float HiddenSlack = 96;

    /// <summary>
    /// Whether the first cascade's grid is the interior one (32 apart across, 24 up, around the interior's focus).
    /// </summary>
    bool _interiorGrid;

    /// <summary>
    /// The storey (96 units) of the interior's focus; the first cascade's bases are raised to the storey below it.
    /// </summary>
    int _interiorLevel;

    /// <summary>
    /// The device the probes are traced on.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The program that marks probes invalid (<see cref="GiShaders.ProbeInvalidate"/>).
    /// </summary>
    readonly GpuProgram _invalidateProgram;

    /// <summary>
    /// The invalidate program's pipeline.
    /// </summary>
    readonly GpuComputePipeline _invalidatePipeline;

    /// <summary>
    /// The trace and blend programs and their pipelines by the rays per probe (the workgroup size): the default, and the other one once the bench's
    /// gi-rays switch asks for it; apart for a trace from relocated places and with the cloud shadows, made once those run.
    /// </summary>
    readonly Dictionary<(int Rays, bool Relocate, bool Clouds), (GpuProgram Trace, GpuProgram Blend, GpuComputePipeline TracePipeline,
        GpuComputePipeline BlendPipeline)> _variants = [];

    /// <summary>
    /// The octahedral irradiance atlas (RGBA16F) and distance atlas (RG16F): one bordered tile per probe.
    /// </summary>
    readonly GpuTexture _irradiance, _distance;

    /// <summary>
    /// The columns' base heights per frame slot (R32F, a row of columns per cascade): the CPU writes the frame being recorded's while the one before
    /// may still read its own.
    /// </summary>
    readonly GpuTexture[] _bases;

    /// <summary>
    /// The rays' results (16 bytes a ray, <see cref="MaxRays"/> per probe) and each probe's state (16 bytes; w 0: unplaced, its first blend starts
    /// over).
    /// </summary>
    readonly DeviceBuffer _rays, _state;

    /// <summary>
    /// Per frame slot: the probes this frame traces and blends, written by the CPU (<see cref="GiFogHide"/>): the probe index, bit 31 a hidden
    /// probe's slow update.
    /// </summary>
    readonly BufferAllocation[] _lists;

    /// <summary>
    /// Per tile (cascade, toroidal row, toroidal column): the layers the fog hides.
    /// </summary>
    readonly byte[] _hiddenLayers = new byte[Cascades * Columns * Columns];

    /// <summary>
    /// The fog's cull state the hidden layers were worked out for: its block, hide radius and weather.
    /// </summary>
    (object? Block, float Radius, float Weather) _hiddenState;

    /// <summary>
    /// The eye the hidden layers were worked out at.
    /// </summary>
    Vector3 _hiddenEye;

    /// <summary>
    /// Whether <see cref="_hiddenLayers"/> holds an answer (the fog culls).
    /// </summary>
    bool _hiddenValid;

    /// <summary>
    /// The linear and the nearest sampler the atlases and the bases are read with.
    /// </summary>
    readonly GpuSampler _linear, _nearest;

    /// <summary>
    /// Per cascade two vectors (<c>uGiGrid</c>): the grid's world origin x and z, its spacing across and up; then its toroidal offsets in columns and
    /// twice its spacing.
    /// </summary>
    readonly Vector4[] _grid = new Vector4[Cascades * 2];

    /// <summary>
    /// The columns' base heights, by cascade and toroidal row and column: the lowest ground under each column's cell.
    /// </summary>
    readonly float[] _baseHeights = new float[Columns * Columns * Cascades];

    /// <summary>
    /// The grid origin each cascade was last placed at, in columns.
    /// </summary>
    readonly (int X, int Z)[] _placed = new (int, int)[Cascades];

    /// <summary>
    /// Whether each cascade's bases were sampled already.
    /// </summary>
    readonly bool[] _basesPlaced = new bool[Cascades];

    /// <summary>
    /// The terrain's fine window the bases were sampled from.
    /// </summary>
    object? _placedHeights;

    /// <summary>
    /// The source of each update's random ray rotation (seeded, so runs repeat).
    /// </summary>
    readonly Random _random = new(1);

    /// <summary>
    /// The frame slot of the last update.
    /// </summary>
    int _slot;

    /// <summary>
    /// Whether the probes were updated at least once (the shaders read them only then).
    /// </summary>
    bool _updated;

    /// <summary>
    /// The last update's constants, for <see cref="HitInputs"/>.
    /// </summary>
    Constants _lastConstants;

    /// <summary>
    /// The lamps of the last update, for <see cref="HitInputs"/>.
    /// </summary>
    WorldLamps? _lastLamps;

    /// <summary>
    /// The lamps' tiles as the traced light binds them (set with the frame's hit images).
    /// </summary>
    internal DescriptorImageInfo TileImage { get; private set; }

    /// <summary>
    /// Cascade <paramref name="cascade"/>'s spacing across, spacing up and rays' length: the interior grid's for the first while it is placed, else
    /// <see cref="Grid"/>'s.
    /// </summary>
    (float Spacing, float SpacingY, float RayLength) Layout(int cascade) =>
        cascade == 0 && _interiorGrid ? (32, 24, Grid[0].RayLength) : Grid[cascade];

    #endregion

    #region Settings

    /// <summary>
    /// Whether the world shaders take their ambient from the probes (the <c>gi</c> switch).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How much of the last value a probe keeps each frame (0.97: about 30 frames to settle).
    /// </summary>
    public float Hysteresis { get; set; } = 0.97f;

    /// <summary>
    /// The objects' albedo at a bounce (their textures are not read yet).
    /// </summary>
    public Vector3 ObjectAlbedo { get; set; } = new(0.35f, 0.32f, 0.28f);

    /// <summary>
    /// The probes are updated in this many interleaved shares, one share a frame (each probe every Phases-th frame; 1, 2, 4 or 8).
    /// </summary>
    public int Phases { get; set; } = 4;

    /// <summary>
    /// Rays each probe traces per update (32 or 64; the bench's gi-rays switch). The hysteresis is scaled with it so that the noise stays the same
    /// (<see cref="UpdateHysteresis"/>).
    /// </summary>
    public int Rays { get; set; } = DefaultRays;

    /// <summary>
    /// The probes of the far cascade are traced and blended on one of every this many of their rounds, with the hysteresis of that many updates in
    /// one (1: every round; the bench's gi-far switch).
    /// </summary>
    public int FarPeriod { get; set; } = 2;

    /// <summary>
    /// The probe updates run so far; their count picks each frame's phase.
    /// </summary>
    public long Updates { get; private set; }

    /// <summary>
    /// How much the probes replace the sky's light (1: fully, 0: not at all; <c>--gi-strength</c>).
    /// </summary>
    public float Strength { get; set; } = 1;

    /// <summary>
    /// Indoor surface indirect-light gain; applied once at display, without amplifying probe feedback.
    /// </summary>
    public float InteriorGain { get; set; } = 4;

    /// <summary>
    /// False: indoor surfaces keep the original ambient while outdoor surfaces still use GI.
    /// </summary>
    public bool InteriorEnabled { get; set; } = true;

    /// <summary>
    /// True: probes move out of geometry (the <c>gihybrid</c> switch's relocation, set per frame while the resolve runs: the programs that read
    /// relocated probes are the resolve and the fragments that read the probes themselves; docs/render-gi.md "Probe relocation").
    /// </summary>
    public bool Relocate { get; set; }

    /// <summary>
    /// True: the cloud transmission map dims the sun at the probes' hits (the <c>gihybrid</c> switch's <c>clouds</c> part, docs/render-clouds.md).
    /// </summary>
    public bool CloudShadows { get; set; }

    /// <summary>
    /// Render globals: x on/filled, y strength, z bits: 1 resolve, 2 reflections, 4 the traced sun, 8 the sun written apart with its cascades, 16
    /// the traced lamps (all three with the resolve); w indoor gain (0 bypasses indoor GI). The compute block carries hysteresis.
    /// </summary>
    Vector4 Params => new(Enabled && Active && Shade && !InReflection && _updated ? 1 : 0, Strength, ParamsBits,
        InteriorEnabled ? MathF.Max(1, InteriorGain) : 0);

    /// <summary>
    /// <see cref="Params"/>' z: its bits.
    /// </summary>
    int ParamsBits => (Resolve ? 1 : 0)
        + (Reflections && !InReflection ? 2 : 0)
        + (Resolve && Traced ? 4 : 0)
        + (Resolve && SunApart ? 8 : 0)
        + (Resolve && LampReceivers ? 16 : 0);

    /// <summary>
    /// True while the main view's opaque scene draws with the traced sun (<c>kenshiLight</c> reads it from <c>uGiParams.z</c> bit 4;
    /// docs/render-shadows.md "Traced sun").
    /// </summary>
    public bool Traced { get; set; }

    /// <summary>
    /// True while the main view's opaque scene draws with the lamps traced: the characters write themselves as the lamp shadows' receivers
    /// (<c>uGiParams.z</c> bit 16; docs/render-lights.md "Traced lamp shadows").
    /// </summary>
    public bool LampReceivers { get; set; }

    /// <summary>
    /// True while the main view's opaque scene draws with the sun written apart for the contact shadows (<c>uGiParams.z</c> bit 8;
    /// docs/render-post.md "Contact shadows").
    /// </summary>
    public bool SunApart { get; set; }

    /// <summary>
    /// True: the lit fragments write their albedo and the post pass adds the probes' light (<c>--gi-resolve</c>, docs/render-gi.md "Resolve").
    /// </summary>
    public bool Resolve { get; set; }

    /// <summary>
    /// True: every lit fragment writes the surface target with its gloss, for the post chain's reflections (docs/render-gi.md "Reflections").
    /// </summary>
    public bool Reflections { get; set; }

    /// <summary>
    /// True while the water reflection draws: the mirrored scene keeps the flat ambient (the frame block is re-read per segment).
    /// </summary>
    public bool InReflection { get; set; }

    /// <summary>
    /// True: the water reflection reads the probes too (off by default; the bench's gi-reflection switch).
    /// </summary>
    public bool Reflected { get; set; }

    /// <summary>
    /// A probe with no surface within reach of its cell (no pixel and no bounce weights it) sleeps: it is traced and blended only every
    /// <c>SleepPeriod</c>-th of its updates, to find out whether something came near (0: never sleeps; the bench's gi-sleep switch).
    /// </summary>
    public int SleepPeriod { get; set; } = 8;

    /// <summary>
    /// A probe that no surface the fog leaves visible can read (<see cref="GiFogHide"/>) is traced and blended only every <c>HiddenPeriod</c>-th of
    /// its updates, with the hysteresis of that many updates in one (0: every probe at its normal rate; the bench's gi-fog switch).
    /// </summary>
    public int HiddenPeriod { get; set; } = 16;

    /// <summary>
    /// Probes this frame's list holds.
    /// </summary>
    public int Listed { get; private set; }

    /// <summary>
    /// The probes the fog hides per cascade (all of them, not only the share of this frame) at the last list.
    /// </summary>
    public int[] Hidden { get; } = new int[Cascades];

    /// <summary>
    /// How many times the hidden layers were worked out (statistics).
    /// </summary>
    public int Solves { get; private set; }

    /// <summary>
    /// The ms spent working the hidden layers out, in total.
    /// </summary>
    public double SolveMs { get; private set; }

    /// <summary>
    /// False: the probes are updated but the world's shading does not read them (the bench's gi-shade switch, the reads' cost).
    /// </summary>
    public bool Shade { get; set; } = true;

    /// <summary>
    /// False while the frame does not update the probes (the bench's gi-scene switch): the shaders then do not read them.
    /// </summary>
    public bool Active { get; set; } = true;

    #endregion

    #region Construction

    /// <summary>
    /// Makes the programs, the atlases, the bases, the ray and state buffers and the lists, clears every probe to invalid and unplaced, and publishes
    /// the probes' globals.
    /// </summary>
    public GiProbes(GpuContext ctx)
    {
        _ctx = ctx;
        _invalidateProgram = ctx.Shaders.Compute(GiShaders.ProbeInvalidate, "gi probe invalidate",
        [
            (0, DescriptorType.StorageBuffer), (1, DescriptorType.UniformBuffer), (2, DescriptorType.StorageImage), (3,
                DescriptorType.CombinedImageSampler),
        ]);
        _invalidatePipeline = _ctx.Pipelines.Get(new ComputePipelineDesc(_invalidateProgram, "gi probe invalidate"));
        Variant(Rays, false, false);
        int rows = Cascades * Columns * Layers;
        _irradiance = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, Columns * (IrradianceTexels + 2), rows * (IrradianceTexels + 2),
            Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst | TextureUse.TransferSrc, Name: "gi probe irradiance"));
        _distance = ctx.CreateTexture(new TextureDesc(InternalFormat.Rg16F, Columns * (DistanceTexels + 2), rows * (DistanceTexels + 2),
            Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst, Name: "gi probe distance"));

        // One per frame slot: the CPU writes the base heights of the frame being recorded while the one before may still read its own.
        _bases = new GpuTexture[_ctx.Device.Frames.Count];
        for (int i = 0; i < _bases.Length; i++)
        {
            _bases[i] = _ctx.CreateTexture(new TextureDesc(InternalFormat.R32F, Columns, Columns * Cascades * (1 + Layers),
                Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst, Name: "gi probe bases"));
        }

        _rays = DeviceBuffer.Create(_ctx, (ulong)Probes * MaxRays * 16, BufferUse.Storage, "gi probe rays");
        _state = DeviceBuffer.Create(_ctx, (ulong)Probes * 16, BufferUse.Storage | BufferUse.TransferDst, "gi probe state");
        _lists = new BufferAllocation[_ctx.Device.Frames.Count];
        for (int i = 0; i < _lists.Length; i++)
            _lists[i] = _ctx.Device.Allocator.CreateBuffer((ulong)Probes * 4, BufferUsageFlags.StorageBufferBit, MemoryKind.Upload, "gi probe list");

        // Every probe starts invalid (alpha 0: no surface reads it, no bounce sees it) and unplaced (state w 0: its first blend starts over).
        var cmd = ctx.Device.BeginImmediate();
        ctx.Device.Vk.CmdFillBuffer(cmd, ctx.Device.BufferAt(_state.Handle.Index), 0, _state.Size, 0);
        var clear = new ClearColorValue(0f, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        ctx.Device.Vk.CmdClearColorImage(cmd, ctx.Device.NativeImage(_irradiance), ImageLayout.General, &clear, 1, &range);
        ctx.Device.Vk.CmdClearColorImage(cmd, ctx.Device.NativeImage(_distance), ImageLayout.General, &clear, 1, &range);
        ctx.Device.EndImmediate(cmd);
        _linear = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Linear, TextureMagFilter.Linear));
        _nearest = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));

        var g = ctx.Globals;
        g.PublishUniform("uGiParams", () => Params);
        g.PublishUniformArray("uGiGrid", _grid, () => _grid.Length);
        g.Publish("uGiIrradiance", () => new SampledTexture(_linear, _irradiance.View()));
        g.Publish("uGiDistance", () => new SampledTexture(_linear, _distance.View()));
        g.Publish("uGiBase", () => new SampledTexture(_nearest, _bases[_slot].View()));
    }

    #endregion

    #region Probe lists

    /// <summary>
    /// This frame's share of the probes (every <paramref name="phases"/>-th from <paramref name="phase"/>) into the slot's list. A probe the fog
    /// hides (<see cref="GiFogHide"/>) is listed only on every <see cref="HiddenPeriod"/>-th of its rounds, with <see cref="GiFogHide.SlowBit"/> set
    /// (the blend then uses the hysteresis of that many updates).
    /// </summary>
    void BuildList(int phases, int phase, FogVolumes? fog, Vector3 eye, bool scrolled)
    {
        bool hide = HiddenPeriod > 0 && fog is { CullActive: true };
        if (hide)
        {
            var now = fog!.CullState;

            // The cached answer holds while the eye has moved less than HiddenSlack and the hide distances have grown by no more than the margin left
            // of the boxes' widening (twice the slack, less the move): the nearest point of a hidden box was at least cached + widening from the old
            // eye.
            float moved = Vector3.Distance(eye, _hiddenEye), margin = 2 * HiddenSlack - moved;
            static bool Holds(float now, float cached, float margin) =>
                now == 0 ? cached == 0 : cached > 0 && now <= cached + margin && now >= cached - 100;
            bool same = _hiddenValid && !scrolled && now.Block == _hiddenState.Block && moved <= HiddenSlack && Holds(now.Radius, _hiddenState.Radius,
                margin) && Holds(now.Weather, _hiddenState.Weather, margin);
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
                    float x0 = _grid[c * 2].X, z0 = _grid[c * 2].Y;
                    int offX = (int)_grid[c * 2 + 1].X, offZ = (int)_grid[c * 2 + 1].Y;
                    for (int k = 0; k < Columns; k++)
                    {
                        for (int i = 0; i < Columns; i++)
                        {
                            int column = (c * Columns + (offZ + k) % Columns) * Columns + (offX + i) % Columns;

                            // Nearer than the nearest hide distance (in x, z alone, a lower bound of the box's distance): nothing there can be
                            // hidden.
                            float dx = MathF.Max(MathF.Abs(x0 + i * s - eye.X) - across, 0), dz = MathF.Max(MathF.Abs(z0 + k * s - eye.Z) - across,
                                0);
                            if (dx * dx + dz * dz < nearest * nearest)
                            {
                                _hiddenLayers[column] = 0;
                                continue;
                            }

                            int layers = GiFogHide.HiddenLayers(fog.Covers, x0 + i * s, z0 + k * s, _baseHeights[column], s, sy, Layers,
                                fog.CullFloor, 2 * HiddenSlack);
                            _hiddenLayers[column] = (byte)layers;
                            Hidden[c] += layers;
                        }
                    }
                }
                Solves++; SolveMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                _hiddenState = now;
                _hiddenEye = eye;
                _hiddenValid = true;
            }
        }
        else _hiddenValid = false;
        var list = new Span<uint>((void*)_lists[_slot].Mapped, Probes);
        Listed = GiFogHide.List(list, phases, phase, (uint)(Updates / phases), hide ? HiddenPeriod : 0, FarPeriod, _hiddenLayers, [_grid[1].X,
            _grid[1].Y, _grid[3].X, _grid[3].Y]);
    }

    /// <summary>
    /// The trace and blend for <paramref name="rays"/> rays a probe (the workgroup size), from the relocated places (<paramref name="relocate"/>)
    /// and with the cloud shadows (<paramref name="clouds"/>), made on first use.
    /// </summary>
    (GpuProgram Trace, GpuProgram Blend, GpuComputePipeline TracePipeline, GpuComputePipeline BlendPipeline) Variant(int rays, bool relocate,
        bool clouds)
    {
        if (_variants.TryGetValue((rays, relocate, clouds), out var made)) return made;

        (uint, DescriptorType)[] set0 =
        [
            (0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer), (2, DescriptorType.StorageBuffer), (3,
                DescriptorType.UniformBuffer),
            (4, DescriptorType.CombinedImageSampler), (5, DescriptorType.CombinedImageSampler), (6, DescriptorType.CombinedImageSampler),
            (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler), (9, DescriptorType.CombinedImageSampler),
            (10, DescriptorType.CombinedImageSampler), (11, DescriptorType.CombinedImageSampler), (12, DescriptorType.CombinedImageSampler),
            (13, DescriptorType.StorageBuffer), (14, DescriptorType.StorageBuffer), (18, DescriptorType.CombinedImageSampler),
            .. clouds ? new (uint, DescriptorType)[] { (15, DescriptorType.CombinedImageSampler), (16, DescriptorType.CombinedImageSampler) } : [],
        ];
        var trace = _ctx.Shaders.Compute(GiShaders.ProbeTrace(rays, relocate, clouds), "gi probe trace " + rays, set0, GpuSetLayout.Bindless);
        var blend = _ctx.Shaders.Compute(GiShaders.ProbeBlend(rays), "gi probe blend " + rays,
        [
            (0, DescriptorType.StorageBuffer), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer),
            (3, DescriptorType.StorageImage), (4, DescriptorType.StorageImage), (5, DescriptorType.CombinedImageSampler), (6,
                    DescriptorType.StorageBuffer),
        ]);
        return _variants[(rays, relocate, clouds)] = (trace, blend, _ctx.Pipelines.Get(new ComputePipelineDesc(trace, "gi probe trace " + rays)),
            _ctx.Pipelines.Get(new ComputePipelineDesc(blend, "gi probe blend " + rays)));
    }

    /// <summary>
    /// The hysteresis of one update. <see cref="Hysteresis"/> is the per-frame figure for 64 rays; with fewer rays the same noise needs a longer
    /// memory: an exponential average of independent samples has a variance of (1 - h) / (1 + h) of one sample's, and a sample of n rays has the
    /// variance of 64 / n times that of 64 rays, so (1 - h) / (1 + h) is scaled by n / 64.
    /// </summary>
    float UpdateHysteresis(int phases)
    {
        float h = MathF.Pow(Hysteresis, phases);
        if (Rays >= MaxRays) return h;
        float k = (1 - h) / (1 + h) * Rays / MaxRays;
        return (1 - k) / (1 + k);
    }

    /// <summary>
    /// The probe passes' uniform block, as the shaders' <c>Params</c> lays it out.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Constants
    {
        /// <summary>
        /// x 1, y this frame's phase, z the phases, w the hysteresis per update.
        /// </summary>
        public Vector4 Params;

        /// <summary>
        /// <see cref="_grid"/>: the cascades' grids.
        /// </summary>
        public Vector4 Grid0, Grid1, Grid2, Grid3;

        /// <summary>
        /// The frame's random rotation of the ray directions.
        /// </summary>
        public Matrix4x4 Rotation;

        /// <summary>
        /// The traced scene's origin (w: the updates so far), the sun's light, the lighting direction, the atmosphere's maps, the objects' albedo (w:
        /// a ground colour map present), the rays' lengths per cascade with the sleep and hidden periods, the lamps' grid and the far cascade's
        /// period (y).
        /// </summary>
        public Vector4 Origin, SunLight, LightDir, Maps, Albedo, RayLength, LightGrid, Extra;

        /// <summary>
        /// The clouds, their wind and the sun as the atmosphere publishes them, for a trace with the cloud shadows.
        /// </summary>
        public Vector4 Cloud, CloudWind, Sun;
    }

    #endregion

    #region Update

    /// <summary>
    /// Places the grids around <paramref name="eye"/> (bases from <paramref name="heights"/>) and records this frame's probe trace and blend against
    /// <paramref name="scene"/>'s structure, after its <see cref="GiScene.Update"/>.
    /// </summary>
    public void Update(GiScene scene, Vector3 eye, HeightSnapshot heights, WorldLamps? lamps = null, FogVolumes? fog = null,
        Vector3? interiorFocus = null)
    {
        if (scene.Top is null) return;

        _slot = _ctx.Frame.Slot;
        if (Relocate) EnsureRelocation();

        var variant = Variant(Rays, Relocate, CloudShadows);
        int phases = Phases is 1 or 2 or 4 or 8 ? Phases : 4, phase = (int)(Updates % phases);
        bool moved = PlaceGrids(eye, heights, interiorFocus);
        BuildList(phases, phase, fog, eye, moved);
        int listed = Listed;
        var bases = _bases[_slot];
        _ctx.Uploads.Write(bases, 0, 0, new PixelRect(0, 0, Columns, Columns * Cascades), MemoryMarshal.AsBytes(_baseHeights.AsSpan()));

        var g = _ctx.Globals;
        var block = WriteConstants(scene, g, phases, phase, lamps, out var ground);

        var cmd = BeginProbePasses();
        if (moved) Invalidate(cmd, block, bases);
        if (Relocate) PublishOffsets(cmd, bases, block);
        Trace(cmd, scene, variant.Trace, variant.TracePipeline, block, ground, lamps, listed);
        cmd.Barrier(ComputeAfterCompute);

        // Two segments, so that the pass meter (StageClock.Sub) times the trace and the blend apart.
        cmd.EndLabel();
        _ctx.EndNative(cmd);
        StageClock.Sub("gi trace");
        cmd = (CommandList)_ctx.BeginNative("gi blend");
        cmd.BeginLabel("gi blend");
        if (Relocate) RelocateListed(cmd, bases, block, listed);
        Blend(cmd, variant.Blend, variant.BlendPipeline, block, bases, listed);
        cmd.Barrier(ComputeAfterCompute);
        cmd.EndLabel();
        _ctx.EndNative(cmd);
        _updated = true;
        Updates++;
    }

    /// <summary>
    /// This frame's update, for a later pass of the frame that shades ray hits the way the trace does (<see cref="GiCache"/>); false before the
    /// first update.
    /// </summary>
    internal bool HitInputs(DescriptorImageInfo* images, out Constants constants)
    {
        constants = _lastConstants;
        if (!_updated) return false;

        HitImages(images, _ctx.Globals.Texture("uWeatherGround")?.Invoke() ?? default, _lastLamps);
        return true;
    }

    /// <summary>
    /// Places each cascade's grid around <paramref name="eye"/> (the first around <paramref name="interiorFocus"/> when set, its bases raised
    /// to the floor's level) and samples the bases of the columns that scrolled in. True when any grid moved or took new bases.
    /// </summary>
    bool PlaceGrids(Vector3 eye, HeightSnapshot heights, Vector3? interiorFocus)
    {
        bool moved = false;
        int level = interiorFocus is { } focus ? (int)MathF.Floor(focus.Y / 96) : 0;
        bool layoutChanged = _interiorGrid != interiorFocus.HasValue || _interiorLevel != level;
        _interiorGrid = interiorFocus.HasValue;
        _interiorLevel = level;
        for (int c = 0; c < Cascades; c++)
        {
            var (s, sy, _) = Layout(c);
            var centre = c == 0 ? interiorFocus ?? eye : eye;
            int ox = (int)MathF.Floor(centre.X / s) - Columns / 2, oz = (int)MathF.Floor(centre.Z / s) - Columns / 2;
            _grid[c * 2] = new Vector4(ox * s, oz * s, s, sy);
            _grid[c * 2 + 1] = new Vector4(Mod(ox, Columns), Mod(oz, Columns), 2 * s, Relocate ? 1 : 0);

            // The bases only change when the grid scrolls or the terrain's fine window is swapped.
            bool sameHeights = ReferenceEquals(_placedHeights, heights.Fine) && (c != 0 || !layoutChanged);
            if (_placed[c] == (ox, oz) && sameHeights) continue;

            // A column that stays in the window keeps its tile (toroidal) and its height (the snapshot is immutable): only the columns that scrolled
            // in are sampled.
            var (oldX, oldZ) = _placed[c];
            bool keep = sameHeights && _basesPlaced[c];
            _placed[c] = (ox, oz);
            _basesPlaced[c] = true;
            moved = true;
            SampleBases(heights, c, s, ox, oz, keep, oldX, oldZ);
        }

        _placedHeights = heights.Fine;
        return moved;
    }

    /// <summary>
    /// The bases of cascade <paramref name="c"/>'s columns at grid origin (<paramref name="ox"/>, <paramref name="oz"/>); with
    /// <paramref name="keep"/> the columns also in the old window (<paramref name="oldX"/>, <paramref name="oldZ"/>) are left as they are.
    /// </summary>
    void SampleBases(HeightSnapshot heights, int c, float s, int ox, int oz, bool keep, int oldX, int oldZ)
    {
        for (int k = 0; k < Columns; k++)
        {
            for (int i = 0; i < Columns; i++)
            {
                if (keep && (uint)(ox + i - oldX) < Columns && (uint)(oz + k - oldZ) < Columns) continue;
                float x = (ox + i) * s, z = (oz + k) * s;

                // The lowest ground under the column's cell, so that the bottom probe stands above it all.
                float h = heights.HeightAt(x, z);
                h = MathF.Min(h, MathF.Min(heights.HeightAt(x + s * 0.4f, z), heights.HeightAt(x - s * 0.4f, z)));
                h = MathF.Min(h, MathF.Min(heights.HeightAt(x, z + s * 0.4f), heights.HeightAt(x, z - s * 0.4f)));
                if (c == 0 && _interiorGrid) h = MathF.Max(h, (_interiorLevel - 1) * 96);
                _baseHeights[(c * Columns + Mod(oz + k, Columns)) * Columns + Mod(ox + i, Columns)] = h;
            }
        }
    }

    /// <summary>
    /// This update's constants (grids, rotation, the sky's light and maps, the lamps' grid, the clouds) in the frame's constant ring, kept for
    /// <see cref="HitInputs"/>; <paramref name="ground"/> is the weather's ground map (null when there is none).
    /// </summary>
    Transient WriteConstants(GiScene scene, FrameGlobals g, int phases, int phase, WorldLamps? lamps, out SampledTexture ground)
    {
        var constants = new Constants
        {
            // The hysteresis per update, so that a probe settles in as many frames whatever the phases.
            Params = new Vector4(1, phase, phases, UpdateHysteresis(phases)),
            Grid0 = _grid[0],
            Grid1 = _grid[1],
            Grid2 = _grid[2],
            Grid3 = _grid[3],
            Rotation = RandomRotation(),
            Origin = new Vector4(scene.Origin, Updates),
            SunLight = new Vector4(Read<Vector3>(g, "uAtmoSunLight"), 0),
            LightDir = Read<Vector4>(g, "uAtmoLight"),
            Maps = Read<Vector4>(g, "uAtmoMaps"),
            RayLength = new Vector4(Grid[0].RayLength, Grid[1].RayLength, SleepPeriod, HiddenPeriod),
            LightGrid = lamps?.Grid ?? default,
            Extra = new Vector4(0, FarPeriod > 1 ? FarPeriod : 1, 0, 0),
            Cloud = Read<Vector4>(g, "uAtmoCloud"),
            CloudWind = Read<Vector4>(g, "uAtmoCloudWind"),
            Sun = Read<Vector4>(g, "uAtmoSun"),
        };
        ground = g.Texture("uWeatherGround")?.Invoke() ?? default;
        constants.Albedo = new Vector4(ObjectAlbedo, ground.IsNull ? 0 : 1);
        (_lastConstants, _lastLamps) = (constants, lamps);
        return _ctx.Frame.Constants.Write<Constants>(new ReadOnlySpan<Constants>(&constants, 1), 256);
    }

    /// <summary>
    /// Opens the probe passes' segment after a full barrier. With the pass meter the full barrier that starts the probe passes is timed apart
    /// (it waits for the top level's build).
    /// </summary>
    CommandList BeginProbePasses()
    {
        var cmd = (CommandList)_ctx.BeginNative("gi probes");
        cmd.BeginLabel("gi probes");
        cmd.Barrier(BarrierBatch.Full);
        if (StageClock.OnClose is not null)
        {
            cmd.EndLabel();
            _ctx.EndNative(cmd);
            StageClock.Sub("gi probes barrier");
            cmd = (CommandList)_ctx.BeginNative("gi probes");
            cmd.BeginLabel("gi probes");
        }

        return cmd;
    }

    /// <summary>
    /// The tiles whose probe moved hold another place's light until their update: hide them (GiShaders.ProbeInvalidate).
    /// </summary>
    void Invalidate(CommandList cmd, Transient block, GpuTexture bases)
    {
        var stateInfo = new DescriptorBufferInfo(_ctx.Device.BufferAt(_state.Handle.Index), 0, _state.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(Constants));
        var images = stackalloc DescriptorImageInfo[2];
        images[0] = new DescriptorImageInfo(default, _ctx.Device.ViewAt(_irradiance.View().Index), ImageLayout.General);
        images[1] = new DescriptorImageInfo(_ctx.Device.SamplerAt(_nearest.Index), _ctx.Device.ViewAt(bases.View().Index), ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[4];
        writes[0] = Buffer(0, DescriptorType.StorageBuffer, &stateInfo);
        writes[1] = Buffer(1, DescriptorType.UniformBuffer, &uniform);
        writes[2] = Storage(2, &images[0]);
        writes[3] = Sampled(3, &images[1]);
        cmd.BindPipeline(_invalidatePipeline);
        cmd.PushDescriptors(_invalidateProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 4), GpuBindPoint.Compute);
        cmd.Dispatch((uint)(Probes / 64));
        cmd.Barrier(ComputeAfterCompute);
    }

    /// <summary>
    /// The trace of the listed probes' rays against the scene's structure: the probes' textures, the sky's maps, the ground and the lamps (and the
    /// cloud maps with <see cref="CloudShadows"/>), the objects' diffuse maps through the bindless table. Keeps what it bound for
    /// <see cref="TraceReflections"/>.
    /// </summary>
    void Trace(CommandList cmd, GiScene scene, GpuProgram program, GpuComputePipeline pipeline, Transient block, SampledTexture ground,
        WorldLamps? lamps, int listed)
    {
        var top = scene.Top!.Handle;
        var asWrite = AccelerationWrite(&top);
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var rayInfo = new DescriptorBufferInfo(_ctx.Device.BufferAt(_rays.Handle.Index), 0, _rays.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(Constants));
        var images = stackalloc DescriptorImageInfo[11];
        HitImages(images, ground, lamps);
        for (int i = 0; i < _traceImages.Length; i++) _traceImages[i] = images[i];
        (_traceConstants, _traceFrame) = (uniform, _ctx.Frame.Number);
        var stateInfo = new DescriptorBufferInfo(_ctx.Device.BufferAt(_state.Handle.Index), 0, _state.Size);
        var listInfo = new DescriptorBufferInfo(_lists[_slot].Buffer, 0, (ulong)Math.Max(listed, 1) * 4);

        var writes = stackalloc WriteDescriptorSet[18];
        writes[0] = Acceleration(0, &asWrite);
        writes[1] = Buffer(1, DescriptorType.StorageBuffer, &records);
        writes[2] = Buffer(2, DescriptorType.StorageBuffer, &rayInfo);
        writes[3] = Buffer(3, DescriptorType.UniformBuffer, &uniform);
        for (int i = 0; i < 9; i++) writes[4 + i] = Sampled((uint)(4 + i), &images[i]);
        writes[13] = Buffer(13, DescriptorType.StorageBuffer, &stateInfo);
        writes[14] = Buffer(14, DescriptorType.StorageBuffer, &listInfo);
        var tiles = TileImage;
        writes[15] = Sampled(18, &tiles);
        int count = 16;
        if (CloudShadows)
        {
            for (int i = 0; i < 2; i++) writes[count++] = Sampled((uint)(15 + i), &images[9 + i]);
        }

        cmd.BindPipeline(pipeline);
        cmd.PushDescriptors(program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, count), GpuBindPoint.Compute);

        // Set 1: the bindless table, for the objects' diffuse maps at the hits.
        cmd.BindBindlessTable(program, 1, GpuBindPoint.Compute);
        if (listed > 0) cmd.Dispatch((uint)listed);
    }

    /// <summary>
    /// The samplers a hit shader reads, its bindings 4 to 12, then the cloud maps for bindings 15 and 16 (a trace's or <see cref="GiCache"/>'s);
    /// sets <see cref="TileImage"/>.
    /// </summary>
    void HitImages(DescriptorImageInfo* images, SampledTexture ground, WorldLamps? lamps)
    {
        var g = _ctx.Globals;
        images[0] = SampledImage(_linear, _irradiance);
        images[1] = SampledImage(_linear, _distance);
        images[2] = SampledImage(_nearest, _bases[_slot]);
        images[3] = Image(g, "uAtmoIrradiance", cube: true);
        images[4] = Image(g, "uAtmoAmbientMap", cube: false);
        images[5] = ground.IsNull ? images[2] : new DescriptorImageInfo(_ctx.Device.SamplerAt(ground.Sampler.Index),
            _ctx.Device.ViewAt(ground.View.Index), ImageLayout.General);

        // The lamps' textures (a stand-in while there are none: the shader reads them only when LightGrid.w is set).
        if (lamps is not null)
        {
            var (lampCells, lampIndex, lampData) = lamps.Current;
            images[6] = SampledImage(lamps.NearestSampler, lampCells);
            images[7] = SampledImage(lamps.NearestSampler, lampIndex);
            images[8] = SampledImage(lamps.NearestSampler, lampData);
        }
        else images[6] = images[7] = images[8] = images[2];

        TileImage = lamps is null ? images[2] : SampledImage(lamps.NearestSampler, lamps.TilesTexture);
        if (!CloudShadows) return;

        images[9] = Image(g, "uAtmoClouds", cube: false);
        images[10] = Image(g, "uAtmoCloudsTile", cube: false);
    }

    /// <summary>
    /// The blend of the listed probes' rays into their irradiance and distance tiles.
    /// </summary>
    void Blend(CommandList cmd, GpuProgram program, GpuComputePipeline pipeline, Transient block, GpuTexture bases, int listed)
    {
        var rayInfo = new DescriptorBufferInfo(_ctx.Device.BufferAt(_rays.Handle.Index), 0, _rays.Size);
        var stateInfo = new DescriptorBufferInfo(_ctx.Device.BufferAt(_state.Handle.Index), 0, _state.Size);
        var listInfo = new DescriptorBufferInfo(_lists[_slot].Buffer, 0, (ulong)Math.Max(listed, 1) * 4);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(Constants));
        var images = stackalloc DescriptorImageInfo[3];
        images[0] = new DescriptorImageInfo(default, _ctx.Device.ViewAt(_irradiance.View().Index), ImageLayout.General);
        images[1] = new DescriptorImageInfo(default, _ctx.Device.ViewAt(_distance.View().Index), ImageLayout.General);
        images[2] = new DescriptorImageInfo(_ctx.Device.SamplerAt(_nearest.Index), _ctx.Device.ViewAt(bases.View().Index), ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[7];
        writes[0] = Buffer(0, DescriptorType.StorageBuffer, &rayInfo);
        writes[1] = Buffer(1, DescriptorType.StorageBuffer, &stateInfo);
        writes[2] = Buffer(2, DescriptorType.UniformBuffer, &uniform);
        writes[3] = Storage(3, &images[0]);
        writes[4] = Storage(4, &images[1]);
        writes[5] = Sampled(5, &images[2]);
        writes[6] = Buffer(6, DescriptorType.StorageBuffer, &listInfo);
        cmd.BindPipeline(pipeline);
        cmd.PushDescriptors(program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 7), GpuBindPoint.Compute);
        if (listed > 0) cmd.Dispatch((uint)listed);
    }

    #endregion

    #region Relocation

    /// <summary>
    /// The probes' offsets from their grid places by tile (docs/render-gi.md "Probe relocation"), made on first use.
    /// </summary>
    DeviceBuffer? _offsets;

    /// <summary>
    /// The pass that lists the offsets for the shaders (<see cref="GiShaders.ProbeOffsetPublish"/>), made with <see cref="_offsets"/>.
    /// </summary>
    GpuProgram _publishProgram;

    /// <summary>
    /// The pipeline of <see cref="_publishProgram"/>.
    /// </summary>
    GpuComputePipeline _publishPipeline;

    /// <summary>
    /// The relocation passes (<see cref="GiShaders.ProbeRelocate"/>) by rays per probe, made on first use.
    /// </summary>
    readonly Dictionary<int, (GpuProgram Program, GpuComputePipeline Pipeline)> _relocators = [];

    /// <summary>
    /// The offsets buffer (zero: every probe at its grid place) and the publish pass, on the first frame relocation runs.
    /// </summary>
    void EnsureRelocation()
    {
        if (_offsets is not null) return;

        _offsets = DeviceBuffer.Create(_ctx, (ulong)Probes * 16, BufferUse.Storage | BufferUse.TransferDst, "gi probe offsets");
        var cmd = _ctx.Device.BeginImmediate();
        _ctx.Device.Vk.CmdFillBuffer(cmd, _ctx.Device.BufferAt(_offsets.Handle.Index), 0, _offsets.Size, 0);
        _ctx.Device.EndImmediate(cmd);
        _publishProgram = _ctx.Shaders.Compute(GiShaders.ProbeOffsetPublish, "gi probe offset publish",
        [
            (0, DescriptorType.StorageBuffer), (1, DescriptorType.UniformBuffer), (2, DescriptorType.StorageImage), (3, DescriptorType.StorageBuffer),
        ]);
        _publishPipeline = _ctx.Pipelines.Get(new ComputePipelineDesc(_publishProgram, "gi probe offset publish"));
    }

    /// <summary>
    /// This frame's offsets into the frame slot's <c>uGiBase</c> rows, before anything reads them (<see cref="GiShaders.ProbeOffsetPublish"/>).
    /// </summary>
    void PublishOffsets(CommandList cmd, GpuTexture slotBases, Transient block)
    {
        var stateInfo = new DescriptorBufferInfo(_ctx.Device.BufferAt(_state.Handle.Index), 0, _state.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(Constants));
        var image = new DescriptorImageInfo(default, _ctx.Device.ViewAt(slotBases.View().Index), ImageLayout.General);
        var offsetInfo = new DescriptorBufferInfo(_ctx.Device.BufferAt(_offsets!.Handle.Index), 0, _offsets.Size);
        var writes = stackalloc WriteDescriptorSet[4];
        writes[0] = Buffer(0, DescriptorType.StorageBuffer, &stateInfo);
        writes[1] = Buffer(1, DescriptorType.UniformBuffer, &uniform);
        writes[2] = Storage(2, &image);
        writes[3] = Buffer(3, DescriptorType.StorageBuffer, &offsetInfo);
        cmd.BindPipeline(_publishPipeline);
        cmd.PushDescriptors(_publishProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 4), GpuBindPoint.Compute);
        cmd.Dispatch((uint)(Probes / 64));
        cmd.Barrier(ComputeAfterCompute);
    }

    /// <summary>
    /// This frame's rays move this frame's probes (<see cref="GiShaders.ProbeRelocate"/>), for the next frame's places. Before the blend: it reads
    /// the states the trace read.
    /// </summary>
    void RelocateListed(CommandList cmd, GpuTexture slotBases, Transient block, int listed)
    {
        if (listed <= 0) return;

        var (program, pipeline) = Relocator(Rays);
        var rayInfo = new DescriptorBufferInfo(_ctx.Device.BufferAt(_rays.Handle.Index), 0, _rays.Size);
        var stateInfo = new DescriptorBufferInfo(_ctx.Device.BufferAt(_state.Handle.Index), 0, _state.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(Constants));
        var image = new DescriptorImageInfo(_ctx.Device.SamplerAt(_nearest.Index), _ctx.Device.ViewAt(slotBases.View().Index), ImageLayout.General);
        var listInfo = new DescriptorBufferInfo(_lists[_slot].Buffer, 0, (ulong)listed * 4);
        var offsetInfo = new DescriptorBufferInfo(_ctx.Device.BufferAt(_offsets!.Handle.Index), 0, _offsets.Size);
        var writes = stackalloc WriteDescriptorSet[6];
        writes[0] = Buffer(0, DescriptorType.StorageBuffer, &rayInfo);
        writes[1] = Buffer(1, DescriptorType.StorageBuffer, &stateInfo);
        writes[2] = Buffer(2, DescriptorType.UniformBuffer, &uniform);
        writes[3] = Sampled(3, &image);
        writes[4] = Buffer(4, DescriptorType.StorageBuffer, &listInfo);
        writes[5] = Buffer(5, DescriptorType.StorageBuffer, &offsetInfo);
        cmd.BindPipeline(pipeline);
        cmd.PushDescriptors(program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 6), GpuBindPoint.Compute);
        cmd.Dispatch((uint)listed);
    }

    /// <summary>
    /// The relocation pass for <paramref name="rays"/> rays a probe, made on first use.
    /// </summary>
    (GpuProgram Program, GpuComputePipeline Pipeline) Relocator(int rays)
    {
        if (_relocators.TryGetValue(rays, out var made)) return made;

        var program = _ctx.Shaders.Compute(GiShaders.ProbeRelocate(rays), "gi probe relocate " + rays,
        [
            (0, DescriptorType.StorageBuffer), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer),
            (3, DescriptorType.CombinedImageSampler), (4, DescriptorType.StorageBuffer), (5, DescriptorType.StorageBuffer),
        ]);
        return _relocators[rays] = (program, _ctx.Pipelines.Get(new ComputePipelineDesc(program, "gi probe relocate " + rays)));
    }

    #endregion

    #region Reflections

    /// <summary>
    /// What this frame's probe trace bound (samplers 4..12) for the reflections' rays.
    /// </summary>
    readonly DescriptorImageInfo[] _traceImages = new DescriptorImageInfo[9];

    /// <summary>
    /// The constants this frame's probe trace bound, for the reflections' rays.
    /// </summary>
    DescriptorBufferInfo _traceConstants;

    /// <summary>
    /// The frame of the last probe trace: the probes may not update every frame.
    /// </summary>
    long _traceFrame = -1;

    /// <summary>
    /// The reflections' program (<see cref="GiShaders.Reflect"/>), made on first use.
    /// </summary>
    GpuProgram? _reflectProgram;

    /// <summary>
    /// The pipeline of <see cref="_reflectProgram"/>.
    /// </summary>
    GpuComputePipeline _reflectPipeline;

    /// <summary>
    /// What <see cref="TraceReflections"/> reads of the post chain's frame: the march's texels, the surfaces, the depth and their camera.
    /// </summary>
    public readonly record struct ReflectionFrame(GpuTexture Hit, GpuTexture Surface, GpuTexture Depth, Vector3 Eye, Vector3 Right, Vector3 Up,
        Vector3 Back, Vector2 Tan, Vector2 Planes, int Width, int Height, int Divisor);

    /// <summary>
    /// The reflections' camera block (binding 15), as <see cref="GiShaders.Reflect"/> lays it out.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ReflectionCamera
    {
        /// <summary>
        /// The eye and the camera's axes; the tangents and the depth's planes; the hit target's size and the divisor.
        /// </summary>
        public Vector4 Eye, Right, Up, Back, View, Sizes;
    }

    /// <summary>
    /// The reflections' rays (<see cref="GiShaders.Reflect"/>) into <paramref name="frame"/>'s hit texels, recorded into the post chain's open
    /// segment. Nothing when no probe trace ran this frame: the march's result stands.
    /// </summary>
    public void TraceReflections(CommandList cmd, GiScene scene, in ReflectionFrame frame)
    {
        if (scene.Top is null || _traceFrame != _ctx.Frame.Number) return;

        if (_reflectProgram is null)
        {
            _reflectProgram = _ctx.Shaders.Compute(GiShaders.Reflect, "gi reflect", ReflectBindings(), GpuSetLayout.Bindless);
            _reflectPipeline = _ctx.Pipelines.Get(new ComputePipelineDesc(_reflectProgram.Value, "gi reflect"));
        }

        var cameraInfo = WriteReflectionCamera(frame);
        var top = scene.Top.Handle;
        var asWrite = AccelerationWrite(&top);
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var constants = _traceConstants;
        var hit = new DescriptorImageInfo(default, _ctx.Device.ViewAt(frame.Hit.View().Index), ImageLayout.General);
        var images = stackalloc DescriptorImageInfo[11];
        for (int i = 0; i < _traceImages.Length; i++) images[i] = _traceImages[i];
        images[9] = SampledImage(_nearest, frame.Surface);
        images[10] = SampledImage(_nearest, frame.Depth);
        var tiles = TileImage;

        var writes = stackalloc WriteDescriptorSet[17];
        writes[16] = Sampled(18, &tiles);
        writes[0] = Acceleration(0, &asWrite);
        writes[1] = Buffer(1, DescriptorType.StorageBuffer, &records);
        writes[2] = Storage(2, &hit);
        writes[3] = Buffer(3, DescriptorType.UniformBuffer, &constants);
        for (int i = 0; i < 11; i++) writes[4 + i] = Sampled((uint)(4 + i), &images[i]);
        writes[15] = Buffer(15, DescriptorType.UniformBuffer, &cameraInfo);

        cmd.BeginLabel("gi reflect");
        cmd.Barrier(BarrierBatch.Full);
        cmd.BindPipeline(_reflectPipeline);
        cmd.PushDescriptors(_reflectProgram.Value, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 17), GpuBindPoint.Compute);
        cmd.BindBindlessTable(_reflectProgram.Value, 1, GpuBindPoint.Compute);
        cmd.Dispatch((uint)((frame.Hit.Desc.Width + 7) / 8), (uint)((frame.Hit.Desc.Height + 7) / 8));
        cmd.Barrier(BarrierBatch.Full);
        cmd.EndLabel();
    }

    /// <summary>
    /// <paramref name="frame"/>'s camera in the frame's constant ring, as the reflections' binding 15.
    /// </summary>
    DescriptorBufferInfo WriteReflectionCamera(in ReflectionFrame frame)
    {
        var camera = new ReflectionCamera
        {
            Eye = new Vector4(frame.Eye, 0),
            Right = new Vector4(frame.Right, 0),
            Up = new Vector4(frame.Up, 0),
            Back = new Vector4(frame.Back, 0),
            View = new Vector4(frame.Tan, frame.Planes.X, frame.Planes.Y),
            Sizes = new Vector4(frame.Width, frame.Height, frame.Divisor, 0),
        };
        var block = _ctx.Frame.Constants.Write<ReflectionCamera>(new ReadOnlySpan<ReflectionCamera>(&camera, 1), 256);
        return new DescriptorBufferInfo(_ctx.Device.BufferAt(block.Handle.Index), block.Offset, (ulong)sizeof(ReflectionCamera));
    }

    /// <summary>
    /// The reflections' set 0: the scene, the records, the hit texels, the trace's constants, its samplers and the surfaces and depth, the camera,
    /// the lamps' tiles.
    /// </summary>
    static (uint, DescriptorType)[] ReflectBindings() =>
    [
        (0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer), (2, DescriptorType.StorageImage),
        (3, DescriptorType.UniformBuffer),
        (4, DescriptorType.CombinedImageSampler), (5, DescriptorType.CombinedImageSampler), (6, DescriptorType.CombinedImageSampler),
        (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler), (9, DescriptorType.CombinedImageSampler),
        (10, DescriptorType.CombinedImageSampler), (11, DescriptorType.CombinedImageSampler), (12, DescriptorType.CombinedImageSampler),
        (13, DescriptorType.CombinedImageSampler), (14, DescriptorType.CombinedImageSampler), (15, DescriptorType.UniformBuffer),
        (18, DescriptorType.CombinedImageSampler),
    ];

    #endregion

    #region Shader helpers

    /// <summary>
    /// The probe passes' writes (storage buffers and images) before the next pass's reads, in compute and in the world's shading: unlike a full
    /// barrier, the work of other passes before it need not drain.
    /// </summary>
    static BarrierBatch ComputeAfterCompute => new()
    {
        SrcStages = PipelineStageFlags2.ComputeShaderBit,
        SrcAccess = AccessFlags2.ShaderStorageWriteBit,
        DstStages = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit,
        DstAccess = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderSampledReadBit,
    };

    /// <summary>
    /// A descriptor write of buffer <paramref name="info"/> at <paramref name="binding"/>.
    /// </summary>
    static WriteDescriptorSet Buffer(uint binding, DescriptorType type, DescriptorBufferInfo* info) =>
        new() { SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PBufferInfo = info };

    /// <summary>
    /// A descriptor write of combined image sampler <paramref name="image"/> at <paramref name="binding"/>.
    /// </summary>
    static WriteDescriptorSet Sampled(uint binding, DescriptorImageInfo* image) => new()
    {
        SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1,
        DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = image,
    };

    /// <summary>
    /// A descriptor write of storage image <paramref name="image"/> at <paramref name="binding"/>.
    /// </summary>
    static WriteDescriptorSet Storage(uint binding, DescriptorImageInfo* image) => new()
    {
        SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1,
        DescriptorType = DescriptorType.StorageImage, PImageInfo = image,
    };

    /// <summary>
    /// A descriptor write of the acceleration structure in <paramref name="write"/> at <paramref name="binding"/>.
    /// </summary>
    static WriteDescriptorSet Acceleration(uint binding, WriteDescriptorSetAccelerationStructureKHR* write) => new()
    {
        SType = StructureType.WriteDescriptorSet, PNext = write, DstBinding = binding, DescriptorCount = 1,
        DescriptorType = DescriptorType.AccelerationStructureKhr,
    };

    /// <summary>
    /// The acceleration structure part of a descriptor write, for the top level at <paramref name="top"/>.
    /// </summary>
    static WriteDescriptorSetAccelerationStructureKHR AccelerationWrite(AccelerationStructureKHR* top) => new()
    {
        SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = top,
    };

    /// <summary>
    /// <paramref name="texture"/> read through <paramref name="sampler"/>, in the general layout.
    /// </summary>
    DescriptorImageInfo SampledImage(GpuSampler sampler, GpuTexture texture) =>
        new(_ctx.Device.SamplerAt(sampler.Index), _ctx.Device.ViewAt(texture.View().Index), ImageLayout.General);

    /// <summary>
    /// The image global <paramref name="name"/> with its sampler; a stand-in when it is not published yet.
    /// </summary>
    DescriptorImageInfo Image(FrameGlobals g, string name, bool cube)
    {
        var t = g.Texture(name)?.Invoke() ?? default;
        if (!t.IsNull)
            return new DescriptorImageInfo(_ctx.Device.SamplerAt(t.Sampler.Index), _ctx.Device.ViewAt(t.View.Index), ImageLayout.General);
        // Not published yet (the game sky off): a stand-in the shader never reads (uAtmoMaps says the map is absent).
        var standIn = _ctx.Dummy(FrameGlobals.Sampler2D(name, cube));
        return new DescriptorImageInfo(_ctx.Device.SamplerAt(standIn.Sampler.Index), _ctx.Device.ViewAt(standIn.View.Index), ImageLayout.General);
    }

    /// <summary>
    /// The uniform global <paramref name="name"/>'s value (default when it is not published).
    /// </summary>
    static T Read<T>(FrameGlobals g, string name) where T : unmanaged
    {
        T value = default;
        g.UniformValue(name)?.TryRead(MemoryMarshal.AsBytes(new Span<T>(ref value)));
        return value;
    }

    /// <summary>
    /// A random rotation: a random axis and angle.
    /// </summary>
    Matrix4x4 RandomRotation()
    {
        var axis = Vector3.Normalize(new Vector3(_random.NextSingle() * 2 - 1, _random.NextSingle() * 2 - 1,
            _random.NextSingle() * 2 - 1) + new Vector3(1e-4f));
        return Matrix4x4.CreateFromAxisAngle(axis, _random.NextSingle() * MathF.Tau);
    }

    /// <summary>
    /// <paramref name="a"/> modulo <paramref name="n"/>, never negative.
    /// </summary>
    static int Mod(int a, int n) => ((a % n) + n) % n;

    #endregion

    #region Diagnostics and disposal

    /// <summary>
    /// A line describing the cascades, rays, phases and fog hiding, for the log.
    /// </summary>
    public string Describe()
    {
        string state = $"{Cascades} cascades of {Columns}×{Layers}×{Columns} probes ({Layout(0).Spacing:0} and {Grid[1].Spacing:0} apart), "
            + $"{Rays} rays each, 1/{Phases} a frame, {(Enabled ? "lighting" : "not lighting")}";
        string hidden = _hiddenValid
            ? $"; fog hides {Hidden[0]} + {Hidden[1]} probes (each updated every {HiddenPeriod}th round; "
                + $"solved {Solves} times, {SolveMs / Math.Max(Solves, 1):0.00} ms each), {Listed} listed this frame"
            : "";
        return state + hidden;
    }

    /// <summary>
    /// Frees the atlases, the bases, the buffers and the programs.
    /// </summary>
    public void Dispose()
    {
        _irradiance.Dispose();
        _distance.Dispose();
        foreach (var b in _bases) b.Dispose();
        foreach (var l in _lists) _ctx.Device.DeferFree(l);
        _rays.Dispose();
        _state.Dispose();
        foreach (var v in _variants.Values)
        {
            _ctx.Shaders.Dispose(v.Trace);
            _ctx.Shaders.Dispose(v.Blend);
        }

        _offsets?.Dispose();
        if (_offsets is not null) _ctx.Shaders.Dispose(_publishProgram);
        foreach (var r in _relocators.Values) _ctx.Shaders.Dispose(r.Program);
        _ctx.Shaders.Dispose(_invalidateProgram);
        if (_reflectProgram is { } reflect) _ctx.Shaders.Dispose(reflect);
    }

    #endregion
}
