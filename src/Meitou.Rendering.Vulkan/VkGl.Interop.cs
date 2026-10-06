using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Vulkan;

/// <summary>The coexistence seam (docs/renderer-native.md 4.2): native segments in VkGl's frame, and GL objects shared both ways.</summary>
public sealed unsafe partial class VkGl : IGlInterop
{
    // Between BeginNative and EndNative: IGl calls that record or flush throw (GuardNative).
    bool nativeOpen, nativeInPass;
    CommandList? nativeList;
    (long Draws, long Pipelines, long Pushes) nativeStart;
    // A native host's own rendering instance is open (BeginHostPass): guest segments record into it (docs/renderer-native.md 4.5).
    CommandList? hostList;
    bool hostGuestOpen;
    // Wave 4: inside a host rendering with secondaries (GpuFrame.Parallel open), the secondary a guest recording at once has.
    CommandList? hostInline;
    // The GL draw framebuffer's attachments when the host pass began (a Clear inside the host must be of these).
    (Attachment? Colour, Attachment? Depth) hostTargets;

    /// <summary>Writes a timestamp where the frame's commands are going now: the primary, or inside a host's rendering with secondaries a
    /// secondary of its own in its place (a primary may record nothing but secondaries there).</summary>
    void WriteTimestamp(QueryPool pool, uint index)
    {
        if (hostList is not null && Context.Frame.Parallel.Open && !hostGuestOpen)
        {
            var list = Context.Frame.Parallel.BeginInline("timestamp");
            vk.CmdWriteTimestamp2(list.Handle, PipelineStageFlags2.AllCommandsBit, pool, index);
            Context.Frame.Parallel.EndInline(list);
            return;
        }
        if (hostInline is { } open) { vk.CmdWriteTimestamp2(open.Handle, PipelineStageFlags2.AllCommandsBit, pool, index); return; }
        vk.CmdWriteTimestamp2(cmd, PipelineStageFlags2.AllCommandsBit, pool, index);
    }

    void GuardNative()
    {
        if (nativeOpen)
            throw new InvalidOperationException("IGl call between BeginNative and EndNative: it could end the frame under the native command list");
    }

    /// <summary>For calls that put something into the frame without touching the pass (an upload into the upload command buffer, a
    /// timestamp): allowed while a native host's rendering is open, where only the host's own commands may touch the pass.</summary>
    void GuardNativePass()
    {
        if (nativeOpen && hostList is null)
            throw new InvalidOperationException("IGl call between BeginNative and EndNative: it could end the frame under the native command list");
    }

    public void SegmentOpening(CommandList list)
    {
        GuardNative();
        EndPass();
        nativeOpen = true;
        nativeInPass = false;
        nativeList = list;
    }

    public void SegmentClosed(CommandList list)
    {
        if (!nativeOpen || !ReferenceEquals(list, nativeList)) throw new InvalidOperationException("SegmentClosed without a matching SegmentOpening");
        nativeOpen = false;
        nativeList = null;
        // Whatever the native code bound is now in the command buffer, and its closing barrier is recorded: VkGl assumes nothing.
        lastPipeline = default;
        dynamicStateDirty = true;
        boundProgram = null;
        pushEpoch++;
        passActive = false;
        passColour = passDepth = null;
    }

    public CommandList BeginNative(string label)
    {
        GuardNative();
        EndPass();
        FullBarrier(Cmd);
        var list = Context.Frame.Commands;
        list.Invalidate();
        Context.Frame.States.AssumeFullBarrier();
        Context.Frame.Stats.NativeSegments++;
        list.BeginLabel(label);
        list.Log?.Note($"native {label}");
        nativeOpen = true;
        nativeList = list;
        var s = list.Stats;
        nativeStart = (s.Draws, s.PipelinesBound, s.DescriptorPushes);
        return list;
    }

