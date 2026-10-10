using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;

namespace Meitou.Rendering;

/// <summary>
/// The shore distance field round the camera (docs/render-water.md "Shore distance field"): a <see cref="Size"/>² RG32F texture of
/// (signed distance to the waterline, exposure), baked on a worker thread by <see cref="ShoreBake"/> from a <see cref="HeightSnapshot"/> and
/// rebaked when the eye has moved more than a quarter of the field's width from its centre. Sampled with linear filtering, clamped to the
/// edge, at uv = (p.xz − Rect.xy) / (Rect.zw − Rect.xy). Render thread only (<see cref="Update"/>, <see cref="Sampled"/>, <see cref="Dispose"/>).
/// </summary>
internal sealed class ShoreField : IDisposable
{
    public const int DefaultSize = 1024;
    public const float DefaultTexel = 10f;

    readonly GpuContext gpu;
    readonly int size;
    readonly float texel;
    TerrainTexture? texture;
    Task<ShoreGrid>? job;
    (float X, float Z)? centre;
    int frame;
    readonly List<(TerrainTexture Texture, int Frame)> retired = [];

    public ShoreField(GpuContext gpu, int size = DefaultSize, float texel = DefaultTexel)
    {
        this.gpu = gpu;
        this.size = size;
        this.texel = texel;
    }

    public int Size => size;
    public float Texel => texel;

    /// <summary>True once the first bake has been uploaded.</summary>
    public bool Ready => texture is not null;

    /// <summary>The world rectangle the texture covers (x0, z0, x1, z1), edge to edge.</summary>
    public Vector4 Rect { get; private set; }

    /// <summary>Milliseconds the last finished bake took on its thread.</summary>
    public double LastBakeMs { get; private set; }

    /// <summary>The sampler and view to bind this frame. Only valid when <see cref="Ready"/>.</summary>
    public SampledTexture Sampled() => texture!.Sampled();

    /// <summary>
    /// Once a frame on the render thread, with a snapshot taken on it (<see cref="TerrainRenderer.Snapshot"/>): uploads a finished bake, then
    /// starts the next when nothing has been baked yet or the eye is more than size·texel/4 from the centre in X or Z.
    /// </summary>
    public void Update(Vector3 eye, HeightSnapshot heights)
    {
        frame++;
        for (int i = retired.Count - 1; i >= 0; i--)
            if (frame - retired[i].Frame >= 4) { retired[i].Texture.Dispose(); retired.RemoveAt(i); }

        if (job is { IsCompleted: true })
        {
            var done = job;
            job = null;
            if (done.IsCompletedSuccessfully) Upload(done.Result);
            else if (done.Exception is { } ex) { centre = null; Console.WriteLine($"water    shore field bake failed: {ex.GetBaseException().Message}"); }
        }

        if (job is not null) return;
        bool far = centre is not { } c || MathF.Abs(eye.X - c.X) > size * texel / 4 || MathF.Abs(eye.Z - c.Z) > size * texel / 4;
        if (!far) return;
        var (cx, cz) = ShoreBake.SnapCentre(eye.X, eye.Z, texel);
        int n = size;
        float t = texel;
        job = Task.Run(() =>
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var grid = ShoreBake.Bake(heights.HeightAt, cx, cz, n, t);
            bakeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            return grid;
        });
        // The next bake is centred on this snapped point however long it takes, so a long bake is not restarted every frame.
        centre = (cx, cz);
    }

    long bakeTicks;

    void Upload(ShoreGrid grid)
    {
        LastBakeMs = bakeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var rg = grid.Interleaved();   // made on the worker
        TerrainTexture next;
        using (var batch = gpu.Uploads.Begin())
        {
            var t = batch.Create(new TextureDesc(Silk.NET.Vulkan.Format.R32G32Sfloat, size, size, Name: "shore field"));
            batch.Write(t, 0, 0, new Silk.NET.Vulkan.Rect2D(new(0, 0), new((uint)size, (uint)size)), MemoryMarshal.AsBytes(rg.AsSpan()));
            next = new TerrainTexture(gpu, t, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
        }
        // A frame in flight may still sample the old one: let it go a few frames later, as the terrain does with a replaced height window.
        if (texture is not null) retired.Add((texture, frame));
        texture = next;
        Rect = new Vector4(grid.X0, grid.Z0, grid.X1, grid.Z1);
        grid.Release();   // the write above copied the texels into the frame's staging: the array is the next bake's
    }

    public void Dispose()
    {
        try { job?.Wait(); } catch (AggregateException) { }
        texture?.Dispose();
        foreach (var r in retired) r.Texture.Dispose();
        retired.Clear();
        texture = null;
    }
}
