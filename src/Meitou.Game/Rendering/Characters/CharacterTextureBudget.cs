using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The character textures' memory governor (an engine choice, not the game's): twice a second, over 90% of the cache's mark every texture is
/// treated as <see cref="NeedScale"/> times farther (a level coarser per doubling, up to 512) until the cache is under half the mark.
/// </summary>
/// <param name="clock">The milliseconds clock <see cref="Due"/> and <see cref="Commit"/> read (<see cref="FrameTick.Now"/> in the game).</param>
internal sealed class CharacterTextureBudget(Func<long>? clock = null)
{
    /// <summary>
    /// The time between rebalances, in milliseconds (twice a second).
    /// </summary>
    public const long IntervalMs = 500;

    /// <summary>
    /// The milliseconds clock; one clock for <see cref="Due"/> and <see cref="Commit"/>, so a commit only comes on a frame that offered.
    /// </summary>
    readonly Func<long> _clock = clock ?? (static () => FrameTick.Now);

    /// <summary>
    /// <see cref="_clock"/> at the last commit (0 before the first).
    /// </summary>
    long _lastCommit;

    /// <summary>
    /// The factor on every texture's need (distance over texel size): 1 while the cache is within its mark.
    /// </summary>
    public float NeedScale { get; private set; } = 1;

    /// <summary>
    /// Whether this frame's users offer their needs (and mark their textures used) and then <see cref="Commit"/> them: once per
    /// <see cref="IntervalMs"/>.
    /// </summary>
    public bool Due => _clock() - _lastCommit >= IntervalMs;

    /// <summary>
    /// Called on a <see cref="Due"/> frame after its users offered: adjusts <see cref="NeedScale"/>, tells the cache how eagerly to coarsen, commits
    /// the frame's needs and rebalances. A commit on a frame that did not offer would leave the drawn textures unmarked for another interval: with
    /// a second clock the two disagreed whenever the interval ended inside the frame, and under memory pressure (2 s idle limit) runs of such
    /// frames unloaded what was drawn (issue #1439: 10555 unloads of 381 textures).
    /// </summary>
    public void Commit(WorldTextureCache cache)
    {
        _lastCommit = _clock();

        double mark = cache.MarkMb * 1048576, resident = cache.ResidentBytes;
        NeedScale = NextScale(NeedScale, resident, mark);
        cache.CoarsenAfterSeconds = NeedScale > 1 ? 0.5 : 10;
        cache.MaxCoarsens = NeedScale > 1 ? 24 : 2;
        cache.CommitNeeds();
        cache.Rebalance();
    }

    /// <summary>
    /// The need factor after <paramref name="scale"/> with <paramref name="resident"/> bytes held against a <paramref name="mark"/>: up by 1.6 over
    /// 90% of the mark (at most 512), down by 1.2 under half (at least 1), else unchanged.
    /// </summary>
    internal static float NextScale(float scale, double resident, double mark)
    {
        if (resident > mark * 0.9) return Math.Min(scale * 1.6f, 512);
        if (resident < mark * 0.5) return Math.Max(scale / 1.2f, 1);
        return scale;
    }
}
