using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;
using Texture = Meitou.Rendering.Gpu.Texture;

namespace Meitou.Tests.Rendering;

/// <summary>
/// Phase 8 stage 1 (docs/renderer-native.md 8): the objects' and foliage's meshes and textures are native resources. These check that what the
/// native code makes is what VkGl made from the GL calls it replaced (compared with VkGl itself until it was deleted in stage 3): the vertex
/// attributes of the GL vertex arrays, and the mip levels VkGl's <c>GenerateMipmap</c> blitted. Synchronisation validation on.
/// </summary>
public unsafe class WorldResourceTests
{
    static VulkanDevice? TryCreate()
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    static void ExpectClean(VulkanDevice d) =>
        Assert.True(d.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));

    /// <summary>
    /// The attributes VkGl exported for the GL vertex arrays the objects' (all seven) and the foliage's (the first five) code made before
    /// phase 8: per location the format and the offset into <see cref="Vertex"/> (the stride), per vertex, a static buffer bound at the offset.
    /// Location 5 was <c>glVertexAttribIPointer</c> of four unsigned bytes.
    /// </summary>
    static readonly (Format Format, ulong Offset)[] GlAttributes =
    [
        (Format.R32G32B32Sfloat, 0), (Format.R32G32B32Sfloat, 12), (Format.R32G32Sfloat, 24), (Format.R32G32B32A32Sfloat, 32),
        (Format.R32G32B32A32Sfloat, 48), (Format.R8G8B8A8Uint, 64), (Format.R32G32B32A32Sfloat, 68),
    ];

    static void SameAttributes(LegacyProgram.Attribute?[] native, Silk.NET.Vulkan.Buffer nativeBuffer, int count)
    {
        for (int loc = 0; loc < count; loc++)
        {
            var n = Assert.IsType<LegacyProgram.Attribute>(native[loc]);
            Assert.Equal(GlAttributes[loc].Format, n.Format);
            Assert.Equal((uint)Vertex.Size, n.Stride);
            Assert.False(n.PerInstance);
            Assert.Equal(GlAttributes[loc].Offset, n.Buffer.Offset);
            Assert.Equal(nativeBuffer.Handle, n.Buffer.Buffer.Handle);
        }
        for (int loc = count; loc < native.Length; loc++) Assert.Null(native[loc]);
    }

    [Fact]
    [Slow]
    public void Native_mesh_attributes_are_the_GL_vertex_arrays_and_the_terrain_mesh_path_takes_them()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var ctx = new GpuContext(d!))
        {
            foreach (bool objects in new[] { true, false })
            {
                using var vertices = DeviceBuffer.Create(ctx, (ulong)(Vertex.Size * 3), BufferUse.Vertex, "test meshes");
                using var indices = DeviceBuffer.Create(ctx, 12, BufferUse.Index, "test meshes");
                var native = objects ? ObjectMeshCache.VertexAttributes(vertices) : FoliageRenderer.VertexAttributes(vertices);
                int count = objects ? 7 : 5;
                Assert.Equal(count, native.Length);
                SameAttributes(native, vertices.Handle, count);

                // The terrain's mesh path takes the same attributes and the whole index buffer.
                var mesh = MeshBindings.Of(native, indices);
                SameAttributes(mesh.Attributes, vertices.Handle, count);
                Assert.Equal(indices.Handle.Handle, mesh.Elements.Buffer.Handle);
                Assert.Equal(0ul, mesh.Elements.Offset);
            }
        }
        ExpectClean(d!);
    }

    /// <summary>Level <paramref name="level"/> of an RGBA8 image (GENERAL layout), read after the GPU is idle.</summary>
    static byte[] ReadLevel(VulkanDevice d, Image image, int width, int height, int level)
    {
        d.Frames.WaitAll();
        int w = Math.Max(width >> level, 1), h = Math.Max(height >> level, 1);
        ulong size = (ulong)(w * h * 4);
        var buffer = d.Allocator.CreateBuffer(size, BufferUsageFlags.TransferDstBit, MemoryKind.Readback, "readback");
        try
        {
            var cb = d.BeginImmediate();
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level, 0, 1),
                ImageExtent = new Extent3D((uint)w, (uint)h, 1),
            };
            d.Vk.CmdCopyImageToBuffer(cb, image, ImageLayout.General, buffer.Buffer, 1, &region);
            d.EndImmediate(cb);
            d.Allocator.Invalidate(buffer.Allocation);
            return new ReadOnlySpan<byte>(buffer.Mapped, (int)size).ToArray();
        }
        finally { d.Allocator.Free(buffer); }
    }

    /// <summary>What VkGl's <c>GenerateMipmap</c> recorded: each level a linear blit of the whole previous one (sizes rounded down, at least 1),
    /// in GENERAL layout, with a full barrier before each.</summary>
    static void GlMipmaps(VulkanDevice d, Image image, int width, int height, int levels)
    {
        var cb = d.BeginImmediate();
        for (int level = 1; level < levels; level++)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllCommandsBit, SrcAccessMask = AccessFlags2.MemoryWriteBit,
                DstStageMask = PipelineStageFlags2.AllCommandsBit, DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            };
            var dep = new DependencyInfo { SType = StructureType.DependencyInfo, MemoryBarrierCount = 1, PMemoryBarriers = &barrier };
            d.Vk.CmdPipelineBarrier2(cb, &dep);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D(Math.Max(width >> (level - 1), 1), Math.Max(height >> (level - 1), 1), 1);
            blit.DstOffsets[1] = new Offset3D(Math.Max(width >> level, 1), Math.Max(height >> level, 1), 1);
            d.Vk.CmdBlitImage(cb, image, ImageLayout.General, image, ImageLayout.General, 1, &blit, Filter.Linear);
        }
        d.EndImmediate(cb);
    }

    [Fact]
    [Slow]
    public void Native_mipmaps_are_the_levels_VkGl_GenerateMipmap_made()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        const int W = 64, H = 24;   // not square, not a power of two in height: the odd level sizes round down as in VkGl
        var pixels = new byte[W * H * 4];
        new Random(8).NextBytes(pixels);
        int levels = 1 + (int)Math.Floor(Math.Log2(Math.Max(W, H)));
        using (var ctx = new GpuContext(d!))
        {
            ctx.EnsureFrame();
            var desc = new TextureDesc(Format.R8G8B8A8Unorm, W, H, levels, Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.TransferSrc, Name: "test textures");
            // The GL way (WorldTextureCache before phase 8): level 0, then GenerateMipmap's blits.
            using var reference = Texture.Create(ctx, desc, ctx.Frame.PreFrame.Handle);
            ctx.Uploads.Write(reference, 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(W, H)), pixels);
            // The native way.
            using var texture = Texture.Create(ctx, desc, ctx.Frame.PreFrame.Handle);
            ctx.Uploads.Write(texture, 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(W, H)), pixels);
            WorldTextureCache.GenerateMipmaps(ctx, texture);
            ctx.EndFrame();
            d!.Frames.WaitAll();
            GlMipmaps(d, reference.Image, W, H, levels);

            for (int level = 0; level < levels; level++)
                Assert.Equal(ReadLevel(d, reference.Image, W, H, level), ReadLevel(d, texture.Image, W, H, level));
        }
        ExpectClean(d!);
    }
}
