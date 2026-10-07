using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Meitou.Rendering.Gpu.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

/// <summary>The bindless table (docs/renderer-native.md 2.6): the integer arrays, and indices usable in the frame they are registered or
/// updated in. Synchronisation validation on.</summary>
public class BindlessTests
{
    static VulkanDevice? TryCreate()
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    static void ExpectClean(VulkanDevice d) =>
        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));

    const string FullScreen = """
        #version 450
        void main()
        {
            vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            gl_Position = vec4(p * 2.0 - 1.0, 0.5, 1.0);
        }
        """;

    static readonly string IntegerFragment = $$"""
        #version 450
        {{BindlessTable.GlslDeclarations}}
        layout(push_constant) uniform Push { uint cells; uint wide; uint signed; } pc;
        layout(location = 0) out uvec4 colour;
        void main()
        {
            ivec2 p = ivec2(gl_FragCoord.xy);
            uvec4 c = texelFetch(utextures2D[nonuniformEXT(pc.cells)], p, 0);
            uint w = texelFetch(utextures2D[nonuniformEXT(pc.wide)], p, 0).r;
            int s = texelFetch(itextures2D[nonuniformEXT(pc.signed)], p, 0).r;
            colour = uvec4(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24), w, uint(s + 100000), 0xC0FFEEu);
        }
        """;

    static readonly string ColourFragment = $$"""
        #version 450
        {{BindlessTable.GlslDeclarations}}
        layout(push_constant) uniform Push { uint texture; } pc;
        layout(location = 0) out vec4 colour;
        void main() { colour = texture(textures2D[nonuniformEXT(pc.texture)], vec2(0.5)); }
        """;

    static void FullTargetState(CommandList cmd, int w, int h)
    {
        cmd.SetViewport(new Viewport(0, 0, w, h, 0, 1));
        cmd.SetScissor(new Rect2D(new Offset2D(0, 0), new Extent2D((uint)w, (uint)h)));
        cmd.SetRaster(CullModeFlags.None, FrontFace.Clockwise);
        cmd.SetDepth(false, false, CompareOp.Always);
        cmd.SetDepthBias(false, 0, 0);
    }

    static Silk.NET.Vulkan.Sampler Nearest(GpuContext ctx, bool integer) =>
        ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, integer, 0));

    static void Draw(GpuContext ctx, ShaderProgram program, Format format, Texture target, ReadOnlySpan<uint> push)
    {
        var frame = ctx.Frame;
        var cmd = frame.Commands;
        var pipeline = ctx.Pipelines.Get(new GraphicsPipelineDesc(program, VertexLayout.Empty, PrimitiveTopology.TriangleList,
            new AttachmentFormats(format, Format.Undefined), BlendState.Off,
            ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit, Silk.NET.Vulkan.PolygonMode.Fill, false, false, program.Name));
        using (new GpuPass(frame, cmd, program.Name, [new PassUse(target, Access.ColourTarget)]))
        {
            cmd.BeginRendering(new RenderingDesc(new RenderTarget(target.Attachment(), AttachmentLoadOp.DontCare, default, target.Image), default, target.Desc.Width, target.Desc.Height));
            cmd.BindPipeline(pipeline);
            FullTargetState(cmd, target.Desc.Width, target.Desc.Height);
            cmd.BindSets(program.Layout, 0, [ctx.Bindless.Set], []);
            for (int i = 0; i < push.Length; i++)
                cmd.PushConstants(program.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, push[i], (uint)(4 * i));
            cmd.Draw(3, 1, 0, 0);
            cmd.EndRendering();
        }
    }

    static byte[] Bytes<T>(params T[] values) where T : unmanaged =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    [Fact]
    public void Kind_follows_the_format()
    {
        Assert.Equal(BindlessKind.UTexture2D, BindlessTable.KindFor(Format.R8G8B8A8Uint));   // the terrain's uCells (GL RGBA8UI)
        Assert.Equal(BindlessKind.UTexture2D, BindlessTable.KindFor(Format.R16Uint));
        Assert.Equal(BindlessKind.UTexture2D, BindlessTable.KindFor(Format.R32Uint));
        Assert.Equal(BindlessKind.ITexture2D, BindlessTable.KindFor(Format.R32Sint));
        Assert.Equal(BindlessKind.ITexture2D, BindlessTable.KindFor(Format.R16Sint));
        Assert.Equal(BindlessKind.Texture2D, BindlessTable.KindFor(Format.R8G8B8A8Unorm));
        Assert.Equal(BindlessKind.Texture2D, BindlessTable.KindFor(Format.D24UnormS8Uint));      // depth reads as float
        Assert.Equal(BindlessKind.Texture2D, BindlessTable.KindFor(Format.D32SfloatS8Uint));
        Assert.Equal(BindlessKind.Shadow2D, BindlessTable.KindFor(Format.D32Sfloat, shadow: true));
        Assert.Equal(BindlessKind.Texture2DArray, BindlessTable.KindFor(Format.R8G8B8A8Srgb, TextureKind.Texture2DArray));
        Assert.Equal(BindlessKind.Cube, BindlessTable.KindFor(Format.R16G16B16A16Sfloat, TextureKind.Cube));
        Assert.Throws<NotSupportedException>(() => BindlessTable.KindFor(Format.R8G8B8A8Uint, TextureKind.Texture2DArray));
        Assert.Throws<NotSupportedException>(() => BindlessTable.KindFor(Format.R8G8B8A8Unorm, shadow: true));
    }

    [Fact]
    [Slow]
    public unsafe void Integer_textures_read_back_exact_values_through_the_bindless_arrays()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            using var cells = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Uint, 2, 1, Name: "cells"));
            using var wide = Texture.Create(ctx, new TextureDesc(Format.R16Uint, 2, 1, Name: "wide"));
            using var signed = Texture.Create(ctx, new TextureDesc(Format.R16Sint, 2, 1, Name: "signed"));
            using var target = Texture.Create(ctx, new TextureDesc(Format.R32G32B32A32Uint, 2, 1, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "integer target"));
            using var program = ctx.Shaders.Native(FullScreen, IntegerFragment, "bindless integer", [ctx.Bindless.Layout], 12);

            // Reflection sees the runtime arrays the shader reads, with their element types.
            var used = program.FragmentReflection!.Samplers.ToDictionary(s => s.Name);
            Assert.Equal((0, 4, ScalarKind.UInt, -1), (used["utextures2D"].Set, used["utextures2D"].Binding, used["utextures2D"].SampledKind, used["utextures2D"].ArrayLength));
            Assert.Equal((0, 5, ScalarKind.Int, -1), (used["itextures2D"].Set, used["itextures2D"].Binding, used["itextures2D"].SampledKind, used["itextures2D"].ArrayLength));

            ctx.BeginFrame();
            // Registered inside the open frame: usable in it.
            var sampler = Nearest(ctx, integer: true);
            var w = ctx.Bindless.Register(wide, sampler);
            var c = ctx.Bindless.Register(cells, sampler);
            var s = ctx.Bindless.Register(signed, sampler);
            Assert.Equal((BindlessKind.UTexture2D, BindlessKind.UTexture2D, BindlessKind.ITexture2D), (w.Kind, c.Kind, s.Kind));
            Assert.NotEqual(w.Index, c.Index);
            var all = new Rect2D(new Offset2D(0, 0), new Extent2D(2, 1));
            ctx.Uploads.Write(cells, 0, 0, all, [1, 2, 3, 254, 255, 0, 128, 7]);
            ctx.Uploads.Write(wide, 0, 0, all, Bytes<ushort>(1000, 65535));
            ctx.Uploads.Write(signed, 0, 0, all, Bytes<short>(-5, 32767));
            ctx.EnsureFrame();
            ctx.Frame.Commands.Invalidate();
            Draw(ctx, program, Format.R32G32B32A32Uint, target, [c.Index, w.Index, s.Index]);
            ctx.EndFrame();

            var texels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(ctx.ReadBack(target, 16)).ToArray();
            Assert.Equal([0xFE030201u, 1000u, 99995u, 0xC0FFEEu, 0x0780_00FFu, 65535u, 132767u, 0xC0FFEEu], texels);
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
    public void A_shader_that_declares_the_bindless_set_wrongly_is_refused()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            const string wrong = """
                #version 450
                #extension GL_EXT_nonuniform_qualifier : require
                layout(set = 0, binding = 0) uniform usampler2D cells[];
                layout(push_constant) uniform Push { uint texture; } pc;
                layout(location = 0) out uvec4 colour;
                void main() { colour = texelFetch(cells[nonuniformEXT(pc.texture)], ivec2(0), 0); }
                """;
            var e = Assert.Throws<InvalidOperationException>(() => ctx.Shaders.Native(FullScreen, wrong, "wrong", [ctx.Bindless.Layout], 4));
            Assert.Contains("must be 'sampler2D textures2D[]' (got UInt", e.Message, StringComparison.Ordinal);   // binding 0 is the float array
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
    public unsafe void Indices_registered_or_updated_in_a_frame_are_seen_in_that_frame()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            byte[][] colours = [[200, 10, 20, 255], [30, 210, 40, 255], [50, 60, 220, 255], [255, 255, 255, 255]];
            var sources = colours.Select((_, i) => Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, 1, 1, Name: $"source {i}"))).ToArray();
            const int Frames = 7;
            var targets = Enumerable.Range(0, Frames).Select(i => Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, 1, 1,
                Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: $"target {i}"))).ToArray();
            using var program = ctx.Shaders.Native(FullScreen, ColourFragment, "bindless colour", [ctx.Bindless.Layout], 4);
            var sampler = Nearest(ctx, integer: false);
            SampledTexture Of(int i) => new(sampler, sources[i].View(), sources[i].Image);
            var expected = new int[Frames];
            BindlessHandle h = default, h2 = default;

            // No waits between frames: two frames in flight, so the slots' sets differ while the journal catches up.
            for (int f = 0; f < Frames; f++)
            {
                ctx.BeginFrame();
                if (f == 0)
                    for (int i = 0; i < sources.Length; i++)
                        ctx.Uploads.Write(sources[i], 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(1, 1)), colours[i]);
                ctx.EnsureFrame();
                ctx.Frame.Commands.Invalidate();
                switch (f)
                {
                    case 0:   // registered in the frame, drawn in it
                        h = ctx.Bindless.Register(sources[0], sampler);
                        expected[f] = 0;
                        break;
                    case 1:   // the other slot: the journal replayed at its begin
                        expected[f] = 0;
                        break;
                    case 2:   // updated in place, then drawn: the new texture this frame
                        ctx.Bindless.Update(h, Of(1));
                        expected[f] = 1;
                        break;
                    case 3:
                        expected[f] = 1;
                        break;
                    case 5:   // a new index for a mid-frame change (the old one freed, deferred): the new texture
                        h2 = ctx.Bindless.Register(sources[3], sampler);
                        ctx.Bindless.Free(h);
                        expected[f] = 3;
                        break;
                    case 6:
                        expected[f] = 3;
                        break;
                }
                Draw(ctx, program, Format.R8G8B8A8Unorm, targets[f], [f >= 5 ? h2.Index : h.Index]);
                if (f == 4)
                {
                    // Updated in place after the draw was recorded: update-after-bind descriptors are read at execution, so the draw shows
                    // the new texture (the documented semantics; a draw-time snapshot needs a new index, as in frame 5).
                    ctx.Bindless.Update(h, Of(2));
                    expected[f] = 2;
                }
                ctx.EndFrame();
            }

            for (int f = 0; f < Frames; f++)
                Assert.Equal(colours[expected[f]], ctx.ReadBack(targets[f], 4));

            // The freed index comes back once the frames that could read it are done.
            for (int i = 0; i <= d!.Frames.Count; i++) { ctx.BeginFrame(); ctx.EndFrame(); }
            Assert.Equal(h.Index, ctx.Bindless.Register(BindlessKind.Texture2D, Of(0)));

            foreach (var t in sources.Concat(targets)) t.Dispose();
        }
        ExpectClean(d!);
    }
}
