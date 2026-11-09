using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Profiling;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Water;

/// <summary>
/// The push constants all three ocean kernels declare (std430, 48 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct OceanPush
{
    public Vector4 Lengths;     // the cascades' tile sizes (units)
    public float Time;          // game seconds modulo OceanSpectrum.RepeatSeconds
    public float Blend;         // 0 the spectrum in slot From, 1 the one in slot To
    public uint From, To;
    public uint Pass;           // the FFT: 0 rows, 1 columns
    public float Choppiness;
    public float Dt;           // game seconds since the last pass (the foam's decay)
    public uint Pad1;
}

/// <summary>
/// The Meitou water's open waves (docs/render-water.md "Meitou water"): Tessendorf's FFT ocean on the GPU. <see cref="OceanSpectrum"/> gives the
/// initial amplitudes (rebuilt on a worker thread when the wind turns or changes strength, and blended in over <see cref="BlendSeconds"/>); each
/// frame a compute pass evolves them to the game clock, runs an inverse FFT over rows and then columns (Stockham, radix 2, in shared memory, four
/// complex fields per texel packing eight real ones) and writes the displacement and the slopes into one RGBA16F texture array with mips (three
/// layers each), which the water's shaders sample. Nothing runs while the clock and the spectrum stand still (a paused game). Recorded into the
/// frame's pre-frame commands; render thread only.
/// </summary>
public sealed unsafe class OceanWaves : IDisposable
{
    public const int DefaultSize = 256;
    /// <summary>
    /// Game seconds over which a new spectrum replaces the old one.
    /// </summary>
    public const float BlendSeconds = 10;
    /// <summary>
    /// Game seconds over which the foam left by a folding crest fades to a third.
    /// </summary>
    public const float FoamSeconds = 4;
    /// <summary>
    /// How far the wind must turn (radians) or change (a fraction of its speed) before the spectrum is rebuilt.
    /// </summary>
    public const float RebuildAngle = 0.17f, RebuildChange = 0.12f;

    const int Cascades = 3;

    const string Common = """
        layout(push_constant) uniform Push { vec4 lengths; float time; float blend; uint from; uint to; uint pass; float choppiness; float dt; uint pad1; } pc;
        const float PI = 3.14159265358979;
        vec2 cmul(vec2 a, vec2 b) { return vec2(a.x * b.x - a.y * b.y, a.x * b.y + a.y * b.x); }

        """;

    // Texel (x, y) of cascade c is the wave vector 2 pi (x - N/2, y - N/2) / L. Packs the eight real fields into four complex ones (each
    // field's spectrum is Hermitian, so F1 + i F2 transforms into f1 + i f2): (Dx, Dz), (Dy, dDx/dz), (dDy/dx, dDy/dz), (dDx/dx, dDz/dz),
    // with D = i k/|k| h the horizontal displacement that sharpens the crests.
    const string EvolveSource = """
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(std430, set = 0, binding = 0) readonly buffer Spectrum { vec4 h0[]; };
        layout(std430, set = 0, binding = 1) writeonly buffer FieldsA { vec4 fa[]; };
        layout(std430, set = 0, binding = 2) writeonly buffer FieldsB { vec4 fb[]; };
        void main()
        {
            uvec3 id = gl_GlobalInvocationID;
            uint c = id.z, i = id.y * N + id.x, cascade = c * N * N;
            vec4 s = mix(h0[(pc.from * 3u) * N * N + cascade + i], h0[(pc.to * 3u) * N * N + cascade + i], pc.blend);
            vec2 k = vec2(float(int(id.x) - int(N / 2u)), float(int(id.y) - int(N / 2u))) * (2.0 * PI / pc.lengths[c]);
            float kl = length(k);
            // The deep-water dispersion in units (g = 98.1 units/s^2), quantised as OceanSpectrum.Omega so the clock can wrap.
            float w0 = 2.0 * PI / 600.0;
            float omega = floor(sqrt(98.1 * kl) / w0) * w0;
            float phase = omega * pc.time;
            vec2 e = vec2(cos(phase), sin(phase));
            // h0(k) e^(-i omega t): with e^(i k.x) in the transform each wave runs along its k (the wind's way).
            vec2 h = cmul(s.xy, vec2(e.x, -e.y)) + cmul(s.zw, e);
            vec2 ih = vec2(-h.y, h.x);
            vec2 kh = kl > 0.0 ? k / kl : vec2(0.0);
            vec2 dx = kh.x * ih, dz = kh.y * ih;
            vec2 sx = k.x * ih, sz = k.y * ih;
            float inv = kl > 0.0 ? 1.0 / kl : 0.0;
            vec2 dxx = -k.x * k.x * inv * h, dzz = -k.y * k.y * inv * h, dxz = -k.x * k.y * inv * h;
            vec2 c0 = dx + vec2(-dz.y, dz.x), c1 = h + vec2(-dxz.y, dxz.x), c2 = sx + vec2(-sz.y, sz.x), c3 = dxx + vec2(-dzz.y, dzz.x);
            fa[cascade + i] = vec4(c0, c1);
            fb[cascade + i] = vec4(c2, c3);
        }
        """;

