using StbImageSharp;

namespace Meitou.Data.Textures;

/// <summary>A decoded texture: one or more mip levels (largest first) of the first image, as RGBA8.</summary>
/// <param name="Source">The DDS file when the texture came from one (for its format and other images).</param>
public sealed record LoadedTexture(IReadOnlyList<RgbaImage> Levels, DdsFile? Source)
{
    public int Width => Levels[0].Width;
    public int Height => Levels[0].Height;
}

/// <summary>
/// Loads the texture files the base game uses: <c>.dds</c> with <see cref="DdsReader"/> / <see cref="DdsDecoder"/>,
/// and <c>.png</c>, <c>.tga</c>, <c>.jpg</c>, <c>.bmp</c> with StbImageSharp (Unlicense OR MIT).
/// </summary>
public static class TextureLoader
{
    public static LoadedTexture LoadFile(string path, bool allMips = true)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == DdsReader.Magic)
            return FromDds(DdsReader.Read(bytes), allMips);
        return new LoadedTexture([LoadImage(bytes)], null);
    }

    public static LoadedTexture FromDds(DdsFile dds, bool allMips = true)
    {
        var levels = new List<RgbaImage>();
        for (int level = 0; level < (allMips ? dds.MipCount : 1); level++)
            levels.Add(DdsDecoder.Decode(dds, 0, level));
        return new LoadedTexture(levels, dds);
    }

    /// <summary>Decodes a PNG / TGA / JPEG / BMP image to RGBA8, rows top to bottom.</summary>
    public static RgbaImage LoadImage(byte[] bytes)
    {
        StbImage.stbi_set_flip_vertically_on_load(0);
        var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
        return new RgbaImage(image.Width, image.Height, image.Data);
    }
}
