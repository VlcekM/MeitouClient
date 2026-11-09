using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Shadows;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// The shadow pass's native resources (docs/renderer-native.md 8, phase 8 stage 1): named allocations, the globals, the blocker map.
/// </summary>
[Collection("StageClock")]   // the pass's jobs add to StageClock's statics
public class ShadowPassNativeTests
{
    [Fact]
    [Slow]
    public void The_Meitou_pass_owns_named_native_targets_publishes_them_and_builds_the_blocker_map()
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
            using var shadows = new ShadowPass(ctx) { Meitou = true, ContactHardening = true,
                    Settings = new ShadowSettings(MapSize: 256, Range: 2000) };
            var view = new ShadowView(new Vector3(0, 100, 0), Vector3.UnitZ, Vector3.UnitY, 1.0f, 16 / 9f, 1);
            int calls = 0;
            // No casters: the cascades are only cleared (every one drawn in the first frame, each layer by its load op).
            var drawn = new List<int>();
            shadows.Render(view, Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f)), (c, _, _, _) => { calls++; drawn.Add(c.Index); });
            Assert.Equal(MeitouShadowFit.DefaultCascades, calls);
            Assert.Equal(Enumerable.Range(0, MeitouShadowFit.DefaultCascades), drawn);
            // The cascade array: a layer per cascade, each the game's tile (the map side over 2).
            Assert.NotNull(shadows.AtlasTexture);
            Assert.Equal((128, 128, MeitouShadowFit.DefaultCascades, TextureKind.Texture2DArray),
                (shadows.AtlasTexture!.Desc.Width, shadows.AtlasTexture.Desc.Height, shadows.AtlasTexture.Desc.Layers,
                        shadows.AtlasTexture.Desc.Kind));

            var names = device!.Allocator.Breakdown().Select(o => o.Name).ToList();
            Assert.Contains("shadow atlas", names);
            Assert.Contains("shadow blocker map", names);

            // The receivers' globals are the native ones: the atlas through a compare sampler, the three blocks.
            var map = ctx.Globals.Texture("uShadowMap")!();
            Assert.Equal(ctx.Device.ViewAt(shadows.AtlasTexture!.View().Index).Handle, ctx.Device.ViewAt(map.View.Index).Handle);
            Assert.False(ctx.Globals.Block(ShadowShaders.ReceiverBlock)!().IsNull);
            Assert.False(ctx.Globals.Block(ShadowShaders.CasterBlock)!().IsNull);
            Assert.False(ctx.Globals.Block("MeitouShadowReceiver")!().IsNull);
            var blocker = ctx.Globals.Texture("uShadowBlocker")!();
            Assert.False(blocker.IsNull);

            ctx.Finish();
            // The blocker map (half a layer, R32F, a layer per cascade): the nearest depth of each 2 × 2 texels of layers cleared to 1.
            Assert.Equal(ctx.Device.ViewAt(shadows.BlockerMap!.View().Index).Handle, ctx.Device.ViewAt(blocker.View.Index).Handle);
            Assert.Equal(MeitouShadowFit.DefaultCascades, shadows.BlockerMap.Desc.Layers);
            for (int layer = 0; layer < MeitouShadowFit.DefaultCascades; layer++)
            {
                var texels = ctx.ReadBack(shadows.BlockerMap, 4, layer);
                var depths = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(texels);
                Assert.Equal(64 * 64, depths.Length);
                Assert.All(depths.ToArray(), z => Assert.Equal(1f, z));
            }

            // The Faithful CSM (F5): the game's four cascades in the tiles of its atlas, the array made again with that atlas as its one layer.
            shadows.Meitou = false;
            calls = 0;
            shadows.Render(view, Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f)), (_, _, _, _) => calls++);
            Assert.Equal(KenshiShadows.CascadeCount, calls);
            Assert.Equal((256, 1), (shadows.AtlasTexture!.Desc.Width, shadows.AtlasTexture.Desc.Layers));
            ctx.Finish();
        }
        Assert.True(device!.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", device.ValidationLog));
    }
}
