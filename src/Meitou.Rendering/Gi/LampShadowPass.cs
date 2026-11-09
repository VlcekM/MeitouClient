using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Meitou.Data.World;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gi;

/// <summary>The view a lamp shadow trace reconstructs positions with (<see cref="PostProcess"/>'s, as the GI resolve's).</summary>
readonly record struct LampShadowView(Vector3 Right, Vector3 Up, Vector3 Back, Vector3 Eye, Vector2 NearPlanes, Vector2 Tan, Vector2 Size, float Footprint, float Phase);

/// <summary>
/// The trace of the lamps' shadows (docs/render-lights.md "Traced lamp shadows"): <see cref="LampShadowShaders.Trace"/> over the scene depth and the surface target
/// into a target of the render size, in the frame's traced scene. Render thread only.
/// </summary>
sealed unsafe class LampShadowPass : IDisposable
{
    const int Bindings = 12;

    readonly GpuContext ctx;
    readonly GpuProgram program;
    readonly GpuComputePipeline pipeline;
    readonly GpuSampler sampler;
    readonly PenumbraFilter penumbra;
    readonly GpuProgram oneProgram, sumProgram;
    readonly GpuComputePipeline onePipeline, sumPipeline;
    readonly List<(int Row, Vector4 Area, uint[] Cells, uint[] Wide)> areas = [];
    readonly List<uint[]> cellPool = [];
    int cellsUsed;
    /// <summary>The side in pixels of the cells a lamp's pass works on; a mask holds 2048, a screen wider than that takes all its cells.</summary>
    const int CellSize = 64, MaxCells = 2048;
    readonly List<(Vector3 Centre, float Radius)> movers = [];
    /// <summary>How far under a mover's sphere the ground is taken to be, for how far its shadow reaches.</summary>
    const float GroundMargin = 60;