    public CommandList BeginNativeInPass(string label)
    {
        if (hostList is { } host)
        {
            // A guest of a native host: the host's rendering instance is the pass; nothing of VkGl's is touched.
            if (hostGuestOpen) throw new InvalidOperationException("BeginNativeInPass inside a native segment");
            if (Context.Frame.Parallel.Open)
            {
                // The host's rendering takes secondaries (wave 4): a guest that records at once gets a secondary of its own on this thread,
                // executed in its place among the prepared segments.
                hostGuestOpen = true;
                return hostInline = Context.Frame.Parallel.BeginInline(label);
            }
            host.Invalidate();
            Context.Frame.Stats.NativeSegments++;
            host.BeginLabel(label);
            host.Log?.Note($"native {label} (in host pass)");
            hostGuestOpen = true;
            var hs = host.Stats;
            nativeStart = (hs.Draws, hs.PipelinesBound, hs.DescriptorPushes);
            return host;
        }
        GuardNative();
        // The pass a VkGl draw would draw into now: kept open, or begun (with its barrier) exactly as PrepareDraw begins it.
        EnsurePass();
        var list = Context.Frame.Commands;
        list.Invalidate();
        Context.Frame.Stats.NativeSegments++;
        list.BeginLabel(label);
        if (list.Log is { } log)
        {
            log.Note($"native {label} (in pass)");
            log.BeginRendering(new RenderingDesc(
                passColour is { } c ? new RenderTarget(AttachmentView(c.Texture, c.Level, c.Layer), Image: c.Texture.Image!.Image) : default,
                passDepth is { } d ? new RenderTarget(AttachmentView(d.Texture, d.Level, d.Layer), Image: d.Texture.Image!.Image) : default,
                passWidth, passHeight));
        }
        nativeOpen = true;
        nativeInPass = true;
        nativeList = list;
        var s = list.Stats;
        nativeStart = (s.Draws, s.PipelinesBound, s.DescriptorPushes);
        return list;
    }

    public void EndNative(CommandList cmd)
    {
        if (hostGuestOpen && hostInline is { } inline)
        {
            if (!ReferenceEquals(cmd, inline)) throw new InvalidOperationException("EndNative without a matching BeginNativeInPass");
            hostGuestOpen = false;
            hostInline = null;
            Context.Frame.Parallel.EndInline(cmd);   // its counters reach VkGl's with the pass's totals (EndHostPass)
            return;
        }
        if (hostGuestOpen)
        {
            if (!ReferenceEquals(cmd, hostList)) throw new InvalidOperationException("EndNative without a matching BeginNativeInPass");
            hostGuestOpen = false;
            cmd.EndLabel();
            var gs = cmd.Stats;
            Stats.Draws += gs.Draws - nativeStart.Draws;
            Stats.PipelineBinds += gs.PipelinesBound - nativeStart.Pipelines;
            Stats.DescriptorPushes += gs.DescriptorPushes - nativeStart.Pushes;
            cmd.Log?.Note("end native (in host pass)");
            return;   // the host's rendering goes on; VkGl's caches are invalidated when the host's own segment ends
        }
        if (!nativeOpen || !ReferenceEquals(cmd, nativeList)) throw new InvalidOperationException("EndNative without a matching BeginNative");
        cmd.EndLabel();
        // The segment's draws count in VkGl's totals too, so the pass meter and the stats line see them (the native side's own are in GpuStats).
        var s = cmd.Stats;
        Stats.Draws += s.Draws - nativeStart.Draws;
        Stats.PipelineBinds += s.PipelinesBound - nativeStart.Pipelines;
        Stats.DescriptorPushes += s.DescriptorPushes - nativeStart.Pushes;
        nativeOpen = false;
        nativeList = null;
        // Whatever the native code bound is now in the command buffer: VkGl assumes nothing (docs/renderer-native.md 4.1).
        lastPipeline = default;
        dynamicStateDirty = true;
        boundProgram = null;
        pushEpoch++;
        if (nativeInPass)
        {
            // Drawn inside VkGl's pass: the pass stays open for the draws that follow, in the same rendering instance (no barrier needed).
            nativeInPass = false;
        }
        else
        {
            FullBarrier(Cmd);
            passActive = false;
            passColour = passDepth = null;
        }
        cmd.Log?.Note("end native");
    }

