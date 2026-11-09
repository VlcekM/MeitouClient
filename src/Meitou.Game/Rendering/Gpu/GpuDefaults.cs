using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Gpu.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// What GL reads where nothing is bound, shared by VkGl and the native API (docs/renderer-native.md 3.2): a vertex buffer holding GL's current
/// generic attribute value (0, 0, 0, 1) as floats at offset 0 and as integers at <see cref="DummyIntOffset"/> (bound with stride 0 for disabled
/// attributes), and 1×1 stand-in textures per kind: (0, 0, 0, 1) for colour samplers, a depth of 1 for shadow samplers. Images are in GENERAL layout.
/// Render thread only.
/// </summary>
public sealed unsafe class GpuDefaults : IDisposable
{
    /// <summary>
    /// Byte offset of the integer (0, 0, 0, 1) in <see cref="DummyVertex"/>.
    /// </summary>
    public const ulong DummyIntOffset = 16;

    /// <summary>
    /// The device the buffer and images are made on.
    /// </summary>
    readonly VulkanDevice _device;
    /// <summary>
    /// The made stand-in textures, by sampler target slot, sampled kind and depth.
    /// </summary>
    readonly Dictionary<(int Slot, ScalarKind Kind, bool Depth), DummyTexture> _textures = [];

    /// <summary>
    /// Makes the dummy vertex buffer with (0, 0, 0, 1) as floats at offset 0 and as integers at <see cref="DummyIntOffset"/>.
    /// </summary>
    public GpuDefaults(VulkanDevice device)
    {
        this._device = device;
        DummyVertex = device.Allocator.CreateBuffer(32, BufferUsageFlags.VertexBufferBit, MemoryKind.Upload, "dummy vertex");

        var p = (float*)DummyVertex.Mapped;
        p[0] = p[1] = p[2] = 0;
        p[3] = 1;

        var ip = (int*)DummyVertex.Mapped + 4;
        ip[0] = ip[1] = ip[2] = 0;
        ip[3] = 1;
    }

    /// <summary>
    /// GL's generic attribute value for disabled vertex attributes.
    /// </summary>
    public BufferAllocation DummyVertex { get; }

    /// <summary>
    /// A 1×1 stand-in image (in GENERAL layout).
    /// </summary>
    public readonly record struct DummyTexture(ImageAllocation Image, InternalFormat Format, int Layers, int Slot);

    /// <summary>
    /// The stand-in for a sampler of target <paramref name="slot"/> (0 2D, 1 2D array, 2 cube), the sampled <paramref name="kind"/>, and
    /// <paramref name="depth"/> (a shadow sampler): created on first use.
    /// </summary>
    public DummyTexture Texture(int slot, ScalarKind kind, bool depth)
    {
        // VkGl's key: the slot and kind for colour, and the same for depth (the kind does not change a depth dummy's contents).
        var key = (slot, kind, depth);
        if (_textures.TryGetValue(key, out var made)) return made;

        var format = FormatOf(kind, depth);
        int layers = slot == 2 ? 6 : 1;
        var image = CreateImage(slot, format, layers);
        ClearImage(image, kind, depth, layers);

        var d = new DummyTexture(image, format, layers, slot);
        _textures[key] = d;
        return d;
    }

    /// <summary>
    /// Frees the dummy vertex buffer, and the stand-in images after the frames in flight.
    /// </summary>
    public void Dispose()
    {
        _device.Allocator.Free(DummyVertex);
        foreach (var t in _textures.Values) _device.DeferFree(t.Image);

        _textures.Clear();
    }

    /// <summary>
    /// The stand-in's format: 24-bit depth for a shadow sampler, R32UI or R32I for the integer kinds, else RGBA8.
    /// </summary>
    static InternalFormat FormatOf(ScalarKind kind, bool depth)
    {
        if (depth) return InternalFormat.DepthComponent24;

        return kind switch
        {
            ScalarKind.UInt => InternalFormat.R32Ui,
            ScalarKind.Int => InternalFormat.R32I,
            _ => InternalFormat.Rgba8,
        };
    }

    /// <summary>
    /// A 1x1 sampled image of <paramref name="layers"/> layers (cube-compatible for slot 2) in device-local memory.
    /// </summary>
    ImageAllocation CreateImage(int slot, InternalFormat format, int layers)
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = GlConventions.VkFormat(format),
            Extent = new Extent3D(1, 1, 1),
            MipLevels = 1,
            ArrayLayers = (uint)layers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            Flags = slot == 2 ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
        };
        return _device.Allocator.CreateImage(in info, MemoryKind.DeviceLocal, "dummy texture");
    }

    /// <summary>
    /// Moves the new image to GENERAL and clears it: 1 for a depth stand-in, (0, 0, 0, 1) for colour or the integer kinds.
    /// </summary>
    void ClearImage(ImageAllocation image, ScalarKind kind, bool depth, int layers)
    {
        var vk = _device.Vk;
        var cb = _device.BeginImmediate();
        var range = new ImageSubresourceRange(depth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit, 0, 1, 0, (uint)layers);
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.None,
            SrcAccessMask = AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Image,
            SubresourceRange = new ImageSubresourceRange(range.AspectMask, 0, Vk.RemainingMipLevels, 0, Vk.RemainingArrayLayers),
        };
        var dep = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cb, &dep);

        if (depth)
        {
            var value = new ClearDepthStencilValue(1, 0);   // shadow samplers only (conventional maps): 1 is "lit"; not DepthConvention.Clear
            vk.CmdClearDepthStencilImage(cb, image.Image, ImageLayout.General, &value, 1, &range);
        }
        else
        {
            var value = ClearColorOf(kind);
            vk.CmdClearColorImage(cb, image.Image, ImageLayout.General, &value, 1, &range);
        }

        _device.EndImmediate(cb);
    }

    /// <summary>
    /// (0, 0, 0, 1) as integers for the integer kinds, else as floats.
    /// </summary>
    static ClearColorValue ClearColorOf(ScalarKind kind)
    {
        if (kind is ScalarKind.UInt or ScalarKind.Int)
            return new ClearColorValue(uint32_0: 0, uint32_1: 0, uint32_2: 0, uint32_3: 1);

        return new ClearColorValue(0f, 0f, 0f, 1f);
    }
}
