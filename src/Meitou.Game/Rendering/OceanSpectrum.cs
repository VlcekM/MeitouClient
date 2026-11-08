using System.Numerics;

namespace Meitou.Rendering;

/// <summary>
/// The Meitou water's wave spectrum (<see cref="OceanWaves"/>, docs/render-water.md "Meitou water"): Tessendorf's statistical ocean, the initial
/// amplitudes h0(k) of every wave on a grid of wave vectors, from a fetch-limited JONSWAP spectrum led by the wind and a cos^2s spreading round
/// it. Three cascades of the same grid over different tile sizes, each keeping only its own band of wave numbers, so together they cover
/// waves from about 100 m down to a few cm without counting any twice. Deterministic: the random phases come from a hash of the texel, so a
/// change of wind changes the amplitudes but keeps the pattern (the GPU blends two spectra into each other).
/// Units are the world's (1 unit = 1 dm); the spectrum itself is evaluated in SI and the amplitudes converted.
/// </summary>
public static class OceanSpectrum
{
    /// <summary>The cascades' tile sizes (units): 250 m, 47 m and 9 m, ratios that are not whole numbers so the tiles do not line up.</summary>
    public static readonly float[] Lengths = [2500, 470, 90];
    /// <summary>A cascade starts at this many times its own fundamental wave number (below that the coarser cascade has the waves).</summary>
    public const float BandStart = 6;
    /// <summary>The dispersion is quantised to whole multiples of 2π / <see cref="RepeatSeconds"/>, so the surface repeats exactly after that
    /// time and the GPU's phases stay small (the clock is taken modulo it).</summary>
    public const double RepeatSeconds = 600;
    /// <summary>Fetch (m): the open water the wind has blown over. Kenshi's seas are lakes; 30 km gives a moderate sea.</summary>
    public const float Fetch = 30000;
    /// <summary>The calmest wind the spectrum is made for (m/s): below it the water would be a mirror.</summary>
    public const float MinWind = 1.5f, MaxWind = 20f;

    const float G = 9.81f;

    /// <summary>The wind in m/s for the weather's wind speed (units per second, 1 unit = 1 dm).</summary>
    public static float WindMetres(float windSpeed) => Math.Clamp(windSpeed / 10f, MinWind, MaxWind);

    /// <summary>The wave number range a cascade keeps (rad per unit): [lo, hi).</summary>
    public static (float Lo, float Hi) Band(int cascade)
    {
        float lo = cascade == 0 ? 0 : 2 * MathF.PI / Lengths[cascade] * BandStart;
        float hi = cascade + 1 < Lengths.Length ? 2 * MathF.PI / Lengths[cascade + 1] * BandStart : float.PositiveInfinity;
        return (lo, hi);
    }

    /// <summary>The angular frequency (rad/s) of a wave with wave number <paramref name="k"/> (rad per unit) in deep water, quantised down to a
    /// multiple of 2π / <see cref="RepeatSeconds"/>. The GPU's evolve kernel computes the same.</summary>
    public static float Omega(float k)
    {
        double w0 = 2 * Math.PI / RepeatSeconds;
        return (float)(Math.Floor(Math.Sqrt(G * 10 * k) / w0) * w0);
    }

    /// <summary>
    /// The initial amplitudes of <paramref name="n"/>² waves per cascade: texel (x, y) is the wave vector 2π (x − n/2, y − n/2) / L, and holds
    /// (h0(k), conj(h0(−k))) as (re, im, re, im) in units. Zero at k = 0, on the Nyquist row and column (x or y = 0, which have no partner) and
    /// outside the cascade's band.
    /// </summary>
    /// <param name="windDirection">Where the wind blows to (world x, z).</param>
    /// <param name="wind">The wind speed in m/s (<see cref="WindMetres"/>).</param>
    public static Vector4[][] Build(int n, Vector2 windDirection, float wind, uint seed = 0x0CEA)
    {
        var dir = windDirection.LengthSquared() > 1e-8f ? Vector2.Normalize(windDirection) : Vector2.UnitX;
        float heading = MathF.Atan2(dir.Y, dir.X);
        var j = new Jonswap(Math.Clamp(wind, MinWind, MaxWind));
        var result = new Vector4[Lengths.Length][];
        for (int c = 0; c < Lengths.Length; c++)
        {
            var h0 = new Vector2[n * n];
            int cascade = c;
            Parallel.For(0, n, y =>
            {
                for (int x = 0; x < n; x++) h0[y * n + x] = Amplitude(j, cascade, n, x, y, heading, seed);
            });
            var texels = new Vector4[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    var a = h0[y * n + x];
                    var b = h0[(n - y) % n * n + (n - x) % n];
                    texels[y * n + x] = new Vector4(a.X, a.Y, b.X, -b.Y);
                }
            result[c] = texels;
        }
        return result;
    }

