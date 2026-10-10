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
/// <para>
/// A rebake costs the render thread next to nothing (2026-10-10; before, 4 to 6 ms and 8 MB of staging in one frame, up to four times a second
/// at 9000 units a second): the worker also lays the texels out (and reuses the arrays of the bake, the interleaved texels and the textures
/// themselves from one bake to the next), a field with no waterline in it at all is one texel, and the rest is written
/// <see cref="RowsPerFrame"/> rows a frame into a texture nothing samples yet, which replaces the shown one, with <see cref="Rect"/>, once whole.
/// </para>
/// </summary>
internal sealed class ShoreField : IDisposable
{
    public const int DefaultSize = 1024;
    public const float DefaultTexel = 10f;

    /// <summary>Rows written to the texture a frame once a field is shown (256 rows of 1024 texels are 2 MB).</summary>
    public const int RowsPerFrame = 256;

    /// <summary>A finished bake: the world rectangle, and the texels, or the one value every texel has.</summary>
    sealed record Baked(float X0, float Z0, float X1, float Z1, bool Uniform, Vector2 Value, float[]? Texels, double Ms);

    readonly GpuContext gpu;
    readonly int size;
    readonly float texel;
    TerrainTexture? texture;
    /// <summary>The shown texture is the one-texel stand-in for a uniform field.</summary>
    bool textureIsUniform;
    Vector2 uniformValue;
    Task<Baked>? job;
    (float X, float Z)? centre;
    int frame;
    readonly List<(TerrainTexture Texture, int Frame)> retired = [];
    /// <summary>Full-size textures nothing samples any more, kept for the next bake (at most <see cref="KeptTextures"/>).</summary>
    readonly List<TerrainTexture> spare = [];
    const int KeptTextures = 2;

    // The worker's arrays: the bake's, and two for the texels (one is read by the upload while the next bake fills the other).
    readonly ShoreBake.Scratch scratch = new();
    readonly float[][] texelBuffers = new float[2][];
    int nextBuffer;

    // The upload in progress: a full-size texture filled a few rows a frame.
    (Baked Bake, TerrainTexture Target, int Row)? uploading;

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

    /// <summary>Bakes that came out uniform (one texel), and those uploaded whole (counted when their upload is done).</summary>
    public int UniformFields { get; private set; }
    public int UploadedFields { get; private set; }

    /// <summary>The sampler and view to bind this frame. Only valid when <see cref="Ready"/>.</summary>
    public SampledTexture Sampled() => texture!.Sampled();