    public void BeginHostPass(CommandList cmd)
    {
        if (!nativeOpen || nativeInPass || !ReferenceEquals(cmd, nativeList)) throw new InvalidOperationException("BeginHostPass needs the list of an open BeginNative segment");
        if (hostList is not null) throw new InvalidOperationException("a host pass is already open");
        hostList = cmd;
        hostTargets = DrawTargets();
        Stats.RenderPasses++;
    }

    public void EndHostPass(CommandList cmd)
    {
        if (hostList is null || !ReferenceEquals(cmd, hostList)) throw new InvalidOperationException("EndHostPass without a matching BeginHostPass");
        if (hostGuestOpen) throw new InvalidOperationException("EndHostPass with a guest segment open");
        hostList = null;
    }

    public void Interleave(Action<CommandList> record)
    {
        if (hostList is { } host && !hostGuestOpen)
        {
            // Inside a native host's rendering (wave 4: the scene's host spans the stage laps): into a secondary of its own when the rendering
            // takes secondaries, else into the host's list (a timestamp or a label does not disturb the pass).
            if (Context.Frame.Parallel.Open)
            {
                var list = Context.Frame.Parallel.BeginInline("interleave");
                record(list);
                Context.Frame.Parallel.EndInline(list);
            }
            else record(host);
            return;
        }
        GuardNative();
        _ = Cmd;   // a frame is open
        record(Context.Frame.Commands);
    }

    public PassTargets CurrentTargets()
    {
        var (colour, depth) = DrawTargets();
        var (w, h) = colour is { } c ? Size(c) : depth is { } d ? Size(d) : (1, 1);
        RenderTarget Target(Attachment? a) => a is { } x
            ? new RenderTarget(AttachmentView(x.Texture, x.Level, x.Layer), AttachmentLoadOp.Load, default, x.Texture.Image!.Image)
            : default;
        var formats = new AttachmentFormats(colour?.Texture.Format ?? Format.Undefined, depth?.Texture.Format ?? Format.Undefined,
            (colour ?? depth)?.Texture.Samples ?? 1);
        // As SetDynamicState: the viewport GL set (or the whole target), the scissor clipped to the target.
        (int X, int Y, int W, int H) vp = viewport.W > 0 ? viewport : (0, 0, w, h);
        (int X, int Y, int W, int H) sc = scissorTest ? scissor : (0, 0, w, h);
        int x0 = Math.Max(sc.X, 0), y0 = Math.Max(sc.Y, 0);
        int x1 = Math.Min(sc.X + sc.W, w), y1 = Math.Min(sc.Y + sc.H, h);
        return new PassTargets(Target(colour), Target(depth), formats, w, h, new Viewport(vp.X, vp.Y, vp.W, vp.H, 0, 1),
            new Rect2D(new Offset2D(x0, y0), new Extent2D((uint)Math.Max(x1 - x0, 0), (uint)Math.Max(y1 - y0, 0))));
    }

    public DrawState CurrentState()
    {
        var (colour, depth) = DrawTargets();
        int samples = (colour ?? depth)?.Texture.Samples ?? 1;
        // As PrepareDraw's pipeline key and SetDynamicState.
        var cull = !cullFace ? CullModeFlags.None : cullMode switch
        {
            TriangleFace.Front => CullModeFlags.FrontBit,
            TriangleFace.FrontAndBack => CullModeFlags.FrontAndBack,
            _ => CullModeFlags.BackBit,
        };
        bool test = depthTest && depth is not null;
        bool on = blend && colour is not null;
        var blendState = new BlendState(on, Factor(on ? blendSrc : BlendingFactor.One), Factor(on ? blendDst : BlendingFactor.Zero));
        var mask = (ColorComponentFlags)((colourMask.R ? 1 : 0) | (colourMask.G ? 2 : 0) | (colourMask.B ? 4 : 0) | (colourMask.A ? 8 : 0));
        bool line = polygonMode == Meitou.Rendering.Gpu.PolygonMode.Line;
        return new DrawState(cull, GlConventions.FrontFace(frontFace), test, test && depthWrite, Compare(depthFunc),
            line ? offsetLine : offsetFill, offsetUnits, offsetFactor, blendState, mask,
            line ? Silk.NET.Vulkan.PolygonMode.Line : Silk.NET.Vulkan.PolygonMode.Fill, alphaToCoverage && samples > 1, depthClamp && device.DepthClamp);
    }

