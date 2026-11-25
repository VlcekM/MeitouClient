using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// The probe GI on Metal (docs/render-gi.md "Metal"): Vulkan's probes (<see cref="GiProbes"/>) over the Metal traced scene
/// (<see cref="MetalGiScene"/>), with the same shaders (<see cref="GiShaders.ProbeTrace(int, bool, bool)"/>, <see cref="GiShaders.ProbeBlend"/>,
/// <see cref="GiShaders.ProbeInvalidate"/>) translated with their set 0 as plain bindings (<see cref="MetalShaders.ComputeDiscrete"/>): the further
/// bounces, the lamps, the textures and the foliage at the hits, the interior grid, the sleeping and fog-hidden probes' slower updates. Render
/// thread only.
/// </summary>
internal sealed unsafe class MetalProbeGi : IProbeGi
{
    #region Constants and state

    /// <summary>
    /// The probe grid (<see cref="GiProbes"/>'): columns, layers, the octahedral tiles' texels, the cascades and all probes.
    /// </summary>
    const int Columns = GiProbes.Columns, Layers = GiProbes.Layers, IrradianceTexels = GiProbes.IrradianceTexels,
        DistanceTexels = GiProbes.DistanceTexels, Cascades = GiProbes.Cascades, Probes = GiProbes.Probes;

    /// <summary>
    /// Per cascade: the spacing across, the spacing up, the rays' length (<see cref="GiProbes"/>' grid).
    /// </summary>
    static readonly (float Spacing, float SpacingY, float RayLength)[] Grid = [(128, 96, 4000), (512, 384, 16000)];

    /// <summary>
    /// The backend.
    /// </summary>
    readonly MetalContext _ctx;

    /// <summary>
    /// The traced scene.
    /// </summary>
    readonly MetalGiScene _scene;

    /// <summary>
    /// The trace and blend programs and their pipelines by rays per probe and cloud shadows, made on first use.
    /// </summary>
    readonly Dictionary<(int Rays, bool Relocate, bool Clouds), (MetalProgram Trace, GpuComputePipeline TracePipeline, MetalProgram Blend,
        GpuComputePipeline BlendPipeline)> _variants = [];

    /// <summary>
    /// The invalidation's pipeline and program.
    /// </summary>
    readonly GpuComputePipeline _invalidate;

    /// <summary>
    /// See <see cref="_invalidate"/>.
    /// </summary>
    readonly MetalProgram _invalidateProgram;

    /// <summary>
    /// The octahedral irradiance and distance atlases (both RGBA16F), one bordered tile per probe.
    /// </summary>
    readonly GpuTexture _irradiance, _distance;

    /// <summary>
    /// The columns' base heights per frame slot (R32F, a row of columns per cascade; the rows below for relocated offsets).
    /// </summary>
    readonly GpuTexture[] _bases;

    /// <summary>
    /// The rays' results (16 bytes a ray, 64 a probe) and each probe's state (16 bytes; w 0: unplaced).
    /// </summary>
    readonly MTLBuffer _rays, _state;

    /// <summary>
    /// Per frame slot: the probes this frame traces and the constants.
    /// </summary>
    readonly MTLBuffer[] _lists, _constants;

    /// <summary>
    /// The linear and the nearest clamped samplers the atlases and the bases are read with.
    /// </summary>
    readonly GpuSampler _linear, _nearest;

    /// <summary>
    /// Per cascade two vectors (<c>uGiGrid</c>): the grid's world origin x and z, its spacing across and up; its toroidal offsets and twice its
    /// spacing.
    /// </summary>
    readonly Vector4[] _grid = new Vector4[Cascades * 2];

    /// <summary>
    /// The columns' base heights by cascade and toroidal row and column.
    /// </summary>
    readonly float[] _baseHeights = new float[Columns * Columns * Cascades];

    /// <summary>
    /// The grid origin each cascade was last placed at, in columns, and whether its bases were sampled.
    /// </summary>
    readonly (int X, int Z)[] _placed = new (int, int)[Cascades];

