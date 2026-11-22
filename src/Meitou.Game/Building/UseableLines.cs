using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Editor;
using Meitou.Game.Ui;
using Meitou.Simulation.Production;

namespace Meitou.Game.Building;

/// <summary>
/// The lines of the original's UseableStuff / StorageBuilding / LightBuilding panel (slot 40 <c>FUN_140304e90</c>, docs/game/ui-screens.md "Selected building") for a
/// selected building with no machine of the player's (a town's furniture or level building): <c>Training:</c> among the state lines of a training
/// dummy, and Workers then Efficiency and Max efficiency, or a dummy's Max Skill Level.
/// </summary>
sealed class UseableLines(PlayerInterface player)
{
    /// <summary>
    /// The functions the original's building factory (<c>FUN_14057cc70</c>) gives a class whose slot 40 is not <c>FUN_140304e90</c>, or no panel
    /// lines (0, 0x12), with 8 (a torture building or UseableStuff by a test not read here) and 27 (rocks, which have their own lines). The rest are
    /// UseableStuff, StorageBuilding (2) and LightBuilding (15).
    /// </summary>
    static readonly HashSet<int> OtherClasses = [-1, 0, 1, 3, 4, 5, 8, 10, 12, 14, 18, 23, 26, 27];

    /// <summary>
    /// BF_BED: section 6 is its Healing bonus alone, which the bed's own lines give.
    /// </summary>
    const int BedFunction = 6;

    /// <summary>
    /// BF_SKELETON_BED: section 6 is Workers, Efficiency and power like any UseableStuff, with no Healing bonus.
    /// </summary>
    const int SkeletonBedFunction = 25;

    /// <summary>
    /// Each BUILDING record's machine data by record id (<see cref="MachineData"/>, asked once per record).
    /// </summary>
    readonly Dictionary<string, MachineData?> _data = new(StringComparer.Ordinal);

    /// <summary>
    /// A BUILDING record's machine data; null for one without (the host sets it).
    /// </summary>
    public Func<string, MachineData?>? MachineData { get; set; }

    /// <summary>
    /// Adds <c>[EMPTY]</c> in Title (the uppercased translation of "Empty" in brackets, no value) when the selected UseableStuff, StorageBuilding
    /// or LightBuilding has an inventory with no items (slot 40 section 3, after <c>Town:</c>): a machine's storage, or a town container's
    /// (<paramref name="detail"/>).
    /// </summary>
    internal void AddEmptyLine(BuildingSelection.SelectedBuilding focus, string? record, Simulation.Locks.BuildingDetail? detail,
        List<PlayerInterface.HudLine> state)
    {
        if (DataOf(record) is not { } data || !Useable(data)) return;

        bool emptyMachine = (player.Production.Machine(focus.SiteId) ?? MachineOf(focus))?.StorageEmpty == true;
        if (!emptyMachine && detail is not { Empty: true }) return;

        string caption = $"[{Loc.Tr("Empty").ToUpper(System.Globalization.CultureInfo.CurrentCulture)}]";
        state.Add(new(caption, "", PlayerInterface.TitleColour));
    }

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
    /// Whether <paramref name="record"/> is a skeleton bed, whose panel has the machine lines and no Healing bonus.
    /// </summary>
    internal bool IsSkeletonBed(string? record) => DataOf(record) is { Function: SkeletonBedFunction };

    /// <summary>
    /// Adds the section 6 lines of a UseableStuff, StorageBuilding or LightBuilding, in a town or not (the panel's town guard passes for any placed
    /// building): Workers (slot 155), then Max Skill Level for a training dummy, else Efficiency and Max efficiency (slot 154), then a consumer's
    /// Power (slot 153). Left out: a bed's, and a consumer's with no power view (<c>ProductionSystem.LevelPowerOf</c>).
    /// </summary>
    internal void AddStatsLines(BuildingSelection.SelectedBuilding focus, List<PlayerInterface.HudLine> stats)
    {
        if (DataOf(focus.Record) is not { } data || !Useable(data)) return;

        var machine = MachineOf(focus);
        var power = player.Session.CurrentSnapshot.InspectedLevelPower is { } p && p.Instance == focus.Instance ? p : (LevelPowerView?)null;
        if (data.IsConsumer && machine is null && power is null) return;

        int operators = machine?.Operators ?? 0;
        stats.Add(ProductionWindows.WorkersLine(operators, data.PowerOperators));
        if (data.Function == Data.Gameplay.Construction.MachineData.TrainingFunction)
        {
            stats.Add(ProductionWindows.MaxSkillLine(data.OutputValue));
        }
        else
        {
            var efficiency = machine?.Efficiency ?? EfficiencyOf(focus, data, operators, power);
            ProductionWindows.AddEfficiencyLines(efficiency, 0, stats, efficiency.Active);
        }

        if (machine is null && power is { Demand: > 0 } drawn) stats.Add(ProductionWindows.ConsumerPowerLine(drawn.Granted, drawn.Demand));
    }

    /// <summary>
    /// Slot 154's inputs for furniture with no machine: no damage but a destroyed building's, the town frame's share of a consumer's draw (1 for
    /// none), the BUILDING's <c>output rate</c>, yield 1, and a light's slot 164 (indoors, else not daytime).
    /// </summary>
    EfficiencyView EfficiencyOf(BuildingSelection.SelectedBuilding focus, MachineData data, int operators, LevelPowerView? power)
    {
        float share = power is { Demand: > 0 } p ? p.Granted / p.Demand : 1;
        bool indoors = data.InteriorFurniture && BuildingSelection.HouseKeyOf(focus.Instance) != focus.Instance;
        bool active = !data.IsLight || indoors || !player.Session.Clock.IsDaytime;
        return new EfficiencyView(operators, data.PowerOperators, focus.Destroyed ? 0 : 1, share, data.IsConsumer, data.OutputRate, 1)
        {
            Active = active,
        };
    }

    /// <summary>
    /// Whether the original makes <paramref name="data"/>'s building a UseableStuff, StorageBuilding or LightBuilding whose section 6 is not a bed's
    /// Healing bonus: it has a functionality and no farm.
    /// </summary>
    static bool Useable(MachineData data) =>
        data.Functionality is not null && data.Farm is null && !OtherClasses.Contains(data.Function) && data.Function != BedFunction;

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
