using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>An input of a production recipe (a <c>consumes</c> entry): the ITEM, how many items its section holds (v0) and the level it loses per unit of work (v1 / 100).</summary>
public readonly record struct RecipeInput(string Item, int Capacity, float Rate);

/// <summary>The output of a production recipe (the first <c>produces</c> entry): the ITEM and how many items the output holds (v0).</summary>
public readonly record struct RecipeOutput(string Item, int Capacity);

/// <summary>
/// What a BUILDING does once built (docs/game/buildings-production.md "Production", "Farming", "Research"): its functionality's work and the recipe, the farm data and the storage.
/// Null from <see cref="From"/> for a building that neither works nor stores.
/// </summary>
public sealed record MachineData
{
    /// <summary>The game day at speed 1 in seconds, G (<b>Verified</b>, <c>FUN_14066cd50</c>): 24 x 1200/11.</summary>
    public const float DaySeconds = 24 * 1200f / 11f;

    public const int MineFunction = 1, ResourceStorageFunction = 2, GeneralStorageFunction = 13, ResearchFunction = 3, RefineryFunction = 4, GeneratorFunction = 5, ItemFurnaceFunction = 14, BatteryFunction = 23, RainCollectorFunction = 26, NaturalMineFunction = 27, TrainingFunction = 7;
    /// <summary><c>MiningResource</c> WATER and GROUND (<b>Verified</b>, editor enum).</summary>
    public const int WaterResource = 5, GroundResource = 6;

    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>The functionality's <c>function</c> (BuildingFunction); -1 without a functionality.</summary>
    public int Function { get; init; } = -1;
    /// <summary>The functionality's <c>flags</c>; a chair's facing rule.</summary>
    public int FunctionFlags { get; init; }
    /// <summary><c>stat used</c> (a <c>StatsEnumerated</c> value); 0 for none.</summary>
    public int Stat { get; init; }
    /// <summary>How many characters may work it at once: the BUILDING's <c>max operators</c>, else the functionality's (at least 1).</summary>
    public int MaxOperators { get; init; } = 1;
    /// <summary>
    /// The work per second of one operator at skill factor 1, before P: BUILDING <c>output rate</c> x functionality <c>production mult</c>, / (2G) when <c>output per day</c>
    /// (not for research), / the raw <c>max operators</c> when above 1, so a full crew makes one rate (<b>Verified</b>, loaders <c>FUN_14029e460</c>, <c>FUN_14029e7b0</c>).
    /// </summary>
    public float Rate { get; init; }
    /// <summary><c>use range</c>: how near an operator works.</summary>
    public float UseRange { get; init; } = 20;
    /// <summary><c>world resource mining</c> (MiningResource); 0 none.</summary>
    public int Resource { get; init; }
    /// <summary>The functionality's <c>tech level</c> (a research bench's level).</summary>
    public int TechLevel { get; init; }
    /// <summary>The <c>animation</c> clip operators play (default <c>stonemine</c>, <b>Observed</b>: <c>Task_OperateMachine</c>).</summary>
    public string Animation { get; init; } = "stonemine";
    /// <summary>The <c>special tool</c> ITEM operators hold in the <c>hands</c> point on the job (a mine's Pickaxe, a farm's Hoe); null for none.</summary>
    public string? Tool { get; init; }
    /// <summary>The functionality's <c>hunger rate</c>: the operator's hunger factor while it works standing still (mines and sword training 1.2; 1 by default).</summary>
    public float HungerRate { get; init; } = 1;
    /// <summary>The functionality's <c>string</c>: the text that floats over the machine when a body is fed into it ("Incinerating"; docs/game/buildings-production.md).</summary>
    public string Label { get; init; } = "";
    public IReadOnlyList<RecipeInput> Inputs { get; init; } = [];
    public RecipeOutput? Output { get; init; }
    public FarmData? Farm { get; init; }
    /// <summary><c>has inventory</c>: a storage grid of <see cref="StorageWidth"/> x <see cref="StorageHeight"/>.</summary>
    public bool HasInventory { get; init; }
    public int StorageWidth { get; init; } = 18;
    public int StorageHeight { get; init; } = 18;
    /// <summary><c>limit inventory</c>: the only ITEMs the storage takes (empty: any).</summary>
    public IReadOnlyList<string> StorageLimit { get; init; } = [];
    /// <summary><c>itemtype limit</c>: the record type the storage takes (fcs.def default ITEM).</summary>
    public FcsRecordType ItemTypeLimit { get; init; } = FcsRecordType.ITEM;
    /// <summary><c>stackable bonus mult</c>: stacks inside are this many times larger (fcs.def).</summary>
    public float StackBonusMult { get; init; } = 1;
    /// <summary>A storage building (function 2 or 13 with an inventory) operators haul to and from (docs/game/buildings-production.md "Hauling").</summary>
    public bool IsStorage => HasInventory && Function is ResourceStorageFunction or GeneralStorageFunction;
    /// <summary>Whether the storage takes an item of this record and type.</summary>
    public bool Accepts(string item, FcsRecordType type) => type == ItemTypeLimit && (StorageLimit.Count == 0 || StorageLimit.Contains(item));
    /// <summary><c>power output</c>: a generator's output, a battery's discharge and charge rate, a consumer's draw when negative (docs/game/buildings-production.md "Power").</summary>
    public float PowerOutput { get; init; }
    /// <summary><c>power capacity</c>: a battery's store, in seconds at its rate (fcs.def).</summary>
    public float PowerCapacity { get; init; }
    /// <summary>The raw <c>max operators</c> (building, else functionality; 0 none): a consumer's draw is x operators / this when it is above 0 (<b>Observed</b>, <c>vfunc_163</c>).</summary>
    public int PowerOperators { get; init; }
    /// <summary>
    /// An automatic machine: the raw <c>max operators</c> is 0 (<b>Verified</b>, <c>FUN_1402a3750</c>). It works with nobody at it (<c>ProductionBuilding::vfunc_28</c>), and its menu
    /// order is 152 "Operating automatic machine" (Auto-Haul) instead of 87.
    /// </summary>
    public bool IsAutomatic { get; init; }
    /// <summary>
    /// Works with nobody at it (the exe's "has operators" flag +0x3a9 clear): the raw <c>max operators</c> is 0, or the BUILDING's and the functionality's are both below 0
    /// (<b>Verified</b>, <c>FUN_1402a3750</c>, <c>FUN_14029e460</c>; <c>ProductionBuilding::vfunc_28</c> <c>FUN_14029ad90</c>). The menu order follows <see cref="IsAutomatic"/> alone.
    /// No base-game pair is negative on both (<b>Verified</b>, probe), so only mods.
    /// </summary>
    public bool Unattended { get; init; }
    /// <summary>A wind generator: function 5 without <c>consumes</c> (<b>Observed</b>, the factory <c>FUN_14057cc70</c>).</summary>
    public bool IsWind { get; init; }
    /// <summary>
    /// The <c>wind speed rotation min/max</c> of the building's first ROTATION_WIND_SPEED part (depth first through <c>parts</c>, collision-less as the layout's rotors; max raised to min): a wind
    /// generator's output share is <see cref="Meitou.Data.World.RotorSpec.WindShare"/> of the wind there (<c>PhysicsCollection::getWindRotationPower</c>). Null without such a part (the share is 0).
    /// </summary>
    public (float Min, float Max)? WindRotor { get; init; }
    /// <summary>The BUILDING's raw <c>output rate</c> (the exe's <c>+0x1a8</c>), a factor of the efficiency its sound emitter is told (docs/audio.md "Point emitters"); 1 without one.</summary>
    public float OutputRate { get; init; } = 1;
    /// <summary>
    /// The exe's output value <c>+0x1a4</c> (<b>Verified</b>, loader <c>FUN_14029e460</c>): <c>output rate</c> x the functionality's <c>production mult</c>, / the raw <c>max operators</c>
    /// when above 1. A bed's <c>Healing bonus</c>, a training dummy's skill cap, a turret's damage.
    /// </summary>
    public float OutputValue { get; init; } = 1;
    /// <summary>The BUILDING's <c>is interior furniture</c> (the exe's <c>+0xf8</c>): standing in a building it is indoors (Building slot 59 <c>isIndoors</c>).</summary>
    public bool InteriorFurniture { get; init; }
    /// <summary>A light building (BF_LIGHT 15): outdoors it works only while it is not daytime (LightBuilding slot 164).</summary>
    public bool IsLight => Function == Meitou.Data.World.WorldLights.LightFunction;
    public bool IsBattery => PowerCapacity > 0;
    public bool IsGenerator => PowerOutput > 0 && !IsBattery;
    public bool IsConsumer => PowerOutput < 0;

