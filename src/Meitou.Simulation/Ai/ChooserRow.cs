namespace Meitou.Simulation.Ai;

/// <summary>
/// One character's serial look for this tick's chooser (<see cref="DecisionSystem"/>): what each source answered, asked serially before the
/// parallel think so that no source is ever called from it, and what the think's ladder picked (<see cref="Ladder"/>), for the serial pass that
/// starts it. Made in the schedule step and used up in the slow world of the same tick: never carried over a tick, so neither saved nor hashed.
/// The think writes only its own character's row.
/// </summary>
internal sealed class ChooserRow
{
    /// <summary>
    /// The tick the look is of (-1: none yet); a row of another tick is stale.
    /// </summary>
    public long Tick = -1;

    /// <summary>
    /// The character looked at (a slot freed and taken again within the tick holds another).
    /// </summary>
    public CharacterId Id;

    /// <summary>
    /// The chooser runs for the character this tick (its think turn, or asked: a replan bit, a front order to try or that leaves now).
    /// </summary>
    public bool Due;

    /// <summary>
    /// The think ran the ladder over the character (the AI's think, or the pass for a character without a mind).
    /// </summary>
    public bool Thought;

    /// <summary>
    /// Per source (in <see cref="DecisionSystem.Sources"/> order): whether the look asked it (a player character is asked every source; another
    /// character only the order step and the engine's job passes).
    /// </summary>
    public bool[] Asked = [];

    /// <summary>
    /// Per source: its answer (<see cref="IDecisionSource.CanRun"/>).
    /// </summary>
    public DecisionCheck[] Checks = [];

    /// <summary>
    /// Per source of the goal step that can run: its score as the decision step's step 4 gives it (desire x weight, x1.25 for the goal running now,
    /// again below 1 while its task is not finished); 0 otherwise.
    /// </summary>
    public float[] Scores = [];

    /// <summary>
    /// Per entry of the second list (<see cref="CharacterCold.SecondList"/> order, <see cref="SecondCount"/> of them): its score when it can start or
    /// runs and is not done (the scorer <c>FUN_14050df10</c>: desire x weight 1, x1.25 for the one running now, again below 1 while its task is not
    /// finished); 0: no candidate.
    /// </summary>
    public float[] SecondScores = [];

    /// <summary>
    /// How many second-list entries the look scored.
    /// </summary>
    public int SecondCount;

    /// <summary>
    /// The ladder's pick (<see cref="LadderPick.Source"/> -1: no source).
    /// </summary>
    public LadderPick Pick = LadderPick.None(0);

    /// <summary>
    /// The trace name of the goal the ladder picked (null: a source's, or none).
    /// </summary>
    public string? Winner;

    /// <summary>
    /// The look's trace lines, one per source asked (only while <see cref="DecisionSystem.Tracing"/> is on; null otherwise).
    /// </summary>
    public string[]? Lines;

    /// <summary>
    /// Makes the row this tick's for <paramref name="count"/> sources, nothing asked or picked yet.
    /// </summary>
    public void Reset(long tick, int count)
    {
        Tick = tick;
        Due = false;
        Thought = false;
        Pick = LadderPick.None(0);
        Winner = null;
        Lines = null;
        SecondCount = 0;
        if (Asked.Length != count)
        {
            Asked = new bool[count];
            Checks = new DecisionCheck[count];
            Scores = new float[count];
            return;
        }

        Array.Clear(Asked);
        Array.Clear(Checks);
        Array.Clear(Scores);
    }
}
