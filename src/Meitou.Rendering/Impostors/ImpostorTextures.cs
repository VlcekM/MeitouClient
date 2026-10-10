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
    int map, level;

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
            skips[i] = Math.Clamp(i == 0 ? skip : surfaceSkip, atlas.FirstLevel, atlas.Levels - 1);   // a level the atlas was read without would upload as nothing
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
            int s = Math.Clamp(t.Map == ImpostorMap.Albedo ? skip : surfaceSkip, atlas.FirstLevel, atlas.Levels - 1);
            for (int l = s; l < t.Levels.Length; l++) bytes += t.Levels[l].Length;
        }
        return bytes;
    }

    /// <summary>The levels to write: one step per (map, level kept).</summary>
    public int StepCount => LevelsOf(0) + LevelsOf(1);

    /// <summary>The bytes the next <see cref="UploadStep"/> writes (0 when complete).</summary>
    public long NextStepBytes => Complete ? 0 : sources[map].Levels[skips[map] + level].Length;

    /// <summary>Writes the next (map, level) through <paramref name="batch"/>; false when everything is written.</summary>
    public bool UploadStep(UploadBatch batch)
    {
        if (Complete) return false;
        int source = skips[map] + level;
        uint size = (uint)(Atlas.AtlasPixels >> source);
        batch.Write(maps[map], level, 0, new Rect2D(default, new Extent2D(size, size)), sources[map].Levels[source]);
        if (++level == LevelsOf(map)) (map, level) = (map + 1, 0);
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
