using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// DA_FOLLOW_WHILE_TALKING: the speakers following their targets while their conversations last (hashed).
/// </summary>
sealed class TalkFollowers(DialogueSystem system)
{
    /// <summary>
    /// Who follows whom while its conversation lasts.
    /// </summary>
    readonly SortedDictionary<CharacterId, CharacterId> _followers = new(Comparer<CharacterId>.Create(DialogueSystem.CompareIds));

    /// <summary>
    /// How many follow.
    /// </summary>
    public int Count => _followers.Count;

    /// <summary>
    /// <paramref name="who"/> follows <paramref name="whom"/> while its conversation lasts (<see cref="StartFollowing"/>).
    /// </summary>
    public void Add(World world, CharacterId who, CharacterId whom)
    {
        _followers[who] = whom;
        StartFollowing(world, who, whom);
    }

    /// <summary>
    /// The follow lasts while the conversation does: when it is over (or the speaker or target is gone) the speaker stops following and stands.
    /// </summary>
    public void StepFollowers(World world)
    {
        if (_followers.Count == 0) return;
        var table = world.Characters;
        foreach (var (who, whom) in _followers.ToArray())
        {
            bool talking = system.LeadsTalk(who);
            if (talking && table.TryResolveNext(who, out _) && DialogueSystem.Position(world, whom) is not null) continue;
            _followers.Remove(who);
            if (table.TryResolveNext(who, out int s) && table.Cold(s) is { } cold && cold.FollowOrder == whom
                && table.Next[s].Task == (byte)CharacterTask.Follow)
            {
                cold.FollowOrder = CharacterId.None;
                TaskStart.Stand(table, s);
            }
        }
    }

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

    /// <summary>
    /// DA_FOLLOW_WHILE_TALKING (16, Task_FollowAndTalk 164): the speaker follows the target with the shared follow task (FUN_140333400 /
    /// FUN_140333500, the same as a follow order: stand-off 20 to 40 on the follower's side, the follow speed profile; <b>Verified</b>), a character
    /// in a fight excepted (engine choice: no walk to end a fight).
    /// </summary>
    static void StartFollowing(World world, CharacterId who, CharacterId whom)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(who, out int s) || table.Cold(s) is not { InCombat: false }) return;
        TaskStart.Start(world, s, new TaskRequest(CharacterTask.Follow, TaskType.FollowWhileTalking, TaskSource.Npc) { Target = whom });
    }
}
