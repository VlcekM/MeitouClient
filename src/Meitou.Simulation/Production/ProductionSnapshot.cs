using Meitou.Simulation.Items;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Production;

/// <summary>A recipe slot as the host shows it: the item, how many are held of how many, and the level (input) or progress (output) 0..1.</summary>
public readonly record struct MachineSlotView(string Item, string Name, int Count, int Capacity, float Fill, int Width = 1, int Height = 1);

/// <summary>A farm as the host shows it (docs/simulation.md "Production").</summary>
public readonly record struct FarmView(float Growth, float Death, int Harvested, int Plants, FarmStatus Status, float Fertility, float CropMatch, float Ripeness)
{
    /// <summary>Each plant's growth (a copy; -10 harvested): what its crop mesh is drawn at.</summary>
    public IReadOnlyList<float> PlantGrowth { get; init; } = [];
}

/// <summary>
/// The resource richness of the building the HUD shows (docs/game/ui-screens.md "Selected building"): a mine's or rock's <c>world resource mining</c> and its yield, for the stats
/// panel's first line. Display only.
/// </summary>
public readonly record struct RichnessView(string Instance, int Resource, float Yield)
{
    /// <summary>A rock nobody has mined yet: its efficiency lines (a mined rock's and a mine's are its machine's, <see cref="MachineSnapshot.Efficiency"/>).</summary>
    public EfficiencyView? Efficiency { get; init; }
    /// <summary>A rock's owner faction: -1 (the original's <c>nofac</c>) until a player character has worked it, then the player's faction; -1 for a player's mine.</summary>
    public int Owner { get; init; } = -1;
    /// <summary>The player town a worked rock joined; null for none.</summary>
    public string? TownName { get; init; }
}

/// <summary>
/// The inputs of a building's Workers and Efficiency lines (slots 155 / 154, docs/game/ui-screens.md "Selected building", Production building): its operators over the raw
/// <c>max operators</c>, health (progress / target), the power factor (granted / demand, 1 without demand), whether it draws power, and the rate (<c>output rate</c> x the research
/// production multiplier) and the yield (1 without a resource).
/// </summary>
public readonly record struct EfficiencyView(int Operators, int MaxOperators, float Health, float Power, bool Consumer, float Rate, float Yield)
{
    /// <summary>A farm out of water (production state 0): its Efficiency is x <see cref="Drought"/>, its <c>drought multiplier</c>, with a <c>No Water:</c> row.</summary>
    public bool Starved { get; init; }
    public float Drought { get; init; } = 1;
    /// <summary>A rain collector's <c>Rain strength:</c> (its slot 156 <c>FUN_14030b460</c>): the <c>wetness</c> of its region's weather, 0 under acid rain or indoors; null for other classes.</summary>
    public float? Rain { get; init; }
    /// <summary>Slot 164: false for a light building outdoors by day, which works at 0 (<see cref="Machine.Active"/>).</summary>
    public bool Active { get; init; } = true;
}

/// <summary>
/// Which Workers and Efficiency lines a building's stats panels draw (each class's slot 40, docs/game/ui-screens.md "Selected building", Production building): Workers, Efficiency and Max
/// efficiency in Stats 1; a farm its own Workers and, hydroponic only, one Efficiency line; a bed (BF_BED) <c>Healing bonus</c> instead; a training dummy (BF_TRAINING) Workers and
/// <c>Max Skill Level</c>; a turret Efficiency in Stats 1 and Workers and Efficiency in Stats 2; a furnace (its slot 40 hands every section but its own 0xf to the production one) the Production lines.
/// </summary>
public enum MachinePanel : byte { Production, Farm, Bed, Training, Turret }

/// <summary>A crafting bench as the host shows it: the queue (the first is being made), the repeat toggle and the finished items waiting in its output.</summary>
public sealed record CraftView(IReadOnlyList<(string Key, string Name, float Progress, float Hours)> Queue, bool Repeat, IReadOnlyList<InventoryItemView> Done);

/// <summary>Something a bench can craft, with its hours and inputs per item, and whether research and the bench level allow it now.</summary>
public sealed record CraftOptionView(string Key, string Name, CraftKind Kind, int Level, float Hours, IReadOnlyList<(string Name, float PerItem)> Inputs, bool Unlocked);

/// <summary>
/// What a bench's current smith would make of one queued craft (<c>FUN_1402bcf60</c>): <paramref name="Quality"/> the armour or crossbow quality (5 to 95; -1 for the other kinds); for a
/// weapon <paramref name="Model"/> the predicted model's name (null for the other kinds), <paramref name="ModelChanged"/> when it differs from the queued one and
/// <paramref name="MaterialCost"/> the weapon's <c>material cost</c>; <paramref name="Critical"/> the shown critical chance in percent (-1: no row).
/// </summary>
public readonly record struct CraftPrediction(int Quality, string? Model, bool ModelChanged, int MaterialCost, int Critical);

