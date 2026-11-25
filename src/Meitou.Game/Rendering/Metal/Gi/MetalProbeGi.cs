using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using Silk.NET.Vulkan;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// Native Metal probe GI: terrain plus the nearby world objects traced as a small TLAS, one probe
/// group per threadgroup, into the same octahedral atlas the world shaders already read. This is
/// deliberately a first slice: objects use a constant albedo, foliage and lamps are not traced, and
/// probe updates overwrite rather than blend (so the first frames pop into place).
/// </summary>
internal sealed unsafe class MetalProbeGi : IProbeGi
{
    #region Probe update

    /// <summary>
    /// The probe grid of each cascade: <c>Columns</c>² columns of <c>Layers</c> probes; each probe's octahedral irradiance tile is
    /// <c>IrradianceTexels</c>² and its distance tile <c>DistanceTexels</c>² texels (plus a one-texel border on each side).
    /// </summary>
    const int Columns = 32, Layers = 8, IrradianceTexels = 8, DistanceTexels = 16, Cascades = 2;
    /// <summary>
    /// Rays per probe (one threadgroup's threads), and the update phases: outside a full update a frame updates one probe in <c>Phases</c>.
    /// </summary>
    const int Rays = 32, Phases = 4;
    /// <summary>
    /// All probes of all cascades.
    /// </summary>
    const int Probes = Columns * Columns * Layers * Cascades;

    /// <summary>
    /// Each cascade's probe spacing, horizontal and vertical, and its rays' length (world units; GiProbes' grid).
    /// </summary>
    static readonly (float Spacing, float SpacingY, float RayLength)[] Layout = [(128, 96, 4000), (512, 384, 16000)];
    /// <summary>
    /// The per-frame hysteresis of 64 rays (GiProbes.Hysteresis); <see cref="UpdateHysteresis"/> turns it into one update's of <see cref="Rays"/>.
    /// </summary>
    const float Hysteresis = 0.97f;
    /// <summary>
    /// The objects' albedo at a hit (GiProbes.ObjectAlbedo: their textures are not read).
    /// </summary>
    static readonly Vector3 ObjectAlbedo = new(0.35f, 0.32f, 0.28f);

    /// <summary>
    /// The Metal backend everything is made on and recorded into.
    /// </summary>
    readonly MetalContext _ctx;
    /// <summary>
    /// The probe update compute pipeline (<c>probe_gi</c>).
    /// </summary>
    readonly MTLComputePipelineState _pipeline;
    /// <summary>
    /// The compiled probe shader library (the embedded <c>ProbeGi.metal</c>).
    /// </summary>
    readonly MTLLibrary _library;
    /// <summary>
    /// The probe update function of <see cref="_library"/>.
    /// </summary>
    readonly MTLFunction _function;
    /// <summary>
    /// The irradiance and distance atlases and the per-column base heights, as the world shaders read them (<c>uGiIrradiance</c>, <c>uGiDistance</c>,
    /// <c>uGiBase</c>).
    /// </summary>
    readonly GpuTexture _irradiance, _distance, _bases;
    /// <summary>
    /// The Metal textures of the atlases and base heights, bound to the probe update.
    /// </summary>
    readonly MTLTexture _irradianceImage, _distanceImage, _baseImage;
    /// <summary>
    /// The bilinear and nearest clamped samplers the world shaders read the atlases with.
    /// </summary>
    readonly GpuSampler _linear, _nearest;
    /// <summary>
    /// The probe constants, one shared buffer per frame ring slot.
    /// </summary>
    readonly MTLBuffer[] _constantsBuffers = new MTLBuffer[MetalContext.FrameRingDepth];
    /// <summary>
    /// The sampler the probe update reads the sky irradiance with (bilinear, clamped).
    /// </summary>
    readonly MTLSamplerState _skySampler;
    /// <summary>
    /// Each cascade's grid origin and spacing, two vectors a cascade (<c>uGiGrid</c>).
    /// </summary>
    readonly Vector4[] _grid = new Vector4[4];
    /// <summary>
    /// The source of each update's random rotation of the ray directions.
    /// </summary>
    readonly Random _random = new(1);

    /// <summary>
    /// Each cascade's per-column probe base height: the lowest terrain height at the column and four points 0.4 spacing around it.
    /// </summary>
    readonly float[] _baseHeights = new float[Columns * Columns * Cascades];
    /// <summary>
    /// Each cascade's grid origin in cells.
    /// </summary>
    readonly (int X, int Z)[] _placed = new (int, int)[Cascades];
    /// <summary>
    /// Whether each cascade's grid has been placed yet.
    /// </summary>
    readonly bool[] _placed_ = new bool[Cascades];

    /// <summary>
    /// The update count, and the update the top level was last built at (far in the past forces a rebuild).
    /// </summary>
    long _frame, _lastTlasFrame = -1000;
    /// <summary>
    /// The probes have been updated since the grid last moved (the world shaders read GI only then).
    /// </summary>
    bool _updated;
    /// <summary>
    /// The next dispatch updates every probe at once (set when the grid moves).
    /// </summary>
    bool _fullUpdate = true;
    /// <summary>
    /// The eye of the last update (a jump of over 2000 units clears the object cache).
    /// </summary>
    Vector3 _lastEye;

