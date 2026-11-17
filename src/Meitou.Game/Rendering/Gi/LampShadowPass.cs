using Meitou.Game.Rendering.Objects;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Data.World;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gi;

/// <summary>
/// The view a lamp shadow trace reconstructs positions with (<see cref="PostProcess"/>'s, as the GI resolve's).
/// </summary>
readonly record struct LampShadowView(
    Vector3 Right, Vector3 Up, Vector3 Back, Vector3 Eye, Vector2 NearPlanes, Vector2 Tan, Vector2 Size, float Footprint, float Phase);

/// <summary>
/// The trace of the lamps' shadows (docs/render-lights.md "Traced lamp shadows"): <see cref="LampShadowShaders.Trace"/> over the scene depth and the
/// surface target into a target of the render size, in the frame's traced scene. Render thread only.
/// </summary>
sealed unsafe class LampShadowPass : IDisposable
{
    #region Full-screen trace

    /// <summary>
    /// The trace's descriptor bindings pushed at once.
    /// </summary>
    const int Bindings = 12;

    /// <summary>
    /// The GPU context the passes record into.
    /// </summary>
    readonly GpuContext _ctx;

    /// <summary>
    /// The trace over every lamp at once (<see cref="LampShadowShaders.Trace"/>).
    /// </summary>
    readonly GpuProgram _program;

    /// <summary>
    /// The pipeline of <see cref="_program"/>.
    /// </summary>
    readonly GpuComputePipeline _pipeline;

    /// <summary>
    /// The nearest, clamped sampler every sampled binding reads through.
    /// </summary>
    readonly GpuSampler _sampler;

    /// <summary>
    /// The filter that spreads the traced flags by the lamps' size.
    /// </summary>
    readonly PenumbraFilter _penumbra;

    /// <summary>
    /// The per-lamp programs: the rays over a lamp's cells, its light where its filtered flag is, and the classification of its cells.
    /// </summary>
    readonly GpuProgram _oneProgram, _sumProgram, _classifyProgram;

    /// <summary>
    /// The pipelines of <see cref="_oneProgram"/>, <see cref="_sumProgram"/> and <see cref="_classifyProgram"/>.
    /// </summary>
    readonly GpuComputePipeline _onePipeline, _sumPipeline, _classifyPipeline;

    /// <summary>
    /// The per-lamp rays over one pixel in four and the light kernel that reads their half-size flags (<see cref="LampShadowShaders.TraceOneHalf"/>,
    /// <see cref="LampShadowShaders.TraceLightHalf"/>).
    /// </summary>
    readonly GpuProgram _oneHalfProgram, _sumHalfProgram;

    /// <summary>
    /// The pipelines of <see cref="_oneHalfProgram"/> and <see cref="_sumHalfProgram"/>.
    /// </summary>
    readonly GpuComputePipeline _oneHalfPipeline, _sumHalfPipeline;

    /// <summary>
    /// The rays of the pixels the half-size rays listed for a ray of their own (<see cref="LampShadowShaders.TraceOwn"/>), and its pipeline.
    /// </summary>
    readonly GpuProgram _ownProgram;

    /// <summary>
    /// See <see cref="_ownProgram"/>.
    /// </summary>
    readonly GpuComputePipeline _ownPipeline;

    /// <summary>
    /// The per-lamp rays' flags, the filter's scratch and its result: arrays of slices (RG16F), a slice a layer of a round. At the render size
    /// one slice, at half of it (rounded up) <see cref="LampShadowShaders.MaxSlices"/>.
    /// </summary>
    GpuTexture? _sliceLost, _sliceScratch, _sliceFiltered;

    /// <summary>
    /// With the half-size slices: per pixel of them, the pixels of its 2 x 2 traced on their own (R32UI, <see cref="LampShadowShaders.TraceOwn"/>).
    /// </summary>
    GpuTexture? _sliceExact;

    /// <summary>
    /// How the per-lamp rays run: 0 a ray a pixel; 1 a ray for one pixel in four, always the same one; 2 the pixel of the four changes each frame
    /// under a temporal resolve (four frames cover them all, and it averages them; the nearest to a ray a pixel in the pictures compared).
    /// </summary>
    const int HalfMode = 2;

    /// <summary>
    /// The trace's constants (<see cref="LampShadowShaders"/>' Params block).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        /// <summary>
        /// The lamps' grid, the camera, the scene's origin, the depth's planes and tangents, the size and the lamp's slot in its list.
        /// </summary>
        public Vector4 LightGrid, Right, Up, Back, Eye, Origin, PlanesTan, Size, Rect;

