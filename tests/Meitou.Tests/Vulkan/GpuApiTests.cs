using System.Reflection;

using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

/// <summary>The native renderer API (docs/renderer-native.md 2 and 3.2) next to VkGl: same pixels, same SPIR-V, its bookkeeping.</summary>
public class GpuApiTests
{
    static VulkanDevice? TryCreate(bool sync = false)
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = sync });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    static void ExpectClean(VulkanDevice d) =>
        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));

    const int W = 64, H = 32;

    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec3 aColour;
        uniform vec2 uOffset;
        out vec3 vColour;
        void main() { vColour = aColour; gl_Position = vec4(aPos + uOffset, 0.5, 1.0); }
        """;

    const string Fragment = """
        #version 330 core
        in vec3 vColour;
        uniform float uScale;
        out vec4 fragColour;
        void main() { fragColour = vec4(vColour * uScale, 1.0); }
        """;

    // A triangle with a colour per corner, so interpolation and rasterisation rules both show in the pixels.
    static readonly float[] Vertices =
    [
        -0.9f, -0.8f, 1, 0, 0,
         0.7f, -0.6f, 0, 1, 0,
        -0.2f,  0.9f, 0, 0, 1,
    ];

    static RenderingDesc Target(Texture t, AttachmentLoadOp load = AttachmentLoadOp.Clear) =>
        new(new RenderTarget(t.Attachment(), load, new ClearValue(new ClearColorValue(0f, 0f, 0f, 1f)), t.Image), default, t.Desc.Width, t.Desc.Height);

    static void FullTargetState(CommandList cmd, int w, int h)
    {
        cmd.SetViewport(new Viewport(0, 0, w, h, 0, 1));
        cmd.SetScissor(new Rect2D(new Offset2D(0, 0), new Extent2D((uint)w, (uint)h)));
        cmd.SetRaster(CullModeFlags.None, FrontFace.Clockwise);
        cmd.SetDepth(false, false, CompareOp.Always);
        cmd.SetDepthBias(false, 0, 0);
    }

    [Fact]
    [Slow]
    public unsafe void Legacy_program_draws_the_same_bytes_as_VkGl()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            var ctx = gl.Context;

            // VkGl: the triangle into an RGBA8 renderbuffer.
            uint fbo = gl.GenFramebuffer(), colour = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colour);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, W, H);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, colour);
            gl.Viewport(0, 0, W, H);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            uint program = WorldGl.Program(gl, Vertex, Fragment);
            uint vao = gl.GenVertexArray(), vbo = gl.GenBuffer();
            gl.BindVertexArray(vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, Vertices, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 20, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 20, (void*)8);
            gl.UseProgram(program);
            gl.Uniform2(gl.GetUniformLocation(program, "uOffset"), 0.1f, -0.05f);
            gl.Uniform1(gl.GetUniformLocation(program, "uScale"), 0.75f);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            var expected = new byte[W * H * 4];
            gl.ReadPixels<byte>(0, 0, W, H, PixelFormat.Rgba, PixelType.UnsignedByte, expected.AsSpan());

            // Native: the same sources through LegacyProgram into a texture of the same format.
            using var target = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, W, H, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "golden"));
            using var lp = LegacyProgram.Create(ctx, Vertex, Fragment, "golden triangle");
            Assert.True(lp.Uniform("uMissing") is { IsValid: false });
            lp.Set(lp.Uniform("uOffset"), 0.1f, -0.05f);
            lp.Set(lp.Uniform("uScale"), 0.75f);

            gl.BeginExternal();
            var frame = ctx.Frame;
            var cmd = frame.Commands;
            cmd.Invalidate();
            var vertices = frame.Constants.Write<float>(Vertices);
            LegacyProgram.Attribute?[] attributes =
            [
                new LegacyProgram.Attribute(vertices.Binding, Format.R32G32Sfloat, 20, false),
                new LegacyProgram.Attribute(new BufferBinding(vertices.Handle, vertices.Offset + 8), Format.R32G32B32Sfloat, 20, false),
            ];
            var pipeline = ctx.Pipelines.Get(new GraphicsPipelineDesc(lp.Program, lp.VertexLayout(attributes), PrimitiveTopology.TriangleList,
                new AttachmentFormats(Format.R8G8B8A8Unorm, Format.Undefined), BlendState.Off,
                ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit, Silk.NET.Vulkan.PolygonMode.Fill, false, false));
            cmd.BeginRendering(Target(target));
            cmd.BindPipeline(pipeline);
            FullTargetState(cmd, W, H);
            lp.BindVertices(cmd, attributes);
            lp.Flush(cmd);
            cmd.Draw(3);
            cmd.EndRendering();
            gl.EndExternal();
            gl.Finish();
            var actual = ctx.ReadBack(target, 4);

            Assert.Contains(expected, b => b != 0 && b != 255);   // the triangle is there, interpolated
            Assert.Equal(expected, actual);
        }
        ExpectClean(d!);
    }

    static string Field(Type t, string name) =>
        (string)(t.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
            ?? throw new MissingFieldException(t.Name, name));

    /// <summary>Every source pair the world renderers link (WorldGl.Program call sites).</summary>
    internal static IEnumerable<(string Name, string Vertex, string Fragment)> WorldPrograms()
    {
        string sky = Field(typeof(SkyRenderer), "Vertex");
        yield return ("sky simple", sky, Field(typeof(SkyRenderer), "SimpleFragment"));
        yield return ("sky", sky, Field(typeof(SkyRenderer), "SkyFragment"));
        yield return ("water", Field(typeof(WaterRenderer), "Vertex"), Field(typeof(WaterRenderer), "Fragment"));
        yield return ("debug overlay", Field(typeof(DebugOverlay), "Vertex"), Field(typeof(DebugOverlay), "Fragment"));
        yield return ("terrain patch", TerrainShaders.PatchVertex, TerrainShaders.Fragment);
        yield return ("terrain mesh", TerrainShaders.MeshVertex, TerrainShaders.MeshFragment);
        yield return ("terrain patch depth", TerrainShaders.PatchVertex, ShadowShaders.DepthFragment);
        yield return ("terrain mesh depth", TerrainShaders.MeshInstancedDepthVertex, ShadowShaders.DepthFragment);
        yield return ("foliage mesh", FoliageShaders.MeshVertex(), FoliageShaders.MeshFragment());
        yield return ("foliage grass", FoliageShaders.GrassVertex, FoliageShaders.GrassFragment);
        yield return ("foliage grass motion", FoliageShaders.GrassMotionVertex, FoliageShaders.GrassMotionFragment);
        yield return ("foliage depth", FoliageShaders.MeshVertex(), ShadowShaders.MeshDepthFragment);
        yield return ("buildings", BuildingLodShaders.Vertex(), BuildingLodShaders.Fragment());
        yield return ("buildings depth", BuildingLodShaders.Vertex(), ShadowShaders.MeshDepthFragment);
        yield return ("shadow debug", ShadowShaders.FullscreenVertex, ShadowShaders.DebugFragment);
        yield return ("shadow atlas", ShadowShaders.FullscreenVertex, ShadowShaders.AtlasFragment);
        yield return ("shadow blocker", ShadowShaders.FullscreenVertex, MeitouShadowShaders.BlockerFragment);
        yield return ("terrain shadow sweep", ShadowShaders.FullscreenVertex, MeitouShadowShaders.SweepFragment);
        foreach (var (name, f) in new[]
        {
            ("ssao", PostProcessShaders.Ssao), ("ssao blur", PostProcessShaders.SsaoBlur), ("composite", PostProcessShaders.Composite),
            ("fxaa", PostProcessShaders.Fxaa), ("heat haze", PostProcessShaders.HeatHaze), ("luminance", PostProcessShaders.Luminance),
            ("adapt", PostProcessShaders.Adapt), ("velocity", UpscaleShaders.Velocity), ("taa", UpscaleShaders.Taa),
        })
            yield return (name, PostProcessShaders.Vertex, f);
    }

    [Fact]
    [Slow]
    public void Legacy_programs_get_VkGl_SPIR_V_for_every_world_program()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            int n = 0;
            foreach (var (name, v, f) in WorldPrograms())
            {
                uint program = WorldGl.Program(gl, v, f);
                var (vkVertex, vkFragment) = gl.ModuleCode(program);
                using var lp = LegacyProgram.Create(gl.Context, v, f, name);
                Assert.True(vkVertex.AsSpan().SequenceEqual(lp.Program.VertexCode), $"{name}: vertex SPIR-V differs");
                Assert.True(vkFragment.AsSpan().SequenceEqual(lp.Program.FragmentCode), $"{name}: fragment SPIR-V differs");
                gl.DeleteProgram(program);
                n++;
            }
            Assert.Equal(27, n);
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
    public async Task Prepared_pipelines_are_not_late()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            var ctx = gl.Context;
            using var lp = LegacyProgram.Create(ctx, Vertex, Fragment, "prepare");
            var layout = lp.VertexLayout([new LegacyProgram.Attribute(default, Format.R32G32Sfloat, 20, false), new LegacyProgram.Attribute(default, Format.R32G32B32Sfloat, 20, false)]);
            var all = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;
            GraphicsPipelineDesc Desc(BlendState blend, string name = "") => new(lp.Program, layout, PrimitiveTopology.TriangleList,
                new AttachmentFormats(Format.R8G8B8A8Unorm, Format.D32Sfloat), blend, all, Silk.NET.Vulkan.PolygonMode.Fill, false, false, name);

            var prepared = new[] { Desc(BlendState.Off), Desc(new BlendState(true, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha)), Desc(BlendState.Off, "same, other name") };
            await ctx.Pipelines.Prepare(prepared);
            int late = ctx.Pipelines.LatePipelines;
            Assert.Same(ctx.Pipelines.Get(prepared[0]), ctx.Pipelines.Get(prepared[2]));   // the name is not part of the key
            ctx.Pipelines.Get(prepared[1]);
            Assert.Equal(late, ctx.Pipelines.LatePipelines);
            ctx.Pipelines.Get(Desc(new BlendState(true, BlendFactor.One, BlendFactor.One)));
            Assert.Equal(late + 1, ctx.Pipelines.LatePipelines);
        }
        ExpectClean(d!);
    }

    [Fact]
    public void Resource_states_plan_barriers_from_the_table()
    {
        var states = new ResourceStates();
        object image = new(), buffer = new();
        Assert.True(states.Plan([new PassUse(image, Access.ColourTarget)]).IsEmpty);   // nothing before: the frame began with a full barrier

        // Write then read: the write made available to the sampling stages.
        var b = states.Plan([new PassUse(image, Access.Sampled)]);
        Assert.Equal(PipelineStageFlags2.ColorAttachmentOutputBit, b.SrcStages);
        Assert.Equal(AccessFlags2.ColorAttachmentWriteBit, b.SrcAccess);
        Assert.Equal(PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, b.DstStages);
        Assert.Equal(AccessFlags2.ShaderSampledReadBit, b.DstAccess);

        // Read then read: nothing. Read then write: an execution dependency only.
        Assert.True(states.Plan([new PassUse(image, Access.Sampled)]).IsEmpty);
        b = states.Plan([new PassUse(image, Access.TransferDst)]);
        Assert.False(b.IsEmpty);
        Assert.Equal((AccessFlags2)0, b.SrcAccess);
        Assert.Equal((AccessFlags2)0, b.DstAccess);

        // Several resources fold into one batch.
        states.Plan([new PassUse(buffer, Access.StorageWrite)]);
        b = states.Plan([new PassUse(buffer, Access.IndirectRead), new PassUse(image, Access.Sampled)]);
        Assert.Equal(PipelineStageFlags2.AllTransferBit | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, b.SrcStages);
        Assert.Equal(AccessFlags2.TransferWriteBit | AccessFlags2.ShaderStorageWriteBit, b.SrcAccess);
        Assert.True(b.DstStages.HasFlag(PipelineStageFlags2.DrawIndirectBit));
        Assert.True(b.DstAccess.HasFlag(AccessFlags2.IndirectCommandReadBit));

        Assert.Equal(2, states.Tracked);
        states.AssumeFullBarrier();
        Assert.Equal(0, states.Tracked);
        Assert.True(states.Plan([new PassUse(buffer, Access.StorageRead)]).IsEmpty);

        // Every access has an entry, and only the writing ones say so.
        foreach (var a in Enum.GetValues<Access>())
            Assert.Equal(a is Access.ColourTarget or Access.DepthTarget or Access.StorageWrite or Access.TransferDst, ResourceStates.Table(a).Write);
    }

    [Fact]
    [Slow]
    public void Timestamps_read_back_a_ring_later()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            var ctx = gl.Context;
            using var scratch = DeviceBuffer.Create(ctx, 64, BufferUse.TransferDst, "timestamp scratch");
            gl.BeginFrame(W, H);
            gl.BeginExternal();
            var arena = ctx.Frame.Timestamps;
            QuerySlot a = arena.Allocate(), b = arena.Allocate();
            Assert.Equal(2, arena.Count);
            ctx.Frame.Commands.Timestamp(arena, a);
            ctx.Frame.Commands.FillBuffer(scratch.Handle, 0, 64, 0);   // something between them
            ctx.Frame.Commands.Timestamp(arena, b);
            gl.EndExternal();
            gl.EndFrame();
            Assert.False(arena.TryRead(a, out _));   // not collected until its slot comes round
            for (int i = 0; i < d!.Frames.Count; i++)
            {
                gl.BeginFrame(W, H);
                gl.EndFrame();
            }
            Assert.True(arena.TryRead(a, out ulong ta));
            Assert.True(arena.TryRead(b, out ulong tb));
            Assert.True(tb >= ta);
            Assert.False(arena.TryRead(default, out _));
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
    public void Uploading_into_a_buffer_the_frame_drew_from_throws()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            var ctx = gl.Context;
            using var buffer = DeviceBuffer.Create(ctx, 256, BufferUse.Vertex | BufferUse.TransferDst, "upload test");
            gl.BeginFrame(W, H);
            ctx.Uploads.Write(buffer, 0, new byte[16]);
            Assert.Throws<ArgumentOutOfRangeException>(() => ctx.Uploads.Write(buffer, 250, new byte[16]));
            _ = buffer.Binding(ctx.Frame);
            Assert.Throws<InvalidOperationException>(() => ctx.Uploads.Write(buffer, 16, new byte[16]));
            gl.EndFrame();
            gl.BeginFrame(W, H);
            ctx.Uploads.Write(buffer, 16, new byte[16]);   // a new frame may write it again
            gl.EndFrame();
        }
        ExpectClean(d!);
    }

    const string ArgsCompute = """
        #version 450
        layout(local_size_x = 1) in;
        layout(std430, set = 0, binding = 0) buffer Args { uint values[]; };
        layout(push_constant) uniform Push { uint indexCount; } pc;
        void main()
        {
            values[0] = pc.indexCount;   // indexCount
            values[1] = 1;               // instanceCount
            values[2] = 0;               // firstIndex
            values[3] = 0;               // vertexOffset
            values[4] = 0;               // firstInstance
        }
        """;

    const string BindlessVertex = """
        #version 450
        void main()
        {
            vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4(p * 2.0 - 1.0, 0.5, 1.0);
        }
        """;

    const string BindlessFragment = """
        #version 450
        #extension GL_EXT_nonuniform_qualifier : require
        layout(set = 0, binding = 0) uniform sampler2D textures2D[];
        layout(push_constant) uniform Push { uint texture; } pc;
        layout(location = 0) out vec4 colour;
        void main() { colour = texture(textures2D[nonuniformEXT(pc.texture)], vec2(0.5)); }
        """;

    [Fact]
    [Slow]
    public unsafe void Compute_writes_indirect_arguments_for_a_bindless_draw()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            var ctx = gl.Context;
            using var source = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, 1, 1, Name: "bindless source"));
            using var target = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, 16, 8, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "bindless target"));
            using var args = DeviceBuffer.Create(ctx, 20, BufferUse.Storage | BufferUse.Indirect, "indirect args");
            using var indices = DeviceBuffer.Create(ctx, 16, BufferUse.Index | BufferUse.TransferDst, "indices");
            using var compute = ctx.Shaders.Compute(ArgsCompute, "args");
            using var draw = ctx.Shaders.Native(BindlessVertex, BindlessFragment, "bindless", [ctx.Bindless.Layout], 4);
            var sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
                TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
            ctx.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, source.View(), source.Image));   // a filler, so the index is not 0
            uint index = ctx.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, source.View(), source.Image));
            Assert.Equal(1u, index);

            gl.BeginFrame(16, 8);   // registered before the frame began: valid in it
            var frame = ctx.Frame;
            ctx.Uploads.Write(source, 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(1, 1)), [10, 200, 30, 255]);
            ctx.Uploads.Write(indices, 0, MemoryMarshalBytes([0u, 1u, 2u]));
            gl.BeginExternal();
            var cmd = frame.Commands;
            cmd.Invalidate();

            var cp = ctx.Pipelines.Get(new ComputePipelineDesc(compute, "args"));
            using (new GpuPass(frame, cmd, "args", [new PassUse(args, Access.StorageWrite)]))
            {
                cmd.BindPipeline(cp);
                var info = new DescriptorBufferInfo(args.Handle, 0, 20);
                var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &info };
                cmd.PushDescriptors(compute.Layout, 0, [write], PipelineBindPoint.Compute);
                cmd.PushConstants(compute.Layout, ShaderStageFlags.ComputeBit, 3u);
                cmd.Dispatch(1);
            }
            Assert.Equal(1, frame.Stats.Dispatches);

            var pipeline = ctx.Pipelines.Get(new GraphicsPipelineDesc(draw, VertexLayout.Empty, PrimitiveTopology.TriangleList,
                new AttachmentFormats(Format.R8G8B8A8Unorm, Format.Undefined), BlendState.Off,
                ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit, Silk.NET.Vulkan.PolygonMode.Fill, false, false, "bindless"));
            using (new GpuPass(frame, cmd, "draw", [new PassUse(args, Access.IndirectRead), new PassUse(target, Access.ColourTarget)]))
            {
                cmd.BeginRendering(Target(target));
                cmd.BindPipeline(pipeline);
                FullTargetState(cmd, 16, 8);
                cmd.BindSets(draw.Layout, 0, [ctx.Bindless.Set], []);
                cmd.PushConstants(draw.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, index);
                cmd.BindIndexBuffer(indices.Binding(frame), IndexType.Uint32);
                cmd.DrawIndexedIndirect(args.Handle, 0, 1);
                cmd.EndRendering();
            }
            Assert.Equal(1, frame.Stats.IndirectDraws);
            gl.EndExternal();
            gl.EndFrame();
            var pixels = ctx.ReadBack(target, 4);
            for (int i = 0; i < pixels.Length; i += 4)
                Assert.Equal((10, 200, 30, 255), (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]));
        }
        ExpectClean(d!);
    }

    static byte[] MemoryMarshalBytes(uint[] values) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    /// <summary>
    /// <see cref="GpuFrame.PreFrame"/> (docs/renderer-native.md 5.3 as built): a dispatch recorded into it after the frame's own commands, while a
    /// native segment holds VkGl's pass open, runs before them: the frame's indirect draw and its copy, recorded earlier, read what it wrote.
    /// The copy lands in a <see cref="ReadbackBuffer"/>, read once the frame has completed. Synchronisation validation is on.
    /// </summary>
    [Fact]
    [Slow]
    public unsafe void A_dispatch_recorded_into_PreFrame_runs_before_the_frames_own_commands()
    {
        using var d = TryCreate(sync: true);
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            var ctx = gl.Context;
            using var source = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, 1, 1, Name: "pre-frame source"));
            using var target = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, 16, 8, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "pre-frame target"));
            using var args = DeviceBuffer.Create(ctx, 20, BufferUse.Storage | BufferUse.Indirect | BufferUse.TransferSrc, "pre-frame args");
            using var indices = DeviceBuffer.Create(ctx, 16, BufferUse.Index | BufferUse.TransferDst, "indices");
            using var readback = ReadbackBuffer.Create(ctx, 20, "pre-frame readback");
            using var compute = ctx.Shaders.Compute(ArgsCompute, "args");
            using var draw = ctx.Shaders.Native(BindlessVertex, BindlessFragment, "bindless", [ctx.Bindless.Layout], 4);
            var sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
                TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
            uint index = ctx.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, source.View(), source.Image));

            gl.BeginFrame(16, 8);
            var frame = ctx.Frame;
            ctx.Uploads.Write(source, 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(1, 1)), [40, 90, 160, 255]);
            ctx.Uploads.Write(indices, 0, MemoryMarshalBytes([0u, 1u, 2u]));
            // The frame's own commands first (in CPU order): an indirect draw from args, then a copy of args.
            gl.BeginExternal();
            var cmd = frame.Commands;
            cmd.Invalidate();
            var pipeline = ctx.Pipelines.Get(new GraphicsPipelineDesc(draw, VertexLayout.Empty, PrimitiveTopology.TriangleList,
                new AttachmentFormats(Format.R8G8B8A8Unorm, Format.Undefined), BlendState.Off,
                ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit, Silk.NET.Vulkan.PolygonMode.Fill, false, false, "bindless"));
            cmd.BeginRendering(Target(target));
            cmd.BindPipeline(pipeline);
            FullTargetState(cmd, 16, 8);
            cmd.BindSets(draw.Layout, 0, [ctx.Bindless.Set], []);
            cmd.PushConstants(draw.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, index);
            cmd.BindIndexBuffer(indices.Binding(frame), IndexType.Uint32);
            cmd.DrawIndexedIndirect(args.Handle, 0, 1);

            // Later on the CPU, with that rendering still open: the dispatch that writes args, into the pre-frame list.
            var pre = frame.PreFrame;
            var cp = ctx.Pipelines.Get(new ComputePipelineDesc(compute, "args"));
            pre.BindPipeline(cp);
            var info = new DescriptorBufferInfo(args.Handle, 0, 20);
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &info };
            pre.PushDescriptors(compute.Layout, 0, [write], PipelineBindPoint.Compute);
            pre.PushConstants(compute.Layout, ShaderStageFlags.ComputeBit, 3u);
            pre.Dispatch(1);
            var after = new BarrierBatch();
            after.Add(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit, PipelineStageFlags2.DrawIndirectBit | PipelineStageFlags2.AllTransferBit,
                AccessFlags2.IndirectCommandReadBit | AccessFlags2.TransferReadBit);
            pre.Barrier(after);

            cmd.EndRendering();
            var full = new BarrierBatch();
            full.Add(PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit, PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);
            cmd.Barrier(full);
            cmd.CopyBuffer(args.Handle, readback.Handle, new BufferCopy(0, 0, 20));
            gl.EndExternal();
            long number = frame.Number;
            gl.EndFrame();
            Assert.False(ReadbackBuffer.Completed(ctx, number + 1));
            d!.Frames.WaitAll();
            Assert.True(ReadbackBuffer.Completed(ctx, number));
            var written = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(readback.Read(0, 20)).ToArray();
            Assert.Equal([3u, 1u, 0u, 0u, 0u], written);
            var pixels = ctx.ReadBack(target, 4);
            for (int i = 0; i < pixels.Length; i += 4)
                Assert.Equal((40, 90, 160, 255), (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]));
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
    public void Buffer_arena_reuses_freed_ranges_after_the_frames_in_flight()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            using var arena = new BufferArena(gl.Context, 1024, BufferUse.Storage, "arena test");
            var a = arena.Allocate(256, 64);
            var b = arena.Allocate(256, 64);
            Assert.Equal(0ul, a.Offset);
            Assert.Equal(256ul, b.Offset);
            gl.BeginFrame(W, H);
            arena.Free(a);
            Assert.Equal(512ul, arena.FreeBytes);   // deferred: the frame being recorded may still use a's range
            gl.EndFrame();
            for (int i = 0; i <= d!.Frames.Count; i++) { gl.BeginFrame(W, H); gl.EndFrame(); }
            Assert.Equal(768ul, arena.FreeBytes);
            Assert.Equal(0ul, arena.Allocate(200, 64).Offset);   // first fit takes a's old range
        }
        ExpectClean(d!);
    }

    /// <summary>A prepared segment of the test below: one fullscreen triangle through a scissor, reading one bindless texture.</summary>
    sealed class ColumnJob(GraphicsPipeline pipeline, PipelineLayout layout, DescriptorSet table, uint texture, int x) : RecordJob
    {
        public override void Record(CommandList cmd)
        {
            FullTargetState(cmd, W, H);
            cmd.SetScissor(new Rect2D(new Offset2D(x, 0), new Extent2D((uint)(W - x), H)));
            cmd.BindPipeline(pipeline);
            cmd.BindSets(layout, 0, [table], []);
            cmd.PushConstants(layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, texture);
            cmd.Draw(3);
        }
    }

    /// <summary>A job that reaches for render-thread state (docs/renderer-native.md 6.3): must throw.</summary>
    sealed class GreedyJob : RecordJob
    {
        public override void Record(CommandList cmd) => RenderJobs.AssertNotInJob();
    }

    /// <summary>
    /// Wave 4 (docs/renderer-native.md 6): a native host's rendering with secondaries. Eight segments, each painting from its column to the
    /// right edge in its own colour, are queued in order (one of them recorded at once on this thread through the seam, as an unported guest
    /// does) and recorded on the job threads; executed in order, column k shows colour k only if the order held. Validation on, 0 errors; and a
    /// job that touches render-thread state throws.
    /// </summary>
    [Fact]
    [Slow]
    public unsafe void Secondaries_recorded_on_job_threads_execute_in_the_order_they_were_queued()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        (int mode, int min) = (Recording.Mode, Recording.MinThreadedDraws);
        (Recording.Mode, Recording.MinThreadedDraws) = (2, 0);   // eight one-draw jobs: on the job threads
        try
        {
            using (var gl = new VkGl(d!))
            {
                var ctx = gl.Context;
                IGlInterop interop = gl;
                const int Columns = 8;
                var sources = new Texture[Columns];
                var indices = new uint[Columns];
                using var target = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, W, H, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "columns"));
                using var draw = ctx.Shaders.Native(BindlessVertex, BindlessFragment, "bindless", [ctx.Bindless.Layout], 4);
                var sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
                    TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
                for (int k = 0; k < Columns; k++)
                {
                    sources[k] = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, 1, 1, Name: $"column {k}"));
                    indices[k] = ctx.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, sources[k].View(), sources[k].Image));
                }
                var formats = new AttachmentFormats(Format.R8G8B8A8Unorm, Format.Undefined);
                var pipeline = ctx.Pipelines.Get(new GraphicsPipelineDesc(draw, VertexLayout.Empty, PrimitiveTopology.TriangleList, formats, BlendState.Off,
                    ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit, Silk.NET.Vulkan.PolygonMode.Fill, false, false, "bindless"));

                gl.BeginFrame(W, H);
                var frame = ctx.Frame;
                for (int k = 0; k < Columns; k++) ctx.Uploads.Write(sources[k], 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(1, 1)), [(byte)(10 + 30 * k), (byte)(200 - 20 * k), (byte)(5 * k), 255]);
                var cmd = interop.BeginNative("columns host");
                cmd.BeginRendering(Target(target), secondaries: true);
                frame.Parallel.Begin(cmd, formats, 0);
                interop.BeginHostPass(cmd);
                for (int k = 0; k < Columns; k++)
                {
                    var job = new ColumnJob(pipeline, draw.Layout, ctx.Bindless.Set, indices[k], k * (W / Columns));
                    if (k == 4)
                    {
                        // An unported guest: records at once into a secondary of its own, in its place.
                        var inline = interop.BeginNativeInPass("inline column");
                        job.Record(inline);
                        interop.EndNative(inline);
                    }
                    else ctx.Record($"column {k}", job);
                }
                Assert.Equal(1 + Columns, frame.Stats.NativeSegments);   // the host's own segment and the columns
                frame.Parallel.End();
                Assert.Equal(Columns, frame.Parallel.Totals.Draws);
                cmd.EndRendering();
                interop.EndHostPass(cmd);
                interop.EndNative(cmd);
                Assert.Throws<InvalidOperationException>(() => ctx.Record("greedy", new GreedyJob()));
                Assert.False(RenderJobs.InJob);
                gl.EndFrame();

                var pixels = ctx.ReadBack(target, 4);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int k = x / (W / Columns), i = (y * W + x) * 4;
                        Assert.Equal((10 + 30 * k, 200 - 20 * k, 5 * k), (pixels[i], pixels[i + 1], pixels[i + 2]));
                    }
                foreach (var s in sources) s.Dispose();
            }
            ExpectClean(d!);
        }
        finally { (Recording.Mode, Recording.MinThreadedDraws) = (mode, min); }
    }
}
