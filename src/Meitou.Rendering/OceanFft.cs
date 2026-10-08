using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>The push constants all three ocean kernels declare (std430, 48 bytes).</summary>
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
/// The Meitou water's open waves (docs/viewer.md "Meitou water"): Tessendorf's FFT ocean on the GPU. <see cref="OceanSpectrum"/> gives the
/// initial amplitudes (rebuilt on a worker thread when the wind turns or changes strength, and blended in over <see cref="BlendSeconds"/>);
/// each frame a compute pass evolves them to the game clock, runs an inverse FFT over rows and then columns (Stockham, radix 2, in shared
/// memory, four complex fields per texel packing eight real ones) and writes per cascade the displacement and the slopes into two RGBA16F
/// texture arrays with mips, which the water's shaders sample. Nothing runs while the clock and the spectrum stand still (a paused game).
/// Recorded into the frame's pre-frame commands; render thread only.
/// </summary>
public sealed unsafe class OceanWaves : IDisposable
{
    public const int DefaultSize = 256;
    /// <summary>Game seconds over which a new spectrum replaces the old one.</summary>
    public const float BlendSeconds = 10;
    /// <summary>Game seconds over which the foam left by a folding crest fades to a third.</summary>
    public const float FoamSeconds = 4;
    /// <summary>How far the wind must turn (radians) or change (a fraction of its speed) before the spectrum is rebuilt.</summary>
    public const float RebuildAngle = 0.17f, RebuildChange = 0.12f;

    const int Cascades = 3;
    readonly GpuContext ctx;
    readonly ShaderProgram evolve, fft, assemble;
    readonly ComputePipeline evolvePipe, fftPipe, assemblePipe;
    readonly DeviceBuffer spectrum, fieldsA, fieldsB, output, foam;
    readonly Texture displacement, slopes;
    readonly Sampler sampler;
    readonly ulong texels;

    // The spectra: slot `to` is shown at blend 1. A new one goes into the other slot once the blend is complete.
    uint from, to = 1;
    double blendStart = double.NegativeInfinity;
    Vector2 builtDirection;
    float builtWind = -1;
    Task<(Vector4[][] Spectrum, Vector2 Direction, float Wind)>? building;
    Vector4[][]? upload;
    bool uploadBoth;
    double seconds;
    (double Time, float Blend)? recorded;
    double? lastPass;
    bool foamCleared;

    public OceanWaves(GpuContext ctx, int size = DefaultSize)
    {
        if (size is not (64 or 128 or 256)) throw new ArgumentOutOfRangeException(nameof(size), "64, 128 or 256");
        this.ctx = ctx;
        Size = size;
        texels = (ulong)size * (ulong)size;
        evolve = ctx.Shaders.Compute(Kernel(EvolveSource, size), "ocean evolve");
        fft = ctx.Shaders.Compute(Kernel(FftSource, size), "ocean fft");
        assemble = ctx.Shaders.Compute(Kernel(AssembleSource, size), "ocean assemble");
        evolvePipe = ctx.Pipelines.Get(new ComputePipelineDesc(evolve, "ocean evolve"));
        fftPipe = ctx.Pipelines.Get(new ComputePipelineDesc(fft, "ocean fft"));
        assemblePipe = ctx.Pipelines.Get(new ComputePipelineDesc(assemble, "ocean assemble"));
        spectrum = DeviceBuffer.Create(ctx, 2 * Cascades * texels * 16, BufferUse.Storage | BufferUse.TransferDst, "ocean spectrum");
        fieldsA = DeviceBuffer.Create(ctx, Cascades * texels * 16, BufferUse.Storage, "ocean fields a");
        fieldsB = DeviceBuffer.Create(ctx, Cascades * texels * 16, BufferUse.Storage, "ocean fields b");
        output = DeviceBuffer.Create(ctx, 2 * Cascades * texels * 8, BufferUse.Storage | BufferUse.TransferSrc, "ocean output");
        foam = DeviceBuffer.Create(ctx, Cascades * texels * 4, BufferUse.Storage | BufferUse.TransferDst, "ocean foam");
        int levels = 1 + (int)Math.Log2(size);
        using (var batch = ctx.Uploads.Begin())
        {
            TextureDesc Desc(string name) => new(Format.R16G16B16A16Sfloat, size, size, levels, Cascades, Kind: TextureKind.Texture2DArray,
                Use: TextureUse.Sampled | TextureUse.TransferDst | TextureUse.TransferSrc, Name: name);
            displacement = batch.Create(Desc("ocean displacement"));
            slopes = batch.Create(Desc("ocean slopes"));
        }
        sampler = ctx.Samplers.Get(new SamplerDesc(Filter.Linear, Filter.Linear, SamplerMipmapMode.Linear, true, SamplerAddressMode.Repeat,
            SamplerAddressMode.Repeat, SamplerAddressMode.Repeat, false, CompareOp.Always, false, false, 8, 0));
    }

