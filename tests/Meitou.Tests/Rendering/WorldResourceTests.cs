using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan;
using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;
using Texture = Meitou.Rendering.Gpu.Texture;

namespace Meitou.Tests.Rendering;

/// <summary>
/// Phase 8 stage 1 (docs/renderer-native.md 8): the objects' and foliage's meshes and textures are native resources. These check that what the
/// native code makes is what VkGl made from the GL calls it replaced: the vertex attributes of the GL vertex arrays,
/// and the mip levels VkGl's <c>GenerateMipmap</c> blitted. Synchronisation validation on.
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

    /// <summary>A GL vertex array as the objects' (all seven attributes) or the foliage's (the first five) code made it before phase 8.</summary>
    static uint GlVertexArray(IGl gl, bool objects, out uint vbo, out uint ebo)
    {
        vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Vertex.Size * 3), null, BufferUsageARB.StaticDraw);
        ebo = gl.GenBuffer();
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        gl.BufferData(BufferTargetARB.ElementArrayBuffer, 12, null, BufferUsageARB.StaticDraw);
        uint stride = (uint)Vertex.Size;
        void Attrib(uint index, int size, int offset)
        {
            gl.EnableVertexAttribArray(index);
            gl.VertexAttribPointer(index, size, VertexAttribPointerType.Float, false, stride, (void*)offset);
        }
        Attrib(0, 3, 0);
        Attrib(1, 3, 12);
        Attrib(2, 2, 24);
        Attrib(3, 4, 32);
        Attrib(4, 4, 48);
        if (objects)
        {
            gl.EnableVertexAttribArray(5);
            gl.VertexAttribIPointer(5, 4, VertexAttribIType.UnsignedByte, stride, (void*)64);
            Attrib(6, 4, 68);
        }
        gl.BindVertexArray(0);
        return vao;
    }

    static void SameAttributes(LegacyProgram.Attribute?[] gl, LegacyProgram.Attribute?[] native, Silk.NET.Vulkan.Buffer nativeBuffer, int count)
    {
        for (int loc = 0; loc < count; loc++)
        {
            var g = Assert.IsType<LegacyProgram.Attribute>(gl[loc]);
            var n = Assert.IsType<LegacyProgram.Attribute>(native[loc]);
            Assert.Equal(g.Format, n.Format);
            Assert.Equal(g.Stride, n.Stride);
            Assert.Equal(g.PerInstance, n.PerInstance);
            Assert.Equal(g.Buffer.Offset, n.Buffer.Offset);   // VkGl binds a static buffer at offset 0 plus the attribute's
            Assert.Equal(nativeBuffer.Handle, n.Buffer.Buffer.Handle);
        }
        for (int loc = count; loc < gl.Length; loc++) Assert.Null(gl[loc]);
    }

    [Fact]
    [Slow]
    public void Native_mesh_attributes_are_the_GL_vertex_arrays_and_the_terrain_mesh_path_takes_them()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            var ctx = gl.Context;
            foreach (bool objects in new[] { true, false })
            {
                uint vao = GlVertexArray(gl, objects, out _, out _);
                var export = interop.VertexArray(vao);
                using var vertices = DeviceBuffer.Create(ctx, (ulong)(Vertex.Size * 3), BufferUse.Vertex, "test meshes");
                using var indices = DeviceBuffer.Create(ctx, 12, BufferUse.Index, "test meshes");
                var native = objects ? ObjectMeshCache.VertexAttributes(vertices) : FoliageRenderer.VertexAttributes(vertices);
                int count = objects ? 7 : 5;
                Assert.Equal(count, native.Length);
                SameAttributes(export.Attributes, native, vertices.Handle, count);

                // The terrain's mesh path takes the same attributes and the whole index buffer.
                var mesh = MeshBindings.Of(native, indices);
                SameAttributes(export.Attributes, mesh.Attributes, vertices.Handle, count);
                Assert.Equal(indices.Handle.Handle, mesh.Elements.Buffer.Handle);
                Assert.Equal(0ul, mesh.Elements.Offset);
            }
            gl.EndFrame();
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
        using (var gl = new VkGl(d!))
        {
            IGlInterop interop = gl;
            var ctx = gl.Context;
            // The GL way (WorldTextureCache before phase 8): level 0, then GenerateMipmap.
            uint id = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, id);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, W, H, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 1000);
            gl.GenerateMipmap(TextureTarget.Texture2D);
            // The native way.
            ctx.EnsureFrame();
            using var texture = Texture.Create(ctx, new TextureDesc(Format.R8G8B8A8Unorm, W, H, levels,
                Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.TransferSrc, Name: "test textures"), ctx.Frame.PreFrame.Handle);
            ctx.Uploads.Write(texture, 0, 0, new Rect2D(new Offset2D(0, 0), new Extent2D(W, H)), pixels);
            WorldTextureCache.GenerateMipmaps(ctx, texture);
            gl.EndFrame();

            var glImage = interop.Texture(id);
            Assert.Equal(levels, glImage.Desc.Levels);
            for (int level = 0; level < levels; level++)
                Assert.Equal(ReadLevel(d!, glImage.Image, W, H, level), ReadLevel(d!, texture.Image, W, H, level));
        }
        ExpectClean(d!);
    }
}