    public bool IsResearch => Function == ResearchFunction;
    /// <summary>A training dummy (function 7, BF_TRAINING): its user trains <see cref="Stat"/> up to the cap <see cref="OutputRate"/> (the "Max Skill Level" of its panel is one less, docs/game/ai-tasks.md "Finders of the jobs").</summary>
    public bool IsTraining => Function == TrainingFunction && Stat > 0;
    /// <summary>A mine, well or refinery: inputs (if any) into one output (functions 1, 4 and 27, <b>Verified</b> enum; the stone and ore processors are 4).</summary>
    public bool IsProducer => Function is MineFunction or RefineryFunction or NaturalMineFunction or RainCollectorFunction && Farm is null && Output is not null;
    /// <summary>An item furnace (function 14, <c>FurnaceBuilding</c>): burns the items of its input grid into its output (docs/game/buildings-production.md "Item furnace").</summary>
    public bool IsFurnace => Function == ItemFurnaceFunction && Output is not null;
    /// <summary>A crafting bench (function 10): its crafts lists.</summary>
    public CraftBench? Craft { get; init; }
    public bool IsCrafting => Craft is not null;
    /// <summary>A building characters can operate.</summary>
    public bool Operable => IsResearch || Farm is not null || IsProducer || IsCrafting;
    /// <summary>
    /// Whether the exe's class updates its sound emitter every frame (<c>ProductionBuilding::vfunc_28</c>: mines, wells, refineries, generators, batteries, furnaces, rain collectors); the
    /// others (benches, research, farms, anything else) power it once when built (<c>FarmBuilding::vfunc_72</c>). <b>Observed</b>: the class follows the function (the factory <c>FUN_14057cc70</c>).
    /// </summary>
    public bool PowersEmitterEachFrame => !IsCrafting && !IsResearch
        && (Function == MineFunction ? Farm is null : Function is RefineryFunction or GeneratorFunction or ItemFurnaceFunction or BatteryFunction or RainCollectorFunction or NaturalMineFunction);

