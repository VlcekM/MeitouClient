using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Tests.Ui;

[Collection("StageClock")]
public class UiRenderingTests
{
    [Fact, Slow]
    public void TopLeftClippingCutsTheCorrectPixelsInTheRenderersBottomLeftTarget()
    {
        VulkanDevice? device;
        try
        {
            device = VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException
            or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            device = null;
        }
        using var owned = device;
        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");
        using (var gpu = new GpuContext(device!))
        {
            using var overlay = DebugOverlay.TryCreate(gpu);
            Assert.SkipWhen(overlay is null, "No font available");
            const int width = 64, height = 32;
            using var target = gpu.CreateTexture(new TextureDesc(InternalFormat.Rgba8, width, height,
                    Use: TextureUse.ColourTarget | TextureUse.TransferSrc));
            var cmd = gpu.BeginNative("clear clipping test");
            var targets = PassTargets.Of(target, null);
            cmd.BeginRendering(targets.Rendering with { Colour = targets.Colour with { Load = LoadOp.Clear, Clear = new ClearColour(0, 0, 0, 1) } });
            cmd.EndRendering();
            gpu.EndNative(cmd);
            overlay.Target = target;
            overlay.Rect(0, 0, width, height, new Vector4(1, 0, 0, 1));
            overlay.Flush(width, height, new UiRect(8, 2, 12, 8));
            gpu.Finish();
            byte[] pixels = gpu.ReadBack(target, 4);
            byte Red(int x, int topY) => pixels[((height - 1 - topY) * width + x) * 4];
            Assert.Equal(255, Red(10, 5));
            Assert.Equal(0, Red(10, 26));
            Assert.Equal(0, Red(6, 5));
            Assert.Equal(0, Red(21, 5));
        }
        Assert.True(device!.ValidationErrors == 0, string.Join('\n', device.ValidationLog));
    }
}
