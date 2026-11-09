using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Sky;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// The volumetric cloud passes on a native Vulkan device: what they draw against the clear sky, the haze and the fog, through resizes, resets
/// and disposal, with clean sync validation.
/// </summary>
public class VolumetricCloudNativeTests
{
    /// <summary>
    /// A sky with its cloud passes drawn into one test picture at a view-projection the steps may change.
    /// </summary>
    sealed class CloudScene(GpuContext ctx, SkyRenderer sky, GpuTexture target, Matrix4x4 viewProjection)
    {
        /// <summary>
        /// The device's context.
        /// </summary>
        public GpuContext Ctx { get; } = ctx;

        /// <summary>
        /// The sky drawn.
        /// </summary>
        public SkyRenderer Sky { get; } = sky;

        /// <summary>
        /// The picture drawn into.
        /// </summary>
        public GpuTexture Target { get; } = target;

        /// <summary>
        /// The view-projection the sky and the clouds are drawn with.
        /// </summary>
        public Matrix4x4 ViewProjection { get; set; } = viewProjection;

        /// <summary>
        /// Prepares the sky, the cloud shadows and the clouds at the size and draws the sky into the picture, waiting for it.
        /// </summary>
        public void Draw(int width, int height, bool reflection)
        {
            Ctx.EnsureFrame();
            Sky.Prepare(Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f)), Sky.Eye.Y, 50000);
            Sky.PrepareCloudShadows();
            Sky.PrepareClouds(ViewProjection, width, height, Sky.Eye, reflection);
            var targets = PassTargets.Of(Target, null);
            var cmd = Ctx.BeginNative("cloud test scene");
            cmd.BeginRendering(targets.Rendering);
            Ctx.BeginHostPass((CommandList)cmd, targets, DrawState.Scene(targets.Formats));
            Sky.Draw(ViewProjection, SkyColours.For(Vector3.UnitY));
            Ctx.EndHostPass(cmd);
            cmd.EndRendering();
            Ctx.EndNative(cmd);
            Ctx.Finish();
        }

        /// <summary>
        /// The picture read back, four bytes a texel.
        /// </summary>
        public byte[] ReadBack() => Ctx.ReadBack(Target, 4);
    }

    /// <summary>
    /// The cloud passes change the sky, follow the coverage override and the weather, and survive resizes, reflections and the lit and shadow
    /// switches, with clean sync validation.
    /// </summary>
    [Fact]
    [Slow]
    public void Cloud_passes_resize_reset_and_dispose_with_clean_sync_validation()
    {
        VulkanDevice? device = TryCreate();

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
                    using var target = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 96, 64,
                        Use: TextureUse.ColourTarget | TextureUse.TransferSrc, Name: "cloud test picture"));
                    var view = Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.Normalize(new Vector3(0, 0.4f, -1)), Vector3.UnitY);
                    var scene = new CloudScene(ctx, sky, target, view * DepthConvention.Perspective(1.0f, 1.5f, 1, 1000));
                    TheVolumeChangesTheSkyAndSurvivesResizes(scene);
                    TheHorizonHazeOvercastAndFogMatch(scene);
                }
                ctx.Finish();
            }

            Assert.True(device!.ValidationErrors == 0, string.Join("\n", device.ValidationLog));
        }
    }

    /// <summary>
    /// A Vulkan 1.3 device with sync validation, or null where there is none.
    /// </summary>
    static VulkanDevice? TryCreate()
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
        return device;
    }

    /// <summary>
    /// The volume changes the clear sky; the coverage override changes it until cleared; the weather's clouds survive resizes, reflections and
    /// the lit and shadow switches.
    /// </summary>
    static void TheVolumeChangesTheSkyAndSurvivesResizes(CloudScene scene)
    {
        var sky = scene.Sky;
        scene.Draw(96, 64, false);
        byte[] cloudy = scene.ReadBack();
        sky.CloudDensityInput = 0;
        scene.Draw(96, 64, false);
        byte[] clear = scene.ReadBack();

        Assert.True(cloudy.Where((b, i) => Math.Abs(b - clear[i]) > 4).Count() > 100,
                "The volume must change the rendered sky without game assets");

        // --clouds intentionally overrides weather; clearing it restores weather control immediately.
        sky.CloudCoverage = 0.5f;
        scene.Draw(96, 64, false);
        byte[] overridden = scene.ReadBack();

        Assert.True(overridden.Where((b, i) => Math.Abs(b - clear[i]) > 4).Count() > 100);

        sky.CloudCoverage = null;
        scene.Draw(96, 64, false);

        Assert.Equal(clear, scene.ReadBack());

        sky.CloudDensityInput = null;
        sky.Weather = SkyWeather.Default with { Name = "overcast", CloudDensity = 0.9f };
        scene.Draw(80, 48, true);
        scene.Draw(80, 48, true);
        scene.Draw(120, 80, false);
        scene.Draw(120, 80, false);
        sky.LitClouds = false;
        scene.Draw(120, 80, false);
        sky.LitClouds = true;
        sky.CloudShadows = false;
        scene.Draw(96, 64, false);

        Assert.Contains(scene.ReadBack(), b => b > 0);
    }

    /// <summary>
    /// Looking within 2.3 degrees of the horizon, volume and distant terrain must share one haze target; full overcast closes the distant sky
    /// whatever the surface haze, and dense fog covers the sky equally with either cloud renderer.
    /// </summary>
    static void TheHorizonHazeOvercastAndFogMatch(CloudScene scene)
    {
        var (ctx, sky, target) = (scene.Ctx, scene.Sky, scene.Target);
        sky.CloudDensityInput = 0.5f;
        sky.HazeStrength = 1;
        scene.ViewProjection = Matrix4x4.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
            * DepthConvention.Perspective(0.08f, 1.5f, 1, 1000);
        scene.Draw(96, 64, false);
        byte[] horizon = scene.ReadBack();
        using var reference = HazeReference(ctx);
        DrawTheReference(ctx, reference, target, scene.ViewProjection);
        byte[] expected = scene.ReadBack();

        Assert.True(horizon.Where((b, i) => Math.Abs(b - expected[i]) > 1).Count() == 0,
            "Volumetric horizon differs from fully hazed terrain");

        // Full overcast must close the distant sky even when Meitou weakens surface haze.
        sky.CloudDensityInput = 1;
        scene.Draw(96, 64, false);
        byte[] overcastHorizon = scene.ReadBack();
        sky.HazeStrength = Enhancements.MeitouHazeStrength;
        scene.Draw(96, 64, false);

        Assert.Equal(overcastHorizon, scene.ReadBack());

        // Dense weather fog must cover the sky equally with either cloud renderer.
        sky.Weather = sky.Weather with { FogEnabled = true, FogColour = new Vector3(0.15f, 0.25f, 0.35f), FogMax = 5000 };
        scene.Draw(96, 64, false);
        byte[] foggedVolume = scene.ReadBack();
        sky.LitClouds = false;
        scene.Draw(96, 64, false);

        Assert.Equal(foggedVolume, scene.ReadBack());
    }

    /// <summary>
    /// A full-screen program of the fully hazed terrain colour along each pixel's ray.
    /// </summary>
    static LegacyProgram HazeReference(GpuContext ctx)
    {
        const string referenceFragment = """
            #version 330 core
            in vec2 vUv;
            out vec4 fragColour;
            uniform mat4 uInverse;
            void main()
            {
                vec2 ndc = vUv * 2.0 - 1.0;
                vec4 a = uInverse * vec4(ndc, DEPTH_NEAREST, 1.0), b = uInverse * vec4(ndc, DEPTH_SKY, 1.0);
                vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
                fragColour = vec4(atmoKenshiHaze(vec3(0.13, 0.27, 0.41), dir * uAtmoFog.w, uAtmoFog.w), 1.0);
            }
            """;
        return LegacyProgram.Create(ctx, PostProcessShaders.Vertex,
            referenceFragment.Replace("in vec2 vUv;", AtmosphereShaders.Functions + DepthConvention.Glsl
                + "\nin vec2 vUv;"), "cloud horizon reference");
    }

    /// <summary>
    /// Draws the reference into the picture at the view-projection's inverse, waiting for it.
    /// </summary>
    static void DrawTheReference(GpuContext ctx, LegacyProgram reference, GpuTexture target, Matrix4x4 vp)
    {
        var segment = new NativeSegment(ctx, reference, PrimitiveType.Triangles, "cloud horizon reference");
        Matrix4x4.Invert(vp, out var inverse);
        reference.Set(reference.Uniform("uInverse"), in inverse);
        reference.ApplyGlobals();
        var referenceTargets = PassTargets.Of(target, null);
        var referenceState = DrawState.For(referenceTargets.Formats, false);
        var referenceCmd = ctx.BeginNative("cloud horizon reference");
        referenceCmd.BeginRendering(referenceTargets.Rendering);
        referenceState.Record(referenceCmd, referenceTargets);
        referenceCmd.BindPipeline(segment.Get(referenceState, referenceTargets.Formats, null));
        reference.Flush(referenceCmd);
        referenceCmd.Draw(3);
        referenceCmd.EndRendering();
        ctx.EndNative(referenceCmd);
        ctx.Finish();
    }
}