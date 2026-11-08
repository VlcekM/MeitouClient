using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Water;

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

    readonly IGpuContext _gpu;
    readonly int _size;
    readonly float _texel;
    TerrainTexture? _texture;
    /// <summary>The shown texture is the one-texel stand-in for a uniform field.</summary>
    bool _textureIsUniform;
    Vector2 _uniformValue;
    Task<Baked>? _job;
    (float X, float Z)? _centre;
    int _frame;
    readonly List<(TerrainTexture Texture, int Frame)> _retired = [];
    /// <summary>Full-size textures nothing samples any more, kept for the next bake (at most <see cref="KeptTextures"/>).</summary>
    readonly List<TerrainTexture> _spare = [];
    const int KeptTextures = 2;

    // The worker's arrays: the bake's, and two for the texels (one is read by the upload while the next bake fills the other).
    readonly ShoreBake.Scratch _scratch = new();
    readonly float[][] _texelBuffers = new float[2][];
    int _nextBuffer;

    // The upload in progress: a full-size texture filled a few rows a frame.
    (Baked Bake, TerrainTexture Target, int Row)? _uploading;

    public ShoreField(IGpuContext gpu, int size = DefaultSize, float texel = DefaultTexel)
    {
        this._gpu = gpu;
        this._size = size;
        this._texel = texel;
    }

    public int Size => _size;
    public float Texel => _texel;

    /// <summary>True once the first bake has been uploaded.</summary>
    public bool Ready => _texture is not null;

    /// <summary>The world rectangle the texture covers (x0, z0, x1, z1), edge to edge.</summary>
    public Vector4 Rect { get; private set; }

    /// <summary>Milliseconds the last finished bake took on its thread.</summary>
    public double LastBakeMs { get; private set; }

    /// <summary>Bakes that came out uniform (one texel), and those uploaded whole (counted when their upload is done).</summary>
    public int UniformFields { get; private set; }
    public int UploadedFields { get; private set; }

    /// <summary>The sampler and view to bind this frame. Only valid when <see cref="Ready"/>.</summary>
    public SampledTexture Sampled() => _texture!.Sampled();

    /// <summary>
    /// Once a frame on the render thread, with a snapshot taken on it (<see cref="TerrainRenderer.Snapshot"/>): writes the next rows of a field
    /// being uploaded, takes a finished bake, then starts the next when nothing has been baked yet or the eye is more than size·texel/4 from
    /// the centre in X or Z.
    /// </summary>
    public void Update(Vector3 eye, HeightSnapshot heights)
    {
        _frame++;
        for (int i = _retired.Count - 1; i >= 0; i--)
            if (_frame - _retired[i].Frame >= 4)
            {
                var done = _retired[i].Texture;
                _retired.RemoveAt(i);
                // A frame in flight may have sampled it until now: from here it is free to be written again.
                if (done.Texture.Desc.Width == _size && _spare.Count < KeptTextures) _spare.Add(done);
                else done.Dispose();
            }

        if (_uploading is not null) ContinueUpload();

        // The next bake's texels may only be taken once the last upload is whole: its buffer is the one the upload reads.
        if (_uploading is null && _job is { IsCompleted: true })
        {
            var finished = _job;
            _job = null;
            if (finished.IsCompletedSuccessfully) Take(finished.Result);
            else if (finished.Exception is { } ex) { _centre = null; Console.WriteLine($"water    shore field bake failed: {ex.GetBaseException().Message}"); }
        }

        if (_job is not null) return;
        bool far = _centre is not { } c || MathF.Abs(eye.X - c.X) > _size * _texel / 4 || MathF.Abs(eye.Z - c.Z) > _size * _texel / 4;
        if (!far) return;
        var (cx, cz) = ShoreBake.SnapCentre(eye.X, eye.Z, _texel);
        int n = _size;
        float t = _texel;
        // Two bakes in flight never share a buffer: this one fills the one the shown field's upload is not reading.
        int buffer = _nextBuffer;
        _nextBuffer ^= 1;
        _job = Task.Run(() =>
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var grid = ShoreBake.Bake(heights.HeightAt, cx, cz, n, t, scratch: _scratch);
            Baked baked;
            if (grid.IsUniform(out var value)) baked = new Baked(grid.X0, grid.Z0, grid.X1, grid.Z1, true, value, null, 0);
            else
            {
                var texels = _texelBuffers[buffer] ??= new float[n * n * 2];
                grid.InterleaveInto(texels);
                baked = new Baked(grid.X0, grid.Z0, grid.X1, grid.Z1, false, default, texels, 0);
            }
            return baked with { Ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency };
        });
        // The next bake is centred on this snapped point however long it takes, so a long bake is not restarted every frame.
        _centre = (cx, cz);
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
            if (!(_textureIsUniform && _uniformValue == baked.Value))
            {
                // One texel of the value; clamped and filtered, it reads the same everywhere on the rectangle.
                TerrainTexture next;
                var value = baked.Value;
                using (var batch = _gpu.Uploads.Begin())
                {
                    var t = batch.Create(new TextureDesc(InternalFormat.Rg32F, 1, 1, Name: "shore field"));
                    batch.Write(t, 0, 0, new PixelRect(0, 0, 1, 1), MemoryMarshal.AsBytes(new ReadOnlySpan<Vector2>(in value)));
                    next = new TerrainTexture(_gpu, t, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
                }
                Swap(next, uniform: true, baked);
                _uniformValue = baked.Value;
            }
            else Rect = new Vector4(baked.X0, baked.Z0, baked.X1, baked.Z1);
            Log(start, "uniform");
            return;
        }
        TerrainTexture target;
        if (_spare.Count > 0)
        {
            target = _spare[^1];
            _spare.RemoveAt(_spare.Count - 1);
        }
        else
        {
            using var batch = _gpu.Uploads.Begin();
            target = new TerrainTexture(_gpu, batch.Create(new TextureDesc(InternalFormat.Rg32F, _size, _size, Name: "shore field")),
                TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
        }
        _uploading = (baked, target, 0);
        // Nothing shown yet (the first field, or the screenshot's): all at once. Otherwise the old field is shown while this one is written.
        ContinueUpload(all: _texture is null);
        Log(start, "start");
    }

    /// <summary>The next <see cref="RowsPerFrame"/> rows (or all) of the field being uploaded; the swap when it is whole.</summary>
    void ContinueUpload(bool all = false)
    {
        var (baked, target, row) = _uploading!.Value;
        int rows = all ? _size - row : Math.Min(RowsPerFrame, _size - row);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        UploadProfile.Take();
        using (var batch = _gpu.Uploads.Begin())
            batch.Write(target.Texture, 0, 0, new PixelRect(0, row, (uint)_size, (uint)rows),
                MemoryMarshal.AsBytes(baked.Texels.AsSpan(row * _size * 2, rows * _size * 2)));
        row += rows;
        if (row < _size) { _uploading = (baked, target, row); Log(start, "rows"); return; }
        _uploading = null;
        UploadedFields++;
        Swap(target, uniform: false, baked);
        Log(start, "last rows");
    }

    /// <summary>The finished field replaces the shown one; the old texture waits out the frames in flight (and is then kept or freed).</summary>
    void Swap(TerrainTexture next, bool uniform, Baked baked)
    {
        // A frame in flight may still sample the old one: let it go a few frames later, as the terrain does with a replaced height window.
        if (_texture is not null) _retired.Add((_texture, _frame));
        _texture = next;
        _textureIsUniform = uniform;
        Rect = new Vector4(baked.X0, baked.Z0, baked.X1, baked.Z1);
    }

    /// <summary>With <c>--log-spikes</c>: a line for a piece of the render thread's work on the field that took 1 ms or more.</summary>
    static void Log(long start, string what)
    {
        if (!UploadProfile.On) return;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        string parts = UploadProfile.Take();
        if (ms >= 1) Console.WriteLine($"water     shore field {what}: {ms:0.0} ms on the render thread [{parts}]");
    }

    public void Dispose()
    {
        try { _job?.Wait(); } catch (AggregateException) { }
        _texture?.Dispose();
        foreach (var r in _retired) r.Texture.Dispose();
        _retired.Clear();
        foreach (var s in _spare) s.Dispose();
        _spare.Clear();
        if (_uploading is { } u) u.Target.Dispose();
        _uploading = null;
        _texture = null;
    }
}