    /// <summary>
    /// Makes the atlases, samplers, constant buffers and the probe pipeline, and publishes the GI uniforms and textures to the frame globals.
    /// </summary>
    public MetalProbeGi(MetalContext ctx)
    {
        this._ctx = ctx;
        var irrRows = Cascades * Columns * Layers * (IrradianceTexels + 2);
        var distRows = Cascades * Columns * Layers * (DistanceTexels + 2);
        _irradiance = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16F, Columns * (IrradianceTexels + 2), irrRows,
            Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst, Name: "metal gi irradiance"));
        _distance = ctx.CreateTexture(new TextureDesc(InternalFormat.Rg16F, Columns * (DistanceTexels + 2), distRows,
            Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst, Name: "metal gi distance"));
        _bases = ctx.CreateTexture(new TextureDesc(InternalFormat.R32F, Columns, Cascades * Columns,
            Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "metal gi bases"));
        _irradianceImage = ctx.TextureAt(_irradiance);
        _distanceImage = ctx.TextureAt(_distance);
        _baseImage = ctx.TextureAt(_bases);
        _linear = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Linear, TextureMagFilter.Linear));
        _nearest = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));
        for (int i = 0; i < _constantsBuffers.Length; i++)
            _constantsBuffers[i] = ctx.Device.NewBuffer((ulong)Marshal.SizeOf<ProbeConstants>(), MTLResourceOptions.ResourceStorageModeShared);

        var skyDescriptor = new MTLSamplerDescriptor(
            new ObjectiveCClass(ObjectiveC.objc_getClass("MTLSamplerDescriptor")).AllocInit());
        skyDescriptor.MinFilter = MTLSamplerMinMagFilter.Linear;
        skyDescriptor.MagFilter = MTLSamplerMinMagFilter.Linear;
        skyDescriptor.SAddressMode = MTLSamplerAddressMode.ClampToEdge;
        skyDescriptor.TAddressMode = MTLSamplerAddressMode.ClampToEdge;
        _skySampler = ctx.Device.NewSamplerState(skyDescriptor);

        var source = ReadResource("Meitou.Metal.Gi.ProbeGi.metal");
        var options = new MTLCompileOptions { LanguageVersion = MTLLanguageVersion.Version30, FastMathEnabled = true };
        NSError error = default;
        _library = ctx.Device.NewLibrary((NSString)source, options, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"metal GI shader: {error.LocalizedDescription}");

        _function = _library.NewFunction((NSString)"probe_gi");
        _pipeline = ctx.Device.NewComputePipelineState(_function, ref error);
        if (error.NativePtr != IntPtr.Zero)
            throw new InvalidOperationException($"metal GI pipeline: {error.LocalizedDescription}");

        var g = ctx.Globals;
        g.PublishUniform("uGiParams", () => Params);
        g.PublishUniformArray("uGiGrid", _grid, () => _grid.Length);
        g.Publish("uGiIrradiance", () => new SampledTexture(_linear, _irradiance.View()));
        g.Publish("uGiDistance", () => new SampledTexture(_linear, _distance.View()));
        g.Publish("uGiBase", () => new SampledTexture(_nearest, _bases.View()));
    }

    /// <summary>
    /// False: GI is off (no dispatch and the world shaders read no GI).
    /// </summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// False: indoor surfaces keep the original ambient (<c>uGiParams.w</c> 0).
    /// </summary>
    public bool InteriorEnabled { get; set; } = true;
    /// <summary>
    /// False: the probes are neither updated nor read.
    /// </summary>
    public bool Active { get; set; } = true;
    /// <summary>
    /// False: no probe update is dispatched and the world's shading does not read the probes.
    /// </summary>
    public bool Shade { get; set; } = true;
    /// <summary>
    /// The GI strength the world shaders apply (<c>uGiParams.y</c>).
    /// </summary>
    public float Strength { get; set; } = 0.8f;
    /// <summary>
    /// Indoor surface indirect-light gain (<c>uGiParams.w</c>, at least 1 when <see cref="InteriorEnabled"/>).
    /// </summary>
    public float InteriorGain { get; set; } = 1;
    /// <summary>
    /// The instances in the last top level, terrain included.
    /// </summary>
    public int InstanceCount { get; private set; }
    /// <summary>
    /// The object bottom levels in the cache.
    /// </summary>
    public int MeshCount => _blasCache.Count;

    /// <summary>
    /// <c>uGiParams</c>: x 1 when the probes are read, y the strength, w the interior gain (0 with the interior off).
    /// </summary>
    Vector4 Params => new(Enabled && Active && Shade && _updated ? 1 : 0, Strength, 0, InteriorEnabled ? MathF.Max(1, InteriorGain) : 0);

    /// <summary>
    /// A one-line status for the debug overlay: probes, instances, meshes and the terrain grid.
    /// </summary>
    public string Describe() => $"metal probes {Probes}, {InstanceCount} instances ({MeshCount} meshes), {_terrain?.Description ?? "no terrain"}, coarse {_coarse?.Description ?? "none"}";

    /// <summary>
    /// One frame's GI: re-centres the terrain, builds at most one new object bottom level, rebuilds the top level when anything changed or 30 updates
    /// have passed, moves the grid, writes the constants and dispatches the probe update.
    /// </summary>
    public void Update(Vector3 eye, HeightSnapshot heights, WorldObjectRenderer? objects, WorldLamps? lamps, FogVolumes? fog)
    {
        _frame++;
        Log("begin");

        // A teleport (return to the character, fly-to) leaves every cached object structure behind and can otherwise
        // accumulate old mesh memory for the rest of the session.
        if (_frame > 1 && Vector3.DistanceSquared(eye, _lastEye) > 2000f * 2000f) ClearObjectCache();

        _lastEye = eye;

        // Close upload/compute encoders before the acceleration-structure encoders.
        ((MetalCommandList)_ctx.Frame.Commands).Finish();
        bool terrainChanged = UpdateTerrain(eye, heights);
        Log($"terrain {terrainChanged}");
        bool builtMesh = CollectObjects(eye, objects);
        Log($"objects {builtMesh} ({_blasCache.Count})");
        if (terrainChanged || builtMesh || _tlas is null || _frame - _lastTlasFrame >= 30) RebuildTlas(eye, objects);

        Log($"tlas {_tlas?.Count ?? 0}");
        if (_tlas is null) return;

        UpdateGrid(eye, heights);
        Log($"grid full={_fullUpdate}");
        WriteConstants();
        Log("constants");

        if (Enabled && Active && Shade)
        {
            DispatchProbes();
            Log("dispatch");
            _updated = true;
            _fullUpdate = false;
        }
    }

    /// <summary>
    /// Releases the acceleration structures, atlases, constant buffers and the shader objects.
    /// </summary>
    public void Dispose()
    {
        foreach (var e in _blasCache) e.Blas.Dispose();

        _blasCache.Clear();
        _tlas?.Dispose();
        _terrain?.Dispose();
        _coarse?.Dispose();
        _irradiance.Dispose();
        _distance.Dispose();
        _bases.Dispose();
        foreach (var b in _constantsBuffers) b.Dispose();

        _function.Dispose();
        _library.Dispose();
        _pipeline.Dispose();
    }

    /// <summary>
    /// Writes a trace line when <c>MEITOU_METAL_GI_LOG=1</c>.
    /// </summary>
    static void Log(string message)
    {
        if (Environment.GetEnvironmentVariable("MEITOU_METAL_GI_LOG") == "1") Console.WriteLine($"gi metal {message}");
    }

    /// <summary>
    /// The text of an embedded resource; throws when it is missing.
    /// </summary>
    static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing embedded resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Moves each cascade's grid when the eye is 8 or more cells from where it was placed; a move re-samples the base heights and asks for a full
    /// update.
    /// </summary>
    void UpdateGrid(Vector3 eye, HeightSnapshot heights)
    {
        bool moved = false;

        for (int c = 0; c < Cascades; c++)
        {
            var (s, _, _) = Layout[c];
            int ox = (int)MathF.Floor(eye.X / s) - Columns / 2;
            int oz = (int)MathF.Floor(eye.Z / s) - Columns / 2;

            // Keep the grid fixed for several cells: moving it every cell (near: 128 units) cleared the atlas and made
            // the world flicker between probe light and the flat ambient while walking.
            if (_placed_[c] && Math.Abs(ox - _placed[c].X) < 8 && Math.Abs(oz - _placed[c].Z) < 8) continue;

            _placed[c] = (ox, oz);
            _placed_[c] = true;
            moved = true;
        }

        if (!moved) return;

        for (int c = 0; c < Cascades; c++)
        {
            var (s, sy, _) = Layout[c];
            var (ox, oz) = _placed[c];
            _grid[c * 2] = new Vector4(ox * s, oz * s, s, sy);
            _grid[c * 2 + 1] = new Vector4(0, 0, 2 * s, 0);

            for (int k = 0; k < Columns; k++)
            for (int i = 0; i < Columns; i++)
            {
                float x = (ox + i) * s, z = (oz + k) * s;
                float h = heights.HeightAt(x, z);
                h = MathF.Min(h, MathF.Min(heights.HeightAt(x + s * 0.4f, z), heights.HeightAt(x - s * 0.4f, z)));
                h = MathF.Min(h, MathF.Min(heights.HeightAt(x, z + s * 0.4f), heights.HeightAt(x, z - s * 0.4f)));
                _baseHeights[(c * Columns + k) * Columns + i] = h;
            }
        }

        // The full update that follows writes every texel without the old values (hysteresis 0); only the base heights are uploaded.
        using (var batch = _ctx.Uploads.Begin())
        {
            batch.Write(_bases, 0, 0, new PixelRect(0, 0, Columns, (uint)(Cascades * Columns)), MemoryMarshal.AsBytes(_baseHeights.AsSpan()));
        }

        _updated = false;
        _fullUpdate = true;
    }

    /// <summary>
    /// Writes this frame's probe constants (the rays' rotation, grid, sun and sky light, albedo, atmosphere maps, update phase and hysteresis, ray
    /// lengths) into the slot's buffer.
    /// </summary>
    void WriteConstants()
    {
        var g = _ctx.Globals;
        bool ground = g.Texture("uWeatherGround")?.Invoke() is { IsNull: false };
        int phases = _fullUpdate ? 1 : Phases;
        var c = new ProbeConstants
        {
            Rotation = RandomRotation(),
            Grid0 = _grid[0], Grid1 = _grid[1], Grid2 = _grid[2], Grid3 = _grid[3],
            SunLight = new Vector4(Read<Vector3>(g, "uAtmoSunLight"), 0),
            LightDir = Read<Vector4>(g, "uAtmoLight"),
            Albedo = new Vector4(ObjectAlbedo, ground ? 1 : 0),
            Maps = Read<Vector4>(g, "uAtmoMaps"),
            Timing = new Vector4(_frame % phases, phases, _fullUpdate ? 0 : UpdateHysteresis(phases), 0),
            RayLength = new Vector4(Layout[0].RayLength, Layout[1].RayLength, 0, 0),
        };
        Marshal.StructureToPtr(c, _constantsBuffers[_ctx.Frame.Slot].Contents, false);
    }

    /// <summary>
    /// The hysteresis of one update of <see cref="Rays"/> rays over <paramref name="phases"/> frames, as GiProbes' (docs/render-gi.md "Probes"): an
    /// exponential average's variance is (1 - h) / (1 + h) of one sample's, scaled by the rays against 64.
    /// </summary>
    static float UpdateHysteresis(int phases)
    {
        float h = MathF.Pow(Hysteresis, phases);
        float k = (1 - h) / (1 + h) * Rays / 64;
        return (1 - k) / (1 + k);
    }

    /// <summary>
    /// A uniformly random rotation (a random axis and angle), so successive updates sample new directions.
    /// </summary>
    Matrix4x4 RandomRotation()
    {
        var axis = Vector3.Normalize(new Vector3(_random.NextSingle() * 2 - 1, _random.NextSingle() * 2 - 1, _random.NextSingle() * 2 - 1)
            + new Vector3(1e-4f));
        return Matrix4x4.CreateFromAxisAngle(axis, _random.NextSingle() * MathF.Tau);
    }

    /// <summary>
    /// Dispatches the probe update as a native segment: one threadgroup of <see cref="Rays"/> threads per probe updated this frame.
    /// </summary>
    void DispatchProbes()
    {
        var cmd = (MetalCommandList)_ctx.BeginNative("gi metal");
        var encoder = cmd.ComputeEncoder();
        encoder.SetComputePipelineState(_pipeline);
        encoder.SetBuffer(_constantsBuffers[_ctx.Frame.Slot], 0, 0);
        encoder.SetBuffer(_tlas!.FirstRecords, 0, 1);
        encoder.SetAccelerationStructure(_tlas.Acceleration, 2);

        // The top level reaches its bottom levels, and the records their vertices and indices, by address only: Metal does not make them resident
        // unless the encoder is told, and a trace or fetch into one that is not faults the GPU.
        cmd.UseResources(_tlas.Resident, _tlas.Resident.Length);
        encoder.SetBuffer(_tlas.Records, 0, 3);
        encoder.SetTexture(_irradianceImage, 0);
        encoder.SetTexture(_irradianceImage, 1);
        encoder.SetTexture(_distanceImage, 2);
        encoder.SetTexture(_distanceImage, 3);
        encoder.SetTexture(_baseImage, 4);
        encoder.SetTexture(GlobalTexture("uAtmoIrradiance", cube: true), 5);
        encoder.SetTexture(GlobalTexture("uWeatherGround", cube: false), 6);
        encoder.SetTexture(GlobalTexture("uAtmoAmbientMap", cube: false), 7);
        encoder.SetSamplerState(_skySampler, 0);
        encoder.SetSamplerState(_skySampler, 1);
        int phases = _fullUpdate ? 1 : Phases;

        encoder.DispatchThreadgroups(new MTLSize { width = (ulong)(Probes / phases), height = 1, depth = 1 },
            new MTLSize { width = Rays, height = 1, depth = 1 });
        cmd.EndCompute();
        _ctx.EndNative(cmd);
    }

    /// <summary>
    /// The Metal texture of a published global texture, a stand-in when none is published.
    /// </summary>
    MTLTexture GlobalTexture(string name, bool cube)
    {
        var texture = _ctx.Globals.Texture(name)?.Invoke() ?? default;
        if (texture.IsNull) texture = _ctx.Dummy(FrameGlobals.Sampler2D(name, cube));

        return _ctx.Sampled(texture).Item1;
    }

    /// <summary>
    /// A published uniform's value as <typeparamref name="T"/>; default when it is missing or does not fit.
    /// </summary>
    static T Read<T>(FrameGlobals g, string name) where T : unmanaged
    {
        var u = g.UniformValue(name);
        if (u is null) return default;

        Span<byte> bytes = stackalloc byte[System.Runtime.CompilerServices.Unsafe.SizeOf<T>()];
        return u.TryRead(bytes) ? MemoryMarshal.Read<T>(bytes) : default;
    }

    /// <summary>
    /// The probe update's constants, laid out as the shader's.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ProbeConstants
    {
        /// <summary>
        /// The ray rotation (identity).
        /// </summary>
        public Matrix4x4 Rotation;
        /// <summary>
        /// The grid vectors of <see cref="_grid"/>.
        /// </summary>
        public Vector4 Grid0, Grid1, Grid2, Grid3;
        /// <summary>
        /// The sun light, the lighting direction (w the environment light's factor), the objects' albedo (w 1 with the ground colour map), the
        /// atmosphere maps, the update (x the phase, y the phase count, z the hysteresis) and the cascades' ray lengths.
        /// </summary>
        public Vector4 SunLight, LightDir, Albedo, Maps, Timing, RayLength;
    }

    #endregion

    #region Acceleration structures

    /// <summary>
    /// The most object instances in the top level (the terrain comes on top).
    /// </summary>
    const int MaxObjects = 64;
    /// <summary>
    /// The most object bottom levels kept in the cache; the least recently used goes first.
    /// </summary>
    const int MaxCachedMeshes = 64;
    /// <summary>
    /// How far from the eye objects are traced (world units).
    /// </summary>
    const float ObjectRange = 2500;
    /// <summary>
    /// The terrain bottom levels' cells on a side.
    /// </summary>
    const int TerrainCells = 256;
    /// <summary>
    /// The fine terrain's cell size and the step it re-centres by; the coarse terrain's (40960 wide, GiScene's coarse grid), which the far cascade's
    /// rays reach (world units).
    /// </summary>
    const float FineSpacing = 32, FineStep = 512, CoarseSpacing = 160, CoarseStep = 2560;
    /// <summary>
    /// The instance options: the terrain's triangles are counter-clockwise from above and culled from below (opaque); an object's are not culled, so
    /// a ray inside one finds its back (<c>MTLAccelerationStructureInstanceOptions</c>: 1 no culling, 2 counter-clockwise front faces, 4 opaque).
    /// </summary>
    const uint TerrainOptions = 2 | 4, ObjectOptions = 1 | 2 | 4;
    /// <summary>
    /// <see cref="GeometryRecord.Kind"/>: the terrain (the ground colour map's albedo) or an object.
    /// </summary>
    const uint TerrainKind = 0, ObjectKind = 1;

    /// <summary>
    /// The object bottom levels built so far (at most <see cref="MaxCachedMeshes"/>).
    /// </summary>
    readonly List<BlasEntry> _blasCache = [];
    /// <summary>
    /// The top level's instance descriptors, 64 bytes each (<c>MTLAccelerationStructureInstanceDescriptor</c>), the terrains first.
    /// </summary>
    readonly byte[] _instanceBytes = new byte[64 * (MaxObjects + 2)];

    /// <summary>
    /// The fine terrain bottom level around the eye; null before the first update.
    /// </summary>
    TerrainBlas? _terrain;
    /// <summary>
    /// The coarse terrain bottom level around the eye; null before the first update.
    /// </summary>
    TerrainBlas? _coarse;
    /// <summary>
    /// The top level over the terrains and the nearest objects; null before the first build.
    /// </summary>
    Tlas? _tlas;

    /// <summary>
    /// Drops every object bottom level and the top level (released after the frames in flight) and forces a rebuild.
    /// </summary>
    void ClearObjectCache()
    {
        foreach (var e in _blasCache) _ctx.DeferDelete(e.Blas.Dispose);

        _blasCache.Clear();

        if (_tlas is not null)
        {
            _ctx.DeferDelete(_tlas.Dispose);
            _tlas = null;
        }

        _lastTlasFrame = -1000;
    }

    /// <summary>
    /// Re-centres the fine and the coarse terrain bottom levels on the eye's cell of their step where it moved; true when one was rebuilt.
    /// </summary>
    bool UpdateTerrain(Vector3 eye, HeightSnapshot heights)
    {
        bool fine = Recentre(ref _terrain, eye, heights, FineSpacing, FineStep, "metal gi terrain");
        bool coarse = Recentre(ref _coarse, eye, heights, CoarseSpacing, CoarseStep, "metal gi coarse terrain");
        return fine || coarse;
    }

    /// <summary>
    /// Rebuilds <paramref name="terrain"/> around the eye's <paramref name="step"/> cell when it is not there yet (the old one released after the
    /// frames in flight); true when it was rebuilt.
    /// </summary>
    bool Recentre(ref TerrainBlas? terrain, Vector3 eye, HeightSnapshot heights, float spacing, float step, string name)
    {
        var centre = new Vector3(MathF.Round(eye.X / step) * step, 0, MathF.Round(eye.Z / step) * step);
        if (terrain is not null && Vector3.DistanceSquared(terrain.Centre, centre) < 1) return false;

        var old = terrain;
        terrain = BuildTerrain(centre, heights, spacing, name);
        if (old is not null) _ctx.DeferDelete(old.Dispose);

        return true;
    }

    /// <summary>
    /// A <see cref="TerrainCells"/>² triangle grid of the heights around <paramref name="centre"/>, <paramref name="spacing"/> apart, built into a
    /// bottom level.
    /// </summary>
    TerrainBlas BuildTerrain(Vector3 centre, HeightSnapshot heights, float spacing, string name)
    {
        var vertices = TerrainVertices(centre, heights, spacing);
        var indices = TerrainIndices();
        var vertexBuffer = _ctx.Device.NewBuffer((ulong)(vertices.Length * 4), MTLResourceOptions.ResourceStorageModeShared);
        var indexBuffer = _ctx.Device.NewBuffer((ulong)(indices.Length * 4), MTLResourceOptions.ResourceStorageModeShared);
        vertexBuffer.Label = (NSString)(name + " vertices");
        indexBuffer.Label = (NSString)(name + " indices");
        Marshal.Copy(vertices, 0, vertexBuffer.Contents, vertices.Length);
        Marshal.Copy(indices, 0, indexBuffer.Contents, indices.Length);

        var geometry = new MTLAccelerationStructureTriangleGeometryDescriptor
        {
            VertexBuffer = vertexBuffer,
            VertexStride = 12,
            VertexFormat = MTLAttributeFormat.Float3,
            IndexBuffer = indexBuffer,
            IndexType = MTLIndexType.UInt32,
            TriangleCount = (ulong)(indices.Length / 3),
            Opaque = true,
        };
        var descriptor = new MTLPrimitiveAccelerationStructureDescriptor
        {
            GeometryDescriptors = NSArray.Array(new NSObject(geometry.NativePtr)),
            Usage = MTLAccelerationStructureUsage.PreferFastIntersection,
        };
        Build(name, descriptor, out var acceleration, out var scratch);
        _ctx.DeferDelete(scratch.Dispose);

        var record = new GeometryRecord(vertexBuffer.GpuAddress, indexBuffer.GpuAddress, 12, TerrainKind);
        return new TerrainBlas(acceleration, vertexBuffer, indexBuffer, record, centre, spacing);
    }

    /// <summary>
    /// The terrain grid's positions: (<see cref="TerrainCells"/> + 1)² samples of the heights, <paramref name="spacing"/> apart round
    /// <paramref name="centre"/>.
    /// </summary>
    static float[] TerrainVertices(Vector3 centre, HeightSnapshot heights, float spacing)
    {
        int side = TerrainCells + 1;
        var vertices = new float[side * side * 3];

        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int i = (z * side + x) * 3;
            float wx = centre.X + (x - TerrainCells * 0.5f) * spacing;
            float wz = centre.Z + (z - TerrainCells * 0.5f) * spacing;
            vertices[i] = wx;
            vertices[i + 1] = heights.HeightAt(wx, wz);
            vertices[i + 2] = wz;
        }

        return vertices;
    }

    /// <summary>
    /// The terrain grid's triangles, two a cell, counter-clockwise seen from above (their cross product points up).
    /// </summary>
    static int[] TerrainIndices()
    {
        int side = TerrainCells + 1;
        var indices = new int[TerrainCells * TerrainCells * 6];
        int n = 0;

        for (int z = 0; z < TerrainCells; z++)
        for (int x = 0; x < TerrainCells; x++)
        {
            int i0 = z * side + x, i1 = i0 + 1, i2 = i0 + side, i3 = i2 + 1;
            indices[n++] = i0;
            indices[n++] = i2;
            indices[n++] = i1;
            indices[n++] = i1;
            indices[n++] = i2;
            indices[n++] = i3;
        }

        return indices;
    }

    /// <summary>
    /// Builds a bottom-level descriptor into a new acceleration structure with a private scratch buffer, recording the build on the frame's command
    /// buffer (the caller releases the scratch).
    /// </summary>
    void Build(string name, MTLPrimitiveAccelerationStructureDescriptor descriptor, out MTLAccelerationStructure acceleration, out MTLBuffer scratch)
    {
        var sizes = _ctx.Device.AccelerationStructureSizes(descriptor);
        acceleration = _ctx.Device.NewAccelerationStructure(sizes.accelerationStructureSize);
        acceleration.Label = (NSString)name;
        scratch = _ctx.Device.NewBuffer(Math.Max(sizes.buildScratchBufferSize, 1), MTLResourceOptions.ResourceStorageModePrivate);
        var encoder = _ctx.Frame.Command.AccelerationStructureCommandEncoder();
        encoder.BuildAccelerationStructure(acceleration, descriptor, scratch, 0);
        encoder.EndEncoding();
    }

    /// <summary>
    /// Builds a top-level descriptor into a new acceleration structure with a private scratch buffer, recording the build on the frame's command
    /// buffer.
    /// </summary>
    void Build(string name, MTLInstanceAccelerationStructureDescriptor descriptor, out MTLAccelerationStructure acceleration, out MTLBuffer scratch)
    {
        var sizes = _ctx.Device.AccelerationStructureSizes(descriptor);
        acceleration = _ctx.Device.NewAccelerationStructure(sizes.accelerationStructureSize);
        acceleration.Label = (NSString)name;
        scratch = _ctx.Device.NewBuffer(Math.Max(sizes.buildScratchBufferSize, 1), MTLResourceOptions.ResourceStorageModePrivate);
        var encoder = _ctx.Frame.Command.AccelerationStructureCommandEncoder();
        encoder.BuildAccelerationStructure(acceleration, descriptor, scratch, 0);
        encoder.EndEncoding();
    }

    /// <summary>
    /// Builds the bottom level of at most one not yet cached mesh among the nearest <see cref="MaxObjects"/> objects (evicting the least recently
    /// used when the cache is full); true when one was built.
    /// </summary>
    bool CollectObjects(Vector3 eye, WorldObjectRenderer? objects)
    {
        if (objects is null) return false;

        var candidates = NearestObjects(eye, objects);

        foreach (var (inst, _) in candidates)
        {
            var mesh = inst.Gpu!;
            if (FindBlas(mesh) is not null) continue;

            EvictIfFull();
            if (BuildObjectBlas(mesh) is null) continue;

            _lastTlasFrame = long.MinValue / 2;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The object instances within <see cref="ObjectRange"/> of the eye that have a mesh on the GPU, nearest first, at most
    /// <see cref="MaxObjects"/>.
    /// </summary>
    static List<(ObjectStreamer.Instance Instance, float Distance)> NearestObjects(Vector3 eye, WorldObjectRenderer objects)
    {
        var candidates = new List<(ObjectStreamer.Instance Instance, float Distance)>();
        foreach (var inst in objects.RayInstances(eye, ObjectRange))
            if (inst.Gpu is not null) candidates.Add((inst, Vector3.Distance(inst.Centre, eye)));

        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (candidates.Count > MaxObjects) candidates.RemoveRange(MaxObjects, candidates.Count - MaxObjects);

        return candidates;
    }

    /// <summary>
    /// Drops the least recently used bottom level when the cache is full (released after the frames in flight) and forces a top level rebuild.
    /// </summary>
    void EvictIfFull()
    {
        if (_blasCache.Count < MaxCachedMeshes) return;

        var oldest = _blasCache.MinBy(e => e.LastUsed);
        if (oldest is null) return;

        _blasCache.Remove(oldest);
        _ctx.DeferDelete(oldest.Blas.Dispose);
        _lastTlasFrame = long.MinValue / 2;
    }

    /// <summary>
    /// The cached bottom level of a mesh at its current level, marked used; null when there is none or the cached one is out of date (which is
    /// dropped).
    /// </summary>
    BlasEntry? FindBlas(GpuObjectMesh mesh)
    {
        int level = Math.Clamp(mesh.MinLevel, 0, Math.Max(mesh.LevelCount - 1, 0));

        for (int i = 0; i < _blasCache.Count; i++)
        {
            var e = _blasCache[i];
            if (!ReferenceEquals(e.Mesh, mesh)) continue;

            if (e.Level == level && Same(e.Sources, mesh))
            {
                e.LastUsed = _frame;
                return e;
            }

            _blasCache.RemoveAt(i);
            _ctx.DeferDelete(e.Blas.Dispose);
            return null;
        }

        return null;
    }

    /// <summary>
    /// Builds a mesh's bottom level from its parts at its current level (or its manual mesh), with a geometry record per part, caches it and returns
    /// it; null when no part has a triangle.
    /// </summary>
    BlasEntry? BuildObjectBlas(GpuObjectMesh mesh)
    {
        int level = Math.Clamp(mesh.MinLevel, 0, Math.Max(mesh.LevelCount - 1, 0));
        var source = mesh.Manual.Length > level && mesh.Manual[level] is { } manual ? manual : mesh;
        int sourceLevel = ReferenceEquals(source, mesh) ? level : 0;
        var geometries = new List<MTLAccelerationStructureTriangleGeometryDescriptor>();
        var records = new List<GeometryRecord>();
        var buffers = new List<nint>();
        var sources = new List<object> { source };

        foreach (var part in source.Parts)
        {
            if (part.Count.Length <= sourceLevel) continue;

            int count = part.Count[sourceLevel];
            if (count < 3) continue;

            var vertices = _ctx.BufferAt(part.Vertices.Handle);
            var indices = _ctx.BufferAt(part.Indices.Handle);
            ulong indexOffset = (ulong)part.Offset[sourceLevel] * 4;
            geometries.Add(new MTLAccelerationStructureTriangleGeometryDescriptor
            {
                VertexBuffer = vertices,
                VertexStride = (ulong)part.Stride,
                VertexFormat = MTLAttributeFormat.Float3,
                IndexBuffer = indices,
                IndexBufferOffset = indexOffset,
                IndexType = MTLIndexType.UInt32,
                TriangleCount = (ulong)(count / 3),
                Opaque = true,
            });
            records.Add(new GeometryRecord(vertices.GpuAddress, indices.GpuAddress + indexOffset, (uint)part.Stride, ObjectKind));
            buffers.Add(vertices.NativePtr);
            buffers.Add(indices.NativePtr);
            sources.Add(part.Vertices);
        }

        if (geometries.Count == 0) return null;

        var descriptor = new MTLPrimitiveAccelerationStructureDescriptor
        {
            GeometryDescriptors = ArrayOf(geometries.Select(g => g.NativePtr)),
            Usage = MTLAccelerationStructureUsage.PreferFastIntersection,
        };
        Build("metal gi object", descriptor, out var accel, out var scratch);
        _ctx.DeferDelete(scratch.Dispose);
        var entry = new BlasEntry
        {
            Mesh = mesh, Level = level, Sources = [.. sources], Blas = accel, Records = [.. records], Buffers = [.. buffers], LastUsed = _frame,
        };
        _blasCache.Add(entry);
        return entry;
    }

    /// <summary>
    /// Whether a cache entry's sources are still the mesh and its parts' vertex buffers.
    /// </summary>
    static bool Same(object[] sources, GpuObjectMesh mesh)
    {
        if (sources.Length != mesh.Parts.Count + 1) return false;
        if (!ReferenceEquals(sources[0], mesh)) return false;
        for (int i = 0; i < mesh.Parts.Count; i++)
            if (!ReferenceEquals(sources[i + 1], mesh.Parts[i].Vertices)) return false;

        return true;
    }

    /// <summary>
    /// Rebuilds the top level: the two terrains, then the nearest objects whose bottom levels are built.
    /// </summary>
    void RebuildTlas(Vector3 eye, WorldObjectRenderer? objects)
    {
        var scene = new SceneLists();
        AddTerrain(scene, _terrain!);
        AddTerrain(scene, _coarse!);
        if (objects is not null) AddObjectInstances(eye, objects, scene);

        BuildTlas(scene);
    }

    /// <summary>
    /// Adds a terrain's instance (untransformed) with its bottom level, record and buffers.
    /// </summary>
    void AddTerrain(SceneLists scene, TerrainBlas terrain)
    {
        WriteInstance(scene.FirstRecords.Count, Matrix4x4.Identity, TerrainOptions, scene.Bottoms.Count);
        scene.FirstRecords.Add((uint)scene.Records.Count);
        scene.Records.Add(terrain.Record);
        scene.Bottoms.Add(terrain.Blas);
        scene.Resident.AddRange([terrain.Blas.NativePtr, terrain.Vertices.NativePtr, terrain.Indices.NativePtr]);
    }

    /// <summary>
    /// Adds the nearest object instances whose bottom levels are built: each instance after those so far, each bottom level with its records and
    /// buffers once.
    /// </summary>
    void AddObjectInstances(Vector3 eye, WorldObjectRenderer objects, SceneLists scene)
    {
        var added = new Dictionary<nint, (int Bottom, uint FirstRecord)>();

        foreach (var (inst, _) in NearestObjects(eye, objects))
        {
            var entry = FindBlas(inst.Gpu!);
            if (entry is null) continue;

            if (!added.TryGetValue(entry.Blas.NativePtr, out var at))
            {
                at = (scene.Bottoms.Count, (uint)scene.Records.Count);
                added[entry.Blas.NativePtr] = at;
                scene.Bottoms.Add(entry.Blas);
                scene.Records.AddRange(entry.Records);
                scene.Resident.Add(entry.Blas.NativePtr);
                scene.Resident.AddRange(entry.Buffers);
            }

            WriteInstance(scene.FirstRecords.Count, inst.Transform, ObjectOptions, at.Bottom);
            scene.FirstRecords.Add(at.FirstRecord);
        }
    }

    /// <summary>
    /// The top level over the instances written, with their first records and the geometry records; the old one goes after the frames in flight.
    /// </summary>
    void BuildTlas(SceneLists scene)
    {
        int count = scene.FirstRecords.Count;
        var instBuffer = _ctx.Device.NewBuffer((ulong)(64 * count), MTLResourceOptions.ResourceStorageModeShared);
        Marshal.Copy(_instanceBytes, 0, instBuffer.Contents, count * 64);
        var firstBuffer = SharedBuffer<uint>(CollectionsMarshal.AsSpan(scene.FirstRecords));
        var recordBuffer = SharedBuffer<GeometryRecord>(CollectionsMarshal.AsSpan(scene.Records));

        var descriptor = new MTLInstanceAccelerationStructureDescriptor
        {
            InstanceCount = (ulong)count,
            InstanceDescriptorBuffer = instBuffer,
            InstanceDescriptorStride = 64,
            InstancedAccelerationStructures = ArrayOf(scene.Bottoms.Select(a => a.NativePtr)),
            InstanceDescriptorType = MTLAccelerationStructureInstanceDescriptorType.Default,
            InstanceTransformationMatrixLayout = MTLMatrixLayout.ColumnMajor,
            Usage = MTLAccelerationStructureUsage.PreferFastIntersection,
        };
        Build("metal gi top", descriptor, out var acceleration, out var scratch);
        _ctx.DeferDelete(scratch.Dispose);

        var old = _tlas;
        _tlas = new Tlas(acceleration, instBuffer, firstBuffer, recordBuffer, count, [.. scene.Resident.Distinct()]);
        InstanceCount = count;
        _lastTlasFrame = _frame;
        if (old is not null) _ctx.DeferDelete(old.Dispose);
    }

    /// <summary>
    /// A new shared buffer holding <paramref name="items"/> (at least 16 bytes).
    /// </summary>
    MTLBuffer SharedBuffer<T>(ReadOnlySpan<T> items) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(items);
        var buffer = _ctx.Device.NewBuffer((ulong)Math.Max(bytes.Length, 16), MTLResourceOptions.ResourceStorageModeShared);
        bytes.CopyTo(new Span<byte>((void*)buffer.Contents, bytes.Length));
        return buffer;
    }

    /// <summary>
    /// Writes the instance descriptor at <paramref name="index"/>: the transform, options, mask 0xFF and the bottom level's index.
    /// </summary>
    void WriteInstance(int index, Matrix4x4 m, uint options, int blas)
    {
        var span = _instanceBytes.AsSpan(index * 64, 64);

        // MTLPackedFloat4x3: four columns of three floats (column-major); a System.Numerics row-vector matrix's rows are those columns.
        ReadOnlySpan<float> transform = [m.M11, m.M12, m.M13, m.M21, m.M22, m.M23, m.M31, m.M32, m.M33, m.M41, m.M42, m.M43];
        MemoryMarshal.AsBytes(transform).CopyTo(span);
        ReadOnlySpan<uint> fields = [options, 0xFF, 0, (uint)blas];
        MemoryMarshal.AsBytes(fields).CopyTo(span[48..]);
    }

    /// <summary>
    /// An NSArray of Objective-C objects.
    /// </summary>
    static NSArray ArrayOf(IEnumerable<IntPtr> objects)
    {
        var mutable = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSMutableArray"), "array");
        foreach (var p in objects) ObjectiveC.objc_msgSend(mutable, "addObject:", p);

        return new NSArray(mutable);
    }

    /// <summary>
    /// A bottom level's geometry as the probe shader reads it (<c>GeometryRecord</c>): its positions' and indices' GPU addresses (the indices at the
    /// part's first), the vertex stride and <see cref="TerrainKind"/> or <see cref="ObjectKind"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct GeometryRecord(ulong Vertices, ulong Indices, uint Stride, uint Kind);

    /// <summary>
    /// What a top level is built from: per instance its first geometry record, the records, the bottom levels and every resource the trace reaches
    /// by address.
    /// </summary>
    sealed class SceneLists
    {
        /// <summary>
        /// Per instance, the index of its bottom level's first record in <see cref="Records"/>.
        /// </summary>
        public readonly List<uint> FirstRecords = [];
        /// <summary>
        /// The bottom levels' geometry records, each bottom level's together.
        /// </summary>
        public readonly List<GeometryRecord> Records = [];
        /// <summary>
        /// The bottom levels the instances refer to by index.
        /// </summary>
        public readonly List<MTLAccelerationStructure> Bottoms = [];
        /// <summary>
        /// The bottom levels and the vertex and index buffers of the records, as native pointers.
        /// </summary>
        public readonly List<nint> Resident = [];
    }

    /// <summary>
    /// A cached object bottom level: the mesh, its level, the sources it was built from and its parts' records.
    /// </summary>
    sealed class BlasEntry
    {
        /// <summary>
        /// The mesh the bottom level is of.
        /// </summary>
        public required GpuObjectMesh Mesh;
        /// <summary>
        /// The mesh level it was built at.
        /// </summary>
        public required int Level;
        /// <summary>
        /// The mesh (or its manual mesh) and its parts' vertex buffers when built, checked by <see cref="Same"/>.
        /// </summary>
        public required object[] Sources;
        /// <summary>
        /// The bottom level.
        /// </summary>
        public required MTLAccelerationStructure Blas;
        /// <summary>
        /// One record per geometry, in the bottom level's geometry order.
        /// </summary>
        public required GeometryRecord[] Records;
        /// <summary>
        /// The records' vertex and index buffers, as native pointers (made resident with the top level).
        /// </summary>
        public required nint[] Buffers;
        /// <summary>
        /// The update it was last used at (<see cref="MaxCachedMeshes"/> evicts the oldest).
        /// </summary>
        public long LastUsed;
    }

    /// <summary>
    /// A terrain bottom level with its vertex and index buffers and its record.
    /// </summary>
    sealed class TerrainBlas(MTLAccelerationStructure blas, MTLBuffer vertices, MTLBuffer indices, GeometryRecord record, Vector3 centre,
        float spacing) : IDisposable
    {
        /// <summary>
        /// The bottom level.
        /// </summary>
        public MTLAccelerationStructure Blas => blas;
        /// <summary>
        /// The grid's positions.
        /// </summary>
        public MTLBuffer Vertices => vertices;
        /// <summary>
        /// The grid's triangles.
        /// </summary>
        public MTLBuffer Indices => indices;
        /// <summary>
        /// The grid's geometry record.
        /// </summary>
        public GeometryRecord Record => record;
        /// <summary>
        /// The centre of the grid.
        /// </summary>
        public Vector3 Centre => centre;
        /// <summary>
        /// The grid's size, spacing and centre, for <see cref="Describe"/>.
        /// </summary>
        public string Description => $"{TerrainCells}² grid {spacing:0} apart at {centre.X:0},{centre.Z:0}";

        /// <summary>
        /// Releases the bottom level and its buffers.
        /// </summary>
        public void Dispose()
        {
            blas.Dispose();
            vertices.Dispose();
            indices.Dispose();
        }
    }

    /// <summary>
    /// A top level with its instance descriptors, the instances' first records, the geometry records and what the trace needs resident.
    /// </summary>
    sealed class Tlas(MTLAccelerationStructure acceleration, MTLBuffer instances, MTLBuffer firstRecords, MTLBuffer records, int count,
        nint[] resident) : IDisposable
    {
        /// <summary>
        /// The acceleration structure.
        /// </summary>
        public MTLAccelerationStructure Acceleration => acceleration;
        /// <summary>
        /// Per instance, its first geometry record (the probe update's <c>firstRecord</c>).
        /// </summary>
        public MTLBuffer FirstRecords => firstRecords;
        /// <summary>
        /// The geometry records (the probe update's <c>records</c>).
        /// </summary>
        public MTLBuffer Records => records;
        /// <summary>
        /// The instances in it.
        /// </summary>
        public int Count => count;
        /// <summary>
        /// The native pointers of the bottom levels and the records' buffers, which a trace needs resident
        /// (<see cref="MetalCommandList.UseResources"/>).
        /// </summary>
        public nint[] Resident => resident;

        /// <summary>
        /// Releases the acceleration structure and its buffers.
        /// </summary>
        public void Dispose()
        {
            acceleration.Dispose();
            instances.Dispose();
            firstRecords.Dispose();
            records.Dispose();
        }
    }

    #endregion
}
