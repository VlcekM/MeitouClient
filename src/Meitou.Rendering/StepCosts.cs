namespace Meitou.Rendering;

/// <summary>
/// What steps of each kind have cost lately (an exponential average, so one stall - a collection, a slow driver call - is forgotten after a few
/// steps): the queues that run steps against a time budget (<see cref="UploadQueue"/>, the texture cache's pump, the foliage update) ask
/// <see cref="Fits"/> before starting a step after the first, so a heavy kind is not started on top of what has already used the budget.
/// A kind that never fits still runs: the first step of a run does not ask. Render thread only.
/// </summary>
internal sealed class StepCosts
{
    /// <summary>The share of a new measurement in the average (a stall of 20 ms against 0.2 raises the expectation to 5 ms, and it is under 1 ms again after 6 steps).</summary>
    const double Rate = 0.25;

    readonly Dictionary<object, double> expected = [];

    /// <summary>The average cost of a kind in milliseconds; 0 for a kind not seen yet.</summary>
    public double Expected(object kind) => expected.GetValueOrDefault(kind);

    public void Learn(object kind, double ms) => expected[kind] = expected.TryGetValue(kind, out var e) ? e + Rate * (ms - e) : ms;

    /// <summary>How far past its budget a run may end (milliseconds). The queues start a step while the budget has not passed, as they always did, so
    /// a run ends a step late; this lets that step be one of the usual size (many small ones, like grass slabs, fill the budget exactly as before)
    /// and only defers one that is expected to take the run past budget + slack.</summary>
    public const double Slack = 1.0;

    /// <summary>Whether a step of this kind is expected to end within the budget plus <see cref="Slack"/> if it starts <paramref name="elapsedMs"/> into the run.</summary>
    public bool Fits(object kind, double elapsedMs, double budgetMs) => elapsedMs + Expected(kind) <= budgetMs + Slack;
}