    /// <summary>
    /// The cells of a building's own inventory. A building whose <c>limit inventory</c> names exactly one item is as big as that item's <c>inventory footprint</c> (the original's building
    /// loader, <c>cmp count, 1</c>; two or more items, or none, take <c>storage size</c>; Observed), so a storage for steel bars is 6 x 1.
    /// </summary>
    public static (int Width, int Height) StorageGrid(GameDatabase db, GameRecord building)
    {
        if (building.GetReferences("limit inventory") is [var only] && db.Find(only.TargetStringId) is { } item)
            return (Math.Max(1, item.GetInt("inventory footprint width", 1)), Math.Max(1, item.GetInt("inventory footprint height", 1)));
        return (Math.Max(1, building.GetInt("storage size width", 18)), Math.Max(1, building.GetInt("storage size height", 18)));
    }

    static (float Min, float Max)? WindRotorOf(GameDatabase db, GameRecord holder, int depth)
    {
        if (depth > 8) return null;
        foreach (var r in holder.GetReferences("parts"))
        {
            if (db.Find(r.TargetStringId) is not { Type: FcsRecordType.BUILDING_PART } part) continue;
            if (part.GetInt("rotation function") == Meitou.Data.World.RotorSpec.WindSpeed && part.GetPath("xml collision").Length == 0)
            {
                float min = part.GetFloat("wind speed rotation min");
                return (min, MathF.Max(min, part.GetFloat("wind speed rotation max")));
            }
            if (WindRotorOf(db, part, depth + 1) is { } below) return below;
        }
        return null;
    }

