using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Ai;

/// <summary>
/// One request on a platoon's blackboard (<c>Blackboard</c> <c>+0x158</c>, filed by <c>FUN_140269ff0</c>, <b>Verified</b>): the character that asked,
/// the member it is for (<see cref="CharacterId.None"/>: any), the goal it asks for (its type, tier and weight) and the tick it lapses at.
/// </summary>
internal readonly record struct BoardRequest(CharacterId Asker, CharacterId Subject, AiGoal Goal, long Until);

/// <summary>
/// A platoon blackboard's requests (docs/game/ai.md "Requests"): goals other characters file for a member to weigh, scored with its tier's goals
/// (<see cref="TierRungs"/>, <c>FUN_140510d60</c> copies the tier's entries whose subject is none or the chooser, <c>FUN_140269240</c>). The only
/// filer is first aid (<see cref="FirstAid.FirstAidSystem"/>: 62 Standing still on the patient). Held on the <see cref="Factions.Squad"/>, as every
/// platoon has a blackboard in the original and Meitou's player squads have none. Written in serial phases only (filed, lapsed); the think reads it.
/// Not saved (the running treatment files it again on every update); hashed while it holds any.
/// </summary>
internal sealed class BoardRequests
{
    /// <summary>
    /// A request's time to live after its last filing (<c>FUN_140269ff0</c> writes 3.0 at <c>+0x90</c>, <b>Verified</b>), counted down by the
    /// blackboard's update, which drops one below 0 (<c>FUN_14026a320</c>, <b>Verified</b>).
    /// </summary>
    public const float LifeSeconds = 3;

    /// <summary>
    /// The live requests, in filing order (one per asker).
    /// </summary>
    readonly List<BoardRequest> _entries = [];

    /// <summary>
    /// How many requests the board holds.
    /// </summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Files a request on the blackboard of <paramref name="subject"/>'s platoon (serial; the first aid update's call, <c>FUN_1403346d0</c>,
    /// <b>Verified</b>, takes the board of the character it names): nothing for a character with no squad.
    /// </summary>
    public static void FileOn(World world, CharacterId asker, CharacterId subject, TaskType type, TaskPriority tier, float weight) =>
        FileOn(world, subject, asker, subject, type, tier, weight);

    /// <summary>
    /// Files a request on the blackboard of <paramref name="owner"/>'s platoon (serial): the splint update's call (<c>FUN_14034ef20</c>,
    /// <b>Verified</b>) takes the medic's own board (<c>FUN_140507070</c> of its AI: AI <c>+0x10</c>, its platoon), so a patient of another squad
    /// never reads it. Nothing for an owner with no squad.
    /// </summary>
    public static void FileOn(World world, CharacterId owner, CharacterId asker, CharacterId subject, TaskType type, TaskPriority tier, float weight)
    {
        if (!world.Characters.TryResolveNext(owner, out int slot) || world.Characters.Cold(slot) is not { SquadId: >= 0 } cold) return;
        if (world.Squads.Find(cold.SquadId) is not { } squad) return;

        (squad.Requests ??= new BoardRequests()).File(world, asker, subject, type, tier, weight);
    }

    /// <summary>
    /// Drops every blackboard's lapsed requests (serial, once a tick, before the think; the blackboards' update).
    /// </summary>
    public static void LapseAll(World world)
    {
        foreach (var squad in world.Squads.All)
            if (squad.Requests is { Count: > 0 } requests) requests.Lapse(world.Tick);
    }

    /// <summary>
    /// Files a request (serial; <c>FUN_140269ff0</c>, <b>Verified</b>): nothing when the asker asks it of itself; an asker's earlier request is
    /// replaced, its life started again; the goal record is kept while the type, tier and weight stay.
    /// </summary>
    public void File(World world, CharacterId asker, CharacterId subject, TaskType type, TaskPriority tier, float weight)
    {
        if (asker == subject) return;

        long until = world.Tick + (long)MathF.Ceiling(LifeSeconds / world.TickSeconds);
        int at = IndexOf(asker);
        var goal = at >= 0 && Same(_entries[at].Goal, type, tier, weight) ? _entries[at].Goal : GoalOf(type, tier, weight);
        var entry = new BoardRequest(asker, subject, goal, until);
        if (at >= 0) _entries[at] = entry;
        else _entries.Add(entry);
    }

    /// <summary>
    /// Drops the requests whose life ran out (serial, each tick; the blackboard update <c>FUN_14026a320</c>).
    /// </summary>
    public void Lapse(long tick) => _entries.RemoveAll(e => e.Until <= tick);

    /// <summary>
    /// The goals the requests of <paramref name="tier"/> ask of <paramref name="who"/> (subject none or itself), in filing order, added to
    /// <paramref name="into"/>. Any phase: a pure read.
    /// </summary>
    public void For(TaskPriority tier, CharacterId who, long tick, List<AiGoal> into)
    {
        foreach (var e in _entries)
        {
            if (e.Goal.Task.Priority == tier && e.Until > tick && (e.Subject.IsNone || e.Subject == who)) into.Add(e.Goal);
        }
    }

    /// <summary>
    /// Adds the requests to the state hash; nothing while none is filed.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_entries.Count == 0) return;

        hasher.Add(0x52657173);   // "Reqs"
        foreach (var e in _entries)
        {
            hasher.Add(e.Asker);
            hasher.Add(e.Subject);
            hasher.Add((int)e.Goal.Task.Type);
            hasher.Add((int)e.Goal.Task.Priority);
            hasher.Add(e.Goal.Weight);
            hasher.Add(e.Until);
        }
    }

    /// <summary>
    /// The place of <paramref name="asker"/>'s request, -1 for none.
    /// </summary>
    int IndexOf(CharacterId asker)
    {
        for (int k = 0; k < _entries.Count; k++)
            if (_entries[k].Asker == asker) return k;
        return -1;
    }

    /// <summary>
    /// Whether a goal record is the one for <paramref name="type"/> at <paramref name="tier"/> and <paramref name="weight"/>.
    /// </summary>
    static bool Same(AiGoal goal, TaskType type, TaskPriority tier, float weight) =>
        goal.Task.Type == type && goal.Task.Priority == tier && goal.Weight == weight;

    /// <summary>
    /// The goal a request asks for: the type at the tier and weight, no target (the request's target hand is none for the only filer), all hours.
    /// </summary>
    static AiGoal GoalOf(TaskType type, TaskPriority tier, float weight) =>
        new(new AiTask("", type.ToString(), type, tier, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 100, weight);
}
