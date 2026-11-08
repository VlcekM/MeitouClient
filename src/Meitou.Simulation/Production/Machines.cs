using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

/// <summary>A recipe input of a machine (docs/game/buildings-production.md "Production", Recipes): the items waiting in its section and the level drawn from them.</summary>
public sealed class InputSlot(RecipeInput recipe)
{
    public RecipeInput Recipe { get; } = recipe;
    /// <summary>The item the section takes: the recipe's, or for a crafting bench the current craft's ingredient (docs/game/buildings-production.md "Crafting").</summary>
    public string Item { get; set; } = recipe.Item;
    /// <summary>Units drawn per unit of work (the recipe's rate, or the craft's ingredient rate).</summary>
    public float Rate { get; set; } = recipe.Rate;
    /// <summary>Items held in the section (no quality: inputs and crops have none, engine choice).</summary>
    public int Count { get; set; }
    /// <summary>The level taken from items, one item = 1 (the save's <c>consumption amount-&lt;k&gt;</c>).</summary>
    public float Level { get; set; }
}

/// <summary>The output of a machine: the items made so far (at most <see cref="RecipeOutput.Capacity"/>) and the progress to the next (the save's <c>production amount</c>).</summary>
public sealed class OutputSlot(RecipeOutput recipe)
{
    public RecipeOutput Recipe { get; } = recipe;
    public int Count { get; set; }
    public float Progress { get; set; }
    public bool Full => Count >= Recipe.Capacity;
}

/// <summary>What a farm reports (<b>Observed</b>, FarmBuilding <c>+0x468</c>).</summary>
public enum FarmStatus : byte { NoWater = 0, OutputFull = 1, Harvest = 2, Growing = 3 }

/// <summary>A farm's crop (docs/game/buildings-production.md "Farming"). Growth 0..1 grows, 1..2 ripe, 2 and up rots; <see cref="Death"/> 1 loses the crop.</summary>
public sealed class FarmState(int plants)
{
    /// <summary>Each plant's growth; a harvested plant is -10.</summary>
    public float[] Plants { get; } = new float[plants];
    /// <summary>The farm's growth (<c>plant growth</c>): the lowest growing plant while below 1, then ripening up to 2.</summary>
    public float Growth { get; set; }
    /// <summary><c>plant death</c>.</summary>
    public float Death { get; set; }
    /// <summary><c>plant start</c>: the lowest growth at replanting.</summary>
    public float Start { get; set; }
    /// <summary><c>plants harvested</c>: also the plant worked next.</summary>
    public int Harvested { get; set; }
    public float HarvestProgress { get; set; }
    /// <summary>Eating by animals (<c>FarmBuilding::eat</c>, the accumulator <c>+0x514</c>): past 1 a plant is destroyed. Not saved.</summary>
    public float EatProgress { get; set; }
    public float ClearProgress { get; set; }
    public FarmStatus Status { get; set; } = FarmStatus.Growing;
    /// <summary>Plants replanted so far, the roll counter of the replanting draws.</summary>
    public int Plantings { get; set; }
    /// <summary>Plant rolls made so far, the roll counter of the harvest draws.</summary>
    public int Rolls { get; set; }
    /// <summary>The game hour the farm last updated (the save's <c>save time</c>); NaN: not known yet.</summary>
    public double LiveHours { get; set; } = double.NaN;
}

/// <summary>One queued craft (docs/game/buildings-production.md "Crafting"): what, its recipe, the work seconds it takes (hours x G/24), its progress 0..1 and its critical roll.</summary>
public sealed class CraftJob(CraftRecipe recipe)
{
    public CraftRecipe Recipe { get; } = recipe;
    public CraftOption Option => Recipe.Option;
    public float Seconds => Recipe.Hours * (MachineData.DaySeconds / 24);
    public float Progress { get; set; }
    /// <summary>The save's <c>crit</c>: 2 not rolled yet, 1 a critical, 0 none (<b>Observed</b>, <c>CraftingBuilding::vfunc_17</c>).</summary>
    public byte Critical { get; set; } = 2;
}