    // One workgroup per row (pass 0) or column (pass 1) of a cascade, N/2 threads each taking two elements per stage.
    const string FftSource = """
        layout(local_size_x = N / 2u) in;
        layout(std430, set = 0, binding = 1) buffer FieldsA { vec4 fa[]; };
        layout(std430, set = 0, binding = 2) buffer FieldsB { vec4 fb[]; };
        shared vec4 sa[N];
        shared vec4 sb[N];
        uint at(uint line, uint j) { return pc.pass == 0u ? line * N + j : j * N + line; }
        void main()
        {
            uint j = gl_LocalInvocationID.x, line = gl_WorkGroupID.x, base = gl_WorkGroupID.y * N * N;
            sa[j] = fa[base + at(line, j)]; sa[j + N / 2u] = fa[base + at(line, j + N / 2u)];
            sb[j] = fb[base + at(line, j)]; sb[j + N / 2u] = fb[base + at(line, j + N / 2u)];
            barrier();
            for (uint ns = 1u; ns < N; ns <<= 1u)
            {
                uint r = j % ns;
                float angle = PI * float(r) / float(ns);   // + for the inverse transform
                vec2 w = vec2(cos(angle), sin(angle));
                vec4 a0 = sa[j], a1 = sa[j + N / 2u], b0 = sb[j], b1 = sb[j + N / 2u];
                a1 = vec4(cmul(a1.xy, w), cmul(a1.zw, w));
                b1 = vec4(cmul(b1.xy, w), cmul(b1.zw, w));
                uint d = (j / ns) * 2u * ns + r;
                barrier();
                sa[d] = a0 + a1; sa[d + ns] = a0 - a1;
                sb[d] = b0 + b1; sb[d + ns] = b0 - b1;
                barrier();
            }
            fa[base + at(line, j)] = sa[j]; fa[base + at(line, j + N / 2u)] = sa[j + N / 2u];
            fb[base + at(line, j)] = sb[j]; fb[base + at(line, j + N / 2u)] = sb[j + N / 2u];
        }
        """;

