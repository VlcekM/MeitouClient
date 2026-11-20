using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// What a player-faction slave's goals of its master's Slave list (job 187, <see cref="ObeyedRoles"/>) need beyond an NPC's: the town its worker goals
/// (189 / 190) work in and their operator slots, and row 35's player-faction desire.
/// </summary>
public sealed partial class AiSystem
{
    #region Work town

    /// <summary>
    /// The town whose workplaces the worker goals 189 / 190 find for <paramref name="mind"/>: a player-faction mind's stamped town
    /// (<see cref="AiMind.WorkTown"/>), else its board's home town.
    /// </summary>
    static CampaignTown WorkTownOf(AiMind mind, Blackboard board) => mind.PlayerSide ? mind.WorkTown : board.HomeTown;

    /// <summary>
    /// The worker goals' finder factor (<c>FUN_1408cac60</c> / <c>FUN_1408cadb0</c>): a workplace of <paramref name="town"/> the character may work at,
    /// the one whose slot it holds or a pick with a free slot.
    /// </summary>
    bool WorkWanted(World world, CharacterId self, AiMind mind, CampaignTown town, TaskType type) =>
        WorkAllowed(world, self.Slot, town) && (HoldsWork(mind, town, type) || PickWork(world, self, town, mind.WorkEpoch, type) is not null);

    /// <summary>
    /// Stamps member <paramref name="k"/>'s work town when it has a player-faction mind: while it obeys, the placed town its squad's leader stands in
    /// (itself without a leader), as the AI's rule writes the platoon's current town from the leader (docs/game/ai.md); else none. Serial.
    /// </summary>
    void StampWorkTown(World world, Squad squad, int k)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(squad.Members[k], out int s) || table.Cold(s) is not { Ai: { PlayerSide: true } mind } cold) return;

        mind.WorkTown = ObeyedRoles(world, squad, cold) is null ? CampaignTown.None : LeaderTown(world, squad, s);
    }

    /// <summary>
    /// The placed town the squad's leader stands in (the character at <paramref name="slot"/> without a living leader); none outside a town.
    /// </summary>
    CampaignTown LeaderTown(World world, Squad squad, int slot)
    {
        var table = world.Characters;
        int at = !squad.Leader.IsNone && table.TryResolveNext(squad.Leader, out int leader) ? leader : slot;
        return TownAt(table.Next[at].Position) is >= 0 and var site ? CampaignTown.OfSite(site) : CampaignTown.None;
    }

    /// <summary>
    /// The operator slots of a player's squad without a board (<see cref="ClaimWork"/> over its members' work towns): a slave on 189 / 190 holds one
    /// as an NPC worker does, the original's Task_OperateMachine adding it to the building's operators (<c>FUN_14035b430</c>, <b>Verified</b>).
    /// Serial, on the package cadence.
    /// </summary>
    void ClaimSlaveWork(World world, Squad squad)
    {
        if (IsPlayers(world, squad)) ClaimWork(world, EngineBoard, squad);
    }

    #endregion

    #region Running away

    /// <summary>
    /// The skill categories of a hip weapon that leave a player-faction character's row 35 wanted (<c>FUN_1405a34e0</c>): Heavy (3), crossbow (6),
    /// polearm (8).
    /// </summary>
    static readonly int[] RunningWeapons = [3, 6, 8];

    /// <summary>
    /// Goal 35 Running away of a player-faction mind (its master's Slave list): done (0) while its list A holds (<c>!39</c> no immediate threat and
    /// <c>6</c> no enemy sensed); else row 35's desire (AI slot 55 <c>FUN_1405a34e0</c>, <b>Verified</b>), whose player-faction branch gives 0 while
    /// either arm works (<c>FUN_140644150</c>), while it shoots at a target that is not down (<c>FUN_1404366b0</c>: the crossbow controller's combat
    /// mode, <see cref="CharacterCold.RangedCombatMode"/>) or while a weapon in its <c>hip</c> section has another <c>skill category</c> than
    /// <see cref="RunningWeapons"/>; then the body's desire (<see cref="SelfPreservation.BodyRunDesire"/>). Reads the last tick's state.
    /// </summary>
    float PlayerSideRunDesire(World world, int slot)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { Medical: { } medical } cold) return 0;

        bool threatened = _combat is { } combat && Bodies.CharacterFacts.ImmediateThreat(combat, world, slot);
        if (!threatened && _aggro?.SensesOf(slot) is not { Enemies: > 0 }) return 0;

        if (medical.ArmWorks(left: true) || medical.ArmWorks(left: false) || Shoots(world, slot, cold) || FightsFromHip(cold)) return 0;

        return SelfPreservation.BodyRunDesire(cold, SelfPreservation.Walled(world, slot));
    }

    /// <summary>
    /// <c>FUN_1404366b0</c>: the crossbow controller is in combat mode and its target is there and not down.
    /// </summary>
    bool Shoots(World world, int slot, CharacterCold cold)
    {
        if (!cold.RangedCombatMode || _combat is not { } combat) return false;

        var table = world.Characters;
        var (target, generation) = combat.TargetOf(slot, table.Previous[slot].Generation);
        var id = new CharacterId(target, generation);
        return target >= 0 && table.IsAlive(id) && table.Cold(target) is { } tc && !Bodies.CharacterFacts.Down(tc);
    }

    /// <summary>
    /// A weapon in the <c>hip</c> section whose <c>skill category</c> is not one of <see cref="RunningWeapons"/> (the loop of
    /// <c>FUN_1405a34e0</c>).
    /// </summary>
    bool FightsFromHip(CharacterCold cold)
    {
        if (cold.Inventory is not { } inventory) return false;

        foreach (var item in inventory.Items)
        {
            if (item.Section != "hip" || data.Combat.Weapon(item.Record) is not { } weapon) continue;
            if (Array.IndexOf(RunningWeapons, weapon.SkillCategory) < 0) return true;
        }

        return false;
    }

    #endregion
}
