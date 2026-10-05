using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Vulkan.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// What GL reads where nothing is bound, shared by VkGl and the native API (docs/renderer-native.md 3.2): a vertex buffer holding GL's
/// current generic attribute value (0, 0, 0, 1) as floats at offset 0 and as integers at <see cref="DummyIntOffset"/> (bound with stride 0
/// for disabled attributes), and 1×1 stand-in textures per kind: (0, 0, 0, 1) for colour samplers, a depth of 1 for shadow samplers.
/// Images are in GENERAL layout. Render thread only.
/// </summary>
public sealed unsafe class GpuDefaults : IDisposable
{
    /// <summary>Byte offset of the integer (0, 0, 0, 1) in <see cref="DummyVertex"/>.</summary>
    public const ulong DummyIntOffset = 16;

    readonly VulkanDevice device;
    readonly Dictionary<(int Slot, ScalarKind Kind, bool Depth), DummyTexture> textures = [];

    public GpuDefaults(VulkanDevice device)
    {
        this.device = device;
        DummyVertex = device.Allocator.CreateBuffer(32, BufferUsageFlags.VertexBufferBit, MemoryKind.Upload, "dummy vertex");
        var p = (float*)DummyVertex.Mapped;
        p[0] = p[1] = p[2] = 0; p[3] = 1;
        var ip = (int*)DummyVertex.Mapped + 4;
        ip[0] = ip[1] = ip[2] = 0; ip[3] = 1;
    }

    /// <summary>GL's generic attribute value for disabled vertex attributes.</summary>
    public GpuBuffer DummyVertex { get; }

    /// <summary>A 1×1 stand-in image (in GENERAL layout).</summary>
    public readonly record struct DummyTexture(GpuImage Image, Format Format, int Layers, int Slot);

    /// <summary>The stand-in for a sampler of target <paramref name="slot"/> (0 2D, 1 2D array, 2 cube), the sampled
    /// <paramref name="kind"/>, and <paramref name="depth"/> (a shadow sampler): created on first use.</summary>
    public DummyTexture Texture(int slot, ScalarKind kind, bool depth)
    {
        // VkGl's key: the slot and kind for colour, and the same for depth (the kind does not change a depth dummy's contents).
        var key = (slot, kind, depth);
        if (textures.TryGetValue(key, out var d)) return d;
        var format = depth ? Format.D32Sfloat : kind switch
        {
            ScalarKind.UInt => Format.R32Uint,
            ScalarKind.Int => Format.R32Sint,
            _ => Format.R8G8B8A8Unorm,
        };
        int layers = slot == 2 ? 6 : 1;
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = format, Extent = new Extent3D(1, 1, 1),
            MipLevels = 1, ArrayLayers = (uint)layers, Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, SharingMode = SharingMode.Exclusive,
            Flags = slot == 2 ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
        };
        var image = device.Allocator.CreateImage(in info, MemoryKind.DeviceLocal, "dummy texture");
        var vk = device.Vk;
        var cb = device.BeginImmediate();
        var range = new ImageSubresourceRange(depth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit, 0, 1, 0, (uint)layers);
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.None, SrcAccessMask = AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.AllCommandsBit, DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            OldLayout = ImageLayout.Undefined, NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Image,
            SubresourceRange = new ImageSubresourceRange(range.AspectMask, 0, Vk.RemainingMipLevels, 0, Vk.RemainingArrayLayers),
        };
        var dep = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cb, &dep);
        if (depth)
        {
            var value = new ClearDepthStencilValue(1, 0);
            vk.CmdClearDepthStencilImage(cb, image.Image, ImageLayout.General, &value, 1, &range);
        }
        else
        {
            var value = kind is ScalarKind.UInt or ScalarKind.Int ? new ClearColorValue(uint32_0: 0, uint32_1: 0, uint32_2: 0, uint32_3: 1) : new ClearColorValue(0f, 0f, 0f, 1f);
            vk.CmdClearColorImage(cb, image.Image, ImageLayout.General, &value, 1, &range);
        }
        device.EndImmediate(cb);
        d = new DummyTexture(image, format, layers, slot);
        textures[key] = d;
        return d;
    }

    public void Dispose()
    {
        device.Allocator.Free(DummyVertex);
        foreach (var t in textures.Values) device.DeferFree(t.Image);
        textures.Clear();
    }
}
