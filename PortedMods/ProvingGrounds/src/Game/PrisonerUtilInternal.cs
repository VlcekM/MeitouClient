using Meitou.Simulation;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/PrisonerUtilInternal.h (Proving Grounds by Shutori, GPLv3), task 6.1: the two declarations of the
// C++ namespace PrisonerUtilInternal, as C# partial method declarations; their bodies are src/Game/PrisonerUtilPlatoon.cs (the C++
// PrisonerUtilPlatoon.cpp). A Building* is a building / cage key (src/Engine/Cages.cs header; null = the C++ NULL).

static partial class PrisonerUtilInternal
{
    // C++: PrisonerUtilInternal.h:9
    internal static partial void CollectPlayerOwnedCageBuildings(World world, List<string> output);

    // C++: PrisonerUtilInternal.h:10
    internal static partial bool IsBuildingOwnedByPlayer(World world, string? building);
}
