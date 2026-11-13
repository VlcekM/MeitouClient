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
            (9, DescriptorType.StorageImage), (11, DescriptorType.CombinedImageSampler), (12, DescriptorType.StorageBuffer),
        ], GpuSetLayout.Bindless);
        _sumPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(_sumProgram, "lamp shadow light"));
        _classifyProgram = ctx.Shaders.Compute(LampShadowShaders.Classify, "lamp shadow cells",
            [(0, DescriptorType.UniformBuffer), (1, DescriptorType.StorageBuffer), (2, DescriptorType.StorageBuffer)]);
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
        GpuTexture total1, Transient constants, GpuProgram to, bool one)
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

        var writes = stackalloc WriteDescriptorSet[Bindings];
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
            cmd.PushDescriptors(to, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, Bindings), GpuBindPoint.Compute);
            return;
        }

        cmd.PushDescriptors(to, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, Bindings), GpuBindPoint.Compute);
    }

    /// <summary>
    /// Clears <paramref name="target"/> (in the general layout) to <paramref name="red"/> in its first channel.
    /// </summary>
    void Clear(CommandList cmd, GpuTexture target, float red)
    {
        var clear = new ClearColorValue(red, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
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
        _ctx.Shaders.Dispose(_classifyProgram);
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
    const int ClassifyMovers = 64;

    /// <summary>
    /// The side in pixels of the cells a lamp's pass works on; a mask holds 2048, a screen wider than that takes all its cells.
    /// </summary>
    const int CellSize = 64, MaxCells = 2048;

    /// <summary>
    /// How far under a mover's sphere the ground is taken to be, for how far its shadow reaches.
    /// </summary>
    const float GroundMargin = 60;

    /// <summary>
    /// The lamps' lists of cells (<see cref="LampShadowShaders.Classify"/>): the indirect dispatches' arguments, 16 bytes a list, then
    /// <see cref="MaxCells"/> cells a list.
    /// </summary>
    DeviceBuffer? _lists;

    /// <summary>
    /// The lists <see cref="_lists"/> has room for.
    /// </summary>
    int _listCapacity;

    /// <summary>
    /// This frame's lamps with a mover in reach: the lamp's row, its area in pixels, the cells its movers' shadows cover and those grown by one.
    /// </summary>
    readonly List<(int Row, Vector4 Area, uint[] Cells, uint[] Wide)> _areas = [];

    /// <summary>
    /// The cell masks handed out to <see cref="_areas"/>, reused from frame to frame.
    /// </summary>
    readonly List<uint[]> _cellPool = [];

    /// <summary>
    /// The masks of <see cref="_cellPool"/> handed out this frame.
    /// </summary>
    int _cellsUsed;

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
        /// Where the lists start, in uints.
        /// </summary>
        public fixed int Base[4];

        /// <summary>
        /// The cells the lamp's movers' shadows cover, one bit each.
        /// </summary>
        public fixed uint Cells[64];

        /// <summary>
        /// The cells grown by one, one bit each.
        /// </summary>
        public fixed uint Wide[64];

        /// <summary>
        /// The movers in the lamp's reach: centre relative to the eye, radius.
        /// </summary>
        public fixed float Movers[ClassifyMovers * 4];
    }

    /// <summary>
    /// The rays of what moves, one lamp at a time over the part of the screen its shadows can reach: traced, filtered and added to
    /// <paramref name="sum"/>, the light lost per unit of albedo, whatever the number of lamps. <paramref name="filtered"/> ends as 1 everywhere,
    /// the factor the drawing pass reads. False: nothing to do, <paramref name="sum"/> is untouched.
    /// </summary>
    public bool RunPerLamp(GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture lost, GpuTexture light, GpuTexture sum,
        GpuTexture scratch, GpuTexture filtered, in LampShadowView view, in Matrix4x4 inverse,
        in Meitou.Game.Rendering.Frame.OcclusionView pyramid = default)
    {
        PlanAreas(scene, lamps, view, inverse);
        CountAreas();
        if (_areas.Count == 0) return false;

        int w = (int)view.Size.X, h = (int)view.Size.Y;
        EnsureLists();
        int listBase = _listCapacity * 4;   // in uints: the lists come after the arguments
        ulong listsSize = _lists.Size;
        var cmd = (CommandList)_ctx.BeginNative("lamp shadows");
        cmd.BeginLabel("lamp shadows");
        cmd.Barrier(BarrierBatch.Full);
        Clear(cmd, sum, 0);
        cmd.FillBuffer(_lists.Handle, 0, (ulong)_listCapacity * 16, 0);
        cmd.Barrier(BarrierBatch.Full);

        // Each lamp's cells: those its movers' shadow boxes cover, less what this frame's depth shows out of the lamp's reach or of every mover's
        // shadow.
        Classify(cmd, lamps, view, pyramid, listBase);
        cmd.Barrier(BarrierBatch.Full);

        var targets = new PenumbraTargets(depth, surface, lost, scratch, filtered, w, h);
        for (int slot = 0; slot < _areas.Count; slot++)
            TraceLamp(cmd, scene, lamps, depth, surface, lost, light, sum, filtered, targets, view, inverse, slot, listBase, listsSize);

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
    /// Grows <see cref="_lists"/> to hold every area's list (twice as many, 32 at least) when it is missing or too small.
    /// </summary>
    [MemberNotNull(nameof(_lists))]
    void EnsureLists()
    {
        if (_lists is not null && _listCapacity >= _areas.Count) return;

        _lists?.Dispose();   // released after the frames in flight
        _listCapacity = Math.Max(_areas.Count * 2, 32);
        var use = BufferUse.Storage | BufferUse.Indirect | BufferUse.TransferDst;
        _lists = DeviceBuffer.Create(_ctx, (ulong)_listCapacity * (16 + MaxCells * 4), use, "lamp shadow cell lists");
    }

    /// <summary>
    /// The lamp of <paramref name="slot"/>: its rays over its listed cells, the filter on its flags, then its light into <paramref name="sum"/>
    /// where its filtered flag is.
    /// </summary>
    void TraceLamp(CommandList cmd, GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture lost, GpuTexture light,
        GpuTexture sum, GpuTexture filtered, in PenumbraTargets targets, in LampShadowView view, in Matrix4x4 inverse, int slot, int listBase,
        ulong listsSize)
    {
        int row = _areas[slot].Row;
        var size = new Vector4(view.Size, TracedSunShaders.LampMovers, row);
        var parameters = TraceParams(lamps, view, new Vector4(scene.Origin, scene.SkinTable), size);
        parameters.Rect = new Vector4(slot, listBase, 0, 0);
        var constants = _ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
        ulong args = (ulong)slot * 16;

        // The rays over the listed cells, the filter on this lamp's flags, then the lamp's light where its filtered flag is.
        Push(cmd, scene, lamps, depth, surface, lost, light, light, constants, _oneProgram, true);
        cmd.BindPipeline(_onePipeline);
        cmd.BindBindlessTable(_oneProgram, 1, GpuBindPoint.Compute);
        cmd.DispatchIndirect(_lists!.Handle, args);
        cmd.Barrier(BarrierBatch.Full);
        _penumbra.RunList(cmd, targets, inverse, view.Footprint, _lists.Handle, listsSize, args, slot, listBase);
        Push(cmd, scene, lamps, depth, surface, filtered, sum, sum, constants, _sumProgram, true);
        cmd.BindPipeline(_sumPipeline);
        cmd.BindBindlessTable(_sumProgram, 1, GpuBindPoint.Compute);
        cmd.DispatchIndirect(_lists.Handle, args);
        cmd.Barrier(BarrierBatch.Full);
    }

    /// <summary>
    /// The cells of every lamp's list (<see cref="LampShadowShaders.Classify"/>), a dispatch a lamp over its area's cells.
    /// </summary>
    void Classify(CommandList cmd, WorldLamps lamps, in LampShadowView view, in Meitou.Game.Rendering.Frame.OcclusionView pyramid, int listBase)
    {
        // The pyramid's level of 64-pixel blocks (level 0 holds 4 x 4 pixels): its vectors as HizPyramid.Vectors lays them out.
        bool has = !pyramid.IsEmpty && pyramid.Vectors[5].Y > 4.5f;
        var eye = has ? new Vector3(pyramid.Vectors[0].X, pyramid.Vectors[0].Y, pyramid.Vectors[0].Z) : view.Eye;
        var hiz = has
            ? new DescriptorBufferInfo(_ctx.Device.BufferAt(pyramid.Buffer.Buffer.Index), pyramid.Buffer.Offset, pyramid.Buffer.Size)
            : new DescriptorBufferInfo(_ctx.Device.BufferAt(_lists!.Handle.Index), 0, _lists.Size);   // a stand-in, not read
        var listed = new DescriptorBufferInfo(_ctx.Device.BufferAt(_lists!.Handle.Index), 0, _lists.Size);
        int wide = ((int)view.Size.X + CellSize - 1) / CellSize, high = ((int)view.Size.Y + CellSize - 1) / CellSize;
        cmd.BindPipeline(_classifyPipeline);
        var writes = stackalloc WriteDescriptorSet[3];
        for (int slot = 0; slot < _areas.Count; slot++)
        {
            ClassifyParams parameters;
            FillClassifyParams(out parameters, slot, lamps, view, pyramid, has, eye, wide, high, listBase);
            var constants = _ctx.Frame.Constants.Write<ClassifyParams>(new ReadOnlySpan<ClassifyParams>(&parameters, 1), 256);
            var uniform = new DescriptorBufferInfo(_ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(ClassifyParams));
            writes[0] = Buffer(0, DescriptorType.UniformBuffer, &uniform);
            writes[1] = Buffer(1, DescriptorType.StorageBuffer, &hiz);
            writes[2] = Buffer(2, DescriptorType.StorageBuffer, &listed);
            cmd.PushDescriptors(_classifyProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 3), GpuBindPoint.Compute);
            int x0 = parameters.CellRect[0], y0 = parameters.CellRect[1], x1 = parameters.CellRect[2], y1 = parameters.CellRect[3];
            cmd.Dispatch((uint)((x1 - x0 + 7) / 8), (uint)((y1 - y0 + 7) / 8));
        }
    }

    /// <summary>
    /// The classification's constants for the area of <paramref name="slot"/>: its lamp, the pyramid's camera (<paramref name="has"/>), its cells
    /// widened by the filter's margins, and the movers in the lamp's reach.
    /// </summary>
    void FillClassifyParams(out ClassifyParams parameters, int slot, WorldLamps lamps, in LampShadowView view,
        in Meitou.Game.Rendering.Frame.OcclusionView pyramid, bool has, Vector3 eye, int wide, int high, int listBase)
    {
        var (row, area, cells, grown) = _areas[slot];
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
        parameters.Info[0] = slot;

        int near = GatherNearMovers(ref parameters, l, eye);
        parameters.Info[1] = near <= ClassifyMovers ? near : -1;
        parameters.Info[2] = has ? (int)BitConverter.SingleToUInt32Bits(pyramid.Vectors[7].X) : 0;
        parameters.Info[3] = has ? ((int)pyramid.Vectors[5].Z + 15) >> 4 : 0;
        parameters.Base[0] = listBase;
        for (int i = 0; i < 64; i++) (parameters.Cells[i], parameters.Wide[i]) = (cells[i], grown[i]);
    }

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
        _movers.Clear();
        _cellsUsed = 0;
        scene.CollectMovers(_movers);
        if (_movers.Count == 0 || !Matrix4x4.Invert(inverse, out var toClip)) return;

        for (int row = 0; row < lamps.InGrid; row++)
            PlanLampArea(row, lamps.RowLamp(row), view, toClip);
    }

    /// <summary>
    /// Adds the area of the lamp at <paramref name="row"/> to <see cref="_areas"/> if a mover's shadow from it falls on the screen.
    /// </summary>
    void PlanLampArea(int row, WorldLight l, in LampShadowView view, in Matrix4x4 toClip)
    {
        Vector4 box = new(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        bool any = false;
        uint[] cells = NextCells();
        foreach (var (centre, radius) in _movers)
        {
            // Each mover apart: one whose shadow stays off the screen must not stretch the lamp's area across it.
            Vector4 own = new(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            if (!Cover(l, centre, radius, view, toClip, ref own) || OffScreen(own, view.Size)) continue;

            box = new Vector4(MathF.Min(box.X, own.X), MathF.Min(box.Y, own.Y), MathF.Max(box.Z, own.Z), MathF.Max(box.W, own.W));
            MarkCells(cells, own, view.Size);
            any = true;
        }

        if (!any)
        {
            _cellsUsed--;
            return;
        }

        var area = new Vector4(
            MathF.Floor(MathF.Max(box.X, 0)),
            MathF.Floor(MathF.Max(box.Y, 0)),
            MathF.Ceiling(MathF.Min(box.Z, view.Size.X)),
            MathF.Ceiling(MathF.Min(box.W, view.Size.Y)));
        if (area.Z <= area.X || area.W <= area.Y)
        {
            _cellsUsed--;
            return;
        }

        _areas.Add((row, area, cells, Grown(cells, view.Size)));
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
        if (_cellsUsed == _cellPool.Count) _cellPool.Add(new uint[64]);

        var cells = _cellPool[_cellsUsed++];
        Array.Clear(cells);
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
