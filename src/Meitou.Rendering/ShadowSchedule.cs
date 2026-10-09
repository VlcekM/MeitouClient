namespace Meitou.Rendering;

/// <summary>
/// Which of the wanted cascade redraws a Meitou shadow frame really does. Cascade 0 is cheap (about 0.2 ms) and always goes when wanted; the far
/// cascades cost 1.1 to 1.8 ms each, so no more than <c>budget</c> of them are drawn in one frame: the rest wait, the longest-waiting first, and
/// are drawn after at most <c>maxWait</c> frames of waiting whatever the budget (docs/render-shadows.md "Spikes"). A cascade without a drawn map yet
/// is never deferred. Pure arithmetic, no GPU.
/// </summary>
public static class ShadowSchedule
{
    /// <summary>The default number of far cascades (1 and up) drawn in one frame.</summary>
    public const int DefaultBudget = 1;
    /// <summary>The most frames a wanted redraw waits before it is forced.</summary>
    public const int DefaultMaxWait = 3;

    /// <summary>
    /// Decides <paramref name="draw"/> from <paramref name="want"/> (what the staleness rules asked for this frame, plus what was deferred before)
    /// and <paramref name="mustDraw"/> (no map yet). <paramref name="waited"/> counts, per cascade, the frames its redraw has been deferred: this
    /// call raises it for the cascades kept waiting and zeroes it for the others. A <paramref name="budget"/> of zero or less means no limit.
    /// </summary>
    public static void Pick(ReadOnlySpan<bool> want, ReadOnlySpan<bool> mustDraw, Span<int> waited, int budget, int maxWait, Span<bool> draw)
    {
        int count = want.Length;
        int used = 0;
        for (int i = 0; i < count; i++)
        {
            draw[i] = false;
            if (!want[i]) { waited[i] = 0; continue; }
            if (i == 0 || mustDraw[i] || budget <= 0 || waited[i] >= maxWait) { draw[i] = true; if (i > 0) used++; }
        }
        // The rest, longest-waiting first (the nearer one of two equals), while the budget lasts.
        while (true)
        {
            int best = -1;
            for (int i = 1; i < count; i++)
                if (want[i] && !draw[i] && (best < 0 || waited[i] > waited[best])) best = i;
            if (best < 0 || used >= budget) break;
            draw[best] = true;
            used++;
        }
        for (int i = 0; i < count; i++)
            waited[i] = want[i] && !draw[i] ? waited[i] + 1 : 0;
    }
}