    public static MachineData? From(GameDatabase db, GameRecord building)
    {
        var function = building.GetReferences("functionality") is [var link, ..] ? db.Find(link.TargetStringId) : null;
        bool stores = building.GetBool("has inventory");
        if (function is null && !stores) return null;
        int fn = function?.GetInt("function", -1) ?? -1;
        var farm = building.GetReferences("farm data") is [var farmLink, ..] && db.Find(farmLink.TargetStringId) is { Type: FcsRecordType.FARM_DATA } fd ? FarmData.From(db, fd) : null;
        float rate = building.GetFloat("output rate", 1);
        if (function is not null)
        {
            rate *= function.GetFloat("production mult", 1);
            if (fn != ResearchFunction && function.GetBool("output per day")) rate /= 2 * DaySeconds;
        }
        var inputs = new List<RecipeInput>();
        RecipeOutput? output = null;
        if (function is not null)
        {
            foreach (var c in function.GetReferences("consumes"))
                if (db.Find(c.TargetStringId) is not null) inputs.Add(new RecipeInput(c.TargetStringId, c.Values.Value0, farm is not null ? 1 : c.Values.Value1 * 0.01f));
            if (function.GetReferences("produces") is [var p, ..] && db.Find(p.TargetStringId) is not null) output = new RecipeOutput(p.TargetStringId, p.Values.Value0);
        }
        int maxOperators = building.GetInt("max operators", -1);
        // The exe's +0x3ac (Verified, FUN_1402a3750): the BUILDING's value unless below 0, then the functionality's.
        int rawOperators = maxOperators >= 0 ? maxOperators : function?.GetInt("max operators", -1) ?? -1;
        bool wind = fn == GeneratorFunction && inputs.Count == 0;
        if (maxOperators <= 0) maxOperators = Math.Max(1, function?.GetInt("max operators", 1) ?? 1);
        // Every machine's rate is shared by its operators: / the raw max operators when above 1, so a machine whose BUILDING says 0 is never divided (Verified, FUN_14029e460,
        // FUN_14029e7b0, FUN_1402b9aa0; each operator then adds the divided rate).
        if (rawOperators > 1) rate /= rawOperators;
        var craft = function is not null ? CraftBench.From(db, function) : null;
        // A bench gets a section per ingredient of its widest recipe when it lists fewer (engine choice: the original's section count for such benches is Unknown).
        if (craft is not null && !craft.OverridesIngredients)
        {
            int capacity = inputs.Count > 0 ? inputs[0].Capacity : 5;
            foreach (var option in craft.Options.Where(o => o.Kind != CraftKind.Armour))
                if (db.Find(option.Item)?.GetReferences("ingredients") is { } list)
                    for (int k = inputs.Count; k < list.Count; k++) inputs.Add(new RecipeInput(list[k].TargetStringId, capacity, 0));
        }
        var limits = building.GetReferences("limit inventory");
        var (storageWidth, storageHeight) = StorageGrid(db, building);
        return new MachineData
        {
            Id = building.StringId,
            Name = building.Name,
            Function = fn,
            FunctionFlags = function?.GetInt("flags") ?? 0,
            Stat = function?.GetInt("stat used") ?? 0,
            MaxOperators = maxOperators,
            Rate = rate,
            UseRange = function?.GetFloat("use range", 20) ?? 20,
            Resource = function?.GetInt("world resource mining") ?? 0,
            TechLevel = function?.GetInt("tech level") ?? 0,
            Animation = function?.GetReferences("animation") is [var anim, ..] && db.Find(anim.TargetStringId) is { } clip ? clip.Name : "stonemine",
            Tool = function?.GetReferences("special tool") is [var tool, ..] ? tool.TargetStringId : null,
            HungerRate = function?.GetFloat("hunger rate", 1) ?? 1,
            Label = function?.GetString("string") ?? "",
            Inputs = inputs,
            Output = output,
            Farm = farm,
            Craft = craft,
            HasInventory = stores,
            StorageWidth = storageWidth,
            StorageHeight = storageHeight,
            StorageLimit = [.. limits.Select(r => r.TargetStringId)],
            ItemTypeLimit = (FcsRecordType)building.GetInt("itemtype limit", (int)FcsRecordType.ITEM),
            StackBonusMult = building.GetFloat("stackable bonus mult", 1),
            PowerOutput = building.GetInt("power output"),
            PowerCapacity = building.GetInt("power capacity"),
            PowerOperators = Math.Max(0, rawOperators),
            IsAutomatic = rawOperators == 0,
            Unattended = rawOperators == 0 || function is not null && building.GetInt("max operators", -1) < 0 && function.GetInt("max operators", -1) < 0,
            IsWind = wind,
            WindRotor = wind ? WindRotorOf(db, building, 0) : null,
            OutputRate = building.GetFloat("output rate", 1),
            OutputValue = building.GetFloat("output rate", 1) * (function?.GetFloat("production mult", 1) ?? 1) / (rawOperators > 1 ? rawOperators : 1),
            InteriorFurniture = building.GetBool("is interior furniture"),
        };
    }
}

