using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;
using Meitou.Game.Rendering.Water;

namespace Meitou.Tests.Rendering;

/// <summary>
/// The Meitou water's FFT ocean (docs/render-water.md "Meitou water"): the spectrum on the CPU, and the GPU's evolve, inverse FFT and assembly
/// against a single wave worked out by hand.
/// </summary>
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

    static void Near(float expected, float actual) => Assert.True(MathF.Abs(expected - actual) < 0.01f, $"expected {expected}, got {actual}");

    static VulkanDevice? TryCreate()
    {
        try
        {
            return VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, SyncValidation = true });
        }
        catch (Exception e)
            when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            return null;
        }
    }

    /// <summary>
    /// One wave per cascade: h0(k) = A at a chosen texel, its mirror holding the conjugate. The GPU must give Dy = 2A cos(k·x − ωt) and Dx = −2A k̂x
    /// sin(k·x − ωt) (the crests drawn together) at every texel.
    /// </summary>
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
                cmd.Order();
                cmd.CopyBuffer(ocean.Output.Handle, readback.Handle, new GpuBufferCopy(0, 0, ocean.Output.Size));
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
