using Meitou.Data.Textures;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>Reads the bound read framebuffer back into a PNG (screenshots), through IGl (VkGl: a readback of the image).</summary>
static class FramebufferCapture
{
    public static void SavePng(IGl gl, string path, int w, int h)
    {
        var pixels = new byte[w * h * 4];
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        gl.ReadPixels<byte>(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        // GL rows start at the bottom; PNG rows at the top. Also force opaque alpha.
        var flipped = new byte[pixels.Length];
        for (int y = 0; y < h; y++) pixels.AsSpan((h - 1 - y) * w * 4, w * 4).CopyTo(flipped.AsSpan(y * w * 4));
        for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        PngWriter.Write(path, w, h, flipped);
    }
}
