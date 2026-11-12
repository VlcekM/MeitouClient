namespace Meitou.Data.Characters;

/// <summary>
/// The original's random numbers (docs/characters.md "Original random sequence", <b>Verified</b> by decompilation): MSVCR100 <c>rand()</c>, a linear
/// congruential generator seeded like <c>srand</c>, and the game's three ways of using a draw. One instance is one sequence.
/// </summary>
public sealed class MsvcRandom(uint seed)
{
    /// <summary>
    /// The generator's state, advanced by every draw.
    /// </summary>
    uint _state = seed;

    /// <summary>
    /// A sequence for one of Meitou's per-look seeds (engine choice: the original seeds once from the clock and keeps one sequence): the seed
    /// scrambled first (murmur3's finaliser), as neighbouring <c>srand</c> seeds start with nearly the same draws.
    /// </summary>
    public static MsvcRandom FromSeed(int seed)
    {
        uint h = (uint)seed;
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return new MsvcRandom(h);
    }

    /// <summary>
    /// <c>rand()</c>: 0 to 32767.
    /// </summary>
    public int Next()
    {
        _state = _state * 214013 + 2531011;
        return (int)(_state >> 16 & 0x7FFF);
    }

    /// <summary>
    /// <c>FUN_1409b1c10</c>: <c>rand() / 32768</c>, in [0, 1).
    /// </summary>
    public float Unit() => (float)(Next() * (1.0 / 32768));

    /// <summary>
    /// <c>FUN_1409b1c80</c>: <c>rand() / 32768 × (max − min) + min</c>, in floats.
    /// </summary>
    public float Range(float min, float max) => Next() * 3.0517578e-05f * (max - min) + min;

    /// <summary>
    /// <c>FUN_1409b3b40</c>: <c>min + (int)(rand() / 32768 × (max − min + 1))</c>, clamped to [min, max].
    /// </summary>
    public int Int(int min, int max)
    {
        int value = min - (int)(Next() * -3.0517578e-05f * (max - min + 1));
        return Math.Max(min, Math.Min(value, max));
    }
}