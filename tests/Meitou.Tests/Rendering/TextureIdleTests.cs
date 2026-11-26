using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Tests.Rendering;

/// <summary>
/// Issue #1439: drawn character textures must be marked used often enough that the cache never judges them idle (2 s under memory pressure).
/// </summary>
public class TextureIdleTests
{
    /// <summary>
    /// The milliseconds clock the budget reads, set by the test.
    /// </summary>
    long _now = 1000;

    /// <summary>
    /// After a commit the next frame is due one interval later, not before; the first frame is due at once.
    /// </summary>
    [Fact]
    public void Commit_makes_the_next_frame_due_one_interval_later()
    {
        var budget = new CharacterTextureBudget(() => _now);
        var cache = new WorldTextureCache(null!, null!, "test");

        Assert.True(budget.Due);

        budget.Commit(cache);
        _now += CharacterTextureBudget.IntervalMs - 1;
        Assert.False(budget.Due);

        _now++;
        Assert.True(budget.Due);
    }

    /// <summary>
    /// Frames of 70 to 400 ms whose character update reads the clock part-way through: the textures are offered (marked used) at least once per
    /// interval plus a frame, so never near the 2 s limit. Before the fix the commit read another clock than <c>Due</c> and could come on a frame
    /// that offered nothing, leaving a gap of several intervals.
    /// </summary>
    [Theory]
    [InlineData(70)]
    [InlineData(250)]
    [InlineData(400)]
    public void Offers_are_never_more_than_an_interval_and_a_frame_apart(int frameMs)
    {
        var budget = new CharacterTextureBudget(() => _now);
        var cache = new WorldTextureCache(null!, null!, "test");
        long lastOffer = _now;
        long longest = 0;

        for (int frame = 0; frame < 400; frame++)
        {
            _now += frameMs;
            if (!budget.Due) continue;

            longest = Math.Max(longest, _now - lastOffer);
            lastOffer = _now;
            budget.Commit(cache);
        }

        Assert.InRange(longest, 1, CharacterTextureBudget.IntervalMs + frameMs);
        Assert.True(longest < cache.GuardIdleSeconds * 1000);
    }

    /// <summary>
    /// A pump more than <see cref="WorldTextureCache.HitchMs"/> after the last starts the trim's second again; a normal frame leaves it.
    /// </summary>
    [Fact]
    public void A_hitch_postpones_the_trim_and_a_normal_frame_does_not()
    {
        Assert.Equal(12_500, WorldTextureCache.TrimAfterGap(lastTrim: 10_000, lastPump: 10_100, now: 12_500));
        Assert.Equal(10_000, WorldTextureCache.TrimAfterGap(lastTrim: 10_000, lastPump: 10_100, now: 10_150));
    }

    /// <summary>
    /// The need factor rises over 90% of the mark (capped at 512), falls under half (floored at 1) and holds between.
    /// </summary>
    [Fact]
    public void Need_scale_follows_the_mark()
    {
        Assert.Equal(1.6f, CharacterTextureBudget.NextScale(1, 95, 100));
        Assert.Equal(512f, CharacterTextureBudget.NextScale(512, 95, 100));
        Assert.Equal(2f, CharacterTextureBudget.NextScale(2, 70, 100));
        Assert.Equal(1f, CharacterTextureBudget.NextScale(1.1f, 10, 100));
    }
}
