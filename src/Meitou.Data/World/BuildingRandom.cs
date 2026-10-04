namespace Meitou.Data.World;

/// <summary>
/// The random numbers Kenshi uses when it builds a placed building (docs/formats/zones.md, "From placements to meshes"):
/// the C runtime's <c>rand()</c> of MSVCR100 (a linear congruential generator, 15-bit results), reseeded from the
/// building's position before its parts are chosen, and the game's two helpers that scale a result to an int or float range.
/// </summary>
public sealed class BuildingRandom(uint seed)
{
    uint state = seed;

    /// <summary>Number of results drawn so far.</summary>
    public int Count { get; private set; }

    /// <summary>MSVCR100 <c>rand()</c>: 0..32767.</summary>
    public int Next()
    {
        Count++;
        unchecked { state = state * 214013 + 2531011; }
        return (int)((state >> 16) & 0x7FFF);
    }

    /// <summary>The game's int range helper: <c>min + trunc(rand / 32768 × (max − min + 1))</c> in single precision, clamped to [min, max].</summary>
    public int NextInt(int min, int max)
    {
        float f = (float)(Next() * (-1.0 / 32768));
        f *= max - min + 1;
        int v = min - (int)f;
        return Math.Clamp(v, min, max);
    }

    /// <summary>The game's float range helper: <c>rand / 32768 × (max − min) + min</c> in single precision, in [min, max).</summary>
    public float NextFloat(float min, float max) => (float)(Next() * (1.0 / 32768)) * (max - min) + min;

    /// <summary>
    /// Seed of a building at world X/Z: both truncated toward zero to an integer, XOR <c>0xDEADBEEF</c>, low 32 bits, then
    /// combined like <c>boost::hash_combine(x, z)</c>.
    /// </summary>
    public static uint Seed(float x, float z)
    {
        unchecked
        {
            uint a = (uint)((long)x ^ 0xDEADBEEF);
            uint b = (uint)((long)z ^ 0xDEADBEEF);
            return ((a >> 2) + 0x9E3779B9u + a * 64 + b) ^ a;
        }
    }
}
