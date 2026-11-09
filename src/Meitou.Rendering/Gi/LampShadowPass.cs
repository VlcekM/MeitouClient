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
    readonly List<(int Row, float Weight, Vector4 Area)> areas = [];
    readonly List<(Vector3 Centre, float Radius)> movers = [];
    /// <summary>The pixels a frame traces per lamp, in screens: past it the faintest lamps lose what moves' shadows.</summary>
    const float ScreensPerFrame = 3;
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
        foreach (var (row, _, area) in areas)
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
            var constants = ctx.Frame.Constants.Write<Params>(new ReadOnlySpan<Params>(&parameters, 1), 256);
            Push(cmd, scene, lamps, depth, surface, lost, light, light, constants, oneProgram, true);
            cmd.BindPipeline(onePipeline);
            cmd.BindBindlessTable(oneProgram, 1, GpuBindPoint.Compute);
            cmd.Dispatch((uint)(((int)(outer.Z - outer.X) + 7) / 8), (uint)(((int)(outer.W - outer.Y) + 7) / 8));
            cmd.Barrier(BarrierBatch.Full);
            penumbra.RunArea(cmd, new PenumbraTargets(depth, surface, lost, scratch, filtered, w, h), inverse, view.Footprint, area);
            Sum(cmd, filtered, light, sum, area);
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

    void Sum(CommandList cmd, GpuTexture flag, GpuTexture light, GpuTexture sum, Vector4 area)
    {
        var constants = ctx.Frame.Constants.Write<Vector4>(new ReadOnlySpan<Vector4>(&area, 1), 256);
        var images = stackalloc DescriptorImageInfo[3];
        images[0] = Image(flag);
        images[1] = Image(light);
        images[2] = new DescriptorImageInfo(default, ctx.Device.ViewAt(sum.View().Index), ImageLayout.General);
        var uniform = new DescriptorBufferInfo(ctx.Device.BufferAt(constants.Handle.Index), constants.Offset, 16);
        var writes = stackalloc WriteDescriptorSet[4];
        writes[0] = Sampled(0, &images[0]);
        writes[1] = Sampled(1, &images[1]);
        writes[2] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.StorageImage, PImageInfo = &images[2] };
        writes[3] = Buffer(3, DescriptorType.UniformBuffer, &uniform);
        cmd.BindPipeline(sumPipeline);
        cmd.PushDescriptors(sumProgram, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 4), GpuBindPoint.Compute);
        cmd.Dispatch((uint)(((int)(area.Z - area.X) + 7) / 8), (uint)(((int)(area.W - area.Y) + 7) / 8));
    }

    /// <summary>For each lamp something moving stands in, the part of the screen its shadow can fall on, strongest lamps first within the frame's pixels.</summary>
    void PlanAreas(GiScene scene, WorldLamps lamps, in LampShadowView view, in Matrix4x4 inverse)
    {
        areas.Clear();
        movers.Clear();
        scene.CollectMovers(movers);
        if (movers.Count == 0 || !Matrix4x4.Invert(inverse, out var toClip)) return;
        for (int row = 0; row < lamps.InGrid; row++)
        {
            var l = lamps.RowLamp(row);
            Vector4 box = new(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            bool any = false;
            foreach (var (centre, radius) in movers)
            {
                // Each mover apart: one whose shadow stays off the screen must not stretch the lamp's area across it.
                Vector4 own = new(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                if (!Cover(l, centre, radius, view, toClip, ref own) || own.Z <= 0 || own.W <= 0 || own.X >= view.Size.X || own.Y >= view.Size.Y) continue;
                box = new Vector4(MathF.Min(box.X, own.X), MathF.Min(box.Y, own.Y), MathF.Max(box.Z, own.Z), MathF.Max(box.W, own.W));
                any = true;
            }
            if (!any) continue;
            var area = new Vector4(MathF.Floor(MathF.Max(box.X, 0)), MathF.Floor(MathF.Max(box.Y, 0)), MathF.Ceiling(MathF.Min(box.Z, view.Size.X)), MathF.Ceiling(MathF.Min(box.W, view.Size.Y)));
            if (area.Z <= area.X || area.W <= area.Y) continue;
            areas.Add((row, l.Intensity * l.Radius / (50 + MathF.Max(Vector3.Distance(view.Eye, l.Position) - l.Radius, 0)), area));
        }
        areas.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        float budget = ScreensPerFrame * view.Size.X * view.Size.Y;
        int margin = PenumbraFilter.RowMargin + PenumbraFilter.ColumnMargin, kept = 0;
        foreach (var (_, _, area) in areas)
        {
            budget -= (area.Z - area.X + 2 * margin) * (area.W - area.Y + 2 * margin);
            if (budget < 0 && kept > 0) break;
            kept++;
        }
        areas.RemoveRange(kept, areas.Count - kept);
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