        /// <summary>
        /// The cells a lamp's rays cover, one bit each.
        /// </summary>
        public fixed uint Cells[64];
    }

    /// <summary>
    /// Compiles the trace, per-lamp and classification programs and makes their pipelines.
    /// </summary>
    public LampShadowPass(GpuContext ctx)
    {
        _ctx = ctx;
        _penumbra = new PenumbraFilter(ctx);
        _program = ctx.Shaders.Compute(LampShadowShaders.Trace(LampShadowShaders.MaxLamps), "lamp shadow trace",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.AccelerationStructureKhr),
            (3, DescriptorType.StorageBuffer), (4, DescriptorType.UniformBuffer), (5, DescriptorType.StorageImage),
            (6, DescriptorType.CombinedImageSampler), (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler),
            (9, DescriptorType.StorageImage), (10, DescriptorType.StorageImage), (11, DescriptorType.CombinedImageSampler),
        ], GpuSetLayout.Bindless);
        _pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_program, "lamp shadow trace"));
        _oneProgram = ctx.Shaders.Compute(LampShadowShaders.TraceOne, "lamp shadow one",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.AccelerationStructureKhr),
            (3, DescriptorType.StorageBuffer), (4, DescriptorType.UniformBuffer), (5, DescriptorType.StorageImage),
            (6, DescriptorType.CombinedImageSampler), (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler),
            (9, DescriptorType.StorageImage), (11, DescriptorType.CombinedImageSampler), (12, DescriptorType.StorageBuffer),
        ], GpuSetLayout.Bindless);
        _onePipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_oneProgram, "lamp shadow one"));

        // The light of the flagged pixels: the rays' bindings (5 the filtered flag, 9 the sum).
        _sumProgram = ctx.Shaders.Compute(LampShadowShaders.TraceLight, "lamp shadow light",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.AccelerationStructureKhr),
            (3, DescriptorType.StorageBuffer), (4, DescriptorType.UniformBuffer), (5, DescriptorType.StorageImage),
            (6, DescriptorType.CombinedImageSampler), (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler),
            (9, DescriptorType.StorageImage), (10, DescriptorType.StorageImage), (11, DescriptorType.CombinedImageSampler),
            (12, DescriptorType.StorageBuffer),
        ], GpuSetLayout.Bindless);
        _sumPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_sumProgram, "lamp shadow light"));
        _oneHalfProgram = ctx.Shaders.Compute(LampShadowShaders.TraceOneHalf, "lamp shadow one half",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.AccelerationStructureKhr),
            (3, DescriptorType.StorageBuffer), (4, DescriptorType.UniformBuffer), (5, DescriptorType.StorageImage),
            (6, DescriptorType.CombinedImageSampler), (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler),
            (9, DescriptorType.StorageImage), (11, DescriptorType.CombinedImageSampler), (12, DescriptorType.StorageBuffer),
            (13, DescriptorType.StorageImage),
        ], GpuSetLayout.Bindless);
        _oneHalfPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_oneHalfProgram, "lamp shadow one half"));
        _ownProgram = ctx.Shaders.Compute(LampShadowShaders.TraceOwn, "lamp shadow own rays",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.AccelerationStructureKhr),
            (3, DescriptorType.StorageBuffer), (4, DescriptorType.UniformBuffer), (5, DescriptorType.StorageImage),
            (6, DescriptorType.CombinedImageSampler), (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler),
            (9, DescriptorType.StorageImage), (11, DescriptorType.CombinedImageSampler), (12, DescriptorType.StorageBuffer),
            (13, DescriptorType.StorageImage),
        ], GpuSetLayout.Bindless);
        _ownPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_ownProgram, "lamp shadow own rays"));
        _sumHalfProgram = ctx.Shaders.Compute(LampShadowShaders.TraceLightHalf, "lamp shadow light half",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.AccelerationStructureKhr),
            (3, DescriptorType.StorageBuffer), (4, DescriptorType.UniformBuffer), (5, DescriptorType.StorageImage),
            (6, DescriptorType.CombinedImageSampler), (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler),
            (9, DescriptorType.StorageImage), (10, DescriptorType.StorageImage), (11, DescriptorType.CombinedImageSampler),
            (12, DescriptorType.StorageBuffer), (13, DescriptorType.StorageImage),
        ], GpuSetLayout.Bindless);
        _sumHalfPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_sumHalfProgram, "lamp shadow light half"));
        _classifyProgram = ctx.Shaders.Compute(LampShadowShaders.Classify, "lamp shadow cells",
            [
                (0, DescriptorType.UniformBuffer),
                (1, DescriptorType.StorageBuffer),
                (2, DescriptorType.StorageBuffer),
                (3, DescriptorType.StorageBuffer),
            ]);
        _classifyPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_classifyProgram, "lamp shadow cells"));
        _sampler = ctx.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Nearest, TextureMagFilter.Nearest));
    }

    /// <summary>
    /// Traces into <paramref name="lost"/>, then filters it by the lamps' size into <paramref name="filtered"/> (<see cref="PenumbraFilter"/>,
    /// through <paramref name="scratch"/>; <paramref name="inverse"/>: NDC, jittered as the frame was, to the position relative to the eye).
    /// <paramref name="scene"/> has this frame's top level.
    /// </summary>
    public void Run(GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture lost, GpuTexture total, GpuTexture total1,
        GpuTexture scratch, GpuTexture filtered, in LampShadowView view, in Matrix4x4 inverse)
    {
        float tileMode = lamps.Tiles is null ? TracedSunShaders.LampMask : TracedSunShaders.LampMovers;
        var parameters = TraceParams(lamps, view, new Vector4(scene.Origin, scene.SkinTable), new Vector4(view.Size, tileMode, 0));
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);

        var cmd = (CommandList)_ctx.BeginNative("lamp shadows");
        cmd.BeginLabel("lamp shadows");
        cmd.Barrier(BarrierBatch.Full);
        Push(cmd, scene, lamps, depth, surface, lost, total, total1, constants, _program, false);
        cmd.BindPipeline(_pipeline);
        cmd.BindBindlessTable(_program, 1, GpuBindPoint.Compute);
        cmd.Dispatch((uint)(((int)view.Size.X + 7) / 8), (uint)(((int)view.Size.Y + 7) / 8));
        cmd.Barrier(BarrierBatch.Full);

        var targets = new PenumbraTargets(depth, surface, lost, scratch, filtered, (int)view.Size.X, (int)view.Size.Y);
        _penumbra.Run(cmd, targets, inverse, view.Footprint, named: true);
        cmd.EndLabel();
        _ctx.EndNative(cmd);
    }

    /// <summary>
    /// The trace's constants for <paramref name="view"/>, with the given origin and size rows (<see cref="Params.Rect"/> zero).
    /// </summary>
    static Params TraceParams(WorldLamps lamps, in LampShadowView view, Vector4 origin, Vector4 size) => new()
    {
        LightGrid = lamps.Grid,
        Right = new Vector4(view.Right, view.Footprint),
        Up = new Vector4(view.Up, view.Phase),
        Back = new Vector4(view.Back, 0),
        Eye = new Vector4(view.Eye, 0),
        Origin = origin,
        PlanesTan = new Vector4(view.NearPlanes, view.Tan.X, view.Tan.Y),
        Size = size,
    };

    /// <summary>
    /// Pushes the trace's descriptors for <paramref name="to"/>: the targets, the lamps' textures, the scene and <paramref name="constants"/>;
    /// <paramref name="one"/>: a per-lamp program, which reads the tiles at 10 and the cell lists at 11.
    /// </summary>
    void Push(CommandList cmd, GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture lost, GpuTexture total,
        GpuTexture total1, Transient constants, GpuProgram to, bool one, GpuTexture? plain = null, GpuTexture? exact = null)
    {
        var (cells, index, data) = lamps.Current;
        var images = stackalloc DescriptorImageInfo[6];
        images[0] = Image(depth);
        images[1] = Image(surface);
        images[2] = Image(cells);
        images[3] = Image(index);
        images[4] = Image(data);
        images[5] = Image(lamps.TilesTexture);
        var storage = StorageImage(lost);
        var storageTotal = StorageImage(total);
        var storageTotal1 = StorageImage(total1);
        var top = scene.Top!.Handle;
        var asWrite = AccelerationWrite(&top);
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));

        var writes = stackalloc WriteDescriptorSet[Bindings + 2];
        writes[0] = Sampled(0, &images[0]);
        writes[1] = Sampled(1, &images[1]);
        writes[2] = Acceleration(2, &asWrite);
        writes[3] = Buffer(3, DescriptorType.StorageBuffer, &records);
        writes[4] = Buffer(4, DescriptorType.UniformBuffer, &uniform);
        writes[5] = Storage(5, &storage);
        for (int i = 0; i < 3; i++) writes[6 + i] = Sampled((uint)(6 + i), &images[2 + i]);
        writes[11] = Sampled(11, &images[5]);
        writes[9] = Storage(9, &storageTotal);
        writes[10] = Storage(10, &storageTotal1);

        if (one)
        {
            var listed = new DescriptorBufferInfo(_ctx.Device.BufferAt(_lists!.Handle.Index), 0, _lists.Size);
            writes[10] = Sampled(11, &images[5]);
            writes[11] = Buffer(12, DescriptorType.StorageBuffer, &listed);
            // The light kernel's second sum (binding 10, the characters' lamps' specular).
            var storagePlain = plain is { } second ? StorageImage(second) : default;
            int count = Bindings;
            if (plain is not null) writes[count++] = Storage(10, &storagePlain);
            // The half-size kernels' own rays (binding 13).
            var storageExact = exact is { } own ? StorageImage(own) : default;
            if (exact is not null) writes[count++] = Storage(13, &storageExact);
            cmd.PushDescriptors(to, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, count), GpuBindPoint.Compute);
            return;
        }

        cmd.PushDescriptors(to, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, Bindings), GpuBindPoint.Compute);
    }

    /// <summary>
    /// Clears every layer of <paramref name="target"/> (in the general layout) to <paramref name="red"/>, <paramref name="green"/>, 0 and
    /// <paramref name="alpha"/>.
    /// </summary>
    void Clear(CommandList cmd, GpuTexture target, float red, float green = 0, float alpha = 0)
    {
        var clear = new ClearColorValue(red, green, 0f, alpha);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, (uint)Math.Max(target.Desc.Layers, 1));
        _ctx.Device.Vk.CmdClearColorImage(cmd.Handle, _ctx.Device.NativeImage(target), ImageLayout.General, &clear, 1, &range);
    }

    /// <summary>
    /// <paramref name="t"/> sampled through <see cref="_sampler"/>.
    /// </summary>
    DescriptorImageInfo Image(GpuTexture t) => new(_ctx.Device.SamplerAt(_sampler.Index), _ctx.Device.ViewAt(t.View().Index), ImageLayout.General);

    /// <summary>
    /// <paramref name="t"/> as a storage image in the general layout.
    /// </summary>
    DescriptorImageInfo StorageImage(GpuTexture t) => new(default, _ctx.Device.ViewAt(t.View().Index), ImageLayout.General);

    /// <summary>
    /// The acceleration structure write for the top level at <paramref name="top"/>.
    /// </summary>
    static WriteDescriptorSetAccelerationStructureKHR AccelerationWrite(AccelerationStructureKHR* top) => new()
    {
        SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
        AccelerationStructureCount = 1,
        PAccelerationStructures = top,
    };

    /// <summary>
    /// A combined image sampler at <paramref name="binding"/>.
    /// </summary>
    static WriteDescriptorSet Sampled(uint binding, DescriptorImageInfo* image) => new()
    {
        SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler,
        PImageInfo = image,
    };

    /// <summary>
    /// A storage image at <paramref name="binding"/>.
    /// </summary>
    static WriteDescriptorSet Storage(uint binding, DescriptorImageInfo* image) => new()
    {
        SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage,
        PImageInfo = image,
    };

    /// <summary>
    /// The top level at <paramref name="binding"/>, through <paramref name="write"/>.
    /// </summary>
    static WriteDescriptorSet Acceleration(uint binding, WriteDescriptorSetAccelerationStructureKHR* write) => new()
    {
        SType = StructureType.WriteDescriptorSet, PNext = write, DstBinding = binding, DescriptorCount = 1,
        DescriptorType = DescriptorType.AccelerationStructureKhr,
    };

    /// <summary>
    /// A buffer of <paramref name="type"/> at <paramref name="binding"/>.
    /// </summary>
    static WriteDescriptorSet Buffer(uint binding, DescriptorType type, DescriptorBufferInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PBufferInfo = info,
    };

    /// <summary>
    /// Disposes the programs, the cell lists and the filter.
    /// </summary>
    public void Dispose()
    {
        _ctx.Shaders.Dispose(_program);
        _ctx.Shaders.Dispose(_oneProgram);
        if (_referenceProgram is not null) _ctx.Shaders.Dispose(_referenceProgram.Value);
        _ctx.Shaders.Dispose(_sumProgram);
        _ctx.Shaders.Dispose(_oneHalfProgram);
        _ctx.Shaders.Dispose(_ownProgram);
        _ctx.Shaders.Dispose(_sumHalfProgram);
        _ctx.Shaders.Dispose(_classifyProgram);
        _sliceLost?.Dispose();
        _sliceScratch?.Dispose();
        _sliceFiltered?.Dispose();
        _sliceExact?.Dispose();
        _lists?.Dispose();
        _penumbra.Dispose();
    }

    #endregion

    #region Reference view

    /// <summary>
    /// The debug reference trace, compiled the first time it is asked for.
    /// </summary>
    GpuProgram? _referenceProgram;

    /// <summary>
    /// The pipeline of <see cref="_referenceProgram"/>.
    /// </summary>
    GpuComputePipeline _referencePipeline;

    /// <summary>
    /// Debug (console <c>lamps refview</c>): one lamp's visibility of the still world into <paramref name="target"/>: the tile's
    /// (<paramref name="truth"/> false), or 256 rays to its disc.
    /// </summary>
    public void RunReference(GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture target, in LampShadowView view,
        int row, int truth)
    {
        EnsureReferenceProgram();

        float objects = truth == 3 ? TracedSunShaders.LampTileObjects : TracedSunShaders.LampTileObjects | TracedSunShaders.LampTileGround;
        var parameters = TraceParams(lamps, view, new Vector4(scene.Origin, truth), new Vector4(view.Size, objects, row));
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        var cmd = (CommandList)_ctx.BeginNative("lamp shadow reference");
        cmd.Barrier(BarrierBatch.Full);
        PushReference(cmd, scene, lamps, depth, surface, target, constants);
        cmd.BindPipeline(_referencePipeline);
        cmd.BindBindlessTable(_referenceProgram!.Value, 1, GpuBindPoint.Compute);
        cmd.Dispatch((uint)(((int)view.Size.X + 7) / 8), (uint)(((int)view.Size.Y + 7) / 8));
        cmd.Barrier(BarrierBatch.Full);
        _ctx.EndNative(cmd);
    }

    /// <summary>
    /// Compiles <see cref="_referenceProgram"/> and its pipeline if not yet done.
    /// </summary>
    void EnsureReferenceProgram()
    {
        if (_referenceProgram is not null) return;

        _referenceProgram = _ctx.Shaders.Compute(LampShadowShaders.TraceReference, "lamp shadow reference",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.AccelerationStructureKhr),
            (3, DescriptorType.StorageBuffer), (4, DescriptorType.UniformBuffer), (5, DescriptorType.StorageImage),
            (8, DescriptorType.CombinedImageSampler), (11, DescriptorType.CombinedImageSampler),
        ], GpuSetLayout.Bindless);
        _referencePipeline = _ctx.Pipelines.Get(new ComputePipelineDesc(_referenceProgram.Value, "lamp shadow reference"));
    }

    /// <summary>
    /// Pushes the reference trace's descriptors: the depth, the surface, the scene, <paramref name="constants"/>, <paramref name="target"/> and the
    /// lamps' data and tiles.
    /// </summary>
    void PushReference(CommandList cmd, GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture target, Transient constants)
    {
        var (_, _, data) = lamps.Current;
        var images = stackalloc DescriptorImageInfo[4];
        images[0] = Image(depth);
        images[1] = Image(surface);
        images[2] = Image(data);
        images[3] = Image(lamps.TilesTexture);
        var storage = StorageImage(target);
        var top = scene.Top!.Handle;
        var asWrite = AccelerationWrite(&top);
        var records = new DescriptorBufferInfo(_ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));

        var writes = stackalloc WriteDescriptorSet[8];
        writes[0] = Sampled(0, &images[0]);
        writes[1] = Sampled(1, &images[1]);
        writes[2] = Acceleration(2, &asWrite);
        writes[3] = Buffer(3, DescriptorType.StorageBuffer, &records);
        writes[4] = Buffer(4, DescriptorType.UniformBuffer, &uniform);
        writes[5] = Storage(5, &storage);
        writes[6] = Sampled(8, &images[2]);
        writes[7] = Sampled(11, &images[3]);
        cmd.PushDescriptors(_referenceProgram!.Value, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 8), GpuBindPoint.Compute);
    }

    #endregion

    #region Per-lamp passes

    /// <summary>
    /// The movers the classification tests one by one; with more of them the cells alone decide.
    /// </summary>
    const int ClassifyMovers = 256;

    /// <summary>
    /// The side in pixels of the cells a lamp's pass works on; a mask holds 2048, a screen wider than that takes all its cells.
    /// </summary>
    const int CellSize = 64, MaxCells = LampShadowShaders.MaxCells;

    /// <summary>
    /// How far under a mover's sphere the ground is taken to be, for how far its shadow reaches.
    /// </summary>
    const float GroundMargin = 60;

    /// <summary>
    /// The rounds' dispatch arguments, maps and lists (<see cref="LampShadowShaders.Classify"/>, laid out by <see cref="ListLayout"/>).
    /// </summary>
    DeviceBuffer? _lists;

    /// <summary>
    /// Where <see cref="_lists"/> holds what, in uints (<see cref="LampShadowShaders.Classify"/>), for <paramref name="Rounds"/> rounds of
    /// <paramref name="Slices"/> slices over <paramref name="Cells"/> cells a screen.
    /// </summary>
    readonly record struct ListLayout(int Rounds, int Slices, int Cells)
    {
        /// <summary>
        /// The cells a round has listed, a uint a cell.
        /// </summary>
        public int Seen => 16 * Rounds;

        /// <summary>
        /// The maps: per round, slice and cell the lamp's row plus one.
        /// </summary>
        public int Map => Seen + Rounds * Cells;

        /// <summary>
        /// The rounds' lists of rays; what comes before is zeroed each frame.
        /// </summary>
        public int Rays => Map + Rounds * Slices * Cells;

        /// <summary>
        /// The rounds' lists of cells.
        /// </summary>
        public int CellLists => Rays + Rounds * Slices * Cells;

        /// <summary>
        /// The list of the pixels the round at work traces on their own (<see cref="LampShadowShaders.OwnCap"/>).
        /// </summary>
        public int Own => CellLists + Rounds * Cells;

        /// <summary>
        /// The uints in all.
        /// </summary>
        public int Total => Own + LampShadowShaders.OwnCap;
    }

    /// <summary>
    /// This frame's lamps with a mover in reach: the lamp's row, its area in pixels, the cells its movers' shadows cover and those grown by one.
    /// </summary>
    readonly List<(int Row, Vector4 Area, uint[] Cells, uint[] Wide, uint[]? Movers, uint[]? Grass)> _areas = [];

    /// <summary>
    /// For each of <see cref="_areas"/>: the lamp stands among traced grass, so every cell in its reach is traced (<see cref="_grass"/>).
    /// </summary>
    readonly List<bool> _areaGrass = [];

    /// <summary>
    /// For each of <see cref="_areas"/>: its layer (<see cref="PlanLayers"/>).
    /// </summary>
    readonly List<int> _areaLayer = [];

    /// <summary>
    /// Per layer: the cells its lamps' passes can list, and those with their neighbours.
    /// </summary>
    readonly List<(uint[] Cells, uint[] Near)> _layers = [];

    /// <summary>
    /// The layers of the last frame, and the rounds they were worked in.
    /// </summary>
    public int LayersLast { get; private set; }

    /// <summary>
    /// See <see cref="LayersLast"/>.
    /// </summary>
    public int RoundsLast { get; private set; }

    /// <summary>
    /// The lamps' rays see the grass (the scene's traced grass, <c>PostOptions.TracedGrass</c>), as far from the eye as it is traced.
    /// </summary>
    bool _grass;

    /// <summary>
    /// How far from the eye a lamp still traces the grass in its reach.
    /// </summary>
    const float GrassLampRange = 500;
    float _grassReach;

    /// <summary>
    /// The cell masks handed out to <see cref="_areas"/>, reused from frame to frame.
    /// </summary>
    readonly List<uint[]> _cellPool = [];

    /// <summary>
    /// The masks of <see cref="_cellPool"/> handed out this frame.
    /// </summary>
    int _cellsUsed;

    /// <summary>
    /// The words of a mask this frame's screen uses (all of them past <see cref="MaxCells"/> cells, where every bit is set).
    /// </summary>
    int _cellWords = MaxCells / 32;

    /// <summary>
    /// This frame's movers (spheres), from <see cref="GiScene.CollectMovers"/>.
    /// </summary>
    readonly List<(Vector3 Centre, float Radius)> _movers = [];

    /// <summary>
    /// The last frame's per-lamp areas.
    /// </summary>
    public int AreasLast { get; private set; }

    /// <summary>
    /// The pixels of the last frame's per-lamp areas' rectangles, summed.
    /// </summary>
    public long AreaPixelsLast { get; private set; }

    /// <summary>
    /// The pixels of the cells the last frame's areas marked (the rays' wider set), summed.
    /// </summary>
    public long CellPixelsLast { get; private set; }

    /// <summary>
    /// The movers (spheres) of the last frame.
    /// </summary>
    public int MoversLast { get; private set; }

    /// <summary>
    /// The classification's constants (<see cref="LampShadowShaders.Classify"/>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct ClassifyParams
    {
        /// <summary>
        /// The lamp relative to the eye and its reach, the pyramid's camera, and its tangents and size.
        /// </summary>
        public Vector4 Lamp, Right, Up, Back, TanSize;

        /// <summary>
        /// The cells to classify: first column and row, then the end ones.
        /// </summary>
        public fixed int CellRect[4];

        /// <summary>
        /// The lamp's slot, its movers (-1: too many), the pyramid's level offset and its blocks across.
        /// </summary>
        public fixed int Info[4];

        /// <summary>
        /// The lamp's slice in its round, 1 among traced grass, the rounds, the lamp's row.
        /// </summary>
        public fixed int Base[4];

        /// <summary>
        /// The slices a round.
        /// </summary>
        public fixed int Lay[4];

        /// <summary>
        /// The cells the lamp's movers' shadows cover, one bit each.
        /// </summary>
        public fixed uint Cells[MaxCells / 32];

        /// <summary>
        /// The cells grown by one, one bit each.
        /// </summary>
        public fixed uint Wide[MaxCells / 32];

        /// <summary>
        /// The movers in the lamp's reach: centre relative to the eye, radius.
        /// </summary>
        public fixed float Movers[ClassifyMovers * 4];

        public Vector4 GrassMap, GrassLift;

        public fixed uint GrassCells[MaxCells / 32];
    }

    /// <summary>
    /// The rays of what moves, each lamp over the part of the screen its shadows can reach: traced, filtered and added to
    /// <paramref name="sum"/>, the light lost per unit of albedo, whatever the number of lamps. <paramref name="filtered"/> ends as 1 everywhere,
    /// the factor the drawing pass reads. False: nothing to do, <paramref name="sum"/> is untouched. <paramref name="light"/> takes a character's
    /// lamps' specular (rgb what is lost of it, a the luminance of all of it): the apply takes that back.
    /// The lamps are dealt into layers (<see cref="PlanLayers"/>) and the layers into rounds: a round's rays, filter and light are one dispatch
    /// each for all its lamps, a layer a slice. A lamp at a time cost its passes' start, wait and end a lamp (4 dispatches, each waited for):
    /// 3 ms for a town's 50 to 70 lamps, whose cells together cover a few screens.
    /// </summary>
    public bool RunPerLamp(GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture light, GpuTexture sum,
        GpuTexture filtered, in LampShadowView view, in Matrix4x4 inverse, in Meitou.Game.Rendering.Frame.OcclusionView pyramid = default)
    {
        (_grass, _grassReach) = (scene.GrassShadows, scene.GrassReach);
        _grassScene = scene;
        _grassMap = scene.GrassMap;
        PlanAreas(scene, lamps, view, inverse);
        PlanLayers(view.Size);
        CountAreas();
        if (_areas.Count == 0) return false;

        int w = (int)view.Size.X, h = (int)view.Size.Y;
        // Which pixel of each 2 x 2 the half-size rays take: the same, or each in turn over four frames while a temporal resolve averages them
        // (the view's phase moves only then).
        int half = HalfMode, turn = half == 2 && view.Phase > 0 ? (int)(_ctx.Frame.Number & 3) : 0;
        var sub = new Vector2(turn & 1, (turn >> 1) ^ (turn & 1));
        int slices = half != 0 ? LampShadowShaders.MaxSlices : 1;
        var layout = new ListLayout((_layers.Count + slices - 1) / slices, slices, ((w + CellSize - 1) / CellSize) * ((h + CellSize - 1) / CellSize));
        RoundsLast = layout.Rounds;
        EnsureLists(layout);
        EnsureSlices(w, h, half != 0);
        ulong listsSize = _lists.Size;
        var cmd = (CommandList)_ctx.BeginNative("lamp shadows");
        cmd.BeginLabel("lamp shadows");
        cmd.Barrier(BarrierBatch.Full);
        Clear(cmd, sum, 0);
        Clear(cmd, light, 0);
        cmd.FillBuffer(_lists.Handle, 0, (ulong)layout.Rays * 4, 0);
        cmd.Barrier(BarrierBatch.Full);

        // Each lamp's cells: those its movers' shadow boxes cover, less what this frame's depth shows out of the lamp's reach or of every mover's
        // shadow; into its round's lists and map.
        Classify(cmd, lamps, view, pyramid, layout);
        cmd.Barrier(BarrierBatch.Full);

        var targets = new PenumbraTargets(depth, surface, _sliceLost!, _sliceScratch!, _sliceFiltered!, w, h);
        for (int round = 0; round < layout.Rounds; round++)
            TraceRound(cmd, scene, lamps, depth, surface, light, sum, targets, view, inverse, round, layout, listsSize, half != 0, sub);

        Clear(cmd, filtered, 1);
        cmd.Barrier(BarrierBatch.Full);
        cmd.EndLabel();
        _ctx.EndNative(cmd);
        return true;
    }

    /// <summary>
    /// Records the last frame's area statistics from <see cref="_areas"/> and <see cref="_movers"/>.
    /// </summary>
    void CountAreas()
    {
        (AreasLast, AreaPixelsLast, MoversLast) = (_areas.Count, 0, _movers.Count);
        CellPixelsLast = 0;
        foreach (var planned in _areas)
        {
            AreaPixelsLast += (long)((planned.Area.Z - planned.Area.X) * (planned.Area.W - planned.Area.Y));
            foreach (uint word in planned.Wide) CellPixelsLast += System.Numerics.BitOperations.PopCount(word) * (long)(CellSize * CellSize);
        }
    }

    /// <summary>
    /// Makes <see cref="_lists"/> again, twice as large as <paramref name="layout"/> asks, when it is missing or too small.
    /// </summary>
    [MemberNotNull(nameof(_lists))]
    void EnsureLists(in ListLayout layout)
    {
        ulong bytes = (ulong)layout.Total * 4;
        if (_lists is not null && _lists.Size >= bytes) return;

        _lists?.Dispose();   // released after the frames in flight
        var use = BufferUse.Storage | BufferUse.Indirect | BufferUse.TransferDst;
        _lists = DeviceBuffer.Create(_ctx, bytes * 2, use, "lamp shadow cell lists");
    }

    /// <summary>
    /// Makes the rays' slices for a render size of <paramref name="w"/> x <paramref name="h"/> when they are missing or of another size:
    /// <see cref="LampShadowShaders.MaxSlices"/> of half the size when <paramref name="half"/>, else one of the size.
    /// </summary>
    void EnsureSlices(int w, int h, bool half)
    {
        int sw = half ? (w + 1) / 2 : w, sh = half ? (h + 1) / 2 : h, layers = half ? LampShadowShaders.MaxSlices : 1;
        if (_sliceLost is not null && _sliceLost.Desc.Width == sw && _sliceLost.Desc.Height == sh && _sliceLost.Desc.Layers == layers) return;

        _sliceLost?.Dispose();   // released after the frames in flight
        _sliceScratch?.Dispose();
        _sliceFiltered?.Dispose();
        _sliceExact?.Dispose();
        _sliceExact = null;
        var use = TextureUse.Storage | TextureUse.Sampled | TextureUse.TransferDst;
        GpuTexture Make(string name) =>
            _ctx.CreateTexture(new TextureDesc(InternalFormat.Rg16F, sw, sh, Layers: layers, Kind: TextureKind.Texture2DArray, Use: use, Name: name));
        (_sliceLost, _sliceScratch, _sliceFiltered) = (Make("lamp shadows flags"), Make("lamp shadows scratch"), Make("lamp shadows filtered"));
        if (half)
            _sliceExact = _ctx.CreateTexture(new TextureDesc(InternalFormat.R32Ui, sw, sh, Layers: layers, Kind: TextureKind.Texture2DArray, Use: use,
                Name: "lamp shadows own rays"));
    }

    /// <summary>
    /// The lamps of <paramref name="round"/> at once, each over its own cells in its layer's slice: their rays, the filter on their flags, then,
    /// a pixel a thread, their light into <paramref name="sum"/> where the filtered flags are. <paramref name="half"/>: the rays and the filter
    /// at half the size (<paramref name="targets"/>' slices, with the render size), a quarter of the rays and of the filter's pixels; the light
    /// kernel, at the render size, takes each pixel's flag from those round it on its own surface.
    /// </summary>
    void TraceRound(CommandList cmd, GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture light, GpuTexture sum,
        in PenumbraTargets targets, in LampShadowView view, in Matrix4x4 inverse, int round, in ListLayout layout, ulong listsSize, bool half,
        Vector2 sub)
    {
        int rays = layout.Rays + round * layout.Slices * layout.Cells, cells = layout.CellLists + round * layout.Cells;
        int map = layout.Map + round * layout.Slices * layout.Cells;
        var size = new Vector4(view.Size, TracedSunShaders.LampMovers | (_grass ? TracedSunShaders.Grass : 0), 0);
        var parameters = TraceParams(lamps, view, new Vector4(scene.Origin, scene.SkinTable), size);
        (parameters.Cells[0], parameters.Cells[1]) = ((uint)layout.Slices, (uint)layout.Cells);
        (parameters.Cells[2], parameters.Cells[3]) = ((uint)(12 * layout.Rounds + 4 * round), (uint)layout.Own);
        parameters.Rect = new Vector4(rays, map, sub.X, sub.Y);
        var raysConstants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        parameters.Rect = new Vector4(cells, map, sub.X, sub.Y);
        var lightConstants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        ulong raysArgs = (ulong)((half ? 4 * layout.Rounds : 0) + 4 * round) * 4, cellsArgs = (ulong)(8 * layout.Rounds + 4 * round) * 4;

        // Outside a lamp's cells its slice holds no receiver (green -1), not what the last round left there: the filter reads round a pixel, and
        // took another lamp's shadow in at a cell's edge (darker 64-pixel blocks beside a shadow). At half the size the result too: the light
        // kernel reads the flags round a pixel, past its cell's edge.
        Clear(cmd, targets.Raw, 0, -1);
        Clear(cmd, targets.Scratch, 0, -1);
        if (half) Clear(cmd, targets.Filtered, 0, -1);
        cmd.Barrier(BarrierBatch.Full);

        // The rays over the round's list, the filter on their flags, then the lamps' light where the filtered flags are.
        var (raysProgram, raysPipeline) = half ? (_oneHalfProgram, _oneHalfPipeline) : (_oneProgram, _onePipeline);
        Push(cmd, scene, lamps, depth, surface, targets.Raw, light, light, raysConstants, raysProgram, true, exact: half ? _sliceExact : null);
        cmd.BindPipeline(raysPipeline);
        cmd.BindBindlessTable(raysProgram, 1, GpuBindPoint.Compute);
        cmd.DispatchIndirect(_lists!.Handle, raysArgs);
        cmd.Barrier(BarrierBatch.Full);
        if (half)
        {
            // The pixels the rays listed for a ray of their own, a thread each.
            Push(cmd, scene, lamps, depth, surface, targets.Raw, light, light, raysConstants, _ownProgram, true, exact: _sliceExact);
            cmd.BindPipeline(_ownPipeline);
            cmd.BindBindlessTable(_ownProgram, 1, GpuBindPoint.Compute);
            cmd.DispatchIndirect(_lists!.Handle, (ulong)(12 * layout.Rounds + 4 * round) * 4);
            cmd.Barrier(BarrierBatch.Full);
        }
        _penumbra.RunList(cmd, targets, inverse, view.Footprint, _lists!.Handle, listsSize, raysArgs, rays, half, sub);
        var (lightProgram, lightPipeline) = half ? (_sumHalfProgram, _sumHalfPipeline) : (_sumProgram, _sumPipeline);
        Push(cmd, scene, lamps, depth, surface, targets.Filtered, sum, sum, lightConstants, lightProgram, true, light, half ? _sliceExact : null);
        cmd.BindPipeline(lightPipeline);
        cmd.BindBindlessTable(lightProgram, 1, GpuBindPoint.Compute);
        cmd.DispatchIndirect(_lists!.Handle, cellsArgs);
        cmd.Barrier(BarrierBatch.Full);
    }

    /// <summary>
    /// Deals <see cref="_areas"/> into layers (<see cref="_areaLayer"/>, <see cref="_layers"/>): a lamp joins the first layer none of whose
    /// lamps' cells is one of its own or next to one, so the passes of a layer never read one lamp's flags for another (the filter reads within
    /// a cell's width of a pixel).
    /// </summary>
    void PlanLayers(Vector2 size)
    {
        _areaLayer.Clear();
        _layers.Clear();
        int words = Math.Min(((((int)size.X + CellSize - 1) / CellSize) * (((int)size.Y + CellSize - 1) / CellSize) + 31) / 32, MaxCells / 32);
        foreach (var area in _areas)
        {
            uint[] wide = area.Wide, near = Grown(wide, size);
            int layer = 0;
            for (; layer < _layers.Count; layer++)
            {
                uint[] taken = _layers[layer].Near;
                bool free = true;
                for (int i = 0; i < words && free; i++) free = (wide[i] & taken[i]) == 0;
                if (free) break;
            }

            if (layer == _layers.Count) _layers.Add((NextCells(), NextCells()));
            var (cells, around) = _layers[layer];
            for (int i = 0; i < words; i++)
            {
                cells[i] |= wide[i];
                around[i] |= near[i];
            }

            _areaLayer.Add(layer);
        }

        LayersLast = _layers.Count;
    }

    /// <summary>
    /// The cells of every lamp into its round's lists and map (<see cref="LampShadowShaders.Classify"/>), a dispatch a lamp over its area's cells.
    /// </summary>
    void Classify(CommandList cmd, WorldLamps lamps, in LampShadowView view, in Meitou.Game.Rendering.Frame.OcclusionView pyramid,
        in ListLayout layout)
    {
        // The pyramid's level of 64-pixel blocks (level 0 holds 4 x 4 pixels): its vectors as HizPyramid.Vectors lays them out.
        bool has = !pyramid.IsEmpty && pyramid.Vectors[5].Y > 4.5f;
        var eye = has ? new Vector3(pyramid.Vectors[0].X, pyramid.Vectors[0].Y, pyramid.Vectors[0].Z) : view.Eye;
        var hiz = has
            ? new DescriptorBufferInfo(_ctx.Device.BufferAt(pyramid.Buffer.Buffer.Index), pyramid.Buffer.Offset, pyramid.Buffer.Size)
            : new DescriptorBufferInfo(_ctx.Device.BufferAt(_lists!.Handle.Index), 0, _lists.Size);   // a stand-in, not read
        var listed = new DescriptorBufferInfo(_ctx.Device.BufferAt(_lists!.Handle.Index), 0, _lists.Size);
        var grassMap = _grassMap is { } map
            ? new DescriptorBufferInfo(_ctx.Device.BufferAt(map.Buffer.Index), 0, map.Buffer.Size)
            : listed;   // a stand-in, not read
        int wide = ((int)view.Size.X + CellSize - 1) / CellSize, high = ((int)view.Size.Y + CellSize - 1) / CellSize;
        cmd.BindPipeline(_classifyPipeline);
        var writes = stackalloc WriteDescriptorSet[4];
        for (int slot = 0; slot < _areas.Count; slot++)
        {
            ClassifyParams parameters;
            FillClassifyParams(out parameters, slot, lamps, view, pyramid, has, eye, wide, high, layout);
            var constants = _ctx.Frame.Constants.Write<ClassifyParams>(new ReadOnlySpan<ClassifyParams>(&parameters, 1), 256);
            var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(ClassifyParams));
            writes[0] = Buffer(0, DescriptorType.UniformBuffer, &uniform);
            writes[1] = Buffer(1, DescriptorType.StorageBuffer, &hiz);
            writes[2] = Buffer(2, DescriptorType.StorageBuffer, &listed);
            writes[3] = Buffer(3, DescriptorType.StorageBuffer, &grassMap);
            cmd.PushDescriptors(_classifyProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 4), GpuBindPoint.Compute);
            int x0 = parameters.CellRect[0], y0 = parameters.CellRect[1], x1 = parameters.CellRect[2], y1 = parameters.CellRect[3];
            cmd.Dispatch((uint)((x1 - x0 + 7) / 8), (uint)((y1 - y0 + 7) / 8));
        }
    }

    /// <summary>
    /// The classification's constants for the area of <paramref name="slot"/>: its lamp, the pyramid's camera (<paramref name="has"/>), its cells
    /// widened by the filter's margins, and the movers in the lamp's reach.
    /// </summary>
    void FillClassifyParams(out ClassifyParams parameters, int slot, WorldLamps lamps, in LampShadowView view,
        in Meitou.Game.Rendering.Frame.OcclusionView pyramid, bool has, Vector3 eye, int wide, int high, in ListLayout layout)
    {
        var (row, area, _, grown, movers, grass) = _areas[slot];
        var l = lamps.RowLamp(row);
        int margin = PenumbraFilter.RowMargin + PenumbraFilter.ColumnMargin;
        int x0 = Math.Max((int)(area.X - margin) / CellSize, 0), y0 = Math.Max((int)(area.Y - margin) / CellSize, 0);
        int x1 = Math.Min((int)(area.Z + margin) / CellSize + 1, wide), y1 = Math.Min((int)(area.W + margin) / CellSize + 1, high);
        parameters = new ClassifyParams
        {
            Lamp = new Vector4(l.Position - eye, l.Radius),
            Right = has ? pyramid.Vectors[1] : default,
            Up = has ? pyramid.Vectors[2] : default,
            Back = has ? pyramid.Vectors[3] : default,
            TanSize = has ? pyramid.Vectors[4] : new Vector4(0, 0, view.Size.X, view.Size.Y),
        };
        (parameters.CellRect[0], parameters.CellRect[1], parameters.CellRect[2], parameters.CellRect[3]) = (x0, y0, x1, y1);
        parameters.Info[0] = _areaLayer[slot] / layout.Slices;

        int near = GatherNearMovers(ref parameters, l, eye);
        parameters.Info[1] = near <= ClassifyMovers ? near : -1;
        parameters.Info[2] = has ? (int)BitConverter.SingleToUInt32Bits(pyramid.Vectors[7].X) : 0;
        parameters.Info[3] = has ? ((int)pyramid.Vectors[5].Z + 15) >> 4 : 0;
        parameters.Base[0] = _areaLayer[slot] % layout.Slices;
        parameters.Base[1] = _areaGrass[slot] ? 1 : 0;
        if (_areaGrass[slot] && _grassMap is { } map)
        {
            parameters.GrassMap = new Vector4(map.Origin.X - eye.X, map.Origin.Y - eye.Z, map.Cell, map.Side);
            parameters.GrassLift = new Vector4(eye.Y, map.Offset, GrassMargin, TracedGrass.MapLevels);
        }
        parameters.Base[2] = layout.Rounds;
        parameters.Base[3] = row;
        parameters.Lay[0] = layout.Slices;
        for (int i = 0; i < MaxCells / 32; i++)
            (parameters.Cells[i], parameters.Wide[i], parameters.GrassCells[i]) = (movers?[i] ?? 0, grown[i], grass?[i] ?? 0);
    }

    /// <summary>
    /// The frame's traced scene, asked where its grass is (<see cref="GiScene.GrassWithin"/>).
    /// </summary>
    GiScene? _grassScene;

    /// <summary>
    /// The frame's traced blades' map (<see cref="TracedGrass.Map"/>): a lamp among grass asks only the cells where a blade can stand between it
    /// and what they show (<c>grassMayShade</c> in <see cref="LampShadowShaders.Classify"/>); null: every cell in its reach.
    /// </summary>
    TracedGrass.MapView? _grassMap;

    /// <summary>
    /// World units round the receivers the map is asked over beyond each cell's sphere: where a lamp's ray leaves from (off the receiver's plane by
    /// a pixel or two, a strand of hair a little further).
    /// </summary>
    const float GrassMargin = 2;

    /// <summary>
    /// The movers within <paramref name="l"/>'s reach, the only ones whose shadow it casts, into <paramref name="parameters"/> up to
    /// <see cref="ClassifyMovers"/>; returns how many there are (too many: the cells alone decide).
    /// </summary>
    int GatherNearMovers(ref ClassifyParams parameters, WorldLight l, Vector3 eye)
    {
        int near = 0;
        foreach (var (centre, radius) in _movers)
        {
            if (Vector3.Distance(centre, l.Position) > l.Radius + radius) continue;

            if (near < ClassifyMovers)
            {
                var at = centre - eye;
                int k = near * 4;
                (parameters.Movers[k], parameters.Movers[k + 1], parameters.Movers[k + 2], parameters.Movers[k + 3]) = (at.X, at.Y, at.Z, radius);
            }

            near++;
        }

        return near;
    }

    /// <summary>
    /// For each lamp something moving stands in, the part of the screen its shadow can fall on.
    /// </summary>
    void PlanAreas(GiScene scene, WorldLamps lamps, in LampShadowView view, in Matrix4x4 inverse)
    {
        _areas.Clear();
        _areaGrass.Clear();
        _movers.Clear();
        _cellsUsed = 0;
        int screenCells = (((int)view.Size.X + CellSize - 1) / CellSize) * (((int)view.Size.Y + CellSize - 1) / CellSize);
        _cellWords = screenCells > MaxCells ? MaxCells / 32 : (screenCells + 31) / 32;
        scene.CollectMovers(_movers);
        if ((_movers.Count == 0 && !_grass) || !Matrix4x4.Invert(inverse, out var toClip)) return;

        for (int row = 0; row < lamps.InGrid; row++)
            PlanLampArea(row, lamps.RowLamp(row), view, toClip);
    }

    /// <summary>
    /// Adds the area of the lamp at <paramref name="row"/> to <see cref="_areas"/> if a mover's shadow from it falls on the screen.
    /// </summary>
    void PlanLampArea(int row, WorldLight l, in LampShadowView view, in Matrix4x4 toClip)
    {
        Vector4 box = new(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        // The mask is taken with the first mover in reach: most lamps of a town have none, and a mask a lamp of the grid was 0.25 ms a frame.
        // The movers' cells and the grass's are kept apart: the classification asks the movers only in theirs (the shadow boxes Cover finds) and the
        // grass only in its own; asked over the grass's whole reach, the movers' test kept 2.5 times the cells in a lit hall.
        uint[]? cells = null, grass = null;
        foreach (var (centre, radius) in _movers)
        {
            // Cover's own first test, before its call: hundreds of lamps by a hundred movers.
            float reach = l.Radius + radius;
            if (Vector3.DistanceSquared(centre, l.Position) > reach * reach) continue;

            // Each mover apart: one whose shadow stays off the screen must not stretch the lamp's area across it.
            Vector4 own = new(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            if (!Cover(l, centre, radius, view, toClip, ref own) || OffScreen(own, view.Size)) continue;

            box = new Vector4(MathF.Min(box.X, own.X), MathF.Min(box.Y, own.Y), MathF.Max(box.Z, own.Z), MathF.Max(box.W, own.W));
            MarkCells(cells ??= NextCells(), own, view.Size);
        }

        // Among traced grass the lamp's whole reach is asked: a blade anywhere in it can shade the ground.
        bool grassLamp = false;
        // Only near the eye: a blade's shadow from a lamp is under a pixel wide past GrassLampRange, and every such lamp costs its own passes.
        // And only with a traced page that holds blades in the lamp's reach: with no grass about, every lamp round the eye asked every pixel it
        // reaches for nothing.
        if (_grass && Vector3.Distance(l.Position, view.Eye) < MathF.Min(_grassReach, GrassLampRange) + l.Radius
            && (_grassScene is not { } traced || traced.GrassWithin(l.Position, l.Radius)))
        {
            Vector4 own = new(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            Grow(l.Position, l.Radius, view, toClip, ref own);
            if (!OffScreen(own, view.Size))
            {
                box = new Vector4(MathF.Min(box.X, own.X), MathF.Min(box.Y, own.Y), MathF.Max(box.Z, own.Z), MathF.Max(box.W, own.W));
                MarkCells(grass = NextCells(), own, view.Size);
                grassLamp = true;
            }
        }

        if (cells is null && grass is null) return;
        var movers = cells;
        if (grass is not null)
        {
            // Both: the cells the area plans with are their union.
            if (movers is null) cells = grass;
            else
            {
                cells = NextCells();
                for (int i = 0; i < _cellWords; i++) cells[i] = movers[i] | grass[i];
            }
        }

        var area = new Vector4(
            MathF.Floor(MathF.Max(box.X, 0)),
            MathF.Floor(MathF.Max(box.Y, 0)),
            MathF.Ceiling(MathF.Min(box.Z, view.Size.X)),
            MathF.Ceiling(MathF.Min(box.W, view.Size.Y)));
        if (area.Z <= area.X || area.W <= area.Y) return;   // the masks taken stay unused until the next frame

        _areas.Add((row, area, cells!, Grown(cells!, view.Size), movers, grass));
        _areaGrass.Add(grassLamp);
    }

    /// <summary>
    /// Whether the pixel box <paramref name="own"/> lies wholly off a screen of <paramref name="size"/>.
    /// </summary>
    static bool OffScreen(Vector4 own, Vector2 size) => own.Z <= 0 || own.W <= 0 || own.X >= size.X || own.Y >= size.Y;

    /// <summary>
    /// The next cleared mask of <see cref="_cellPool"/>, made when the pool runs out.
    /// </summary>
    uint[] NextCells()
    {
        if (_cellsUsed == _cellPool.Count) _cellPool.Add(new uint[MaxCells / 32]);

        var cells = _cellPool[_cellsUsed++];
        Array.Clear(cells, 0, _cellWords);   // the screen's cells alone: the rest is never read
        return cells;
    }

    /// <summary>
    /// Marks the cells the pixel box <paramref name="box"/> touches; all of them on a screen of more than <see cref="MaxCells"/> cells.
    /// </summary>
    static void MarkCells(uint[] cells, Vector4 box, Vector2 size)
    {
        int wide = ((int)size.X + CellSize - 1) / CellSize, high = ((int)size.Y + CellSize - 1) / CellSize;
        if (wide * high > MaxCells)
        {
            Array.Fill(cells, uint.MaxValue);
            return;
        }

        int x0 = Math.Clamp((int)box.X / CellSize, 0, wide - 1), x1 = Math.Clamp((int)box.Z / CellSize, 0, wide - 1);
        int y0 = Math.Clamp((int)box.Y / CellSize, 0, high - 1), y1 = Math.Clamp((int)box.W / CellSize, 0, high - 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                cells[(y * wide + x) / 32] |= 1u << ((y * wide + x) % 32);
    }

    /// <summary>
    /// The cells and their neighbours: what the rays must have done round a filtered cell.
    /// </summary>
    uint[] Grown(uint[] cells, Vector2 size)
    {
        var grown = NextCells();
        int wide = ((int)size.X + CellSize - 1) / CellSize, high = ((int)size.Y + CellSize - 1) / CellSize;
        if (wide * high > MaxCells)
        {
            Array.Fill(grown, uint.MaxValue);
            return grown;
        }

        for (int y = 0; y < high; y++)
            for (int x = 0; x < wide; x++)
            {
                if ((cells[(y * wide + x) / 32] >> ((y * wide + x) % 32) & 1) == 0) continue;

                for (int ny = Math.Max(y - 1, 0); ny <= Math.Min(y + 1, high - 1); ny++)
                    for (int nx = Math.Max(x - 1, 0); nx <= Math.Min(x + 1, wide - 1); nx++)
                        grown[(ny * wide + nx) / 32] |= 1u << ((ny * wide + nx) % 32);
            }

        return grown;
    }

    /// <summary>
    /// Grows <paramref name="box"/> (pixels) by where a sphere's shadow from <paramref name="l"/> can fall: the sphere and its shadow's end on the
    /// ground under it, or the lamp's reach. False: the lamp cannot reach it.
    /// </summary>
    static bool Cover(WorldLight l, Vector3 centre, float radius, in LampShadowView view, in Matrix4x4 toClip, ref Vector4 box)
    {
        var v = centre - l.Position;
        float dist = v.Length();
        if (dist > l.Radius + radius) return false;

        if (dist > radius + 1 && l.Type == WorldLightType.Spot)
        {
            float half = MathF.Max(l.InnerAngle, l.OuterAngle) * 0.5f;
            float outside = MathF.Acos(Math.Clamp(Vector3.Dot(v, l.Direction) / dist, -1, 1)) - MathF.Asin(Math.Min(radius / dist, 1));
            if (outside > half * 1.04f) return false;
        }

        var dir = dist > 1e-3f ? v / dist : Vector3.UnitY * -1;
        float along = MathF.Max(l.Radius - dist + radius, 0);
        if (dir.Y < -1e-3f) along = MathF.Min(along, (centre.Y - radius - GroundMargin - centre.Y) / dir.Y);

        Grow(centre, radius, view, toClip, ref box);
        float scale = dist > radius ? (dist + along) / dist : 1;
        Grow(centre + dir * along, radius * scale, view, toClip, ref box);
        return true;
    }

    /// <summary>
    /// Grows <paramref name="box"/> (pixels) by a sphere's projection, plus 2 pixels; the whole screen when the sphere reaches the eye's plane.
    /// </summary>
    static void Grow(Vector3 at, float radius, in LampShadowView view, in Matrix4x4 toClip, ref Vector4 box)
    {
        var clip = Vector4.Transform(new Vector4(at - view.Eye, 1), toClip);
        if (clip.W <= radius + 1)
        {
            box = new Vector4(0, 0, view.Size.X, view.Size.Y);
            return;
        }

        float x = (clip.X / clip.W + 1) * 0.5f * view.Size.X, y = (clip.Y / clip.W + 1) * 0.5f * view.Size.Y;
        float rx = radius / (view.Tan.X * clip.W) * 0.5f * view.Size.X + 2, ry = radius / (view.Tan.Y * clip.W) * 0.5f * view.Size.Y + 2;
        box = new Vector4(MathF.Min(box.X, x - rx), MathF.Min(box.Y, y - ry), MathF.Max(box.Z, x + rx), MathF.Max(box.W, y + ry));
    }

    #endregion
}
