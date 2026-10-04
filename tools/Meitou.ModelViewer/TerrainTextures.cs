using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

/// <summary>
/// GPU resources for texturing the terrain of a region (docs/formats/terrain.md, "How the terrain is textured"):
/// the biome layer textures of every biome that <c>blendinfo.dat</c> lists in a cell touching the region (as two
/// texture arrays, one layer per distinct texture pair), a parameter texture with one row per biome, the cell
/// table, the world blend map, and the region's piece of the overlay and colour maps.
/// </summary>
public sealed unsafe class TerrainTextures : IDisposable
{
    readonly GL gl;
    uint diffuseArray, normalArray, paramTexture, cellTexture, blendTexture, overlayTexture, colourTexture;

    TerrainTextures(GL gl) => this.gl = gl;

    public bool HasBiomes { get; private set; }
    public bool HasMaps { get; private set; }
    public int CellsX { get; private set; } = 1;
    public int CellsZ { get; private set; } = 1;
    /// <summary>x0, z0, 1/width, 1/depth of the overlay texture in world units.</summary>
    public Vector4 Region { get; private set; }
    /// <summary>The same for the colour texture.</summary>
    public Vector4 ColourRegion { get; private set; }
    public List<BiomeTerrain> Biomes { get; } = [];
    public List<string> Messages { get; } = [];

