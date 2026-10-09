using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Rendering;

/// <summary>The Meitou water's FFT ocean (docs/render-water.md "Meitou water"): the spectrum on the CPU, and the GPU's evolve, inverse FFT and
/// assembly against a single wave worked out by hand.</summary>
public class OceanTests
{
    const int N = 64;

    [Fact]
    public void Spectrum_pairs_each_wave_with_its_mirror()
    {
        var s = OceanSpectrum.Build(N, new Vector2(1, 0.4f), 6);
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    var t = s[c][y * N + x];
                    var m = s[c][(N - y) % N * N + (N - x) % N];
                    // zw holds conj(h0(-k)), which is the mirror texel's own h0 conjugated.
                    Assert.Equal(m.X, t.Z);
                    Assert.Equal(-m.Y, t.W);
                }
    }

    [Fact]
    public void Spectrum_is_empty_at_zero_on_the_nyquist_edges_and_outside_each_band()
    {
        var s = OceanSpectrum.Build(N, Vector2.UnitX, 10);
        for (int c = 0; c < 3; c++)
        {
            Assert.Equal(Vector4.Zero, s[c][N / 2 * N + N / 2]);
            for (int i = 0; i < N; i++)
            {
                Assert.Equal(0, s[c][i].X);            // y = 0
                Assert.Equal(0, s[c][i * N].X);        // x = 0
            }
            var (lo, hi) = OceanSpectrum.Band(c);
            float dk = 2 * MathF.PI / OceanSpectrum.Lengths[c];
            for (int y = 1; y < N; y++)
                for (int x = 1; x < N; x++)
                {
                    float k = new Vector2(x - N / 2, y - N / 2).Length() * dk;
                    if (k < lo || k >= hi) Assert.Equal(0, s[c][y * N + x].X);
                }
        }
        // The bands meet: each cascade starts where the one before it stops.
        Assert.Equal(OceanSpectrum.Band(0).Hi, OceanSpectrum.Band(1).Lo);
        Assert.Equal(OceanSpectrum.Band(1).Hi, OceanSpectrum.Band(2).Lo);
    }

    [Fact]
    public void Spectrum_is_deterministic_grows_with_the_wind_and_follows_it()
    {
        Assert.Equal(OceanSpectrum.Build(N, Vector2.UnitX, 6)[0], OceanSpectrum.Build(N, Vector2.UnitX, 6)[0]);
        static double Variance(Vector4[][] s) => s.Sum(c => c.Sum(t => (double)t.X * t.X + t.Y * t.Y));
        Assert.True(Variance(OceanSpectrum.Build(N, Vector2.UnitX, 10)) > 4 * Variance(OceanSpectrum.Build(N, Vector2.UnitX, 4)));
        // Downwind waves carry more than upwind ones.
        var s = OceanSpectrum.Build(N, Vector2.UnitX, 8)[0];
        double down = 0, up = 0;
        for (int y = 1; y < N; y++)
            for (int x = 1; x < N; x++)
            {
                double e = s[y * N + x].X * s[y * N + x].X + s[y * N + x].Y * s[y * N + x].Y;
                if (x > N / 2) down += e; else if (x < N / 2) up += e;
            }
        Assert.True(down > 5 * up, $"downwind {down}, upwind {up}");
        // Same seed, other wind: the phases (the sign pattern of the amplitudes) stay.
        var calm = OceanSpectrum.Build(N, Vector2.UnitX, 7)[1];
        var windy = OceanSpectrum.Build(N, Vector2.UnitX, 7.5f)[1];
        for (int i = 0; i < calm.Length; i++)
            if (calm[i].X != 0) Assert.Equal(MathF.Sign(calm[i].X), MathF.Sign(windy[i].X));
    }

    [Fact]
    public void Spreading_integrates_to_one()
    {
        foreach (float s in new[] { 1f, 2.5f, 8f, 30f })
        {
            double sum = 0;
            const int steps = 4000;
            for (int i = 0; i < steps; i++)
            {
                double theta = -Math.PI + (i + 0.5) * 2 * Math.PI / steps;
                sum += Math.Pow(Math.Abs(Math.Cos(theta / 2)), 2 * s) * 2 * Math.PI / steps;
            }
            Assert.Equal(1.0, sum * OceanSpectrum.Normalisation(s), 3);
        }
    }

    [Fact]
    public void Dispersion_repeats_after_the_repeat_time()
    {
        foreach (float k in new[] { 0.003f, 0.05f, 1.2f })
        {
            float w = OceanSpectrum.Omega(k);
            double cycles = w * OceanSpectrum.RepeatSeconds / (2 * Math.PI);
            Assert.Equal(Math.Round(cycles), cycles, 3);
            Assert.InRange(w, MathF.Sqrt(98.1f * k) - 2 * MathF.PI / 600 - 1e-4f, MathF.Sqrt(98.1f * k) + 1e-4f);
        }
    }

    static void Near(float expected, float actual) => Assert.True(MathF.Abs(expected - actual) < 0.01f, $"expected {expected}, got {actual}");

    static VulkanDevice? TryCreate()
    {
        try { return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true }); }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    /// <summary>One wave per cascade: h0(k) = A at a chosen texel, its mirror holding the conjugate. The GPU must give
    /// Dy = 2A cos(k·x − ωt) and Dx = −2A k̂x sin(k·x − ωt) (the crests drawn together) at every texel.</summary>
    [Fact]
    [Slow]
    public void Gpu_transform_gives_the_wave_worked_out_by_hand()
    {
        using var d = TryCreate();
        Assert.SkipWhen(d is null, "No Vulkan 1.3 device");
        (int X, int Y, float A)[] bins = [(N / 2 + 3, N / 2 + 1, 0.8f), (N / 2 - 2, N / 2 + 5, 0.5f), (N / 2 + 1, N / 2 - 4, 0.3f)];
        var spectrum = new Vector4[3][];
        for (int c = 0; c < 3; c++)
        {
            spectrum[c] = new Vector4[N * N];
            var (x, y, a) = bins[c];
            spectrum[c][y * N + x] = new Vector4(a, 0, 0, 0);
            spectrum[c][(N - y) % N * N + (N - x) % N] = new Vector4(0, 0, a, 0);
        }
        using (var ctx = new GpuContext(d!))
        {
            using var ocean = new OceanWaves(ctx, N) { Choppiness = 1 };
            using var readback = ReadbackBuffer.Create(ctx, ocean.Output.Size, "ocean readback");
            foreach (double t in new[] { 0.0, 7.3 })
            {
                ctx.BeginFrame();
                ocean.SetSpectrum(spectrum);
                ocean.SetTime(t);
                ocean.Record();
                var cmd = ctx.Frame.PreFrame;
                cmd.Barrier(BarrierBatch.Full);
                cmd.CopyBuffer(ocean.Output.Handle, readback.Handle, new BufferCopy(0, 0, ocean.Output.Size));
                ctx.EndFrame();
                d!.Frames.WaitAll();
                var halves = MemoryMarshal.Cast<byte, Half>(readback.Read(0, ocean.Output.Size)).ToArray();
                for (int c = 0; c < 3; c++)
                {
                    var (bx, by, a) = bins[c];
                    float l = OceanSpectrum.Lengths[c];
                    var k = new Vector2(bx - N / 2, by - N / 2) * (2 * MathF.PI / l);
                    float omega = OceanSpectrum.Omega(k.Length());
                    var kh = Vector2.Normalize(k);
                    for (int y = 0; y < N; y += 5)
                        for (int x = 0; x < N; x += 3)
                        {
                            float theta = k.X * x * l / N + k.Y * y * l / N - omega * (float)t;
                            int o = ((c * N * N) + y * N + x) * 4;
                            Near(2 * a * MathF.Cos(theta), (float)halves[o + 1]);         // height
                            Near(-2 * a * kh.X * MathF.Sin(theta), (float)halves[o]);     // x displacement
                            Near(-2 * a * kh.Y * MathF.Sin(theta), (float)halves[o + 2]); // z displacement
                            int so = (((3 + c) * N * N) + y * N + x) * 4;
                            Near(-2 * a * k.X * MathF.Sin(theta), (float)halves[so]);      // dDy/dx
                            Near(-2 * a * k.Y * MathF.Sin(theta), (float)halves[so + 1]);  // dDy/dz
                        }
                }
            }
            Assert.Equal(2, ocean.Dispatched);
        }
        Assert.True(d!.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", d.ValidationLog));
    }
}
