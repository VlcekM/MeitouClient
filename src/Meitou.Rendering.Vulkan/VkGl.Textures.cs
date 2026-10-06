using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Vulkan;

public sealed unsafe partial class VkGl
{
    /// <summary>
    /// A GL texture (or the image behind a renderbuffer). The image is made when level 0 (or the first level) is specified, with
    /// the whole mip chain allocated up front (GL may define more levels later); views are cut per base/max level and swizzle.
    /// Re-specifying level 0 with another size or format makes a new image (the old one is released after the frames in flight).
    /// </summary>
    internal sealed class GlTextureObj(uint id)
    {
        public readonly uint Id = id;
        public TextureTarget Target = TextureTarget.Texture2D;
        public InternalFormat GlFormat;
        public Format Format;
        public int Width, Height, Layers = 1, Levels = 1, Samples = 1;
        public uint DefinedLevels;   // bit per level that has been specified or generated
        public GpuImage? Image;
        public bool Borrowed;        // the image belongs to someone else (the shared stand-ins, an imported native texture): views are freed, the image is not
        public Meitou.Rendering.Gpu.Texture? Exported;   // the interop's export (VkGl.Texture), until the image goes
        public readonly Dictionary<(int Base, int Count, int LayerBase, int LayerCount, ImageViewType Type, ulong Swizzle), ImageView> Views = [];
        // GL sampler state
        public TextureMinFilter MinFilter = TextureMinFilter.NearestMipmapLinear;
        public TextureMagFilter MagFilter = TextureMagFilter.Linear;
        public TextureWrapMode WrapS = TextureWrapMode.Repeat, WrapT = TextureWrapMode.Repeat, WrapR = TextureWrapMode.Repeat;
        public int BaseLevel, MaxLevel = 1000;
        public bool Compare;
        public DepthFunction CompareFunc = DepthFunction.Lequal;
        public bool TransparentBorder;
        public float Anisotropy = 1;
        public ComponentSwizzle SwizzleR = ComponentSwizzle.Identity, SwizzleG = ComponentSwizzle.Identity, SwizzleB = ComponentSwizzle.Identity, SwizzleA = ComponentSwizzle.Identity;
        // Sampler and view as last used (VkGl.SamplerAndView), valid while Version, DefinedLevels and Image are unchanged.
        public int Version, CachedVersion = -1;
        public float CachedBias;
        public bool CachedShadow;
        public uint CachedLevels;
        public GpuImage? CachedImage;
        public Silk.NET.Vulkan.Sampler CachedSampler;
        public ImageView CachedView;
        // The interop's bindless entries (VkGl.Bindless), plain and shadow: the handle and what it was registered with.
        public (BindlessHandle Handle, SampledTexture Texture)? BindlessPlain, BindlessShadow;
        public bool IsDepth => GlConventions.IsDepthFormat(Format);
        public bool IsInteger => GlConventions.IsIntegerFormat(Format);
        public bool IsCube => Target == TextureTarget.TextureCubeMap;
        public bool IsArray => Target == TextureTarget.Texture2DArray;
    }

    readonly GlTextureObj?[,] units = new GlTextureObj?[64, 3];   // per unit: 2D, 2D array, cube
    int activeUnit;
    int unpackAlignment = 4, packAlignment = 4;

    static int TargetSlot(TextureTarget t) => t switch
    {
        TextureTarget.Texture2DArray => 1,
        TextureTarget.TextureCubeMap => 2,
        _ => 0,
    };

    public uint GenTexture()
    {
        uint id = NewId();
        textures[id] = new GlTextureObj(id);
        return id;
    }

    public void DeleteTexture(uint texture)
    {
        if (!textures.Remove(texture, out var t)) return;
        DestroyTexture(t);
        for (int u = 0; u < units.GetLength(0); u++)
            for (int s = 0; s < 3; s++)
                if (units[u, s] == t) units[u, s] = null;
    }

