using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// The native renderer API (docs/renderer-native.md 2 and 3.2): its pixels, the legacy modules VkGl made, its bookkeeping.
/// </summary>
public class GpuApiTests
{
    static VulkanDevice? TryCreate(bool sync = false)
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = sync });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException
            or Silk.NET.Core.Loader.SymbolLoadingException)
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

    static RenderingDesc Target(GpuTexture t, LoadOp load = LoadOp.Clear) =>
        new(new RenderTarget(t.Attachment(), load, new ClearColour(0f, 0f, 0f, 1f)), default, t.Desc.Width, t.Desc.Height);

    static void FullTargetState(IGpuCommandList cmd, int w, int h)
    {
        cmd.SetViewport(new PixelViewport(0, 0, w, h, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)w, (uint)h));
        cmd.SetRaster(CullMode.None, FrontFaceDirection.Ccw);
        cmd.SetDepth(false, false, DepthFunction.Always);
        cmd.SetDepthBias(false, 0, 0);
    }

    [Fact]
    [Slow]
    public unsafe void Legacy_program_draws_the_interpolated_triangle()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            // The sources through LegacyProgram into an RGBA8 texture (until phase 8 stage 3 compared byte for byte with VkGl's picture).
            using var target = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, W, H, Use: TextureUse.ColourTarget | TextureUse.TransferSrc,
                    Name: "golden"));
            using var lp = LegacyProgram.Create(ctx, Vertex, Fragment, "golden triangle");
            Assert.True(lp.Uniform("uMissing") is { IsValid: false });
            lp.Set(lp.Uniform("uOffset"), 0.1f, -0.05f);
            lp.Set(lp.Uniform("uScale"), 0.75f);

            ctx.EnsureFrame();
            var frame = ctx.Frame;
            var cmd = frame.Commands;
            cmd.Invalidate();
            var vertices = frame.Constants.Write<float>(Vertices);
            LegacyProgram.Attribute?[] attributes =
            [
                new LegacyProgram.Attribute(vertices.Binding, InternalFormat.Rg32F, 20, false),
                new LegacyProgram.Attribute(new GpuBufferBinding(vertices.Handle, vertices.Offset + 8), InternalFormat.Rgb32F, 20, false),
            ];
            var pipeline = ctx.Pipelines.Get(new GraphicsPipelineDesc(lp.Program, lp.VertexLayout(attributes), PrimitiveType.Triangles,
                new AttachmentFormats(InternalFormat.Rgba8, InternalFormat.None), BlendState.Off,
                ColorMask.R | ColorMask.G | ColorMask.B | ColorMask.A, PolygonKind.Fill, false, false));
            cmd.BeginRendering(Target(target));
            cmd.BindPipeline(pipeline);
            FullTargetState(cmd, W, H);
            lp.BindVertices(cmd, attributes);
            lp.Flush(cmd);
            cmd.Draw(3);
            cmd.EndRendering();
            ctx.Finish();
            var actual = ctx.ReadBack(target, 4);

            // Each pixel against the triangle at its centre (row 0 at NDC y = -1): inside, the corners' colours mixed by the barycentric
            // weights and scaled; outside, the clear colour. Pixels near an edge are left out (rasterisation rules, not this test's subject).
            var p0 = new System.Numerics.Vector2(Vertices[0] + 0.1f, Vertices[1] - 0.05f);
            var p1 = new System.Numerics.Vector2(Vertices[5] + 0.1f, Vertices[6] - 0.05f);
            var p2 = new System.Numerics.Vector2(Vertices[10] + 0.1f, Vertices[11] - 0.05f);
            float area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
            int inside = 0, outside = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var p = new System.Numerics.Vector2((x + 0.5f) / W * 2 - 1, (y + 0.5f) / H * 2 - 1);
                    float l1 = ((p.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p.Y - p0.Y)) / area;
                    float l2 = ((p1.X - p0.X) * (p.Y - p0.Y) - (p.X - p0.X) * (p1.Y - p0.Y)) / area;
                    float l0 = 1 - l1 - l2;
                    int i = (y * W + x) * 4;
                    var got = (actual[i], actual[i + 1], actual[i + 2], actual[i + 3]);
                    if (l0 > 0.05f && l1 > 0.05f && l2 > 0.05f)
                    {
                        inside++;
                        Assert.InRange(got.Item1, l0 * 0.75f * 255 - 2, l0 * 0.75f * 255 + 2);
                        Assert.InRange(got.Item2, l1 * 0.75f * 255 - 2, l1 * 0.75f * 255 + 2);
                        Assert.InRange(got.Item3, l2 * 0.75f * 255 - 2, l2 * 0.75f * 255 + 2);
                        Assert.Equal(255, got.Item4);
                    }
                    else if (l0 < -0.05f || l1 < -0.05f || l2 < -0.05f)
                    {
                        outside++;
                        Assert.Equal((0, 0, 0, 255), ((int)got.Item1, (int)got.Item2, (int)got.Item3, (int)got.Item4));
                    }
                }
            Assert.True(inside > 300 && outside > 300, $"{inside} pixels inside, {outside} outside");
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

    static byte[] MemoryMarshalBytes(uint[] values) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    /// <summary>
    /// <see cref="GpuFrame.PreFrame"/> (docs/renderer-native.md 5.3 as built): a dispatch recorded into it after the frame's own commands, while a
    /// rendering of the frame is open, runs before them: the frame's indirect draw and its copy, recorded earlier, read what it wrote.
    /// The copy lands in a <see cref="ReadbackBuffer"/>, read once the frame has completed. Synchronisation validation is on.
    /// </summary>
    [Fact]
    [Slow]
    public unsafe void A_dispatch_recorded_into_PreFrame_runs_before_the_frames_own_commands()
    {
        using var d = TryCreate(sync: true);
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            using var source = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 1, 1, Name: "pre-frame source"));
            using var target = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 16, 8, Use: TextureUse.ColourTarget | TextureUse.TransferSrc,
                    Name: "pre-frame target"));
            using var args = DeviceBuffer.Create(ctx, 20, BufferUse.Storage | BufferUse.Indirect | BufferUse.TransferSrc, "pre-frame args");
            using var indices = DeviceBuffer.Create(ctx, 16, BufferUse.Index | BufferUse.TransferDst, "indices");
            using var readback = ReadbackBuffer.Create(ctx, 20, "pre-frame readback");
            var compute = ctx.Shaders.Compute(ArgsCompute, "args");
            var draw = ctx.Shaders.Native(BindlessVertex, BindlessFragment, "bindless", [GpuSetLayout.Bindless], 4);
            var sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
                    TextureWrapMode.ClampToEdge,
                TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
            uint index = ctx.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, source.View()));

            ctx.BeginFrame();
            var frame = ctx.Frame;
            ctx.Uploads.Write(source, 0, 0, new PixelRect(0, 0, 1, 1), [40, 90, 160, 255]);
            ctx.Uploads.Write(indices, 0, MemoryMarshalBytes([0u, 1u, 2u]));
            // The frame's own commands first (in CPU order): an indirect draw from args, then a copy of args.
            ctx.EnsureFrame();
            var cmd = frame.Commands;
            cmd.Invalidate();
            var pipeline = ctx.Pipelines.Get(new GraphicsPipelineDesc(draw, VertexLayout.Empty, PrimitiveType.Triangles,
                new AttachmentFormats(InternalFormat.Rgba8, InternalFormat.None), BlendState.Off,
                ColorMask.R | ColorMask.G | ColorMask.B | ColorMask.A, PolygonKind.Fill, false, false, "bindless"));
            cmd.BeginRendering(Target(target));
            cmd.BindPipeline(pipeline);
            FullTargetState(cmd, 16, 8);
            cmd.BindBindlessTable(draw, 0);
            cmd.PushConstants(draw, GpuStage.Vertex | GpuStage.Fragment, index);
            cmd.BindIndexBuffer(indices.Binding(frame), IndexKind.U32);
            cmd.DrawIndexedIndirect(args.Handle, 0, 1);

            // Later on the CPU, with that rendering still open: the dispatch that writes args, into the pre-frame list.
            var pre = frame.PreFrame;
            var cp = ctx.Pipelines.Get(new ComputePipelineDesc(compute, "args"));
            pre.BindPipeline(cp);
            var info = new DescriptorBufferInfo(ctx.Device.BufferAt(args.Handle.Index), 0, 20);
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 0, DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &info };
            pre.PushDescriptors(compute, 0, [write], GpuBindPoint.Compute);
            pre.PushConstants(compute, GpuStage.Compute, 3u);
            pre.Dispatch(1);
            var after = new BarrierBatch();
            after.Add(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                    PipelineStageFlags2.DrawIndirectBit | PipelineStageFlags2.AllTransferBit,
                AccessFlags2.IndirectCommandReadBit | AccessFlags2.TransferReadBit);
            pre.Barrier(after);

            cmd.EndRendering();
            var full = new BarrierBatch();
            full.Add(PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit, PipelineStageFlags2.AllCommandsBit,
                    AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit);
            cmd.Barrier(full);
            cmd.CopyBuffer(args.Handle, readback.Handle, new GpuBufferCopy(0, 0, 20));
            long number = frame.Number;
            ctx.EndFrame();
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

    /// <summary>
    /// A prepared segment of the test below: one fullscreen triangle through a scissor, reading one bindless texture.
    /// </summary>
    sealed class ColumnJob(GpuPipeline pipeline, GpuProgram program, uint texture, int x) : RecordJob
    {
        public override void Record(IGpuCommandList cmd)
        {
            FullTargetState(cmd, W, H);
            cmd.SetScissor(new PixelRect(x, 0, (uint)(W - x), H));
            cmd.BindPipeline(pipeline);
            cmd.BindBindlessTable(program, 0);
            cmd.PushConstants(program, GpuStage.Vertex | GpuStage.Fragment, texture);
            cmd.Draw(3);
        }
    }

    /// <summary>
    /// A job that reaches for render-thread state (docs/renderer-native.md 6.3): must throw.
    /// </summary>
    sealed class GreedyJob : RecordJob
    {
        public override void Record(IGpuCommandList cmd) => RenderJobs.AssertNotInJob();
    }

    /// <summary>
    /// Wave 4 (docs/renderer-native.md 6): a native host's rendering with secondaries. Eight segments, each painting from its column to the right
    /// edge in its own colour, are queued in order (one of them recorded at once on this thread through BeginGuest, as a guest that is not a job
    /// does) and recorded on the job threads; executed in order, column k shows colour k only if the order held. Validation on, 0 errors; and a job
    /// that touches render-thread state throws.
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
            using (var ctx = new GpuContext(d!))
            {
                const int columns = 8;
                var sources = new GpuTexture[columns];
                var indices = new uint[columns];
                using var target = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, W, H,
                        Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "columns"));
                var draw = ctx.Shaders.Native(BindlessVertex, BindlessFragment, "bindless", [GpuSetLayout.Bindless], 4);
                var sampler = ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
                        TextureWrapMode.ClampToEdge,
                    TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, false, 0));
                for (int k = 0; k < columns; k++)
                {
                    sources[k] = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 1, 1, Name: $"column {k}"));
                    indices[k] = ctx.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, sources[k].View()));
                }
                var formats = new AttachmentFormats(InternalFormat.Rgba8, InternalFormat.None);
                var pipeline = ctx.Pipelines.Get(new GraphicsPipelineDesc(draw, VertexLayout.Empty, PrimitiveType.Triangles, formats, BlendState.Off,
                    ColorMask.R | ColorMask.G | ColorMask.B | ColorMask.A, PolygonKind.Fill, false, false, "bindless"));

                ctx.BeginFrame();
                var frame = ctx.Frame;
                for (int k = 0; k < columns; k++) ctx.Uploads.Write(sources[k], 0, 0, new PixelRect(0, 0, 1, 1),
                        [(byte)(10 + 30 * k), (byte)(200 - 20 * k), (byte)(5 * k), 255]);
                var cmd = ctx.BeginNative("columns host");
                cmd.BeginRendering(Target(target), secondaries: true);
                frame.Parallel.Begin((CommandList)cmd, formats, 0);
                ctx.BeginHostPass((CommandList)cmd, PassTargets.Of(target, null), DrawState.For(formats, d!.DepthClamp));
                for (int k = 0; k < columns; k++)
                {
                    var job = new ColumnJob(pipeline, draw, indices[k], k * (W / columns));
                    if (k == 4)
                    {
                        // A guest that is not a job: records at once into a secondary of its own, in its place.
                        var inline = ctx.BeginGuest("inline column");
                        job.Record(inline);
                        ctx.EndGuest(inline);
                    }
                    else ctx.Record($"column {k}", job);
                }
                Assert.Equal(1 + columns, frame.Stats.NativeSegments);   // the host's own segment and the columns
                frame.Parallel.End();
                Assert.Equal(columns, frame.Parallel.Totals.Draws);
                cmd.EndRendering();
                ctx.EndHostPass(cmd);
                ctx.EndNative(cmd);
                Assert.Throws<InvalidOperationException>(() => ctx.Record("greedy", new GreedyJob()));
                Assert.False(RenderJobs.InJob);
                ctx.EndFrame();

                var pixels = ctx.ReadBack(target, 4);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int k = x / (W / columns), i = (y * W + x) * 4;
                        Assert.Equal((10 + 30 * k, 200 - 20 * k, 5 * k), (pixels[i], pixels[i + 1], pixels[i + 2]));
                    }
                foreach (var s in sources) s.Dispose();
            }
            ExpectClean(d!);
        }
        finally
        {
            (Recording.Mode, Recording.MinThreadedDraws) = (mode, min);
        }
    }
}