/// <summary>A FARM_DATA record (docs/game/buildings-production.md "Farming"). Times in game hours, rates per game day as in the data.</summary>
public sealed record FarmData
{
    public required string Id { get; init; }
    /// <summary><c>amount</c>: the plants asked for; the farm has <see cref="FarmLayout.Count"/> (a grid can hold fewer).</summary>
    public int Plants { get; init; } = 1;
    /// <summary>The plant layout (docs/game/buildings-production.md "Farm plants"): <c>layout</c> 0 grid, 1 ring, 2 random, with <c>spacing</c>, <c>jitter</c>, <c>aspect</c>.</summary>
    public int Layout { get; init; }
    public float Spacing { get; init; } = 10;
    public float Jitter { get; init; }
    public float Aspect { get; init; } = 1;
    /// <summary><c>inside</c>: the plants stand at the building's height, not on the ground.</summary>
    public bool Inside { get; init; }
    /// <summary><c>death colour</c> (ARGB): the tint of a dying crop.</summary>
    public uint DeathColour { get; init; } = 0xFFFFFFFF;
    /// <summary>The <c>plants</c> list: the FARM_PART meshes the plants are made of.</summary>
    public IReadOnlyList<FarmPart> Parts { get; init; } = [];
    public float GrowthTime { get; init; } = 1;
    public float HarvestTime { get; init; } = 1;
    public float DeathTime { get; init; } = 1;
    public float DroughtDeathTime { get; init; }
    public float DroughtMultiplier { get; init; }
    public float DeathThreshold { get; init; } = 0.4f;
    public float ConsumptionRate { get; init; }
    public float HarvestRate { get; init; }
    public float ClearRate { get; init; }
    public float OutputPerPlant { get; init; } = 1;
    public float FertilityEffect { get; init; }
    public float MinimumFertility { get; init; }
    /// <summary>Crop weights for the biome's <c>arid</c>, <c>green</c> and <c>swamp</c> coverages.</summary>
    public float Arid { get; init; }
    public float Green { get; init; }
    public float Swamp { get; init; }
    /// <summary>
    /// An indoor farm (<c>fertility effect</c> 0): the ground counts only at or below <c>minimum fertility</c>, the crop match is 1; the biome's water usage still applies
    /// (<b>Verified</b>, ctor <c>FUN_1400e8420</c>, <c>FUN_1400e4320</c>).
    /// </summary>
    public bool Indoor => FertilityEffect == 0;

    public static FarmData From(GameDatabase db, GameRecord r) => new()
    {
        Id = r.StringId,
        Plants = Math.Max(1, r.GetInt("amount", 1)),
        Layout = r.GetInt("layout"),
        Spacing = r.GetFloat("spacing", 10),
        Jitter = r.GetFloat("jitter"),
        Aspect = r.GetFloat("aspect", 1),
        Inside = r.GetBool("inside"),
        DeathColour = unchecked((uint)r.GetInt("death colour", -1)),
        Parts = [.. r.GetReferences("plants").Select(p => db.Find(p.TargetStringId) is { Type: FcsRecordType.FARM_PART } part ? FarmPart.From(db, part, p.Values.Value0, p.Values.Value1) : null).OfType<FarmPart>()],
        GrowthTime = r.GetFloat("growth time", 1),
        HarvestTime = r.GetFloat("harvest time", 1) is > 0 and var h ? h : 1e-6f,
        DeathTime = r.GetFloat("death time", 1),
        DroughtDeathTime = r.GetFloat("drought death time"),
        DroughtMultiplier = r.GetFloat("drought multiplier"),
        DeathThreshold = r.GetFloat("death threshold", 0.4f),
        ConsumptionRate = r.GetFloat("consumption rate"),
        HarvestRate = r.GetFloat("harvest rate"),
        ClearRate = r.GetFloat("clear rate"),
        OutputPerPlant = r.GetFloat("output per plant", 1),
        FertilityEffect = r.GetFloat("fertility effect"),
        MinimumFertility = r.GetFloat("minimum fertility"),
        Arid = r.GetFloat("arid"),
        Green = r.GetFloat("green"),
        Swamp = r.GetFloat("swamp"),
    };

    /// <summary>The fertility factor F = <c>1 - (1 - f) x fertility effect</c>, 0 when f is at or below <c>minimum fertility</c> (<b>Observed</b>, <c>FUN_1400e4320</c>).</summary>
    public float FertilityFactor(float fertility) => fertility <= MinimumFertility ? 0 : 1 - (1 - fertility) * FertilityEffect;
}