    // The centred wave numbers put a (-1)^(x+y) on the transform; then the fields go out as halves for the copy into the textures. Where a
    // cascade's surface folds (its Jacobian below 0.75) foam is renewed; elsewhere it decays, so it lingers behind the crests.
    const string AssembleSource = """
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(std430, set = 0, binding = 1) readonly buffer FieldsA { vec4 fa[]; };
        layout(std430, set = 0, binding = 2) readonly buffer FieldsB { vec4 fb[]; };
        layout(std430, set = 0, binding = 3) writeonly buffer Output { uvec2 texels[]; };
        layout(std430, set = 0, binding = 4) buffer Foam { float foam[]; };
        void main()
        {
            uvec3 id = gl_GlobalInvocationID;
            uint c = id.z, i = id.y * N + id.x;
            float sign = ((id.x + id.y) & 1u) == 0u ? 1.0 : -1.0;
            vec4 a = fa[c * N * N + i] * sign, b = fb[c * N * N + i] * sign;
            float l = pc.choppiness;
            float jacobian = (1.0 + b.z * l) * (1.0 + b.w * l) - a.w * a.w * l * l;
            float f = max(foam[c * N * N + i] * exp(-pc.dt * 1.1 / 4.0), clamp((0.75 - jacobian) * 2.5, 0.0, 1.0));
            foam[c * N * N + i] = f;
            texels[c * N * N + i] = uvec2(packHalf2x16(vec2(a.x * l, a.z)), packHalf2x16(vec2(a.y * l, f)));
            texels[(3u + c) * N * N + i] = uvec2(packHalf2x16(vec2(b.x, b.y)), packHalf2x16(vec2(b.z * l, b.w * l)));
        }
        """;
    readonly IGpuContext _ctx;
    readonly GpuProgram _evolve, _fft, _assemble;
    readonly GpuComputePipeline _evolvePipe, _fftPipe, _assemblePipe;
    readonly DeviceBuffer _spectrum, _fieldsA, _fieldsB, _output, _foam;
    readonly GpuTexture _oceanMaps;   // layers 0..2 displacement, 3..5 slopes
    readonly GpuSampler _sampler;
    readonly ulong _texels;

    // The spectra: slot `to` is shown at blend 1. A new one goes into the other slot once the blend is complete.
    uint _from, _to = 1;
    double _blendStart = double.NegativeInfinity;
    Vector2 _builtDirection;
    float _builtWind = -1;
    Task<(Vector4[][] Spectrum, Vector2 Direction, float Wind)>? _building;
    Vector4[][]? _upload;
    bool _uploadBoth;
    double _seconds;
    (double Time, float Blend)? _recorded;
    double? _lastPass;
    bool _foamCleared;

    public OceanWaves(IGpuContext ctx, int size = DefaultSize)
    {
        if (size is not (64 or 128 or 256))
        {
            throw new ArgumentOutOfRangeException(nameof(size), "64, 128 or 256");
        }

        this._ctx = ctx;
        Size = size;
        _texels = (ulong)size * (ulong)size;
        _evolve = ctx.Shaders.Compute(Kernel(EvolveSource, size), "ocean evolve");
        _fft = ctx.Shaders.Compute(Kernel(FftSource, size), "ocean fft");
        _assemble = ctx.Shaders.Compute(Kernel(AssembleSource, size), "ocean assemble");
        _evolvePipe = ctx.Pipelines.Get(new ComputePipelineDesc(_evolve, "ocean evolve"));
        _fftPipe = ctx.Pipelines.Get(new ComputePipelineDesc(_fft, "ocean fft"));
        _assemblePipe = ctx.Pipelines.Get(new ComputePipelineDesc(_assemble, "ocean assemble"));
        _spectrum = DeviceBuffer.Create(ctx, 2 * Cascades * _texels * 16, BufferUse.Storage | BufferUse.TransferDst, "ocean spectrum");
        _fieldsA = DeviceBuffer.Create(ctx, Cascades * _texels * 16, BufferUse.Storage, "ocean fields a");
        _fieldsB = DeviceBuffer.Create(ctx, Cascades * _texels * 16, BufferUse.Storage, "ocean fields b");
        _output = DeviceBuffer.Create(ctx, 2 * Cascades * _texels * 8, BufferUse.Storage | BufferUse.TransferSrc, "ocean output");
        _foam = DeviceBuffer.Create(ctx, Cascades * _texels * 4, BufferUse.Storage | BufferUse.TransferDst, "ocean foam");
        int levels = 1 + (int)Math.Log2(size);
        using (var batch = ctx.Uploads.Begin())
        {
            _oceanMaps = batch.Create(new TextureDesc(InternalFormat.Rgba16F, size, size, levels, 2 * Cascades,
                Kind: TextureKind.Texture2DArray, Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.TransferSrc,
                Name: "ocean maps"));
        }
        _sampler = ctx.Samplers.Get(new SamplerDesc(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, true,
            TextureWrapMode.Repeat, TextureWrapMode.Repeat, TextureWrapMode.Repeat, false, DepthFunction.Always, false, false, 8, 0));
    }

