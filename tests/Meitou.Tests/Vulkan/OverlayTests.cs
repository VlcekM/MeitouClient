using Meitou.Data.Textures;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;

namespace Meitou.Tests.Vulkan;

/// <summary>The overlay, the profiler chart and the screenshot readback (wave 3 agent F, step P: docs/renderer-native.md 7.1), through VkGl with sync validation.</summary>
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

    static void Target(IGl gl)
    {
        uint fbo = gl.GenFramebuffer(), colour = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, colour);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, W, H, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colour, 0);
        gl.Viewport(0, 0, W, H);
    }

    [Fact]
    [Slow]
    public void Panels_and_the_profiler_chart_draw_natively_and_the_screenshot_readback_equals_ReadPixels()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        string path = Path.Combine(Path.GetTempPath(), $"meitou-overlay-{Guid.NewGuid():N}.png");
        try
        {
            using (var gl = new VkGl(d!))
            {
                using var overlay = DebugOverlay.TryCreate(gl, gl.Context);
                Assert.SkipWhen(overlay is null, "No monospace system font");
                using var profiler = new FrameProfiler(gl.Context) { Showing = FrameProfiler.Mode.Cpu };
                for (int frame = 0; frame < 3; frame++)
                {
                    gl.BeginFrame(W, H);
                    profiler.BeginFrame();
                    StageClock.Lap(0);
                    profiler.EndFrame();
                    gl.EndFrame();
                }
                gl.BeginFrame(W, H);
                Target(gl);
                gl.ClearColor(0.2f, 0.3f, 0.4f, 1f);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                overlay!.Panel(W, H, "Keys", ["T textures", "Esc quit"]);
                profiler.Draw(overlay, W, H);

                // The native readback (what the screenshots use) against ReadPixels, which this test reads after it.
                FramebufferCapture.SavePng(gl, path, W, H);
                var expected = new byte[W * H * 4];
                gl.ReadPixels<byte>(0, 0, W, H, PixelFormat.Rgba, PixelType.UnsignedByte, expected.AsSpan());
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
                gl.EndFrame();
            }
            ExpectClean(d!);
        }
        finally { File.Delete(path); }
    }

    static void ExpectClean(VulkanDevice d) =>
        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));
}
