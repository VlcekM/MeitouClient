using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using Texture = Meitou.Rendering.Gpu.Texture;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// An atlas on the GPU (phase 8 stage 2, docs/impostors.md "Runtime"): one native texture per map with the baked levels (the chain stops at
/// 4 × 4 pixels a frame), named "impostor atlas albedo / normal" (the F12 VRAM pie groups them), sampled trilinear and clamped through
/// one bindless entry per map (<see cref="Index"/>). Made empty by the constructor; the levels are written by <see cref="Upload"/> (all at once)
/// or step by step (<see cref="UploadStep"/>, <see cref="StepCount"/>), so a large atlas can be spread over frames. Render thread only.
/// </summary>
public sealed class ImpostorTextures : IDisposable
{
    readonly GpuContext gpu;
    readonly Texture[] maps = new Texture[2];
    readonly ImpostorTexture[] sources = new ImpostorTexture[2];
    readonly uint[] indices = new uint[2];
    float indexBias;
    bool registered;

    public ImpostorAtlas Atlas { get; }
    /// <summary>Top levels left out per map (0 albedo, 1 normal): the foliage path drops those its transition distance never needs
    /// (docs/impostors.md 7, "VRAM").</summary>
    readonly int[] skips = new int[2];
    public int SkipOf(int map) => skips[map];
    /// <summary>The levels of a map on the GPU.</summary>
    public int LevelsOf(int map) => Atlas.Levels - skips[map];
    /// <summary>The GPU bytes: the levels kept, of the maps.</summary>
    public long Bytes { get; }
    public Texture Albedo => maps[0];
    public Texture Normal => maps[1];
    /// <summary>Every level written (<see cref="UploadStep"/> ran <see cref="StepCount"/> times).</summary>
    public bool Complete => map >= 2;
    int map, level, row;

    /// <summary>The most one <see cref="UploadStep"/> writes: a level is cut into slabs of whole rows (of blocks, for the compressed maps) of about this size.
    /// A level was one step before, up to 9 MB (a 3072² BC1 albedo): the copy into staging alone took 2 to 3 ms of the render thread.</summary>
    public const int SlabBytes = 1 << 20;

    public static string AllocationName(ImpostorMap map) => map switch
    {
        ImpostorMap.Albedo => "impostor atlas albedo",
        _ => "impostor atlas normal",
    };

    /// <param name="batch">Where the textures' layout transitions are recorded (<see cref="Uploader.Begin"/>).</param>
    /// <param name="skip">Top levels of the albedo to leave out; <paramref name="surfaceSkip"/> those of the normal map (at
    /// most all but the last level).</param>
    public ImpostorTextures(GpuContext gpu, ImpostorAtlas atlas, UploadBatch batch, int skip = 0, int surfaceSkip = 0)
    {
        this.gpu = gpu;
        Atlas = atlas;
        ImpostorMap[] order = [ImpostorMap.Albedo, ImpostorMap.Normal];
        for (int i = 0; i < 2; i++)
        {
            skips[i] = Math.Clamp(i == 0 ? skip : surfaceSkip, 0, atlas.Levels - 1);
            var texture = atlas[order[i]] ?? throw new InvalidDataException($"impostor atlas {atlas.Name} has no {order[i]} map");
            sources[i] = texture;
            var format = texture.Encoding switch
            {
                ImpostorEncoding.Bc3 => Format.BC3UnormBlock,
                ImpostorEncoding.Bc1 => Format.BC1RgbaUnormBlock,
                ImpostorEncoding.Bc5 => Format.BC5UnormBlock,
                _ => Format.R8G8B8A8Unorm,
            };
            int size = atlas.AtlasPixels >> skips[i];
            maps[i] = batch.Create(new TextureDesc(format, size, size, LevelsOf(i), Name: AllocationName(order[i])));
            for (int l = skips[i]; l < texture.Levels.Length; l++) Bytes += texture.Levels[l].Length;
        }
    }

    /// <summary>The GPU bytes an atlas takes with <paramref name="skip"/> top levels of its albedo and <paramref name="surfaceSkip"/> of its normal map left out.</summary>
    public static long BytesFor(ImpostorAtlas atlas, int skip, int surfaceSkip)
    {
        long bytes = 0;
        foreach (var t in atlas.Textures)
        {
            int s = Math.Clamp(t.Map == ImpostorMap.Albedo ? skip : surfaceSkip, 0, atlas.Levels - 1);
            for (int l = s; l < t.Levels.Length; l++) bytes += t.Levels[l].Length;
        }
        return bytes;
    }