    /// <summary>
    /// Once a frame on the render thread, with a snapshot taken on it (<see cref="TerrainRenderer.Snapshot"/>): writes the next rows of a field
    /// being uploaded, takes a finished bake, then starts the next when nothing has been baked yet or the eye is more than size·texel/4 from
    /// the centre in X or Z.
    /// </summary>
    public void Update(Vector3 eye, HeightSnapshot heights)
    {
        frame++;
        for (int i = retired.Count - 1; i >= 0; i--)
            if (frame - retired[i].Frame >= 4)
            {
                var done = retired[i].Texture;
                retired.RemoveAt(i);
                // A frame in flight may have sampled it until now: from here it is free to be written again.
                if (done.Texture.Desc.Width == size && spare.Count < KeptTextures) spare.Add(done);
                else done.Dispose();
            }

        if (uploading is not null) ContinueUpload();

        // The next bake's texels may only be taken once the last upload is whole: its buffer is the one the upload reads.
        if (uploading is null && job is { IsCompleted: true })
        {
            var finished = job;
            job = null;
            if (finished.IsCompletedSuccessfully) Take(finished.Result);
            else if (finished.Exception is { } ex) { centre = null; Console.WriteLine($"water    shore field bake failed: {ex.GetBaseException().Message}"); }
        }

        if (job is not null) return;
        bool far = centre is not { } c || MathF.Abs(eye.X - c.X) > size * texel / 4 || MathF.Abs(eye.Z - c.Z) > size * texel / 4;
        if (!far) return;
        var (cx, cz) = ShoreBake.SnapCentre(eye.X, eye.Z, texel);
        int n = size;
        float t = texel;
        // Two bakes in flight never share a buffer: this one fills the one the shown field's upload is not reading.
        int buffer = nextBuffer;
        nextBuffer ^= 1;
        job = Task.Run(() =>
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var grid = ShoreBake.Bake(heights.HeightAt, cx, cz, n, t, scratch: scratch);
            Baked baked;
            if (grid.IsUniform(out var value)) baked = new Baked(grid.X0, grid.Z0, grid.X1, grid.Z1, true, value, null, 0);
            else
            {
                var texels = texelBuffers[buffer] ??= new float[n * n * 2];
                grid.InterleaveInto(texels);
                baked = new Baked(grid.X0, grid.Z0, grid.X1, grid.Z1, false, default, texels, 0);
            }
            return baked with { Ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency };
        });
        // The next bake is centred on this snapped point however long it takes, so a long bake is not restarted every frame.
        centre = (cx, cz);
    }

    /// <summary>A finished bake: a uniform field is swapped in at once (one texel); the rest starts its upload.</summary>
    void Take(Baked baked)
    {
        LastBakeMs = baked.Ms;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        UploadProfile.Take();
        if (baked.Uniform)
        {
            UniformFields++;
            if (!(textureIsUniform && uniformValue == baked.Value))
            {
                // One texel of the value; clamped and filtered, it reads the same everywhere on the rectangle.
                TerrainTexture next;
                var value = baked.Value;
                using (var batch = gpu.Uploads.Begin())
                {
                    var t = batch.Create(new TextureDesc(Silk.NET.Vulkan.Format.R32G32Sfloat, 1, 1, Name: "shore field"));
                    batch.Write(t, 0, 0, new Silk.NET.Vulkan.Rect2D(new(0, 0), new(1, 1)), MemoryMarshal.AsBytes(new ReadOnlySpan<Vector2>(in value)));
                    next = new TerrainTexture(gpu, t, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
                }
                Swap(next, uniform: true, baked);
                uniformValue = baked.Value;
            }
            else Rect = new Vector4(baked.X0, baked.Z0, baked.X1, baked.Z1);
            Log(start, "uniform");
            return;
        }
        TerrainTexture target;
        if (spare.Count > 0)
        {
            target = spare[^1];
            spare.RemoveAt(spare.Count - 1);
        }
        else
        {
            using var batch = gpu.Uploads.Begin();
            target = new TerrainTexture(gpu, batch.Create(new TextureDesc(Silk.NET.Vulkan.Format.R32G32Sfloat, size, size, Name: "shore field")),
                TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
        }
        uploading = (baked, target, 0);
        // Nothing shown yet (the first field, or the screenshot's): all at once. Otherwise the old field is shown while this one is written.
        ContinueUpload(all: texture is null);
        Log(start, "start");
    }

    /// <summary>The next <see cref="RowsPerFrame"/> rows (or all) of the field being uploaded; the swap when it is whole.</summary>
    void ContinueUpload(bool all = false)
    {
        var (baked, target, row) = uploading!.Value;
        int rows = all ? size - row : Math.Min(RowsPerFrame, size - row);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        UploadProfile.Take();
        using (var batch = gpu.Uploads.Begin())
            batch.Write(target.Texture, 0, 0, new Silk.NET.Vulkan.Rect2D(new(0, row), new((uint)size, (uint)rows)),
                MemoryMarshal.AsBytes(baked.Texels.AsSpan(row * size * 2, rows * size * 2)));
        row += rows;
        if (row < size) { uploading = (baked, target, row); Log(start, "rows"); return; }
        uploading = null;
        UploadedFields++;
        Swap(target, uniform: false, baked);
        Log(start, "last rows");
    }

    /// <summary>The finished field replaces the shown one; the old texture waits out the frames in flight (and is then kept or freed).</summary>
    void Swap(TerrainTexture next, bool uniform, Baked baked)
    {
        // A frame in flight may still sample the old one: let it go a few frames later, as the terrain does with a replaced height window.
        if (texture is not null) retired.Add((texture, frame));
        texture = next;
        textureIsUniform = uniform;
        Rect = new Vector4(baked.X0, baked.Z0, baked.X1, baked.Z1);
    }

    static readonly bool StreamLog = Environment.GetEnvironmentVariable("MEITOU_STREAM_LOG") == "1";

    /// <summary>MEITOU_STREAM_LOG=1: a line for a piece of the render thread's work on the field that took 1 ms or more.</summary>
    static void Log(long start, string what)
    {
        if (!StreamLog) return;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        string parts = UploadProfile.Take();
        if (ms >= 1) Console.WriteLine($"water     shore field {what}: {ms:0.0} ms on the render thread [{parts}]");
    }

    public void Dispose()
    {
        try { job?.Wait(); } catch (AggregateException) { }
        texture?.Dispose();
        foreach (var r in retired) r.Texture.Dispose();
        retired.Clear();
        foreach (var s in spare) s.Dispose();
        spare.Clear();
        if (uploading is { } u) u.Target.Dispose();
        uploading = null;
        texture = null;
    }
}