    /// <summary>
    /// See <see cref="_placed"/>.
    /// </summary>
    readonly bool[] _basesPlaced = new bool[Cascades];

    /// <summary>
    /// The layers the fog hides per probe column.
    /// </summary>
    readonly ProbeFogCache _fogCache = new();

    /// <summary>
    /// The source of each update's random ray rotation (seeded, so runs repeat).
    /// </summary>
    readonly Random _random = new(1);

    /// <summary>
    /// The terrain's fine window the bases were sampled from.
    /// </summary>
    object? _placedHeights;

    /// <summary>
    /// Whether the first cascade is the interior grid (32 across, 24 up round the interior's focus), and the focus's storey.
    /// </summary>
    bool _interiorGrid;

    /// <summary>
    /// See <see cref="_interiorGrid"/>.
    /// </summary>
    int _interiorLevel;

    /// <summary>
    /// The frame slot of the last update, and whether the probes were updated (the shaders read them only then).
    /// </summary>
    int _slot;

    /// <summary>
    /// See <see cref="_slot"/>.
    /// </summary>
    bool _updated;

    /// <summary>
    /// Makes the scene, the programs, the atlases, the bases, the buffers, clears every probe to invalid and unplaced and publishes the probes' globals.
    /// </summary>
    public MetalProbeGi(MetalContext ctx)
    {
        _ctx = ctx;
        _scene = new MetalGiScene(ctx);
        var shaders = (MetalShaders)ctx.Shaders;
        var invalidate = shaders.ComputeDiscrete(GiShaders.ProbeInvalidate, "metal gi probe invalidate");
        _invalidate = ctx.Pipelines.Get(new ComputePipelineDesc(invalidate, "metal gi probe invalidate"));
        _invalidateProgram = shaders.Backend(invalidate);

        int rows = Cascades * Columns * Layers;
        var use = TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst;
        _irradiance = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, Columns * (IrradianceTexels + 2), rows * (IrradianceTexels + 2),
            Use: use, Name: "metal gi irradiance"));
        // RGBA16F, not Vulkan's RG16F: the blend reads and writes the atlas, and Metal has no read-write access to two-channel formats.
        _distance = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, Columns * (DistanceTexels + 2), rows * (DistanceTexels + 2), Use: use,
            Name: "metal gi distance"));
        _bases = new GpuTexture[MetalContext.FrameRingDepth];
        for (int i = 0; i < _bases.Length; i++)
            _bases[i] = ctx.CreateTexture(new TextureDesc(InternalFormat.R32F, Columns, Columns * Cascades * (1 + Layers), Use: use, Name: "metal gi bases"));

        _rays = ctx.Device.NewBuffer((ulong)Probes * GiProbes.MaxRays * 16, MTLResourceOptions.ResourceStorageModePrivate);
        _state = ctx.Device.NewBuffer((ulong)Probes * 16, MTLResourceOptions.ResourceStorageModeShared);
        new Span<byte>((void*)_state.Contents, Probes * 16).Clear();
        _lists = NewBuffers((ulong)Probes * 4, "metal gi probe list");
        _constants = NewBuffers((ulong)sizeof(GiProbes.Constants), "metal gi constants");
        ClearAtlases(rows);

        _linear = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Linear, TextureMagFilter.Linear));
        _nearest = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));
        var g = ctx.Globals;
        g.PublishUniform("uGiParams", () => Params);
        g.PublishUniformArray("uGiGrid", _grid, () => _grid.Length);
        g.Publish("uGiIrradiance", () => new SampledTexture(_linear, _irradiance.View()));
        g.Publish("uGiDistance", () => new SampledTexture(_linear, _distance.View()));
        g.Publish("uGiBase", () => new SampledTexture(_nearest, _bases[_slot].View()));
    }

    /// <summary>
    /// One shared buffer per frame slot.
    /// </summary>
    MTLBuffer[] NewBuffers(ulong size, string label)
    {
        var buffers = new MTLBuffer[MetalContext.FrameRingDepth];
        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i] = _ctx.Device.NewBuffer(size, MTLResourceOptions.ResourceStorageModeShared);
            buffers[i].Label = (NSString)label;
        }

        return buffers;
    }

    /// <summary>
    /// Every probe starts invalid (alpha 0: no surface reads it, no bounce sees it).
    /// </summary>
    void ClearAtlases(int rows)
    {
        using var batch = _ctx.Uploads.Begin();
        uint irrWidth = (uint)(Columns * (IrradianceTexels + 2)), irrHeight = (uint)(rows * (IrradianceTexels + 2));
        uint distWidth = (uint)(Columns * (DistanceTexels + 2)), distHeight = (uint)(rows * (DistanceTexels + 2));
        batch.Write(_irradiance, 0, 0, new PixelRect(0, 0, irrWidth, irrHeight), new byte[irrWidth * irrHeight * 8]);
        batch.Write(_distance, 0, 0, new PixelRect(0, 0, distWidth, distHeight), new byte[distWidth * distHeight * 8]);
    }

    #endregion

    #region Settings

    /// <summary>
    /// Whether the world shaders take their ambient from the probes (the <c>gi</c> switch).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// False: indoor surfaces keep the original ambient (<c>uGiParams.w</c> 0).
    /// </summary>
    public bool InteriorEnabled { get; set; } = true;

    /// <summary>
    /// False while the frame does not update the probes: the shaders then do not read them.
    /// </summary>
    public bool Active { get; set; } = true;

    /// <summary>
    /// False: the probes are updated but the world's shading does not read them.
    /// </summary>
    public bool Shade { get; set; } = true;

    /// <summary>
    /// How much the probes replace the sky's light (<see cref="GiProbes.Strength"/>).
    /// </summary>
    public float Strength { get; set; } = 1;

    /// <summary>
    /// 1 + the share of the game's ambient indoor surfaces get at least (<see cref="GiProbes.InteriorGain"/>).
    /// </summary>
    public float InteriorGain { get; set; } = 1.25f;

    /// <summary>
    /// True: the lit fragments write their surface and the post chain adds the probes' light (<c>uGiParams.z</c> bit 1).
    /// </summary>
    public bool Resolve { get; set; }

    /// <summary>
    /// True: the probes move out of geometry (docs/render-gi.md "Probe relocation"); with <see cref="Resolve"/>.
    /// </summary>
    public bool Relocate { get; set; }

    /// <summary>
    /// Rays each probe traces per update (32 or 64, the "GI 64 probe rays" row).
    /// </summary>
    public int Rays { get; set; } = GiProbes.DefaultRays;

    /// <summary>
    /// The cloud transmission dims the sun at the probes' hits (the "GI cloud shadows" row).
    /// </summary>
    public bool CloudShadows { get; set; }

    /// <summary>
    /// The per-frame hysteresis of 64 rays, the update phases, the far cascade's period, the sleepers' and the hidden probes' periods
    /// (<see cref="GiProbes"/>' defaults).
    /// </summary>
    const float Hysteresis = 0.97f;

    /// <summary>
    /// See <see cref="Hysteresis"/>.
    /// </summary>
    const int Phases = 4, FarPeriod = 2, SleepPeriod = 8, HiddenPeriod = 16;

    /// <summary>
    /// The objects' albedo where a hit has no texture.
    /// </summary>
    static readonly Vector3 ObjectAlbedo = new(0.35f, 0.32f, 0.28f);

    /// <summary>
    /// The probe updates run so far; their count picks each frame's phase.
    /// </summary>
    public long Updates { get; private set; }

    /// <summary>
    /// Probes this frame's list holds.
    /// </summary>
    public int Listed { get; private set; }

    /// <summary>
    /// <c>uGiParams</c>: x on and filled, y the strength, z bit 1 the resolve, w the indoor gain (0 bypasses indoor GI).
    /// </summary>
    Vector4 Params => new(Enabled && Active && Shade && _updated ? 1 : 0, Strength, Resolve ? 1 : 0, InteriorEnabled ? MathF.Max(1, InteriorGain) : 0);

    /// <summary>
    /// Cascade <paramref name="cascade"/>'s spacing across, up and rays' length: the interior grid's for the first while it is placed.
    /// </summary>
    (float Spacing, float SpacingY, float RayLength) Layout(int cascade) =>
        cascade == 0 && _interiorGrid ? (32, 24, Grid[0].RayLength) : Grid[cascade];

    /// <summary>
    /// A line for the log: the probes and the scene.
    /// </summary>
    public string Describe() => $"metal probes {Probes} ({Rays} rays, {Listed} listed), scene {_scene.Describe()}";

    #endregion

    #region Update

    /// <summary>
    /// This frame's scene, the grids placed round the eye (the first round <paramref name="interiorFocus"/> when set), the list, the constants and
    /// the probe passes.
    /// </summary>
    public void Update(Vector3 eye, HeightSnapshot heights, WorldObjectRenderer? objects, FoliageRenderer? foliage, WorldLamps? lamps,
        FogVolumes? fog, Vector3? interiorFocus)
    {
        _scene.Update(eye, heights, objects, foliage, lamps);
        if (_scene.Top is null || !(Enabled && Active && Shade)) return;

        _slot = _ctx.Frame.Slot;
        int phase = (int)(Updates % Phases);
        bool moved = PlaceGrids(eye, heights, interiorFocus);
        BuildList(phase, fog, eye, moved);
        using (var batch = _ctx.Uploads.Begin())
            batch.Write(_bases[_slot], 0, 0, new PixelRect(0, 0, Columns, Columns * Cascades), MemoryMarshal.AsBytes(_baseHeights.AsSpan()));
        WriteConstants(phase, lamps);

        Dispatch(moved, lamps);
        _updated = true;
        Updates++;
        if (Updates % 600 == 1 && Environment.GetEnvironmentVariable("MEITOU_METAL_GI_LOG") == "1") Console.WriteLine($"gi        {Describe()}");
    }

    /// <summary>
    /// Places each cascade's grid round <paramref name="eye"/> and samples the bases of the columns that scrolled in (<see cref="GiProbes"/>'
    /// placement); true when any grid moved or took new bases.
    /// </summary>
    bool PlaceGrids(Vector3 eye, HeightSnapshot heights, Vector3? interiorFocus)
    {
        bool moved = false;
        int level = interiorFocus is { } focus ? (int)MathF.Floor(focus.Y / 96) : 0;
        bool layoutChanged = _interiorGrid != interiorFocus.HasValue || _interiorLevel != level;
        (_interiorGrid, _interiorLevel) = (interiorFocus.HasValue, level);

        for (int c = 0; c < Cascades; c++)
        {
            var (s, sy, _) = Layout(c);
            var centre = c == 0 ? interiorFocus ?? eye : eye;
            int ox = (int)MathF.Floor(centre.X / s) - Columns / 2, oz = (int)MathF.Floor(centre.Z / s) - Columns / 2;
            _grid[c * 2] = new Vector4(ox * s, oz * s, s, sy);
            _grid[c * 2 + 1] = new Vector4(Mod(ox, Columns), Mod(oz, Columns), 2 * s, Relocate ? 1 : 0);

            bool sameHeights = ReferenceEquals(_placedHeights, heights.Fine) && (c != 0 || !layoutChanged);
            if (_placed[c] == (ox, oz) && sameHeights) continue;

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
    /// The bases of cascade <paramref name="c"/>'s columns: the lowest ground under each column's cell (raised to the storey below the interior's
    /// focus on the interior grid); with <paramref name="keep"/> the columns also in the old window keep theirs.
    /// </summary>
    void SampleBases(HeightSnapshot heights, int c, float s, int ox, int oz, bool keep, int oldX, int oldZ)
    {
        for (int k = 0; k < Columns; k++)
        {
            for (int i = 0; i < Columns; i++)
            {
                if (keep && (uint)(ox + i - oldX) < Columns && (uint)(oz + k - oldZ) < Columns) continue;

                float x = (ox + i) * s, z = (oz + k) * s;
                float h = heights.HeightAt(x, z);
                h = MathF.Min(h, MathF.Min(heights.HeightAt(x + s * 0.4f, z), heights.HeightAt(x - s * 0.4f, z)));
                h = MathF.Min(h, MathF.Min(heights.HeightAt(x, z + s * 0.4f), heights.HeightAt(x, z - s * 0.4f)));
                if (c == 0 && _interiorGrid) h = MathF.Max(h, (_interiorLevel - 1) * 96);

                _baseHeights[(c * Columns + Mod(oz + k, Columns)) * Columns + Mod(ox + i, Columns)] = h;
            }
        }
    }

    /// <summary>
    /// <paramref name="a"/> modulo <paramref name="n"/>, never negative.
    /// </summary>
    static int Mod(int a, int n) => (a % n + n) % n;

    /// <summary>
    /// This frame's share of the probes into the slot's list; a probe the fog hides is listed only on every <see cref="HiddenPeriod"/>-th round.
    /// </summary>
    void BuildList(int phase, FogVolumes? fog, Vector3 eye, bool scrolled)
    {
        bool hide = fog is { CullActive: true };
        if (hide) _fogCache.Update(fog!, eye, scrolled, _grid, _baseHeights, c => (Layout(c).Spacing, Layout(c).SpacingY));
        else _fogCache.Invalidate();

        var list = new Span<uint>((void*)_lists[_slot].Contents, Probes);
        ReadOnlySpan<float> offsets = [_grid[1].X, _grid[1].Y, _grid[3].X, _grid[3].Y];
        Listed = GiFogHide.List(list, Phases, phase, (uint)(Updates / Phases), hide ? HiddenPeriod : 0, FarPeriod, _fogCache.HiddenLayers, offsets);
    }

    /// <summary>
    /// The hysteresis of one update (<see cref="GiProbes"/>' rule: the memory of 32 rays whatever the count).
    /// </summary>
    static float UpdateHysteresis(int phases)
    {
        float h = MathF.Pow(Hysteresis, phases);
        float k = (1 - h) / (1 + h) * GiProbes.DefaultRays / GiProbes.MaxRays;
        return (1 - k) / (1 + k);
    }

    /// <summary>
    /// This update's constants into the slot's buffer (<see cref="GiProbes.Constants"/>, the shaders' <c>Params</c>).
    /// </summary>
    void WriteConstants(int phase, WorldLamps? lamps)
    {
        var g = _ctx.Globals;
        bool ground = g.Texture("uWeatherGround")?.Invoke() is { IsNull: false };
        var constants = new GiProbes.Constants
        {
            Params = new Vector4(1, phase, Phases, UpdateHysteresis(Phases)),
            Grid0 = _grid[0],
            Grid1 = _grid[1],
            Grid2 = _grid[2],
            Grid3 = _grid[3],
            Rotation = RandomRotation(),
            Origin = new Vector4(_scene.Origin, Updates),
            SunLight = new Vector4(Read<Vector3>(g, "uAtmoSunLight"), 0),
            LightDir = Read<Vector4>(g, "uAtmoLight"),
            Maps = Read<Vector4>(g, "uAtmoMaps"),
            Albedo = new Vector4(ObjectAlbedo, ground ? 1 : 0),
            RayLength = new Vector4(Grid[0].RayLength, Grid[1].RayLength, SleepPeriod, HiddenPeriod),
            LightGrid = lamps?.Grid ?? default,
            Extra = new Vector4(_interiorGrid ? 1 : 0, FarPeriod, 0, 0),
            Cloud = Read<Vector4>(g, "uAtmoCloud"),
            CloudWind = Read<Vector4>(g, "uAtmoCloudWind"),
            Sun = Read<Vector4>(g, "uAtmoSun"),
        };
        *(GiProbes.Constants*)_constants[_slot].Contents = constants;
    }

    /// <summary>
    /// A uniformly random rotation, so successive updates sample new directions.
    /// </summary>
    Matrix4x4 RandomRotation()
    {
        var axis = Vector3.Normalize(new Vector3(_random.NextSingle() * 2 - 1, _random.NextSingle() * 2 - 1, _random.NextSingle() * 2 - 1)
            + new Vector3(1e-4f));
        return Matrix4x4.CreateFromAxisAngle(axis, _random.NextSingle() * MathF.Tau);
    }

    /// <summary>
    /// A published uniform's value as <typeparamref name="T"/>; default when it is missing.
    /// </summary>
    static T Read<T>(FrameGlobals g, string name) where T : unmanaged
    {
        var u = g.UniformValue(name);
        if (u is null) return default;

        Span<byte> bytes = stackalloc byte[sizeof(T)];
        return u.TryRead(bytes) ? MemoryMarshal.Read<T>(bytes) : default;
    }

    #endregion

    #region Passes

    /// <summary>
    /// The invalidation (after a move), the trace and the blend, in one compute encoder (its dispatches run in order).
    /// </summary>
    void Dispatch(bool moved, WorldLamps? lamps)
    {
        var variant = Variant(Rays, Relocate, CloudShadows);
        var cmd = (MetalCommandList)_ctx.BeginNative("gi metal");
        if (moved) Invalidate(cmd);
        if (Relocate) PublishOffsets(cmd);
        if (Listed > 0)
        {
            Trace(cmd, variant.Trace, variant.TracePipeline, lamps);
            if (Relocate) RelocateListed(cmd);
            Blend(cmd, variant.Blend, variant.BlendPipeline);
        }

        cmd.EndCompute();
        _ctx.EndNative(cmd);
    }

    /// <summary>
    /// The trace and blend for <paramref name="rays"/> rays a probe, with the cloud shadows, made on first use.
    /// </summary>
    (MetalProgram Trace, GpuComputePipeline TracePipeline, MetalProgram Blend, GpuComputePipeline BlendPipeline) Variant(int rays, bool relocate,
        bool clouds)
    {
        if (_variants.TryGetValue((rays, relocate, clouds), out var made)) return made;

        var shaders = (MetalShaders)_ctx.Shaders;
        string name = $"metal gi probe trace {rays}{(relocate ? " relocated" : "")}{(clouds ? " clouds" : "")}";
        var trace = shaders.ComputeDiscrete(GiShaders.ProbeTrace(rays, relocate, clouds), name);
        var blend = shaders.ComputeDiscrete(GiShaders.ProbeBlend(rays), "metal gi probe blend " + rays);
        return _variants[(rays, relocate, clouds)] = (shaders.Backend(trace), _ctx.Pipelines.Get(new ComputePipelineDesc(trace, name)),
            shaders.Backend(blend), _ctx.Pipelines.Get(new ComputePipelineDesc(blend, "metal gi probe blend " + rays)));
    }

    /// <summary>
    /// Hides the tiles whose probe moved until their update (<see cref="GiShaders.ProbeInvalidate"/>).
    /// </summary>
    void Invalidate(MetalCommandList cmd)
    {
        cmd.BindPipeline(_invalidate);
        var e = cmd.ComputeEncoder();
        e.SetBuffer(_state, 0, 0);
        e.SetBuffer(_constants[_slot], 0, 1);
        e.SetTexture(_ctx.TextureAt(_irradiance), 2);
        BindSampled(e, _invalidateProgram, 3, new SampledTexture(_nearest, _bases[_slot].View()));
        cmd.Dispatch(Probes / 64);
    }

    /// <summary>
    /// The listed probes' rays against the scene: the probes, the sky's maps, the ground, the lamps (and the clouds), the objects' maps through the
    /// bindless table; the scene's structures and buffers made resident.
    /// </summary>
    void Trace(MetalCommandList cmd, MetalProgram program, GpuComputePipeline pipeline, WorldLamps? lamps)
    {
        cmd.BindPipeline(pipeline);
        var e = cmd.ComputeEncoder();
        e.SetAccelerationStructure(_scene.Top!.Value, 0);
        e.SetBuffer(_scene.Records, 0, 1);
        e.SetBuffer(_rays, 0, 2);
        e.SetBuffer(_constants[_slot], 0, 3);
        e.SetBuffer(_state, 0, 13);
        e.SetBuffer(_lists[_slot], 0, 14);
        BindHitImages(e, program, lamps);
        cmd.BindBindlessTable(new GpuProgram(program.Info.Index, program.Info), Frame.NativeShaders.BindlessSet, GpuBindPoint.Compute);
        cmd.UseResources(_scene.Resident, _scene.Resident.Length);
        cmd.Dispatch((uint)Listed);
    }

    /// <summary>
    /// The samplers a hit reads (bindings 4 to 12, 15 and 16, 18): the probes, the sky, the ground, the lamps and their tiles, the clouds.
    /// </summary>
    void BindHitImages(MTLComputeCommandEncoder e, MetalProgram program, WorldLamps? lamps)
    {
        var bases = new SampledTexture(_nearest, _bases[_slot].View());
        BindSampled(e, program, 4, new SampledTexture(_linear, _irradiance.View()));
        BindSampled(e, program, 5, new SampledTexture(_linear, _distance.View()));
        BindSampled(e, program, 6, bases);
        BindSampled(e, program, 7, Global("uAtmoIrradiance", cube: true));
        BindSampled(e, program, 8, Global("uAtmoAmbientMap", cube: false));
        var ground = _ctx.Globals.Texture("uWeatherGround")?.Invoke() ?? default;
        BindSampled(e, program, 9, ground.IsNull ? bases : ground);
        BindSampled(e, program, 15, Global("uAtmoClouds", cube: false));
        BindSampled(e, program, 16, Global("uAtmoCloudsTile", cube: false));

        if (lamps is null)
        {
            foreach (int b in (ReadOnlySpan<int>)[10, 11, 12, 18]) BindSampled(e, program, b, bases);
            return;
        }

        var (cells, index, data) = lamps.Current;
        BindSampled(e, program, 10, new SampledTexture(lamps.NearestSampler, cells.View()));
        BindSampled(e, program, 11, new SampledTexture(lamps.NearestSampler, index.View()));
        BindSampled(e, program, 12, new SampledTexture(lamps.NearestSampler, data.View()));
        BindSampled(e, program, 18, new SampledTexture(lamps.NearestSampler, lamps.TilesTexture.View()));
    }

    /// <summary>
    /// The blend of the listed probes' rays into their tiles (<see cref="GiShaders.ProbeBlend"/>).
    /// </summary>
    void Blend(MetalCommandList cmd, MetalProgram program, GpuComputePipeline pipeline)
    {
        cmd.BindPipeline(pipeline);
        var e = cmd.ComputeEncoder();
        e.SetBuffer(_rays, 0, 0);
        e.SetBuffer(_state, 0, 1);
        e.SetBuffer(_constants[_slot], 0, 2);
        e.SetTexture(_ctx.TextureAt(_irradiance), 3);
        e.SetTexture(_ctx.TextureAt(_distance), 4);
        BindSampled(e, program, 5, new SampledTexture(_nearest, _bases[_slot].View()));
        e.SetBuffer(_lists[_slot], 0, 6);
        cmd.Dispatch((uint)Listed);
    }

    /// <summary>
    /// A sampled texture at texture and sampler index <paramref name="binding"/> (a combined sampler's two halves in the discrete set).
    /// </summary>
    void BindSampled(MTLComputeCommandEncoder e, MetalProgram program, int binding, SampledTexture sampled)
    {
        var (texture, sampler) = _ctx.Sampled(sampled);
        e.SetTexture(texture, (ulong)binding);

        int slot = program.SamplerSlot(binding);
        if (slot >= 0) e.SetSamplerState(sampler, (ulong)slot);
    }

    /// <summary>
    /// A published global texture, a stand-in when none is published.
    /// </summary>
    SampledTexture Global(string name, bool cube)
    {
        var texture = _ctx.Globals.Texture(name)?.Invoke() ?? default;
        return texture.IsNull ? _ctx.Dummy(FrameGlobals.Sampler2D(name, cube)) : texture;
    }

    #endregion

    #region Relocation

    /// <summary>
    /// The probes' offsets from their grid places by tile (docs/render-gi.md "Probe relocation"), made on first use with the passes that publish
    /// and move them.
    /// </summary>
    MTLBuffer _offsets;

    /// <summary>
    /// The offsets' publish (<see cref="GiShaders.ProbeOffsetPublish"/>) and the relocation (<see cref="GiShaders.ProbeRelocate"/>) by rays.
    /// </summary>
    (MetalProgram Program, GpuComputePipeline Pipeline) _publish;

    /// <summary>
    /// See <see cref="_publish"/>.
    /// </summary>
    readonly Dictionary<int, (MetalProgram Program, GpuComputePipeline Pipeline)> _relocators = [];

    /// <summary>
    /// The offsets (zero: every probe at its grid place) and the publish pass, on the first frame relocation runs.
    /// </summary>
    void EnsureRelocation()
    {
        if (_offsets.NativePtr != IntPtr.Zero) return;

        _offsets = _ctx.Device.NewBuffer((ulong)Probes * 16, MTLResourceOptions.ResourceStorageModeShared);
        _offsets.Label = (NSString)"metal gi probe offsets";
        new Span<byte>((void*)_offsets.Contents, Probes * 16).Clear();
        _publish = Compute(GiShaders.ProbeOffsetPublish, "metal gi probe offset publish");
    }

    /// <summary>
    /// A discrete-set compute program and its pipeline.
    /// </summary>
    (MetalProgram Program, GpuComputePipeline Pipeline) Compute(string glsl, string name)
    {
        var shaders = (MetalShaders)_ctx.Shaders;
        var program = shaders.ComputeDiscrete(glsl, name);
        return (shaders.Backend(program), _ctx.Pipelines.Get(new ComputePipelineDesc(program, name)));
    }

    /// <summary>
    /// This frame's offsets into the slot's <c>uGiBase</c> rows below the column bases, before anything reads them.
    /// </summary>
    void PublishOffsets(MetalCommandList cmd)
    {
        EnsureRelocation();
        cmd.BindPipeline(_publish.Pipeline);
        var e = cmd.ComputeEncoder();
        e.SetBuffer(_state, 0, 0);
        e.SetBuffer(_constants[_slot], 0, 1);
        e.SetTexture(_ctx.TextureAt(_bases[_slot]), 2);
        e.SetBuffer(_offsets, 0, 3);
        cmd.Dispatch(Probes / 64);
    }

    /// <summary>
    /// This frame's rays move this frame's probes for the next frame's places; before the blend (it reads the states the trace read).
    /// </summary>
    void RelocateListed(MetalCommandList cmd)
    {
        if (!_relocators.TryGetValue(Rays, out var relocator))
            _relocators[Rays] = relocator = Compute(GiShaders.ProbeRelocate(Rays), "metal gi probe relocate " + Rays);

        cmd.BindPipeline(relocator.Pipeline);
        var e = cmd.ComputeEncoder();
        e.SetBuffer(_rays, 0, 0);
        e.SetBuffer(_state, 0, 1);
        e.SetBuffer(_constants[_slot], 0, 2);
        BindSampled(e, relocator.Program, 3, new SampledTexture(_nearest, _bases[_slot].View()));
        e.SetBuffer(_lists[_slot], 0, 4);
        e.SetBuffer(_offsets, 0, 5);
        cmd.Dispatch((uint)Listed);
    }

    #endregion

    #region Disposal

    /// <summary>
    /// Releases the scene, the atlases, the bases and the buffers.
    /// </summary>
    public void Dispose()
    {
        _scene.Dispose();
        _ctx.ReleaseTexture(_irradiance);
        _ctx.ReleaseTexture(_distance);
        foreach (var b in _bases) _ctx.ReleaseTexture(b);
        _rays.Dispose();
        _state.Dispose();
        foreach (var b in _lists) b.Dispose();
        foreach (var b in _constants) b.Dispose();
        if (_offsets.NativePtr != IntPtr.Zero) _offsets.Dispose();
    }

    #endregion
}
