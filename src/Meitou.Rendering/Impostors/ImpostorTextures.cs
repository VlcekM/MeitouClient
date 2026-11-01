using InternalFormat = Meitou.Rendering.Gpu.InternalFormat;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using GpuTexture = Meitou.Rendering.Gpu.GpuTexture;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// An atlas on the GPU (phase 8 stage 2, docs/impostors.md "Runtime"): one native texture per map with the baked levels (the chain stops at
/// 4 × 4 pixels a frame), named "impostor atlas albedo / normal" (the F12 VRAM pie groups them), sampled trilinear and clamped through
/// one bindless entry per map (<see cref="Index"/>). Made empty by the constructor; the levels are written by <see cref="Upload"/> (all at once)
/// or step by step (<see cref="UploadStep"/>, <see cref="StepCount"/>), so a large atlas can be spread over frames. Render thread only.
/// </summary>
public sealed class ImpostorTextures : IDisposable
{
    readonly IGpuContext gpu;
    readonly GpuTexture[] maps = new GpuTexture[2];
    readonly ImpostorTexture?[] sources = new ImpostorTexture?[2];
    readonly uint[] indices = new uint[2];
    float indexBias;
    bool registered;

    /// <summary>The atlas; once <see cref="Complete"/>, only its shape (<see cref="ImpostorAtlas.WithoutLevels"/>): the level data was the CPU copy.</summary>
    public ImpostorAtlas Atlas { get; private set; }
    readonly int levels;
    /// <summary>Top levels left out per map (0 albedo, 1 normal): the foliage path drops those its transition distance never needs
    /// (docs/impostors.md 7, "VRAM").</summary>
    readonly int[] skips = new int[2];
    public int SkipOf(int map) => skips[map];
    /// <summary>The levels of a map on the GPU.</summary>
    public int LevelsOf(int map) => levels - skips[map];
    /// <summary>The GPU bytes: the levels kept, of the maps.</summary>
    public long Bytes { get; }
    public GpuTexture Albedo => maps[0];
    public GpuTexture Normal => maps[1];
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

    static readonly ImpostorMap[] Order = [ImpostorMap.Albedo, ImpostorMap.Normal];

    /// <param name="batch">Where the textures' layout transitions are recorded (<see cref="Uploader.Begin"/>).</param>
    /// <param name="skip">Top levels of the albedo to leave out; <paramref name="surfaceSkip"/> those of the normal map (at
    /// most all but the last level).</param>
    /// <param name="made">The maps <see cref="MakeOffThread"/> made with the same skips (an entry null: made here).</param>
    public ImpostorTextures(IGpuContext gpu, ImpostorAtlas atlas, IGpuUploadBatch batch, int skip = 0, int surfaceSkip = 0, GpuTexture?[]? made = null)
    {
        this.gpu = gpu;
        Atlas = atlas;
        levels = atlas.Levels;
        var descs = Shape(atlas, skip, surfaceSkip, skips);
        for (int i = 0; i < 2; i++)
        {
            var texture = sources[i] = atlas[Order[i]]!;
            if (made?[i] is { } image) batch.Commands.InitTexture(maps[i] = image);
            else maps[i] = batch.Create(descs[i]);
            for (int l = skips[i]; l < texture.Levels.Length; l++) Bytes += texture.Levels[l].Length;
        }
    }

    /// <summary>The two maps' textures for <paramref name="skip"/> and <paramref name="surfaceSkip"/>, and the skips in effect into <paramref name="skips"/>.
    /// Streamed (atlases come and go with the view).</summary>
    static TextureDesc[] Shape(ImpostorAtlas atlas, int skip, int surfaceSkip, int[] skips)
    {
        var descs = new TextureDesc[2];
        for (int i = 0; i < 2; i++)
        {
            skips[i] = Math.Clamp(i == 0 ? skip : surfaceSkip, atlas.FirstLevel, atlas.Levels - 1);   // a level the atlas was read without would upload as nothing
            var texture = atlas[Order[i]] ?? throw new InvalidDataException($"impostor atlas {atlas.Name} has no {Order[i]} map");
            var format = texture.Encoding switch
            {
                ImpostorEncoding.Bc3 => InternalFormat.CompressedRgbaS3TCDxt5Ext,
                ImpostorEncoding.Bc1 => InternalFormat.CompressedRgbaS3TCDxt1Ext,
                ImpostorEncoding.Bc5 => InternalFormat.CompressedRGRgtc2,
                _ => InternalFormat.Rgba8,
            };
            int size = atlas.AtlasPixels >> skips[i];
            descs[i] = new TextureDesc(format, size, size, atlas.Levels - skips[i], Name: AllocationName(Order[i]), Streamed: true);
        }
        return descs;
    }