/// <summary>A crafting bench's queue, its repeat flag (the save's <c>repeat</c>) and the finished items waiting in its output.</summary>
public sealed class CraftState
{
    public List<CraftJob> Queue { get; } = [];
    public bool Repeat { get; set; }
    public List<ItemInstance> Done { get; } = [];
    /// <summary>The craft the input sections are set for (its key), so they are rebound when the first job changes.</summary>
    public string? Bound { get; set; }
    /// <summary>Critical rolls made so far, the roll counter.</summary>
    public int Rolls { get; set; }
    /// <summary>
    /// The levels of ingredients no section holds now (the original's item-to-level map, <b>Verified</b> <c>FUN_1402bb470</c>): a recipe switch stores each section's level under its item and a section
    /// taking an item gets its level back; saved as <c>craft partial item ...</c>. Only levels above 0 are kept.
    /// </summary>
    public Dictionary<string, float> Partial { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A complete player building that works or stores (docs/simulation.md "Production"): its recipe slots, storage grid and farm. Made by <see cref="ProductionSystem"/> when the site completes
/// (or loads complete) and dropped with the site; serial phases only.
/// </summary>
public sealed class Machine
{
    public required string Site { get; init; }
    public required MachineData Data { get; init; }
    /// <summary>Where the building stands (its items drop here when it goes).</summary>
    public System.Numerics.Vector3 Position { get; init; }
    public InputSlot[] Inputs { get; init; } = [];
    public OutputSlot? Output { get; init; }
    /// <summary>The storage grid of a building with <c>has inventory</c> (a research bench's books).</summary>
    public Inventory? Storage { get; init; }
    public FarmState? Farm { get; init; }
    /// <summary>A crafting bench's queue and output.</summary>
    public CraftState? Craft { get; init; }
    /// <summary>The resource yield (<c>resource mult</c>): 1 without a resource, else the ground's value (0 becomes 0.5); for a farm its fertility factor.</summary>
    public float Yield { get; set; } = 1;
    /// <summary>A farm's crop match with the biome (C).</summary>
    public float CropMatch { get; set; } = 1;
    /// <summary>A farm's water draw per second: <c>consumption rate</c> / G x the biome's <c>farm water usage</c>.</summary>
    public float WaterRate { get; set; }
    /// <summary>
    /// Production status (<c>+0x468</c>, docs/game/buildings-production.md "Blocked output"): 2 ready or working (a producer starts there), 1 output full (blocked: the planner's states 63 / 64
    /// read it), 0 nothing to work with. Not saved (the original saves no such key). Hashed.
    /// </summary>
    public byte Status { get; set; }

    /// <summary>
    /// <c>machineHasEmptyInputs</c> (slot 149 <c>FUN_140298b70</c>, <b>Verified</b>): some rated input holds no item and its level is spent (0.001 or less).
    /// </summary>
    public bool HasEmptyInputs
    {
        get
        {
            foreach (var input in Inputs)
                if (input.Rate > 0 && input.Count == 0 && input.Level <= 0.001f) return true;
            return false;
        }
    }

    /// <summary>
    /// <c>isMachineOutputFull</c>, the output half of <c>isMachineJammed</c> (slot 175, <c>FUN_140296fb0</c>, <b>Verified</b>): a production machine with an output whose status is 1. Farms,
    /// benches and storages have variants of their own that Meitou does not evaluate (false).
    /// </summary>
    public bool OutputBlocked => Status == 1 && Output is not null && Farm is null && Craft is null && !Data.IsStorage;

    /// <summary><c>isMachineJammed</c> (<c>FUN_1405a2e60</c>, <b>Verified</b>): an empty input, else a blocked output.</summary>
    public bool Jammed => HasEmptyInputs || OutputBlocked;
    /// <summary>Characters working it this tick (derived; not saved, not hashed).</summary>
    public int Operators { get; set; }
    /// <summary>
    /// The character that last worked a crafting bench: the exe's operator hand (building <c>+0x4d0</c>, written by each operator's tick in <c>FUN_1402b5a40</c> and never cleared), which the
    /// Craft tab's Current Smith reads. Derived; not saved, not hashed (the original's handle is not saved either).
    /// </summary>
    public CharacterId Smith { get; set; } = CharacterId.None;
    /// <summary>Operators last tick: what a consumer's draw is scaled by (the grid runs after the operators, so its result applies one tick later).</summary>
    public int LastOperators { get; set; }
    /// <summary>The power factor its work is scaled by: granted / demand for a consumer (1 with no demand); health is <see cref="Health"/> (docs/game/buildings-production.md "Power").</summary>
    public float Power { get; set; } = 1;
    /// <summary>A consumer's demand and the share granted, a generator's output, a battery's flow (+ charging, - draining).</summary>
    public float Demand { get; set; }
    public float Granted { get; set; }
    /// <summary>A generator's load (<c>vfunc_167</c>): the share of its output the grid uses; fuel burns at this rate.</summary>
    public float Usage { get; set; }
    /// <summary>A battery's store in seconds at its rate (the save's <c>batterycharge</c>).</summary>
    public float Charge { get; set; }
    /// <summary>
    /// A battery's town grid this tick (the town's power object, slot 0x268): whether there is one, and whether its batteries charged (+0x488) or drained (+0x484) overall, which the battery's
    /// header word reads (<c>FUN_140300b40</c>). Derived each tick; not saved, not hashed.
    /// </summary>
    public bool OnGrid { get; set; }
    public bool GridCharging { get; set; }
    public bool GridDraining { get; set; }
    /// <summary>A battery's capacity now (data + research), for the view.</summary>
    public float Store { get; set; }
    /// <summary>The save's <c>gets battery</c>: a consumer short of generator power draws from batteries (default on).</summary>
    public bool GetsBattery { get; set; } = true;
    /// <summary>The save's <c>power on</c> (default on; no UI switch yet).</summary>
    public bool PowerOn { get; set; } = true;
    /// <summary>
    /// The save's <c>broken</c> (UseableStuff <c>+0x3b6</c>, docs/game/buildings-production.md "Broken machines"): set by every dismantle step (<c>Building::addDismantleProgress</c>), cleared
    /// by a build step that leaves progress above 0, by completion and by the ruin's first tick. A broken turret cannot be manned.
    /// </summary>
    public bool Broken { get; set; }
    /// <summary>
    /// What the machine tells its sound emitter (docs/audio.md "Point emitters"): the exe's efficiency (<c>FarmBuilding::vfunc_108</c>) and whether it has power (<c>FUN_140299370</c>). Derived
    /// every tick for the host's display; not saved, not hashed.
    /// </summary>
    public float Efficiency { get; set; }
    public bool HasPower { get; set; }
    /// <summary>
    /// Building health, a factor of its work (the exe's slot 109, <c>FUN_140296e00</c>): construction progress / target capped at 1 (1 for a rock or a target of 0), and the research
    /// production multiplier. Derived each tick; not saved, not hashed.
    /// </summary>
    public float Health { get; set; } = 1;
    /// <summary>
    /// Building slot 59 (<c>isIndoors</c>) is set: its record is <c>is interior furniture</c> and it stands in a building; and slot 164: it works now (a light building outdoors only while
    /// it is not daytime, every other always). An inactive machine draws no power and its efficiency is 0. Derived each tick; not saved, not hashed.
    /// </summary>
    public bool Indoors { get; set; }
    public bool Active { get; set; } = true;
    public float ProductionMult { get; set; } = 1;
    /// <summary>A wind generator's share of its output: its blade's <see cref="Meitou.Data.World.RotorSpec.WindShare"/> of the wind at the site (derived each tick; not saved, not hashed).</summary>
    public float Wind { get; set; }
    /// <summary>The wind range of the rotor this placement spawned (<see cref="Meitou.Data.World.WorldObjectLayout.WindRotor"/>; derived at creation, not saved); null: the share is 0.</summary>
    public (float Min, float Max)? WindRotor { get; init; }
    /// <summary>A wind generator's wind speed at the site (units/s) and its rated output (data + research), for its panel; derived each tick.</summary>
    public float WindSpeed { get; set; }
    public float WindRated { get; set; }
    /// <summary>
    /// A rain collector's rain strength (<c>FUN_140298400</c>): the <c>wetness</c> of the current weather of the region at the site unless that weather's <c>affect type</c> is acid
    /// (2), 0 without weather; derived each tick for its panel.
    /// </summary>
    public float Rain { get; set; }
    /// <summary>
    /// What its parts with <c>rotation function</c> ROTATION_OUTPUT turn by (<c>getOutputBasedRotationSpeedMult</c>, docs/game/buildings-production.md "Rotating parts"): a generator's output x 0.01
    /// while switched on, every other machine's <see cref="Efficiency"/>. Derived each tick for the host's display; not saved, not hashed.
    /// </summary>
    public float Rotation { get; set; }
    /// <summary>
    /// An item furnace's <c>active</c> (FurnaceBuilding <c>+0x490</c>): it is burning its grid (<see cref="Storage"/>), which is closed meanwhile. Not saved (the original's save keeps
    /// no such key, so a loaded furnace is idle).
    /// </summary>
    public bool Burning { get; set; }
    /// <summary>An item furnace's input level (1 when it starts burning, down to 0; the save's <c>consumption amount-0</c>).</summary>
    public float BurnLevel { get; set; }
    /// <summary>A burning furnace's grid takes and gives nothing (<c>InventorySection::setEnabled</c> off).</summary>
    public bool Closed => Burning && Data.IsFurnace;
    /// <summary>An item furnace's grid metal (<see cref="ProductionSystem.FurnaceMetal"/>), for its panel; derived each tick, not saved, not hashed.</summary>
    public float Metal { get; set; }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Rng.StableHash(Site));
        h.Add(Yield);
        h.Add(Status);
        h.Add(Power); h.Add(Charge); h.Add(Granted);
        if (Broken) h.Add(0x42726B6E);
        if (Data.IsFurnace) { h.Add(Burning ? 1 : 0); h.Add(BurnLevel); }
        foreach (var i in Inputs) { h.Add(i.Count); h.Add(i.Level); }
        if (Output is { } o) { h.Add(o.Count); h.Add(o.Progress); }
        if (Storage is { } s) { h.Add(s.Items.Count); foreach (var item in s.Items) { h.Add(Rng.StableHash(item.Record)); h.Add(item.Quantity); h.Add(item.X); h.Add(item.Y); } }
        if (Farm is { } f)
        {
            h.Add(f.Growth); h.Add(f.Death); h.Add(f.Start); h.Add(f.Harvested); h.Add(f.HarvestProgress); h.Add(f.ClearProgress); h.Add((byte)f.Status);
            foreach (var p in f.Plants) h.Add(p);
            if (f.EatProgress != 0) h.Add(f.EatProgress);
        }
        if (Craft is { } c)
        {
            h.Add(c.Queue.Count); h.Add(c.Repeat ? 1 : 0); h.Add(c.Done.Count);
            foreach (var j in c.Queue) { h.Add(Rng.StableHash(j.Option.Key)); h.Add(j.Progress); h.Add(j.Critical); }
            foreach (var d in c.Done) { h.Add(Rng.StableHash(d.Record)); h.Add(d.Quantity); h.Add(d.Quality); }
            // Remembered levels of items no section holds (the held ones are in Inputs), sorted: a loaded map also lists the held ones.
            if (c.Partial.Count > 0)
                foreach (var (item, level) in c.Partial.OrderBy(p => p.Key, StringComparer.Ordinal))
                    if (!Inputs.Any(i => i.Item == item)) { h.Add(Rng.StableHash(item)); h.Add(level); }
        }
    }
}

