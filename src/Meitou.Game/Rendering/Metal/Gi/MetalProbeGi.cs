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
    const int Columns = 32, Layers = 8, IrradianceTexels = 8, DistanceTexels = 16, Cascades = 2;
    const int Rays = 32, Phases = 4;
    const int Probes = Columns * Columns * Layers * Cascades;
    const int MaxObjects = 64;
    const int MaxCachedMeshes = 64;
    const float ObjectRange = 2500;
    const int TerrainCells = 256;
    const float TerrainSpacing = 32;
    static readonly (float Spacing, float SpacingY)[] Layout = [(128, 96), (512, 384)];

    readonly MetalContext _ctx;
    readonly MTLComputePipelineState _pipeline;
    readonly MTLLibrary _library;
    readonly MTLFunction _function;
    readonly GpuTexture _irradiance, _distance, _bases;
    readonly MTLTexture _irradianceImage, _distanceImage, _baseImage;
    readonly GpuSampler _linear, _nearest;
    readonly MTLBuffer[] _constantsBuffers = new MTLBuffer[MetalContext.FrameRingDepth];
    readonly MTLSamplerState _skySampler;
    readonly Vector4[] _grid = new Vector4[4];
    readonly List<BlasEntry> _blasCache = [];
    readonly byte[] _instanceBytes = new byte[64 * (MaxObjects + 1)];
    readonly float[] _instanceInfo = new float[4 * (MaxObjects + 1)];
    readonly float[] _baseHeights = new float[Columns * Columns * Cascades];
    readonly (int X, int Z)[] _placed = new (int, int)[Cascades];
    readonly bool[] _placed_ = new bool[Cascades];
    TerrainBlas? _terrain;
    Tlas? _tlas;
    long _frame, _lastTlasFrame = -1000;
    bool _updated;
    bool _fullUpdate = true;
    Vector3 _lastEye;

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
        _linear = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
        _nearest = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
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
        if (error.NativePtr != IntPtr.Zero) throw new InvalidOperationException($"metal GI shader: {error.LocalizedDescription}");
        _function = _library.NewFunction((NSString)"probe_gi");
        _pipeline = ctx.Device.NewComputePipelineState(_function, ref error);
        if (error.NativePtr != IntPtr.Zero) throw new InvalidOperationException($"metal GI pipeline: {error.LocalizedDescription}");

        var g = ctx.Globals;
        g.PublishUniform("uGiParams", () => Params);
        g.PublishUniformArray("uGiGrid", _grid, () => _grid.Length);
        g.Publish("uGiIrradiance", () => new SampledTexture(_linear, _irradiance.View()));
        g.Publish("uGiDistance", () => new SampledTexture(_linear, _distance.View()));
        g.Publish("uGiBase", () => new SampledTexture(_nearest, _bases.View()));
    }

    public bool Enabled { get; set; } = true;
    public bool InteriorEnabled { get; set; } = true;
    public bool Active { get; set; } = true;
    public bool Shade { get; set; } = true;
    public float Strength { get; set; } = 0.8f;
    public float InteriorGain { get; set; } = 1;
    public int InstanceCount { get; private set; }
    public int MeshCount => _blasCache.Count;

    Vector4 Params => new(Enabled && Active && Shade && _updated ? 1 : 0, Strength, 0, InteriorEnabled ? MathF.Max(1, InteriorGain) : 0);

    public string Describe() => $"metal probes {Probes}, {InstanceCount} instances ({MeshCount} meshes), {_terrain?.Description ?? "no terrain"}";

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
        if (terrainChanged || builtMesh || _tlas is null || _frame - _lastTlasFrame >= 30)
            RebuildTlas(eye, objects);
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

    void ClearObjectCache()
    {
        foreach (var e in _blasCache) _ctx.DeferDelete(e.Blas.Dispose);
        _blasCache.Clear();
        if (_tlas is not null) { _ctx.DeferDelete(_tlas.Dispose); _tlas = null; }
        _lastTlasFrame = -1000;
    }

    static void Log(string message)
    {
        if (Environment.GetEnvironmentVariable("MEITOU_METAL_GI_LOG") == "1")
            Console.WriteLine($"gi metal {message}");
    }

    static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing embedded resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    bool UpdateTerrain(Vector3 eye, HeightSnapshot heights)
    {
        var centre = new Vector3(MathF.Round(eye.X / 512) * 512, 0, MathF.Round(eye.Z / 512) * 512);
        if (_terrain is not null && Vector3.DistanceSquared(_terrain.Centre, centre) < 1) return false;
        var old = _terrain;
        _terrain = BuildTerrain(centre, heights);
        if (old is not null) _ctx.DeferDelete(old.Dispose);
        return true;
    }

    TerrainBlas BuildTerrain(Vector3 centre, HeightSnapshot heights)
    {
        int side = TerrainCells + 1;
        var vertices = new float[side * side * 3];
        for (int z = 0; z < side; z++)
        for (int x = 0; x < side; x++)
        {
            int i = (z * side + x) * 3;
            float wx = centre.X + (x - TerrainCells * 0.5f) * TerrainSpacing;
            float wz = centre.Z + (z - TerrainCells * 0.5f) * TerrainSpacing;
            vertices[i] = wx;
            vertices[i + 1] = heights.HeightAt(wx, wz);
            vertices[i + 2] = wz;
        }
        var indices = new int[TerrainCells * TerrainCells * 6];
        int n = 0;
        for (int z = 0; z < TerrainCells; z++)
        for (int x = 0; x < TerrainCells; x++)
        {
            int i0 = z * side + x, i1 = i0 + 1, i2 = i0 + side, i3 = i2 + 1;
            indices[n++] = i0; indices[n++] = i2; indices[n++] = i1;
            indices[n++] = i1; indices[n++] = i2; indices[n++] = i3;
        }
        var vertexBuffer = _ctx.Device.NewBuffer((ulong)(vertices.Length * 4), MTLResourceOptions.ResourceStorageModeShared);
        var indexBuffer = _ctx.Device.NewBuffer((ulong)(indices.Length * 4), MTLResourceOptions.ResourceStorageModeShared);
        vertexBuffer.Label = (NSString)"metal gi terrain vertices";
        indexBuffer.Label = (NSString)"metal gi terrain indices";
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
        Build("metal gi terrain", descriptor, out var acceleration, out var scratch);
        _ctx.DeferDelete(scratch.Dispose);
        return new TerrainBlas(acceleration, vertexBuffer, indexBuffer, centre);
    }

    // Builds into a fresh acceleration structure, records the encoder and returns its size class.
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

    bool CollectObjects(Vector3 eye, WorldObjectRenderer? objects)
    {
        if (objects is null) return false;
        int built = 0;
        var candidates = new List<(ObjectStreamer.Instance Instance, float Distance)>();
        foreach (var inst in objects.RayInstances(eye, ObjectRange))
        {
            if (inst.Gpu is null) continue;
            candidates.Add((inst, Vector3.Distance(inst.Centre, eye)));
        }
        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        int count = Math.Min(candidates.Count, MaxObjects);
        for (int i = 0; i < count; i++)
        {
            var mesh = candidates[i].Instance.Gpu!;
            if (FindBlas(mesh) is not null) continue;
            if (built >= 1) break;
            if (_blasCache.Count >= MaxCachedMeshes)
            {
                var oldest = _blasCache.MinBy(e => e.LastUsed);
                if (oldest is not null)
                {
                    _blasCache.Remove(oldest);
                    _ctx.DeferDelete(oldest.Blas.Dispose);
                    _lastTlasFrame = long.MinValue / 2;
                }
            }
            if (BuildObjectBlas(mesh) is not null) { built++; _lastTlasFrame = long.MinValue / 2; }
        }
        return built > 0;
    }

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

    BlasEntry? BuildObjectBlas(GpuObjectMesh mesh)
    {
        int level = Math.Clamp(mesh.MinLevel, 0, Math.Max(mesh.LevelCount - 1, 0));
        var source = mesh.Manual.Length > level && mesh.Manual[level] is { } manual ? manual : mesh;
        int sourceLevel = ReferenceEquals(source, mesh) ? level : 0;
        var geometries = new List<MTLAccelerationStructureTriangleGeometryDescriptor>();
        var sources = new List<object> { source };
        foreach (var part in source.Parts)
        {
            if (part.Count.Length <= sourceLevel) continue;
            int count = part.Count[sourceLevel];
            if (count < 3) continue;
            var geometry = new MTLAccelerationStructureTriangleGeometryDescriptor
            {
                VertexBuffer = _ctx.BufferAt(part.Vertices.Handle),
                VertexStride = (ulong)part.Stride,
                VertexFormat = MTLAttributeFormat.Float3,
                IndexBuffer = _ctx.BufferAt(part.Indices.Handle),
                IndexBufferOffset = (ulong)part.Offset[sourceLevel] * 4,
                IndexType = MTLIndexType.UInt32,
                TriangleCount = (ulong)(count / 3),
                Opaque = true,
            };
            geometries.Add(geometry);
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
        var entry = new BlasEntry { Mesh = mesh, Level = level, Sources = sources.ToArray(), Blas = accel, LastUsed = _frame };
        _blasCache.Add(entry);
        return entry;
    }

    static bool Same(object[] sources, GpuObjectMesh mesh)
    {
        if (sources.Length != mesh.Parts.Count + 1) return false;
        if (!ReferenceEquals(sources[0], mesh)) return false;
        for (int i = 0; i < mesh.Parts.Count; i++)
            if (!ReferenceEquals(sources[i + 1], mesh.Parts[i].Vertices)) return false;
        return true;
    }

    void RebuildTlas(Vector3 eye, WorldObjectRenderer? objects)
    {
        var blas = new List<MTLAccelerationStructure> { _terrain!.Blas };
        var infos = new List<Vector4> { new(0.50f, 0.48f, 0.42f, 0f) };
        int count = 0;
        WriteInstance(0, Matrix4x4.Identity, 5, 0);
        count++;
        if (objects is not null)
        {
            var candidates = new List<(ObjectStreamer.Instance Instance, float Distance)>();
            foreach (var inst in objects.RayInstances(eye, ObjectRange))
                if (inst.Gpu is not null) candidates.Add((inst, Vector3.Distance(inst.Centre, eye)));
            candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            foreach (var (inst, _) in candidates)
            {
                if (count > MaxObjects) break;
                var entry = FindBlas(inst.Gpu!);
                if (entry is null) continue;
                int index = blas.FindIndex(a => a.NativePtr == entry.Blas.NativePtr);
                if (index < 0) { index = blas.Count; blas.Add(entry.Blas); }
                WriteInstance(count, inst.Transform, 5, index);
                infos.Add(new Vector4(0.40f, 0.38f, 0.35f, 1f));
                count++;
            }
        }
        if (count == 0) return;
        var instBuffer = _ctx.Device.NewBuffer((ulong)(64 * count), MTLResourceOptions.ResourceStorageModeShared);
        Marshal.Copy(_instanceBytes, 0, instBuffer.Contents, count * 64);
        var infoFloats = new float[count * 4];
        for (int i = 0; i < count; i++)
            for (int k = 0; k < 4; k++) infoFloats[i * 4 + k] = infos[i][k];
        var infoBuf = _ctx.Device.NewBuffer((ulong)(16 * count), MTLResourceOptions.ResourceStorageModeShared);
        Marshal.Copy(infoFloats, 0, infoBuf.Contents, infoFloats.Length);

        var descriptor = new MTLInstanceAccelerationStructureDescriptor
        {
            InstanceCount = (ulong)count,
            InstanceDescriptorBuffer = instBuffer,
            InstanceDescriptorStride = 64,
            InstancedAccelerationStructures = ArrayOf(blas.Select(a => a.NativePtr)),
            InstanceDescriptorType = MTLAccelerationStructureInstanceDescriptorType.Default,
            InstanceTransformationMatrixLayout = MTLMatrixLayout.ColumnMajor,
            Usage = MTLAccelerationStructureUsage.PreferFastIntersection,
        };
        Build("metal gi top", descriptor, out var acceleration, out var scratch);
        _ctx.DeferDelete(scratch.Dispose);
        var old = _tlas;
        _tlas = new Tlas(acceleration, instBuffer, infoBuf, count);
        InstanceCount = count;
        _lastTlasFrame = _frame;
        if (old is not null) _ctx.DeferDelete(old.Dispose);
    }

    void WriteInstance(int index, Matrix4x4 m, uint options, int blas)
    {
        int o = index * 64;
        // MTLPackedFloat4x3: four columns of three floats (column-major).
        WriteFloat(o + 0, m.M11); WriteFloat(o + 4, m.M12); WriteFloat(o + 8, m.M13);
        WriteFloat(o + 12, m.M21); WriteFloat(o + 16, m.M22); WriteFloat(o + 20, m.M23);
        WriteFloat(o + 24, m.M31); WriteFloat(o + 28, m.M32); WriteFloat(o + 32, m.M33);
        WriteFloat(o + 36, m.M41); WriteFloat(o + 40, m.M42); WriteFloat(o + 44, m.M43);
        WriteUInt(o + 48, options);
        WriteUInt(o + 52, 0xFF);
        WriteUInt(o + 56, 0);
        WriteUInt(o + 60, (uint)blas);
    }

    void WriteFloat(int offset, float value) => BitConverter.GetBytes(value).CopyTo(_instanceBytes, offset);
    void WriteUInt(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(_instanceBytes, offset);

    void UpdateGrid(Vector3 eye, HeightSnapshot heights)
    {
        bool moved = false;
        for (int c = 0; c < Cascades; c++)
        {
            var (s, _) = Layout[c];
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
            var (s, sy) = Layout[c];
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
        int iw = Columns * (IrradianceTexels + 2), ih = Cascades * Columns * Layers * (IrradianceTexels + 2);
        int dw = Columns * (DistanceTexels + 2), dh = Cascades * Columns * Layers * (DistanceTexels + 2);
        var zeroI = new byte[iw * ih * 8];
        var zeroD = new byte[dw * dh * 4];
        using (var batch = _ctx.Uploads.Begin())
        {
            batch.Write(_irradiance, 0, 0, new PixelRect(0, 0, (uint)iw, (uint)ih), zeroI);
            batch.Write(_distance, 0, 0, new PixelRect(0, 0, (uint)dw, (uint)dh), zeroD);
            batch.Write(_bases, 0, 0, new PixelRect(0, 0, Columns, (uint)(Cascades * Columns)), MemoryMarshal.AsBytes(_baseHeights.AsSpan()));
        }
        _updated = false;
        _fullUpdate = true;
    }

    void WriteConstants()
    {
        var g = _ctx.Globals;
        var sun = Read<Vector3>(g, "uAtmoSunLight");
        var light = Read<Vector4>(g, "uAtmoLight");
        bool hasSky = Environment.GetEnvironmentVariable("MEITOU_METAL_GI_FALLBACK") != "1" &&
            g.Texture("uAtmoIrradiance")?.Invoke() is { IsNull: false };
        var maps = Read<Vector4>(g, "uAtmoMaps");
        var c = new ProbeConstants
        {
            Rotation = Matrix4x4.Identity,
            Grid0 = _grid[0], Grid1 = _grid[1], Grid2 = _grid[2], Grid3 = _grid[3],
            SunLight = new Vector4(sun, 0),
            LightDir = light,
            Albedo = new Vector4(0.50f, 0.48f, 0.42f, 0),
            SkyFallback = new Vector4(0.35f, 0.48f, 0.70f, hasSky ? 1 : 0),
            Maps = maps,
            Timing = new Vector4(_fullUpdate ? 0 : _frame % Phases, _fullUpdate ? 1 : Phases, 0, 0),
        };
        Marshal.StructureToPtr(c, _constantsBuffers[_ctx.Frame.Slot].Contents, false);
    }

    void DispatchProbes()
    {
        var cmd = (MetalCommandList)_ctx.BeginNative("gi metal");
        var encoder = cmd.ComputeEncoder();
        encoder.SetComputePipelineState(_pipeline);
        encoder.SetBuffer(_constantsBuffers[_ctx.Frame.Slot], 0, 0);
        encoder.SetBuffer(_tlas!.Info, 0, 1);
        encoder.SetAccelerationStructure(_tlas.Acceleration, 2);
        encoder.SetTexture(_irradianceImage, 0);
        encoder.SetTexture(_distanceImage, 1);
        encoder.SetTexture(_baseImage, 2);
        var sky = _ctx.Globals.Texture("uAtmoIrradiance")?.Invoke() ?? default;
        if (sky.IsNull) sky = _ctx.Dummy(FrameGlobals.Sampler2D("gi sky", cube: true));
        var (skyTexture, skyState) = _ctx.Sampled(sky);
        encoder.SetTexture(skyTexture, 3);
        encoder.SetSamplerState(skyState, 0);
        var ground = _ctx.Globals.Texture("uWeatherGround")?.Invoke() ?? default;
        if (ground.IsNull) ground = _ctx.Dummy(FrameGlobals.Sampler2D("gi ground"));
        var (groundTexture, groundState) = _ctx.Sampled(ground);
        encoder.SetTexture(groundTexture, 4);
        encoder.SetSamplerState(groundState, 1);
        int phases = _fullUpdate ? 1 : Phases;
        encoder.DispatchThreadgroups(new MTLSize { width = (ulong)(Probes / phases), height = 1, depth = 1 },
            new MTLSize { width = Rays, height = 1, depth = 1 });
        cmd.EndCompute();
        _ctx.EndNative(cmd);
    }

    static T Read<T>(FrameGlobals g, string name) where T : unmanaged
    {
        var u = g.UniformValue(name);
        if (u is null) return default;
        Span<byte> bytes = stackalloc byte[System.Runtime.CompilerServices.Unsafe.SizeOf<T>()];
        return u.TryRead(bytes) ? MemoryMarshal.Read<T>(bytes) : default;
    }

    static NSArray ArrayOf(IEnumerable<IntPtr> objects)
    {
        var mutable = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSMutableArray"), "array");
        foreach (var p in objects) ObjectiveC.objc_msgSend(mutable, "addObject:", p);
        return new NSArray(mutable);
    }

    public void Dispose()
    {
        foreach (var e in _blasCache) e.Blas.Dispose();
        _blasCache.Clear();
        _tlas?.Dispose();
        _terrain?.Dispose();
        _irradiance.Dispose();
        _distance.Dispose();
        _bases.Dispose();
        foreach (var b in _constantsBuffers) b.Dispose();
        _function.Dispose();
        _library.Dispose();
        _pipeline.Dispose();
    }

    sealed class BlasEntry
    {
        public required GpuObjectMesh Mesh;
        public required int Level;
        public required object[] Sources;
        public required MTLAccelerationStructure Blas;
        public long LastUsed;
    }

    sealed class TerrainBlas(MTLAccelerationStructure blas, MTLBuffer vertices, MTLBuffer indices, Vector3 centre) : IDisposable
    {
        public MTLAccelerationStructure Blas => blas;
        public Vector3 Centre => centre;
        public string Description => $"{TerrainCells}² grid at {centre.X:0},{centre.Z:0}";
        public void Dispose() { blas.Dispose(); vertices.Dispose(); indices.Dispose(); }
    }

    sealed class Tlas(MTLAccelerationStructure acceleration, MTLBuffer instances, MTLBuffer info, int count) : IDisposable
    {
        public MTLAccelerationStructure Acceleration => acceleration;
        public MTLBuffer Info => info;
        public int Count => count;
        public void Dispose() { acceleration.Dispose(); instances.Dispose(); info.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProbeConstants
    {
        public Matrix4x4 Rotation;
        public Vector4 Grid0, Grid1, Grid2, Grid3;
        public Vector4 SunLight, LightDir, Albedo, SkyFallback, Maps, Timing;
    }
}
