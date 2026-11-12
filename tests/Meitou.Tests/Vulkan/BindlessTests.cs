using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Gpu.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// The bindless table (docs/renderer-native.md 2.6): the integer arrays, and indices usable in the frame they are registered or updated in.
/// Synchronisation validation on.
/// </summary>
public class BindlessTests
{
    static VulkanDevice? TryCreate()
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException
            or Silk.NET.Core.Loader.SymbolLoadingException)
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

    static void FullTargetState(IGpuCommandList cmd, int w, int h)
    {
        cmd.SetViewport(new PixelViewport(0, 0, w, h, 0, 1));
        cmd.SetScissor(new PixelRect(0, 0, (uint)w, (uint)h));
        cmd.SetRaster(CullMode.None, FrontFaceDirection.Ccw);
        cmd.SetDepth(false, false, DepthFunction.Always);
        cmd.SetDepthBias(false, 0, 0);
    }

    static GpuSampler Nearest(GpuContext ctx, bool integer) =>
        ctx.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge,
                TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, false, 1, integer, 0));

    static void Draw(GpuContext ctx, GpuProgram program, InternalFormat format, GpuTexture target, ReadOnlySpan<uint> push)
    {
        var frame = ctx.Frame;
        var cmd = frame.Commands;
        var pipeline = ctx.Pipelines.Get(new GraphicsPipelineDesc(program, VertexLayout.Empty, PrimitiveType.Triangles,
            new AttachmentFormats(format, InternalFormat.None), BlendState.Off,
            ColorMask.R | ColorMask.G | ColorMask.B | ColorMask.A, PolygonKind.Fill, false, false, program.Name));
        using (new GpuPass(frame, cmd, program.Name, [new PassUse(target, Access.ColourTarget)]))
        {
            cmd.BeginRendering(new RenderingDesc(new RenderTarget(target.Attachment(), LoadOp.DontCare, default), default, target.Desc.Width,
                    target.Desc.Height));
            cmd.BindPipeline(pipeline);
            FullTargetState(cmd, target.Desc.Width, target.Desc.Height);
            cmd.BindBindlessTable(program, 0);
            for (int i = 0; i < push.Length; i++)
                cmd.PushConstants(program, GpuStage.Vertex | GpuStage.Fragment, push[i], (uint)(4 * i));
            cmd.Draw(3, 1, 0, 0);
            cmd.EndRendering();
        }
    }

    static byte[] Bytes<T>(params T[] values) where T : unmanaged =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();

    [Fact]
    [Slow]
    public unsafe void Integer_textures_read_back_exact_values_through_the_bindless_arrays()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            using var cells = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8Ui, 2, 1, Name: "cells"));
            using var wide = ctx.CreateTexture(new TextureDesc(InternalFormat.R16Ui, 2, 1, Name: "wide"));
            using var signed = ctx.CreateTexture(new TextureDesc(InternalFormat.R16I, 2, 1, Name: "signed"));
            using var target = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba32Ui, 2, 1, Use: TextureUse.ColourTarget | TextureUse.TransferSrc,
                    Name: "integer target"));
            var program = ctx.Shaders.Native(FullScreen, IntegerFragment, "bindless integer", [GpuSetLayout.Bindless], 12);

            // Reflection sees the runtime arrays the shader reads, with their element types.
            var used = program.FragmentReflection!.Samplers.ToDictionary(s => s.Name);
            Assert.Equal((0, 4, ScalarKind.UInt, -1),
                    (used["utextures2D"].Set, used["utextures2D"].Binding, used["utextures2D"].SampledKind, used["utextures2D"].ArrayLength));
            Assert.Equal((0, 5, ScalarKind.Int, -1),
                    (used["itextures2D"].Set, used["itextures2D"].Binding, used["itextures2D"].SampledKind, used["itextures2D"].ArrayLength));

            ctx.BeginFrame();
            // Registered inside the open frame: usable in it.
            var sampler = Nearest(ctx, integer: true);
            var w = ctx.Bindless.Register(wide, sampler);
            var c = ctx.Bindless.Register(cells, sampler);
            var s = ctx.Bindless.Register(signed, sampler);
            Assert.Equal((BindlessKind.UintTexture2D, BindlessKind.UintTexture2D, BindlessKind.IntTexture2D), (w.Kind, c.Kind, s.Kind));
            Assert.NotEqual(w.Index, c.Index);
            var all = new PixelRect(0, 0, 2, 1);
            ctx.Uploads.Write(cells, 0, 0, all, [1, 2, 3, 254, 255, 0, 128, 7]);
            ctx.Uploads.Write(wide, 0, 0, all, Bytes<ushort>(1000, 65535));
            ctx.Uploads.Write(signed, 0, 0, all, Bytes<short>(-5, 32767));
            ctx.EnsureFrame();
            ctx.Frame.Commands.Invalidate();
            Draw(ctx, program, InternalFormat.Rgba32Ui, target, [c.Index, w.Index, s.Index]);
            ctx.EndFrame();

            var texels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(ctx.ReadBack(target, 16)).ToArray();
            Assert.Equal([0xFE030201u, 1000u, 99995u, 0xC0FFEEu, 0x0780_00FFu, 65535u, 132767u, 0xC0FFEEu], texels);
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
            var sources = colours.Select((_, i) => ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 1, 1, Name: $"source {i}"))).ToArray();
            const int frames = 7;
            var targets = Enumerable.Range(0, frames).Select(i => ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 1, 1,
                Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: $"target {i}"))).ToArray();
            var program = ctx.Shaders.Native(FullScreen, ColourFragment, "bindless colour", [GpuSetLayout.Bindless], 4);
            var sampler = Nearest(ctx, integer: false);
            SampledTexture Of(int i) => new(sampler, sources[i].View());
            var expected = new int[frames];
            BindlessHandle h = default, h2 = default;

            // No waits between frames: two frames in flight, so the slots' sets differ while the journal catches up.
            for (int f = 0; f < frames; f++)
            {
                ctx.BeginFrame();
                if (f == 0)
                    for (int i = 0; i < sources.Length; i++)
                        ctx.Uploads.Write(sources[i], 0, 0, new PixelRect(0, 0, 1, 1), colours[i]);
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
                Draw(ctx, program, InternalFormat.Rgba8, targets[f], [f >= 5 ? h2.Index : h.Index]);
                if (f == 4)
                {
                    // Updated in place after the draw was recorded: update-after-bind descriptors are read at execution, so the draw shows
                    // the new texture (the documented semantics; a draw-time snapshot needs a new index, as in frame 5).
                    ctx.Bindless.Update(h, Of(2));
                    expected[f] = 2;
                }
                ctx.EndFrame();
            }

            for (int f = 0; f < frames; f++)
                Assert.Equal(colours[expected[f]], ctx.ReadBack(targets[f], 4));

            // The freed index comes back once the frames that could read it are done.
            for (int i = 0; i <= d!.Frames.Count; i++) { ctx.BeginFrame(); ctx.EndFrame(); }
            Assert.Equal(h.Index, ctx.Bindless.Register(BindlessKind.Texture2D, Of(0)));

            foreach (var t in sources.Concat(targets)) t.Dispose();
        }
        ExpectClean(d!);
    }
}