    /// <summary>Texels along a cascade's side.</summary>
    public int Size { get; }
    /// <summary>The horizontal displacement's scale (1: Tessendorf's, sharp crests; 0: plain heights).</summary>
    public float Choppiness { get; set; } = 1.0f;
    /// <summary>The cascades' tile sizes (units), w unused.</summary>
    public Vector4 Lengths => new(OceanSpectrum.Lengths[0], OceanSpectrum.Lengths[1], OceanSpectrum.Lengths[2], 0);
    /// <summary>Per cascade (layer): displacement x, y, z (units; the horizontal ones times the choppiness) and the foam (0..1): where the crests
    /// fold it is renewed, elsewhere it fades over <see cref="FoamSeconds"/>, so foam lingers behind a crest.</summary>
    public SampledTexture Displacement => new(sampler, displacement.View(), displacement.Image);
    /// <summary>Per cascade (layer): ∂Dy/∂x, ∂Dy/∂z, ∂Dx/∂x, ∂Dz/∂z (the last two times the choppiness).</summary>
    public SampledTexture Slopes => new(sampler, slopes.View(), slopes.Image);
    /// <summary>Compute passes recorded so far (a paused game records none).</summary>
    public long Dispatched { get; private set; }
    /// <summary>GPU time of the timed passes (microseconds, summed) and their count, read a few frames late.</summary>
    public double GpuMicroseconds { get; private set; }
    public long GpuTimed { get; private set; }
    readonly List<(QuerySlot Begin, QuerySlot End)> pendingTimes = [];
    /// <summary>A spectrum has been uploaded: before that the textures are undefined and the water must stay flat.</summary>
    public bool Ready { get; private set; }

    /// <summary>For tests: the packed output (per texture, per cascade, Size² texels of four halves; displacement first).</summary>
    internal DeviceBuffer Output => output;
    /// <summary>For tests: a spectrum of their own in both slots (<see cref="Update"/> then builds none).</summary>
    internal void SetSpectrum(Vector4[][] s) { (upload, uploadBoth, builtWind) = (s, true, float.NaN); }
    /// <summary>For tests: the clock in game seconds.</summary>
    internal void SetTime(double s) => seconds = s;

    /// <summary>Takes the game clock and the weather's wind (direction it blows to, speed in units per second); starts a new spectrum when the
    /// wind has moved far enough from the one shown. The first call builds one at once.</summary>
    public void Update(double gameHours, Vector2 windDirection, float windSpeed)
    {
        seconds = gameHours * WaveSet.SecondsPerGameHour;
        if (float.IsNaN(builtWind)) return;   // a test's own spectrum
        var dir = windDirection.LengthSquared() > 1e-8f ? Vector2.Normalize(windDirection) : Vector2.UnitX;
        float wind = OceanSpectrum.WindMetres(windSpeed);
        if (builtWind < 0)
        {
            (upload, uploadBoth, builtDirection, builtWind) = (OceanSpectrum.Build(Size, dir, wind), true, dir, wind);
            return;
        }
        if (building is { IsCompleted: true } done && upload is null && BlendAt(seconds) >= 1)
        {
            building = null;
            if (done.IsCompletedSuccessfully) (upload, uploadBoth, builtDirection, builtWind) = (done.Result.Spectrum, false, done.Result.Direction, done.Result.Wind);
        }
        if (building is null && upload is null && BlendAt(seconds) >= 1 &&
            (MathF.Acos(Math.Clamp(Vector2.Dot(dir, builtDirection), -1, 1)) > RebuildAngle || MathF.Abs(wind - builtWind) > RebuildChange * builtWind))
        {
            int n = Size;
            building = Task.Run(() => (OceanSpectrum.Build(n, dir, wind), dir, wind));
        }
    }

    float BlendAt(double s) => (float)Math.Clamp((s - blendStart) / BlendSeconds, 0, 1);

