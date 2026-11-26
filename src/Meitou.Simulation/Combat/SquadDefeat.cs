using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Dialogue;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The melee hit's squad defeat (<c>FUN_1404394f0</c>, docs/game/factions-squads-towns.md 3.5, <b>Verified</b> by decompile): a landed blow that
/// leaves its victim out cold or dead while the victim's whole squad is beaten (<see cref="SquadIntact.AllBeaten"/>). The bolt hit
/// (<c>FUN_140439c90</c>) has none of it.
/// </summary>
static class SquadDefeat
{
    /// <summary>
    /// After a melee blow left <paramref name="victim"/> out cold or dead: when its squad is all beaten, in the original's order, the victim's squad
    /// tags the striker 7 DEFEATED_MY_SQUAD_ONCE, the striker's squad tags the victim 8 SQUAD_LOST_TO_ME_ONCE (<c>FUN_140673a20</c>), the striker
    /// says event 15 I_DEFEATED_SQUAD at the victim (<c>FUN_140685070</c>, deferred a tick like the other combat events) and an animal striker
    /// calls <see cref="SoundKind.Victory"/>. No test of the two squads: a squadmate's blow counts too. The victim's state after the blow decides,
    /// so a blow on one already down fires again; the event's own refusals are the only limit.
    /// </summary>
    public static void After(World world, SquadMemory? memory, int striker, int victim)
    {
        var table = world.Characters;
        if (table.Cold(striker) is not { } s || table.Cold(victim) is not { } v || world.Squads.Find(v.SquadId) is not { } squad)
            return;
        if (!SquadIntact.AllBeaten(table, squad)) return;

        var strikerId = table.IdOf(striker);
        var victimId = table.IdOf(victim);
        memory?.Remember(world, v.SquadId, strikerId, MemoryTag.DefeatedMySquad);
        if (s.SquadId >= 0) memory?.Remember(world, s.SquadId, victimId, MemoryTag.SquadLostToMe);

        world.Defer(new DialogueEvent(strikerId, victimId, DialogueEvents.DefeatedSquad) { Tick = world.Tick + 1 });
        CombatSystem.Sound(world, SoundKind.Victory, striker, victim, table.Previous[striker].Position);
    }
}