    /// <summary>
    /// The maps for the constructor with the same skips, made on a worker (<see cref="IGpuContext.CreateTextureOffThread"/>: their memory, and a new
    /// block for it, costs no frame); an entry is null where it could not be made there (the backend has no such path, or no memory now), and the
    /// constructor makes that one. Any thread; what is returned is the render thread's to pass on or dispose.
    /// </summary>
    public static GpuTexture?[] MakeOffThread(IGpuContext gpu, ImpostorAtlas atlas, int skip, int surfaceSkip)
    {
        var descs = Shape(atlas, skip, surfaceSkip, new int[2]);
        var made = new GpuTexture?[2];
        for (int i = 0; i < 2; i++)
        {
            try { made[i] = gpu.CreateTextureOffThread(descs[i]); }
            catch (Meitou.Rendering.Gpu.Core.VulkanException) { }   // no memory now: the constructor makes it (or fails) on the render thread
        }
        return made;
    }

    /// <summary>The GPU bytes an atlas takes with <paramref name="skip"/> top levels of its albedo and <paramref name="surfaceSkip"/> of its normal map left out.</summary>
    public static long BytesFor(ImpostorAtlas atlas, int skip, int surfaceSkip)
    {
        long bytes = 0;
        foreach (var t in atlas.Textures)
        {
            int s = Math.Clamp(t.Map == ImpostorMap.Albedo ? skip : surfaceSkip, atlas.FirstLevel, atlas.Levels - 1);
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
            var (rows, rowBytes) = RowsOf(sources[map]!.Encoding, Atlas.AtlasPixels >> source);
            int perSlab = Math.Max(1, SlabBytes / rowBytes);
            return (long)Math.Min(perSlab, rows - row) * rowBytes;
        }
    }

    /// <summary>Writes the next slab of the next (map, level) through <paramref name="batch"/>; false when everything is written.</summary>
    public bool UploadStep(IGpuUploadBatch batch)
    {
        if (Complete) return false;
        int source = skips[map] + level;
        int pixels = Atlas.AtlasPixels >> source;
        var texture = sources[map]!;
        var encoding = texture.Encoding;
        var (rows, rowBytes) = RowsOf(encoding, pixels);
        int perSlab = Math.Max(1, SlabBytes / rowBytes);
        int n = Math.Min(perSlab, rows - row);
        int unit = encoding == ImpostorEncoding.Rgba8 ? 1 : 4;   // pixel rows per row
        // A slab of whole block rows; the last one ends at the level's edge.
        int y = row * unit, height = Math.Min(n * unit, pixels - y);
        batch.Write(maps[map], level, 0, new PixelRect(0, y, (uint)pixels, (uint)height),
            texture.Levels[source].Span.Slice(row * rowBytes, n * rowBytes));
        row += n;
        if (row >= rows)
        {
            row = 0;
            if (++level == LevelsOf(map)) (map, level) = (map + 1, 0);
        }
        if (Complete)
        {
            // All on the GPU: drop the level data, which otherwise stays in memory for as long as the textures do (up to tens of MB an atlas).
            sources[0] = sources[1] = null;
            Atlas = Atlas.WithoutLevels();
        }
        return true;
    }

    /// <summary>Writes every level through <paramref name="batch"/>.</summary>
    public void Upload(IGpuUploadBatch batch)
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
                indices[i] = gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, maps[i].View(0, LevelsOf(i))));
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
