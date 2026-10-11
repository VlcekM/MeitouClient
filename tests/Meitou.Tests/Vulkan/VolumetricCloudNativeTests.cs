using System.Numerics;
using Meitou.Rendering;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

public class VolumetricCloudNativeTests
{
    [Fact]
    [Slow]
    public void Cloud_passes_resize_reset_and_dispose_with_clean_sync_validation()
    {
        VulkanDevice? device;
        try { device = VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true }); }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException) { device = null; }
        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");
        using (device)
        {
            using (var ctx = new GpuContext(device!))
            {
                using (var sky = new SkyRenderer(ctx))
                {
                    sky.CloudDensityInput = 0.5f;
                    sky.Eye = new Vector3(-50000, 2000, 3000);
                    sky.Prepare(Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f)), sky.Eye.Y, 50000);
                    using var target = Texture.Create(ctx, new TextureDesc(Vk.Format.R8G8B8A8Unorm, 96, 64,
                        Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "cloud test picture"));
                    var view = Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.Normalize(new Vector3(0, 0.4f, -1)), Vector3.UnitY);
                    var vp = view * Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1.5f, 1, 1000);
                    void Draw(int width, int height, bool reflection)
                    {
                        ctx.EnsureFrame();
                        sky.Prepare(Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f)), sky.Eye.Y, 50000);
                        sky.PrepareCloudShadows();
                        sky.PrepareClouds(vp, width, height, sky.Eye, reflection);
                        var targets = PassTargets.Of(target, null);
                        var cmd = ctx.BeginNative("cloud test scene");
                        cmd.BeginRendering(targets.Rendering);
                        ctx.BeginHostPass(cmd, targets, DrawState.Scene(targets.Formats));
                        sky.Draw(vp, SkyColours.For(Vector3.UnitY));
                        ctx.EndHostPass(cmd); cmd.EndRendering(); ctx.EndNative(cmd);
                        ctx.Finish();
                    }
                    Draw(96, 64, false);
                    byte[] cloudy = ctx.ReadBack(target, 4);
                    sky.CloudDensityInput = 0;
                    Draw(96, 64, false);
                    byte[] clear = ctx.ReadBack(target, 4);
                    Assert.True(cloudy.Where((b, i) => Math.Abs(b - clear[i]) > 4).Count() > 100, "The volume must change the rendered sky without game assets");
                    // --clouds intentionally overrides weather; clearing it restores weather control immediately.
                    sky.CloudCoverage = 0.5f;
                    Draw(96, 64, false);
                    byte[] overridden = ctx.ReadBack(target, 4);
                    Assert.True(overridden.Where((b, i) => Math.Abs(b - clear[i]) > 4).Count() > 100);
                    sky.CloudCoverage = null;
                    Draw(96, 64, false);
                    Assert.Equal(clear, ctx.ReadBack(target, 4));
                    sky.CloudDensityInput = null;
                    sky.Weather = SkyWeather.Default with { Name = "overcast", CloudDensity = 0.9f };
                    Draw(80, 48, true); Draw(80, 48, true);
                    Draw(120, 80, false); Draw(120, 80, false);
                    sky.LitClouds = false;
                    Draw(120, 80, false);
                    sky.LitClouds = true; sky.CloudShadows = false;
                    Draw(96, 64, false);
                    Assert.Contains(ctx.ReadBack(target, 4), b => b > 0);

                    // Looking within 2.3 degrees of the horizon, volume and distant terrain must share one haze target.
                    sky.CloudDensityInput = 0.5f;
                    sky.HazeStrength = 1;
                    vp = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
                        * Matrix4x4.CreatePerspectiveFieldOfView(0.08f, 1.5f, 1, 1000);
                    Draw(96, 64, false);
                    byte[] horizon = ctx.ReadBack(target, 4);
                    const string referenceFragment = """
                        #version 330 core
                        in vec2 vUv;
                        out vec4 fragColour;
                        uniform mat4 uInverse;
                        void main()
                        {
                            vec2 ndc = vUv * 2.0 - 1.0;
                            vec4 a = uInverse * vec4(ndc, 0.0, 1.0), b = uInverse * vec4(ndc, 1.0, 1.0);
                            vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
                            fragColour = vec4(atmoKenshiHaze(vec3(0.13, 0.27, 0.41), dir * uAtmoFog.w, uAtmoFog.w), 1.0);
                        }
                        """;
                    using var reference = LegacyProgram.Create(ctx, PostProcessShaders.Vertex,
                        referenceFragment.Replace("in vec2 vUv;", AtmosphereShaders.Functions + "\nin vec2 vUv;"), "cloud horizon reference");
                    var segment = new NativeSegment(ctx, reference, Vk.PrimitiveTopology.TriangleList, "cloud horizon reference");
                    Matrix4x4.Invert(vp, out var inverse);
                    reference.Set(reference.Uniform("uInverse"), in inverse);
                    reference.ApplyGlobals();
                    var referenceTargets = PassTargets.Of(target, null);
                    var referenceState = DrawState.For(referenceTargets.Formats, false);
                    var referenceCmd = ctx.BeginNative("cloud horizon reference");
                    referenceCmd.BeginRendering(referenceTargets.Rendering);
                    referenceState.Record(referenceCmd, referenceTargets);
                    referenceCmd.BindPipeline(segment.Get(referenceState, referenceTargets.Formats, null));
                    reference.Flush(referenceCmd); referenceCmd.Draw(3); referenceCmd.EndRendering();
                    ctx.EndNative(referenceCmd); ctx.Finish();
                    byte[] expected = ctx.ReadBack(target, 4);
                    Assert.True(horizon.Where((b, i) => Math.Abs(b - expected[i]) > 1).Count() == 0,
                        "Volumetric horizon differs from fully hazed terrain");
                    // Full overcast must close the distant sky even when Meitou weakens surface haze.
                    sky.CloudDensityInput = 1;
                    Draw(96, 64, false);
                    byte[] overcastHorizon = ctx.ReadBack(target, 4);
                    sky.HazeStrength = Enhancements.MeitouHazeStrength;
                    Draw(96, 64, false);
                    Assert.Equal(overcastHorizon, ctx.ReadBack(target, 4));
                    // Dense weather fog must cover the sky equally with either cloud renderer.
                    sky.Weather = sky.Weather with { FogEnabled = true, FogColour = new Vector3(0.15f, 0.25f, 0.35f), FogMax = 5000 };
                    Draw(96, 64, false);
                    byte[] foggedVolume = ctx.ReadBack(target, 4);
                    sky.LitClouds = false;
                    Draw(96, 64, false);
                    Assert.Equal(foggedVolume, ctx.ReadBack(target, 4));
                }
                ctx.Finish();
            }
            Assert.True(device!.ValidationErrors == 0, string.Join("\n", device.ValidationLog));
        }
    }
}