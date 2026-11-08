using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Meitou.Rendering;
using Meitou.Rendering.Gi;
using Meitou.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using Silk.NET.Vulkan;

namespace Meitou.Metal.Gi;

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

    readonly MetalContext ctx;
    readonly MTLComputePipelineState pipeline;
    readonly MTLLibrary library;
    readonly MTLFunction function;
    readonly GpuTexture irradiance, distance, bases;
    readonly MTLTexture irradianceImage, distanceImage, baseImage;
    readonly GpuSampler linear, nearest;
    readonly MTLBuffer[] constantsBuffers = new MTLBuffer[MetalContext.FrameRingDepth];
    readonly MTLSamplerState skySampler;
    readonly Vector4[] grid = new Vector4[4];
    readonly List<BlasEntry> blasCache = [];
    readonly byte[] instanceBytes = new byte[64 * (MaxObjects + 1)];
    readonly float[] instanceInfo = new float[4 * (MaxObjects + 1)];
    readonly float[] baseHeights = new float[Columns * Columns * Cascades];
    readonly (int X, int Z)[] placed = new (int, int)[Cascades];
    readonly bool[] placed_ = new bool[Cascades];
    TerrainBlas? terrain;
    Tlas? tlas;
    long frame, lastTlasFrame = -1000;
    bool updated;
    bool fullUpdate = true;
    Vector3 lastEye;

    public MetalProbeGi(MetalContext ctx)
    {
        this.ctx = ctx;
        var irrRows = Cascades * Columns * Layers * (IrradianceTexels + 2);
        var distRows = Cascades * Columns * Layers * (DistanceTexels + 2);
        irradiance = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba16f, Columns * (IrradianceTexels + 2), irrRows,
            Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst, Name: "metal gi irradiance"));
        distance = ctx.CreateTexture(new TextureDesc(InternalFormat.RG16f, Columns * (DistanceTexels + 2), distRows,
            Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst, Name: "metal gi distance"));
        bases = ctx.CreateTexture(new TextureDesc(InternalFormat.R32f, Columns, Cascades * Columns,
            Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "metal gi bases"));
        irradianceImage = ctx.TextureAt(irradiance);
        distanceImage = ctx.TextureAt(distance);
        baseImage = ctx.TextureAt(bases);
        linear = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
        nearest = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest,
            TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
        for (int i = 0; i < constantsBuffers.Length; i++)
            constantsBuffers[i] = ctx.Device.NewBuffer((ulong)Marshal.SizeOf<ProbeConstants>(), MTLResourceOptions.ResourceStorageModeShared);
        var skyDescriptor = new MTLSamplerDescriptor(
            new ObjectiveCClass(ObjectiveC.objc_getClass("MTLSamplerDescriptor")).AllocInit());
        skyDescriptor.MinFilter = MTLSamplerMinMagFilter.Linear;
        skyDescriptor.MagFilter = MTLSamplerMinMagFilter.Linear;
        skyDescriptor.SAddressMode = MTLSamplerAddressMode.ClampToEdge;
        skyDescriptor.TAddressMode = MTLSamplerAddressMode.ClampToEdge;
        skySampler = ctx.Device.NewSamplerState(skyDescriptor);

        var source = ReadResource("Meitou.Metal.Gi.ProbeGi.metal");
        var options = new MTLCompileOptions { LanguageVersion = MTLLanguageVersion.Version30, FastMathEnabled = true };
        NSError error = default;
        library = ctx.Device.NewLibrary((NSString)source, options, ref error);
        if (error.NativePtr != IntPtr.Zero) throw new InvalidOperationException($"metal GI shader: {error.LocalizedDescription}");
        function = library.NewFunction((NSString)"probe_gi");
        pipeline = ctx.Device.NewComputePipelineState(function, ref error);
        if (error.NativePtr != IntPtr.Zero) throw new InvalidOperationException($"metal GI pipeline: {error.LocalizedDescription}");

        var g = ctx.Globals;
        g.PublishUniform("uGiParams", () => Params);
        g.PublishUniformArray("uGiGrid", grid, () => grid.Length);
        g.Publish("uGiIrradiance", () => new SampledTexture(linear, irradiance.View()));
        g.Publish("uGiDistance", () => new SampledTexture(linear, distance.View()));
        g.Publish("uGiBase", () => new SampledTexture(nearest, bases.View()));
    }

    public bool Enabled { get; set; } = true;
    public bool InteriorEnabled { get; set; } = true;
    public bool Active { get; set; } = true;
    public bool Shade { get; set; } = true;
    public float Strength { get; set; } = 0.8f;
    public float InteriorGain { get; set; } = 1;
    public int InstanceCount { get; private set; }
    public int MeshCount => blasCache.Count;

    Vector4 Params => new(Enabled && Active && Shade && updated ? 1 : 0, Strength, 0, InteriorEnabled ? MathF.Max(1, InteriorGain) : 0);

    public string Describe() => $"metal probes {Probes}, {InstanceCount} instances ({MeshCount} meshes), {terrain?.Description ?? "no terrain"}";

    public void Update(Vector3 eye, HeightSnapshot heights, WorldObjectRenderer? objects, WorldLamps? lamps, FogVolumes? fog)
    {
        frame++;
        Log("begin");
        // A teleport (return to the character, fly-to) leaves every cached object structure behind and can otherwise
        // accumulate old mesh memory for the rest of the session.
        if (frame > 1 && Vector3.DistanceSquared(eye, lastEye) > 2000f * 2000f) ClearObjectCache();
        lastEye = eye;
        // Close upload/compute encoders before the acceleration-structure encoders.
        ((MetalCommandList)ctx.Frame.Commands).Finish();
        bool terrainChanged = UpdateTerrain(eye, heights);
        Log($"terrain {terrainChanged}");
        bool builtMesh = CollectObjects(eye, objects);
        Log($"objects {builtMesh} ({blasCache.Count})");
        if (terrainChanged || builtMesh || tlas is null || frame - lastTlasFrame >= 30)
            RebuildTlas(eye, objects);
        Log($"tlas {tlas?.Count ?? 0}");
        if (tlas is null) return;
        UpdateGrid(eye, heights);
        Log($"grid full={fullUpdate}");
        WriteConstants();
        Log("constants");
        if (Enabled && Active && Shade)
        {
            DispatchProbes();
            Log("dispatch");
            updated = true;
            fullUpdate = false;
        }
    }

    void ClearObjectCache()
    {
        foreach (var e in blasCache) ctx.DeferDelete(e.Blas.Dispose);
        blasCache.Clear();
        if (tlas is not null) { ctx.DeferDelete(tlas.Dispose); tlas = null; }
        lastTlasFrame = -1000;
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
        if (terrain is not null && Vector3.DistanceSquared(terrain.Centre, centre) < 1) return false;
        var old = terrain;
        terrain = BuildTerrain(centre, heights);
        if (old is not null) ctx.DeferDelete(old.Dispose);
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
        var vertexBuffer = ctx.Device.NewBuffer((ulong)(vertices.Length * 4), MTLResourceOptions.ResourceStorageModeShared);
        var indexBuffer = ctx.Device.NewBuffer((ulong)(indices.Length * 4), MTLResourceOptions.ResourceStorageModeShared);
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
        ctx.DeferDelete(scratch.Dispose);
        return new TerrainBlas(acceleration, vertexBuffer, indexBuffer, centre);
    }

    // Builds into a fresh acceleration structure, records the encoder and returns its size class.
    void Build(string name, MTLPrimitiveAccelerationStructureDescriptor descriptor, out MTLAccelerationStructure acceleration, out MTLBuffer scratch)
    {
        var sizes = ctx.Device.AccelerationStructureSizes(descriptor);
        acceleration = ctx.Device.NewAccelerationStructure(sizes.accelerationStructureSize);
        acceleration.Label = (NSString)name;
        scratch = ctx.Device.NewBuffer(Math.Max(sizes.buildScratchBufferSize, 1), MTLResourceOptions.ResourceStorageModePrivate);
        var encoder = ctx.Frame.Command.AccelerationStructureCommandEncoder();
        encoder.BuildAccelerationStructure(acceleration, descriptor, scratch, 0);
        encoder.EndEncoding();
    }

    void Build(string name, MTLInstanceAccelerationStructureDescriptor descriptor, out MTLAccelerationStructure acceleration, out MTLBuffer scratch)
    {
        var sizes = ctx.Device.AccelerationStructureSizes(descriptor);
        acceleration = ctx.Device.NewAccelerationStructure(sizes.accelerationStructureSize);
        acceleration.Label = (NSString)name;
        scratch = ctx.Device.NewBuffer(Math.Max(sizes.buildScratchBufferSize, 1), MTLResourceOptions.ResourceStorageModePrivate);
        var encoder = ctx.Frame.Command.AccelerationStructureCommandEncoder();
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
            if (blasCache.Count >= MaxCachedMeshes)
            {
                var oldest = blasCache.MinBy(e => e.LastUsed);
                if (oldest is not null)
                {
                    blasCache.Remove(oldest);
                    ctx.DeferDelete(oldest.Blas.Dispose);
                    lastTlasFrame = long.MinValue / 2;
                }
            }
            if (BuildObjectBlas(mesh) is not null) { built++; lastTlasFrame = long.MinValue / 2; }
        }
        return built > 0;
    }

    BlasEntry? FindBlas(GpuObjectMesh mesh)
    {
        int level = Math.Clamp(mesh.MinLevel, 0, Math.Max(mesh.LevelCount - 1, 0));
        for (int i = 0; i < blasCache.Count; i++)
        {
            var e = blasCache[i];
            if (!ReferenceEquals(e.Mesh, mesh)) continue;
            if (e.Level == level && Same(e.Sources, mesh))
            {
                e.LastUsed = frame;
                return e;
            }
            blasCache.RemoveAt(i);
            ctx.DeferDelete(e.Blas.Dispose);
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
                VertexBuffer = ctx.BufferAt(part.Vertices.Handle),
                VertexStride = (ulong)part.Stride,
                VertexFormat = MTLAttributeFormat.Float3,
                IndexBuffer = ctx.BufferAt(part.Indices.Handle),
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
        ctx.DeferDelete(scratch.Dispose);
        var entry = new BlasEntry { Mesh = mesh, Level = level, Sources = sources.ToArray(), Blas = accel, LastUsed = frame };
        blasCache.Add(entry);
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
        var blas = new List<MTLAccelerationStructure> { terrain!.Blas };
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
        var instBuffer = ctx.Device.NewBuffer((ulong)(64 * count), MTLResourceOptions.ResourceStorageModeShared);
        Marshal.Copy(instanceBytes, 0, instBuffer.Contents, count * 64);
        var infoFloats = new float[count * 4];
        for (int i = 0; i < count; i++)
            for (int k = 0; k < 4; k++) infoFloats[i * 4 + k] = infos[i][k];
        var infoBuf = ctx.Device.NewBuffer((ulong)(16 * count), MTLResourceOptions.ResourceStorageModeShared);
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
        ctx.DeferDelete(scratch.Dispose);
        var old = tlas;
        tlas = new Tlas(acceleration, instBuffer, infoBuf, count);
        InstanceCount = count;
        lastTlasFrame = frame;
        if (old is not null) ctx.DeferDelete(old.Dispose);
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

    void WriteFloat(int offset, float value) => BitConverter.GetBytes(value).CopyTo(instanceBytes, offset);
    void WriteUInt(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(instanceBytes, offset);

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
            if (placed_[c] && Math.Abs(ox - placed[c].X) < 8 && Math.Abs(oz - placed[c].Z) < 8) continue;
            placed[c] = (ox, oz);
            placed_[c] = true;
            moved = true;
        }
        if (!moved) return;
        for (int c = 0; c < Cascades; c++)
        {
            var (s, sy) = Layout[c];
            var (ox, oz) = placed[c];
            grid[c * 2] = new Vector4(ox * s, oz * s, s, sy);
            grid[c * 2 + 1] = new Vector4(0, 0, 2 * s, 0);
            for (int k = 0; k < Columns; k++)
            for (int i = 0; i < Columns; i++)
            {
                float x = (ox + i) * s, z = (oz + k) * s;
                float h = heights.HeightAt(x, z);
                h = MathF.Min(h, MathF.Min(heights.HeightAt(x + s * 0.4f, z), heights.HeightAt(x - s * 0.4f, z)));
                h = MathF.Min(h, MathF.Min(heights.HeightAt(x, z + s * 0.4f), heights.HeightAt(x, z - s * 0.4f)));
                baseHeights[(c * Columns + k) * Columns + i] = h;
            }
        }
        int iw = Columns * (IrradianceTexels + 2), ih = Cascades * Columns * Layers * (IrradianceTexels + 2);
        int dw = Columns * (DistanceTexels + 2), dh = Cascades * Columns * Layers * (DistanceTexels + 2);
        var zeroI = new byte[iw * ih * 8];
        var zeroD = new byte[dw * dh * 4];
        using (var batch = ctx.Uploads.Begin())
        {
            batch.Write(irradiance, 0, 0, new PixelRect(0, 0, (uint)iw, (uint)ih), zeroI);
            batch.Write(distance, 0, 0, new PixelRect(0, 0, (uint)dw, (uint)dh), zeroD);
            batch.Write(bases, 0, 0, new PixelRect(0, 0, Columns, (uint)(Cascades * Columns)), MemoryMarshal.AsBytes(baseHeights.AsSpan()));
        }
        updated = false;
        fullUpdate = true;
    }

    void WriteConstants()
    {
        var g = ctx.Globals;
        var sun = Read<Vector3>(g, "uAtmoSunLight");
        var light = Read<Vector4>(g, "uAtmoLight");
        bool hasSky = Environment.GetEnvironmentVariable("MEITOU_METAL_GI_FALLBACK") != "1" &&
            g.Texture("uAtmoIrradiance")?.Invoke() is { IsNull: false };
        var maps = Read<Vector4>(g, "uAtmoMaps");
        var c = new ProbeConstants
        {
            Rotation = Matrix4x4.Identity,
            Grid0 = grid[0], Grid1 = grid[1], Grid2 = grid[2], Grid3 = grid[3],
            SunLight = new Vector4(sun, 0),
            LightDir = light,
            Albedo = new Vector4(0.50f, 0.48f, 0.42f, 0),
            SkyFallback = new Vector4(0.35f, 0.48f, 0.70f, hasSky ? 1 : 0),
            Maps = maps,
            Timing = new Vector4(fullUpdate ? 0 : frame % Phases, fullUpdate ? 1 : Phases, 0, 0),
        };
        Marshal.StructureToPtr(c, constantsBuffers[ctx.Frame.Slot].Contents, false);
    }

    void DispatchProbes()
    {
        var cmd = (MetalCommandList)ctx.BeginNative("gi metal");
        var encoder = cmd.ComputeEncoder();
        encoder.SetComputePipelineState(pipeline);
        encoder.SetBuffer(constantsBuffers[ctx.Frame.Slot], 0, 0);
        encoder.SetBuffer(tlas!.Info, 0, 1);
        encoder.SetAccelerationStructure(tlas.Acceleration, 2);
        encoder.SetTexture(irradianceImage, 0);
        encoder.SetTexture(distanceImage, 1);
        encoder.SetTexture(baseImage, 2);
        var sky = ctx.Globals.Texture("uAtmoIrradiance")?.Invoke() ?? default;
        if (sky.IsNull) sky = ctx.Dummy(FrameGlobals.Sampler2D("gi sky", cube: true));
        var (skyTexture, skyState) = ctx.Sampled(sky);
        encoder.SetTexture(skyTexture, 3);
        encoder.SetSamplerState(skyState, 0);
        var ground = ctx.Globals.Texture("uWeatherGround")?.Invoke() ?? default;
        if (ground.IsNull) ground = ctx.Dummy(FrameGlobals.Sampler2D("gi ground"));
        var (groundTexture, groundState) = ctx.Sampled(ground);
        encoder.SetTexture(groundTexture, 4);
        encoder.SetSamplerState(groundState, 1);
        int phases = fullUpdate ? 1 : Phases;
        encoder.DispatchThreadgroups(new MTLSize { width = (ulong)(Probes / phases), height = 1, depth = 1 },
            new MTLSize { width = Rays, height = 1, depth = 1 });
        cmd.EndCompute();
        ctx.EndNative(cmd);
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
        foreach (var e in blasCache) e.Blas.Dispose();
        blasCache.Clear();
        tlas?.Dispose();
        terrain?.Dispose();
        irradiance.Dispose();
        distance.Dispose();
        bases.Dispose();
        foreach (var b in constantsBuffers) b.Dispose();
        function.Dispose();
        library.Dispose();
        pipeline.Dispose();
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
