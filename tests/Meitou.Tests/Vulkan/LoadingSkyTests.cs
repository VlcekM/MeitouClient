using System.Numerics;
using Meitou.Data;
using Meitou.Game;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Title;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

[Collection("StageClock")]
public class LoadingSkyTests
{
    [Theory, Slow]
    [InlineData(UpscalerKind.Taa)]
    public void StarsRenderThroughTheLoadingPassWithoutWorldCallbacksOrValidationErrors(UpscalerKind upscaler)
    {
        VulkanDevice? native;
        try
        {
            native = VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException
            or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            native = null;
        }
        using var device = native;
        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");
        using (var context = new GpuContext(device!))
        using (var scene = WorldFrame.LoadFlat(new WorldOptions(), new GameDatabase()))
        {
            var options = PostOptions.Create("meitou");
            options.Upscale.Kind = upscaler;
            options.Upscale.Scale = 1;
            using var gpu = new WorldGpu
            {
                Terrain = new TerrainRenderer(context, scene.Coarse, scene.CoarseSize, scene.Window),
                Sky = new SkyRenderer(context) { MeitouStars = true },
                Post = new PostProcess(context, options) { InstantAdaptation = true },
            };
            using var target = context.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 320, 180,
                Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "loading sky test"));
            gpu.Post.Target = target;
            int worldCallbacks = 0;
            gpu.Post.ObjectMotion = _ => worldCallbacks++;
            var camera = new WorldCamera();
            SkyArrival.Place(camera, new Vector3(0, scene.FlatHeight + 5, 0), 0.4f, SkyArrival.SkyPitch, 80, 0.9f);
            for (int i = 0; i < 4; i++)
            {
                context.BeginFrame();
                WorldFrame.DrawLoadingSky(gpu, scene, camera, 320, 180, 0, i / 60f, 20000);
                context.Finish();
            }
            Assert.Equal(0, worldCallbacks);
            Assert.NotNull(gpu.Post.ObjectMotion);
            var pixels = context.ReadBack(target, 4);
            Assert.True(pixels.Where((_, i) => i % 4 != 3).Distinct().Count() > 8,
                    "The sky should contain visible variation, rather than a blank frame.");
        }
        Assert.True(device!.ValidationErrors == 0, string.Join("\n", device.ValidationLog));
    }
}
