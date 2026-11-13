using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Water;

/// <summary>
/// The shore distance field round the camera (docs/render-water.md "Shore distance field"): a <see cref="Size"/>² RGBA32F texture of
/// (signed distance to the waterline, exposure, the shore waves' travel time and swell; <see cref="ShoreTravel"/>), baked on a worker thread by
/// <see cref="ShoreBake"/> from a <see cref="HeightSnapshot"/> and rebaked when the eye has moved more than a quarter of the field's width from its
/// centre. Sampled with linear filtering, clamped to the edge, at uv = (p.xz − Rect.xy) / (Rect.zw − Rect.xy). Render thread only
/// (<see cref="Update"/>, <see cref="Sampled"/>, <see cref="Dispose"/>).
/// <para>
/// A rebake costs the render thread next to nothing (2026-10-10; before, 4 to 6 ms and 8 MB of staging in one frame, up to four times a second at
/// 9000 units a second): the worker also lays the texels out (and reuses the arrays of the bake, the interleaved texels and the textures themselves
/// from one bake to the next), a field with no waterline in it at all is one texel, and the rest is written <see cref="RowsPerFrame"/> rows a frame
/// into a texture nothing samples yet, which replaces the shown one, with <see cref="Rect"/>, once whole.
/// </para>
/// </summary>
internal sealed class ShoreField : IDisposable
{
    /// <summary>
    /// Texels along the field's side by default.
    /// </summary>
    public const int DefaultSize = 1024;

    /// <summary>
    /// World units per texel by default (a 1024² field spans 10240 units).
    /// </summary>
    public const float DefaultTexel = 10f;

    /// <summary>
    /// Rows written to the texture a frame once a field is shown (128 rows of 1024 texels are 2 MB).
    /// </summary>
    public const int RowsPerFrame = 128;

    /// <summary>
    /// Full-size textures kept for reuse at most (<see cref="_spare"/>).
    /// </summary>
    const int KeptTextures = 2;

    /// <summary>
    /// A finished bake: the world rectangle, and the texels, or the one value every texel has.
    /// </summary>
    sealed record Baked(float X0, float Z0, float X1, float Z1, bool Uniform, Vector2 Value, float[]? Texels, double Ms);

    /// <summary>
    /// The walls and posts standing in the water round a point (centre, radius; X/Z segments where the buildings' collision cuts the water's
    /// height), which the shore waves do not pass (<see cref="ShoreTravel"/>). Called on the bake's worker. Setting it bakes the field again.
    /// </summary>
    public Func<Vector2, float, IReadOnlyList<(Vector2 A, Vector2 B)>>? Walls
    {
        get => _walls;
        set
        {
            _walls = value;
            _centre = null;
        }
    }

    /// <summary>
    /// The walls the game set (<see cref="Walls"/>).
    /// </summary>
    Func<Vector2, float, IReadOnlyList<(Vector2 A, Vector2 B)>>? _walls;

    /// <summary>
    /// The context the textures and their uploads go through.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// Texels along the field's side.
    /// </summary>
    readonly int _size;

    /// <summary>
    /// World units per texel.
    /// </summary>
    readonly float _texel;

    /// <summary>
    /// Textures replaced this frame or recently, with the frame they were replaced in; freed or kept four frames later (a frame in flight may
    /// sample them).
    /// </summary>
    readonly List<(TerrainTexture Texture, int Frame)> _retired = [];

    /// <summary>
    /// Full-size textures nothing samples any more, kept for the next bake (at most <see cref="KeptTextures"/>).
    /// </summary>
    readonly List<TerrainTexture> _spare = [];

    /// <summary>
    /// The worker's bake arrays (<see cref="ShoreBake.Scratch"/>), reused from one bake to the next.
    /// </summary>
    readonly ShoreBake.Scratch _scratch = new();

    /// <summary>
    /// The worker's two texel buffers (distance and exposure interleaved, <see cref="Size"/>² × 2 floats, made on first use): one is read by the
    /// upload while the next bake fills the other.
    /// </summary>
    readonly float[][] _texelBuffers = new float[2][];

    /// <summary>
    /// The shown texture (null before the first bake is uploaded).
    /// </summary>
    TerrainTexture? _texture;

    /// <summary>
    /// The shown texture is the one-texel stand-in for a uniform field.
    /// </summary>
    bool _textureIsUniform;

    /// <summary>
    /// The value of the shown one-texel stand-in.
    /// </summary>
    Vector2 _uniformValue;

    /// <summary>
    /// The bake running on the worker (null: none).
    /// </summary>
    Task<Baked>? _job;

    /// <summary>
    /// The centre the last bake was started at (null: none, or it failed; the next <see cref="Update"/> bakes).
    /// </summary>
    (float X, float Z)? _centre;

    /// <summary>
    /// Frames counted by <see cref="Update"/>.
    /// </summary>
    int _frame;

