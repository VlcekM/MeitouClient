using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan.Core;

namespace Meitou.Tests.Vulkan;

/// <summary>The shadow pass's native resources (docs/renderer-native.md 8, phase 8 stage 1): named allocations, the globals, the blocker map.</summary>
[Collection("StageClock")]   // the pass's jobs add to StageClock's statics
public class ShadowPassNativeTests
{
    [Fact]
    [Slow]
    public void The_Meitou_pass_owns_named_native_targets_publishes_them_and_builds_the_blocker_map()
    {
        VulkanDevice? d;
        try { d = VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true }); }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException) { d = null; }
        using var device = d;
        Assert.SkipWhen(device is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(device!))
        {
            using var shadows = new ShadowPass(ctx) { Meitou = true, ContactHardening = true, Settings = new ShadowSettings(MapSize: 256, Range: 2000) };
            var view = new ShadowView(new Vector3(0, 100, 0), Vector3.UnitZ, Vector3.UnitY, 1.0f, 16 / 9f, 1);
            int calls = 0;
            // No casters: the cascades are only cleared (every one drawn in the first frame: the whole atlas by the load op).
            shadows.Render(view, Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f)), (_, _, _, _) => calls++);
            Assert.Equal(4, calls);
            Assert.NotNull(shadows.AtlasTexture);

            var names = device!.Allocator.Breakdown().Select(o => o.Name).ToList();
            Assert.Contains("shadow atlas", names);
            Assert.Contains("shadow blocker map", names);

            // The receivers' globals are the native ones: the atlas through a compare sampler, the three blocks.
            var map = ctx.Globals.Texture("uShadowMap")!();
            Assert.Equal(shadows.AtlasTexture!.Image.Handle, map.Image.Handle);
            Assert.False(ctx.Globals.Block(ShadowShaders.ReceiverBlock)!().IsNull);
            Assert.False(ctx.Globals.Block(ShadowShaders.CasterBlock)!().IsNull);
            Assert.False(ctx.Globals.Block("MeitouShadowReceiver")!().IsNull);
            var blocker = ctx.Globals.Texture("uShadowBlocker")!();
            Assert.False(blocker.IsNull);

            ctx.Finish();
            // The blocker map (half the atlas, R32F): the nearest depth of each 2 × 2 texels of an atlas cleared to 1.
            Assert.Equal(shadows.BlockerMap!.Image.Handle, blocker.Image.Handle);
            var texels = ctx.ReadBack(shadows.BlockerMap, 4);
            var depths = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(texels);
            Assert.Equal(128 * 128, depths.Length);
            Assert.All(depths.ToArray(), z => Assert.Equal(1f, z));
        }
        Assert.True(device!.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", device.ValidationLog));
    }
}
