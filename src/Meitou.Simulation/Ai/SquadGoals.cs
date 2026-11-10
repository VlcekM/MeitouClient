using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Ai;

/// <summary>
/// What a squad member works on: a character (a body to lift or loot, a patient, a captive) or a place (a machine or a cage, by its id).
/// </summary>
public readonly record struct GoalTarget(CharacterId Body, string? Place)
{
    /// <summary>
    /// A target that is a character.
    /// </summary>
    public static GoalTarget Of(CharacterId body) => new(body, null);
    /// <summary>
    /// A target that is a place (a fixture or building id).
    /// </summary>
    public static GoalTarget At(string place) => new(CharacterId.None, place);
}

/// <summary>
/// A system that owns work on a target: it publishes the (worker, target) pairs it is acting out into the squads' goal list
/// (<see cref="SquadGoals.Of"/>).
/// </summary>
public interface IGoalSource
{
    /// <summary>
    /// Adds the (worker, target) pairs it acts out now to <paramref name="goals"/>.
    /// </summary>
    void Publish(World world, SquadGoals goals);
}

/// <summary>
/// The squads' goal list: the entries of each squad blackboard's list at +0x238 (worker, sub-target hand). Every finder that skips what a squad mate
/// already works on asks it (<c>Blackboard::howManyGuysWorkingOnThisSubTarget</c>, <c>FUN_1402690e0</c>: the entries of the asker's squad whose
/// worker is neither the asker nor the target and whose sub-target is the target; <c>isMySubTarget</c>, <c>FUN_140269190</c>: an entry of that worker
/// with that target; <b>Verified</b>, docs/game/ai-tasks.md). Meitou keeps no stored list: each system that acts out work publishes its pairs
/// (<see cref="IGoalSource"/>) when a finder takes a snapshot, and the finder adds what it starts itself (<see cref="Add"/>).
/// </summary>
public sealed class SquadGoals
{
    /// <summary>
    /// The entries: the squad, the worker and the target.
    /// </summary>
    readonly List<(int Squad, CharacterId Worker, GoalTarget Target)> _entries = [];
    /// <summary>
    /// The world's goal claims, refreshed by every pair <see cref="Add"/> records (null: none).
    /// </summary>
    GoalClaims? _book;

    /// <summary>
    /// A snapshot of every source's work now plus the claims that ended within the last <see cref="GoalClaims.Seconds"/> (an entry of the original's
    /// list stays that long after its last refresh, <c>FUN_14026a320</c>, <c>FUN_140269ff0</c>); the sources' work refreshes the world's book.
    /// </summary>
    public static SquadGoals Of(World world)
    {
        world.Claims.Now = world.Tick;
        var goals = new SquadGoals { _book = world.Claims };
        foreach (var system in world.Systems)
            if (system is IGoalSource source) source.Publish(world, goals);
        goals._entries.Clear();
        goals._entries.AddRange(world.Claims.Live(world.Tick));
        return goals;
    }

    /// <summary>
    /// Records that <paramref name="worker"/> of <paramref name="squad"/> works on <paramref name="target"/>; workers outside every squad (-1) share
    /// one list (the original has none: every character has a squad).
    /// </summary>
    public void Add(int squad, CharacterId worker, GoalTarget target)
    {
        if (target.Body.IsNone && target.Place is null) return;
        _entries.Add((squad, worker, target));
        _book?.Refresh(squad, worker, target);
    }

    /// <summary>
    /// How many other members of <paramref name="squad"/> work on <paramref name="target"/> (<c>FUN_1402690e0</c>): not the asker, nor the target
    /// itself.
    /// </summary>
    public int Working(int squad, CharacterId asker, GoalTarget target)
    {
        int n = 0;
        foreach (var (s, worker, t) in _entries)
            if (s == squad && worker != asker && worker != target.Body && t == target) n++;
        return n;
    }

    /// <summary>
    /// How many workers of any squad work on <paramref name="target"/>: not the asker, nor the target itself. Engine only (the original's list is
    /// per squad, <see cref="Working"/>).
    /// </summary>
    public int WorkingAny(CharacterId asker, GoalTarget target)
    {
        int n = 0;
        foreach (var (_, worker, t) in _entries)
            if (worker != asker && worker != target.Body && t == target) n++;
        return n;
    }

    /// <summary>
    /// Whether <paramref name="worker"/> itself works on <paramref name="target"/> (<c>FUN_140269190</c>).
    /// </summary>
    public bool IsMine(CharacterId worker, GoalTarget target)
    {
        foreach (var (_, w, t) in _entries)
            if (w == worker && t == target) return true;
        return false;
    }
}