/// <summary>The machines of the player's buildings, by site id, in the order they were made. Hashed only while it holds one.</summary>
public sealed class MachineRegistry
{
    readonly List<Machine> _all = [];
    readonly Dictionary<string, Machine> _bySite = new(StringComparer.Ordinal);
    IReadOnlyList<MachineSnapshot>? _published;

    public int Count => _all.Count;
    public IReadOnlyList<Machine> All => _all;
    public Machine? Find(string site) => _bySite.GetValueOrDefault(site);
    /// <summary>Bumped on every change the host shows (items, progress, status).</summary>
    public long Version { get; private set; }

    public void Touch()
    {
        Version++;
        _published = null;
    }

    internal void Add(Machine machine)
    {
        _all.Add(machine);
        _bySite[machine.Site] = machine;
        Touch();
    }

    /// <summary>Puts <paramref name="machine"/> in place of its site's machine, keeping its place in the order (an upgraded building's machine for its new record).</summary>
    internal void Replace(Machine machine)
    {
        int at = _all.FindIndex(m => m.Site == machine.Site);
        if (at < 0) _all.Add(machine);
        else _all[at] = machine;
        _bySite[machine.Site] = machine;
        Touch();
    }

    internal bool Remove(string site)
    {
        if (!_bySite.Remove(site, out var m)) return false;
        _all.Remove(m);
        Touch();
        return true;
    }

    internal void Clear()
    {
        _all.Clear();
        _bySite.Clear();
        Touch();
    }

    internal void Hash(ref StateHasher h)
    {
        h.Add(0x4D616368);
        h.Add(_all.Count);
        foreach (var m in _all) m.Hash(ref h);
    }

    internal IReadOnlyList<MachineSnapshot> Published(ItemFactory items) => _published ??= [.. _all.Select(m => MachineSnapshot.Of(m, items))];
}