    public LampShadowPass(GpuContext ctx, int lamps)
    {
        this.ctx = ctx;
        penumbra = new PenumbraFilter(ctx);
        program = ctx.Shaders.Compute(LampShadowShaders.Trace(lamps), "lamp shadow trace",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.AccelerationStructureKhr),
            (3, DescriptorType.StorageBuffer), (4, DescriptorType.UniformBuffer), (5, DescriptorType.StorageImage),
            (6, DescriptorType.CombinedImageSampler), (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler), (9, DescriptorType.StorageImage), (10, DescriptorType.StorageImage), (11, DescriptorType.CombinedImageSampler),
        ], GpuSetLayout.Bindless);
        pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(program, "lamp shadow trace"));
        oneProgram = ctx.Shaders.Compute(LampShadowShaders.TraceOne, "lamp shadow one",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.AccelerationStructureKhr),
            (3, DescriptorType.StorageBuffer), (4, DescriptorType.UniformBuffer), (5, DescriptorType.StorageImage),
            (6, DescriptorType.CombinedImageSampler), (7, DescriptorType.CombinedImageSampler), (8, DescriptorType.CombinedImageSampler), (9, DescriptorType.StorageImage), (11, DescriptorType.CombinedImageSampler),
        ], GpuSetLayout.Bindless);
        onePipeline = ctx.Pipelines.Get(new ComputePipelineDesc(oneProgram, "lamp shadow one"));
        sumProgram = ctx.Shaders.Compute(LampShadowShaders.TraceSum, "lamp shadow sum",
        [
            (0, DescriptorType.CombinedImageSampler), (1, DescriptorType.CombinedImageSampler), (2, DescriptorType.StorageImage), (3, DescriptorType.UniformBuffer),
        ]);
        sumPipeline = ctx.Pipelines.Get(new ComputePipelineDesc(sumProgram, "lamp shadow sum"));
        sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public Vector4 LightGrid, Right, Up, Back, Eye, Origin, PlanesTan, Size, Rect;
        public fixed uint Cells[64];
    }

    /// <summary>
    /// Traces into <paramref name="lost"/>, then filters it by the lamps' size into <paramref name="filtered"/> (<see cref="PenumbraFilter"/>, through <paramref name="scratch"/>;
    /// <paramref name="inverse"/>: NDC, jittered as the frame was, to the position relative to the eye). <paramref name="scene"/> has this frame's top level.
    /// </summary>
    public void Run(GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture lost, GpuTexture total, GpuTexture total1, GpuTexture scratch, GpuTexture filtered, in LampShadowView view,
        in Matrix4x4 inverse)
    {
        var parameters = new Params
        {
            LightGrid = lamps.Grid,
            Right = new Vector4(view.Right, view.Footprint),
            Up = new Vector4(view.Up, view.Phase),
            Back = new Vector4(view.Back, 0),
            Eye = new Vector4(view.Eye, 0),
            Origin = new Vector4(scene.Origin, 0),
            PlanesTan = new Vector4(view.NearPlanes, view.Tan.X, view.Tan.Y),
            Size = new Vector4(view.Size, lamps.Tiles is null ? TracedSunShaders.LampMask : TracedSunShaders.LampMovers, 0),
        };
        var constants = ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);

        var cmd = (CommandList)ctx.BeginNative("lamp shadows");
        cmd.BeginLabel("lamp shadows");
        cmd.Barrier(BarrierBatch.Full);
        Push(cmd, scene, lamps, depth, surface, lost, total, total1, constants, program, false);
        cmd.BindPipeline(pipeline);
        cmd.BindBindlessTable(program, 1, GpuBindPoint.Compute);
        cmd.Dispatch((uint)(((int)view.Size.X + 7) / 8), (uint)(((int)view.Size.Y + 7) / 8));
        cmd.Barrier(BarrierBatch.Full);
        penumbra.Run(cmd, new PenumbraTargets(depth, surface, lost, scratch, filtered, (int)view.Size.X, (int)view.Size.Y), inverse, view.Footprint, named: true);
        cmd.EndLabel();
        ctx.EndNative(cmd);
    }

    void Push(CommandList cmd, GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture lost, GpuTexture total, GpuTexture total1, Transient constants, GpuProgram to, bool one)
    {
        var (cells, index, data) = lamps.Current;
        var images = stackalloc DescriptorImageInfo[6];
        images[0] = Image(depth);
        images[1] = Image(surface);
        images[2] = Image(cells);
        images[3] = Image(index);
        images[4] = Image(data);
        images[5] = Image(lamps.TilesTexture);
        var storage = new DescriptorImageInfo(default, ctx.Device.ViewAt(lost.View().Index), ImageLayout.General);
        var storageTotal = new DescriptorImageInfo(default, ctx.Device.ViewAt(total.View().Index), ImageLayout.General);
        var storageTotal1 = new DescriptorImageInfo(default, ctx.Device.ViewAt(total1.View().Index), ImageLayout.General);
        var top = scene.Top!.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr, AccelerationStructureCount = 1, PAccelerationStructures = &top,
        };
        var records = new DescriptorBufferInfo(ctx.Device.BufferAt(scene.Records.Buffer.Index), scene.Records.Offset, scene.Records.Size);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(Params));

        var writes = stackalloc WriteDescriptorSet[Bindings];
        writes[0] = Sampled(0, &images[0]);
        writes[1] = Sampled(1, &images[1]);
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.AccelerationStructureKhr,
        };
        writes[3] = Buffer(3, DescriptorType.StorageBuffer, &records);
        writes[4] = Buffer(4, DescriptorType.UniformBuffer, &uniform);
        writes[5] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 5, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &storage,
        };
        for (int i = 0; i < 3; i++) writes[6 + i] = Sampled((uint)(6 + i), &images[2 + i]);
        writes[11] = Sampled(11, &images[5]);
        writes[9] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 9, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &storageTotal,
        };
        writes[10] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstBinding = 10, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &storageTotal1,
        };
        if (one)
        {
            writes[10] = Sampled(11, &images[5]);
            cmd.PushDescriptors(to, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, Bindings - 1), GpuBindPoint.Compute);
            return;
        }
        cmd.PushDescriptors(to, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, Bindings), GpuBindPoint.Compute);
    }

    /// <summary>
    /// The rays of what moves, one lamp at a time over the part of the screen its shadows can reach: traced, filtered and added to <paramref name="sum"/>, the light lost per unit of albedo, whatever the number of lamps.
    /// <paramref name="filtered"/> ends as 1 everywhere, the factor the drawing pass reads. False: nothing to do, <paramref name="sum"/> is untouched.
    /// </summary>
    public bool RunPerLamp(GiScene scene, WorldLamps lamps, GpuTexture depth, GpuTexture surface, GpuTexture lost, GpuTexture light, GpuTexture sum, GpuTexture scratch, GpuTexture filtered,
        in LampShadowView view, in Matrix4x4 inverse)
    {
        PlanAreas(scene, lamps, view, inverse);
        if (areas.Count == 0) return false;
        int w = (int)view.Size.X, h = (int)view.Size.Y;
        var cmd = (CommandList)ctx.BeginNative("lamp shadows");
        cmd.BeginLabel("lamp shadows");
        cmd.Barrier(BarrierBatch.Full);
        Clear(cmd, sum, 0);
        cmd.Barrier(BarrierBatch.Full);
        int margin = PenumbraFilter.RowMargin + PenumbraFilter.ColumnMargin;
        foreach (var (row, area, cells, wide) in areas)
        {
            var outer = new Vector4(MathF.Max(area.X - margin, 0), MathF.Max(area.Y - margin, 0), MathF.Min(area.Z + margin, w), MathF.Min(area.W + margin, h));
            var parameters = new Params
            {
                LightGrid = lamps.Grid,
                Right = new Vector4(view.Right, view.Footprint),
                Up = new Vector4(view.Up, view.Phase),
                Back = new Vector4(view.Back, 0),
                Eye = new Vector4(view.Eye, 0),
                Origin = new Vector4(scene.Origin, 0),
                PlanesTan = new Vector4(view.NearPlanes, view.Tan.X, view.Tan.Y),
                Size = new Vector4(view.Size, TracedSunShaders.LampMovers, row),
                Rect = outer,
            };
            for (int i = 0; i < 64; i++) parameters.Cells[i] = wide[i];
            var constants = ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
            Push(cmd, scene, lamps, depth, surface, lost, light, light, constants, oneProgram, true);
            cmd.BindPipeline(onePipeline);
            cmd.BindBindlessTable(oneProgram, 1, GpuBindPoint.Compute);
            cmd.Dispatch((uint)(((int)(outer.Z - outer.X) + 7) / 8), (uint)(((int)(outer.W - outer.Y) + 7) / 8));
            cmd.Barrier(BarrierBatch.Full);
            penumbra.RunArea(cmd, new PenumbraTargets(depth, surface, lost, scratch, filtered, w, h), inverse, view.Footprint, area, wide, cells);
            Sum(cmd, filtered, light, sum, area, cells, view.Size);
            cmd.Barrier(BarrierBatch.Full);
        }
        Clear(cmd, filtered, 1);
        cmd.Barrier(BarrierBatch.Full);
        cmd.EndLabel();
        ctx.EndNative(cmd);
        return true;
    }

    void Clear(CommandList cmd, GpuTexture target, float red)
    {
        var clear = new ClearColorValue(red, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        ctx.Device.Vk.CmdClearColorImage(cmd.Handle, ctx.Device.NativeImage(target), ImageLayout.General, &clear, 1, &range);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SumParams
    {
        public Vector4 Rect, Size;
        public fixed uint Cells[64];
    }

    void Sum(CommandList cmd, GpuTexture flag, GpuTexture light, GpuTexture sum, Vector4 area, uint[] cells, Vector2 size)
    {
        var parameters = new SumParams { Rect = area, Size = new Vector4(size, 0, 0) };
        for (int i = 0; i < 64; i++) parameters.Cells[i] = cells[i];
        var constants = ctx.Frame.Constants.Write<SumParams>(new ReadOnlySpan<SumParams>(&parameters, 1), 256);
        var images = stackalloc DescriptorImageInfo[3];
        images[0] = Image(flag);
        images[1] = Image(light);
        images[2] = new DescriptorImageInfo(default, ctx.Device.ViewAt(sum.View().Index), ImageLayout.General);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, (ulong)sizeof(SumParams));
        var writes = stackalloc WriteDescriptorSet[4];
        writes[0] = Sampled(0, &images[0]);
        writes[1] = Sampled(1, &images[1]);
        writes[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &images[2] };
        writes[3] = Buffer(3, DescriptorType.UniformBuffer, &uniform);
        cmd.BindPipeline(sumPipeline);
        cmd.PushDescriptors(sumProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 4), GpuBindPoint.Compute);
        cmd.Dispatch((uint)(((int)(area.Z - area.X) + 7) / 8), (uint)(((int)(area.W - area.Y) + 7) / 8));
    }

    /// <summary>For each lamp something moving stands in, the part of the screen its shadow can fall on.</summary>
    void PlanAreas(GiScene scene, WorldLamps lamps, in LampShadowView view, in Matrix4x4 inverse)
    {
        areas.Clear();
        movers.Clear();
        cellsUsed = 0;
        scene.CollectMovers(movers);
        if (movers.Count == 0 || !Matrix4x4.Invert(inverse, out var toClip)) return;
        for (int row = 0; row < lamps.InGrid; row++)
        {
            var l = lamps.RowLamp(row);
            Vector4 box = new(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            bool any = false;
            uint[] cells = NextCells();
            foreach (var (centre, radius) in movers)
            {
                // Each mover apart: one whose shadow stays off the screen must not stretch the lamp's area across it.
                Vector4 own = new(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                if (!Cover(l, centre, radius, view, toClip, ref own) || own.Z <= 0 || own.W <= 0 || own.X >= view.Size.X || own.Y >= view.Size.Y) continue;
                box = new Vector4(MathF.Min(box.X, own.X), MathF.Min(box.Y, own.Y), MathF.Max(box.Z, own.Z), MathF.Max(box.W, own.W));
                MarkCells(cells, own, view.Size);
                any = true;
            }
            if (!any) { cellsUsed--; continue; }
            var area = new Vector4(MathF.Floor(MathF.Max(box.X, 0)), MathF.Floor(MathF.Max(box.Y, 0)), MathF.Ceiling(MathF.Min(box.Z, view.Size.X)), MathF.Ceiling(MathF.Min(box.W, view.Size.Y)));
            if (area.Z <= area.X || area.W <= area.Y) { cellsUsed--; continue; }
            areas.Add((row, area, cells, Grown(cells, view.Size)));
        }
    }

    uint[] NextCells()
    {
        if (cellsUsed == cellPool.Count) cellPool.Add(new uint[64]);
        var cells = cellPool[cellsUsed++];
        Array.Clear(cells);
        return cells;
    }

    static void MarkCells(uint[] cells, Vector4 box, Vector2 size)
    {
        int wide = ((int)size.X + CellSize - 1) / CellSize, high = ((int)size.Y + CellSize - 1) / CellSize;
        if (wide * high > MaxCells) { Array.Fill(cells, uint.MaxValue); return; }
        int x0 = Math.Clamp((int)box.X / CellSize, 0, wide - 1), x1 = Math.Clamp((int)box.Z / CellSize, 0, wide - 1);
        int y0 = Math.Clamp((int)box.Y / CellSize, 0, high - 1), y1 = Math.Clamp((int)box.W / CellSize, 0, high - 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                cells[(y * wide + x) / 32] |= 1u << ((y * wide + x) % 32);
    }

    /// <summary>The cells and their neighbours: what the rays must have done round a filtered cell.</summary>
    uint[] Grown(uint[] cells, Vector2 size)
    {
        var grown = NextCells();
        int wide = ((int)size.X + CellSize - 1) / CellSize, high = ((int)size.Y + CellSize - 1) / CellSize;
        if (wide * high > MaxCells) { Array.Fill(grown, uint.MaxValue); return grown; }
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

    /// <summary>Grows <paramref name="box"/> (pixels) by where a sphere's shadow from <paramref name="l"/> can fall: the sphere and its shadow's end on the ground under it, or the lamp's reach. False: the lamp cannot reach it.</summary>
    static bool Cover(WorldLight l, Vector3 centre, float radius, in LampShadowView view, in Matrix4x4 toClip, ref Vector4 box)
    {
        var v = centre - l.Position;
        float dist = v.Length();
        if (dist > l.Radius + radius) return false;
        if (dist > radius + 1 && l.Type == WorldLightType.Spot)
        {
            float half = MathF.Max(l.InnerAngle, l.OuterAngle) * 0.5f;
            if (MathF.Acos(Math.Clamp(Vector3.Dot(v, l.Direction) / dist, -1, 1)) - MathF.Asin(Math.Min(radius / dist, 1)) > half * 1.04f) return false;
        }
        var dir = dist > 1e-3f ? v / dist : Vector3.UnitY * -1;
        float along = MathF.Max(l.Radius - dist + radius, 0);
        if (dir.Y < -1e-3f) along = MathF.Min(along, (centre.Y - radius - GroundMargin - centre.Y) / dir.Y);
        Grow(centre, radius, view, toClip, ref box);
        float scale = dist > radius ? (dist + along) / dist : 1;
        Grow(centre + dir * along, radius * scale, view, toClip, ref box);
        return true;
    }

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

    DescriptorImageInfo Image(GpuTexture t) => new(ctx.Device.SamplerAt(sampler.Index), ctx.Device.ViewAt(t.View().Index), ImageLayout.General);

    static WriteDescriptorSet Sampled(uint binding, DescriptorImageInfo* image) => new()
    {
        SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = image,
    };

    static WriteDescriptorSet Buffer(uint binding, DescriptorType type, DescriptorBufferInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet, DstBinding = binding, DescriptorCount = 1, DescriptorType = type, PBufferInfo = info,
    };

    public void Dispose()
    {
        ctx.Shaders.Dispose(program);
        ctx.Shaders.Dispose(oneProgram);
        ctx.Shaders.Dispose(sumProgram);
        penumbra.Dispose();
    }
}
