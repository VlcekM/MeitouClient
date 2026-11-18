using Meitou.Simulation;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/SquadUtil.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.2 (PA §2.2; CAP 5). Meitou 0.17.3
// (tag v0.17.3, fa34a1b). Sim thread, serial steps (both read world state).
//
// Engine calls of the C++ and what replaces them (E1):
//   ou->player->getFaction()->getAllActiveSquads(), getActivePlatoon()->getThings() (:21-48) ... PgEngine.PlayerSquads (world.Squads.All of the
//       player's faction, not dead squads)
//   obj->isValid(), getDataType() CHARACTER / HUMAN_CHARACTER / ANIMAL_CHARACTER, isDead() (:50-62) ... PgEngine.Exists / IsDead (every squad member
//       of Meitou is a character)
//   ou->player->selectedCharacters (:73-81) ... PgEngine.SelectedCharacters (world.Player.Selection, world state)
//   gui->selectedPlayerCharacter (the fallback when the selection is empty, :83-89) ... none: Meitou keeps no "active character" apart from the
//       selection (PlayerState.cs:81-121: Selection, Squad, Inspected = another faction's character, Pointed = under the pointer), so an empty
//       selection collects nobody (divergences.md "ContextMenuHooks")
// Callers: ArenaIngress (src/Game/PgIngressHost.cs: FirstSquadMember for HasLocationSite :1111, CollectSelectedCharacters for BeginApproachForUI
// :1198). ArenaUI's roster (:453) reads PgEngine.PlayerSquads through its own host (task 4.5a) with the same filter.

/// <summary>The glue of the original SquadUtil.cpp.</summary>
static class SquadUtil
{
    // C++: SquadUtil.cpp:20-64
    /// <summary>The living characters of the player faction's active squads, squad by squad.</summary>
    internal static List<CharacterId> CollectPlayerSquad(World world)
    {
        var list = new List<CharacterId>();
        foreach (var squad in PgEngine.PlayerSquads(world))
            foreach (var id in squad.Members)
                if (PgEngine.Exists(world, id) && !PgEngine.IsDead(world, id))
                    list.Add(id);
        return list;
    }

    // C++: SquadUtil.cpp:66-90
    /// <summary>The selected characters that exist and are not dead, in selection order (the C++'s fallback to the active character: see the
    /// header).</summary>
    internal static List<CharacterId> CollectSelectedCharacters(World world)
    {
        var list = new List<CharacterId>();
        foreach (var id in PgEngine.SelectedCharacters(world))
            if (PgEngine.Exists(world, id) && !PgEngine.IsDead(world, id))
                list.Add(id);
        return list;
    }
}
