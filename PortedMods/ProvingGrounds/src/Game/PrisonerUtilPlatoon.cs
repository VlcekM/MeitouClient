using Meitou.Simulation;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/PrisonerUtilPlatoon.cpp (Proving Grounds by Shutori, GPLv3), task 6.1 (PA §2.19, CAP 15): the
// player's cages and the player's ownership of a building, the bodies of the declarations in src/Game/PrisonerUtilInternal.cs. Engine reads
// through src/Engine/Cages.cs. Sim thread, serial steps only.
//
// Engine calls of the C++ (E1) and what replaces them:
//   ou->player->getFaction(), Faction::getAllActiveSquads(), Platoon::getOwnerships() (:24-60) ... Cages.PlayerFaction: Meitou keeps ownership
//       per faction (Fixture.Owner, BuildSite.Owner, the town property's owner), not per squad: the player faction stands for the set of its
//       squads' Ownerships (none: no ownerships, as the C++'s !ou / !player / !faction / no squads); docs/divergences.md "PrisonerUtil"
//   Ownerships::getHomeFurnitureOfType(found, BF_CAGE), getBuildingsWithFunction(found, BF_CAGE) (:88-90) ... Cages.CageIds filtered by
//       Cages.Owns (every cage the faction owns, registry order; home furniture and owned cage buildings are one list in Meitou)
//   Ownerships::isOwned(building->getHandle()) (:110-116) ... Cages.Owns(faction, key)

static partial class PrisonerUtilInternal
{
    // C++: PrisonerUtilPlatoon.cpp:24-60 CollectPlayerOwnerships: the player faction (-1: none), see the header
    static int CollectPlayerOwnerships(World world) => Cages.PlayerFaction(world);

    // C++: PrisonerUtilPlatoon.cpp:62-70
    static bool ContainsBuilding(List<string> list, string building)
    {
        for (int i = 0; i < list.Count; ++i)
        {
            if (list[i] == building)
                return true;
        }
        return false;
    }

    // C++: PrisonerUtilPlatoon.cpp:73-100
    internal static partial void CollectPlayerOwnedCageBuildings(World world, List<string> output)
    {
        output.Clear();

        int ownerships = CollectPlayerOwnerships(world);
        if (ownerships < 0)
            return;

        var found = new List<string>();
        Cages.CageIds(world, found);   // getHomeFurnitureOfType / getBuildingsWithFunction(BF_CAGE): filtered by ownership below
        for (int i = 0; i < found.Count; ++i)
        {
            string building = found[i];
            if (building is null || ContainsBuilding(output, building) || !Cages.Owns(world, ownerships, building))
                continue;
            output.Add(building);
        }
    }

    // C++: PrisonerUtilPlatoon.cpp:102-118
    internal static partial bool IsBuildingOwnedByPlayer(World world, string? building)
    {
        if (building is null)
            return false;

        int ownerships = CollectPlayerOwnerships(world);
        return Cages.Owns(world, ownerships, building);
    }
}