    void DestroyTexture(GlTextureObj t)
    {
        var image = t.Borrowed ? null : t.Image;
        t.Exported?.Dispose();
        t.Exported = null;
        if (t.BindlessPlain is { } bp) Context.Bindless.Free(bp.Handle);
        if (t.BindlessShadow is { } bs) Context.Bindless.Free(bs.Handle);
        t.BindlessPlain = t.BindlessShadow = null;
        var views = t.Views.Values.ToArray();
        t.Image = null;
        t.Views.Clear();
        device.Frames.DeferDelete(() =>
        {
            foreach (var v in views) vk.DestroyImageView(dev, v, null);
            if (image is not null) device.Allocator.Free(image);
        });
    }

    public void ActiveTexture(TextureUnit texture) => activeUnit = texture - TextureUnit.Texture0;

    public void BindTexture(TextureTarget target, uint texture) { Stats.TextureBinds++; BindTextureCore(target, texture); }

    void BindTextureCore(TextureTarget target, uint texture) =>
        units[activeUnit, TargetSlot(target)] = texture != 0 && textures.TryGetValue(texture, out var t) ? Retarget(t, target) : null;

    static GlTextureObj Retarget(GlTextureObj t, TextureTarget target)
    {
        if (t.Image is null) t.Target = target;
        return t;
    }

    GlTextureObj BoundTexture(TextureTarget target)
    {
        var t = target is >= TextureTarget.TextureCubeMapPositiveX and <= TextureTarget.TextureCubeMapNegativeZ ? units[activeUnit, 2] : units[activeUnit, TargetSlot(target)];
        return t ?? throw new InvalidOperationException($"no texture bound to {target} on unit {activeUnit}");
    }

    public void TexParameter(TextureTarget target, TextureParameterName pname, int param)
    {
        var t = BoundTexture(target);
        t.Version++;
        switch (pname)
        {
            case TextureParameterName.TextureMinFilter: t.MinFilter = (TextureMinFilter)param; break;
            case TextureParameterName.TextureMagFilter: t.MagFilter = (TextureMagFilter)param; break;
            case TextureParameterName.TextureWrapS: t.WrapS = (TextureWrapMode)param; break;
            case TextureParameterName.TextureWrapT: t.WrapT = (TextureWrapMode)param; break;
            case TextureParameterName.TextureWrapR: t.WrapR = (TextureWrapMode)param; break;
            case TextureParameterName.TextureBaseLevel: t.BaseLevel = param; break;
            case TextureParameterName.TextureMaxLevel: t.MaxLevel = param; break;
            case TextureParameterName.TextureCompareMode: t.Compare = param == (int)GLEnum.CompareRefToTexture; break;
            case TextureParameterName.TextureCompareFunc: t.CompareFunc = (DepthFunction)param; break;
            case (TextureParameterName)0x8E42: t.SwizzleR = Swizzle(param); break;   // TEXTURE_SWIZZLE_R
            case (TextureParameterName)0x8E43: t.SwizzleG = Swizzle(param); break;
            case (TextureParameterName)0x8E44: t.SwizzleB = Swizzle(param); break;
            case (TextureParameterName)0x8E45: t.SwizzleA = Swizzle(param); break;
            case (TextureParameterName)0x84FE: t.Anisotropy = param; break;          // TEXTURE_MAX_ANISOTROPY
            default: break;   // LOD bias, min/max LOD: not used by the renderers
        }
    }

    public void TexParameter(TextureTarget target, TextureParameterName pname, float param)
    {
        if (pname == (TextureParameterName)0x84FE) { var t = BoundTexture(target); t.Anisotropy = param; t.Version++; }
        else TexParameter(target, pname, (int)param);
    }

    public void TexParameter(TextureTarget target, TextureParameterName pname, ReadOnlySpan<float> @params)
    {
        if (pname == TextureParameterName.TextureBorderColor)
        {
            var t = BoundTexture(target);
            t.TransparentBorder = @params.Length >= 4 && @params[3] == 0;
            t.Version++;
        }
    }

    static ComponentSwizzle Swizzle(int gl) => GlConventions.Swizzle(gl);

    public void PixelStore(PixelStoreParameter pname, int param)
    {
        if (pname == PixelStoreParameter.UnpackAlignment) unpackAlignment = param;
        else if (pname == PixelStoreParameter.PackAlignment) packAlignment = param;
    }