    /// <summary>
    /// The texel buffer the next bake fills (0 or 1).
    /// </summary>
    int _nextBuffer;

    /// <summary>
    /// The upload in progress: a full-size texture filled a few rows a frame.
    /// </summary>
    (Baked Bake, TerrainTexture Target, int Row)? _uploading;

    /// <summary>
    /// A field of <paramref name="size"/>² texels of <paramref name="texel"/> world units; nothing is baked until the first <see cref="Update"/>.
    /// </summary>
    public ShoreField(IGpuContext gpu, int size = DefaultSize, float texel = DefaultTexel)
    {
        this._gpu = gpu;
        this._size = size;
        this._texel = texel;
    }

    /// <summary>
    /// Texels along the field's side.
    /// </summary>
    public int Size => _size;

    /// <summary>
    /// World units per texel.
    /// </summary>
    public float Texel => _texel;

    /// <summary>
    /// True once the first bake has been uploaded.
    /// </summary>
    public bool Ready => _texture is not null;

    /// <summary>
    /// The world rectangle the texture covers (x0, z0, x1, z1), edge to edge.
    /// </summary>
    public Vector4 Rect { get; private set; }

    /// <summary>
    /// Milliseconds the last finished bake took on its thread.
    /// </summary>
    public double LastBakeMs { get; private set; }

    /// <summary>
    /// Bakes that came out uniform (one texel), and those uploaded whole (counted when their upload is done).
    /// </summary>
    public int UniformFields { get; private set; }

    /// <summary>
    /// Bakes uploaded whole (see <see cref="UniformFields"/>).
    /// </summary>
    public int UploadedFields { get; private set; }

    /// <summary>
    /// The sampler and view to bind this frame. Only valid when <see cref="Ready"/>.
    /// </summary>
    public SampledTexture Sampled() => _texture!.Sampled();

    /// <summary>
    /// Once a frame on the render thread, with a snapshot taken on it (<see cref="TerrainRenderer.Snapshot"/>): writes the next rows of a field being
    /// uploaded, takes a finished bake, then starts the next when nothing has been baked yet or the eye is more than size·texel/4 from the centre in
    /// X or Z.
    /// </summary>
    public void Update(Vector3 eye, HeightSnapshot heights)
    {
        _frame++;
        RetireOldTextures();
        if (_uploading is not null) ContinueUpload();

        // The next bake's texels may only be taken once the last upload is whole: its buffer is the one the upload reads.
        if (_uploading is null && _job is { IsCompleted: true } finished) TakeFinishedJob(finished);
        if (_job is not null) return;

        if (!IsFarFromCentre(eye)) return;

        StartBake(eye, heights);
    }

    /// <summary>
    /// Waits for a bake still running, then frees every texture.
    /// </summary>
    public void Dispose()
    {
        try
        {
            _job?.Wait();
        }
        catch (AggregateException)
        {
        }

        _texture?.Dispose();
        foreach (var r in _retired) r.Texture.Dispose();
        _retired.Clear();
        foreach (var s in _spare) s.Dispose();
        _spare.Clear();
        if (_uploading is { } u) u.Target.Dispose();
        _uploading = null;
        _texture = null;
    }

    /// <summary>
    /// Textures a few frames retired are kept for the next bake (up to <see cref="KeptTextures"/>) or freed.
    /// </summary>
    void RetireOldTextures()
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (_frame - _retired[i].Frame < 4) continue;

            var done = _retired[i].Texture;
            _retired.RemoveAt(i);

