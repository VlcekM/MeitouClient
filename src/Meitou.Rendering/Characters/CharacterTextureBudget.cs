namespace Meitou.Rendering.Characters;

/// <summary>
/// The character textures' memory governor (an engine choice, not the game's): twice a second, over 90% of the cache's mark every texture is
/// treated as <see cref="NeedScale"/> times farther (a level coarser per doubling, up to 512) until the cache is under half the mark.
/// </summary>
internal sealed class CharacterTextureBudget
{
    const long IntervalMs = 500;
    long lastRebalance;

    /// <summary>The factor on every texture's need (distance over texel size): 1 while the cache is within its mark.</summary>
    public float NeedScale { get; private set; } = 1;

    /// <summary>Once per <see cref="IntervalMs"/>: adjusts <see cref="NeedScale"/>, tells the cache how eagerly to coarsen, commits the frame's needs and rebalances.</summary>
    public void Update(WorldTextureCache cache)
    {
        long tick = Environment.TickCount64;
        if (tick - lastRebalance < IntervalMs) return;
        lastRebalance = tick;
        double mark = cache.MarkMb * 1048576, resident = cache.ResidentBytes;
        if (resident > mark * 0.9) NeedScale = Math.Min(NeedScale * 1.6f, 512);
        else if (resident < mark * 0.5) NeedScale = Math.Max(NeedScale / 1.2f, 1);
        cache.CoarsenAfterSeconds = NeedScale > 1 ? 0.5 : 10;
        cache.MaxCoarsens = NeedScale > 1 ? 24 : 2;
        cache.CommitNeeds();
        cache.Rebalance();
    }
}