    internal static Format VkFormat(InternalFormat f) => GlConventions.VkFormat(f);

    static bool IsCompressed(Format f) => f is Format.BC1RgbUnormBlock or Format.BC1RgbaUnormBlock or Format.BC2UnormBlock or Format.BC3UnormBlock or Format.BC4UnormBlock or Format.BC5UnormBlock;

    static int BytesPerTexel(Format f) => f switch
    {
        Format.R8Unorm => 1,
        Format.R8G8Unorm or Format.R16Unorm or Format.R16Sfloat => 2,
        Format.R8G8B8A8Unorm or Format.R8G8B8A8Uint or Format.R16G16Sfloat or Format.R32Sfloat or Format.B10G11R11UfloatPack32 or Format.D32Sfloat or Format.D24UnormS8Uint => 4,
        Format.R16G16B16A16Sfloat or Format.R32G32Sfloat => 8,
        Format.R32G32B32A32Sfloat => 16,
        _ => throw new NotSupportedException($"texel size of {f}"),
    };

    static int BlockBytes(Format f) => f is Format.BC1RgbUnormBlock or Format.BC1RgbaUnormBlock or Format.BC4UnormBlock ? 8 : 16;

    static int FullChain(int w, int h) => 1 + (int)Math.Floor(Math.Log2(Math.Max(Math.Max(w, h), 1)));

