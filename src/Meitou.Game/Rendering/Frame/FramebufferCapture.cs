using Meitou.Data.Textures;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// Reads a target back into a picture file (screenshots; always PNG). Native (docs/renderer-native.md 7.5, 8.9): the frame so far is submitted and
/// waited for (<see cref="GpuContext.Finish"/>), then the target's image is copied to host memory. The target is RGBA8 with one sample, at least
/// <c>w</c> × <c>h</c>.
/// </summary>
public static unsafe class FramebufferCapture
{
    /// <summary>
    /// Reads <paramref name="target"/> back and writes it to <paramref name="path"/> as a PNG, creating its folder.
    /// </summary>
    public static void SavePng(GpuContext ctx, GpuTexture target, string path, int w, int h, bool topDown = false)
    {
        var picture = ReadPicture(ctx, target, w, h, topDown);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        PngWriter.Write(path, w, h, picture);
    }

    /// <summary>
    /// The target as a picture: RGBA8 rows from the top, alpha opaque.
    /// </summary>
    public static byte[] ReadPicture(GpuContext ctx, GpuTexture target, int w, int h, bool topDown = false)
    {
        var pixels = Read(ctx, target, w, h);

        // The picture's rows start at the bottom (the GL convention it is drawn with; not when topDown); PNG rows at the top.
        // Also force opaque alpha.
        var flipped = new byte[pixels.Length];
        for (int y = 0; y < h; y++)
            pixels.AsSpan((topDown ? y : h - 1 - y) * w * 4, w * 4).CopyTo(flipped.AsSpan(y * w * 4));

        for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255;
        return flipped;
    }

    /// <summary>
    /// The target's RGBA8 texels in the lower-left <paramref name="w"/> × <paramref name="h"/> (rows in the image's own order).
    /// </summary>
    public static byte[] Read(GpuContext ctx, GpuTexture target, int w, int h)
    {
        var d = target.Desc;
        if (d.Format != InternalFormat.Rgba8 || d.Samples != 1 || d.Width < w || d.Height < h)
        {
            throw new ArgumentException($"a capture reads an RGBA8 single-sample target of at least {w} x {h} " +
                                        $"({d.Format}, {d.Samples} samples, {d.Width} x {d.Height})");
        }

        ctx.Finish();   // submits the frame so far and waits for it (the readback below is outside the frame)
        var device = ctx.Device;
        ulong size = (ulong)(w * h * 4);
        var buffer = device.Allocator.CreateBuffer(size, Vk.BufferUsageFlags.TransferDstBit, MemoryKind.Readback, "screenshot");
        try
        {
            var cb = device.BeginImmediate();
            var region = new Vk.BufferImageCopy
            {
                ImageSubresource = new Vk.ImageSubresourceLayers(Vk.ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Vk.Extent3D((uint)w, (uint)h, 1),
            };
            device.Vk.CmdCopyImageToBuffer(cb, device.NativeImage(target), Vk.ImageLayout.General, buffer.Buffer, 1, &region);
            device.EndImmediate(cb);
            device.Allocator.Invalidate(buffer.Allocation);
            return new ReadOnlySpan<byte>(buffer.Mapped, (int)size).ToArray();
        }
        finally
        {
            device.Allocator.Free(buffer);
        }
    }
}