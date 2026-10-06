using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// An atlas on the GPU through <see cref="IGl"/>: one 2D texture per map with the baked levels (trilinear, clamped; the chain stops at
/// 4 × 4 pixels a frame, so <c>TEXTURE_MAX_LEVEL</c> is set). The textures the preview and, until bindless materials exist, any impostor
/// draw binds to <c>uImpostorAlbedo</c>, <c>uImpostorNormal</c> and <c>uImpostorDepth</c>.
/// </summary>
public sealed unsafe class ImpostorTextures : IDisposable
{
    readonly IGl gl;
    public uint Albedo { get; }
    public uint Normal { get; }
    public uint Depth { get; }
    public ImpostorAtlas Atlas { get; }
    public long Bytes => Atlas.Bytes;

    public ImpostorTextures(IGl gl, ImpostorAtlas atlas)
    {
        this.gl = gl;
        Atlas = atlas;
        Albedo = Upload(atlas[ImpostorMap.Albedo]!);
        Normal = Upload(atlas[ImpostorMap.Normal]!);
        Depth = Upload(atlas[ImpostorMap.Depth]!);
    }

    uint Upload(ImpostorTexture texture)
    {
        uint id = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, id);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        for (int level = 0; level < texture.Levels.Length; level++)
        {
            uint size = (uint)(Atlas.AtlasPixels >> level);
            var data = texture.Levels[level];
            fixed (byte* p = data)
            {
                if (texture.Encoding == ImpostorEncoding.Rgba8)
                {
                    // Uncompressed (ImpostorBaker.Compress = false; tests only). Open: through VkGl a 3072² RGBA8 atlas samples wrong coverage
                    // (the CPU data is right; docs/impostors.md), so the viewer no longer offers it. Each level declared without data, then
                    // filled in strips of at most 4 MB.
                    gl.TexImage2D(TextureTarget.Texture2D, level, InternalFormat.Rgba8, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
                    int strip = (int)Math.Max(1, (4 << 20) / (size * 4));
                    for (int y = 0; y < size; y += strip)
                    {
                        uint rows = (uint)Math.Min(strip, (int)size - y);
                        gl.TexSubImage2D(TextureTarget.Texture2D, level, 0, y, size, rows, PixelFormat.Rgba, PixelType.UnsignedByte, p + (long)y * size * 4);
                    }
                }
                else
                    gl.CompressedTexImage2D(TextureTarget.Texture2D, level,
                        texture.Encoding == ImpostorEncoding.Bc3 ? InternalFormat.CompressedRgbaS3TCDxt5Ext : (InternalFormat)GLEnum.CompressedRGRgtc2,
                        size, size, 0, (uint)data.Length, p);
            }
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBaseLevel, 0);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, texture.Levels.Length - 1);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return id;
    }

    public void Dispose()
    {
        gl.DeleteTexture(Albedo);
        gl.DeleteTexture(Normal);
        gl.DeleteTexture(Depth);
    }
}
