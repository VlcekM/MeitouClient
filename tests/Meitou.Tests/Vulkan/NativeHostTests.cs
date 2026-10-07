using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// The native frame's hosts, guests and segments (docs/renderer-native.md 8.9, phase 8 stage 3), with sync validation: what the seam tests
/// checked through VkGl before it was deleted, now on <see cref="GpuContext"/> alone.
/// </summary>
[Collection("StageClock")]   // the profiler test and OverlayTests share StageClock's statics
public class NativeHostTests
{
    static VulkanDevice? TryCreate(bool sync = true)
    {
        try { return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = sync }); }
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

    static Texture Target(GpuContext ctx) =>
        Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, W, H, Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "native host test"));

    static SampledImage Solid2x2(GpuContext ctx, byte r, byte g, byte b, string name) =>
        SampledImage.Rgba8(ctx, 2, 2, [r, g, b, 255, r, g, b, 255, r, g, b, 255, r, g, b, 255], repeat: false, mipmaps: false, name);

    static (byte, byte, byte, byte) At(byte[] p, int x, int y)
    {
        int i = (y * W + x) * 4;
        return (p[i], p[i + 1], p[i + 2], p[i + 3]);
    }

    static Rect2D Quarter(int q) => new(new Offset2D(q * W / 4, 0), new Extent2D(W / 4, H));

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

    [Fact]
    [Slow]
    public void A_host_clears_and_its_guests_record_into_its_rendering_with_the_viewport_and_state_it_hands_them()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            using var red = LegacyProgram.Create(ctx, Fullscreen, Solid, "host red");
            using var yellow = LegacyProgram.Create(ctx, Fullscreen, Yellow, "host yellow");
            var colour = red.Uniform("uColour");
            using var target = Target(ctx);
            ctx.BeginFrame();
            Assert.Throws<InvalidOperationException>(() => ctx.CurrentTargets());   // no pass is open
            Assert.Throws<InvalidOperationException>(() => ctx.BeginGuest("no host"));

            // The host: its own segment and rendering instance (cleared green by the load op), announced with its targets and state.
            var cmd = ctx.BeginNative("host");
            var targets = PassTargets.Of(target, null);
            cmd.BeginRendering(targets.Rendering with { Colour = targets.Colour with { Load = AttachmentLoadOp.Clear, Clear = new ClearValue(new ClearColorValue(0, 1, 0, 1)) } });
            ctx.BeginHostPass(cmd, targets, DrawState.For(targets.Formats, d!.DepthClamp));
            Assert.Throws<InvalidOperationException>(() => ctx.BeginNative("nested"));
            Assert.Throws<InvalidOperationException>(() => ctx.BeginHostPass(cmd, targets, DrawState.For(targets.Formats, d.DepthClamp)));
            // A clear of the host's targets in its place: the first quarter blue.
            ctx.Clear(cmd, true, new ClearColorValue(0, 0, 1, 1), false, 1, Quarter(0));

            // Guest A: the host's list, with the scissor of its quarter.
            ctx.SetPassViewport(targets.Viewport, Quarter(1));
            var a = ctx.BeginGuest("guest a");
            Assert.Same(cmd, a);
            Assert.Throws<InvalidOperationException>(() => ctx.BeginGuest("nested guest"));
            var ta = ctx.CurrentTargets();
            Assert.Equal(Quarter(1), ta.Scissor);
            Assert.Equal(targets.Formats, ta.Formats);
            var state = ctx.CurrentState();
            a.BindPipeline(ctx.Pipelines.Get(state.Pipeline(red.Program, red.VertexLayout([]), PrimitiveTopology.TriangleList, ta.Formats, "host red")));
            state.Record(a, ta);
            red.Set(colour, 1f, 0f, 0f, 1f);
            red.Flush(a);
            a.Draw(3);
            ctx.EndGuest(a);
            Assert.Throws<InvalidOperationException>(() => ctx.EndGuest(a));

            // Guest B: another program and quarter in the same rendering instance.
            ctx.SetPassViewport(targets.Viewport, Quarter(3));
            var b = ctx.BeginGuest("guest b");
            var tb = ctx.CurrentTargets();
            b.BindPipeline(ctx.Pipelines.Get(state.Pipeline(yellow.Program, yellow.VertexLayout([]), PrimitiveTopology.TriangleList, tb.Formats, "host yellow")));
            state.Record(b, tb);
            yellow.Flush(b);
            b.Draw(3);
            ctx.EndGuest(b);

            Assert.Throws<InvalidOperationException>(() => ctx.EndNative(cmd));   // the host pass is still open
            cmd.EndRendering();
            ctx.EndHostPass(cmd);
            Assert.Throws<InvalidOperationException>(() => ctx.EndHostPass(cmd));
            ctx.EndNative(cmd);

            ctx.Finish();   // submits the frame and waits for it
            var p = ctx.ReadBack(target, 4);
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
        using (var ctx = new GpuContext(d!))
        {
            using var blue = Solid2x2(ctx, 0, 0, 255, "globals blue");
            using var solid = LegacyProgram.Create(ctx, Fullscreen, Solid, "globals solid");
            using var sampled = LegacyProgram.Create(ctx, Fullscreen, Sampled, "globals sampled");
            bool on = false;
            ctx.Globals.PublishUniform("uColour", () => new System.Numerics.Vector4(1, 0, 1, 1), () => on);
            ctx.Globals.Publish("uTex", () => blue.Sampled());
            using var target = Target(ctx);
            var formats = new AttachmentFormats(Format.R8G8B8A8Unorm, Format.Undefined);
            var t = new PassTargets(default, default, formats, W, H, new Viewport(0, 0, W, H, 0, 1), new Rect2D(new Offset2D(0, 0), new Extent2D(W, H)));

            solid.ApplyGlobals();   // the predicate is false: nothing written, the default block keeps zeros
            Assert.All(solid.FragmentDefault, b => Assert.Equal(0, b));
            on = true;
            solid.ApplyGlobals();

            var cmd = ctx.BeginNative("globals");
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
            ctx.EndNative(cmd);
            ctx.Finish();   // submits the frame and waits for it
            var p = ctx.ReadBack(target, 4);
            Assert.Equal((255, 0, 255, 255), At(p, 1, 1));
            Assert.Equal((0, 0, 255, 255), At(p, W - 2, 1));
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
    public void A_flush_after_a_bind_pushes_the_new_texture_and_one_without_keeps_it()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            using var blue = Solid2x2(ctx, 0, 0, 255, "flush blue");
            using var red = Solid2x2(ctx, 255, 0, 0, "flush red");
            using var sampled = LegacyProgram.Create(ctx, Fullscreen, Sampled, "flush skip");
            using var target = Target(ctx);
            var targets = PassTargets.Of(target, null);

            var cmd = ctx.BeginNative("flush skip");
            cmd.BeginRendering(targets.Rendering with { Colour = targets.Colour with { Load = AttachmentLoadOp.Clear, Clear = new ClearValue(new ClearColorValue(0, 0, 0, 1)) } });
            cmd.BindPipeline(ctx.Pipelines.Get(Desc(sampled, targets.Formats)));
            var slot = sampled.Sampler("uTex");
            for (int q = 0; q < 3; q++)
            {
                State(cmd, targets, Quarter(q));
                if (q < 2) sampled.Bind(slot, (q == 0 ? blue : red).Sampled());   // the third flushes with no bind
                sampled.Flush(cmd);
                cmd.Draw(3);
            }
            cmd.EndRendering();
            ctx.EndNative(cmd);

            ctx.Finish();   // submits the frame and waits for it
            var p = ctx.ReadBack(target, 4);
            Assert.Equal((0, 0, 255, 255), At(p, 2, 2));
            Assert.Equal((255, 0, 0, 255), At(p, W / 4 + 2, 2));
            Assert.Equal((255, 0, 0, 255), At(p, W / 2 + 2, 2));
            Assert.Equal((0, 0, 0, 255), At(p, 3 * W / 4 + 2, 2));
        }
        ExpectClean(d!);
    }

    [Fact]
    [Slow]
    public void Profiler_stamps_go_into_the_frame_and_inside_a_host_pass()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            using var red = LegacyProgram.Create(ctx, Fullscreen, Solid, "profiler red");
            using var target = Target(ctx);
            using var profiler = new FrameProfiler(ctx, () => ctx.GpuFrameMs);
            var targets = PassTargets.Of(target, null);
            for (int frame = 0; frame < 2 * d!.Frames.Count + 2; frame++)
            {
                ctx.BeginFrame();
                profiler.BeginFrame();
                var cmd = ctx.BeginNative("profiler host");
                cmd.BeginRendering(targets.Rendering with { Colour = targets.Colour with { Load = AttachmentLoadOp.Clear } });
                ctx.BeginHostPass(cmd, targets, DrawState.For(targets.Formats, d.DepthClamp));
                var a = ctx.BeginGuest("profiler guest");
                var state = ctx.CurrentState();
                a.BindPipeline(ctx.Pipelines.Get(state.Pipeline(red.Program, red.VertexLayout([]), PrimitiveTopology.TriangleList, targets.Formats, "profiler red")));
                state.Record(a, ctx.CurrentTargets());
                red.Flush(a);
                a.Draw(3);
                ctx.EndGuest(a);
                StageClock.Lap(0);   // a stamp inside the host's rendering
                cmd.EndRendering();
                ctx.EndHostPass(cmd);
                ctx.EndNative(cmd);
                StageClock.Lap(1);   // and one between segments
                Assert.Equal(4, ctx.Frame.Timestamps.Count);   // the frame's start, the profiler's and the two stages
                profiler.EndFrame();
                ctx.EndFrame();
            }
            Assert.True(profiler.GpuFrames > 0, "no GPU frame read");
            Assert.True(profiler.LastGpuMs >= 0);
            Assert.True(ctx.GpuFrameMs > 0, "no whole-frame GPU time");
        }
        ExpectClean(d!);
    }
}