/// <summary>
/// The resources of the ground at a position (docs/game/buildings-production.md "Resources"): the BIOME_GROUP of the areas map names an ENVIRONMENT_RESOURCES record whose
/// <c>water</c> / <c>farming</c> fields give the WATER and GROUND values by height, and whose coverages weigh the crops.
/// </summary>
public sealed class ResourceField
{
    readonly BiomeMap? map;
    readonly GameDatabase db;
    readonly IResourceNodes? nodes;
    readonly Dictionary<int, GameRecord?> resourcesOf = [];

    /// <summary>How far a mine finds a rock of its resource (<b>Observed</b>, <c>uses_resources</c>: 1500², 2.25e6).</summary>
    public const float NodeRange = 1500;
    public const int IronResource = 1, StoneResource = 2, CopperResource = 3, CarbonResource = 4;

    public ResourceField(GameDatabase db, BiomeMap? map, IResourceNodes? nodes = null)
    {
        this.db = db;
        this.map = map;
        this.nodes = nodes;
    }

    /// <summary>Whether there is an areas map (without one every value is 0).</summary>
    public bool HasMap => map is not null;
    /// <summary>Whether rocks are known (without them IRON, COPPER and CARBON are 0).</summary>
    public bool HasNodes => nodes is not null;

    /// <summary>The field prefix of a MiningResource (<c>read_stone_n3</c>).</summary>
    static string Prefix(int resource) => resource switch
    {
        IronResource => "iron", StoneResource => "stone", CopperResource => "copper", CarbonResource => "carbon", MachineData.WaterResource => "water", MachineData.GroundResource => "farming", _ => "",
    };

    /// <summary>
    /// A player building's <c>world resource mining</c> yield at a position (<b>Observed</b>, <c>uses_world_resource_mining</c> -> <c>uses_resources</c>): WATER and GROUND as
    /// <see cref="Value"/>; STONE the coverage noise (<c>Foliage_NoiseHashC</c>: scale <c>stone noise zoom</c> x 1000, cutoff <c>stone noise cutoff</c>, boost 3 and cap 1,
    /// <b>Verified</b> bytes) x <c>stone mult</c>; IRON, COPPER and CARBON the nearest rock of the resource within <see cref="NodeRange"/>: its own yield x (1 - falloff), falloff
    /// = (d² - r²) / (1500² - r²) clamped, r² at least 625. Nothing (0) without a resources record or a rock; the original's 0.5 for nothing applies to buildings not the player's.
    /// </summary>
    public float Yield(int resource, System.Numerics.Vector3 at)
    {
        if (resource is MachineData.WaterResource or MachineData.GroundResource or StoneResource) return FieldYield(resource, at, at);
        if (Resources(at.X, at.Z) is not { } r) return 0;
        if (nodes is null || Prefix(resource).Length == 0) return 0;
        ResourceNode? best = null;
        float bestD = float.MaxValue;
        foreach (var node in nodes.Near(resource, at.X, at.Z, NodeRange))
        {
            float d = System.Numerics.Vector3.DistanceSquared(node.Position, at);
            if (d < bestD) (best, bestD) = (node, d);
        }
        const float Range2 = NodeRange * NodeRange;
        if (best is not { } n || bestD > Range2) return 0;
        float r2 = Math.Min(Range2, MathF.Max(625, n.Radius * n.Radius));
        float falloff = Math.Clamp((bestD - r2) / (Range2 - r2), 0, 1);
        return NodeYield(resource, n.Position) * (1 - falloff);
    }

    /// <summary>
    /// WATER, GROUND or STONE at <paramref name="at"/> by the resources record of the ground at <paramref name="recordAt"/> (the prospecting map reads every cell by the spot's record, <b>Verified</b>,
    /// <c>FUN_14049cd60</c> passes the group it looked up once). 0 for another resource or without a record.
    /// </summary>
    public float FieldYield(int resource, System.Numerics.Vector3 at, System.Numerics.Vector3 recordAt)
    {
        if (Resources(recordAt.X, recordAt.Z) is not { } r) return 0;
        if (resource == StoneResource)
            return World.FoliageNoise.Coverage(at.X, at.Z, r.GetFloat("stone noise zoom") * 1000, r.GetFloat("stone noise cutoff"), 3, 1) * r.GetFloat("stone mult");
        return resource is MachineData.WaterResource or MachineData.GroundResource ? Value(r, resource, at.Y) : 0;
    }

