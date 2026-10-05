using System.Runtime.InteropServices;
using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Vulkan.Shaders;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using GlPolygonMode = Meitou.Rendering.Gpu.PolygonMode;
using Sampler = Silk.NET.Vulkan.Sampler;

namespace Meitou.Rendering.Vulkan;

public sealed unsafe partial class VkGl
{
    /// <summary>
    /// What a pipeline is made from besides the program: the vertex layout of the inputs the program reads (format, stride and
    /// per-instance flag, packed per location), the primitive, the attachments' formats and the fixed-function state that is not
    /// dynamic. Cull mode, front face, depth test/write/compare, depth bias, viewport and scissor are dynamic state (Vulkan 1.3 core).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal record struct PipelineKey(
        uint Program, ulong V0, ulong V1, ulong V2, ulong V3, ulong V4, ulong V5, ulong V6, ulong V7,
        PrimitiveTopology Topology, Format Colour, Format Depth, int Samples,
        bool Blend, BlendingFactor Src, BlendingFactor Dst, byte Mask, GlPolygonMode Polygon, bool AlphaToCoverage, bool DepthClamp);

    readonly Dictionary<PipelineKey, Pipeline> pipelines = [];
    Silk.NET.Vulkan.Extensions.KHR.KhrPushDescriptor? pushDescriptor;
    Pipeline lastPipeline;
    // The stand-ins for missing attributes and textures, and the samplers: shared with the native API (Gpu/GlConventions.cs).
    GpuDefaults defaults = null!;
    SamplerCache samplers = null!;
    readonly Dictionary<(int Kind, bool Depth), GlTextureObj> dummyTextures = [];


    // Dynamic state as last recorded into the current command buffer.
    (int X, int Y, int W, int H) sentViewport, sentScissor;
    CullModeFlags sentCull;
    Silk.NET.Vulkan.FrontFace sentFront;
    bool sentDepthTest, sentDepthWrite, sentBiasEnable;
    CompareOp sentCompare;
    (float, float) sentBias;

    void InitDummies()
    {
        // Disabled attributes read GL's default generic value (0, 0, 0, 1): float at offset 0, int at 16 (GpuDefaults.DummyVertex).
        defaults = Context.Defaults;
        samplers = Context.Samplers;
        Context.DummyOverride = SampledDummy;
        Context.LodBias = () => TextureLodBias;
    }

    void DestroyDummies()
    {
        foreach (var t in dummyTextures.Values) DestroyTexture(t);
        dummyTextures.Clear();
    }

    void DestroyPipelines()
    {
        foreach (var pl in pipelines.Values) vk.DestroyPipeline(dev, pl, null);
        pipelines.Clear();
    }

    void ForgetPipelines(GlProgramObj p)
    {
        foreach (var key in pipelines.Keys.Where(k => k.Program == p.Id).ToList())
        {
            var pl = pipelines[key];
            pipelines.Remove(key);
            device.Frames.DeferDelete(() => vk.DestroyPipeline(dev, pl, null));
        }
    }

    // ----- Draws -----

    public void DrawArrays(PrimitiveType mode, int first, uint count) => DrawArraysInstanced(mode, first, count, 1);

    public void DrawArraysInstanced(PrimitiveType mode, int first, uint count, uint instancecount)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (count == 0 || instancecount == 0 || !PrepareDraw(mode)) return;
        Stats.DrawTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        long nt = NatStart(); vk.CmdDraw(cmd, count, instancecount, (uint)first, 0); NatEnd(8, nt);
        Stats.Draws++;
        Lap(8);
    }

    /// <summary><c>MEITOU_VKGL_PHASES=1</c>: times the parts of a draw's preparation into <see cref="VkGlStats.PhaseTicks"/> (a few stopwatch reads per draw: for
    /// measuring, off otherwise; static readonly, so the JIT drops the calls when off).</summary>
    public static readonly bool Phases = Environment.GetEnvironmentVariable("MEITOU_VKGL_PHASES") == "1";
    long phaseStart;

    /// <summary>With <see cref="Phases"/>: the time spent inside the <c>vkCmd*</c> calls themselves (Silk.NET's dispatch and the driver), by phase, in <see cref="VkGlStats.NativeTicks"/>.</summary>
    static long NatStart() => Phases ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
    void NatEnd(int phase, long start)
    {
        if (Phases) Stats.NativeTicks[phase] += System.Diagnostics.Stopwatch.GetTimestamp() - start;
    }

    void Lap(int phase)
    {
        if (!Phases) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        Stats.PhaseTicks[phase] += now - phaseStart;
        phaseStart = now;
    }

    public void DrawElements(PrimitiveType mode, uint count, DrawElementsType type, void* indices) => DrawElementsInstanced(mode, count, type, indices, 1);

    public void DrawElementsInstanced(PrimitiveType mode, uint count, DrawElementsType type, void* indices, uint instancecount)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (count == 0 || instancecount == 0 || !PrepareDraw(mode)) return;
        Stats.DrawTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        var indexType = type switch
        {
            DrawElementsType.UnsignedInt => IndexType.Uint32,
            DrawElementsType.UnsignedShort => IndexType.Uint16,
            _ => throw new NotSupportedException($"index type {type}"),
        };
        var eb = buffers.TryGetValue(CurrentVao.ElementBuffer, out var b) ? b : throw new InvalidOperationException("no element buffer bound");
        var (buffer, offset) = Use(eb);
        long nt = NatStart(); vk.CmdBindIndexBuffer(cmd, buffer, offset + (ulong)(nint)indices, indexType);
        vk.CmdDrawIndexed(cmd, count, instancecount, 0, 0, 0); NatEnd(8, nt);
        Stats.Draws++;
        Stats.IndexBufferBinds++;
        Lap(8);
    }

    /// <summary>Records everything a draw needs: the pass, the pipeline, dynamic state, descriptors and vertex buffers. False when
    /// nothing can be drawn (no program, or a program that failed to link).</summary>
    bool PrepareDraw(PrimitiveType mode)
    {
        if (program is not { Linked: true } p) return false;
        EnsurePass();
        var cb = cmd;
        var vao = CurrentVao;
        if (Phases) phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // Vertex layout of the inputs the program reads.
        Span<ulong> packed = stackalloc ulong[8];
        packed.Clear();
        var locs = p.InputLocations;
        for (int i = 0; i < locs.Length; i++)
        {
            int loc = locs[i];
            ref var a = ref vao.Attribs[loc];
            uint entry;
            if (a.Enabled) entry = (uint)AttribFormat(in a) | (a.Stride == 0 ? AttribBytes(in a) : a.Stride) << 12 | a.Divisor << 28 | 1u << 29;
            else entry = (uint)DummyFormat(p.InputKinds[i]) | 1u << 30;
            packed[loc >> 1] |= (ulong)entry << ((loc & 1) * 32);
        }

        var key = new PipelineKey(p.Id, packed[0], packed[1], packed[2], packed[3], packed[4], packed[5], packed[6], packed[7],
            GlConventions.Topology(mode),
            passColour?.Texture.Format ?? Format.Undefined, passDepth?.Texture.Format ?? Format.Undefined,
            (passColour ?? passDepth)?.Texture.Samples ?? 1,
            blend && passColour is not null, blend ? blendSrc : BlendingFactor.One, blend ? blendDst : BlendingFactor.Zero,
            (byte)((colourMask.R ? 1 : 0) | (colourMask.G ? 2 : 0) | (colourMask.B ? 4 : 0) | (colourMask.A ? 8 : 0)),
            polygonMode, alphaToCoverage && ((passColour ?? passDepth)?.Texture.Samples ?? 1) > 1, depthClamp && device.DepthClamp);
        Lap(0);
        if (!pipelines.TryGetValue(key, out var pipeline))
        {
            long created = System.Diagnostics.Stopwatch.GetTimestamp();
            pipeline = CreatePipeline(p, in key, vao);
            pipelines[key] = pipeline;
            Stats.PipelinesCreated++;
            Stats.PipelineCreateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - created;
        }
        Lap(1);
        if (pipeline.Handle != lastPipeline.Handle)
        {
            long nt = NatStart(); vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline); NatEnd(2, nt);
            lastPipeline = pipeline;
            Stats.PipelineBinds++;
        }
        Lap(2);

        SetDynamicState(cb);
        Lap(3);
        BindResources(cb, p);

        // Vertex buffers: one binding per input location.
        for (int i = 0; i < locs.Length; i++)
        {
            int loc = locs[i];
            ref var a = ref vao.Attribs[loc];
            VkBuffer buffer;
            ulong offset;
            if (a.Enabled && buffers.TryGetValue(a.Buffer, out var vb) && vb.Defined)
            {
                var (bb, bo) = Use(vb);
                (buffer, offset) = (bb, bo + (ulong)a.Offset);
            }
            else (buffer, offset) = (defaults.DummyVertex.Buffer, GlConventions.DummyVertexOffset(p.InputKinds[i]));
            long nt = NatStart(); vk.CmdBindVertexBuffers(cb, (uint)loc, 1, &buffer, &offset); NatEnd(7, nt);
            Stats.VertexBufferBinds++;
        }
        Lap(7);
        return true;
    }

    static Format DummyFormat(ScalarKind kind) => GlConventions.DummyVertexFormat(kind);

    /// <summary>The pipeline for a key, through the factory the native API uses (Gpu/Pipelines.cs), so both make the same pipelines.</summary>
    Pipeline CreatePipeline(GlProgramObj p, in PipelineKey key, GlVertexArray vao) =>
        PipelineFactory.CreateGraphics(device, p.VertexModule, p.FragmentModule, p.Layout, KeyInputs(p, in key), key.Topology,
            new AttachmentFormats(key.Colour, key.Depth, key.Samples), new BlendState(key.Blend, Factor(key.Src), Factor(key.Dst)),
            (ColorComponentFlags)key.Mask, key.Polygon == GlPolygonMode.Line, key.AlphaToCoverage, key.DepthClamp);

    /// <summary>The vertex inputs a key packs: per input location its format, stride and rate (stride 0 for a disabled attribute).</summary>
    static VertexInput[] KeyInputs(GlProgramObj p, in PipelineKey key)
    {
        var locs = p.InputLocations;
        var inputs = new VertexInput[locs.Length];
        Span<ulong> packed = [key.V0, key.V1, key.V2, key.V3, key.V4, key.V5, key.V6, key.V7];
        for (int i = 0; i < locs.Length; i++)
        {
            int loc = locs[i];
            uint e = (uint)(packed[loc >> 1] >> ((loc & 1) * 32));
            bool dummy = (e & (1u << 30)) != 0;
            inputs[i] = new VertexInput((uint)loc, (Format)(e & 0xFFF), dummy ? 0 : (e >> 12) & 0xFFFF, !dummy && ((e >> 28) & 1) != 0);
        }
        return inputs;
    }

    static BlendFactor Factor(BlendingFactor f) => GlConventions.BlendFactor(f);

    static CompareOp Compare(DepthFunction f) => GlConventions.CompareOp(f);

    void SetDynamicState(CommandBuffer cb)
    {
        bool all = dynamicStateDirty;
        dynamicStateDirty = false;
        (int X, int Y, int W, int H) vp = viewport.W > 0 ? viewport : (0, 0, passWidth, passHeight);
        if (all || vp != sentViewport)
        {
            var v = new Viewport(vp.X, vp.Y, vp.W, vp.H, 0, 1);
            Stats.DynamicStateCalls++; long nt = NatStart(); vk.CmdSetViewport(cb, 0, 1, &v); NatEnd(3, nt);
            sentViewport = vp;
        }
        (int X, int Y, int W, int H) sc = scissorTest ? scissor : (0, 0, passWidth, passHeight);
        // Vulkan scissors may not start below zero; clip to the attachment.
        int x0 = Math.Max(sc.X, 0), y0 = Math.Max(sc.Y, 0);
        int x1 = Math.Min(sc.X + sc.W, passWidth), y1 = Math.Min(sc.Y + sc.H, passHeight);
        var clipped = (x0, y0, Math.Max(x1 - x0, 0), Math.Max(y1 - y0, 0));
        if (all || clipped != sentScissor)
        {
            var r = new Rect2D(new Offset2D(clipped.x0, clipped.y0), new Extent2D((uint)clipped.Item3, (uint)clipped.Item4));
            Stats.DynamicStateCalls++; long nt = NatStart(); vk.CmdSetScissor(cb, 0, 1, &r); NatEnd(3, nt);
            sentScissor = clipped;
        }
        var cull = !cullFace ? CullModeFlags.None : cullMode switch
        {
            TriangleFace.Front => CullModeFlags.FrontBit,
            TriangleFace.FrontAndBack => CullModeFlags.FrontAndBack,
            _ => CullModeFlags.BackBit,
        };
        if (all || cull != sentCull) { Stats.DynamicStateCalls++; long nt = NatStart(); vk.CmdSetCullMode(cb, cull); NatEnd(3, nt); sentCull = cull; }
        // GL's counter-clockwise is Vulkan's clockwise: same pixel rows, opposite sign convention for the area.
        var front = GlConventions.FrontFace(frontFace);
        if (all || front != sentFront) { Stats.DynamicStateCalls++; long nt = NatStart(); vk.CmdSetFrontFace(cb, front); NatEnd(3, nt); sentFront = front; }
        bool test = depthTest && passDepth is not null;
        // GL writes depth only while the depth test is on.
        bool write = test && depthWrite;
        if (all || test != sentDepthTest) { Stats.DynamicStateCalls++; long nt = NatStart(); vk.CmdSetDepthTestEnable(cb, test); NatEnd(3, nt); sentDepthTest = test; }
        if (all || write != sentDepthWrite) { Stats.DynamicStateCalls++; long nt = NatStart(); vk.CmdSetDepthWriteEnable(cb, write); NatEnd(3, nt); sentDepthWrite = write; }
        var cmp = Compare(depthFunc);
        if (all || cmp != sentCompare) { Stats.DynamicStateCalls++; long nt = NatStart(); vk.CmdSetDepthCompareOp(cb, cmp); NatEnd(3, nt); sentCompare = cmp; }
        bool bias = polygonMode == GlPolygonMode.Line ? offsetLine : offsetFill;
        if (all || bias != sentBiasEnable) { Stats.DynamicStateCalls++; long nt = NatStart(); vk.CmdSetDepthBiasEnable(cb, bias); NatEnd(3, nt); sentBiasEnable = bias; }
        if (all || (offsetUnits, offsetFactor) != sentBias) { Stats.DynamicStateCalls++; long nt = NatStart(); vk.CmdSetDepthBias(cb, offsetUnits, 0, offsetFactor); NatEnd(3, nt); sentBias = (offsetUnits, offsetFactor); }
    }

    // ----- Descriptors -----

    void BindResources(CommandBuffer cb, GlProgramObj p)
    {
        bool programChanged = boundProgram != p;
        boundProgram = p;

        // Set 1: the loose uniforms, copied into the uniform ring when they changed (or the frame moved on), bound by offset.
        if (p.VertexDefaultBlock is not null || p.FragmentDefaultBlock is not null)
        {
            long frame = device.Frames.FrameNumber;
            if (p.SliceFrame != frame) { p.VertexDirty = p.FragmentDirty = true; p.SliceFrame = frame; }
            bool moved = false;
            if (p.VertexDirty && p.VertexDefault.Length > 0) { p.VertexSlice = CopyToRing(p.VertexDefault); p.VertexDirty = false; moved = true; }
            if (p.FragmentDirty && p.FragmentDefault.Length > 0) { p.FragmentSlice = CopyToRing(p.FragmentDefault); p.FragmentDirty = false; moved = true; }
            if (moved || programChanged)
            {
                var set = DynamicSet(p);
                var offsets = stackalloc uint[2];
                uint n = 0;
                if (p.VertexDefaultBlock is not null) offsets[n++] = (uint)p.VertexSlice.Offset;
                if (p.FragmentDefaultBlock is not null) offsets[n++] = (uint)p.FragmentSlice.Offset;
                long nt = NatStart(); vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, p.Layout, 1, 1, &set, n, offsets); NatEnd(4, nt);
                Stats.Set1Binds++;
            }
        }

        Lap(4);
        // Set 0: named blocks and samplers, pushed when they differ from what this program last pushed in this command buffer.
        var blocks = p.BlockList;
        var samplerList = p.SamplerList;
        int count = blocks.Length + samplerList.Length;
        if (count == 0) { Lap(5); return; }
        var writes = stackalloc WriteDescriptorSet[count];
        var bufferInfos = stackalloc DescriptorBufferInfo[blocks.Length + 1];
        var imageInfos = stackalloc DescriptorImageInfo[samplerList.Length + 1];
        var last = p.LastPush;
        bool same = !programChanged && p.LastPushEpoch == pushEpoch;
        int w = 0;
        for (int i = 0; i < blocks.Length; i++)
        {
            ref readonly var b = ref blocks[i];
            uint point = p.BlockBindings.TryGetValue(b.Name, out var pt) ? pt : 0;
            if (!buffers.TryGetValue(uniformBindings[point], out var ub) || !ub.Defined)
                throw new InvalidOperationException($"program {p.Id}: no buffer bound for uniform block {b.Name} (binding {point})");
            var (buf, off) = Use(ub);
            ulong range = (ulong)Math.Min(b.Size, ub.Size);
            bufferInfos[i] = new DescriptorBufferInfo(buf, off, range);
            same &= last[w * 3] == buf.Handle && last[w * 3 + 1] == off && last[w * 3 + 2] == range;
            (last[w * 3], last[w * 3 + 1], last[w * 3 + 2]) = (buf.Handle, off, range);
            writes[w++] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstBinding = b.Binding, DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &bufferInfos[i],
            };
        }
        for (int i = 0; i < samplerList.Length; i++)
        {
            ref readonly var s = ref samplerList[i];
            var t = SamplerTexture(s.Info, s.Slot.Unit);
            var (sampler, view) = SamplerAndView(t, s.Info.Depth);
            imageInfos[i] = new DescriptorImageInfo(sampler, view, ImageLayout.General);
            same &= last[w * 3] == sampler.Handle && last[w * 3 + 1] == view.Handle;
            (last[w * 3], last[w * 3 + 1]) = (sampler.Handle, view.Handle);
            writes[w++] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstBinding = s.Binding, DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &imageInfos[i],
            };
        }
        if (same) { Stats.PushSkips++; Lap(5); return; }
        Lap(5);
        p.LastPushEpoch = pushEpoch;

        if (p.PushDescriptors)
        {
            // The extension entry point: vkCmdPushDescriptorSet is only core from Vulkan 1.4.
            if (pushDescriptor is null && !vk.TryGetDeviceExtension(device.Instance, dev, out pushDescriptor))
                throw new InvalidOperationException("VK_KHR_push_descriptor entry points missing");
            long nt = NatStart(); pushDescriptor!.CmdPushDescriptorSet(cb, PipelineBindPoint.Graphics, p.Layout, 0, (uint)w, writes); NatEnd(6, nt);
            Stats.DescriptorPushes++;
            Stats.DescriptorWrites += w;
            Stats.PushedTextures += samplerList.Length;
            Lap(6);
            return;
        }
        var set0 = AllocateSet(p.SetLayout);
        for (int i = 0; i < w; i++) writes[i].DstSet = set0;
        vk.UpdateDescriptorSets(dev, (uint)w, writes, 0, null);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, p.Layout, 0, 1, &set0, 0, null);
    }

    // The program whose layout the command buffer's descriptors were last bound with, and a counter that invalidates every
    // program's LastPush when a new command buffer starts (ResetFrameState).
    GlProgramObj? boundProgram;
    long pushEpoch;

    /// <summary>Set 1 for the chunks the program's current uniform slices live in (made once per pair of chunks; the uniform
    /// ring's chunks live as long as the translation).</summary>
    DescriptorSet DynamicSet(GlProgramObj p)
    {
        var key = (p.VertexDefaultBlock is null ? 0 : p.VertexSlice.Buffer.Handle, p.FragmentDefaultBlock is null ? 0 : p.FragmentSlice.Buffer.Handle);
        if (p.DynamicSets.TryGetValue(key, out var set)) return set;
        set = AllocatePersistentSet(p.DynamicSetLayout);
        var infos = stackalloc DescriptorBufferInfo[2];
        var writes = stackalloc WriteDescriptorSet[2];
        uint n = 0;
        if (p.VertexDefaultBlock is { } vb)
        {
            infos[n] = new DescriptorBufferInfo(p.VertexSlice.Buffer, 0, (ulong)vb.Size);
            writes[n] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBufferDynamic, PBufferInfo = &infos[n] };
            n++;
        }
        if (p.FragmentDefaultBlock is { } fb)
        {
            infos[n] = new DescriptorBufferInfo(p.FragmentSlice.Buffer, 0, (ulong)fb.Size);
            writes[n] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBufferDynamic, PBufferInfo = &infos[n] };
            n++;
        }
        vk.UpdateDescriptorSets(dev, n, writes, 0, null);
        p.DynamicSets[key] = set;
        return set;
    }

    readonly List<DescriptorPool> persistentPools = [];

    DescriptorSet AllocatePersistentSet(DescriptorSetLayout layout)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (persistentPools.Count > 0)
            {
                var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = persistentPools[^1], DescriptorSetCount = 1, PSetLayouts = &layout };
                DescriptorSet set;
                if (vk.AllocateDescriptorSets(dev, &ai, &set) == Result.Success) return set;
            }
            var size = new DescriptorPoolSize(DescriptorType.UniformBufferDynamic, 2048);
            var pi = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1024, PoolSizeCount = 1, PPoolSizes = &size };
            Check(vk.CreateDescriptorPool(dev, &pi, null, out var fresh));
            persistentPools.Add(fresh);
        }
        throw new InvalidOperationException("descriptor set allocation failed");
    }

    ulong uniformAlign => Math.Max(device.Limits.MinUniformBufferOffsetAlignment, 16);

    /// <summary>The sampler and view a texture samples with, cached on the texture until its state changes.</summary>
    (Sampler, ImageView) SamplerAndView(GlTextureObj t, bool shadow)
    {
        if (t.CachedVersion == t.Version && t.CachedShadow == shadow && t.CachedLevels == t.DefinedLevels && t.CachedImage == t.Image && t.CachedBias == TextureLodBias)
            return (t.CachedSampler, t.CachedView);
        t.CachedSampler = SamplerFor(t, shadow);
        t.CachedView = SampleView(t);
        (t.CachedVersion, t.CachedShadow, t.CachedLevels, t.CachedImage, t.CachedBias) = (t.Version, shadow, t.DefinedLevels, t.Image, TextureLodBias);
        return (t.CachedSampler, t.CachedView);
    }

    RingSlice CopyToRing(byte[] data)
    {
        var slice = uniformRings[device.Frames.Slot].Allocate((ulong)data.Length, uniformAlign);
        fixed (byte* src = data) System.Buffer.MemoryCopy(src, slice.Pointer, data.Length, data.Length);
        Stats.UniformBytes += data.Length;
        Stats.UniformCopies++;
        return slice;
    }

    /// <summary>The texture bound to <paramref name="unit"/> for the sampler's type, or a 1×1 stand-in (GL reads incomplete or
    /// missing textures as (0, 0, 0, 1)).</summary>
    GlTextureObj SamplerTexture(SamplerInfo s, int unit)
    {
        int slot = s.Dimension == SamplerDimension.Cube ? 2 : s.Arrayed ? 1 : 0;
        // A shadow sampler needs a depth texture; a plain sampler reads a depth texture's value (SSAO, the motion vectors).
        if (unit >= 0 && unit < units.GetLength(0) && units[unit, slot] is { Image: not null } t && (t.IsDepth || !s.Depth)) return t;
        int kind = slot * 4 + (int)s.SampledKind;
        if (!dummyTextures.TryGetValue((kind, s.Depth), out var d))
        {
            d = CreateDummyTexture(slot, s.SampledKind, s.Depth);
            dummyTextures[(kind, s.Depth)] = d;
        }
        return d;
    }

    /// <summary>The sampler and view VkGl binds for <paramref name="s"/> when nothing is bound (the native API's stand-in while VkGl exists).</summary>
    SampledTexture SampledDummy(SamplerInfo s)
    {
        var t = SamplerTexture(s, -1);
        var (sampler, view) = SamplerAndView(t, s.Depth);
        return new SampledTexture(sampler, view, t.Image!.Image);
    }

    /// <summary>A GL texture object over the shared stand-in image (borrowed: <see cref="DestroyTexture"/> leaves the image to <see cref="GpuDefaults"/>).</summary>
    GlTextureObj CreateDummyTexture(int slot, ScalarKind kind, bool depth)
    {
        var d = defaults.Texture(slot, kind, depth);
        return new GlTextureObj(0)
        {
            Format = d.Format, Width = 1, Height = 1, Layers = d.Layers, Levels = 1, DefinedLevels = 1,
            Target = slot == 2 ? TextureTarget.TextureCubeMap : slot == 1 ? TextureTarget.Texture2DArray : TextureTarget.Texture2D,
            MinFilter = TextureMinFilter.Nearest, MagFilter = TextureMagFilter.Nearest,
            Image = d.Image, Borrowed = true,
        };
    }

    /// <summary>The sampler GL's state on <paramref name="t"/> describes (<see cref="SamplerDesc.FromGl"/>, shared with the native API).</summary>
    Sampler SamplerFor(GlTextureObj t, bool shadow) =>
        samplers.Get(SamplerDesc.FromGl(t.MinFilter, t.MagFilter, t.WrapS, t.WrapT, t.WrapR, shadow && t.Compare, t.CompareFunc, t.TransparentBorder,
            t.Anisotropy, t.IsInteger || t.Format is Format.R32Sint, TextureLodBias));

    void DestroySamplers()
    {

        foreach (var pool in descriptorPools) vk.DestroyDescriptorPool(dev, pool, null);
        foreach (var pool in persistentPools) vk.DestroyDescriptorPool(dev, pool, null);
        persistentPools.Clear();
        descriptorPools.Clear();
        DestroyQueries();
    }

    // Fallback when a program has more descriptors than push descriptors allow: sets from per-frame pools.
    readonly List<DescriptorPool> descriptorPools = [];
    readonly Dictionary<int, List<DescriptorPool>> framePools = [];

    DescriptorSet AllocateSet(DescriptorSetLayout layout)
    {
        int slot = device.Frames.Slot;
        if (!framePools.TryGetValue(slot, out var list)) framePools[slot] = list = [];
        var sizes = stackalloc DescriptorPoolSize[] { new(DescriptorType.UniformBuffer, 4096), new(DescriptorType.CombinedImageSampler, 8192) };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (list.Count > 0)
            {
                var pool = list[^1];
                var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &layout };
                DescriptorSet set;
                if (vk.AllocateDescriptorSets(dev, &ai, &set) == Result.Success) return set;
            }
            var pi = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1024, PoolSizeCount = 2, PPoolSizes = sizes };
            Check(vk.CreateDescriptorPool(dev, &pi, null, out var fresh));
            descriptorPools.Add(fresh);
            list.Add(fresh);
        }
        throw new InvalidOperationException("descriptor set allocation failed");
    }

    /// <summary>At the start of a frame: its slot's descriptor pools are free again.</summary>
    void ResetFramePools(int slot)
    {
        if (!framePools.TryGetValue(slot, out var list)) return;
        foreach (var pool in list) vk.ResetDescriptorPool(dev, pool, 0);
    }
}