    /// <summary>(Re)creates <paramref name="t"/>'s image for a base size, format and layer count, the whole mip chain, in GENERAL layout.</summary>
    void Allocate(GlTextureObj t, InternalFormat glFormat, int width, int height, int layers, int samples = 1, int? levels = null)
    {
        if (t.Image is not null) DestroyTexture(t);
        t.GlFormat = glFormat;
        t.Format = VkFormat(glFormat);
        t.Width = width;
        t.Height = height;
        t.Layers = t.IsCube ? 6 : layers;
        t.Samples = samples;
        t.Levels = samples > 1 ? 1 : levels ?? FullChain(width, height);
        t.DefinedLevels = 0;
        var usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit;
        if (!IsCompressed(t.Format)) usage |= t.IsDepth ? ImageUsageFlags.DepthStencilAttachmentBit : ImageUsageFlags.ColorAttachmentBit;
        // Float colour targets can also be storage images: the vendor upscalers write their output with compute (ImageOf).
        if (samples == 1 && t.Format is Format.R16G16B16A16Sfloat or Format.R32Sfloat or Format.R16G16Sfloat && SupportsStorage(t.Format)) usage |= ImageUsageFlags.StorageBit;
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = t.Format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = (uint)t.Levels,
            ArrayLayers = (uint)t.Layers,
            Samples = (SampleCountFlags)samples,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            Flags = t.IsCube ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
        };
        t.Image = device.Allocator.CreateImage(in info, MemoryKind.DeviceLocal, $"gl texture {t.Id}");
        ToGeneral(UploadCmd, t);
    }

    void ToGeneral(CommandBuffer cb, GlTextureObj t)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.None, SrcAccessMask = AccessFlags2.None,
            DstStageMask = PipelineStageFlags2.AllCommandsBit, DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            OldLayout = ImageLayout.Undefined, NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = t.Image!.Image,
            SubresourceRange = new ImageSubresourceRange(Aspect(t), 0, Vk.RemainingMipLevels, 0, Vk.RemainingArrayLayers),
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cb, &info);
    }

    static ImageAspectFlags Aspect(GlTextureObj t) => t.IsDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;

    /// <summary>A render target image (backbuffer, renderbuffers): one level.</summary>
    GlTextureObj NewRenderTexture(Format format, int width, int height, int samples, string name)
    {
        var t = new GlTextureObj(0) { Format = format, Width = width, Height = height, Samples = samples, Levels = 1, DefinedLevels = 1, MinFilter = TextureMinFilter.Linear };
        var usage = (format == Format.D32Sfloat ? ImageUsageFlags.DepthStencilAttachmentBit : ImageUsageFlags.ColorAttachmentBit) |
            ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | (samples == 1 ? ImageUsageFlags.SampledBit : 0);
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = format, Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1, ArrayLayers = 1, Samples = (SampleCountFlags)samples, Tiling = ImageTiling.Optimal, Usage = usage,
            SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined,
        };
        t.Image = device.Allocator.CreateImage(in info, MemoryKind.DeviceLocal, name);
        var cb = device.BeginImmediate();
        ToGeneral(cb, t);
        device.EndImmediate(cb);
        return t;
    }

    public void TexImage2D(TextureTarget target, int level, InternalFormat internalformat, uint width, uint height, int border, PixelFormat format, PixelType type, void* pixels)
    {
        var t = BoundTexture(target);
        int layer = target is >= TextureTarget.TextureCubeMapPositiveX and <= TextureTarget.TextureCubeMapNegativeZ ? target - TextureTarget.TextureCubeMapPositiveX : 0;
        int baseW = (int)width << level, baseH = (int)height << level;
        if (t.Image is null || (level == 0 && (t.Width != (int)width || t.Height != (int)height || t.GlFormat != internalformat)) || level >= t.Levels)
            Allocate(t, internalformat, level == 0 ? (int)width : baseW, level == 0 ? (int)height : baseH, 1);
        if (pixels is not null) UploadTexels(t, level, layer, 0, 0, (int)width, (int)height, format, type, pixels);
        t.DefinedLevels |= 1u << level;
    }

    public void TexImage2D<T>(TextureTarget target, int level, InternalFormat internalformat, uint width, uint height, int border, PixelFormat format, PixelType type, ReadOnlySpan<T> pixels) where T : unmanaged
    {
        fixed (T* p = pixels) TexImage2D(target, level, internalformat, width, height, border, format, type, p);
    }

    public void TexSubImage2D(TextureTarget target, int level, int xoffset, int yoffset, uint width, uint height, PixelFormat format, PixelType type, void* pixels)
    {
        var t = BoundTexture(target);
        int layer = target is >= TextureTarget.TextureCubeMapPositiveX and <= TextureTarget.TextureCubeMapNegativeZ ? target - TextureTarget.TextureCubeMapPositiveX : 0;
        UploadTexels(t, level, layer, xoffset, yoffset, (int)width, (int)height, format, type, pixels);
    }

    public void TexSubImage2D<T>(TextureTarget target, int level, int xoffset, int yoffset, uint width, uint height, PixelFormat format, PixelType type, ReadOnlySpan<T> pixels) where T : unmanaged
    {
        fixed (T* p = pixels) TexSubImage2D(target, level, xoffset, yoffset, width, height, format, type, p);
    }

    public void CompressedTexImage2D(TextureTarget target, int level, InternalFormat internalformat, uint width, uint height, int border, uint imageSize, void* data)
    {
        var t = BoundTexture(target);
        int layer = target is >= TextureTarget.TextureCubeMapPositiveX and <= TextureTarget.TextureCubeMapNegativeZ ? target - TextureTarget.TextureCubeMapPositiveX : 0;
        if (t.Image is null || (level == 0 && (t.Width != (int)width || t.Height != (int)height || t.GlFormat != internalformat)) || level >= t.Levels)
            Allocate(t, internalformat, level == 0 ? (int)width : (int)width << level, level == 0 ? (int)height : (int)height << level, 1);
        if (data is not null) UploadRaw(t, level, layer, 1, 0, 0, (int)width, (int)height, data, imageSize);
        t.DefinedLevels |= 1u << level;
    }

    public void CompressedTexImage3D(TextureTarget target, int level, InternalFormat internalformat, uint width, uint height, uint depth, int border, uint imageSize, void* data)
    {
        var t = BoundTexture(target);
        if (t.Image is null || (level == 0 && (t.Width != (int)width || t.Height != (int)height || t.Layers != (int)depth || t.GlFormat != internalformat)) || level >= t.Levels)
            Allocate(t, internalformat, level == 0 ? (int)width : (int)width << level, level == 0 ? (int)height : (int)height << level, (int)depth);
        if (data is not null) UploadRaw(t, level, 0, (int)depth, 0, 0, (int)width, (int)height, data, imageSize);
        t.DefinedLevels |= 1u << level;
    }

    public void CompressedTexSubImage2D(TextureTarget target, int level, int xoffset, int yoffset, uint width, uint height, InternalFormat format, uint imageSize, void* data)
    {
        var t = BoundTexture(target);
        int layer = target is >= TextureTarget.TextureCubeMapPositiveX and <= TextureTarget.TextureCubeMapNegativeZ ? target - TextureTarget.TextureCubeMapPositiveX : 0;
        UploadRaw(t, level, layer, 1, xoffset, yoffset, (int)width, (int)height, data, imageSize);
    }

    public void CompressedTexSubImage3D(TextureTarget target, int level, int xoffset, int yoffset, int zoffset, uint width, uint height, uint depth, InternalFormat format, uint imageSize, void* data) =>
        UploadRaw(BoundTexture(target), level, zoffset, (int)depth, xoffset, yoffset, (int)width, (int)height, data, imageSize);

    /// <summary>Uncompressed data in GL's (format, type), converted to the image's format where they differ, rows padded to the unpack alignment.</summary>
    void UploadTexels(GlTextureObj t, int level, int layer, int x, int y, int width, int height, PixelFormat format, PixelType type, void* pixels)
    {
        if (width <= 0 || height <= 0) return;
        int srcComponents = format switch
        {
            PixelFormat.Red or PixelFormat.RedInteger or PixelFormat.DepthComponent => 1,
            PixelFormat.RG or PixelFormat.RGInteger => 2,
            PixelFormat.Rgb or PixelFormat.RgbInteger => 3,
            _ => 4,
        };
        int srcComponentBytes = type switch
        {
            PixelType.UnsignedByte or PixelType.Byte => 1,
            PixelType.UnsignedShort or PixelType.Short or PixelType.HalfFloat => 2,
            _ => 4,
        };
        int rowBytes = width * srcComponents * srcComponentBytes;
        int pitch = (rowBytes + unpackAlignment - 1) / unpackAlignment * unpackAlignment;
        int dstTexel = BytesPerTexel(t.Format);
        int dstRow = width * dstTexel;
        var staging = Ring.Allocate((ulong)(dstRow * height), 16);
        byte* src = (byte*)pixels;
        bool direct = srcComponents * srcComponentBytes == dstTexel && !(type == PixelType.Float && t.Format is Format.R16G16B16A16Sfloat or Format.R16G16Sfloat or Format.R16Sfloat);
        for (int row = 0; row < height; row++)
        {
            byte* s = src + (long)row * pitch;
            byte* d = staging.Pointer + (long)row * dstRow;
            if (direct) System.Buffer.MemoryCopy(s, d, dstRow, rowBytes);
            else ConvertRow(s, d, width, srcComponents, type, t.Format);
        }
        CopyToImage(t, level, layer, 1, x, y, width, height, staging);
    }

    static void ConvertRow(byte* s, byte* d, int width, int srcComponents, PixelType type, Format dst)
    {
        for (int i = 0; i < width; i++)
        {
            switch (dst)
            {
                case Format.R8G8B8A8Unorm when type == PixelType.UnsignedByte:
                    d[i * 4] = s[i * srcComponents];
                    d[i * 4 + 1] = srcComponents > 1 ? s[i * srcComponents + 1] : (byte)0;
                    d[i * 4 + 2] = srcComponents > 2 ? s[i * srcComponents + 2] : (byte)0;
                    d[i * 4 + 3] = srcComponents > 3 ? s[i * srcComponents + 3] : (byte)255;
                    break;
                case Format.R16G16B16A16Sfloat or Format.R16G16Sfloat or Format.R16Sfloat when type == PixelType.Float:
                {
                    int n = dst == Format.R16G16B16A16Sfloat ? 4 : dst == Format.R16G16Sfloat ? 2 : 1;
                    float* f = (float*)s;
                    Half* h = (Half*)d;
                    for (int c = 0; c < n; c++) h[i * n + c] = c < srcComponents ? (Half)f[i * srcComponents + c] : (c == 3 ? (Half)1f : (Half)0f);
                    break;
                }
                default: throw new NotSupportedException($"texel conversion to {dst} from {srcComponents} x {type}");
            }
        }
    }

    /// <summary>Compressed (or already matching) data: blocks as stored, for <paramref name="layers"/> layers from <paramref name="layer"/>.</summary>
    void UploadRaw(GlTextureObj t, int level, int layer, int layers, int x, int y, int width, int height, void* data, uint size)
    {
        if (data is null || size == 0) return;
        var staging = Ring.Allocate(size, 16);
        System.Buffer.MemoryCopy(data, staging.Pointer, size, size);
        CopyToImage(t, level, layer, layers, x, y, width, height, staging);
    }

    void CopyToImage(GlTextureObj t, int level, int layer, int layers, int x, int y, int width, int height, RingSlice staging)
    {
        var region = new BufferImageCopy
        {
            BufferOffset = staging.Offset,
            ImageSubresource = new ImageSubresourceLayers(Aspect(t), (uint)level, (uint)layer, (uint)layers),
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        // Partial blocks at the edge of small mips: the copy extent must still be the level's real size.
        if (IsCompressed(t.Format))
        {
            int lw = Math.Max(t.Width >> level, 1), lh = Math.Max(t.Height >> level, 1);
            region.ImageExtent = new Extent3D((uint)Math.Min(width, lw - x), (uint)Math.Min(height, lh - y), 1);
        }
        var cb = UploadCmd;
        OrderUpload(cb, t.Image!.Image.Handle);
        vk.CmdCopyBufferToImage(cb, staging.Buffer, t.Image!.Image, ImageLayout.General, 1, &region);
        Stats.Uploads++;
        Stats.UploadBytes += (long)staging.Size;
    }

    public void GenerateMipmap(TextureTarget target)
    {
        var t = BoundTexture(target);
        if (t.Image is null || t.Levels < 2) return;
        // In the frame (after its uploads): the levels follow whatever was uploaded or rendered into level 0 so far.
        EndPass();
        var cb = Cmd;
        FullBarrier(cb);
        for (int level = 1; level < t.Levels; level++)
        {
            BufferWriteBarrierAll(cb);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level - 1, 0, (uint)t.Layers),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level, 0, (uint)t.Layers),
            };
            blit.SrcOffsets[1] = new Offset3D(Math.Max(t.Width >> (level - 1), 1), Math.Max(t.Height >> (level - 1), 1), 1);
            blit.DstOffsets[1] = new Offset3D(Math.Max(t.Width >> level, 1), Math.Max(t.Height >> level, 1), 1);
            vk.CmdBlitImage(cb, t.Image.Image, ImageLayout.General, t.Image.Image, ImageLayout.General, 1, &blit, Filter.Linear);
        }
        FullBarrier(cb);
        t.DefinedLevels = (1u << t.Levels) - 1;
    }

    // Images copied into by this frame's upload command buffer since its last transfer barrier.
    readonly HashSet<ulong> uploadedImages = [];
    long uploadedFrame = -1;

    /// <summary>
    /// A second copy into an image in one upload command buffer (a strip re-uploaded, a level re-specified) waits for the first: without
    /// the barrier the two copies race and either may land last (sync validation's WRITE_AFTER_WRITE; it made pixels differ between runs).
    /// </summary>
    void OrderUpload(CommandBuffer cb, ulong image)
    {
        if (uploadedFrame != device.Frames.FrameNumber) { uploadedImages.Clear(); uploadedFrame = device.Frames.FrameNumber; }
        if (uploadedImages.Add(image)) return;
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllTransferBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.AllTransferBit, DstAccessMask = AccessFlags2.TransferWriteBit | AccessFlags2.TransferReadBit,
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cb, &info);
        uploadedImages.Clear();
        uploadedImages.Add(image);
    }

    void BufferWriteBarrierAll(CommandBuffer cb)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit, SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.TransferBit, DstAccessMask = AccessFlags2.TransferReadBit,
        };
        var info = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
        vk.CmdPipelineBarrier2(cb, &info);
    }

    /// <summary>The number of levels from 0 that are defined (GL's complete range when it is used for sampling).</summary>
    static int DefinedCount(GlTextureObj t)
    {
        int n = 0;
        while (n < t.Levels && (t.DefinedLevels & (1u << n)) != 0) n++;
        return Math.Max(n, 1);
    }

    /// <summary>A view of <paramref name="t"/> for sampling, following its base/max level and swizzle.</summary>
    ImageView SampleView(GlTextureObj t)
    {
        bool mipmapped = t.MinFilter is TextureMinFilter.NearestMipmapNearest or TextureMinFilter.NearestMipmapLinear or TextureMinFilter.LinearMipmapNearest or TextureMinFilter.LinearMipmapLinear;
        int first = Math.Clamp(t.BaseLevel, 0, t.Levels - 1);
        int last = mipmapped ? Math.Min(Math.Min(t.MaxLevel, t.Levels - 1), DefinedCount(t) - 1) : first;
        int count = Math.Max(last - first + 1, 1);
        var type = t.IsCube ? ImageViewType.TypeCube : t.IsArray ? ImageViewType.Type2DArray : ImageViewType.Type2D;
        ulong swizzle = (ulong)t.SwizzleR | (ulong)t.SwizzleG << 8 | (ulong)t.SwizzleB << 16 | (ulong)t.SwizzleA << 24;
        return View(t, first, count, 0, t.Layers, type, swizzle);
    }

    /// <summary>A one-level, one-layer view to render into.</summary>
    ImageView AttachmentView(GlTextureObj t, int level, int layer) => View(t, level, 1, layer, 1, ImageViewType.Type2D, 0);

    ImageView View(GlTextureObj t, int baseLevel, int levels, int baseLayer, int layers, ImageViewType type, ulong swizzle)
    {
        var key = (baseLevel, levels, baseLayer, layers, type, swizzle);
        if (t.Views.TryGetValue(key, out var view)) return view;
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = t.Image!.Image,
            ViewType = type,
            Format = t.Format,
            Components = new ComponentMapping((ComponentSwizzle)(swizzle & 0xFF), (ComponentSwizzle)((swizzle >> 8) & 0xFF), (ComponentSwizzle)((swizzle >> 16) & 0xFF), (ComponentSwizzle)((swizzle >> 24) & 0xFF)),
            SubresourceRange = new ImageSubresourceRange(Aspect(t), (uint)baseLevel, (uint)levels, (uint)baseLayer, (uint)layers),
        };
        Check(vk.CreateImageView(dev, &info, null, out view));
        t.Views[key] = view;
        return view;
    }
}

