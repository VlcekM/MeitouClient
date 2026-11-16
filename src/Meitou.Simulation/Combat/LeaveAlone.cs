using Meitou.Data.Gameplay;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Dialogue;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The leave-alone test <c>FUN_1408569a0</c> (docs/game/combat.md "Target score", <b>Verified</b> by decompile): the target score
/// <c>FUN_14059e300</c> skips every candidate it passes, so every chooser that scores (the fight's own choice, the attack-on-sight look, the
/// assault finder) leaves these alone; 21's finder and the sensed-enemy finder <c>FUN_14059fa70</c> also call it before scoring.
/// </summary>
public static class LeaveAlone
{
    /// <summary>
    /// The tools the test reads: the live relation table, the squads' memory and the fights (null where the world has none).
    /// </summary>
    public readonly record struct Kit(FactionRelations? Relations, SquadMemory? Memory, CombatSystem? Combat);

    /// <summary>
    /// Whether <paramref name="chooser"/> (<paramref name="cc"/>) leaves <paramref name="candidate"/> (<paramref name="tc"/>) alone: the NPC branch
    /// for a chooser not of the player's faction (the faction's <c>+0x250</c>), else the player branch.
    /// </summary>
    public static bool Spares(World world, in Kit kit, int chooser, CharacterCold cc, int candidate, CharacterCold tc) =>
        world.Player.Faction >= 0 && cc.Faction == world.Player.Faction
            ? PlayerSpares(world, kit, chooser, cc, candidate, tc)
            : NpcSpares(world, kit, cc, candidate, tc);

    /// <summary>
    /// The NPC branch: a candidate the chooser's squad holds no TEMP_ENEMY on (<c>FUN_140678960</c> tag 4) and whose faction the chooser's is not
    /// hostile to (relation above -30, <c>FUN_1406b25d0</c>) is left alone when it has diplomatic status (<c>FUN_140796240</c> flag 0: NPC class 5,
    /// no biome test) or marches in a campaign still in phase 0 (<c>FUN_1409c4b90</c>).
    /// </summary>
    static bool NpcSpares(World world, in Kit kit, CharacterCold cc, int candidate, CharacterCold tc)
    {
        var target = world.Characters.IdOf(candidate);
        if (kit.Memory is { } memory && cc.SquadId >= 0 && memory.HasShort(world, cc.SquadId, target, MemoryTag.TempEnemy)) return false;
        if (kit.Relations is { } r && r.Contains(cc.Faction) && r.Contains(tc.Faction) && r.IsHostile(cc.Faction, tc.Faction)) return false;

        return CharacterFacts.DiplomaticStatus(tc, default, null, false) || CharacterFacts.MarchingToCampaign(world, tc);
    }

    /// <summary>
    /// The player branch: an obedient slave (state 1, holding job 187) leaves alone whom its owner regards as friendly; any chooser leaves alone a
    /// candidate that does not know of it (<c>FUN_140855960</c> on the candidate's senses), is not down, is of another faction, while the chooser
    /// mans no turret (its turret handle <c>+0x260</c> null), unless the candidate is after someone (its AI's target, <c>FUN_1404357a0</c>) with its
    /// running task's intended aggression (status <c>+0xa0</c>) above 0 and the chooser regards that one as friendly (slot 126).
    /// </summary>
    static bool PlayerSpares(World world, in Kit kit, int chooser, CharacterCold cc, int candidate, CharacterCold tc)
    {
        var table = world.Characters;
        if (SpareForMaster(world, kit, cc, candidate, tc)) return true;
        if (CharacterFacts.Knows(world, candidate, table.IdOf(chooser)) || CharacterFacts.Down(tc)) return false;
        if (cc.Gunning is not null || tc.Faction == cc.Faction) return false;

        int after = AfterWhom(world, kit, candidate, tc);
        if (after < 0 || TaskAggression.OfTask(tc) <= 0) return true;

        return !RegardsFriendly(world, kit, chooser, cc, after);
    }

    /// <summary>
    /// The slave part of the player branch: a chooser in slave state 1 holding job 187 whose owner (the master squad,
    /// <see cref="CharacterCold.SlaveMaster"/>) regards the candidate as friendly (the owner's slot 126,
    /// <see cref="Aggro.AggroSystem.MasterRegardsFriendly(World, FactionRelations, SquadMemory?, Factions.Squad, CharacterId, CharacterCold)"/>).
    /// </summary>
    static bool SpareForMaster(World world, in Kit kit, CharacterCold cc, int candidate, CharacterCold tc)
    {
        if (cc is not { SlaveState: 1, SlaveMaster: >= 0 } || !Kidnap.ObedientSlave.HasJob(cc)) return false;
        if (kit.Relations is not { } relations || world.Squads.Find(cc.SlaveMaster) is not { } master) return false;

        return Aggro.AggroSystem.MasterRegardsFriendly(world, relations, kit.Memory, master, world.Characters.IdOf(candidate), tc);
    }

    /// <summary>
    /// The candidate's AI target (<c>FUN_1404357a0</c>: the AI's target hand): its fight's target while it fights, else its running task's target
    /// character; -1 for none or one gone.
    /// </summary>
    static int AfterWhom(World world, in Kit kit, int candidate, CharacterCold tc)
    {
        var table = world.Characters;
        if (kit.Combat is { } combat && combat.TargetOf(candidate, table.Previous[candidate].Generation) is { Slot: >= 0 } fight
            && table.TryResolveNext(new CharacterId(fight.Slot, fight.Generation), out int fought))
        {
            return fought;
        }

        return tc.Running is { Target.IsNone: false } run && table.TryResolveNext(run.Target, out int aimed) ? aimed : -1;
    }

    /// <summary>
    /// Slot 126 of the chooser on <paramref name="other"/> (<c>FUN_1407923d0</c>): the dialogue system's rule, else the same or an allied faction.
    /// </summary>
    static bool RegardsFriendly(World world, in Kit kit, int chooser, CharacterCold cc, int other)
    {
        if (world.SystemOf<DialogueSystem>() is { } dialogue) return CharacterFacts.RegardsFriendly(dialogue, world, chooser, other);

        var oc = world.Characters.Cold(other)!;
        return oc.Faction == cc.Faction
            || kit.Relations is { } r && r.Contains(cc.Faction) && r.Contains(oc.Faction) && r.IsAlly(cc.Faction, oc.Faction);
    }
}
