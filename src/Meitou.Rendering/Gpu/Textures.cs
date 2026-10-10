using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using Sampler = Silk.NET.Vulkan.Sampler;

namespace Meitou.Rendering.Gpu;

[Flags]
public enum TextureUse { Sampled = 1, Storage = 2, ColourTarget = 4, DepthTarget = 8, TransferSrc = 16, TransferDst = 32, ShadingRate = 64 }

public enum TextureKind { Texture2D, Texture2DArray, Cube }

/// <summary>What a <see cref="Texture"/> is made from (docs/renderer-native.md 2.4).</summary>
public sealed record TextureDesc(Format Format, int Width, int Height, int Levels = 1, int Layers = 1, int Samples = 1,
    TextureKind Kind = TextureKind.Texture2D, TextureUse Use = TextureUse.Sampled | TextureUse.TransferDst, string Name = "")
{
    public bool IsDepth => GlConventions.IsDepthFormat(Format);
    public ImageAspectFlags Aspect => IsDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
}

/// <summary>A texture as a draw samples it: the sampler and the view (combined image sampler, GENERAL layout). <see cref="Image"/> is
/// for logs and barriers.</summary>
public readonly record struct SampledTexture(Sampler Sampler, ImageView View, Image Image)
{
    public bool IsNull => View.Handle == 0;

    // By handle: Silk.NET's handle structs are not IEquatable, so the generated equality boxed each of them (on every Bindless call).
    public bool Equals(SampledTexture other) => Sampler.Handle == other.Sampler.Handle && View.Handle == other.View.Handle && Image.Handle == other.Image.Handle;
    public override int GetHashCode() => HashCode.Combine(Sampler.Handle, View.Handle, Image.Handle);
}

/// <summary>
/// An image in GENERAL layout (docs/renderer-native.md 4.4) with views cut on demand and cached. Either owned (made by <see cref="Create"/>)
/// or borrowed (a GL texture exported by the interop: its views come from VkGl's own cache, so both sides use the same handles).
/// </summary>
public sealed unsafe class Texture : IDisposable
{
    readonly VulkanDevice device;
    readonly GpuImage? owned;
    readonly Func<int, int, int, int, ImageView>? attachmentViews;   // borrowed: VkGl's attachment views (level, 1, layer, 1)
    readonly Dictionary<(int, int, int, int, ImageViewType, int), ImageView> views = [];   // the last: the swizzle, packed (a ComponentMapping key was boxed twice per lookup, 130 MB in a minute of flight)

    Texture(VulkanDevice device, TextureDesc desc, Image image, GpuImage? owned, Func<int, int, int, int, ImageView>? attachmentViews)
    {
        this.device = device;
        Desc = desc;
        Image = image;
        this.owned = owned;
        this.attachmentViews = attachmentViews;
    }

    public TextureDesc Desc { get; }
    public Image Image { get; }
    /// <summary>The allocation behind an owned texture (null when borrowed).</summary>
    public GpuImage? Underlying => owned;
    public bool Borrowed => owned is null;

