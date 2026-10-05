using System.Runtime.InteropServices;
using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Vulkan.Shaders;
using Silk.NET.OpenGL;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using GlPolygonMode = Silk.NET.OpenGL.PolygonMode;
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
    GpuBuffer? dummyVertex;
    readonly Dictionary<(int Kind, bool Depth), GlTextureObj> dummyTextures = [];
    readonly Dictionary<SamplerKey, Sampler> samplers = [];


    // Dynamic state as last recorded into the current command buffer.
    (int X, int Y, int W, int H) sentViewport, sentScissor;
    CullModeFlags sentCull;
    Silk.NET.Vulkan.FrontFace sentFront;
    bool sentDepthTest, sentDepthWrite, sentBiasEnable;
    CompareOp sentCompare;
    (float, float) sentBias;

    void InitDummies()
    {
        // Disabled attributes read GL's default generic value (0, 0, 0, 1): float at offset 0, int at 16.
        dummyVertex = device.Allocator.CreateBuffer(32, BufferUsageFlags.VertexBufferBit, MemoryKind.Upload, "dummy vertex");
        var p = (float*)dummyVertex.Mapped;
        p[0] = p[1] = p[2] = 0; p[3] = 1;
        var ip = (int*)dummyVertex.Mapped + 4;
        ip[0] = ip[1] = ip[2] = 0; ip[3] = 1;
    }

    void DestroyDummies()
    {
        if (dummyVertex is not null) device.Allocator.Free(dummyVertex);
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
        vk.CmdDraw(cmd, count, instancecount, (uint)first, 0);
        Stats.Draws++;
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
        vk.CmdBindIndexBuffer(cmd, buffer, offset + (ulong)(nint)indices, indexType);
        vk.CmdDrawIndexed(cmd, count, instancecount, 0, 0, 0);
        Stats.Draws++;
    }

    /// <summary>Records everything a draw needs: the pass, the pipeline, dynamic state, descriptors and vertex buffers. False when
    /// nothing can be drawn (no program, or a program that failed to link).</summary>
    bool PrepareDraw(PrimitiveType mode)
    {
        if (program is not { Linked: true } p) return false;
        EnsurePass();
        var cb = cmd;
        var vao = CurrentVao;

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
            mode == PrimitiveType.TriangleStrip ? PrimitiveTopology.TriangleStrip : mode switch
            {
                PrimitiveType.Triangles => PrimitiveTopology.TriangleList,
                PrimitiveType.Lines => PrimitiveTopology.LineList,
                PrimitiveType.LineStrip => PrimitiveTopology.LineStrip,
                PrimitiveType.Points => PrimitiveTopology.PointList,
                PrimitiveType.TriangleFan => PrimitiveTopology.TriangleFan,
                _ => throw new NotSupportedException($"primitive {mode}"),
            },
            passColour?.Texture.Format ?? Format.Undefined, passDepth?.Texture.Format ?? Format.Undefined,
            (passColour ?? passDepth)?.Texture.Samples ?? 1,
            blend && passColour is not null, blend ? blendSrc : BlendingFactor.One, blend ? blendDst : BlendingFactor.Zero,
            (byte)((colourMask.R ? 1 : 0) | (colourMask.G ? 2 : 0) | (colourMask.B ? 4 : 0) | (colourMask.A ? 8 : 0)),
            polygonMode, alphaToCoverage && ((passColour ?? passDepth)?.Texture.Samples ?? 1) > 1, depthClamp && device.DepthClamp);
        if (!pipelines.TryGetValue(key, out var pipeline))
        {
            pipeline = CreatePipeline(p, in key, vao);
            pipelines[key] = pipeline;
            Stats.PipelinesCreated++;
        }
        if (pipeline.Handle != lastPipeline.Handle)
        {
            vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
            lastPipeline = pipeline;
        }

        SetDynamicState(cb);
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
            else (buffer, offset) = (dummyVertex!.Buffer, p.InputKinds[i] == ScalarKind.Float ? 0ul : 16ul);
            vk.CmdBindVertexBuffers(cb, (uint)loc, 1, &buffer, &offset);
        }
        return true;
    }

    static Format DummyFormat(ScalarKind kind) => kind switch
    {
        ScalarKind.Int => Format.R32G32B32A32Sint,
        ScalarKind.UInt or ScalarKind.Bool => Format.R32G32B32A32Uint,
        _ => Format.R32G32B32A32Sfloat,
    };

    Pipeline CreatePipeline(GlProgramObj p, in PipelineKey key, GlVertexArray vao)
    {
        var entry = "main"u8;
        var locs = p.InputLocations;
        int n = locs.Length;
        var vbind = stackalloc VertexInputBindingDescription[Math.Max(n, 1)];
        var vattr = stackalloc VertexInputAttributeDescription[Math.Max(n, 1)];
        var divisors = stackalloc VertexInputBindingDivisorDescriptionEXT[Math.Max(n, 1)];
        Span<ulong> packed = [key.V0, key.V1, key.V2, key.V3, key.V4, key.V5, key.V6, key.V7];
        for (int i = 0; i < n; i++)
        {
            int loc = locs[i];
            uint e = (uint)(packed[loc >> 1] >> ((loc & 1) * 32));
            bool dummy = (e & (1u << 30)) != 0;
            var format = (Format)(e & 0xFFF);
            uint stride = dummy ? 0 : (e >> 12) & 0xFFFF;
            bool instanced = !dummy && ((e >> 28) & 1) != 0;
            vbind[i] = new VertexInputBindingDescription((uint)loc, stride, instanced ? VertexInputRate.Instance : VertexInputRate.Vertex);
            vattr[i] = new VertexInputAttributeDescription((uint)loc, (uint)loc, format, 0);
        }
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = (uint)n, PVertexBindingDescriptions = vbind,
            VertexAttributeDescriptionCount = (uint)n, PVertexAttributeDescriptions = vattr,
        };
        var assembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = key.Topology };
        var clip = new PipelineViewportDepthClipControlCreateInfoEXT { SType = StructureType.PipelineViewportDepthClipControlCreateInfoExt, NegativeOneToOne = true };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
            PNext = device.HasDepthClipControl ? &clip : null,
        };
        var raster = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = key.DepthClamp,
            PolygonMode = key.Polygon == GlPolygonMode.Line && device.FillModeNonSolid ? Silk.NET.Vulkan.PolygonMode.Line : Silk.NET.Vulkan.PolygonMode.Fill,
            LineWidth = 1,
        };
        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = (SampleCountFlags)key.Samples,
            AlphaToCoverageEnable = key.AlphaToCoverage,
        };
        var depthStencil = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
        var attachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = key.Blend,
            SrcColorBlendFactor = Factor(key.Src), DstColorBlendFactor = Factor(key.Dst), ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = Factor(key.Src), DstAlphaBlendFactor = Factor(key.Dst), AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = (ColorComponentFlags)key.Mask,
        };
        var blendState = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = key.Colour == Format.Undefined ? 0u : 1u, PAttachments = &attachment,
        };
        var dynamics = stackalloc DynamicState[]
        {
            DynamicState.Viewport, DynamicState.Scissor, DynamicState.DepthBias, DynamicState.CullMode, DynamicState.FrontFace,
            DynamicState.DepthTestEnable, DynamicState.DepthWriteEnable, DynamicState.DepthCompareOp, DynamicState.DepthBiasEnable,
        };
        var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 9, PDynamicStates = dynamics };
        var colourFormat = key.Colour;
        bool stencil = key.Depth is Format.D24UnormS8Uint or Format.D32SfloatS8Uint;
        var rendering = new PipelineRenderingCreateInfo
        {
            SType = StructureType.PipelineRenderingCreateInfo,
            ColorAttachmentCount = colourFormat == Format.Undefined ? 0u : 1u, PColorAttachmentFormats = &colourFormat,
            DepthAttachmentFormat = key.Depth, StencilAttachmentFormat = stencil ? key.Depth : Format.Undefined,
        };
        fixed (byte* name = entry)
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = p.VertexModule, PName = name };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = p.FragmentModule, PName = name };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2, PStages = stages,
                PVertexInputState = &vertexInput, PInputAssemblyState = &assembly, PViewportState = &viewportState,
                PRasterizationState = &raster, PMultisampleState = &multisample, PDepthStencilState = &depthStencil,
                PColorBlendState = &blendState, PDynamicState = &dynamic, Layout = p.Layout,
            };
            Check(vk.CreateGraphicsPipelines(dev, device.PipelineCache, 1, &info, null, out var pipeline));
            return pipeline;
        }
    }

    static BlendFactor Factor(BlendingFactor f) => f switch
    {
        BlendingFactor.Zero => BlendFactor.Zero,
        BlendingFactor.One => BlendFactor.One,
        BlendingFactor.SrcColor => BlendFactor.SrcColor,
        BlendingFactor.OneMinusSrcColor => BlendFactor.OneMinusSrcColor,
        BlendingFactor.DstColor => BlendFactor.DstColor,
        BlendingFactor.OneMinusDstColor => BlendFactor.OneMinusDstColor,
        BlendingFactor.SrcAlpha => BlendFactor.SrcAlpha,
        BlendingFactor.OneMinusSrcAlpha => BlendFactor.OneMinusSrcAlpha,
        BlendingFactor.DstAlpha => BlendFactor.DstAlpha,
        BlendingFactor.OneMinusDstAlpha => BlendFactor.OneMinusDstAlpha,
        _ => throw new NotSupportedException($"blend factor {f}"),
    };

    static CompareOp Compare(DepthFunction f) => f switch
    {
        DepthFunction.Never => CompareOp.Never,
        DepthFunction.Less => CompareOp.Less,
        DepthFunction.Equal => CompareOp.Equal,
        DepthFunction.Lequal => CompareOp.LessOrEqual,
        DepthFunction.Greater => CompareOp.Greater,
        DepthFunction.Notequal => CompareOp.NotEqual,
        DepthFunction.Gequal => CompareOp.GreaterOrEqual,
        _ => CompareOp.Always,
    };

    void SetDynamicState(CommandBuffer cb)
    {
        bool all = dynamicStateDirty;
        dynamicStateDirty = false;
        (int X, int Y, int W, int H) vp = viewport.W > 0 ? viewport : (0, 0, passWidth, passHeight);
        if (all || vp != sentViewport)
        {
            var v = new Viewport(vp.X, vp.Y, vp.W, vp.H, 0, 1);
            vk.CmdSetViewport(cb, 0, 1, &v);
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
            vk.CmdSetScissor(cb, 0, 1, &r);
            sentScissor = clipped;
        }
        var cull = !cullFace ? CullModeFlags.None : cullMode switch
        {
            TriangleFace.Front => CullModeFlags.FrontBit,
            TriangleFace.FrontAndBack => CullModeFlags.FrontAndBack,
            _ => CullModeFlags.BackBit,
        };
        if (all || cull != sentCull) { vk.CmdSetCullMode(cb, cull); sentCull = cull; }
        // GL's counter-clockwise is Vulkan's clockwise: same pixel rows, opposite sign convention for the area.
        var front = frontFace == FrontFaceDirection.Ccw ? Silk.NET.Vulkan.FrontFace.Clockwise : Silk.NET.Vulkan.FrontFace.CounterClockwise;
        if (all || front != sentFront) { vk.CmdSetFrontFace(cb, front); sentFront = front; }
        bool test = depthTest && passDepth is not null;
        // GL writes depth only while the depth test is on.
        bool write = test && depthWrite;
        if (all || test != sentDepthTest) { vk.CmdSetDepthTestEnable(cb, test); sentDepthTest = test; }
        if (all || write != sentDepthWrite) { vk.CmdSetDepthWriteEnable(cb, write); sentDepthWrite = write; }
        var cmp = Compare(depthFunc);
        if (all || cmp != sentCompare) { vk.CmdSetDepthCompareOp(cb, cmp); sentCompare = cmp; }
        bool bias = polygonMode == GlPolygonMode.Line ? offsetLine : offsetFill;
        if (all || bias != sentBiasEnable) { vk.CmdSetDepthBiasEnable(cb, bias); sentBiasEnable = bias; }
        if (all || (offsetUnits, offsetFactor) != sentBias) { vk.CmdSetDepthBias(cb, offsetUnits, 0, offsetFactor); sentBias = (offsetUnits, offsetFactor); }
    }

    // ----- Descriptors -----

    void BindResources(CommandBuffer cb, GlProgramObj p)
    {
        var blocks = p.BlockList;
        var samplerList = p.SamplerList;
        int count = blocks.Length + samplerList.Length;
        if (count == 0) return;
        var writes = stackalloc WriteDescriptorSet[count];
        var bufferInfos = stackalloc DescriptorBufferInfo[blocks.Length + 1];
        var imageInfos = stackalloc DescriptorImageInfo[samplerList.Length + 1];
        int w = 0;

        // Loose uniforms: copied into the frame ring when they changed (or the frame moved on).
        long frame = device.Frames.FrameNumber;
        if (p.SliceFrame != frame) { p.VertexDirty = p.FragmentDirty = true; p.SliceFrame = frame; }
        if (p.VertexDirty && p.VertexDefault.Length > 0) { p.VertexSlice = CopyToRing(p.VertexDefault, uniformAlign); p.VertexDirty = false; }
        if (p.FragmentDirty && p.FragmentDefault.Length > 0) { p.FragmentSlice = CopyToRing(p.FragmentDefault, uniformAlign); p.FragmentDirty = false; }

        for (int i = 0; i < blocks.Length; i++)
        {
            ref readonly var b = ref blocks[i];
            if (b.Default)
            {
                var slice = b.Vertex ? p.VertexSlice : p.FragmentSlice;
                bufferInfos[i] = new DescriptorBufferInfo(slice.Buffer, slice.Offset, (ulong)b.Size);
            }
            else
            {
                uint point = p.BlockBindings.TryGetValue(b.Name, out var pt) ? pt : 0;
                if (!buffers.TryGetValue(uniformBindings[point], out var ub) || !ub.Defined)
                    throw new InvalidOperationException($"program {p.Id}: no buffer bound for uniform block {b.Name} (binding {point})");
                var (buf, off) = Use(ub);
                bufferInfos[i] = new DescriptorBufferInfo(buf, off, (ulong)Math.Min(b.Size, ub.Size));
            }
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
            writes[w++] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstBinding = s.Binding, DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &imageInfos[i],
            };
        }

        if (p.PushDescriptors)
        {
            // The extension entry point: vkCmdPushDescriptorSet is only core from Vulkan 1.4.
            if (pushDescriptor is null && !vk.TryGetDeviceExtension(device.Instance, dev, out pushDescriptor))
                throw new InvalidOperationException("VK_KHR_push_descriptor entry points missing");
            pushDescriptor!.CmdPushDescriptorSet(cb, PipelineBindPoint.Graphics, p.Layout, 0, (uint)w, writes);
            return;
        }
        var set = AllocateSet(p.SetLayout);
        for (int i = 0; i < w; i++) writes[i].DstSet = set;
        vk.UpdateDescriptorSets(dev, (uint)w, writes, 0, null);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, p.Layout, 0, 1, &set, 0, null);
    }

    ulong uniformAlign => Math.Max(device.Limits.MinUniformBufferOffsetAlignment, 16);

    /// <summary>The sampler and view a texture samples with, cached on the texture until its state changes.</summary>
    (Sampler, ImageView) SamplerAndView(GlTextureObj t, bool shadow)
    {
        if (t.CachedVersion == t.Version && t.CachedShadow == shadow && t.CachedLevels == t.DefinedLevels && t.CachedImage == t.Image)
            return (t.CachedSampler, t.CachedView);
        t.CachedSampler = SamplerFor(t, shadow);
        t.CachedView = SampleView(t);
        (t.CachedVersion, t.CachedShadow, t.CachedLevels, t.CachedImage) = (t.Version, shadow, t.DefinedLevels, t.Image);
        return (t.CachedSampler, t.CachedView);
    }

    RingSlice CopyToRing(byte[] data, ulong align)
    {
        var slice = Ring.Allocate((ulong)data.Length, align);
        fixed (byte* src = data) System.Buffer.MemoryCopy(src, slice.Pointer, data.Length, data.Length);
        Stats.UniformBytes += data.Length;
        return slice;
    }

    /// <summary>The texture bound to <paramref name="unit"/> for the sampler's type, or a 1×1 stand-in (GL reads incomplete or
    /// missing textures as (0, 0, 0, 1)).</summary>
    GlTextureObj SamplerTexture(SamplerInfo s, int unit)
    {
        int slot = s.Dimension == SamplerDimension.Cube ? 2 : s.Arrayed ? 1 : 0;
        if (unit >= 0 && unit < units.GetLength(0) && units[unit, slot] is { Image: not null } t && t.IsDepth == s.Depth) return t;
        int kind = slot * 4 + (int)s.SampledKind;
        if (!dummyTextures.TryGetValue((kind, s.Depth), out var d))
        {
            d = CreateDummyTexture(slot, s.SampledKind, s.Depth);
            dummyTextures[(kind, s.Depth)] = d;
        }
        return d;
    }

    GlTextureObj CreateDummyTexture(int slot, ScalarKind kind, bool depth)
    {
        var format = depth ? Format.D32Sfloat : kind switch
        {
            ScalarKind.UInt => Format.R32Uint,
            ScalarKind.Int => Format.R32Sint,
            _ => Format.R8G8B8A8Unorm,
        };
        int layers = slot == 2 ? 6 : 1;
        var t = new GlTextureObj(0)
        {
            Format = format, Width = 1, Height = 1, Layers = layers, Levels = 1, DefinedLevels = 1,
            Target = slot == 2 ? TextureTarget.TextureCubeMap : slot == 1 ? TextureTarget.Texture2DArray : TextureTarget.Texture2D,
            MinFilter = TextureMinFilter.Nearest, MagFilter = TextureMagFilter.Nearest,
        };
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = format, Extent = new Extent3D(1, 1, 1),
            MipLevels = 1, ArrayLayers = (uint)layers, Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, SharingMode = SharingMode.Exclusive,
            Flags = slot == 2 ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
        };
        t.Image = device.Allocator.CreateImage(in info, MemoryKind.DeviceLocal, "dummy texture");
        var cb = device.BeginImmediate();
        ToGeneral(cb, t);
        var range = new ImageSubresourceRange(depth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit, 0, 1, 0, (uint)layers);
        if (depth)
        {
            var value = new ClearDepthStencilValue(1, 0);
            vk.CmdClearDepthStencilImage(cb, t.Image.Image, ImageLayout.General, &value, 1, &range);
        }
        else
        {
            var value = kind is ScalarKind.UInt or ScalarKind.Int ? new ClearColorValue(uint32_0: 0, uint32_1: 0, uint32_2: 0, uint32_3: 1) : new ClearColorValue(0f, 0f, 0f, 1f);
            vk.CmdClearColorImage(cb, t.Image.Image, ImageLayout.General, &value, 1, &range);
        }
        device.EndImmediate(cb);
        return t;
    }

    internal record struct SamplerKey(Filter Min, Filter Mag, SamplerMipmapMode Mip, bool Mipmapped, SamplerAddressMode U, SamplerAddressMode V, SamplerAddressMode W,
        bool Compare, CompareOp Op, bool TransparentBorder, float Anisotropy);

    Sampler SamplerFor(GlTextureObj t, bool shadow)
    {
        bool integer = t.IsInteger || t.Format is Format.R32Sint;
        var (min, mip, mipmapped) = t.MinFilter switch
        {
            TextureMinFilter.Nearest => (Filter.Nearest, SamplerMipmapMode.Nearest, false),
            TextureMinFilter.Linear => (Filter.Linear, SamplerMipmapMode.Nearest, false),
            TextureMinFilter.NearestMipmapNearest => (Filter.Nearest, SamplerMipmapMode.Nearest, true),
            TextureMinFilter.LinearMipmapNearest => (Filter.Linear, SamplerMipmapMode.Nearest, true),
            TextureMinFilter.NearestMipmapLinear => (Filter.Nearest, SamplerMipmapMode.Linear, true),
            _ => (Filter.Linear, SamplerMipmapMode.Linear, true),
        };
        var mag = t.MagFilter == TextureMagFilter.Nearest ? Filter.Nearest : Filter.Linear;
        if (integer) (min, mag, mip) = (Filter.Nearest, Filter.Nearest, SamplerMipmapMode.Nearest);
        var key = new SamplerKey(min, mag, mip, mipmapped, Wrap(t.WrapS), Wrap(t.WrapT), Wrap(t.WrapR),
            shadow && t.Compare, Compare(t.CompareFunc), t.TransparentBorder, device.SamplerAnisotropy ? Math.Clamp(t.Anisotropy, 1, 16) : 1);
        if (samplers.TryGetValue(key, out var sampler)) return sampler;
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MinFilter = key.Min, MagFilter = key.Mag, MipmapMode = key.Mip,
            AddressModeU = key.U, AddressModeV = key.V, AddressModeW = key.W,
            AnisotropyEnable = key.Anisotropy > 1, MaxAnisotropy = key.Anisotropy,
            CompareEnable = key.Compare, CompareOp = key.Op,
            MinLod = 0, MaxLod = key.Mipmapped ? Vk.LodClampNone : 0.25f,
            // NVIDIA's OpenGL picks mips a quarter level finer than its Vulkan driver with anisotropic filtering on: matched here
            // (docs/engine.md "Vulkan backend"; measured 1.2 -> 0.001 mean difference on the rock view).
            MipLodBias = key.Anisotropy > 1 && device.Properties.VendorID == 0x10DE ? -0.25f : 0,
            BorderColor = integer ? (key.TransparentBorder ? BorderColor.IntTransparentBlack : BorderColor.IntOpaqueBlack)
                : key.TransparentBorder ? BorderColor.FloatTransparentBlack : BorderColor.FloatOpaqueWhite,
        };
        Check(vk.CreateSampler(dev, &info, null, out sampler));
        samplers[key] = sampler;
        return sampler;
    }

    static SamplerAddressMode Wrap(TextureWrapMode m) => m switch
    {
        TextureWrapMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
        TextureWrapMode.ClampToBorder => SamplerAddressMode.ClampToBorder,
        TextureWrapMode.MirroredRepeat => SamplerAddressMode.MirroredRepeat,
        _ => SamplerAddressMode.Repeat,
    };

    void DestroySamplers()
    {
        foreach (var s in samplers.Values) vk.DestroySampler(dev, s, null);
        samplers.Clear();
        foreach (var pool in descriptorPools) vk.DestroyDescriptorPool(dev, pool, null);
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