    /// <summary>
    /// Texels along a cascade's side.
    /// </summary>
    public int Size { get; }
    /// <summary>
    /// The horizontal displacement's scale (1: Tessendorf's, sharp crests; 0: plain heights).
    /// </summary>
    public float Choppiness { get; set; } = 1.0f;
    /// <summary>
    /// The cascades' tile sizes (units), w unused.
    /// </summary>
    public Vector4 Lengths => new(OceanSpectrum.Lengths[0], OceanSpectrum.Lengths[1], OceanSpectrum.Lengths[2], 0);
    /// <summary>
    /// Layers 0..2: per cascade displacement x, y, z (units; the horizontal ones times the choppiness) and the foam (0..1):
    /// where the crests fold it is renewed, elsewhere it fades over <see cref="FoamSeconds"/>, so foam lingers behind a crest.
    /// Layers 3..5: ∂Dy/∂x, ∂Dy/∂z, ∂Dx/∂x, ∂Dz/∂z (the last two times the choppiness).
    /// </summary>
    public SampledTexture OceanMaps => new(_sampler, _oceanMaps.View());
    /// <summary>
    /// Compute passes recorded so far (a paused game records none).
    /// </summary>
    public long Dispatched { get; private set; }
    /// <summary>
    /// GPU time of the timed passes (microseconds, summed) and their count, read a few frames late.
    /// </summary>
    public double GpuMicroseconds { get; private set; }
    public long GpuTimed { get; private set; }
    readonly List<(QuerySlot Begin, QuerySlot End)> _pendingTimes = [];
    /// <summary>
    /// A spectrum has been uploaded: before that the textures are undefined and the water must stay flat.
    /// </summary>
    public bool Ready { get; private set; }

    /// <summary>
    /// For tests: the packed output (per texture, per cascade, Size² texels of four halves; displacement first).
    /// </summary>
    internal DeviceBuffer Output => _output;
    /// <summary>
    /// For tests: a spectrum of their own in both slots (<see cref="Update"/> then builds none).
    /// </summary>
    internal void SetSpectrum(Vector4[][] s)
    {
        (_upload, _uploadBoth, _builtWind) = (s, true, float.NaN);
    }

    /// <summary>
    /// For tests: the clock in game seconds.
    /// </summary>
    internal void SetTime(double s) => _seconds = s;

    /// <summary>
    /// Takes the game clock and the weather's wind (direction it blows to, speed in units per second); starts a new spectrum when the
    /// wind has moved far enough from the one shown. The first call builds one at once.
    /// </summary>
    public void Update(double gameHours, Vector2 windDirection, float windSpeed)
    {
        _seconds = gameHours * WaveSet.SecondsPerGameHour;
        if (float.IsNaN(_builtWind)) return;   // a test's own spectrum
        var dir = windDirection.LengthSquared() > 1e-8f ? Vector2.Normalize(windDirection) : Vector2.UnitX;
        float wind = OceanSpectrum.WindMetres(windSpeed);
        if (_builtWind < 0)
        {
            (_upload, _uploadBoth, _builtDirection, _builtWind) = (OceanSpectrum.Build(Size, dir, wind), true, dir, wind);
            return;
        }

        TakeFinishedSpectrum();
        if (_building is null && _upload is null && BlendAt(_seconds) >= 1 && SpectrumStale(dir, wind))
        {
            int n = Size;
            _building = Task.Run(() => (OceanSpectrum.Build(n, dir, wind), dir, wind));
        }
    }