    public VertexArrayBindings VertexArray(uint glVertexArray)
    {
        var vao = glVertexArray != 0 && vertexArrays.TryGetValue(glVertexArray, out var v) ? v : defaultVao;
        if (vao.Exported is { } exported && ExportStillValid(vao))
        {
            // The same object again (callers may keep what they derived from it, by reference), marked used as a draw would.
            foreach (var e in vao.ExportedBuffers) if (e is { Buffer: { } b, Defined: true }) Use(b);
            return exported;
        }
        return vao.Exported = Export(vao);
    }

    static bool ExportStillValid(GlVertexArray vao)
    {
        if (vao.Version != vao.ExportedVersion) return false;
        foreach (var e in vao.ExportedBuffers) if (!e.Still()) return false;
        return true;
    }

    VertexArrayBindings Export(GlVertexArray vao)
    {
        vao.ExportedVersion = vao.Version;
        List<ExportedBuffer> used = [];
        void Note(uint id)
        {
            foreach (var e in used) if (e.Id == id) return;
            used.Add(ExportedBuffer.Of(id, buffers.GetValueOrDefault(id)));
        }
        var attributes = new LegacyProgram.Attribute?[vao.Attribs.Length];
        for (int loc = 0; loc < attributes.Length; loc++)
        {
            ref var a = ref vao.Attribs[loc];
            if (!a.Enabled) continue;
            Note(a.Buffer);
            // As PrepareDraw: an enabled attribute without storage keeps its format and stride but reads the stand-in.
            BufferBinding binding = buffers.TryGetValue(a.Buffer, out var vb) && vb.Defined
                ? Use(vb) is var (bb, bo) ? new BufferBinding(bb, bo + (ulong)a.Offset) : default
                : new BufferBinding(defaults.DummyVertex.Buffer, 0);
            attributes[loc] = new LegacyProgram.Attribute(binding, AttribFormat(in a), a.Stride == 0 ? AttribBytes(in a) : a.Stride, a.Divisor != 0);
        }
        BufferBinding elements = default;
        Note(vao.ElementBuffer);
        if (buffers.TryGetValue(vao.ElementBuffer, out var eb) && eb.Defined)
        {
            var (buffer, offset) = Use(eb);
            elements = new BufferBinding(buffer, offset, (ulong)eb.Size);
        }
        vao.ExportedBuffers = [.. used];
        foreach (var e in used) if (e.Buffer is { } b) b.Exported = true;
        return new VertexArrayBindings(attributes, elements);
    }

    long exportStamp = 1;

    public long VertexArrayStamp => exportStamp;

    // ---- export ----

    GlTextureObj Stored(uint glTexture) =>
        textures.TryGetValue(glTexture, out var t) && t.Image is not null ? t : throw new InvalidOperationException($"texture {glTexture} has no storage");

    public Texture Texture(uint glTexture)
    {
        var t = Stored(glTexture);
        if (t.Exported is { } e) return e;
        var kind = t.IsCube ? TextureKind.Cube : t.IsArray ? TextureKind.Texture2DArray : TextureKind.Texture2D;
        var desc = new TextureDesc(t.Format, t.Width, t.Height, t.Levels, t.IsCube ? 1 : t.Layers, t.Samples, kind, Name: $"gl texture {t.Id}");
        return t.Exported = Gpu.Texture.Borrow(device, desc, t.Image!.Image, (level, _, layer, _) => AttachmentView(t, level, layer));
    }

    static SamplerInfo SamplerOf(GlTextureObj? t, bool shadow) =>
        new("", 0, 0, t is { IsCube: true } ? SamplerDimension.Cube : SamplerDimension.Dim2D, t is { IsArray: true }, false, shadow,
            t is null ? ScalarKind.Float : t.Format is Format.R32Uint ? ScalarKind.UInt : t.Format is Format.R32Sint ? ScalarKind.Int : ScalarKind.Float, 0);

