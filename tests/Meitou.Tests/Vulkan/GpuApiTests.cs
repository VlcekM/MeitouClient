using System.Reflection;

using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

/// <summary>The native renderer API (docs/renderer-native.md 2 and 3.2): its pixels, the legacy modules VkGl made, its bookkeeping.</summary>
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
    public unsafe void Legacy_program_draws_the_interpolated_triangle()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            // The sources through LegacyProgram into an RGBA8 texture (until phase 8 stage 3 compared byte for byte with VkGl's picture).
            using var target = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, W, H, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "golden"));
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
        yield return ("water meitou", Field(typeof(WaterRenderer), "MeitouVertex"), Field(typeof(WaterRenderer), "MeitouFragment"));
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
            ("adapt", PostProcessShaders.Adapt), ("velocity", UpscaleShaders.Velocity), ("taa", UpscaleShaders.Taa), ("fog volumes", PostProcessShaders.FogVolumes),
        })
            yield return (name, PostProcessShaders.Vertex, f);
    }

    /// <summary>
    /// SHA-256 (first 128 bits) of each world program's legacy modules as given to the driver (vertex then fragment code, default blocks moved
    /// to set 1): recorded on 2026-10-07 from the build in which <c>LegacyProgram</c> was last checked byte for byte against VkGl's modules
    /// (phase 8 stage 3, before VkGl was deleted), so the legacy programs still get exactly the SPIR-V VkGl gave them.
    /// </summary>
    static readonly Dictionary<string, string> LegacyModules = new()
    {
        ["sky simple"] = "4C48F27F2EFC495E4B81B6421F095B12",
        ["sky"] = "A1961E3A2EB10AA21CA6FAE09F6A418A",
        ["water"] = "815EB74140844C2B7F8549B4A13D2230",
        ["water meitou"] = "13FC72EB383404C53772F93ACB9F2ECC",
        ["debug overlay"] = "AE37E44A4CF61D8AE0E36CD0364EE1A2",
        ["terrain patch"] = "9104E758D1117BE7BD047ADB19AED53C",
        ["terrain mesh"] = "46C2AF6F2B3452ED193905494658B900",
        ["terrain patch depth"] = "824EFC3C10ECB608BF9A18D97831652B",
        ["terrain mesh depth"] = "FD249B5F918DBB257C412C7394FD4ABC",
        ["foliage mesh"] = "64DA1F072336D1786B4A8D5BCC0733D2",
        ["foliage grass"] = "7FFA52CDF8AECEB08284377556031C8A",
        ["foliage grass motion"] = "A836A4928C20E6352E07004C224C6A92",
        ["foliage depth"] = "099C2868B7FA91B71BE2ACE3ED5DD997",
        ["buildings"] = "D4D6A546C579566FF4558F18729C854F",
        ["buildings depth"] = "24B964B69B81036B6258A57BAA9486A8",
        ["shadow debug"] = "AFF23DFE04A66C7FF2E32E4B701EDB4E",
        ["shadow atlas"] = "7A8BC32D82FC65DBE0B7F1BEE4AFB43A",
        ["shadow blocker"] = "EF2D9447A695FB305A1FD0097A66C3DA",
        ["terrain shadow sweep"] = "73B5D1C7C3FAED3EF09B596E0C18CD1F",
        ["ssao"] = "7F5CFED6375EE7E8026BCE6643FB1571",
        ["ssao blur"] = "EBF4C0B9D76D55C8BA77F6135B802B20",
        ["composite"] = "34F19B1A033E547FFEF05115ABF0018C",
        ["fxaa"] = "11A560F30D679E1B0E0EB877EBE075F6",
        ["heat haze"] = "0F4D5CE1E9D6A3E0A18147A23C4E75FE",
        ["luminance"] = "1D11EE5E842070E8D4801D47B5730D4F",
        ["adapt"] = "E1201F9895006424E7FE9C1983191734",
        ["velocity"] = "DD61543F9F99ECD7D4C32252F6CB8473",
        ["taa"] = "0BF03F6B50AA2B4DC5B30C22868ECC00",
        ["fog volumes"] = "3B9958713CCE931A162CA4533F6FE815",
    };

    [Fact]
    [Slow]
    public void Legacy_programs_get_the_modules_VkGl_gave_every_world_program()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            var actual = new List<string>();
            var wrong = new List<string>();
            foreach (var (name, v, f) in WorldPrograms())
            {
                using var lp = LegacyProgram.Create(ctx, v, f, name);
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([.. lp.Program.VertexCode!, .. lp.Program.FragmentCode!]))[..32];
                actual.Add($"[\"{name}\"] = \"{hash}\",");
                if (!LegacyModules.TryGetValue(name, out var expected) || expected != hash) wrong.Add(name);
            }
            Assert.Equal(29, actual.Count);
            Assert.True(wrong.Count == 0, $"modules differ for {string.Join(", ", wrong)}; actual:\n{string.Join("\n", actual)}");
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
    public async Task Prepared_pipelines_are_not_late()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
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
        using (var ctx = new GpuContext(d!))
        {
            using var scratch = DeviceBuffer.Create(ctx, 64, BufferUse.TransferDst, "timestamp scratch");
            ctx.BeginFrame();
            ctx.EnsureFrame();
            var arena = ctx.Frame.Timestamps;
            QuerySlot a = arena.Allocate(), b = arena.Allocate();
            Assert.Equal(3, arena.Count);   // and the frame's own start (GpuContext.GpuFrameMs)
            ctx.Frame.Commands.Timestamp(arena, a);
            ctx.Frame.Commands.FillBuffer(scratch.Handle, 0, 64, 0);   // something between them
            ctx.Frame.Commands.Timestamp(arena, b);
            ctx.EndFrame();
            Assert.False(arena.TryRead(a, out _));   // not collected until its slot comes round
            for (int i = 0; i < d!.Frames.Count; i++)
            {
                ctx.BeginFrame();
                ctx.EndFrame();
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
        using (var ctx = new GpuContext(d!))
        {
            using var buffer = DeviceBuffer.Create(ctx, 256, BufferUse.Vertex | BufferUse.TransferDst, "upload test");
            ctx.BeginFrame();
            ctx.Uploads.Write(buffer, 0, new byte[16]);
            Assert.Throws<ArgumentOutOfRangeException>(() => ctx.Uploads.Write(buffer, 250, new byte[16]));
            _ = buffer.Binding(ctx.Frame);
            Assert.Throws<InvalidOperationException>(() => ctx.Uploads.Write(buffer, 16, new byte[16]));
            ctx.EndFrame();
            ctx.BeginFrame();
            ctx.Uploads.Write(buffer, 16, new byte[16]);   // a new frame may write it again
            ctx.EndFrame();
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
        using (var ctx = new GpuContext(d!))
        {
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

            ctx.BeginFrame();   // registered before the frame began: valid in it
            var frame = ctx.Frame;
            ctx.Uploads.Write(source, 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(1, 1)), [10, 200, 30, 255]);
            ctx.Uploads.Write(indices, 0, MemoryMarshalBytes([0u, 1u, 2u]));
            ctx.EnsureFrame();
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
            ctx.EndFrame();
            var pixels = ctx.ReadBack(target, 4);
            for (int i = 0; i < pixels.Length; i += 4)
                Assert.Equal((10, 200, 30, 255), (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]));
        }
        ExpectClean(d!);
    }

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

            ctx.BeginFrame();
            var frame = ctx.Frame;
            ctx.Uploads.Write(source, 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(1, 1)), [40, 90, 160, 255]);
            ctx.Uploads.Write(indices, 0, MemoryMarshalBytes([0u, 1u, 2u]));
            // The frame's own commands first (in CPU order): an indirect draw from args, then a copy of args.
            ctx.EnsureFrame();
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

    [Fact]
    [Slow]
    public void Buffer_arena_reuses_freed_ranges_after_the_frames_in_flight()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            using var arena = new BufferArena(ctx, 1024, BufferUse.Storage, "arena test");
            var a = arena.Allocate(256, 64);
            var b = arena.Allocate(256, 64);
            Assert.Equal(0ul, a.Offset);
            Assert.Equal(256ul, b.Offset);
            ctx.BeginFrame();
            arena.Free(a);
            Assert.Equal(512ul, arena.FreeBytes);   // deferred: the frame being recorded may still use a's range
            ctx.EndFrame();
            for (int i = 0; i <= d!.Frames.Count; i++) { ctx.BeginFrame(); ctx.EndFrame(); }
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
    /// right edge in its own colour, are queued in order (one of them recorded at once on this thread through BeginGuest, as a guest that is not a job
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
            using (var ctx = new GpuContext(d!))
            {
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

                ctx.BeginFrame();
                var frame = ctx.Frame;
                for (int k = 0; k < Columns; k++) ctx.Uploads.Write(sources[k], 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(1, 1)), [(byte)(10 + 30 * k), (byte)(200 - 20 * k), (byte)(5 * k), 255]);
                var cmd = ctx.BeginNative("columns host");
                cmd.BeginRendering(Target(target), secondaries: true);
                frame.Parallel.Begin(cmd, formats, 0);
                ctx.BeginHostPass(cmd, PassTargets.Of(target, null), DrawState.For(formats, d!.DepthClamp));
                for (int k = 0; k < Columns; k++)
                {
                    var job = new ColumnJob(pipeline, draw.Layout, ctx.Bindless.Set, indices[k], k * (W / Columns));
                    if (k == 4)
                    {
                        // A guest that is not a job: records at once into a secondary of its own, in its place.
                        var inline = ctx.BeginGuest("inline column");
                        job.Record(inline);
                        ctx.EndGuest(inline);
                    }
                    else ctx.Record($"column {k}", job);
                }
                Assert.Equal(1 + Columns, frame.Stats.NativeSegments);   // the host's own segment and the columns
                frame.Parallel.End();
                Assert.Equal(Columns, frame.Parallel.Totals.Draws);
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
