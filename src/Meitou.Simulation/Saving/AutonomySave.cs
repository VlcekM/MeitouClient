using Meitou.Data.Fcs;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Saving;

/// <summary>
/// An autonomous player squad on its PLATOON record (docs/formats/save.md "Player platoons"; Meitou only): <c>meitou autonomous</c> (bool), its
/// packages in the NPC platoons' keys (<see cref="AiSave"/>), <c>meitou independent</c> (bool: <see cref="Squad.Independent"/>) and
/// <c>meitou home</c> (string: <see cref="Squad.AssignedHome"/>), <c>meitou autonomy owner</c> (string: <see cref="Squad.AutonomyOwner"/>). A squad
/// that is not autonomous has none of them but the home, and its record's AI keys are left as the save had them.
/// </summary>
public static class AutonomySave
{
    /// <summary>
    /// The PLATOON keys: the switch, the independence, the assigned home and the mod that set the switch.
    /// </summary>
    public const string AutonomousKey = "meitou autonomous", IndependentKey = "meitou independent", HomeKey = "meitou home",
        OwnerKey = "meitou autonomy owner";

    /// <summary>
    /// The PLATOON keys of the assigned work building (string: <see cref="Squad.AssignedWork"/>) and the conduct (int: <see cref="Squad.Conduct"/>).
    /// </summary>
    public const string WorkKey = "meitou work", ConductKey = "meitou conduct";

    /// <summary>
    /// Writes the squad's autonomy over the platoon's; a squad no longer autonomous loses the keys, and the AI keys they came with are cleared.
    /// </summary>
    public static void Write(FcsRecord platoon, Squad squad)
    {
        if (squad.AssignedHome is { } home) platoon.Strings[HomeKey] = home;
        else platoon.Strings.Remove(HomeKey);

        if (squad.AssignedWork is { } work) platoon.Strings[WorkKey] = work;
        else platoon.Strings.Remove(WorkKey);

        if (squad.Conduct != Ai.SquadConduct.None) platoon.Ints[ConductKey] = (int)squad.Conduct;
        else platoon.Ints.Remove(ConductKey);

        if (squad.Independent) platoon.Bools[IndependentKey] = true;
        else platoon.Bools.Remove(IndependentKey);

        if (squad is { Autonomous: true, AutonomyOwner: { } owner }) platoon.Strings[OwnerKey] = owner;
        else platoon.Strings.Remove(OwnerKey);

        if (squad.Autonomous)
        {
            platoon.Bools[AutonomousKey] = true;
            AiSave.Write(platoon, AiSave.Of(squad.Ai) ?? squad.SavedAi);
        }
        else if (platoon.Bools.Remove(AutonomousKey))
        {
            AiSave.Write(platoon, null);
        }
    }

    /// <summary>
    /// Gives a loaded player squad the autonomy its platoon record holds: the switch, the home and, for the AI to apply when it attaches the
    /// squad, its packages.
    /// </summary>
    public static void Read(FcsRecord platoon, Squad squad)
    {
        squad.AssignedHome = platoon.Strings.GetValueOrDefault(HomeKey) is { Length: > 0 } home ? home : null;
        squad.AssignedWork = platoon.Strings.GetValueOrDefault(WorkKey) is { Length: > 0 } work ? work : null;
        squad.Conduct = (Ai.SquadConduct)(platoon.Ints.GetValueOrDefault(ConductKey) & (int)Ai.SquadConduct.All);
        squad.Autonomous = platoon.Bools.GetValueOrDefault(AutonomousKey);
        squad.Independent = squad.Autonomous && platoon.Bools.GetValueOrDefault(IndependentKey);
        squad.AutonomyOwner = squad.Autonomous && platoon.Strings.GetValueOrDefault(OwnerKey) is { Length: > 0 } owner ? owner : null;
        if (squad.Autonomous) squad.SavedAi = AiSave.Read(platoon);
    }
}
