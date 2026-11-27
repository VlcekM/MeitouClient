using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Post;
using Meitou.Modding;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// The code mods' post passes (docs/modding.md "Post passes") through the Vulkan chain: both stages, their order, what the game binds and sets,
/// and the passes that are left out.
/// </summary>
[Collection("StageClock")]
public unsafe class ModPostPassTests
{
    /// <summary>
    /// The start of every pass's fragment here: the picture so far.
    /// </summary>
    const string Head = "#version 330 core\nin vec2 vUv;\nout vec4 fragColour;\nuniform sampler2D uImage;\n";

    [Theory]
    [Slow]
    [InlineData(UpscalerKind.Off, false)]
    [InlineData(UpscalerKind.Taa, false)]
    [InlineData(UpscalerKind.Off, true)]
    public void Mod_passes_draw_in_order_at_both_stages_and_a_failing_one_is_turned_off(UpscalerKind upscaler, bool noAntiAliasing)
    {
        VulkanDevice? d;
        try
        {
            d = VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException
            or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            d = null;
        }
        using var device = d;
        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(device!))
        {
            var options = PostOptions.Create("meitou");
            options.Upscale.Kind = upscaler;
            options.Upscale.Scale = 1;
            (options.ToneMap, options.Grade, options.Dither) = (ToneMapOperator.Clamp, false, false);
            if (noAntiAliasing) (options.Fxaa, options.Smaa) = (false, false);

            int prepared = 0;
            var scene = new PostPass("scene red", Head + "void main() { fragColour = vec4(texture(uImage, vUv).r, 0.0, 0.0, 1.0); }")
                { Stage = PostStage.Scene };
            var mark = new PostPass("mark", Head + PostPass.DepthGlsl + """
                uniform sampler2D uDepth;
                uniform sampler2D uNormal;
                uniform vec2 uViewport;
                uniform vec2 uPlanes;
                uniform float uMark;
                """ + PostPass.NormalGlsl + """
                void main()
                {
                    float sky = depthIsSky(texture(uDepth, vUv).r) ? 1.0 : 0.0;
                    float none = dot(meitouNormal(vUv), vec3(1.0)) == 0.0 ? 1.0 : 0.0;
                    float sized = uViewport.x > 1.0 && uPlanes.y > uPlanes.x ? 1.0 : 0.0;
                    fragColour = vec4(texture(uImage, vUv).r, sky * none * sized, uMark, 1.0);
                }
                """)
            {
                Prepare = f =>
                {
                    prepared++;
                    f.Set("uMark", 0.25f);
                    f.Set("uNotThere", 1f);
                },
            };
            var swap = new PostPass("swap", Head + "void main() { fragColour = texture(uImage, vUv).bgra; }") { Order = 5 };
            var broken = new PostPass("broken", Head + "void main() { fragColour = nothing; }");
            var skipped = new PostPass("skipped", Head + "void main() { fragColour = vec4(1.0); }") { Order = 9, Prepare = f => f.Skip = true };
            var off = new PostPass("off", Head + "void main() { fragColour = vec4(1.0); }") { Order = 9, Enabled = false };
            var thrower = new PostPass("thrower", Head + "void main() { fragColour = vec4(1.0); }")
                { Order = 9, Prepare = _ => throw new InvalidOperationException("boom") };
            var mods = new ModPostPasses();
            foreach (var p in new[] { scene, mark, swap, broken, skipped, off, thrower }) p.Owner = "sample";
            mods.Add([scene, swap, mark, broken, skipped, off, thrower]);
            options.ModPasses = mods;

            using var post = new PostProcess(ctx, options) { AutoExposure = null, InstantAdaptation = true };
            foreach (var (w, h) in new[] { (64, 36), (80, 48) })
            {
                using var target = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, w, h,
                    Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "test target"));
                post.Target = target;
                for (int frame = 0; frame < 3; frame++) Frame(ctx, post, w, h);

                // The scene pass leaves red alone before the composite; mark (order 0) writes green 1 (a sky's depth, no normal, the viewport and
                // planes set) and blue 0.25 from its Prepare; swap (order 5) runs last and turns the channels round.
                var texels = ctx.ReadBack(target, 4);
                for (int i = 0; i < texels.Length; i += 4)
                {
                    Assert.InRange(texels[i], 63, 65);
                    Assert.Equal(255, texels[i + 1]);
                    Assert.InRange(texels[i + 2], 100, 255);
                }
            }
            Assert.True(prepared >= 6);
            Assert.Contains("did not compile", broken.Fault);
            Assert.Contains("boom", thrower.Fault);
            Assert.Null(skipped.Fault);
            Assert.Null(mark.Fault);

            // Every pass off: the chain draws its own picture, the grey scene.
            scene.Enabled = mark.Enabled = swap.Enabled = false;
            using var plain = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 80, 48,
                Use: TextureUse.ColourTarget | TextureUse.TransferSrc | TextureUse.Sampled, Name: "test target"));
            post.Target = plain;
            Frame(ctx, post, 80, 48);
            var grey = ctx.ReadBack(plain, 4);
            Assert.Equal(grey[0], grey[1]);
            Assert.Equal(grey[1], grey[2]);
        }
        Assert.True(device!.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", device.ValidationLog));
    }

    /// <summary>
    /// One frame of a grey scene with a sky's depth through the chain.
    /// </summary>
    static void Frame(GpuContext ctx, PostProcess post, int w, int h)
    {
        post.Begin(w, h);
        post.SetCamera(Vector3.Zero, Matrix4x4.Identity, 1, w / (float)h);
        post.BeginScene(1, 1000, 1, w / (float)h);
        var t = post.SceneTargets;
        var cmd = ctx.BeginNative("test scene clear");
        cmd.BeginRendering(new RenderingDesc(
            t.Colour with { Load = LoadOp.Clear, Clear = new ClearColour(0.5f, 0.5f, 0.5f, 1) },
            t.Depth with { Load = LoadOp.Clear, DepthClear = DepthConvention.Clear },
            t.Width, t.Height));
        cmd.EndRendering();
        ctx.EndNative(cmd);
        post.End();
        ctx.Finish();
    }
}