    static Vector2 Amplitude(in Jonswap j, int cascade, int n, int x, int y, float heading, uint seed)
    {
        if (x == 0 || y == 0) return default;
        float l = Lengths[cascade];
        float dk = 2 * MathF.PI / l;
        var k = new Vector2(x - n / 2, y - n / 2) * dk;
        float kl = k.Length();
        var (lo, hi) = Band(cascade);
        if (kl <= 0 || kl < lo || kl >= hi) return default;
        // SI: k in rad/m, the spectrum in m^4 per (rad/m)^2.
        float km = kl * 10, dkm = dk * 10;
        float omega = MathF.Sqrt(G * km);
        float s = j.S(omega) * (G / (2 * omega));                          // S(k) = S(omega) d(omega)/dk
        float theta = MathF.Atan2(k.Y, k.X) - heading;
        float spread = j.Spreading(omega, theta);
        float p = s * spread / km * dkm * dkm;                              // the variance this wave carries (m²)
        var (r1, r2) = Gaussians(seed, (uint)cascade, (uint)x, (uint)y);
        return new Vector2(r1, r2) * (0.5f * MathF.Sqrt(MathF.Max(p, 0)) * 10);   // m to units
    }

    /// <summary>Two independent standard normal numbers for a texel (Box–Muller over a hash).</summary>
    static (float, float) Gaussians(uint seed, uint c, uint x, uint y)
    {
        uint h1 = Hash(seed ^ Hash(c * 0x9E3779B9u ^ Hash(x * 0x85EBCA6Bu ^ Hash(y + 0x632BE5ABu))));
        uint h2 = Hash(h1 ^ 0xA511E9B3u);
        double u1 = (h1 + 1.0) / 4294967297.0, u2 = h2 / 4294967296.0;
        double r = Math.Sqrt(-2 * Math.Log(u1));
        return ((float)(r * Math.Cos(2 * Math.PI * u2)), (float)(r * Math.Sin(2 * Math.PI * u2)));
    }

    static uint Hash(uint v)
    {
        v ^= v >> 16; v *= 0x7FEB352Du; v ^= v >> 15; v *= 0x846CA68Bu; v ^= v >> 16;
        return v;
    }

    /// <summary>The fetch-limited JONSWAP spectrum (Hasselmann et al. 1973, the fetch relations as in Horvath 2015) for a wind of U m/s.</summary>
    readonly struct Jonswap
    {
        readonly float alpha, peak;

        public Jonswap(float u)
        {
            alpha = 0.076f * MathF.Pow(u * u / (Fetch * G), 0.22f);
            peak = 22 * MathF.Pow(G * G / (u * Fetch), 1f / 3f);
            Wind = u;
        }

        public float Wind { get; }

        /// <summary>S(ω) in m²·s.</summary>
        public float S(float omega)
        {
            float sigma = omega <= peak ? 0.07f : 0.09f;
            float r = MathF.Exp(-(omega - peak) * (omega - peak) / (2 * sigma * sigma * peak * peak));
            return alpha * G * G / MathF.Pow(omega, 5) * MathF.Exp(-1.25f * MathF.Pow(peak / omega, 4)) * MathF.Pow(3.3f, r);
        }

        /// <summary>The share of a frequency's energy going at <paramref name="theta"/> off the wind: Q(s) |cos(θ/2)|^2s, s narrowest at the
        /// peak (Mitsuyasu's form), integrating to 1 over the circle.</summary>
        public float Spreading(float omega, float theta)
        {
            float sp = 11.5f * MathF.Pow(peak * Wind / G, -2.5f);
            float s = omega <= peak ? sp * MathF.Pow(omega / peak, 5) : sp * MathF.Pow(omega / peak, -2.5f);
            s = Math.Clamp(s, 1, 40);
            return Normalisation(s) * MathF.Pow(MathF.Abs(MathF.Cos(theta / 2)), 2 * s);
        }
    }

    /// <summary>Q(s) = 2^(2s−1) Γ(s+1)² / (π Γ(2s+1)): what makes |cos(θ/2)|^2s integrate to 1 over [−π, π].</summary>
    internal static float Normalisation(float s) =>
        (float)Math.Exp((2 * s - 1) * Math.Log(2) + 2 * LogGamma(s + 1) - LogGamma(2 * s + 1) - Math.Log(Math.PI));

    /// <summary>ln Γ(x) for x &gt; 0 (Lanczos, g = 7, 9 terms).</summary>
    static double LogGamma(double x)
    {
        ReadOnlySpan<double> c = [0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
            12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7];
        if (x < 0.5) return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);
        x -= 1;
        double a = c[0], t = x + 7.5;
        for (int i = 1; i < 9; i++) a += c[i] / (x + i);
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }
}
