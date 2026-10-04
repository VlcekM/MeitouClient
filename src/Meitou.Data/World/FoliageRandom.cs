using System.Globalization;

namespace Meitou.Data.World;

/// <summary>
/// The random numbers of Kenshi's foliage placer (docs/formats/foliage.md, "Random numbers"): a 32-bit Mersenne Twister
/// (MT19937, the standard initialisation and tempering), reseeded for every foliage layer of every zone, with the game's
/// float draw (in double precision, <c>u / (2^32 − 1)</c>, inclusive of both ends) and its integer draw (mask rejection).
/// </summary>
public sealed class FoliageRandom
{
    const int N = 624, M = 397;
    readonly uint[] state = new uint[N];
    int index;

    public FoliageRandom(uint seed) => Seed(seed);

    /// <summary>Number of 32-bit results drawn since the last seed.</summary>
    public int Count { get; private set; }

    public void Seed(uint seed)
    {
        state[0] = seed;
        for (int i = 1; i < N; i++)
            state[i] = unchecked(1812433253u * (state[i - 1] ^ (state[i - 1] >> 30)) + (uint)i);
        // The game regenerates the block right after seeding (the first draw then reads index 0).
        Twist();
        Count = 0;
    }

    void Twist()
    {
        for (int i = 0; i < N; i++)
        {
            uint y = (state[i] & 0x80000000u) | (state[(i + 1) % N] & 0x7FFFFFFFu);
            uint v = state[(i + M) % N] ^ (y >> 1);
            if ((y & 1) != 0) v ^= 0x9908B0DFu;
            state[i] = v;
        }
        index = 0;
    }

    /// <summary>The next tempered 32-bit result.</summary>
    public uint Next()
    {
        if (index >= N) Twist();
        uint y = state[index++];
        y ^= y >> 11;
        y ^= (y << 7) & 0x9D2C5680u;
        y ^= (y << 15) & 0xEFC60000u;
        y ^= y >> 18;
        Count++;
        return y;
    }

    /// <summary>The game's float draw: <c>min + u / (2^32 − 1) × (max − min)</c> in double precision, rounded to single.</summary>
    public float Float(float min, float max) => (float)(Next() * (1.0 / uint.MaxValue) * (double)(max - min) + min);

    /// <summary>
    /// The game's integer draw in [0, <paramref name="range"/>]: results masked to the smallest all-ones mask covering the
    /// range, redrawn while above it.
    /// </summary>
    public uint IntUpTo(uint range)
    {
        uint mask = range;
        mask |= mask >> 1;
        mask |= mask >> 2;
        mask |= mask >> 4;
        mask |= mask >> 8;
        mask |= mask >> 16;
        uint v;
        do v = Next() & mask; while (v > range);
        return v;
    }

    /// <summary>
    /// Seed of a foliage layer in a zone: the leading integer of the layer record's string id (its number), minus the
    /// truncated <c>(h / 2^30) × 16777215 − 16777215</c> for the integer noise hash <c>h</c> of the zone's minimum corner.
    /// </summary>
    public static uint LayerSeed(string layerStringId, float zoneMinX, float zoneMinZ)
    {
        int id = LeadingInteger(layerStringId);
        uint h = FoliageNoise.Hash((int)zoneMinX, (int)zoneMinZ);
        double v = h * (1.0 / 1073741824) * 16777215.0 - 16777215.0;
        return unchecked((uint)(id - (int)v));
    }

    /// <summary>Like a C++ stream read of an int: optional sign, then digits; 0 when there are none.</summary>
    public static int LeadingInteger(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        int start = i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        return int.TryParse(s.AsSpan(start, i - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : 0;
    }
}

/// <summary>
/// Kenshi's value noise for grass coverage and per-position randomness (docs/formats/foliage.md, "Noise"): the classic
/// integer hash, smoothed over the 3 × 3 neighbourhood, cosine interpolated, summed over five octaves.
/// </summary>
public static class FoliageNoise
{
    /// <summary>The exe's π for the cosine interpolation (a double constant 3.1415927).</summary>
    const double Pi = 3.1415927;

    /// <summary>The integer hash <c>n = x + 57 z; n ^= n &lt;&lt; 13; (n (n² 15731 + 789221) + 1376312589) &amp; 0x7FFFFFFF</c>.</summary>
    public static uint Hash(int x, int z)
    {
        unchecked
        {
            uint n = (uint)(z * 57 + x);
            n ^= n << 13;
            return (n * (n * n * 15731u + 789221u) + 1376312589u) & 0x7FFFFFFFu;
        }
    }

    /// <summary>The hash as a value in (−1, 1]: <c>1 − h / 2^30</c>.</summary>
    public static double Value(int x, int z) => 1.0 - Hash(x, z) * (1.0 / 1073741824);

    /// <summary>The value at an integer point smoothed with its neighbours: sides / 8, corners / 16, centre / 4.</summary>
    public static float Smooth(float x, float z)
    {
        int ix = (int)x, iz = (int)z, xm = (int)(x - 1), xp = (int)(x + 1), zm = (int)(z - 1), zp = (int)(z + 1);
        double sides = Value(xp, iz) + Value(xm, iz) + Value(ix, zm) + Value(ix, zp);
        double corners = Value(xp, zm) + Value(xm, zm) + Value(xm, zp) + Value(xp, zp);
        float s = (float)(sides * 0.125);
        s += (float)(corners * 0.0625);
        s += (float)(Value(ix, iz) * 0.25);
        return s;
    }

    /// <summary>Smoothed values at the four surrounding integer points, cosine interpolated.</summary>
    public static float Interpolated(float x, float z)
    {
        float fx0 = MathF.Floor(x), fz0 = MathF.Floor(z);
        double wx = (1.0 - Math.Cos((x - fx0) * Pi)) * 0.5;
        double wz = (1.0 - Math.Cos((z - fz0) * Pi)) * 0.5;
        float fx1 = fx0 + 1, fz1 = fz0 + 1;
        float n00 = Smooth(fx0, fz0), n10 = Smooth(fx1, fz0), n01 = Smooth(fx0, fz1), n11 = Smooth(fx1, fz1);
        float top = (float)(n00 * (1.0 - wx) + n10 * wx);
        float bottom = (float)(n01 * (1.0 - wx) + n11 * wx);
        return (float)(top * (1.0 - wz) + bottom * wz);
    }

    /// <summary>
    /// The grass coverage noise at world (x, z): five octaves (frequency 2^o / scale, amplitude 0.5^o) mapped to
    /// <c>0.5 + 0.5 n</c>, zero at or below <paramref name="cutoff"/> and rescaled above it, times
    /// <paramref name="boost"/>, clamped to [0, <paramref name="cap"/>].
    /// </summary>
    public static float Coverage(double x, double z, double scale, float cutoff, float boost, float cap)
    {
        double sum = 0;
        float frequency = 1;
        double amplitude = 1;
        for (int o = 0; o < 5; o++)
        {
            float u = (float)(frequency * x / scale);
            float v = (float)((double)frequency * z / scale);
            sum += Interpolated(u, v) * amplitude;
            frequency *= 2;
            amplitude *= 0.5;
        }
        double n = sum * 0.5 + 0.5;
        double r = n <= cutoff ? 0.0 : (float)(((float)n - cutoff) / (1f - cutoff));
        r *= boost;
        if (r < 0) r = 0;
        return r > cap ? cap : (float)r;
    }
}