    /// <summary>A new image in GENERAL layout (the transition is recorded into <paramref name="cmd"/>, or submitted at once when null).</summary>
    public static Texture Create(GpuContext ctx, TextureDesc desc, CommandBuffer cmd = default)
    {
        var usage = (ImageUsageFlags)0;
        if (desc.Use.HasFlag(TextureUse.Sampled)) usage |= ImageUsageFlags.SampledBit;
        if (desc.Use.HasFlag(TextureUse.Storage)) usage |= ImageUsageFlags.StorageBit;
        if (desc.Use.HasFlag(TextureUse.ColourTarget)) usage |= ImageUsageFlags.ColorAttachmentBit;
        if (desc.Use.HasFlag(TextureUse.DepthTarget)) usage |= ImageUsageFlags.DepthStencilAttachmentBit;
        if (desc.Use.HasFlag(TextureUse.TransferSrc)) usage |= ImageUsageFlags.TransferSrcBit;
        if (desc.Use.HasFlag(TextureUse.TransferDst)) usage |= ImageUsageFlags.TransferDstBit;
        if (desc.Use.HasFlag(TextureUse.ShadingRate)) usage |= ImageUsageFlags.FragmentShadingRateAttachmentBitKhr;
        int layers = desc.Kind == TextureKind.Cube ? 6 * Math.Max(desc.Layers, 1) : desc.Layers;
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = desc.Format,
            Extent = new Extent3D((uint)desc.Width, (uint)desc.Height, 1), MipLevels = (uint)desc.Levels, ArrayLayers = (uint)layers,
            Samples = (SampleCountFlags)desc.Samples, Tiling = ImageTiling.Optimal, Usage = usage, SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined, Flags = desc.Kind == TextureKind.Cube ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
        };
        var image = ctx.Device.Allocator.CreateImage(in info, MemoryKind.DeviceLocal, desc.Name.Length > 0 ? desc.Name : "native texture");
        bool immediate = cmd.Handle == 0;
        var cb = immediate ? ctx.Device.BeginImmediate() : cmd;
        ToGeneral(ctx.Device.Vk, cb, image.Image, desc.Aspect);
        if (immediate) ctx.Device.EndImmediate(cb);
        return new Texture(ctx.Device, desc, image.Image, image, null);
    }

    /// <summary>A non-owning texture over someone else's image (the interop's export of a GL texture).</summary>
    public static Texture Borrow(VulkanDevice device, TextureDesc desc, Image image, Func<int, int, int, int, ImageView>? attachmentViews = null) =>
        new(device, desc, image, null, attachmentViews);

    internal static void ToGeneral(Vk vk, CommandBuffer cb, Image image, ImageAspectFlags aspect)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.None, SrcAccessMask = AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.AllCommandsBit, DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            OldLayout = ImageLayout.Undefined, NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image, SubresourceRange = new ImageSubresourceRange(aspect, 0, Vk.RemainingMipLevels, 0, Vk.RemainingArrayLayers),
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cb, &info);
    }

    /// <summary>A view of a level and layer range with a swizzle (-1 = the rest); cached on the texture.</summary>
    public ImageView View(int baseLevel = 0, int levels = -1, int baseLayer = 0, int layers = -1, ComponentMapping swizzle = default)
    {
        int levelCount = levels < 0 ? Desc.Levels - baseLevel : levels;
        int totalLayers = Desc.Kind == TextureKind.Cube ? 6 * Math.Max(Desc.Layers, 1) : Desc.Layers;
        int layerCount = layers < 0 ? totalLayers - baseLayer : layers;
        var type = Desc.Kind switch
        {
            TextureKind.Cube => ImageViewType.TypeCube,
            TextureKind.Texture2DArray => ImageViewType.Type2DArray,
            _ => layerCount > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
        };
        return View(baseLevel, levelCount, baseLayer, layerCount, type, swizzle);
    }

    /// <summary>A one-level, one-layer 2D view to render into.</summary>
    public ImageView Attachment(int level = 0, int layer = 0) =>
        attachmentViews is not null ? attachmentViews(level, 1, layer, 1) : View(level, 1, layer, 1, ImageViewType.Type2D, default);

    ImageView View(int baseLevel, int levels, int baseLayer, int layers, ImageViewType type, ComponentMapping swizzle)
    {
        var key = (baseLevel, levels, baseLayer, layers, type, (int)swizzle.R | (int)swizzle.G << 4 | (int)swizzle.B << 8 | (int)swizzle.A << 12);
        if (views.TryGetValue(key, out var view)) return view;
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = Image, ViewType = type, Format = Desc.Format, Components = swizzle,
            SubresourceRange = new ImageSubresourceRange(Desc.Aspect, (uint)baseLevel, (uint)levels, (uint)baseLayer, (uint)layers),
        };
        VulkanException.Check(device.Vk.CreateImageView(device.Device, &info, null, out view), "vkCreateImageView");
        views[key] = view;
        return view;
    }

    public void Dispose()
    {
        var list = views.Values.ToArray();
        views.Clear();
        var image = owned;
        var vk = device.Vk;
        var dev = device.Device;
        device.Frames.DeferDelete(() =>
        {
            foreach (var v in list) vk.DestroyImageView(dev, v, null);
            if (image is not null) device.Allocator.Free(image);
        });
    }
}
