using Meitou.Data.Textures;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan.Core;

namespace Meitou.Tests.Vulkan;

/// <summary>The overlay, the profiler chart and the screenshot readback (wave 3 agent F, step P: docs/renderer-native.md 7.1), on the native API with sync validation.</summary>
[Collection("StageClock")]   // StageClock's statics, as SeamTests' profiler test
public class OverlayTests
{
    const int W = 480, H = 320;

    static VulkanDevice? TryCreate()
    {
        try { return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true }); }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    /// <summary>An RGBA8 target cleared to (0.2, 0.3, 0.4) by a rendering's load op.</summary>
    static Texture Target(GpuContext ctx)
    {
        var t = Texture.Create(ctx, new TextureDesc(Silk.NET.Vulkan.Format.R8G8B8A8Unorm, W, H,
            Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "test target"));
        var p = PassTargets.Of(t, null);
        var cmd = ctx.BeginNative("test clear");
        cmd.BeginRendering(new RenderingDesc(p.Colour with
        {
            Load = Silk.NET.Vulkan.AttachmentLoadOp.Clear, Clear = new Silk.NET.Vulkan.ClearValue(new Silk.NET.Vulkan.ClearColorValue(0.2f, 0.3f, 0.4f, 1f)),
        }, default, W, H));
        cmd.EndRendering();
        ctx.EndNative(cmd);
        return t;
    }

    [Fact]
    [Slow]
    public void Panels_and_the_profiler_chart_draw_natively_and_the_screenshot_readback_equals_the_texels()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        string path = Path.Combine(Path.GetTempPath(), $"meitou-overlay-{Guid.NewGuid():N}.png");
        try
        {
            using (var ctx = new GpuContext(d!))
            {
                using var overlay = DebugOverlay.TryCreate(ctx);
                Assert.SkipWhen(overlay is null, "No monospace system font");
                using var profiler = new FrameProfiler(ctx) { Showing = FrameProfiler.Mode.Cpu };
                for (int frame = 0; frame < 3; frame++)
                {
                    ctx.BeginFrame();
                    profiler.BeginFrame();
                    StageClock.Lap(0);
                    profiler.EndFrame();
                    ctx.EndFrame();
                }
                ctx.BeginFrame();
                using var target = Target(ctx);
                overlay!.Target = target;
                overlay.Panel(W, H, "Keys", ["T textures", "Esc quit"]);
                profiler.Draw(overlay, W, H);

                // The screenshot readback against the texture's own texels (GpuContext.ReadBack), read after it.
                FramebufferCapture.SavePng(ctx, target, path, W, H);
                var expected = ctx.ReadBack(target, 4);
                var saved = TextureLoader.LoadFile(path, allMips: false).Levels[0];
                Assert.Equal((W, H), (saved.Width, saved.Height));
                int different = 0;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int from = ((H - 1 - y) * W + x) * 4, to = (y * W + x) * 4;   // PNG rows start at the top
                        for (int c = 0; c < 3; c++) Assert.Equal(expected[from + c], saved.Pixels[to + c]);
                        Assert.Equal(255, saved.Pixels[to + 3]);
                        if (saved.Pixels[to] != 51 || saved.Pixels[to + 1] != 77 || saved.Pixels[to + 2] != 102) different++;
                    }
                // The key panel and the chart cover a good part of the picture, and the panel's own colour is darker than the clear colour.
                Assert.True(different > W * H / 10, $"only {different} pixels differ from the clear colour");
                int panel = (24 * W + 24) * 4;   // inside the key panel's top-left (16 px margin), rows from the top
                Assert.True(saved.Pixels[panel] < 40 && saved.Pixels[panel + 2] < 50, "the panel is not drawn");
                ctx.EndFrame();
            }
            ExpectClean(d!);
        }
        finally { File.Delete(path); }
    }

    static void ExpectClean(VulkanDevice d) =>
        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));
}
