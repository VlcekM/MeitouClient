namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>
    /// Who looks at whom for the dialogue's walks (docs/animation.md "Head turning"; read only): a Talk order's talker at its target while it walks
    /// up (<c>Task_PlayerTalkto</c>) and an NPC walking up to talk at its target (<c>Task_TalktoPlayer</c>), both whether moving or not; a speaker
    /// following its target while it talks (<c>Task_FollowAndTalk</c>) and a town stalker at its victim (<c>Task_TownStalker</c>) only while they
    /// stand (<see cref="Movement.LookTargets.StillOnly"/>).
    /// </summary>
    internal IEnumerable<(CharacterId Who, CharacterId Whom, bool StillOnly)> LookPairs()
    {
        foreach (var (talker, target) in _talks.Pairs) yield return (talker, target, false);
        foreach (var (npc, target) in _npcApproaches.Pairs) yield return (npc, target, false);
        foreach (var (who, whom) in _followers.Pairs) yield return (who, whom, true);
        foreach (var (stalker, victim) in _stalkers.Pairs) yield return (stalker, victim, true);
    }
}