/// <summary>A crafting bench's current smith (<see cref="Machine.Smith"/>): his name, his effective level of the bench's stat, the stat multiplier's penalty in percent (0: none) and the prediction per queued craft.</summary>
public sealed record CraftSmithView(string Name, int Skill, int Penalty, IReadOnlyList<CraftPrediction> Entries);

/// <summary>A machine's power as the host shows it: a consumer's demand and grant, a generator's output and load, a battery's flow and store (docs/game/buildings-production.md "Power").</summary>
public enum PowerKind : byte { Consumer, Generator, Battery }

public readonly record struct PowerView(PowerKind Kind, float Demand, float Granted, float Usage, float Charge, float Store, bool Fueled, bool PowerOn = true, bool GetsBattery = true)
{
    /// <summary>A battery's town grid: whether it has one and whether its batteries charged or drained this tick (<see cref="Machine.GridCharging"/>).</summary>
    public bool OnGrid { get; init; }
    public bool GridCharging { get; init; }
    public bool GridDraining { get; init; }
}

/// <summary>What the host tells a machine's sound emitter (docs/audio.md "Point emitters"): the efficiency and whether it is powered, as the exe's <c>FUN_140258c20</c> takes them.</summary>
public readonly record struct MachineSound(float Efficiency, bool Powered);

/// <summary>
/// A wind generator as its panel shows it (docs/game/buildings-production.md "Wind generators"): its blades' wind share, the wind speed at the site (units/s), the blades' operating range
/// (null without a ROTATION_WIND_SPEED part), its rated output (data + research) and whether it is switched on.
/// </summary>
public readonly record struct WindView(float Share, float Speed, (float Min, float Max)? Range, float Rated, bool PowerOn);

/// <summary>An item furnace's panel (<c>FurnaceBuilding::getGUIData</c> section 0xf): whether it burns (<see cref="Machine.Burning"/>) and its grid's metal (<c>Total Metals:</c>).</summary>
public readonly record struct FurnaceView(bool Burning, float Metal);

/// <summary>A machine of a complete player building after a tick (docs/simulation.md "Production"); <see cref="Site"/> is the building's instance id.</summary>
public sealed record MachineSnapshot(string Site, string Record, IReadOnlyList<MachineSlotView> Inputs, MachineSlotView? Output, InventoryView? Storage, FarmView? Farm, byte Status, int Operators, int MaxOperators)
{
    /// <summary>A container of a building's interior, open (<see cref="PlayerState.OpenContainer"/>): <see cref="Site"/> is its fixture id; no research, recipe or power.</summary>
    public bool IsContainer { get; init; }
    /// <summary>The research bench's tech level (0 for other machines).</summary>
    public int TechLevel { get; init; }
    /// <summary>Characters can work it (a well, farm or research bench).</summary>
    public bool Operable { get; init; }
    /// <summary>An automatic machine: works unattended; its menu order is Auto-Haul (152) instead of Use.</summary>
    public bool IsAutomatic { get; init; }
    /// <summary><see cref="Machine.HasEmptyInputs"/>: with status 0 the panel reads Out of resource.</summary>
    public bool InputsEmpty { get; init; }
    /// <summary>A storage (a "Hauling to" job can name it).</summary>
    public bool IsStorage { get; init; }
    /// <summary>Its BUILDING_FUNCTIONALITY <c>function</c> (-1: none; 12 a turret, 14 a furnace).</summary>
    public int Function { get; init; } = -1;
    /// <summary>The one item a storage is limited to (its heading in the window), else null.</summary>
    public string? StorageLimitName { get; init; }
    /// <summary>A limited storage's <c>Max Capacity</c>: the building's <c>stackable bonus mult</c> (0 when it has none).</summary>
    public int StorageCapacity { get; init; }
    /// <summary>The crafting bench state (null for other machines).</summary>
    public CraftView? Craft { get; init; }
    /// <summary>Its power (null for a building outside the grid).</summary>
    public PowerView? Power { get; init; }
    /// <summary>What its sound emitter is told each frame (docs/audio.md "Point emitters").</summary>
    public MachineSound Sound { get; init; }
    /// <summary>What its ROTATION_OUTPUT parts turn by (<see cref="Machine.Rotation"/>).</summary>
    public float Rotation { get; init; }
    /// <summary>A wind generator's wind (null for other machines).</summary>
    public WindView? Wind { get; init; }
    /// <summary>Its Workers and Efficiency lines' inputs (slots 155 / 154); which of them its panels draw is <see cref="Panel"/>.</summary>
    public EfficiencyView? Efficiency { get; init; }
    /// <summary>Which Workers and Efficiency lines its class draws.</summary>
    public MachinePanel Panel { get; init; }
    /// <summary>The exe's output value <c>+0x1a4</c> (<see cref="MachineData.OutputValue"/>): a bed's Healing bonus, a training dummy's skill cap.</summary>
    public float OutputValue { get; init; }
    /// <summary>The research multiplier of its building (<c>FUN_14082ec70</c>; the player's buildings only): <see cref="OutputValue"/> x this is Building slot 120, a turret's "Default damage".</summary>
    public float Research { get; init; } = 1;
    /// <summary><see cref="Machine.Broken"/> (UseableStuff <c>+0x3b6</c>): the header reads Broken and a finished one offers Repair (docs/game/buildings-production.md "Broken machines").</summary>
    public bool Broken { get; init; }
    /// <summary>A hydroponic farm (its FARM_DATA's <c>fertility effect</c> is 0, the exe's FarmBuilding <c>+0x558</c>): the only farms with an Efficiency line.</summary>
    public bool Hydroponic { get; init; }
    /// <summary>An item furnace's state (null for other machines).</summary>
    public FurnaceView? Furnace { get; init; }

