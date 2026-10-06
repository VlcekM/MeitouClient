using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

/// <summary>The coexistence seam's invariants (docs/renderer-native.md 4.6): VkGl and native segments in one frame, sharing objects.</summary>
[Collection("StageClock")]   // the profiler test and OverlayTests share StageClock's statics
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

    // As Fullscreen and Sampled, with default blocks in both stages (set 1 bound before set 0, as LegacyProgram.Flush does) and a set 0 too big to push.
    const string FullscreenScaled = """
        #version 330 core
        uniform float uScale;
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            gl_Position = vec4((p * 2.0 - 1.0) * uScale, 0.5, 1.0);
        }
        """;

    // 40 samplers: more than the push-descriptor limit, so set 0 is an allocated set, as the terrain mesh program's.
    static readonly string SampledTinted = "#version 330 core\nuniform sampler2D uTex;\nuniform vec4 uTint;\nout vec4 colour;\n" +
        string.Concat(Enumerable.Range(0, 40).Select(i => $"uniform sampler2D uMany{i};\n")) +
        "void main() { colour = texelFetch(uTex, ivec2(gl_FragCoord.xy) % textureSize(uTex, 0), 0) * uTint" +
        string.Concat(Enumerable.Range(0, 40).Select(i => $" + texelFetch(uMany{i}, ivec2(0), 0) * 0.0")) + "; }\n";

    const string SampledTintedFew = """
        #version 330 core
        uniform sampler2D uTex;
        uniform vec4 uTint;
        out vec4 colour;
        void main() { colour = texelFetch(uTex, ivec2(gl_FragCoord.xy) % textureSize(uTex, 0), 0) * uTint; }
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
    [Slow]
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
    [Slow]
    public void An_in_pass_native_segment_draws_into_VkGl_open_pass_and_VkGl_continues_it()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            var ctx = gl.Context;
            uint blue = WorldGl.Texture2D(gl, 2, 2, [0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255], repeat: false, mipmaps: false);
            uint solid = WorldGl.Program(gl, Fullscreen, Solid), yellow = WorldGl.Program(gl, Fullscreen, Yellow);
            using var sampled = LegacyProgram.Create(ctx, Fullscreen, Sampled, "seam in-pass sampled");
            gl.BindVertexArray(gl.GenVertexArray());

            Target(gl);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);

            gl.UseProgram(solid);
            gl.Uniform4(gl.GetUniformLocation(solid, "uColour"), 1f, 0f, 0f, 1f);
            Quarter(gl, 0);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // Native, inside the pass VkGl has open: no BeginRendering of our own, our own pipeline and dynamic state.
            var cmd = interop.BeginNativeInPass("seam in-pass");
            Assert.Throws<InvalidOperationException>(() => gl.DrawArrays(PrimitiveType.Triangles, 0, 3));
            var targets = interop.CurrentTargets();
            cmd.BindPipeline(ctx.Pipelines.Get(Desc(sampled, targets.Formats)));
            State(cmd, targets, new Rect2D(new Offset2D(W / 4, 0), new Extent2D(W / 4, H)));
            sampled.Bind(sampled.Sampler("uTex"), interop.Sampled(blue, shadowSampler: false));
            sampled.Flush(cmd);
            cmd.Draw(3);
            interop.EndNative(cmd);

            // VkGl continues the same pass: its pipeline and dynamic state must be re-set over ours.
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
    [Slow]
    public unsafe void A_native_host_clears_and_its_native_guests_record_into_its_rendering_through_the_same_seam_calls()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            var ctx = gl.Context;
            uint solid = WorldGl.Program(gl, Fullscreen, Solid);
            using var red = LegacyProgram.Create(ctx, Fullscreen, Solid, "seam host red");
            using var yellow = LegacyProgram.Create(ctx, Fullscreen, Yellow, "seam host yellow");
            var colour = red.Uniform("uColour");
            gl.BindVertexArray(gl.GenVertexArray());
            uint query = gl.GenQuery();
            uint buffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.UniformBuffer, buffer);
            gl.BufferData<byte>(BufferTargetARB.UniformBuffer, new byte[16], BufferUsageARB.DynamicDraw);

            Target(gl);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.UseProgram(solid);
            gl.Uniform4(gl.GetUniformLocation(solid, "uColour"), 1f, 1f, 1f, 1f);
            Quarter(gl, 0);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);   // white in the first quarter: the host's clear must wipe it

            // The host: its own segment and rendering instance (cleared by the load op), announced to the seam.
            var cmd = interop.BeginNative("seam host");
            var targets = interop.CurrentTargets();
            interop.BeginHostPass(cmd);
            Assert.Throws<InvalidOperationException>(() => interop.BeginNative("nested"));
            cmd.BeginRendering(targets.Rendering with { Colour = targets.Colour with { Load = AttachmentLoadOp.Clear, Clear = new ClearValue(new ClearColorValue(0, 0, 1, 1)) } });

            // What the IGl calls of a host and its guests do meanwhile: state is free, uploads and timestamps record, a pass-touching call throws.
            Assert.Throws<InvalidOperationException>(() => gl.Clear(ClearBufferMask.ColorBufferBit));
            Assert.Throws<InvalidOperationException>(() => gl.DrawArrays(PrimitiveType.Triangles, 0, 3));
            fixed (byte* zero = new byte[16]) gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, 16, zero);
            gl.QueryCounter(query, QueryCounterTarget.Timestamp);

            // Guest A: the pass is the host's, with the GL scissor of its quarter; the same calls a guest on VkGl's own pass makes.
            Quarter(gl, 1);
            var a = interop.BeginNativeInPass("guest a");
            Assert.Same(cmd, a);
            Assert.Throws<InvalidOperationException>(() => interop.BeginNativeInPass("nested guest"));
            var ta = interop.CurrentTargets();
            Assert.Equal(new Rect2D(new Offset2D(W / 4, 0), new Extent2D(W / 4, H)), ta.Scissor);
            Assert.Equal(targets.Formats, ta.Formats);
            var state = interop.CurrentState();
            a.BindPipeline(ctx.Pipelines.Get(state.Pipeline(red.Program, red.VertexLayout([]), PrimitiveTopology.TriangleList, ta.Formats, "seam host red")));
            state.Record(a, ta);
            red.Set(colour, 1f, 0f, 0f, 1f);
            red.Flush(a);
            a.Draw(3);
            interop.EndNative(a);

            // Guest B: another program and quarter in the same rendering instance, after the first one's segment ended.
            Quarter(gl, 3);
            var b = interop.BeginNativeInPass("guest b");
            var tb = interop.CurrentTargets();
            b.BindPipeline(ctx.Pipelines.Get(state.Pipeline(yellow.Program, yellow.VertexLayout([]), PrimitiveTopology.TriangleList, tb.Formats, "seam host yellow")));
            state.Record(b, tb);
            yellow.Flush(b);
            b.Draw(3);
            interop.EndNative(b);

            cmd.EndRendering();
            interop.EndHostPass(cmd);
            Assert.Throws<InvalidOperationException>(() => interop.EndHostPass(cmd));
            interop.EndNative(cmd);

            // VkGl again, the same program and pipeline it had before.
            gl.Uniform4(gl.GetUniformLocation(solid, "uColour"), 0f, 1f, 0f, 1f);
            Quarter(gl, 2);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.Disable(EnableCap.ScissorTest);

            var p = Read(gl);
            Assert.Equal((0, 0, 255, 255), At(p, 2, 2));
            Assert.Equal((255, 0, 0, 255), At(p, W / 4 + 2, 2));
            Assert.Equal((0, 255, 0, 255), At(p, W / 2 + 2, 2));
            Assert.Equal((255, 255, 0, 255), At(p, 3 * W / 4 + 2, H - 2));
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
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

    const string Triangle = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        uniform vec2 uOffset;
        void main() { gl_Position = vec4(aPos + uOffset, 0.5, 1.0); }
        """;

    [Fact]
    [Slow]
    public unsafe void A_port_logs_the_same_draw_as_VkGl()
    {
        using var d = TryCreate(sync: false);
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            var ctx = gl.Context;
            uint blue = WorldGl.Texture2D(gl, 2, 2, new byte[16], repeat: false, mipmaps: false);
            uint glProgram = WorldGl.Program(gl, Triangle, Sampled);
            using var lp = LegacyProgram.Create(ctx, Triangle, Sampled, "log port");
            float[] vertices = [-1, -1, 1, -1, -1, 1];
            uint vao = gl.GenVertexArray(), vbo = gl.GenBuffer();
            gl.BindVertexArray(vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, (void*)0);
            Target(gl);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, blue);
            gl.Clear(ClearBufferMask.ColorBufferBit);

            var writer = new StringWriter();
            ctx.Log = new DrawLog(writer, ctx.HostMemory);
            ctx.Frame.Commands.Log = ctx.Log;

            // The GL draw.
            gl.UseProgram(glProgram);
            gl.Uniform2(gl.GetUniformLocation(glProgram, "uOffset"), 0.25f, 0f);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // The same draw ported: VkGl's buffer, texture and targets through the interop, GL's state set explicitly.
            lp.Set(lp.Uniform("uOffset"), 0.25f, 0f);
            var cmd = interop.BeginNative("port");
            var t = interop.CurrentTargets();
            cmd.BeginRendering(t.Rendering);
            LegacyProgram.Attribute?[] attributes = [new LegacyProgram.Attribute(interop.Buffer(vbo), Format.R32G32Sfloat, 8, false)];
            cmd.BindPipeline(ctx.Pipelines.Get(new GraphicsPipelineDesc(lp.Program, lp.VertexLayout(attributes), PrimitiveTopology.TriangleList, t.Formats,
                BlendState.Off, ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                Silk.NET.Vulkan.PolygonMode.Fill, false, false)));
            cmd.SetViewport(t.Viewport);
            cmd.SetScissor(t.Scissor);
            cmd.SetRaster(CullModeFlags.None, FrontFace.Clockwise);
            cmd.SetDepth(false, false, CompareOp.Less);
            cmd.SetDepthBias(false, 0, 0);
            lp.Bind(lp.Sampler("uTex"), interop.SampledUnit(0, lp.SamplerInfo(lp.Sampler("uTex"))));
            lp.BindVertices(cmd, attributes);
            lp.Flush(cmd);
            cmd.Draw(3);
            cmd.EndRendering();
            interop.EndNative(cmd);

            ctx.Log.Dispose();
            ctx.Log = null;
            ctx.Frame.Commands.Log = null;
            var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith('#')).ToArray();
            Assert.Equal(2, lines.Length);
            static string Body(string line) => line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..];
            Assert.Equal(Body(lines[0]), Body(lines[1]));
        }
        ExpectClean(d!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Slow]
    public void A_native_model_segment_then_a_legacy_segment_then_VkGl_each_draw_their_own(bool pushed)
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            var ctx = gl.Context;
            uint blue = WorldGl.Texture2D(gl, 2, 2, [0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255], repeat: false, mipmaps: false);
            uint red = WorldGl.Texture2D(gl, 2, 2, [255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255], repeat: false, mipmaps: false);
            uint yellow = WorldGl.Program(gl, Fullscreen, Yellow);
            // Set 0 pushed (the terrain meshes' program) or allocated (more samplers than a push may hold).
            using var sampled = LegacyProgram.Create(ctx, FullscreenScaled, pushed ? SampledTintedFew : SampledTinted, "seam legacy after native");
            Assert.Equal(pushed, sampled.Program.PushDescriptors);
            using var frame = new NativeFrame(ctx);
            const string nativeFragment = "layout(location = 0) out vec4 colour;\nvoid main() { colour = texelFetch(textures2D[pc.diffuse], ivec2(0), 0); }\n";
            using var native = frame.Program(NativeShaders.Port(Fullscreen), "#version 450\n" + NativeShaders.Prelude(NativeShaders.MeshPushMembers) + nativeFragment, "seam native model");
            gl.BindVertexArray(gl.GenVertexArray());
            Target(gl);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.UseProgram(yellow);
            Quarter(gl, 0);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            for (int round = 0; round < 2; round++)
            {
                // The native model: bindless set 0, set 1 from NativeFrame, push constants.
                var cmd = interop.BeginNativeInPass("seam native model");
                var targets = interop.CurrentTargets();
                cmd.BindPipeline(ctx.Pipelines.Get(new GraphicsPipelineDesc(native, new VertexLayout([]), PrimitiveTopology.TriangleList, targets.Formats, BlendState.Off,
                    ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit, Silk.NET.Vulkan.PolygonMode.Fill, false, false)));
                State(cmd, targets, new Rect2D(new Offset2D(W / 4, 0), new Extent2D(W / 4, H)));
                frame.Bind(cmd, native.Layout, new ViewConstants());
                var pc = new MeshPush { Diffuse = interop.Bindless(blue).Index };
                cmd.PushConstants(native.Layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, in pc);
                cmd.Draw(3);
                interop.EndNative(cmd);

                // A legacy program's segment next (its set 0 pushed over the native sets), as the terrain meshes follow the foliage.
                cmd = interop.BeginNativeInPass("seam legacy");
                State(cmd, targets, new Rect2D(new Offset2D(W / 2, 0), new Extent2D(W / 4, H)));
                sampled.Bind(sampled.Sampler("uTex"), interop.Sampled(red, shadowSampler: false));
                sampled.Set(sampled.Uniform("uScale"), 1f);
                sampled.Set(sampled.Uniform("uTint"), 1f, 1f, 1f, 1f);
                sampled.Flush(cmd);
                cmd.BindPipeline(ctx.Pipelines.Get(Desc(sampled, targets.Formats)));
                cmd.Draw(3);
                interop.EndNative(cmd);
            }

            gl.UseProgram(yellow);
            Quarter(gl, 3);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.Disable(EnableCap.ScissorTest);
            var p = Read(gl);
            Assert.Equal((255, 255, 0, 255), At(p, 2, 2));
            Assert.Equal((0, 0, 255, 255), At(p, W / 4 + 2, 2));
            Assert.Equal((255, 0, 0, 255), At(p, W / 2 + 2, 2));
            Assert.Equal((255, 255, 0, 255), At(p, 3 * W / 4 + 2, H - 2));
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
    public unsafe void The_vertex_array_stamp_moves_exactly_when_an_export_may_be_stale()
    {
        using var d = TryCreate(sync: false);
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            gl.BeginFrame(W, H);
            uint vao = gl.GenVertexArray(), vbo = gl.GenBuffer(), other = gl.GenBuffer();
            gl.BindVertexArray(vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, new float[12], BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
            gl.BindVertexArray(0);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, other);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, new float[4], BufferUsageARB.DynamicDraw);

            var first = interop.VertexArray(vao);
            long stamp = interop.VertexArrayStamp;
            Assert.Same(first, interop.VertexArray(vao));
            Assert.Equal(stamp, interop.VertexArrayStamp);   // fetching an export moves nothing

            // A buffer no export names: written and renamed as often as it likes, the stamp stays.
            float x = 1;
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, other);
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, 4, &x);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, new float[8], BufferUsageARB.DynamicDraw);
            Assert.Equal(stamp, interop.VertexArrayStamp);

            // The exported static buffer written after this frame used it: renamed, so the stamp moves and the export is new.
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, 4, &x);
            Assert.NotEqual(stamp, interop.VertexArrayStamp);
            var second = interop.VertexArray(vao);
            Assert.NotSame(first, second);
            Assert.NotEqual(first.Attributes[0]!.Value.Buffer, second.Attributes[0]!.Value.Buffer);

            // The vertex array itself changes.
            stamp = interop.VertexArrayStamp;
            gl.BindVertexArray(vao);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
            gl.BindVertexArray(0);
            Assert.NotEqual(stamp, interop.VertexArrayStamp);

            // A new frame: the export stays current (no fetch needed), and the buffers it names count as used by the frame anyway, so a
            // write to one takes new memory and moves the stamp, as after a draw.
            var current = interop.VertexArray(vao);
            stamp = interop.VertexArrayStamp;
            gl.EndFrame();
            gl.BeginFrame(W, H);
            Assert.Equal(stamp, interop.VertexArrayStamp);
            Assert.Same(current, interop.VertexArray(vao));
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, 4, &x);
            Assert.NotEqual(stamp, interop.VertexArrayStamp);
            Assert.NotEqual(current.Attributes[0]!.Value.Buffer, interop.VertexArray(vao).Attributes[0]!.Value.Buffer);
            gl.EndFrame();
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
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
    [Slow]
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

    [Fact]
    [Slow]
    public void A_flush_after_a_bind_pushes_the_new_texture_and_one_without_keeps_it()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            var ctx = gl.Context;
            uint blue = WorldGl.Texture2D(gl, 2, 2, [0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255], repeat: false, mipmaps: false);
            uint red = WorldGl.Texture2D(gl, 2, 2, [255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255], repeat: false, mipmaps: false);
            using var sampled = LegacyProgram.Create(ctx, Fullscreen, Sampled, "flush skip");
            gl.BindVertexArray(gl.GenVertexArray());
            Target(gl);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);

            var cmd = interop.BeginNative("flush skip");
            var targets = interop.CurrentTargets();
            cmd.BeginRendering(targets.Rendering);
            cmd.BindPipeline(ctx.Pipelines.Get(Desc(sampled, targets.Formats)));
            var slot = sampled.Sampler("uTex");
            for (int q = 0; q < 3; q++)
            {
                State(cmd, targets, new Rect2D(new Offset2D(q * W / 4, 0), new Extent2D(W / 4, H)));
                if (q < 2) sampled.Bind(slot, interop.Sampled(q == 0 ? blue : red, shadowSampler: false));   // the third flushes with no bind
                sampled.Flush(cmd);
                cmd.Draw(3);
            }
            cmd.EndRendering();
            interop.EndNative(cmd);

            var p = Read(gl);
            Assert.Equal((0, 0, 255, 255), At(p, 2, 2));
            Assert.Equal((255, 0, 0, 255), At(p, W / 4 + 2, 2));
            Assert.Equal((255, 0, 0, 255), At(p, W / 2 + 2, 2));
            Assert.Equal((0, 0, 0, 255), At(p, 3 * W / 4 + 2, 2));
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
    public void Profiler_stamps_go_through_the_seam_into_VkGl_passes()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            uint solid = WorldGl.Program(gl, Fullscreen, Solid);
            gl.BindVertexArray(gl.GenVertexArray());
            using var profiler = new FrameProfiler(gl.Context);
            for (int frame = 0; frame < 2 * d!.Frames.Count + 2; frame++)
            {
                gl.BeginFrame(W, H);
                profiler.BeginFrame();
                Target(gl);
                gl.UseProgram(solid);
                gl.Uniform4(gl.GetUniformLocation(solid, "uColour"), 1f, 0f, 0f, 1f);
                gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                StageClock.Lap(0);   // a stamp inside VkGl's open pass
                gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                StageClock.Lap(1);
                Assert.Equal(3, gl.Context.Frame.Timestamps.Count);   // the native arena's, not GL queries
                profiler.EndFrame();
                gl.EndFrame();
            }
            Assert.True(profiler.GpuFrames > 0, "no GPU frame read");
            Assert.True(profiler.LastGpuMs >= 0);
        }
        ExpectClean(d!);
    }
}