    /// <summary>Records this frame's update of the textures into the frame's pre-frame commands (a frame must be open), unless nothing changed.</summary>
    public void Record()
    {
        CollectTimes();
        var cmd = ctx.Frame.PreFrame;
        bool uploaded = false;
        if (upload is { } s)
        {
            if (uploadBoth) { (from, to) = (0, 1); Write(0, s); Write(1, s); blendStart = double.NegativeInfinity; }
            else { (from, to) = (to, from); Write(to, s); blendStart = seconds; }
            upload = null;
            uploaded = true;
            Ready = true;
        }
        if (!Ready) return;
        float blend = BlendAt(seconds);
        double time = seconds % OceanSpectrum.RepeatSeconds;
        if (!uploaded && recorded is { } r && r.Time == time && r.Blend == blend) return;
        recorded = (time, blend);

        float dt = lastPass is { } last ? (float)Math.Clamp(seconds - last, 0, 1) : 0;
        lastPass = seconds;
        var push = new OceanPush
        {
            Lengths = Lengths, Time = (float)time, Blend = blend, From = from, To = to, Choppiness = Choppiness, Dt = dt,
        };
        Span<BufferBinding> b =
        [
            new(spectrum.Handle, 0, spectrum.Size), new(fieldsA.Handle, 0, fieldsA.Size), new(fieldsB.Handle, 0, fieldsB.Size), new(output.Handle, 0, output.Size),
            new(foam.Handle, 0, foam.Size),
        ];
        cmd.BeginLabel("ocean");
        // The last frame's draws sampled the textures, its passes used the buffers, and the spectrum may just have been written.
        if (!foamCleared) { cmd.FillBuffer(foam.Handle, 0, foam.Size, 0); foamCleared = true; }
        cmd.Barrier(BarrierBatch.Full);
        var stamps = (ctx.Frame.Timestamps.Allocate(), ctx.Frame.Timestamps.Allocate());
        cmd.Timestamp(ctx.Frame.Timestamps, stamps.Item1, PipelineStageFlags2.ComputeShaderBit);
        uint groups = (uint)Size / 16;
        Run(cmd, evolve, evolvePipe, b, push, groups, groups, Cascades);
        Compute(cmd);
        Run(cmd, fft, fftPipe, b, push with { Pass = 0 }, (uint)Size, Cascades, 1);
        Compute(cmd);
        Run(cmd, fft, fftPipe, b, push with { Pass = 1 }, (uint)Size, Cascades, 1);
        Compute(cmd);
        Run(cmd, assemble, assemblePipe, b, push, groups, groups, Cascades);
        var copy = new BarrierBatch();
        copy.Add(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit, PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferReadBit);
        cmd.Barrier(copy);
        for (int c = 0; c < Cascades; c++)
        {
            cmd.CopyBufferToImage(output.Handle, (ulong)c * texels * 8, displacement, 0, c);
            cmd.CopyBufferToImage(output.Handle, (ulong)(Cascades + c) * texels * 8, slopes, 0, c);
        }
        cmd.GenerateMips(displacement);
        cmd.GenerateMips(slopes);
        cmd.Timestamp(ctx.Frame.Timestamps, stamps.Item2, PipelineStageFlags2.AllTransferBit);
        if (stamps.Item1.IsValid && stamps.Item2.IsValid) pendingTimes.Add(stamps);
        cmd.EndLabel();
        Dispatched++;
    }

    void CollectTimes()
    {
        var q = ctx.Frame.Timestamps;
        for (int i = 0; i < pendingTimes.Count; i++)
        {
            var (b, e) = pendingTimes[i];
            if (b.Frame + ctx.Device.Frames.Count + 2 < ctx.Frame.Number) { pendingTimes.RemoveAt(i--); continue; }   // never collected
            if (!q.TryRead(b, out ulong tb) || !q.TryRead(e, out ulong te)) continue;
            GpuMicroseconds += (te - tb) / 1000.0;
            GpuTimed++;
            pendingTimes.RemoveAt(i--);
        }
    }

    void Write(uint slot, Vector4[][] s)
    {
        for (int c = 0; c < Cascades; c++)
            ctx.Uploads.Write(spectrum, ((ulong)slot * Cascades + (ulong)c) * texels * 16, MemoryMarshal.AsBytes(s[c].AsSpan()));
    }

    static void Compute(CommandList cmd)
    {
        var batch = new BarrierBatch();
        batch.Add(PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit, PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        cmd.Barrier(batch);
    }

    static void Run(CommandList cmd, ShaderProgram p, ComputePipeline pipe, ReadOnlySpan<BufferBinding> b, in OceanPush push, uint x, uint y, uint z)
    {
        cmd.BindPipeline(pipe);
        var bindings = p.ComputeReflection!.Blocks.Where(k => k.Kind == Gpu.Shaders.BlockKind.StorageBuffer && k.Set == 0).Select(k => (uint)k.Binding).ToArray();
        int count = bindings.Length;
        var infos = stackalloc DescriptorBufferInfo[count];
        var writes = stackalloc WriteDescriptorSet[count];
        for (int i = 0; i < count; i++)
        {
            ref readonly var bb = ref b[(int)bindings[i]];
            infos[i] = new DescriptorBufferInfo(bb.Buffer, bb.Offset, bb.Size);
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstBinding = bindings[i], DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &infos[i],
            };
        }
        cmd.PushDescriptors(p.Layout, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, count), PipelineBindPoint.Compute);
        cmd.PushConstants(p.Layout, ShaderStageFlags.ComputeBit, in push);
        cmd.Dispatch(x, y, z);
    }

    static string Kernel(string body, int n) => $"#version 450\n#define N {n}u\n" + Common + body;

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

    public void Dispose()
    {
        building?.ContinueWith(_ => { });
        spectrum.Dispose();
        fieldsA.Dispose();
        fieldsB.Dispose();
        output.Dispose();
        foam.Dispose();
        displacement.Dispose();
        slopes.Dispose();
        evolve.Dispose();
        fft.Dispose();
        assemble.Dispose();
    }
}