public sealed unsafe partial class VkGl
{
    readonly Dictionary<Format, bool> storageSupport = [];

    bool SupportsStorage(Format format)
    {
        if (!storageSupport.TryGetValue(format, out bool ok))
        {
            vk.GetPhysicalDeviceFormatProperties(device.PhysicalDevice, format, out var props);
            storageSupport[format] = ok = (props.OptimalTilingFeatures & FormatFeatureFlags.StorageImageBit) != 0;
        }
        return ok;
    }

    /// <summary>
    /// The Vulkan image behind a GL texture name, with a view of its first level, for work recorded outside the GL calls
    /// (<see cref="RecordInFrame"/>: the vendor upscalers). The image stays in <see cref="ImageLayout.General"/>; whoever transitions
    /// it must return it there. Float colour textures (RGBA16F, RG16F, R32F) are created with storage usage where the device allows.
    /// </summary>
    public VkGlImage ImageOf(uint texture)
    {
        var t = textures[texture];
        if (t.Image is null) throw new InvalidOperationException($"texture {texture} has no storage");
        return new VkGlImage(t.Image.Image, AttachmentView(t, 0, 0), t.Format, t.Width, t.Height, t.Levels, t.IsDepth, t.Image.Usage);
    }
}

/// <summary>A GL texture's Vulkan image (<see cref="VkGl.ImageOf"/>): the image, a view of level 0, its format and size.</summary>
public readonly record struct VkGlImage(Image Image, ImageView View, Format Format, int Width, int Height, int Levels, bool IsDepth, ImageUsageFlags Usage);