    /// <summary>The panel of a machine's class: farms; BF_BED 6; BF_TRAINING 7; BF_TURRET 12; every other class (furnaces too) Production.</summary>
    static MachinePanel PanelOf(MachineData d) => d.Farm is not null ? MachinePanel.Farm : d.Function switch
    {
        6 => MachinePanel.Bed, MachineData.TrainingFunction => MachinePanel.Training, 12 => MachinePanel.Turret, _ => MachinePanel.Production,
    };

    internal static MachineSnapshot Of(Machine m, ItemFactory items)
    {
        string Name(string record) => items.Info(record)?.Name ?? record;
        var inputs = m.Inputs.Select(i => new MachineSlotView(i.Item, Name(i.Item), i.Count, i.Recipe.Capacity, Math.Clamp(i.Level, 0, 1), items.Info(i.Item)?.Width ?? 1, items.Info(i.Item)?.Height ?? 1)).ToArray();
        MachineSlotView? output = m.Output is { } o ? new MachineSlotView(o.Recipe.Item, Name(o.Recipe.Item), o.Count, o.Recipe.Capacity, Math.Clamp(o.Progress, 0, 1), items.Info(o.Recipe.Item)?.Width ?? 1, items.Info(o.Recipe.Item)?.Height ?? 1) : null;
        InventoryView? storage = m.Storage is { } s ? new InventoryView(s.Width, s.Height, 0, 0,
            Array.AsReadOnly(s.Items.Select((item, index) => new InventoryItemView(index, item.Record, item.Name, "main", item.X, item.Y, item.Width, item.Height, item.Quantity, item.Weight, item.Quality)
                { Material = item.MaterialId, Colour = item.ColourId, Charge = item.ChargeFraction }).ToArray())) : null;
        FarmView? farm = m.Farm is { } f && m.Data.Farm is { } fd
            ? new FarmView(f.Growth, f.Death, f.Harvested, f.Plants.Length, f.Status, m.Yield, m.CropMatch, ProductionSystem.Ripeness(f)) { PlantGrowth = [.. f.Plants] } : null;
        // The classes that update their emitter every frame tell it the efficiency and the power; the others powered it once, when built, with no operators (efficiency 0).
        var sound = m.Data.PowersEmitterEachFrame ? new MachineSound(m.Efficiency, m.HasPower && m.Efficiency > 0) : new MachineSound(0, true);
        return new MachineSnapshot(m.Site, m.Data.Id, inputs, output, storage, farm, m.Status, m.Operators, m.Data.MaxOperators) { Sound = sound, Rotation = m.Rotation, Wind = m.Data.IsWind ? new WindView(m.Wind, m.WindSpeed, m.WindRotor, m.WindRated, m.PowerOn) : null, TechLevel = m.Data.IsResearch ? m.Data.TechLevel : 0, Operable = m.Data.Operable, IsAutomatic = m.Data.IsAutomatic, InputsEmpty = m.HasEmptyInputs, IsStorage = m.Data.IsStorage, Function = m.Data.Function, StorageLimitName = m.Data.StorageLimit is [var only] ? Name(only) : null, StorageCapacity = m.Data.StorageLimit is [_] ? (int)m.Data.StackBonusMult : 0, Craft = Crafting(m, items),
            Power = m.Data.IsConsumer || m.Data.IsGenerator || m.Data.IsBattery ? new PowerView(m.Data.IsBattery ? PowerKind.Battery : m.Data.IsGenerator ? PowerKind.Generator : PowerKind.Consumer, m.Demand, m.Granted, m.Usage, m.Charge, m.Store, m.Inputs.Length == 0 || Array.Exists(m.Inputs, i => i.Level > 0 || i.Count > 0), m.PowerOn, m.GetsBattery) { OnGrid = m.OnGrid, GridCharging = m.GridCharging, GridDraining = m.GridDraining } : null,
            Panel = PanelOf(m.Data), OutputValue = m.Data.OutputValue, Research = m.ProductionMult, Broken = m.Broken, Hydroponic = m.Data.Farm is { Indoor: true },
            Furnace = m.Data.IsFurnace ? new FurnaceView(m.Burning, m.Metal) : null,
            Efficiency = new(m.Operators, m.Data.PowerOperators, m.Health, m.Power, m.Data.IsConsumer, m.Data.OutputRate * m.ProductionMult, m.Yield)
            {
                Starved = m.Farm is { Status: FarmStatus.NoWater }, Drought = m.Data.Farm?.DroughtMultiplier ?? 1,
                Rain = m.Data.Function == MachineData.RainCollectorFunction ? m.Rain : null, Active = m.Active,
            } };
    }