    /// <summary>The (map, level) pairs to write; a level takes <see cref="StepsOfLevel"/> steps.</summary>
    public int StepCount => LevelsOf(0) + LevelsOf(1);

    /// <summary>The rows (of texels, or of 4 x 4 blocks when compressed) of a level of <paramref name="size"/> pixels, and the bytes of one.</summary>
    static (int Rows, int RowBytes) RowsOf(ImpostorEncoding encoding, int size)
    {
        int bytes = ImpostorTexture.LevelBytes(encoding, size);
        int rows = encoding == ImpostorEncoding.Rgba8 ? size : size / 4;
        return rows > 0 && bytes % rows == 0 ? (rows, bytes / rows) : (1, bytes);
    }

    /// <summary>The steps one level of <paramref name="size"/> pixels is written in.</summary>
    public static int StepsOfLevel(ImpostorEncoding encoding, int size)
    {
        var (rows, rowBytes) = RowsOf(encoding, size);
        int perSlab = Math.Max(1, SlabBytes / rowBytes);
        return (rows + perSlab - 1) / perSlab;
    }

    /// <summary>The bytes the next <see cref="UploadStep"/> writes (0 when complete).</summary>
    public long NextStepBytes
    {
        get
        {
            if (Complete) return 0;
            int source = skips[map] + level;
            var (rows, rowBytes) = RowsOf(sources[map].Encoding, Atlas.AtlasPixels >> source);
            int perSlab = Math.Max(1, SlabBytes / rowBytes);
            return (long)Math.Min(perSlab, rows - row) * rowBytes;
        }
    }

    /// <summary>Writes the next slab of the next (map, level) through <paramref name="batch"/>; false when everything is written.</summary>
    public bool UploadStep(UploadBatch batch)
    {
        if (Complete) return false;
        int source = skips[map] + level;
        int pixels = Atlas.AtlasPixels >> source;
        var encoding = sources[map].Encoding;
        var (rows, rowBytes) = RowsOf(encoding, pixels);
        int perSlab = Math.Max(1, SlabBytes / rowBytes);
        int n = Math.Min(perSlab, rows - row);
        int unit = encoding == ImpostorEncoding.Rgba8 ? 1 : 4;   // pixel rows per row
        // A slab of whole block rows; the last one ends at the level's edge.
        int y = row * unit, height = Math.Min(n * unit, pixels - y);
        batch.Write(maps[map], level, 0, new Rect2D(new Offset2D(0, y), new Extent2D((uint)pixels, (uint)height)),
            sources[map].Levels[source].AsSpan(row * rowBytes, n * rowBytes));
        row += n;
        if (row >= rows)
        {
            row = 0;
            if (++level == LevelsOf(map)) (map, level) = (map + 1, 0);
        }
        return true;
    }

    /// <summary>Writes every level through <paramref name="batch"/>.</summary>
    public void Upload(UploadBatch batch)
    {
        while (UploadStep(batch)) { }
    }

    /// <summary>
    /// The bindless index of a map (0 albedo, 1 normal) in the 2D float array: trilinear, clamped to the edge, the baked levels, with
    /// the LOD bias <paramref name="bias"/> in the sampler. Registered when first asked for and again when the bias changes (the old entries
    /// are freed after the frames in flight). Render thread, outside recording jobs.
    /// </summary>
    public uint Index(int map, float bias)
    {
        if (!registered || bias != indexBias)
        {
            RenderJobs.AssertNotInJob();
            if (registered) foreach (var i in indices) gpu.Bindless.Free(BindlessKind.Texture2D, i);
            var sampler = gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge,
                TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge, false, DepthFunction.Lequal, transparentBorder: false, anisotropy: 1, integerFormat: false, bias));
            for (int i = 0; i < 2; i++)
                indices[i] = gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, maps[i].View(0, LevelsOf(i)), maps[i].Image));
            (registered, indexBias) = (true, bias);
        }
        return indices[map];
    }

    public void Dispose()
    {
        if (registered) foreach (var i in indices) gpu.Bindless.Free(BindlessKind.Texture2D, i);
        registered = false;
        foreach (var t in maps) t?.Dispose();
    }
}