    /// <summary>
    /// Takes a finished spectrum build once the shown one has blended in.
    /// </summary>
    void TakeFinishedSpectrum()
    {
        if (_building is { IsCompleted: true } done && _upload is null && BlendAt(_seconds) >= 1)
        {
            _building = null;
            if (done.IsCompletedSuccessfully)
            {
                (_upload, _uploadBoth, _builtDirection, _builtWind) = (done.Result.Spectrum, false, done.Result.Direction, done.Result.Wind);
            }
        }
    }

    /// <summary>
    /// Whether the wind has turned or changed far enough from the one the shown spectrum was built for.
    /// </summary>
    bool SpectrumStale(Vector2 dir, float wind)
    {
        float turn = MathF.Acos(Math.Clamp(Vector2.Dot(dir, _builtDirection), -1, 1));
        return turn > RebuildAngle || MathF.Abs(wind - _builtWind) > RebuildChange * _builtWind;
    }

    float BlendAt(double s) => (float)Math.Clamp((s - _blendStart) / BlendSeconds, 0, 1);

    /// <summary>
    /// Records this frame's update of the textures into the frame's pre-frame commands (a frame must be open), unless nothing changed.
    /// </summary>
    public void Record()
    {
        CollectTimes();
        var cmd = _ctx.Frame.PreFrame;
        bool uploaded = false;
        if (_upload is { } s)
        {
            SwapInUpload(s);
            _upload = null;
            uploaded = true;
            Ready = true;
        }
        if (!Ready) return;
        float blend = BlendAt(_seconds);
        double time = _seconds % OceanSpectrum.RepeatSeconds;
        if (!uploaded && _recorded is { } r && r.Time == time && r.Blend == blend) return;
        _recorded = (time, blend);

        float dt = _lastPass is { } last ? (float)Math.Clamp(_seconds - last, 0, 1) : 0;
        _lastPass = _seconds;
        var push = new OceanPush
        {
            Lengths = Lengths,
            Time = (float)time,
            Blend = blend,
            From = _from,
            To = _to,
            Choppiness = Choppiness,
            Dt = dt,
        };
        Span<GpuBufferBinding> b =
        [
            new(_spectrum.Handle, 0, _spectrum.Size),
            new(_fieldsA.Handle, 0, _fieldsA.Size),
            new(_fieldsB.Handle, 0, _fieldsB.Size),
            new(_output.Handle, 0, _output.Size),
            new(_foam.Handle, 0, _foam.Size),
        ];
        FrameProfiler.PreStamp(cmd, StageClock.Uploads);
        cmd.BeginLabel("ocean");
        // The last frame's draws sampled the textures, its passes used the buffers, and the spectrum may just have been written.
        if (!_foamCleared)
        {
            cmd.FillBuffer(_foam.Handle, 0, _foam.Size, 0);
            _foamCleared = true;
        }

        cmd.Order();
        var stamps = (_ctx.Frame.Timestamps.Allocate(), _ctx.Frame.Timestamps.Allocate());
        cmd.Timestamp(_ctx.Frame.Timestamps, stamps.Item1, GpuStage.Compute);
        uint groups = (uint)Size / 16;
        Run(cmd, _evolve, _evolvePipe, b, push, groups, groups, Cascades);
        Compute(cmd);
        Run(cmd, _fft, _fftPipe, b, push with { Pass = 0 }, (uint)Size, Cascades, 1);
        Compute(cmd);
        Run(cmd, _fft, _fftPipe, b, push with { Pass = 1 }, (uint)Size, Cascades, 1);
        Compute(cmd);
        Run(cmd, _assemble, _assemblePipe, b, push, groups, groups, Cascades);
        cmd.Barrier([new GpuDependency(Access.StorageWrite, Access.TransferSrc)]);
        for (int c = 0; c < Cascades; c++)
        {
            cmd.CopyBufferToImage(_output.Handle, (ulong)c * _texels * 8, _oceanMaps, 0, c);
            cmd.CopyBufferToImage(_output.Handle, (ulong)(Cascades + c) * _texels * 8, _oceanMaps, 0, Cascades + c);
        }
        cmd.GenerateMips(_oceanMaps);
        cmd.Timestamp(_ctx.Frame.Timestamps, stamps.Item2, GpuStage.All);
        if (stamps.Item1.IsValid && stamps.Item2.IsValid) _pendingTimes.Add(stamps);
        cmd.EndLabel();
        FrameProfiler.PreStamp(cmd, 9);   // the water stage: the ocean's FFT is the water's
        Dispatched++;
    }

