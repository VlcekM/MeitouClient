using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The followers on order 164 (DA_FOLLOW_WHILE_TALKING, <c>Task_FollowAndTalk</c>, <see cref="DialogueOrders"/>) and their targets (hashed): each
/// follows with the movement's follow task until it and its target are both free of conversation (slot 4 <c>FUN_14033bc90</c>, <b>Verified</b>;
/// a threat on it ends the order by its list B, <c>!158</c>).
/// </summary>
sealed class TalkFollowers(DialogueSystem system)
{
    /// <summary>
    /// Who follows whom on its order.
    /// </summary>
    readonly SortedDictionary<CharacterId, CharacterId> _followers = new(Comparer<CharacterId>.Create(DialogueSystem.CompareIds));

    /// <summary>
    /// How many follow.
    /// </summary>
    public int Count => _followers.Count;

    /// <summary>
    /// <paramref name="who"/>'s order 164 on <paramref name="whom"/> has started.
    /// </summary>
    public void Add(CharacterId who, CharacterId whom) => _followers[who] = whom;

    /// <summary>
    /// Each follow's step (serial): dropped once its follower no longer runs the order; done once the follower and its target are both free of
    /// conversation (or the target is gone) and the follower standing. Either way the follow target goes (#883: a kept one sent its next follow of
    /// its leader after the talk's target instead).
    /// </summary>
    public void Step(World world)
    {
        if (_followers.Count == 0) return;

        var table = world.Characters;
        foreach (var (who, whom) in _followers.ToArray())
        {
            bool there = table.TryResolveNext(who, out int s);
            if (!there || !DialogueOrders.Runs(table, s, TaskType.FollowWhileTalking, whom))
            {
                _followers.Remove(who);
                if (there) ForgetTarget(table.Cold(s)!, whom);
                continue;
            }

            bool talks = !system.Free(who) || !system.Free(whom);
            if (talks && table.TryResolveNext(whom, out _)) continue;

            _followers.Remove(who);
            ForgetTarget(table.Cold(s)!, whom);
            TaskStart.End(world, s, TaskEnd.Done);
        }
    }

    /// <summary>
    /// The follow target goes while it is still the talk's: not when a follow of the same character another order or a job gave runs since.
    /// </summary>
    static void ForgetTarget(CharacterCold cold, CharacterId whom)
    {
        if (cold.FollowOrder != whom) return;
        bool laterFollow = cold.Running is { Task: Movement.CharacterTask.Follow, Row: not TaskType.FollowWhileTalking } run && run.Target == whom;
        if (laterFollow) return;

        cold.FollowOrder = CharacterId.None;
    }

    /// <summary>
    /// Each follower and the target it follows (read only).
    /// </summary>
    public IEnumerable<KeyValuePair<CharacterId, CharacterId>> Pairs => _followers;

    /// <summary>
    /// Adds the followers and their targets, in id order.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        foreach (var (who, whom) in _followers)
        {
            hasher.Add(who);
            hasher.Add(whom);
        }
    }
}
