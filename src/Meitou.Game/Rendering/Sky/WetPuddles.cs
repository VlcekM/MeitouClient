using System.Numerics;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// Where the wet world's puddles lie, on the CPU: the terrain shader's rule (<c>AtmosphereShaders</c> <c>wetGround</c>, <c>wetNoise</c>,
/// <c>wetHash</c>; docs/formats/weather.md "Wet world") with the same hash and noises, so a foot can be told to be in one. The shader also takes the
/// ground layers' absorbance, which only the GPU has: a middle value stands in for it here, so near a puddle's edge the two may differ by a few
/// centimetres.
/// </summary>
static class WetPuddles
{
    /// <summary>
    /// The absorbance taken for the ground (the shader's runs 0 to 1 with the layers).
    /// </summary>
    const float Absorbance = 0.6f;

    /// <summary>
    /// The shader's <c>wetHollow</c> (TerrainShaders): how far the ground at (x, z) lies under the ground round it, from <paramref name="height"/>.
    /// </summary>
    public static float Hollow(Func<float, float, float> height, float x, float z)
    {
        float h = height(x, z), near = 0, far = 0;
        for (int i = 0; i < 6; i++)
        {
            var (s, c) = MathF.SinCos(i * 1.0471976f);
            near += height(x + c * 12, z + s * 12);
            far += height(x + c * 40, z + s * 40);
        }

        float hollow = 0.25f * Math.Clamp((near / 6 - h) / 1.2f, -1, 1) + 0.25f * Math.Clamp((far / 6 - h) / 4, -1, 1);
        return float.IsFinite(hollow) ? hollow : 0;
    }

    /// <summary>
    /// Water stands at (x, z): <paramref name="wetness"/> the weather's, <paramref name="levelY"/> the y of the ground's normal there,
    /// <paramref name="hollow"/> from <see cref="Hollow"/> (the displaced relief's troughs, which only the GPU has, are left out).
    /// </summary>
    public static bool Under(float x, float z, float wetness, float levelY, float hollow)
    {
        float even = Smooth(0.955f, 0.99f, levelY);
        if (even <= 0 || wetness <= 0.2f) return false;

        float low = 0.6f * Noise(x / 80f, 0, z / 80f) + 0.4f * Noise(x / 15f, 0, z / 15f) + 0.1f * (Noise(x / 3f, 0, z / 3f) - 0.5f) - hollow;
        float fill = Smooth(0.2f, 1f, wetness) * (0.47f + (0.3f - 0.47f) * Absorbance) - (1 - even) * 0.3f;
        return fill - low > 0.0125f;   // half way up the shader's water edge (0 to 0.025)
    }

    /// <summary>
    /// The shader's <c>smoothstep(a, b, v)</c>.
    /// </summary>
    static float Smooth(float a, float b, float v)
    {
        float t = Math.Clamp((v - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// The shader's <c>wetNoise</c>: value noise over the integer lattice of <see cref="Hash"/>, smoothstep-interpolated.
    /// </summary>
    static float Noise(float x, float y, float z)
    {
        float cx = MathF.Floor(x), cy = MathF.Floor(y), cz = MathF.Floor(z);
        float fx = x - cx, fy = y - cy, fz = z - cz;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        fz = fz * fz * (3 - 2 * fz);
        int ix = (int)cx, iy = (int)cy, iz = (int)cz;
        float x00 = float.Lerp(Hash(ix, iy, iz), Hash(ix + 1, iy, iz), fx);
        float x10 = float.Lerp(Hash(ix, iy + 1, iz), Hash(ix + 1, iy + 1, iz), fx);
        float x01 = float.Lerp(Hash(ix, iy, iz + 1), Hash(ix + 1, iy, iz + 1), fx);
        float x11 = float.Lerp(Hash(ix, iy + 1, iz + 1), Hash(ix + 1, iy + 1, iz + 1), fx);
        return float.Lerp(float.Lerp(x00, x10, fy), float.Lerp(x01, x11, fy), fz);
    }

    /// <summary>
    /// The x of the shader's <c>wetHash(cell, 7u)</c>.
    /// </summary>
    static float Hash(int x, int y, int z)
    {
        unchecked
        {
            const uint salt = 7u * 0x9E3779B9u;
            uint vx = (uint)x + salt, vy = (uint)y + salt, vz = (uint)z + salt;
            vx = vx * 1664525u + 1013904223u;
            vy = vy * 1664525u + 1013904223u;
            vz = vz * 1664525u + 1013904223u;
            vx += vy * vz;
            vy += vz * vx;
            vz += vx * vy;
            vx ^= vx >> 16;
            vy ^= vy >> 16;
            vz ^= vz >> 16;
            vx += vy * vz;
            return vx * (1f / 4294967296f);
        }
    }
}
