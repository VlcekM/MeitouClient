using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Ai;

/// <summary>
/// Each character's <c>followMe</c> (StateBroadcastData <c>+0xa8</c>; docs/game/ai-tasks.md "Desire and commitment", <b>Verified</b>): the AI update
/// <c>FUN_1405a4ae0</c> sets it to its current goal's TaskData <c>followMeWeight</c> (<c>AITaskSytem</c> <c>+0x1c0</c>, the goal's TaskData
/// <c>+0x30</c>; 0 without a goal), raised to 0.1 when below. Following's desire reads the followed character's. Stamped for every character in the
/// serial schedule step, so the parallel think reads one value per tick whatever the partitions.
/// </summary>
public sealed class FollowMe
{
    /// <summary>
    /// The least <c>followMe</c> the update leaves (<c>DAT_14167fad0</c>, 0.1).
    /// </summary>
    public const float Floor = 0.1f;

    /// <summary>
    /// The stamped values by slot (<see cref="Stamp"/>).
    /// </summary>
    float[] _values = [];

    /// <summary>
    /// Stamps every living character's value (serial).
    /// </summary>
    public void Stamp(World world)
    {
        var table = world.Characters;
        if (_values.Length < table.HighWater) _values = new float[Math.Max(table.HighWater, _values.Length * 2)];

        for (int i = 0; i < table.HighWater; i++)
            _values[i] = table.Next[i].Alive && table.Cold(i) is { } cold ? Current(table, i, cold) : Floor;
    }

    /// <summary>
    /// The stamped value of the character at <paramref name="slot"/> (<see cref="Floor"/> before its first stamp).
    /// </summary>
    public float Of(int slot) => slot < _values.Length ? _values[slot] : Floor;

    /// <summary>
    /// The character's value now: its current goal's weight (<see cref="TaskDataTable.FollowMeWeight"/>), at least <see cref="Floor"/>.
    /// </summary>
    public static float Current(CharacterTable table, int slot, CharacterCold cold) =>
        Goal(table, slot, cold) is { } row ? MathF.Max(TaskDataTable.FollowMeWeight(row), Floor) : Floor;

    /// <summary>
    /// The current goal's row: the AI's chosen goal, else the row of the order, job or goal whose task runs (a player character's chooser goal).
    /// </summary>
    static TaskType? Goal(CharacterTable table, int slot, CharacterCold cold)
    {
        if (cold.Ai?.Goal is { } goal) return goal.Task.Type;

        var task = (CharacterTask)table.Next[slot].Task;
        return cold.Running is { } run && run.Runs(task) ? run.Row : null;
    }
}
