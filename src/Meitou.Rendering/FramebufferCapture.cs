using Meitou.Data.Textures;

using Meitou.Rendering.Gpu;
using Meitou.Rendering.Vulkan.Core;
using Vk = Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>
/// Reads the bound framebuffer back into a PNG (screenshots). Native (docs/renderer-native.md 7.5, step P): the frame so far is submitted
/// and waited for (<c>Finish</c>, as VkGl's own readback did), then the target's image is copied to host memory. Anything else (another
/// format, a multisampled or smaller target, a read framebuffer other than the draw one) goes through <c>ReadPixels</c> as before.
/// </summary>
static unsafe class FramebufferCapture
{
    public static void SavePng(IGl gl, string path, int w, int h)
    {
        var pixels = ReadNative(gl, w, h);
        if (pixels is null)
        {
            pixels = new byte[w * h * 4];
            gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            gl.ReadPixels<byte>(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        }
        // GL rows start at the bottom; PNG rows at the top. Also force opaque alpha.
        var flipped = new byte[pixels.Length];
        for (int y = 0; y < h; y++) pixels.AsSpan((h - 1 - y) * w * 4, w * 4).CopyTo(flipped.AsSpan(y * w * 4));
        for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        PngWriter.Write(path, w, h, flipped);
    }

    /// <summary>The target's RGBA8 texels (rows in the image's own order, as <c>ReadPixels</c> returns them), or null when the target is not one this can read.</summary>
    static byte[]? ReadNative(IGl gl, int w, int h)
    {
        if (gl is not IGlInterop { Context: { } ctx } interop) return null;
        // The image behind the read framebuffer is what the interop reports for the draw framebuffer: only when they are the same.
        gl.GetInteger(GetPName.DrawFramebufferBinding, out int draw);
        gl.GetInteger(GetPName.ReadFramebufferBinding, out int read);
        if (draw != read) return null;
        gl.Finish();   // submits the frame so far and waits for it (the readback below is outside the frame)
        var t = interop.CurrentTargets();
        if (t.Colour.IsNull || t.Formats.Colour != Vk.Format.R8G8B8A8Unorm || t.Formats.Samples != 1 || t.Width < w || t.Height < h) return null;
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
            device.Vk.CmdCopyImageToBuffer(cb, t.Colour.Image, Vk.ImageLayout.General, buffer.Buffer, 1, &region);
            device.EndImmediate(cb);
            device.Allocator.Invalidate(buffer.Allocation);
            return new ReadOnlySpan<byte>(buffer.Mapped, (int)size).ToArray();
        }
        finally { device.Allocator.Free(buffer); }
    }
}
