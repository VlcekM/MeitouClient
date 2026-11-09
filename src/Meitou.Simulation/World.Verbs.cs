using Meitou.Data.Gameplay;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Population;

namespace Meitou.Simulation;

/// <summary>
/// Whose the town a character stands in is (the original's status object <c>+0x88</c>, docs/game/ui-input.md "Context menu verbs"):
/// the town's faction against the player's.
/// </summary>
public enum TownOwnership : byte
{
    /// <summary>
    /// In no town (the original's <c>+0x88</c> is null).
    /// </summary>
    None,

    /// <summary>
    /// A town of the player's faction.
    /// </summary>
    Player,

    /// <summary>
    /// A town of any other faction.
    /// </summary>
    Other,
}

/// <summary>
/// What the context menu's pick-up verb (<c>FUN_140796f80</c>) and throw-out verbs (<c>FUN_140793d80</c>) read about a target,
/// asked between ticks (<see cref="World.VerbFactsOf"/>).
/// </summary>
/// <param name="RegardsLead">The target regards the lead as friendly (Character slot 126 on the lead).</param>
/// <param name="LeadRegards">The lead regards the target as friendly.</param>
/// <param name="InsideWalls">The target is inside town walls (<c>amInsideTownWalls</c> above 0); asked only in a town.</param>
/// <param name="SquadTrader">The target's squad template is a trader's (<c>FUN_1405cff70</c>).</param>
/// <param name="Town">Whose town the target stands in.</param>
/// <param name="Bounty">The target's bounty (the ledger entry's amount, claimed or not) for the booking faction of its town's owner
/// (and the owner's own entry, where Meitou books).</param>
/// <param name="Bandit">The target's NPC class is Bandit.</param>
/// <param name="NotReal">The target's faction is flagged "not real".</param>
public readonly record struct VerbFacts(
    bool RegardsLead, bool LeadRegards, bool InsideWalls, bool SquadTrader, TownOwnership Town, int Bounty, bool Bandit, bool NotReal);

public sealed partial class World
{
    /// <summary>
    /// The result of the arrival banter try <paramref name="serial"/> (<see cref="Dialogue.AreaArrival"/>): null while it has not run yet,
    /// else whether the host forgets the group; true without a dialogue system.
    /// </summary>
    public bool? ArrivalForget(int serial) =>
        SystemOf<Dialogue.DialogueSystem>() is not { } dialogue ? true : dialogue.ArrivalResult is var (done, forget) && done == serial ? forget : null;

    /// <summary>
    /// Whether <paramref name="who"/> regards <paramref name="of"/> as friendly (Character slot 126,
    /// <see cref="Dialogue.DialogueSystem.RegardsFriendly(World, int, int)"/>); the host asks between ticks. False without a dialogue system.
    /// </summary>
    public bool RegardsFriendly(CharacterId who, CharacterId of) =>
        Characters.Phase == TablePhase.Outside
        && Characters.TryResolveNext(who, out int a)
        && Characters.TryResolveNext(of, out int b)
        && SystemOf<Dialogue.DialogueSystem>() is { } dialogue
        && CharacterFacts.RegardsFriendly(dialogue, this, a, b);

    /// <summary>
    /// The facts the pick-up verb reads (<see cref="VerbFacts"/>), the host asking between ticks; null when the world has no population data or a
    /// character is gone. The town a character stands in is the one its point is in (<c>AiSystem.TownAt</c>, <b>engine reading</b> of the status
    /// object's <c>+0x88</c>, docs/game/ui-input.md).
    /// </summary>
    public VerbFacts? VerbFactsOf(CharacterId lead, CharacterId target)
    {
        if (Characters.Phase != TablePhase.Outside || !Characters.TryResolveNext(lead, out int l) || !Characters.TryResolveNext(target, out int t))
            return null;

        if (SystemOf<PopulationSystem>()?.Data is not { } data || SystemOf<Dialogue.DialogueSystem>() is not { } dialogue || Characters.Cold(t) is not { } tc)
            return null;

        var town = Ai.AiSystem.TownAt(data, this, Characters.Next[t].Position);
        int owner = town.Site >= 0
            ? (town.Site < data.SiteFactions.Count ? data.SiteFactions[town.Site] : -1)
            : Buildings.FindTown(town.PlayerTown)?.Owner ?? -1;
        var ownership = town.IsNone ? TownOwnership.None : owner >= 0 && owner == Player.Faction ? TownOwnership.Player : TownOwnership.Other;

        // The ledger entry of the booking faction of the town's owner (FUN_140852010 with no faction given);
        // a caged target gets Pick Up whatever this says, so its prison owner is not asked.
        int bounty = tc.Bounties is { } ledger && owner >= 0 ? ledger.BookedAmount(data.Relations, owner) : 0;
        bool squadTrader = tc.SquadId >= 0 && Squads.Find(tc.SquadId) is { } squad && data.Db.Find(squad.TemplateId)?.GetBool("is trader") == true;
        bool notReal = (uint)tc.Faction < (uint)data.Factions.Count && data.Factions[tc.Faction].NotReal;
        bool inside = ownership != TownOwnership.None
            && (SystemOf<Kidnap.TownDutySystem>()?.AmInsideTownWalls(this, Characters.Next[t].Position) ?? false);
        return new(
            CharacterFacts.RegardsFriendly(dialogue, this, t, l),
            CharacterFacts.RegardsFriendly(dialogue, this, l, t),
            inside, squadTrader, ownership, bounty, tc.NpcClass == FundamentalType.Bandit, notReal);
    }
}