    static CraftView? Crafting(Machine m, ItemFactory items) => m.Craft is { } c
        ? new CraftView([.. c.Queue.Select(j => (j.Option.Key, j.Recipe.Name, Math.Clamp(j.Progress, 0, 1), j.Recipe.Hours))], c.Repeat,
            [.. c.Done.Select((item, index) => new InventoryItemView(index, item.Record, item.Name, "out", 0, 0, item.Width, item.Height, item.Quantity, item.Weight, item.Quality) { Material = item.MaterialId, Colour = item.ColourId })])
        : null;
}

/// <summary>A research the player may queue, with its cost and whether the benches can pay it and the bench level allows it.</summary>
public readonly record struct ResearchOption(string Id, string Name, string Category, int Level, float Hours, bool Affordable, bool BenchTooLow, float Progress)
{
    public string Description { get; init; } = "";
    /// <summary>The items paid at queue time (empty once paid), each with whether the research benches hold enough of it.</summary>
    public IReadOnlyList<(string Name, int Count, bool Held)> Cost { get; init; } = [];
    /// <summary>Its cost was paid already, so queuing it is free.</summary>
    public bool Paid { get; init; }
    /// <summary>The RESEARCH it requires by name, each finished or in the queue (the description's colours).</summary>
    public IReadOnlyList<(string Name, bool Finished, bool Queued)> Requirements { get; init; } = [];
    /// <summary>The records it unlocks by name, with their <see cref="Meitou.Data.Gameplay.Construction.ResearchData.UnlockFields"/> field.</summary>
    public IReadOnlyList<(string Field, string Name)> Unlocks { get; init; } = [];
}

/// <summary>The research state as the host shows it (docs/simulation.md "Production", Research).</summary>
public sealed record ResearchSnapshot(int TechLevel, int BenchLevel, IReadOnlyList<(string Id, string Name, float Progress)> Queue, float Rate, IReadOnlyList<ResearchOption> Available)
{
    /// <summary>How many researches have been finished (counting repeats): the host plays the notification when it grows.</summary>
    public int Finished { get; init; }
    /// <summary>The researches finished at least once and those whose requirements are met strictly, as a blueprint learns them (a blueprint's tooltip state, <c>FUN_1407ab430</c>).</summary>
    public IReadOnlySet<string> Known { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> RequirementsMet { get; init; } = new HashSet<string>();
    public static ResearchSnapshot Empty { get; } = new(1, 0, [], 0, []);
}

/// <summary>
/// A town grid's totals after a tick (docs/game/buildings-production.md "Power"): the generators' output and the consumers' demand (the original's Town +0x474, +0x47c), what charged the
/// batteries and what was drawn from them (+0x488, +0x484), and the batteries' charge and capacity (+0x48c, +0x490).
/// </summary>
public readonly record struct TownPower(float Generated, float Demand, float Charging, float Draining, float Charge, float Capacity);

/// <summary>A player outpost as the town panel shows it (docs/game/ui-screens.md "Town panel").</summary>
public sealed record OutpostSnapshot(int Number, string Name, System.Numerics.Vector3 Position, float Radius, int Owner, bool Public, TownPower Power);
