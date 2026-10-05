using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

/// <summary>The coexistence seam's invariants (docs/renderer-native.md 4.6): VkGl and native segments in one frame, sharing objects.</summary>
public class SeamTests
{
    static VulkanDevice? TryCreate(bool sync = true)
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

    const int W = 64, H = 16;

    // A triangle covering the target, from gl_VertexID (no vertex inputs); what it draws is cut by the scissor.
    const string Fullscreen = """
        #version 330 core
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            gl_Position = vec4(p * 2.0 - 1.0, 0.5, 1.0);
        }
        """;

    const string Solid = """
        #version 330 core
        uniform vec4 uColour;
        out vec4 colour;
        void main() { colour = uColour; }
        """;

    const string Yellow = """
        #version 330 core
        out vec4 colour;
        void main() { colour = vec4(1.0, 1.0, 0.0, 1.0); }
        """;

    const string Sampled = """
        #version 330 core
        uniform sampler2D uTex;
        out vec4 colour;
        void main() { colour = texelFetch(uTex, ivec2(gl_FragCoord.xy) % textureSize(uTex, 0), 0); }
        """;

    static (uint Fbo, uint Colour) Target(IGl gl, uint texture = 0)
    {
        uint fbo = gl.GenFramebuffer();
        uint colour = texture;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        if (colour == 0)
        {
            colour = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, colour);
            gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, W, H, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        }
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colour, 0);
        gl.Viewport(0, 0, W, H);
        return (fbo, colour);
    }

    static byte[] Read(IGl gl)
    {
        var pixels = new byte[W * H * 4];
        gl.ReadPixels<byte>(0, 0, W, H, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        return pixels;
    }

    static (byte, byte, byte, byte) At(byte[] p, int x, int y)
    {
        int i = (y * W + x) * 4;
        return (p[i], p[i + 1], p[i + 2], p[i + 3]);
    }

    static GraphicsPipelineDesc Desc(LegacyProgram lp, AttachmentFormats formats) =>
        new(lp.Program, lp.VertexLayout([]), PrimitiveTopology.TriangleList, formats, BlendState.Off,
            ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit, Silk.NET.Vulkan.PolygonMode.Fill, false, false);

    static void State(CommandList cmd, PassTargets t, Rect2D scissor)
    {
        cmd.SetViewport(t.Viewport);
        cmd.SetScissor(scissor);
        cmd.SetRaster(CullModeFlags.None, FrontFace.Clockwise);
        cmd.SetDepth(false, false, CompareOp.Always);
        cmd.SetDepthBias(false, 0, 0);
    }

    static void Quarter(IGl gl, int q)
    {
        gl.Enable(EnableCap.ScissorTest);
        gl.Scissor(q * W / 4, 0, W / 4, H);
    }

    [Fact]
    public void VkGl_native_VkGl_in_one_frame_draws_each_part_with_its_own_state()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            var ctx = gl.Context;
            Assert.Same(gl, ctx.Interop);
            Assert.True(d!.SyncValidationEnabled);
            Assert.Same(ctx, GpuContext.Of(gl));
            uint blue = WorldGl.Texture2D(gl, 2, 2, [0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255], repeat: false, mipmaps: false);
            uint solid = WorldGl.Program(gl, Fullscreen, Solid), yellow = WorldGl.Program(gl, Fullscreen, Yellow);
            using var sampled = LegacyProgram.Create(ctx, Fullscreen, Sampled, "seam sampled");
            gl.BindVertexArray(gl.GenVertexArray());

            Target(gl);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);

            // VkGl: red in the first quarter.
            gl.UseProgram(solid);
            gl.Uniform4(gl.GetUniformLocation(solid, "uColour"), 1f, 0f, 0f, 1f);
            Quarter(gl, 0);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // Native: the exported blue texture in the second, into the pass VkGl was drawing.
            var cmd = interop.BeginNative("seam test");
            Assert.Throws<InvalidOperationException>(() => gl.Clear(ClearBufferMask.ColorBufferBit));   // the IGl guard
            Assert.Throws<InvalidOperationException>(() => gl.DrawArrays(PrimitiveType.Triangles, 0, 3));
            var targets = interop.CurrentTargets();
            Assert.Equal(new Rect2D(new Offset2D(0, 0), new Extent2D(W / 4, H)), targets.Scissor);   // GL's scissor
            cmd.BeginRendering(targets.Rendering);
            cmd.BindPipeline(ctx.Pipelines.Get(Desc(sampled, targets.Formats)));
            State(cmd, targets, new Rect2D(new Offset2D(W / 4, 0), new Extent2D(W / 4, H)));
            sampled.Bind(sampled.Sampler("uTex"), interop.Sampled(blue, shadowSampler: false));
            sampled.Flush(cmd);
            cmd.Draw(3);
            cmd.EndRendering();
            interop.EndNative(cmd);

            // VkGl again, the same program and pipeline as before the native segment (the stale-pipeline case of 4.1), then a third one.
            gl.Uniform4(gl.GetUniformLocation(solid, "uColour"), 0f, 1f, 0f, 1f);
            Quarter(gl, 2);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.UseProgram(yellow);
            Quarter(gl, 3);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.Disable(EnableCap.ScissorTest);

            var p = Read(gl);
            Assert.Equal((255, 0, 0, 255), At(p, 2, 2));
            Assert.Equal((0, 0, 255, 255), At(p, W / 4 + 2, 2));
            Assert.Equal((0, 255, 0, 255), At(p, W / 2 + 2, 2));
            Assert.Equal((255, 255, 0, 255), At(p, 3 * W / 4 + 2, H - 2));
        }
        ExpectClean(d!);
    }

    [Fact]
    public void Legacy_programs_take_frame_globals_they_were_not_given()
    {
        using var d = TryCreate(sync: false);
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            var ctx = gl.Context;
            uint blue = WorldGl.Texture2D(gl, 2, 2, [0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255], repeat: false, mipmaps: false);
            using var solid = LegacyProgram.Create(ctx, Fullscreen, Solid, "globals solid");
            using var sampled = LegacyProgram.Create(ctx, Fullscreen, Sampled, "globals sampled");
            bool on = false;
            ctx.Globals.PublishUniform("uColour", () => new System.Numerics.Vector4(1, 0, 1, 1), () => on);
            ctx.Globals.Publish("uTex", () => interop.Sampled(blue, shadowSampler: false));
            using var target = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, W, H, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "globals"));
            var formats = new AttachmentFormats(Format.R8G8B8A8Unorm, Format.Undefined);
            var t = new PassTargets(default, default, formats, W, H, new Viewport(0, 0, W, H, 0, 1), new Rect2D(new Offset2D(0, 0), new Extent2D(W, H)));

            solid.ApplyGlobals();   // the predicate is false: nothing written, the default block keeps zeros
            Assert.All(solid.FragmentDefault, b => Assert.Equal(0, b));
            on = true;
            solid.ApplyGlobals();

            var cmd = interop.BeginNative("globals");
            cmd.BeginRendering(new RenderingDesc(new RenderTarget(target.Attachment(), AttachmentLoadOp.Clear, default, target.Image), default, W, H));
            cmd.BindPipeline(ctx.Pipelines.Get(Desc(solid, formats)));
            State(cmd, t, new Rect2D(new Offset2D(0, 0), new Extent2D(W / 2, H)));
            solid.Flush(cmd);
            cmd.Draw(3);
            cmd.BindPipeline(ctx.Pipelines.Get(Desc(sampled, formats)));
            cmd.SetScissor(new Rect2D(new Offset2D(W / 2, 0), new Extent2D(W / 2, H)));
            sampled.Flush(cmd);
            cmd.Draw(3);
            cmd.EndRendering();
            interop.EndNative(cmd);
            gl.Finish();
            var p = ctx.ReadBack(target, 4);
            Assert.Equal((255, 0, 255, 255), At(p, 1, 1));
            Assert.Equal((0, 0, 255, 255), At(p, W - 2, 1));
        }
        ExpectClean(d!);
    }

    [Fact]
    public void Exports_are_VkGl_own_objects()
    {
        using var d = TryCreate(sync: false);
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            uint tex = WorldGl.Texture2D(gl, 4, 4, new byte[64], repeat: true);
            gl.ActiveTexture(TextureUnit.Texture0 + 3);
            gl.BindTexture(TextureTarget.Texture2D, tex);
            gl.ActiveTexture(TextureUnit.Texture0);
            var info = new Meitou.Rendering.Vulkan.Shaders.SamplerInfo("uTex", 0, 0, Meitou.Rendering.Vulkan.Shaders.SamplerDimension.Dim2D, false, false, false,
                Meitou.Rendering.Vulkan.Shaders.ScalarKind.Float, 0);
            var a = interop.Sampled(tex, shadowSampler: false);
            Assert.Equal(a, interop.Sampled(tex, info));
            Assert.Equal(a, interop.SampledUnit(3, info));        // what a VkGl draw with uTex on unit 3 pushes
            Assert.NotEqual(a, interop.SampledUnit(4, info));     // nothing there: the stand-in
            Assert.Equal(interop.SampledUnit(4, info), interop.Sampled(0, info));
            Assert.Equal(a.Image, interop.Texture(tex).Image);
            Assert.Same(interop.Texture(tex), interop.Texture(tex));

            uint buffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.UniformBuffer, buffer);
            gl.BufferData<byte>(BufferTargetARB.UniformBuffer, new byte[64], BufferUsageARB.StaticDraw);
            gl.BindBufferBase(BufferTargetARB.UniformBuffer, 5, buffer);
            Assert.Equal(interop.Buffer(buffer), interop.UniformBinding(5));
            Assert.Equal(64ul, interop.Buffer(buffer).Size);
        }
        ExpectClean(d!);
    }

    [Fact]
    public void Imported_and_exported_textures_read_back_identically_on_both_sides()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            var ctx = gl.Context;
            using var copy = LegacyProgram.Create(ctx, Fullscreen, Sampled, "seam copy");
            uint solid = WorldGl.Program(gl, Fullscreen, Solid);
            gl.BindVertexArray(gl.GenVertexArray());
            var all = new Rect2D(new Offset2D(0, 0), new Extent2D(W, H));

            // GL renders into a native texture (imported), the native side copies it into another native texture.
            using var native = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, W, H, Use: TextureUse.ColourTarget | TextureUse.Sampled | TextureUse.TransferSrc, Name: "imported"));
            using var nativeCopy = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, W, H, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "copy"));
            uint imported = interop.Import(native);
            Target(gl, imported);
            gl.ClearColor(0.25f, 0.5f, 0.75f, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.UseProgram(solid);
            gl.Uniform4(gl.GetUniformLocation(solid, "uColour"), 1f, 0.2f, 0.1f, 1f);
            Quarter(gl, 1);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.Disable(EnableCap.ScissorTest);
            var byGl = Read(gl);

            var cmd = interop.BeginNative("copy imported");
            var formats = new AttachmentFormats(Format.R8G8B8A8Unorm, Format.Undefined);
            cmd.BeginRendering(new RenderingDesc(new RenderTarget(nativeCopy.Attachment(), AttachmentLoadOp.DontCare, default, nativeCopy.Image), default, W, H));
            cmd.BindPipeline(ctx.Pipelines.Get(Desc(copy, formats)));
            var t = new PassTargets(default, default, formats, W, H, new Viewport(0, 0, W, H, 0, 1), all);
            State(cmd, t, all);
            copy.Bind(copy.Sampler("uTex"), interop.Sampled(imported, shadowSampler: false));
            copy.Flush(cmd);
            cmd.Draw(3);
            cmd.EndRendering();
            interop.EndNative(cmd);
            gl.Finish();
            Assert.Equal(byGl, ctx.ReadBack(native, 4));
            Assert.Equal(byGl, ctx.ReadBack(nativeCopy, 4));
            Assert.Throws<ArgumentException>(() => interop.Import(interop.Texture(imported)));   // only owned textures

            // The reverse: the native side renders into a GL texture (exported), GL reads it.
            var (_, glTexture) = Target(gl);
            cmd = interop.BeginNative("render exported");
            var exported = interop.Texture(glTexture);
            cmd.BeginRendering(new RenderingDesc(new RenderTarget(exported.Attachment(), AttachmentLoadOp.DontCare, default, exported.Image), default, W, H));
            cmd.BindPipeline(ctx.Pipelines.Get(Desc(copy, formats)));
            State(cmd, t, all);
            copy.Bind(copy.Sampler("uTex"), interop.Sampled(imported, shadowSampler: false));
            copy.Flush(cmd);
            cmd.Draw(3);
            cmd.EndRendering();
            interop.EndNative(cmd);
            Assert.Equal(byGl, Read(gl));
            gl.DeleteTexture(imported);   // forgets the name; the image stays the native texture's
        }
        ExpectClean(d!);
    }
}