    public SampledTexture Sampled(uint glTexture, SamplerInfo sampler)
    {
        var t = glTexture != 0 && textures.TryGetValue(glTexture, out var x) && x.Image is not null && (x.IsDepth || !sampler.Depth) ? x : SamplerTexture(sampler, -1);
        var (s, v) = SamplerAndView(t, sampler.Depth);
        return new SampledTexture(s, v, t.Image!.Image);
    }

    public SampledTexture Sampled(uint glTexture, bool shadowSampler) =>
        Sampled(glTexture, SamplerOf(textures.GetValueOrDefault(glTexture), shadowSampler));

    public BindlessHandle Bindless(uint glTexture, bool shadowSampler = false)
    {
        var known = glTexture != 0 ? textures.GetValueOrDefault(glTexture) : null;
        var t = known is { Image: not null } && (known.IsDepth || !shadowSampler) ? known : SamplerTexture(SamplerOf(known, shadowSampler), -1);
        var kind = BindlessTable.KindFor(t.Format, t.IsCube ? TextureKind.Cube : t.IsArray ? TextureKind.Texture2DArray : TextureKind.Texture2D, shadowSampler);
        var (s, v) = SamplerAndView(t, shadowSampler);
        var sampled = new SampledTexture(s, v, t.Image!.Image);
        var entry = shadowSampler ? t.BindlessShadow : t.BindlessPlain;
        if (entry is { } e && e.Handle.Kind == kind && e.Texture == sampled) return e.Handle;
        // Changed (view, sampler, LOD bias) or new: a new index, so draws recorded earlier in the frame keep what they were given; the old
        // index is freed after the frames in flight.
        if (entry is { } old) Context.Bindless.Free(old.Handle);
        var handle = new BindlessHandle(kind, Context.Bindless.Register(kind, sampled));
        if (shadowSampler) t.BindlessShadow = (handle, sampled); else t.BindlessPlain = (handle, sampled);
        return handle;
    }

    public SampledTexture SampledUnit(int unit, SamplerInfo sampler)
    {
        var t = SamplerTexture(sampler, unit);
        var (s, v) = SamplerAndView(t, sampler.Depth);
        return new SampledTexture(s, v, t.Image!.Image);
    }

    public BufferBinding Buffer(uint glBuffer)
    {
        var b = buffers.TryGetValue(glBuffer, out var x) && x.Defined ? x : throw new InvalidOperationException($"buffer {glBuffer} has no storage");
        var (buffer, offset) = Use(b);
        return new BufferBinding(buffer, offset, (ulong)b.Size);
    }

    public BufferBinding UniformBinding(uint index) => Buffer(uniformBindings[index]);

    // ---- import ----

    public uint Import(Texture texture)
    {
        var image = texture.Underlying ?? throw new ArgumentException("only an owned native texture can be imported (an exported one has its GL name)");
        var d = texture.Desc;
        uint id = GenTexture();
        var t = textures[id];
        t.Target = d.Kind switch
        {
            TextureKind.Cube => TextureTarget.TextureCubeMap,
            TextureKind.Texture2DArray => TextureTarget.Texture2DArray,
            _ => TextureTarget.Texture2D,
        };
        t.Format = d.Format;
        t.Width = d.Width;
        t.Height = d.Height;
        t.Layers = d.Kind == TextureKind.Cube ? 6 * Math.Max(d.Layers, 1) : d.Layers;
        t.Levels = d.Levels;
        t.Samples = d.Samples;
        t.DefinedLevels = d.Levels >= 32 ? uint.MaxValue : (1u << d.Levels) - 1;
        t.Image = image;
        t.Borrowed = true;
        return id;
    }

    public uint ImportBuffer(DeviceBuffer buffer)
    {
        uint id = GenBuffer();
        var b = buffers[id];
        b.Size = (long)buffer.Size;
        b.Device = buffer.Underlying;
        b.Borrowed = true;
        return id;
    }
}
