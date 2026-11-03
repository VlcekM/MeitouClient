using Meitou.Data.Textures;
using StbImageWriteSharp;

namespace Meitou.Rendering;

/// <summary>Writes an RGBA8 picture (rows from the top) as PNG, JPEG or BMP, by the file's extension (the formats of the game's screenshots, <c>screens.cfg</c> <c>format</c>).</summary>
public static class PictureWriter
{
    /// <summary>The JPEG quality: FreeImage's default, which Ogre's screenshot writer uses (<b>Observed</b>, not measured against a vanilla file).</summary>
    public const int JpegQuality = 75;

    public static void Write(string path, int width, int height, byte[] rgba)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".jpg" or ".bmp")) { PngWriter.Write(path, width, height, rgba); return; }
        using var stream = File.Create(path);
        var writer = new ImageWriter();
        if (extension == ".jpg") writer.WriteJpg(rgba, width, height, ColorComponents.RedGreenBlueAlpha, stream, JpegQuality);
        else writer.WriteBmp(rgba, width, height, ColorComponents.RedGreenBlueAlpha, stream);
    }
}
