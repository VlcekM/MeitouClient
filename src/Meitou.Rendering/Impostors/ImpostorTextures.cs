using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using Texture = Meitou.Rendering.Gpu.Texture;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// An atlas on the GPU (phase 8 stage 2, docs/impostors.md "Runtime"): one native texture per map with the baked levels (the chain stops at
/// 4 × 4 pixels a frame), named "impostor atlas albedo / normal / depth" (the F12 VRAM pie groups them), sampled trilinear and clamped through
/// one bindless entry per map (<see cref="Index"/>). Made empty by the constructor; the levels are written by <see cref="Upload"/> (all at once)
/// or step by step (<see cref="UploadStep"/>, <see cref="StepCount"/>), so a large atlas can be spread over frames. Render thread only.
/// </summary>
public sealed class ImpostorTextures : IDisposable
{
    readonly GpuContext gpu;
    readonly Texture[] maps = new Texture[3];
    readonly ImpostorTexture[] sources = new ImpostorTexture[3];
    readonly uint[] indices = new uint[3];
    float indexBias;
    bool registered;

    public ImpostorAtlas Atlas { get; }
    public long Bytes => Atlas.Bytes;
    public Texture Albedo => maps[0];
    public Texture Normal => maps[1];
    public Texture Depth => maps[2];
    /// <summary>Every level written (<see cref="UploadStep"/> ran <see cref="StepCount"/> times).</summary>
    public bool Complete => written >= StepCount;
    int written;

    public static string AllocationName(ImpostorMap map) => map switch
    {
        ImpostorMap.Albedo => "impostor atlas albedo",
        ImpostorMap.Normal => "impostor atlas normal",
        _ => "impostor atlas depth",
    };

    /// <param name="batch">Where the textures' layout transitions are recorded (<see cref="Uploader.Begin"/>).</param>
    public ImpostorTextures(GpuContext gpu, ImpostorAtlas atlas, UploadBatch batch)
    {
        this.gpu = gpu;
        Atlas = atlas;
        ImpostorMap[] order = [ImpostorMap.Albedo, ImpostorMap.Normal, ImpostorMap.Depth];
        for (int i = 0; i < 3; i++)
        {
            var texture = atlas[order[i]] ?? throw new InvalidDataException($"impostor atlas {atlas.Name} has no {order[i]} map");
            sources[i] = texture;
            var format = texture.Encoding switch
            {
                ImpostorEncoding.Bc3 => Format.BC3UnormBlock,
                ImpostorEncoding.Bc5 => Format.BC5UnormBlock,
                _ => Format.R8G8B8A8Unorm,
            };
            maps[i] = batch.Create(new TextureDesc(format, atlas.AtlasPixels, atlas.AtlasPixels, texture.Levels.Length, Name: AllocationName(order[i])));
        }
    }

    /// <summary>The levels to write: one step per (map, level).</summary>
    public int StepCount => 3 * Atlas.Levels;

    /// <summary>Writes the next (map, level) through <paramref name="batch"/>; false when everything is written.</summary>
    public bool UploadStep(UploadBatch batch)
    {
        if (Complete) return false;
        int map = written / Atlas.Levels, level = written % Atlas.Levels;
        var texture = sources[map];
        uint size = (uint)(Atlas.AtlasPixels >> level);
        batch.Write(maps[map], level, 0, new Rect2D(default, new Extent2D(size, size)), texture.Levels[level]);
        written++;
        return true;
    }

    /// <summary>Writes every level through <paramref name="batch"/>.</summary>
    public void Upload(UploadBatch batch)
    {
        while (UploadStep(batch)) { }
    }

    /// <summary>
    /// The bindless index of a map (0 albedo, 1 normal, 2 depth) in the 2D float array: trilinear, clamped to the edge, the baked levels, with
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
            for (int i = 0; i < 3; i++)
                indices[i] = gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, maps[i].View(0, Atlas.Levels), maps[i].Image));
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
