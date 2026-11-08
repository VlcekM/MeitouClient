namespace Meitou.Rendering;

/// <summary>
/// Which of the wanted cascade redraws a Meitou shadow frame really does. The near cascades (cascade 0, about 0.2 ms; with five cascades also cascade 1,
/// a few hundred units deep) always go when wanted; the far cascades (the last three) cost 1.1 to 1.8 ms each, so no more than <c>budget</c> of them are
/// drawn in one frame: the rest wait, the longest-waiting first, and are drawn after at most <c>maxWait</c> frames of waiting whatever the budget
/// (docs/render-shadows.md "Spikes"). A cascade without a drawn map yet is never deferred. Pure arithmetic, no GPU.
/// </summary>
public static class ShadowSchedule
{
    /// <summary>The default number of far cascades drawn in one frame.</summary>
    public const int DefaultBudget = 1;
    /// <summary>The most frames a wanted redraw waits before it is forced.</summary>
    public const int DefaultMaxWait = 3;
    /// <summary>How many cascades, the last ones, count as far (share the budget): the three that were cascades 1 to 3 of four.</summary>
    public const int FarCascades = 3;

    /// <summary>The cascades before the far ones (at least cascade 0), which are never deferred: 1 of four, 2 of five.</summary>
    public static int NearCascades(int count) => Math.Max(1, count - FarCascades);

    /// <summary>
    /// Decides <paramref name="draw"/> from <paramref name="want"/> (what the staleness rules asked for this frame, plus what was deferred before)
    /// and <paramref name="mustDraw"/> (no map yet). <paramref name="waited"/> counts, per cascade, the frames its redraw has been deferred: this
    /// call raises it for the cascades kept waiting and zeroes it for the others. A <paramref name="budget"/> of zero or less means no limit.
    /// The first <paramref name="near"/> cascades are never deferred and do not count against the budget.
    /// </summary>
    public static void Pick(ReadOnlySpan<bool> want, ReadOnlySpan<bool> mustDraw, Span<int> waited, int budget, int maxWait, Span<bool> draw, int near = 1)
    {
        int count = want.Length;
        int used = 0;
        for (int i = 0; i < count; i++)
        {
            draw[i] = false;
            if (!want[i]) { waited[i] = 0; continue; }
            if (i < near || mustDraw[i] || budget <= 0 || waited[i] >= maxWait) { draw[i] = true; if (i >= near) used++; }
        }
        // The rest, longest-waiting first (the nearer one of two equals), while the budget lasts.
        while (true)
        {
            int best = -1;
            for (int i = near; i < count; i++)
                if (want[i] && !draw[i] && (best < 0 || waited[i] > waited[best])) best = i;
            if (best < 0 || used >= budget) break;
            draw[best] = true;
            used++;
        }
        for (int i = 0; i < count; i++)
            waited[i] = want[i] && !draw[i] ? waited[i] + 1 : 0;
    }
}
