using System.Numerics;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// Phase 8 stage 2 (docs/renderer-native.md 8.6): the sky's and the water's resources are native. These check that what the native code makes is
/// what VkGl made from the GL calls it replaced (compared with VkGl itself until it was deleted in stage 3: the sampler of each GL texture, also
/// under an upscaler's LOD bias; the water quad's vertex input),
/// and that the sky publishes its globals and the shadows-off blocks without a GL program. Synchronisation validation on.
/// </summary>
public unsafe class SkyWaterNativeTests
{
    static VulkanDevice? TryCreate()
    {
        try { return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true }); }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException) { return null; }
    }

    static void ExpectClean(VulkanDevice d) =>
        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));

    /// <summary>
    /// The GL state each texture had (<c>WorldGl.Texture2D</c>: trilinear when mipmapped, else linear; repeat or clamp on S and T, R GL's default
    /// repeat; <c>WaterRenderer.FloatTexture</c>: linear, clamped), as its sampler (until phase 8 stage 3 checked against VkGl's own sampler for
    /// the GL texture), with the upscaler's LOD bias on the mipmapped ones only; and the formats and levels.
    /// </summary>
    [Fact]
    [Slow]
    public void Native_sky_and_water_textures_sample_as_their_GL_versions_did()
    {
        using var device = TryCreate();
        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(device!))
        {
            var pixels = new byte[48 * 20 * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
            var floats = Enumerable.Range(0, 6 * 5).Select(i => new Vector4(i, -i, 0.5f * i, 1)).ToArray();
            const TextureMinFilter Trilinear = TextureMinFilter.LinearMipmapLinear, Linear = TextureMinFilter.Linear;
            const TextureWrapMode Clamp = TextureWrapMode.ClampToEdge, Repeat = TextureWrapMode.Repeat;
            var native = new List<(string Name, SampledImage Image, TextureMinFilter Min, TextureWrapMode Wrap, Silk.NET.Vulkan.Format Format, int Levels)>
            {
                ("mipmapped clamp", SampledImage.Rgba8(ctx, 48, 20, pixels, repeat: false, mipmaps: true, "sky test a"), Trilinear, Clamp, Silk.NET.Vulkan.Format.R8G8B8A8Unorm, 6),
                ("mipmapped repeat", SampledImage.Rgba8(ctx, 48, 20, pixels, repeat: true, mipmaps: true, "sky test b"), Trilinear, Repeat, Silk.NET.Vulkan.Format.R8G8B8A8Unorm, 6),
                ("one level", SampledImage.Rgba8(ctx, 48, 20, pixels, repeat: false, mipmaps: false, "sky test c"), Linear, Clamp, Silk.NET.Vulkan.Format.R8G8B8A8Unorm, 1),
                ("1 x 1 stand-in", SampledImage.Rgba8(ctx, 1, 1, [1, 2, 3, 4], repeat: true, mipmaps: true, "water test d"), Trilinear, Repeat, Silk.NET.Vulkan.Format.R8G8B8A8Unorm, 1),
                ("float", SampledImage.Rgba32F(ctx, floats, 6, 5, "water test e"), Linear, Clamp, Silk.NET.Vulkan.Format.R32G32B32A32Sfloat, 1),
            };
            foreach (float bias in (ReadOnlySpan<float>)[0f, 0.75f])
            {
                ctx.LodBias = bias;   // the upscaler's bias: on the mipmapped samplers only
                foreach (var (name, image, min, wrap, format, levels) in native)
                {
                    var expected = ctx.Samplers.Get(SamplerDesc.FromGl(min, TextureMagFilter.Linear, wrap, wrap, Repeat, false, DepthFunction.Lequal, false, 1, false, bias));
                    Assert.True(image.Sampled().Sampler.Handle == expected.Handle, $"{name}, bias {bias}: another sampler than the GL texture's");
                    Assert.Equal((format, levels), (image.Texture.Desc.Format, image.Texture.Desc.Levels));
                }
            }
            var names = device!.Allocator.Breakdown().Select(o => o.Name).ToList();
            Assert.Contains("sky test a", names);
            Assert.Contains("water test e", names);

            // The water quad's vertex input: what VkGl exported for the GL vertex array it replaced (two floats per vertex, location 0).
            using var quad = DeviceBuffer.Create(ctx, 32, BufferUse.Vertex, "water quad");
            var built = WaterRenderer.QuadAttribute(quad);
            Assert.Equal((Silk.NET.Vulkan.Format.R32G32Sfloat, 8u, false, 0ul), (built.Format, built.Stride, built.PerInstance, built.Buffer.Offset));

            foreach (var (_, image, _, _, _, _) in native) image.Dispose();
            ctx.Finish();
        }
        ExpectClean(device!);
    }

    [Fact]
    [Slow]
    public void The_sky_publishes_its_globals_and_the_shadows_off_blocks_without_a_GL_program()
    {
        using var device = TryCreate();
        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(device!))
        {
            using var sky = new SkyRenderer(ctx);
            var g = ctx.Globals;
            // No GL program linked: the atmosphere's names are there anyway (no texture files here: the stand-in, as an empty GL unit).
            foreach (var name in new[] { "uAtmoIrradiance", "uAtmoSpecular", "uAtmoAmbientMap" })
                Assert.True(g.Texture(name)!().IsNull, name);
            Assert.NotNull(g.UniformValue("uAtmoSun"));
            // --no-shadows: zero-filled blocks of the sizes the shaders declare, as ShadowShaders.Bind's GL buffers were.
            var receiver = g.Block(ShadowShaders.ReceiverBlock)!;
            ctx.EnsureFrame();   // a frame for the constants
            Assert.False(receiver().IsNull);
            Assert.False(g.Block(ShadowShaders.CasterBlock)!().IsNull);
            Assert.False(g.Block(MeitouShadowShaders.Block)!().IsNull);
            sky.Prepare(Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f)), 100, 50000);
            // A ShadowPass made later publishes its own blocks over them.
            using var shadows = new ShadowPass(ctx);
            Assert.NotSame(receiver, g.Block(ShadowShaders.ReceiverBlock));
            ctx.Finish();
        }
        ExpectClean(device!);
    }
}