    void CollectTimes()
    {
        var q = _ctx.Frame.Timestamps;
        for (int i = 0; i < _pendingTimes.Count; i++)
        {
            var (b, e) = _pendingTimes[i];
            if (b.Frame + _ctx.FramesInFlight + 2 < _ctx.Frame.Number)
            {
                _pendingTimes.RemoveAt(i--);   // never collected
                continue;
            }

            if (!q.TryRead(b, out ulong tb) || !q.TryRead(e, out ulong te)) continue;
            GpuMicroseconds += (te - tb) / 1000.0;
            GpuTimed++;
            _pendingTimes.RemoveAt(i--);
        }
    }

    /// <summary>
    /// Makes the spectrum upload into the slot that is not shown (or both slots, the first time), and starts the blend into it.
    /// </summary>
    void SwapInUpload(Vector4[][] s)
    {
        if (_uploadBoth)
        {
            (_from, _to) = (0, 1);
            Write(0, s);
            Write(1, s);
            _blendStart = double.NegativeInfinity;
        }
        else
        {
            (_from, _to) = (_to, _from);
            Write(_to, s);
            _blendStart = _seconds;
        }
    }

    void Write(uint slot, Vector4[][] s)
    {
        using var batch = _ctx.Uploads.Begin();
        for (int c = 0; c < Cascades; c++)
            batch.Write(_spectrum, ((ulong)slot * Cascades + (ulong)c) * _texels * 16, MemoryMarshal.AsBytes(s[c].AsSpan()));
    }

    static void Compute(IGpuCommandList cmd)
    {
        cmd.Barrier([new GpuDependency(Access.StorageWrite, Access.StorageRead | Access.StorageWrite)]);
    }

    void Run(IGpuCommandList cmd, GpuProgram p, GpuComputePipeline pipe, ReadOnlySpan<GpuBufferBinding> b, in OceanPush push, uint x, uint y, uint z)
    {
        cmd.BindPipeline(pipe);
        var slots = p.Info.ComputeReflection!.Blocks
            .Where(k => k.Kind == Gpu.Shaders.BlockKind.StorageBuffer && k.Set == 0)
            .Select(k => (uint)k.Binding)
            .ToArray();
        int count = slots.Length;
        var bindings = stackalloc GpuBinding[count];
        for (int i = 0; i < count; i++)
        {
            ref readonly var bb = ref b[(int)slots[i]];
            bindings[i] = GpuBinding.Storage(slots[i], bb);
        }
        cmd.BindProgramResources(p, 0, new ReadOnlySpan<GpuBinding>(bindings, count));
        cmd.PushConstants(p, GpuStage.Compute, in push);
        cmd.Dispatch(x, y, z);
    }

    static string Kernel(string body, int n) => $"#version 450\n#define N {n}u\n" + Common + body;


    public void Dispose()
    {
        _building?.ContinueWith(_ => { });
        _spectrum.Dispose();
        _fieldsA.Dispose();
        _fieldsB.Dispose();
        _output.Dispose();
        _foam.Dispose();
        _oceanMaps.Dispose();
        _ctx.Shaders.Dispose(_evolve);
        _ctx.Shaders.Dispose(_fft);
        _ctx.Shaders.Dispose(_assemble);
    }
}