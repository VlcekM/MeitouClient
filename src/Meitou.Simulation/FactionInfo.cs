namespace Meitou.Simulation;

/// <summary>
/// What a row of the Faction tab's info panel is (docs/game/ui-screens.md "Faction tab"): it picks the row's colours.
/// </summary>
public enum FactionInfoKind : byte
{
    /// <summary>
    /// A heading: a faction at war with the player.
    /// </summary>
    Heading,
    /// <summary>
    /// A name row: a contract's title or a campaign.
    /// </summary>
    Name,
    /// <summary>
    /// A detail row of the name above it.
    /// </summary>
    Detail,
    /// <summary>
    /// An empty row between entries.
    /// </summary>
    Spacer,
    /// <summary>
    /// A queued campaign that has not started.
    /// </summary>
    Queued,
    /// <summary>
    /// A rule line between contracts.
    /// </summary>
    Rule,
}

/// <summary>
/// A row of the Faction tab's Active Contracts or Upcoming Events: a label and a value, and for a time the game hours left (<see cref="Hours"/>, NaN:
/// none; the interface prints it as <c>TimeOfDay::getTotalTimeString</c> does).
/// </summary>
public readonly record struct FactionInfoLine(FactionInfoKind Kind, string Label, string Value = "", double Hours = double.NaN);

/// <summary>
/// The world's Faction tab: the contracts and events it lists.
/// </summary>
public sealed partial class World
{
    /// <summary>
    /// The AI signals of the contracts the Faction tab lists: <c>Contract_HiredAlly</c> (bodyguards) and <c>Contract_DefendPlayerBase</c>
    /// (base defenders).
    /// </summary>
    const int HiredAllySignal = 12, DefendBaseSignal = 15;

    /// <summary>
    /// Active Contracts (<c>FUN_1404927f0</c>, <c>getGUIData</c> of the two contract packages): per squad running one that involves the player
    /// (<see cref="Population.MapSquads.InvolvesPlayer"/>), "Bodyguard Contract" or "Base Defence Contract" with the hired squad's faction,
    /// "-Protecting:" and the name of what it protects when that resolves (the bodyguard's mission target, a character; the base defender's mission
    /// town, <c>FUN_1402953f0</c>), "-Expires in:" and the time left, then a rule. One entry per hired squad (the original lists each hired
    /// character's package; Meitou's packages are the squad's).
    /// </summary>
    public List<FactionInfoLine> ActiveContracts(Func<int, string?> factionName)
    {
        var lines = new List<FactionInfoLine>();
        foreach (var squad in Squads.All)
        {
            if (squad.Ai is not { Contract.Signal: HiredAllySignal or DefendBaseSignal } board || !Population.MapSquads.InvolvesPlayer(this, board))
                continue;

            string title = board.Contract.Signal == HiredAllySignal ? "Bodyguard Contract" : "Base Defence Contract";
            lines.Add(new(FactionInfoKind.Name, title, factionName(squad.Faction) ?? ""));
            if (ProtectedName(board) is { } name) lines.Add(new(FactionInfoKind.Detail, "-Protecting:", name));
            lines.Add(new(FactionInfoKind.Detail, "-Expires in:", "", board.ContractEnd - GameHours));
            lines.Add(new(FactionInfoKind.Rule, "-----------"));
        }

        return lines;
    }

    /// <summary>
    /// The name of what a contract protects as the mods' text sources show it: a base defender's mission town, a bodyguard's mission target; null
    /// when it does not resolve.
    /// </summary>
    string? ProtectedName(Ai.Blackboard board)
    {
        if (board.Contract?.Signal == DefendBaseSignal)
            return Buildings.FindTown(board.ContractTown.PlayerTown) is { } town ? ShownName(town) : null;

        var table = Characters;
        return table.TryResolveNext(board.MissionTarget, out int slot) && table.Cold(slot) is { } c ? ShownName(c, ShownPlace.Window) : null;
    }
}
