using System.Numerics;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// Phase 8 stage 2 (docs/renderer-native.md 8.6): the sky's and the water's resources are native. These check that what the native code makes is
/// what VkGl made from the GL calls it replaced (the sampler of each GL texture, also under an upscaler's LOD bias; the water quad's vertex input),
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

    /// <summary>The RGBA32F texture <c>WaterRenderer.FloatTexture</c> made before phase 8 stage 2.</summary>
    static uint GlFloatTexture(IGl gl, Vector4[] data, int width, int height)
    {
        uint id = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, id);
        gl.TexImage2D<Vector4>(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.Float, data.AsSpan());
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return id;
    }

    [Fact]
    [Slow]
    public void Native_sky_and_water_textures_sample_as_their_GL_versions_did()
    {
        using var device = TryCreate();
        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(device!))
        {
            var ctx = gl.Context;
            IGlInterop interop = gl;
            var plain = FrameGlobals.Sampler2D("t");
            var pixels = new byte[48 * 20 * 4];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
            var floats = Enumerable.Range(0, 6 * 5).Select(i => new Vector4(i, -i, 0.5f * i, 1)).ToArray();
            var native = new List<(string Name, SampledImage Image, uint Gl)>
            {
                ("mipmapped clamp", SampledImage.Rgba8(ctx, 48, 20, pixels, repeat: false, mipmaps: true, "sky test a"), WorldGl.Texture2D(gl, 48, 20, pixels, repeat: false)),
                ("mipmapped repeat", SampledImage.Rgba8(ctx, 48, 20, pixels, repeat: true, mipmaps: true, "sky test b"), WorldGl.Texture2D(gl, 48, 20, pixels, repeat: true)),
                ("one level", SampledImage.Rgba8(ctx, 48, 20, pixels, repeat: false, mipmaps: false, "sky test c"), WorldGl.Texture2D(gl, 48, 20, pixels, repeat: false, mipmaps: false)),
                ("1 x 1 stand-in", SampledImage.Rgba8(ctx, 1, 1, [1, 2, 3, 4], repeat: true, mipmaps: true, "water test d"), WorldGl.Texture2D(gl, 1, 1, [1, 2, 3, 4], repeat: true)),
                ("float", SampledImage.Rgba32F(ctx, floats, 6, 5, "water test e"), GlFloatTexture(gl, floats, 6, 5)),
            };
            foreach (float bias in (ReadOnlySpan<float>)[0f, 0.75f])
            {
                gl.TextureLodBias = bias;   // the upscaler's bias: on the mipmapped samplers only, in both
                foreach (var (name, image, glName) in native)
                {
                    var mine = image.Sampled();
                    var theirs = interop.Sampled(glName, plain);
                    Assert.True(mine.Sampler.Handle == theirs.Sampler.Handle, $"{name}, bias {bias}: another sampler than VkGl's");
                    Assert.Equal(interop.Texture(glName).Desc.Format, image.Texture.Desc.Format);
                }
            }
            var names = device!.Allocator.Breakdown().Select(o => o.Name).ToList();
            Assert.Contains("sky test a", names);
            Assert.Contains("water test e", names);

            // The water quad's vertex input is what VkGl exported for the GL vertex array it replaces.
            using var quad = DeviceBuffer.Create(ctx, 32, BufferUse.Vertex, "water quad");
            uint vbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.BufferData(BufferTargetARB.ArrayBuffer, 32, null, BufferUsageARB.StaticDraw);
            uint vao = gl.GenVertexArray();
            gl.BindVertexArray(vao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, (void*)0);
            gl.BindVertexArray(0);
            var exported = interop.VertexArray(vao).Attributes[0]!.Value;
            var built = WaterRenderer.QuadAttribute(quad);
            Assert.Equal((exported.Format, exported.Stride, exported.PerInstance, exported.Buffer.Offset), (built.Format, built.Stride, built.PerInstance, built.Buffer.Offset));
            gl.DeleteVertexArray(vao);
            gl.DeleteBuffer(vbo);

            foreach (var (_, image, glName) in native) { image.Dispose(); gl.DeleteTexture(glName); }
            gl.Finish();
        }
        ExpectClean(device!);
    }

    [Fact]
    [Slow]
    public void The_sky_publishes_its_globals_and_the_shadows_off_blocks_without_a_GL_program()
    {
        using var device = TryCreate();
        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(device!))
        {
            var ctx = gl.Context;
            using var sky = new SkyRenderer(null, ctx);
            var g = ctx.Globals;
            // No GL program linked: the atmosphere's names are there anyway (no texture files here: the stand-in, as an empty GL unit).
            foreach (var name in new[] { "uAtmoIrradiance", "uAtmoSpecular", "uAtmoAmbientMap" })
                Assert.True(g.Texture(name)!().IsNull, name);
            Assert.NotNull(g.UniformValue("uAtmoSun"));
            // --no-shadows: zero-filled blocks of the sizes the shaders declare, as ShadowShaders.Bind's GL buffers were.
            var receiver = g.Block(ShadowShaders.ReceiverBlock)!;
            GlBridge.EnsureFrame(ctx);   // a frame for the constants
            Assert.False(receiver().IsNull);
            Assert.False(g.Block(ShadowShaders.CasterBlock)!().IsNull);
            Assert.False(g.Block(MeitouShadowShaders.Block)!().IsNull);
            sky.Prepare(Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f)), 100, 50000);
            // A ShadowPass made later publishes its own blocks over them.
            using var shadows = new ShadowPass(gl, ctx);
            Assert.NotSame(receiver, g.Block(ShadowShaders.ReceiverBlock));
            gl.Finish();
        }
        ExpectClean(device!);
    }
}
