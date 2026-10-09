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
    public const int Columns = 32, Layers = 8, IrradianceTexels = 8, DistanceTexels = 16, Cascades = 2, Rays = 64;
    public const int ProbesPerCascade = Columns * Columns * Layers, Probes = ProbesPerCascade * Cascades;
    /// <summary>Per cascade: the spacing across, the spacing up, the rays' length.</summary>
    static readonly (float Spacing, float SpacingY, float RayLength)[] Grid = [(128, 96, 4000), (512, 384, 16000)];

    readonly GpuContext ctx;
    readonly ShaderProgram traceProgram, blendProgram, invalidateProgram;
    readonly ComputePipeline tracePipeline, blendPipeline, invalidatePipeline;
    readonly Texture irradiance, distance;
    readonly Texture[] bases;
    readonly DeviceBuffer rays, state;
    readonly Sampler linear, nearest;
    readonly Vector4[] grid = new Vector4[Cascades * 2];
    readonly float[] baseHeights = new float[Columns * Columns * Cascades];
    readonly (int X, int Z)[] placed = new (int, int)[Cascades];
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
    public long Updates { get; private set; }

    /// <summary>What the shaders read: x 1 while the probes are on and filled, w the hysteresis.</summary>
    Vector4 Params => new(Enabled && Active && updated ? 1 : 0, 0, 0, Hysteresis);
    /// <summary>False while the frame does not update the probes (the bench's gi-scene switch): the shaders then do not read them.</summary>
    public bool Active { get; set; } = true;

    public GiProbes(GpuContext ctx)
    {
        this.ctx = ctx;
        traceProgram = ctx.Shaders.Compute(GiShaders.ProbeTrace, "gi probe trace",
        [
            (0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer), (2, DescriptorType.StorageBuffer), (3, DescriptorType.UniformBuffer),
            (4, DescriptorType.CombinedImageSampler), (5, DescriptorType.CombinedImageSampler), (6, DescriptorType.CombinedImageSampler),
            (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler), (9, DescriptorType.CombinedImageSampler),
        ], ctx.Bindless.Layout);
        blendProgram = ctx.Shaders.Compute(GiShaders.ProbeBlend, "gi probe blend",
        [
            (0, DescriptorType.StorageBuffer), (1, DescriptorType.StorageBuffer), (2, DescriptorType.UniformBuffer),
            (3, DescriptorType.StorageImage), (4, DescriptorType.StorageImage), (5, DescriptorType.CombinedImageSampler),
        ]);
        invalidateProgram = ctx.Shaders.Compute(GiShaders.ProbeInvalidate, "gi probe invalidate",
        [
            (0, DescriptorType.StorageBuffer), (1, DescriptorType.UniformBuffer), (2, DescriptorType.StorageImage), (3, DescriptorType.CombinedImageSampler),
        ]);
        invalidatePipeline = ctx.Pipelines.Get(new ComputePipelineDesc(invalidateProgram, "gi probe invalidate"));
        tracePipeline = ctx.Pipelines.Get(new ComputePipelineDesc(traceProgram, "gi probe trace"));
        blendPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(blendProgram, "gi probe blend"));
        int rows = Cascades * Columns * Layers;
        irradiance = Texture.Create(ctx, new TextureDesc(Format.R16G16B16A16Sfloat, Columns * (IrradianceTexels + 2), rows * (IrradianceTexels + 2),
            Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst, Name: "gi probe irradiance"));
        distance = Texture.Create(ctx, new TextureDesc(Format.R16G16Sfloat, Columns * (DistanceTexels + 2), rows * (DistanceTexels + 2),
            Use: TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst, Name: "gi probe distance"));
        // One per frame slot: the CPU writes the base heights of the frame being recorded while the one before may still read its own.
        bases = new Texture[ctx.Device.Frames.Count];
        for (int i = 0; i < bases.Length; i++)
            bases[i] = Texture.Create(ctx, new TextureDesc(Format.R32Sfloat, Columns, Columns * Cascades, Use: TextureUse.Sampled | TextureUse.TransferDst, Name: "gi probe bases"));
        rays = DeviceBuffer.Create(ctx, (ulong)Probes * Rays * 16, BufferUse.Storage, "gi probe rays");
        state = DeviceBuffer.Create(ctx, (ulong)Probes * 16, BufferUse.Storage | BufferUse.TransferDst, "gi probe state");
        // Every probe starts invalid (alpha 0: no surface reads it, no bounce sees it) and unplaced (state w 0: its first blend starts over).
        var cmd = ctx.Device.BeginImmediate();
        ctx.Device.Vk.CmdFillBuffer(cmd, state.Handle, 0, state.Size, 0);
        var clear = new ClearColorValue(0f, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        ctx.Device.Vk.CmdClearColorImage(cmd, irradiance.Image, ImageLayout.General, &clear, 1, &range);
        ctx.Device.Vk.CmdClearColorImage(cmd, distance.Image, ImageLayout.General, &clear, 1, &range);
        ctx.Device.EndImmediate(cmd);
        linear = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
        nearest = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));

        var g = ctx.Globals;
        g.PublishUniform("uGiParams", () => Params);
        g.PublishUniformArray("uGiGrid", grid, () => grid.Length);
        g.Publish("uGiIrradiance", () => new SampledTexture(linear, irradiance.View(), irradiance.Image));
        g.Publish("uGiDistance", () => new SampledTexture(linear, distance.View(), distance.Image));
        g.Publish("uGiBase", () => new SampledTexture(nearest, bases[slot].View(), bases[slot].Image));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Constants
    {
        public Vector4 Params;
        public Vector4 Grid0, Grid1, Grid2, Grid3;
        public Matrix4x4 Rotation;
        public Vector4 Origin, SunLight, LightDir, Maps, Albedo, RayLength;
    }

    /// <summary>
    /// Places the grids around <paramref name="eye"/> (bases from <paramref name="heights"/>) and records this frame's probe trace and blend against
    /// <paramref name="scene"/>'s structure, after its <see cref="GiScene.Update"/>.
    /// </summary>
    public void Update(GiScene scene, Vector3 eye, HeightSnapshot heights)
    {
        if (scene.Top is null) return;
        slot = ctx.Frame.Slot;
        int phases = Phases is 1 or 2 or 4 or 8 ? Phases : 4, phase = (int)(Updates % phases);
        bool moved = false;
        for (int c = 0; c < Cascades; c++)
        {
            var (s, sy, _) = Grid[c];
            int ox = (int)MathF.Floor(eye.X / s) - Columns / 2, oz = (int)MathF.Floor(eye.Z / s) - Columns / 2;
            grid[c * 2] = new Vector4(ox * s, oz * s, s, sy);
            grid[c * 2 + 1] = new Vector4(Mod(ox, Columns), Mod(oz, Columns), 2 * s, 0);
            // The bases only change when the grid scrolls or the terrain's fine window is swapped.
            if (placed[c] == (ox, oz) && ReferenceEquals(placedHeights, heights.Fine)) continue;
            placed[c] = (ox, oz);
            moved = true;
            for (int k = 0; k < Columns; k++)
                for (int i = 0; i < Columns; i++)
                {
                    float x = (ox + i) * s, z = (oz + k) * s;
                    // The lowest ground under the column's cell, so that the bottom probe stands above it all.
                    float h = heights.HeightAt(x, z);
                    h = MathF.Min(h, MathF.Min(heights.HeightAt(x + s * 0.4f, z), heights.HeightAt(x - s * 0.4f, z)));
                    h = MathF.Min(h, MathF.Min(heights.HeightAt(x, z + s * 0.4f), heights.HeightAt(x, z - s * 0.4f)));
                    baseHeights[(c * Columns + Mod(oz + k, Columns)) * Columns + Mod(ox + i, Columns)] = h;
                }
        }
        placedHeights = heights.Fine;
        var bases = this.bases[slot];
        ctx.Uploads.Write(bases, 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(Columns, Columns * Cascades)), MemoryMarshal.AsBytes(baseHeights.AsSpan()));

        var g = ctx.Globals;
        var constants = new Constants
        {
            // The hysteresis per update, so that a probe settles in as many frames whatever the phases.
            Params = new Vector4(1, phase, phases, MathF.Pow(Hysteresis, phases)),
            Grid0 = grid[0], Grid1 = grid[1], Grid2 = grid[2], Grid3 = grid[3],
            Rotation = RandomRotation(),
            Origin = new Vector4(scene.Origin, Updates),
            SunLight = new Vector4(Read<Vector3>(g, "uAtmoSunLight"), 0),
            LightDir = Read<Vector4>(g, "uAtmoLight"),
            Maps = Read<Vector4>(g, "uAtmoMaps"),
            RayLength = new Vector4(Grid[0].RayLength, Grid[1].RayLength, 0, 0),
        };
        var ground = g.Texture("uWeatherGround")?.Invoke() ?? default;
        constants.Albedo = new Vector4(ObjectAlbedo, ground.IsNull ? 0 : 1);
        var block = ctx.Frame.Constants.Write<Constants>(new ReadOnlySpan<Constants>(&constants, 1), 256);

        var cmd = ctx.BeginNative("gi probes");
        cmd.BeginLabel("gi probes");
        cmd.Barrier(BarrierBatch.Full);
        if (moved)
        {
            // The tiles whose probe moved hold another place's light until their update: hide them (GiShaders.ProbeInvalidate).
            var stateInfo = new DescriptorBufferInfo(state.Handle, 0, state.Size);
            var uniform = new DescriptorBufferInfo(block.Handle, block.Offset, (ulong)sizeof(Constants));
            var images = stackalloc DescriptorImageInfo[2];
            images[0] = new DescriptorImageInfo(default, irradiance.View(), ImageLayout.General);
            images[1] = new DescriptorImageInfo(nearest, bases.View(), ImageLayout.General);
            var writes = stackalloc WriteDescriptorSet[4];
            writes[0] = Buffer(0, DescriptorType.StorageBuffer, &stateInfo);
            writes[1] = Buffer(1, DescriptorType.UniformBuffer, &uniform);
            writes[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &images[0] };
            writes[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &images[1] };
            cmd.BindPipeline(invalidatePipeline);
            cmd.PushDescriptors(invalidateProgram.Layout, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 4), PipelineBindPoint.Compute);
            cmd.Dispatch((uint)(Probes / 64));
            cmd.Barrier(ComputeAfterCompute);
        }
        {
            var top = scene.Top.Handle;
            var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top };
            var records = new DescriptorBufferInfo(scene.Records.Buffer, scene.Records.Offset, scene.Records.Size);
            var rayInfo = new DescriptorBufferInfo(rays.Handle, 0, rays.Size);
            var uniform = new DescriptorBufferInfo(block.Handle, block.Offset, (ulong)sizeof(Constants));
            var images = stackalloc DescriptorImageInfo[6];
            images[0] = new DescriptorImageInfo(linear, irradiance.View(), ImageLayout.General);
            images[1] = new DescriptorImageInfo(linear, distance.View(), ImageLayout.General);
            images[2] = new DescriptorImageInfo(nearest, bases.View(), ImageLayout.General);
            images[3] = Image(g, "uAtmoIrradiance", cube: true);
            images[4] = Image(g, "uAtmoAmbientMap", cube: false);
            images[5] = ground.IsNull ? images[2] : new DescriptorImageInfo(ground.Sampler, ground.View, ImageLayout.General);
            var writes = stackalloc WriteDescriptorSet[10];
            writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr };
            writes[1] = Buffer(1, DescriptorType.StorageBuffer, &records);
            writes[2] = Buffer(2, DescriptorType.StorageBuffer, &rayInfo);
            writes[3] = Buffer(3, DescriptorType.UniformBuffer, &uniform);
            for (int i = 0; i < 6; i++)
                writes[4 + i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = (uint)(4 + i), DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &images[i] };
            cmd.BindPipeline(tracePipeline);
            cmd.PushDescriptors(traceProgram.Layout, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 10), PipelineBindPoint.Compute);
            // Set 1: the bindless table, for the objects' diffuse maps at the hits.
            var table = ctx.Bindless.Set;
            cmd.BindSets(traceProgram.Layout, 1, new ReadOnlySpan<DescriptorSet>(&table, 1), default, PipelineBindPoint.Compute);
            cmd.Dispatch((uint)(Probes / phases));
        }
        cmd.Barrier(ComputeAfterCompute);
        {
            var rayInfo = new DescriptorBufferInfo(rays.Handle, 0, rays.Size);
            var stateInfo = new DescriptorBufferInfo(state.Handle, 0, state.Size);
            var uniform = new DescriptorBufferInfo(block.Handle, block.Offset, (ulong)sizeof(Constants));
            var images = stackalloc DescriptorImageInfo[3];
            images[0] = new DescriptorImageInfo(default, irradiance.View(), ImageLayout.General);
            images[1] = new DescriptorImageInfo(default, distance.View(), ImageLayout.General);
            images[2] = new DescriptorImageInfo(nearest, bases.View(), ImageLayout.General);
            var writes = stackalloc WriteDescriptorSet[6];
            writes[0] = Buffer(0, DescriptorType.StorageBuffer, &rayInfo);
            writes[1] = Buffer(1, DescriptorType.StorageBuffer, &stateInfo);
            writes[2] = Buffer(2, DescriptorType.UniformBuffer, &uniform);
            writes[3] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 3, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &images[0] };
            writes[4] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 4, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &images[1] };
            writes[5] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &images[2] };
            cmd.BindPipeline(blendPipeline);
            cmd.PushDescriptors(blendProgram.Layout, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 6), PipelineBindPoint.Compute);
            cmd.Dispatch((uint)(Probes / phases));
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
        if (!t.IsNull) return new DescriptorImageInfo(t.Sampler, t.View, ImageLayout.General);
        // Not published yet (the game sky off): a stand-in the shader never reads (uAtmoMaps says the map is absent).
        var standIn = ctx.Dummy(FrameGlobals.Sampler2D(name, cube));
        return new DescriptorImageInfo(standIn.Sampler, standIn.View, ImageLayout.General);
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

    public string Describe() => $"{Cascades} cascades of {Columns}×{Layers}×{Columns} probes ({Grid[0].Spacing:0} and {Grid[1].Spacing:0} apart), {Rays} rays each, 1/{Phases} a frame, {(Enabled ? "lighting" : "not lighting")}";

    public void Dispose()
    {
        irradiance.Dispose();
        distance.Dispose();
        foreach (var b in bases) b.Dispose();
        rays.Dispose();
        state.Dispose();
        traceProgram.Dispose();
        blendProgram.Dispose();
        invalidateProgram.Dispose();
    }
}
