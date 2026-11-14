using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Editor;
using Meitou.Game.Ui;
using Meitou.Simulation.Production;

namespace Meitou.Game.Building;

/// <summary>
/// The lines of the original's UseableStuff / StorageBuilding panel (slot 40 <c>FUN_140304e90</c>, docs/game/ui-screens.md "Selected building") for a
/// selected building with no machine of the player's (a town's furniture or level building): <c>Training:</c> among the state lines of a training
/// dummy, and in a town Workers then Efficiency and Max efficiency, or a dummy's Max Skill Level.
/// </summary>
sealed class UseableLines(PlayerInterface player)
{
    /// <summary>
    /// The functions the original's building factory (<c>FUN_14057cc70</c>) gives a class of its own other than UseableStuff and StorageBuilding,
    /// or no panel lines (0, 0x12), with 8 (a torture building or UseableStuff by a test not read here) and 27 (rocks, which have their own lines).
    /// </summary>
    static readonly HashSet<int> OtherClasses = [-1, 0, 1, 3, 4, 5, 8, 10, 12, 14, 15, 18, 23, 26, 27];

    /// <summary>
    /// The functions that show something else than Workers and Efficiency: a bed's Healing bonus (6, 25), which the bed's own lines give.
    /// </summary>
    static readonly HashSet<int> Beds = [6, 25];

    /// <summary>
    /// Each BUILDING record's machine data by record id (<see cref="MachineData"/>, asked once per record).
    /// </summary>
    readonly Dictionary<string, MachineData?> _data = new(StringComparer.Ordinal);

    /// <summary>
    /// A BUILDING record's machine data; null for one without (the host sets it).
    /// </summary>
    public Func<string, MachineData?>? MachineData { get; set; }

    /// <summary>
    /// Adds <c>Training:</c> with the dummy's stat (the editor's stat name, <c>FUN_140888440</c>) when <paramref name="record"/> is a training dummy
    /// (function 7, whatever its stat).
    /// </summary>
    internal void AddStateLine(string? record, List<PlayerInterface.HudLine> state)
    {
        if (DataOf(record) is not { Function: Data.Gameplay.Construction.MachineData.TrainingFunction } data) return;

        string stat = EditorRaceStats.EditorStatName((StatsEnumerated)data.Stat);
        state.Add(new(stat.Length > 0 ? $"{Loc.Tr("Training:")} {stat}" : "Training:", ""));
    }

    /// <summary>
    /// Adds the section 6 lines of a UseableStuff or StorageBuilding standing in a town: Workers (slot 155), then Max Skill Level for a training
    /// dummy, else Efficiency and Max efficiency (slot 154). Beds, power consumers and lights are left out.
    /// </summary>
    internal void AddStatsLines(BuildingSelection.SelectedBuilding focus, List<PlayerInterface.HudLine> stats)
    {
        if (focus.Town is not { Length: > 0 } || DataOf(focus.Record) is not { } data || !Useable(data)) return;

        var machine = MachineOf(focus);
        int operators = machine?.Operators ?? 0;
        stats.Add(ProductionWindows.WorkersLine(operators, data.PowerOperators));

        if (data.Function == Data.Gameplay.Construction.MachineData.TrainingFunction)
        {
            stats.Add(ProductionWindows.MaxSkillLine(data.OutputValue));
            return;
        }

        var efficiency = machine?.Efficiency ?? new EfficiencyView(operators, data.PowerOperators, 1, 1, false, data.OutputRate, 1);
        ProductionWindows.AddEfficiencyLines(efficiency, 0, stats);
    }

    /// <summary>
    /// Whether the original makes <paramref name="data"/>'s building a UseableStuff or StorageBuilding whose lines are Workers and Efficiency (or
    /// Max Skill Level): it has a functionality and no farm, is no bed, and draws no power (a town's power supply is not modelled).
    /// </summary>
    static bool Useable(MachineData data) =>
        data.Functionality is not null && data.Farm is null && !OtherClasses.Contains(data.Function) && !Beds.Contains(data.Function)
        && !data.IsConsumer;

    /// <summary>
    /// The machine of the selected furniture, made once a character used it: a seat's by its fixture id, a training dummy's by its spot.
    /// </summary>
    MachineSnapshot? MachineOf(BuildingSelection.SelectedBuilding focus) =>
        player.Production.Machine(focus.Instance) ?? player.Production.Machine(SpotOf(focus)?.Id);

    /// <summary>
    /// The machine data of <paramref name="record"/>, asked of <see cref="MachineData"/> once and kept; null for none.
    /// </summary>
    MachineData? DataOf(string? record)
    {
        if (record is null) return null;
        if (!_data.TryGetValue(record, out var data)) _data[record] = data = MachineData?.Invoke(record);

        return data;
    }

    /// <summary>
    /// The town training dummy the selected piece is: of its house (<c>&lt;house&gt;@dummy&lt;n&gt;</c>), its record, the nearest to it.
    /// </summary>
    Simulation.Locks.TrainingSpot? SpotOf(BuildingSelection.SelectedBuilding focus)
    {
        string prefix = BuildingSelection.HouseKeyOf(focus.Instance) + "@dummy";
        return player.Session.CurrentSnapshot.Dummies
            .Where(s => s.Record == focus.Record && s.Id.StartsWith(prefix, StringComparison.Ordinal))
            .MinBy(s => System.Numerics.Vector3.DistanceSquared(s.Position, focus.Position));
    }
}
