using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// The native frame's hosts, guests and segments (docs/renderer-native.md 8.9, phase 8 stage 3), with sync validation: what the seam tests
/// checked through VkGl before it was deleted, now on <see cref="GpuContext"/> alone.
/// </summary>
[Collection("StageClock")]   // serialised with the other native pass tests, which share StageClock's statics
public class NativeHostTests
{
    static VulkanDevice? TryCreate(bool sync = true)
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

    static GpuTexture Target(GpuContext ctx) =>
        ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, W, H, Use: TextureUse.ColourTarget | TextureUse.TransferSrc,
                Name: "native host test"));

    static (byte, byte, byte, byte) At(byte[] p, int x, int y)
    {
        int i = (y * W + x) * 4;
        return (p[i], p[i + 1], p[i + 2], p[i + 3]);
    }

    static PixelRect Quarter(int q) => new(q * W / 4, 0, W / 4, H);

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
            cmd.BeginRendering(targets.Rendering with { Colour = targets.Colour with { Load = LoadOp.Clear, Clear = new ClearColour(0, 1, 0, 1) } });
            ctx.BeginHostPass((CommandList)cmd, targets, DrawState.For(targets.Formats, d!.DepthClamp));
            Assert.Throws<InvalidOperationException>(() => ctx.BeginNative("nested"));
            Assert.Throws<InvalidOperationException>(() => ctx.BeginHostPass((CommandList)cmd, targets,
                    DrawState.For(targets.Formats, d.DepthClamp)));
            // A clear of the host's targets in its place: the first quarter blue.
            ctx.Clear(cmd, true, new ClearColour(0, 0, 1, 1), false, 1, Quarter(0));

            // Guest A: the host's list, with the scissor of its quarter.
            ctx.SetPassViewport(targets.Viewport, Quarter(1));
            var a = ctx.BeginGuest("guest a");
            Assert.Same(cmd, a);
            Assert.Throws<InvalidOperationException>(() => ctx.BeginGuest("nested guest"));
            var ta = ctx.CurrentTargets();
            Assert.Equal(Quarter(1), ta.Scissor);
            Assert.Equal(targets.Formats, ta.Formats);
            var state = ctx.CurrentState();
            a.BindPipeline(ctx.Pipelines.Get(state.Pipeline(red.Program, red.VertexLayout([]), PrimitiveType.Triangles, ta.Formats, "host red")));
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
            b.BindPipeline(ctx.Pipelines.Get(state.Pipeline(yellow.Program, yellow.VertexLayout([]), PrimitiveType.Triangles, tb.Formats,
                    "host yellow")));
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
}
