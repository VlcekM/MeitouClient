using Meitou.Data.World;

namespace Meitou.Data.Gameplay.Economy;

/// <summary>
/// The draws the original's artifact code makes (docs/game/economy.md "Artifacts"): boost 1.60's <c>uniform_int</c> on its <c>mt19937</c> (the
/// standard MT19937, <see cref="FoliageRandom"/>), and MSVC 2010's <c>random_shuffle</c> driven by it. These are the libraries' published algorithms.
/// </summary>
public sealed class BoostRandom
{
    /// <summary>
    /// The MT19937 engine every draw takes from.
    /// </summary>
    readonly FoliageRandom _mt;

    /// <summary>
    /// A generator seeded with <paramref name="seed"/> (MT19937's seeding).
    /// </summary>
    public BoostRandom(uint seed) => _mt = new FoliageRandom(seed);

    /// <summary>
    /// A generator drawing from <paramref name="mt"/> (for <see cref="Clone"/>).
    /// </summary>
    BoostRandom(FoliageRandom mt) => this._mt = mt;

    /// <summary>
    /// A generator in the same state, which then draws on its own (boost's <c>variate_generator</c> holds a copy of its engine).
    /// </summary>
    public BoostRandom Clone() => new(_mt.Clone());

    /// <summary>
    /// <c>uniform_int(min, max)</c>: the whole 32-bit range is cut into range + 1 buckets of floor(2^32 - 1 / (range + 1)) results (one more when
    /// that division leaves range over); a result whose bucket is past the last is drawn again. A range of 0 draws nothing.
    /// </summary>
    public int UniformInt(int min, int max)
    {
        uint range = (uint)(max - min);
        if (range == 0) return min;

        uint bucket = uint.MaxValue / (range + 1);
        if (uint.MaxValue % (range + 1) == range) bucket++;

        while (true)
        {
            uint r = _mt.Next() / bucket;
            if (r <= range) return (int)(min + r);
        }
    }

    /// <summary>
    /// <c>random_shuffle</c>: each position i from 1 swaps with one drawn by <c>uniform_int(0, i)</c>.
    /// </summary>
    public void Shuffle<T>(IList<T> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            int j = UniformInt(0, i);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
