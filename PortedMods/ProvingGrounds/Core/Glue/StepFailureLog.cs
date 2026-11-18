namespace ProvingGrounds.Core.Glue;

/// <summary>Counts, per frame-loop step, how often it has thrown in this world, so each step's first failure is logged once (not every
/// tick) and the status can say which steps keep failing. Failing steps are never switched off. One per world (the system that owns it
/// is rebuilt for every new game or load). Sim thread only. Not a port of an original file.</summary>
public sealed class StepFailureLog
{
    readonly long[] counts;

    public StepFailureLog(int steps) => counts = new long[steps];

    /// <summary>Number of steps that have failed at least once.</summary>
    public int FailedSteps { get; private set; }

    /// <summary>Counts one failure of step <paramref name="index"/>; true the first time in this world (log it then), false after.</summary>
    public bool Record(int index)
    {
        if (counts[index]++ > 0) return false;
        FailedSteps++;
        return true;
    }

    /// <summary>How often step <paramref name="index"/> has failed in this world.</summary>
    public long Count(int index) => counts[index];
}
