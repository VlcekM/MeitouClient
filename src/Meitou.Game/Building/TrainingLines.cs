using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Editor;
using Meitou.Game.Ui;

namespace Meitou.Game.Building;

/// <summary>
/// The lines a selected training dummy (BF_TRAINING, function 7) adds to the building panel (UseableStuff slot 40, docs/game/ui-screens.md "Selected
/// building"): <c>Training:</c> and its stat among the state lines, and for a dummy with no machine of the player's (a town's) Workers and Max Skill
/// Level among the stats.
/// </summary>
sealed class TrainingLines(PlayerInterface player)
{
    /// <summary>
    /// Each BUILDING record's machine data by record id (<see cref="MachineData"/>, asked once per record).
    /// </summary>
    readonly Dictionary<string, MachineData?> _data = new(StringComparer.Ordinal);

    /// <summary>
    /// A BUILDING record's machine data; null for one without (the host sets it).
    /// </summary>
    public Func<string, MachineData?>? MachineData { get; set; }

    /// <summary>
    /// Adds <c>Training:</c> with the dummy's stat (the editor's stat name, <c>FUN_140888440</c>) when <paramref name="record"/> is a training dummy.
    /// </summary>
    internal void AddStateLine(string? record, List<PlayerInterface.HudLine> state)
    {
        if (DummyData(record) is not { } data) return;

        string stat = EditorRaceStats.EditorStatName((StatsEnumerated)data.Stat);
        state.Add(new(stat.Length > 0 ? $"{Loc.Tr("Training:")} {stat}" : "Training:", ""));
    }

    /// <summary>
    /// Adds Workers (the users of the town dummy's machine, 0 before anyone has used it) and Max Skill Level when the selected building is a
    /// training dummy.
    /// </summary>
    internal void AddStatsLines(BuildingSelection.SelectedBuilding focus, List<PlayerInterface.HudLine> stats)
    {
        if (DummyData(focus.Record) is not { } data) return;

        int operators = SpotOf(focus) is { } spot ? player.Production.Machine(spot.Id)?.Operators ?? 0 : 0;
        stats.Add(ProductionWindows.WorkersLine(operators, data.PowerOperators));
        stats.Add(ProductionWindows.MaxSkillLine(data.OutputValue));
    }

    /// <summary>
    /// The machine data of <paramref name="record"/> when its functionality is BF_TRAINING, whatever its stat; null for any other.
    /// </summary>
    MachineData? DummyData(string? record)
    {
        if (record is null) return null;
        if (!_data.TryGetValue(record, out var data)) _data[record] = data = MachineData?.Invoke(record);

        return data is { Function: Data.Gameplay.Construction.MachineData.TrainingFunction } ? data : null;
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
