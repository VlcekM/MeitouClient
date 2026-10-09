using System.Numerics;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;

namespace Meitou.Tests.Vulkan;

/// <summary>The post-processing chain's native targets (docs/renderer-native.md 8, phase 8 stage 2): named allocations, a resize, the exposure readback.</summary>
[Collection("StageClock")]
public unsafe class PostProcessNativeTests
{
    [Theory]
    [Slow]
    [InlineData(UpscalerKind.Off)]
    [InlineData(UpscalerKind.Taa)]
    public void The_chain_owns_named_native_targets_survives_a_resize_and_reads_its_exposure_back(UpscalerKind upscaler)
    {
        VulkanDevice? d;
        try { d = VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true }); }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException) { d = null; }
        using var device = d;
        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(device!))
        {
            var options = PostOptions.Create("meitou");
            options.Upscale.Kind = upscaler;
            (options.ToneMap, options.Grade) = (ToneMapOperator.Clamp, false);   // the plain clip, so the grey below is exact
            using var post = new PostProcess(ctx, options) { AutoExposure = (0.01f, 10f), InstantAdaptation = true };
            foreach (var (w, h) in new[] { (64, 36), (80, 48) })
            {
                // The caller's RGBA8 target, as the viewer's offscreen framebuffer.
                using var target = Texture.Create(ctx, new TextureDesc(Silk.NET.Vulkan.Format.R8G8B8A8Unorm, w, h,
                    Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "test target"));
                post.Target = target;

                for (int frame = 0; frame < 2; frame++)
                {
                    post.Begin(w, h);
                    post.SetCamera(Vector3.Zero, Matrix4x4.Identity, 1, w / (float)h);
                    post.BeginFarSlice(1000, 10000);
                    post.BeginNearSlice(1, 1000);
                    // A grey scene: luminance 0.5 everywhere, so the measured mean and the adapted value are 0.5.
                    var t = post.SceneTargets;
                    var cmd = ctx.BeginNative("test scene clear");
                    cmd.BeginRendering(new RenderingDesc(
                        t.Colour with { Load = Silk.NET.Vulkan.AttachmentLoadOp.Clear, Clear = new Silk.NET.Vulkan.ClearValue(new Silk.NET.Vulkan.ClearColorValue(0.5f, 0.5f, 0.5f, 1)) },
                        t.Depth with { Load = Silk.NET.Vulkan.AttachmentLoadOp.Clear, Clear = new Silk.NET.Vulkan.ClearValue(depthStencil: new Silk.NET.Vulkan.ClearDepthStencilValue(1f, 0)) },
                        t.Width, t.Height));
                    cmd.EndRendering();
                    ctx.EndNative(cmd);
                    post.SetNearSlice(1, 1000, 1, w / (float)h);
                    post.End();
                    ctx.Finish();
                }
                Assert.Equal((w, h), (post.RenderWidth, post.RenderHeight));

                var names = device!.Allocator.Breakdown().Select(o => o.Name).ToList();
                foreach (var name in new[] { "post scene colour", "post scene depth", "post ldr", "post ssao", "post luminance", "post exposure adapted" })
                    Assert.Contains(name, names);
                if (upscaler != UpscalerKind.Off)
                    foreach (var name in new[] { "post far slice depth", "post motion", "post upscale depth", "post reactive", "post taa history" })
                        Assert.Contains(name, names);

                var (adapted, mean) = post.ReadExposure();
                Assert.Equal(0.5f, mean, 0.01f);
                Assert.Equal(0.5f, adapted, 0.01f);

                // The composite reached the caller's target: 0.5 × 0.55 / 0.5, clipped, with the dither's ±1.
                var texels = ctx.ReadBack(target, 4);
                Assert.Equal(w * h * 4, texels.Length);
                for (int i = 0; i < texels.Length; i += 4) Assert.InRange(texels[i], 138, 143);
            }
        }
        Assert.True(device!.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", device.ValidationLog));
    }
}