            // A frame in flight may have sampled it until now: from here it is free to be written again.
            if (done.Texture.Desc.Width == _size && _spare.Count < KeptTextures) _spare.Add(done);
            else done.Dispose();
        }
    }

    /// <summary>
    /// Takes a finished bake; a failed one is logged and the next <see cref="Update"/> bakes again.
    /// </summary>
    void TakeFinishedJob(Task<Baked> finished)
    {
        _job = null;
        if (finished.IsCompletedSuccessfully)
        {
            Take(finished.Result);
        }
        else if (finished.Exception is { } ex)
        {
            _centre = null;
            Console.WriteLine($"water    shore field bake failed: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Whether the eye is more than a quarter of the field's width from the last bake's centre in X or Z (true before the first bake).
    /// </summary>
    bool IsFarFromCentre(Vector3 eye)
    {
        if (_centre is not { } c) return true;

        float reach = _size * _texel / 4;
        return MathF.Abs(eye.X - c.X) > reach || MathF.Abs(eye.Z - c.Z) > reach;
    }

    /// <summary>
    /// Starts a bake on the worker, centred on the snapped eye; the next bake is centred on the same point however long this one takes.
    /// </summary>
    void StartBake(Vector3 eye, HeightSnapshot heights)
    {
        var (cx, cz) = ShoreBake.SnapCentre(eye.X, eye.Z, _texel);
        int n = _size;
        float t = _texel;
        var wallsAt = _walls;

        // Two bakes in flight never share a buffer: this one fills the one the shown field's upload is not reading.
        int buffer = _nextBuffer;
        _nextBuffer ^= 1;
        _job = Task.Run(() => BakeOnWorker(heights, cx, cz, n, t, buffer, wallsAt));

        // The next bake is centred on this snapped point however long it takes, so a long bake is not restarted every frame.
        _centre = (cx, cz);
    }

    /// <summary>
    /// The worker's bake: the grid (its waves stopped by <paramref name="wallsAt"/>), then either its one value or its texels interleaved into
    /// buffer <paramref name="buffer"/>, timed.
    /// </summary>
    Baked BakeOnWorker(HeightSnapshot heights, float cx, float cz, int n, float t, int buffer,
        Func<Vector2, float, IReadOnlyList<(Vector2 A, Vector2 B)>>? wallsAt)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        IReadOnlyList<(Vector2 A, Vector2 B)>? cut = null;
        try
        {
            cut = wallsAt?.Invoke(new Vector2(cx, cz), n * t * 0.71f);
        }
        catch (Exception e)
        {
            Console.WriteLine($"water     shore walls not read: {e.GetBaseException().Message}");
        }

        var grid = ShoreBake.Bake(heights.HeightAt, cx, cz, n, t, scratch: _scratch, walls: cut);
        Baked baked;
        if (grid.IsUniform(out var value))
        {
            baked = new Baked(grid.X0, grid.Z0, grid.X1, grid.Z1, true, value, null, 0);
        }
        else
        {
            var texels = _texelBuffers[buffer] ??= new float[n * n * 4];
            grid.InterleaveInto(texels);
            baked = new Baked(grid.X0, grid.Z0, grid.X1, grid.Z1, false, default, texels, 0);
        }

        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return baked with { Ms = ms };
    }

    /// <summary>
    /// A finished bake: a uniform field is swapped in at once (one texel); the rest starts its upload.
    /// </summary>
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
                    var t = batch.Create(new TextureDesc(InternalFormat.Rgba32F, 1, 1, Name: "shore field"));
                    var texel4 = new Vector4(value, 0, 0);
                    batch.Write(t, 0, 0, new PixelRect(0, 0, 1, 1), MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in texel4)));
                    next = new TerrainTexture(_gpu, t, TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
                }

                Swap(next, uniform: true, baked);
                _uniformValue = baked.Value;
            }
            else
            {
                Rect = new Vector4(baked.X0, baked.Z0, baked.X1, baked.Z1);
            }

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
            target = new TerrainTexture(_gpu, batch.Create(new TextureDesc(InternalFormat.Rgba32F, _size, _size, Name: "shore field")),
                TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge);
        }

        _uploading = (baked, target, 0);

        // Nothing shown yet (the first field, or the screenshot's): all at once. Otherwise the old field is shown while this one is written.
        ContinueUpload(all: _texture is null);
        Log(start, "start");
    }

    /// <summary>
    /// The next <see cref="RowsPerFrame"/> rows (or all) of the field being uploaded; the swap when it is whole.
    /// </summary>
    void ContinueUpload(bool all = false)
    {
        var (baked, target, row) = _uploading!.Value;
        int rows = all ? _size - row : Math.Min(RowsPerFrame, _size - row);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        UploadProfile.Take();
        using (var batch = _gpu.Uploads.Begin())
        {
            batch.Write(target.Texture, 0, 0, new PixelRect(0, row, (uint)_size, (uint)rows),
                MemoryMarshal.AsBytes(baked.Texels.AsSpan(row * _size * 4, rows * _size * 4)));
        }

        row += rows;
        if (row < _size)
        {
            _uploading = (baked, target, row);
            Log(start, "rows");
            return;
        }

        _uploading = null;
        UploadedFields++;
        Swap(target, uniform: false, baked);
        Log(start, "last rows");
    }

    /// <summary>
    /// The finished field replaces the shown one; the old texture waits out the frames in flight (and is then kept or freed).
    /// </summary>
    void Swap(TerrainTexture next, bool uniform, Baked baked)
    {
        // A frame in flight may still sample the old one: let it go a few frames later, as the terrain does with a replaced height window.
        if (_texture is not null) _retired.Add((_texture, _frame));
        _texture = next;
        _textureIsUniform = uniform;
        Rect = new Vector4(baked.X0, baked.Z0, baked.X1, baked.Z1);
    }

    /// <summary>
    /// With <c>--log-spikes</c>: a line for a piece of the render thread's work on the field that took 1 ms or more.
    /// </summary>
    static void Log(long start, string what)
    {
        if (!UploadProfile.On) return;

        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        string parts = UploadProfile.Take();
        if (ms >= 1) Console.WriteLine($"water     shore field {what}: {ms:0.0} ms on the render thread [{parts}]");
    }
}