    /// <summary>
    /// A rock's own yield (its natural site's, <c>FarmBuilding::vfunc_92</c>; <b>Observed</b>, <c>uses_world_resource_mining</c> -> <c>FUN_140a12ff0</c>): WATER and GROUND as
    /// <see cref="Value"/>, STONE the stone noise x <c>stone mult</c> as <see cref="Yield"/>, the others the resources record's <c>&lt;p&gt; mult</c> where it stands; at least 0.1 (<b>Verified</b>, <c>FUN_14029c670</c>'s natural branch).
    /// </summary>
    public float NodeYield(int resource, System.Numerics.Vector3 at)
    {
        float v;
        if (resource is MachineData.WaterResource or MachineData.GroundResource or StoneResource) v = Yield(resource, at);
        else
        {
            string key = Prefix(resource) + " mult";
            v = Resources(at.X, at.Z) is { } r && r.Floats.ContainsKey(key) ? r.GetFloat(key) : 0;
        }
        return MathF.Max(0.1f, v);
    }

    GameRecord? Group(float x, float z)
    {
        if (map is null) return null;
        var (cx, cz) = World.WeatherAreas.CellOf(x, z);
        int g = map.GroupOf(cx, cz);
        return g >= 0 ? db.Find(map.Groups[g].Id) : null;
    }

    GameRecord? Resources(float x, float z)
    {
        if (map is null) return null;
        var (cx, cz) = World.WeatherAreas.CellOf(x, z);
        int g = map.GroupOf(cx, cz);
        if (resourcesOf.TryGetValue(g, out var cached)) return cached;
        var record = g >= 0 && db.Find(map.Groups[g].Id) is { } group && group.GetReferences("resources") is [var link, ..] ? db.Find(link.TargetStringId) : null;
        resourcesOf[g] = record;
        return record;
    }

    /// <summary>
    /// The WATER or GROUND value at a position (<b>Observed</b>, <c>FUN_140a12ff0</c>): band x <c>&lt;p&gt; mult</c> + <c>&lt;p&gt; min</c>, the band 1 between the altitude min and max and
    /// ramping to 0 over the fade beyond them. 0 without a resources record.
    /// </summary>
    public float Value(int resource, float x, float y, float z) => Resources(x, z) is { } r ? Value(r, resource, y) : 0;

    static float Value(GameRecord r, int resource, float y)
    {
        string p = resource == MachineData.WaterResource ? "water" : resource == MachineData.GroundResource ? "farming" : "";
        if (p.Length == 0) return 0;
        float min = r.GetInt(p + " altitude min"), max = r.GetInt(p + " altitude max"), fade = r.GetInt(p + " altitude fade");
        float band;
        if (y >= min && y <= max) band = 1;
        else if (fade <= 0) band = 0;
        else if (y < min) band = Math.Clamp((y - (min - fade)) / fade, 0, 1);
        else band = Math.Clamp(((max + fade) - y) / fade, 0, 1);
        return band * r.GetFloat(p + " mult") + r.GetFloat(p + " min");
    }

    /// <summary>The farm's crop match C = <c>arid</c> x the arid coverage + <c>green</c> x green + <c>swamp</c> x swamp (<b>Observed</b>, <c>FUN_1400dfe10</c>); 1 indoors.</summary>
    public float CropMatch(FarmData farm, float x, float z)
    {
        if (farm.Indoor) return 1;
        if (Resources(x, z) is not { } r) return 0;
        return farm.Arid * r.GetFloat("arid") + farm.Green * r.GetFloat("green") + farm.Swamp * r.GetFloat("swamp");
    }

    /// <summary>The name of the BIOME_GROUP at a position (null without an areas map): what the prospecting window shows as the zone.</summary>
    public string? ZoneName(float x, float z) => Group(x, z)?.Name;

    /// <summary>The crop coverages (<c>green</c>, <c>arid</c>, <c>swamp</c>) of the ground at a position (<b>Observed</b>, the prospecting window's Environment); all 0 without a resources record.</summary>
    public (float Green, float Arid, float Swamp) Coverage(float x, float z) =>
        Resources(x, z) is { } r ? (r.GetFloat("green"), r.GetFloat("arid"), r.GetFloat("swamp")) : default;

    /// <summary>The biome group's <c>farm water usage</c> (1 without a group).</summary>
    public float WaterUsage(float x, float z) => Group(x, z) is { } g && g.Floats.ContainsKey("farm water usage") ? g.GetFloat("farm water usage") : 1;
}