    /// <param name="layerSize">Edge length every layer texture is brought to (the arrays need one size).</param>
    public static TerrainTextures Build(GL gl, GameInstall install, GameDatabase db, AssetLocator assets,
        double x0, double z0, double x1, double z1, int layerSize = 512, int maxMapSize = 4096)
    {
        var t = new TerrainTextures(gl);
        try { t.BuildBiomes(install, db, assets, x0, z0, x1, z1, layerSize); }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            t.Messages.Add($"biome textures: {e.Message}");
        }
        try { t.BuildMaps(install, x0, z0, x1, z1, maxMapSize); }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            t.Messages.Add($"overlay maps: {e.Message}");
        }
        return t;
    }

    void BuildBiomes(GameInstall install, GameDatabase db, AssetLocator assets, double x0, double z0, double x1, double z1, int size)
    {
        var info = BlendInfoFile.Open(install);
        var all = BiomeTerrain.ByIndex(db);
        CellsX = info.CellsX;
        CellsZ = info.CellsZ;
        var (cx0, cz0) = info.CellOf(x0, z0);
        var (cx1, cz1) = info.CellOf(x1, z1);
        var index = new Dictionary<uint, int>();
        for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
                for (int k = 0; k < BlendInfoFile.SlotsPerCell; k++)
                {
                    uint colour = info.Slot(cx, cz, k);
                    if (colour == 0 || index.ContainsKey(colour)) continue;
                    if (!all.TryGetValue(colour, out var biome)) { Messages.Add($"blendinfo cell {cx},{cz}: no BIOMES record with index #{colour:X6}"); continue; }
                    if (Biomes.Count >= 254) continue;
                    index[colour] = Biomes.Count;
                    Biomes.Add(biome);
                }
        if (Biomes.Count == 0) return;

        // Cell table, two texels per cell: slots 0..3, then slot 4 in R; region biome index, 255 = none.
        var cells = new byte[CellsX * 2 * CellsZ * 4];
        Array.Fill(cells, (byte)255);
        for (int cz = 0; cz < CellsZ; cz++)
            for (int cx = 0; cx < CellsX; cx++)
                for (int k = 0; k < BlendInfoFile.SlotsPerCell; k++)
                    cells[(cz * CellsX * 2 + cx * 2) * 4 + k] = index.TryGetValue(info.Slot(cx, cz, k), out int b) ? (byte)b : (byte)255;
        cellTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, cellTexture);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8ui, (uint)CellsX * 2, (uint)CellsZ, 0, PixelFormat.RgbaInteger, PixelType.UnsignedByte, cells.AsSpan());
        Nearest(TextureTarget.Texture2D);

        // Distinct (diffuse, normal) pairs become array layers shared by all biomes using them.
        var pairs = new List<(string? Diffuse, string? Normal)>();
        var pairIndex = new Dictionary<(string?, string?), int>();
        var layers = new int[Biomes.Count, 6];
        for (int b = 0; b < Biomes.Count; b++)
            for (int l = 0; l < 6; l++)
            {
                var key = (Biomes[b].Diffuse[l]?.ToLowerInvariant(), Biomes[b].Normal[l]?.ToLowerInvariant());
                if (!pairIndex.TryGetValue(key, out int p)) { pairIndex[key] = p = pairs.Count; pairs.Add((Biomes[b].Diffuse[l], Biomes[b].Normal[l])); }
                layers[b, l] = p;
            }
        var diffuse = new byte[pairs.Count][];
        var normal = new byte[pairs.Count][];
        var problems = new ConcurrentBag<string>();
        Parallel.For(0, pairs.Count, p =>
        {
            diffuse[p] = LoadLayer(assets, pairs[p].Diffuse, size, [128, 128, 128, 60], problems);
            normal[p] = LoadLayer(assets, pairs[p].Normal, size, [128, 128, 255, 255], problems);
        });
        Messages.AddRange(problems.Order(StringComparer.Ordinal));
        diffuseArray = UploadArray(diffuse, size);
        normalArray = UploadArray(normal, size);

        // Parameter rows (TerrainShaders.ParamTexels texels each).
        var param = new float[Biomes.Count * TerrainShaders.ParamTexels * 4];
        for (int b = 0; b < Biomes.Count; b++)
        {
            var bt = Biomes[b];
            var tl = bt.Tiling;
            float k = bt.DistortWavelength > 0 ? 1 / bt.DistortWavelength : 0;
            Vector4[] row =
            [
                new(layers[b, 0], layers[b, 1], layers[b, 2], layers[b, 3]),
                new(layers[b, 4], layers[b, 5], bt.BrightnessFix, 0),
                new(tl[1].X, tl[1].Y, tl[2].X, tl[2].Y),   // slope, cliff
                new(tl[0].X, tl[0].Y, tl[3].X, tl[3].Y),   // base, grass
                new(tl[4].X, tl[4].Y, tl[5].X, tl[5].Y),   // dirt, road
                bt.SlopeMin, bt.SlopeMax, bt.SlopeBlend, bt.OverlayMult,
                new(bt.GroundColour, bt.FadeDistance > 0 ? 1 / bt.FadeDistance : 0),
                new(k, bt.DistortAmplitude * 0.01f, 0, 0),
            ];
            for (int i = 0; i < row.Length; i++)
            {
                int o = (b * TerrainShaders.ParamTexels + i) * 4;
                (param[o], param[o + 1], param[o + 2], param[o + 3]) = (row[i].X, row[i].Y, row[i].Z, row[i].W);
            }
        }
        paramTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, paramTexture);
        gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, TerrainShaders.ParamTexels, (uint)Biomes.Count, 0, PixelFormat.Rgba, PixelType.Float, param.AsSpan());
        Nearest(TextureTarget.Texture2D);

        var blend = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BlendMap)));
        blendTexture = WorldGl.Texture2D(gl, blend.Width, blend.Height, blend.Pixels, repeat: false, mipmaps: false);
        HasBiomes = true;
    }

    /// <summary>Decodes a texture at the mip level nearest <paramref name="size"/> and scales it to size × size.</summary>
    static byte[] LoadLayer(AssetLocator assets, string? name, int size, byte[] fallback, ConcurrentBag<string> problems)
    {
        RgbaImage? image = null;
        if (name is not null)
        {
            var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
            if (path is null) problems.Add($"terrain texture not found: {name}");
            else
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == DdsReader.Magic)
                    {
                        var dds = DdsReader.Read(bytes);
                        int level = 0;
                        while (level + 1 < dds.MipCount && Math.Max(dds.Width >> (level + 1), 1) >= size) level++;
                        image = DdsDecoder.Decode(dds, 0, level);
                    }
                    else image = TextureLoader.LoadImage(bytes);
                }
                catch (Exception e) when (e is DdsFormatException or InvalidOperationException or IOException or ArgumentException)
                {
                    problems.Add($"terrain texture {name}: {e.Message}");
                }
        }
        var result = new byte[size * size * 4];
        if (image is null)
        {
            for (int i = 0; i < result.Length; i += 4) fallback.CopyTo(result, i);
            return result;
        }
        // Box filter when shrinking, nearest when growing.
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sx0 = x * image.Width / size, sx1 = Math.Max(sx0 + 1, (x + 1) * image.Width / size);
                int sy0 = y * image.Height / size, sy1 = Math.Max(sy0 + 1, (y + 1) * image.Height / size);
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                    for (int sx = sx0; sx < sx1; sx++)
                    {
                        int o = (sy * image.Width + sx) * 4;
                        r += image.Pixels[o]; g += image.Pixels[o + 1]; b += image.Pixels[o + 2]; a += image.Pixels[o + 3]; n++;
                    }
                int d = (y * size + x) * 4;
                (result[d], result[d + 1], result[d + 2], result[d + 3]) = ((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
            }
        return result;
    }

    uint UploadArray(byte[][] layers, int size)
    {
        uint id = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2DArray, id);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgba8, (uint)size, (uint)size, (uint)layers.Length, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        for (int l = 0; l < layers.Length; l++)
            gl.TexSubImage3D<byte>(TextureTarget.Texture2DArray, 0, 0, 0, l, (uint)size, (uint)size, 1, PixelFormat.Rgba, PixelType.UnsignedByte, layers[l].AsSpan());
        gl.GenerateMipmap(TextureTarget.Texture2DArray);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        gl.TexParameter(TextureTarget.Texture2DArray, (TextureParameterName)0x84FE, 8f); // max anisotropy (GL 4.6 / EXT)
        gl.GetError();
        return id;
    }

    void Nearest(TextureTarget target)
    {
        gl.TexParameter(target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(target, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(target, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    /// <summary>Cuts the region out of the 8 × 8 tiled overlay and colour maps (each at most <paramref name="maxSize"/> pixels per side).</summary>
    void BuildMaps(GameInstall install, double x0, double z0, double x1, double z1, int maxSize)
    {
        var overlay = Compose(install, "new_overlay", x0, z0, x1, z1, maxSize, out var region);
        var colour = Compose(install, "colour", x0, z0, x1, z1, maxSize, out var colourRegion);
        if (overlay is null || colour is null) return;
        Region = region;
        ColourRegion = colourRegion;
        overlayTexture = WorldGl.Texture2D(gl, overlay.Width, overlay.Height, overlay.Pixels, repeat: false);
        colourTexture = WorldGl.Texture2D(gl, colour.Width, colour.Height, colour.Pixels, repeat: false);
        HasMaps = true;
    }

    RgbaImage? Compose(GameInstall install, string kind, double x0, double z0, double x1, double z1, int maxSize, out Vector4 region)
    {
        region = default;
        var first = TerrainMaps.OverlayTile(install, kind, 0, 0);
        if (!File.Exists(first)) { Messages.Add($"missing {first}"); return null; }
        int tile = TextureLoader.LoadImage(File.ReadAllBytes(first)).Width;
        int full = tile * TerrainMaps.OverlayTiles;
        double unit = (double)WorldLayout.WorldSize / full;
        int px0 = Math.Clamp((int)Math.Floor((x0 + WorldLayout.HalfWorldSize) / unit), 0, full - 1);
        int pz0 = Math.Clamp((int)Math.Floor((z0 + WorldLayout.HalfWorldSize) / unit), 0, full - 1);
        int px1 = Math.Clamp((int)Math.Ceiling((x1 + WorldLayout.HalfWorldSize) / unit), px0 + 1, full);
        int pz1 = Math.Clamp((int)Math.Ceiling((z1 + WorldLayout.HalfWorldSize) / unit), pz0 + 1, full);
        int step = Math.Max(1, (int)Math.Ceiling(Math.Max(px1 - px0, pz1 - pz0) / (double)maxSize));
        int w = (px1 - px0 + step - 1) / step, h = (pz1 - pz0 + step - 1) / step;
        var result = new RgbaImage(w, h);
        var tiles = new Dictionary<(int, int), RgbaImage?>();
        for (int ty = pz0 / tile; ty <= (pz1 - 1) / tile; ty++)
            for (int tx = px0 / tile; tx <= (px1 - 1) / tile; tx++)
                tiles[(tx, ty)] = null;
        Parallel.ForEach(tiles.Keys.ToList(), key =>
        {
            var path = TerrainMaps.OverlayTile(install, kind, key.Item1, key.Item2);
            var img = File.Exists(path) ? TextureLoader.LoadImage(File.ReadAllBytes(path)) : null;
            lock (tiles) tiles[key] = img;
        });
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int gx = px0 + x * step, gz = pz0 + y * step;
                var img = tiles[(gx / tile, gz / tile)];
                if (img is null) continue;
                Array.Copy(img.Pixels, ((gz % tile) * tile + gx % tile) * 4, result.Pixels, (y * w + x) * 4, 4);
            }
        double wx = px0 * unit - WorldLayout.HalfWorldSize, wz = pz0 * unit - WorldLayout.HalfWorldSize;
        region = new Vector4((float)wx, (float)wz, (float)(1 / (w * step * unit)), (float)(1 / (h * step * unit)));
        return result;
    }

    public void Bind()
    {
        uint[] units = [diffuseArray, normalArray, paramTexture, cellTexture, blendTexture, overlayTexture, colourTexture];
        for (int i = 0; i < units.Length; i++)
        {
            gl.ActiveTexture(TextureUnit.Texture0 + i);
            gl.BindTexture(i < 2 ? TextureTarget.Texture2DArray : TextureTarget.Texture2D, units[i]);
        }
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    public void Dispose()
    {
        foreach (var t in new[] { diffuseArray, normalArray, paramTexture, cellTexture, blendTexture, overlayTexture, colourTexture })
            if (t != 0) gl.DeleteTexture(t);
    }
}
