using Meitou.Data.Fcs;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Saving;

/// <summary>
/// An autonomous player squad on its PLATOON record (docs/formats/save.md "Player platoons"; Meitou only): <c>meitou autonomous</c> (bool), its
/// packages in the NPC platoons' keys (<see cref="AiSave"/>) and <c>meitou home</c> (string: <see cref="Squad.AssignedHome"/>). A squad that is
/// not autonomous has none of them, and its record's AI keys are left as the save had them.
/// </summary>
public static class AutonomySave
{
    /// <summary>
    /// The PLATOON keys: the switch and the assigned home.
    /// </summary>
    public const string AutonomousKey = "meitou autonomous", HomeKey = "meitou home";

    /// <summary>
    /// Writes the squad's autonomy over the platoon's; a squad no longer autonomous loses the keys, and the AI keys they came with are cleared.
    /// </summary>
    public static void Write(FcsRecord platoon, Squad squad)
    {
        if (squad.AssignedHome is { } home) platoon.Strings[HomeKey] = home;
        else platoon.Strings.Remove(HomeKey);

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
        squad.Autonomous = platoon.Bools.GetValueOrDefault(AutonomousKey);
        if (squad.Autonomous) squad.SavedAi = AiSave.Read(platoon);
    }
}
