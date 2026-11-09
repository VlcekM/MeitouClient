using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

// The item furnace (docs/game/buildings-production.md "Item furnace"): FurnaceBuilding burns whatever lies in its input grid into its output (Raw Iron), by the metal the items hold.
public sealed partial class ProductionSystem
{
    /// <summary>
    /// The furnace's input grid (<b>Verified</b>, <c>FurnaceBuilding::setupFromData</c> <c>FUN_14029f980</c>): section <c>in1</c>, 12 x 7 cells,
    /// stacks at least 10.
    /// </summary>
    public const int FurnaceWidth = 12, FurnaceHeight = 7, FurnaceStackMinimum = 10;
    /// <summary>
    /// The ITEM whose ingredient amounts are the iron in an item (<b>Verified</b>, the literal in <c>FurnaceBuilding::getIronAmountInItem</c>
    /// <c>FUN_1402a5750</c>).
    /// </summary>
    public const string RawIron = "1866-gamedata.base";
    /// <summary>
    /// The LOCATIONAL_DAMAGE entry whose <c>part coverage</c> counts 1.5 times in an armour's total coverage (<b>Observed</b>, <c>FUN_14082fd80</c>).
    /// </summary>
    const string WeightedPart = "101-gamedata.quack";

    /// <summary>
    /// An empty furnace grid (<see cref="FurnaceWidth"/> x <see cref="FurnaceHeight"/>, stacks at least <see cref="FurnaceStackMinimum"/>).
    /// </summary>
    static Inventory FurnaceGrid() => new(FurnaceWidth, FurnaceHeight) { StackBonusMinimum = FurnaceStackMinimum };

    /// <summary>
    /// The Active button (<c>FurnaceBuilding::incinerate</c> <c>FUN_140299790</c>, <b>Observed</b>): an idle furnace with something in its grid starts burning (level 1, progress 0) and its
    /// grid is closed; a burning one stops (level and progress 0, the items stay) and its grid opens again.
    /// </summary>
    static void Incinerate(Machine m)
    {
        if (!m.Data.IsFurnace || m.Storage is not { } grid || m.Output is not { } output) return;
        if (!m.Burning && grid.Items.Count == 0) return;
        m.Burning = !m.Burning;
        m.BurnLevel = m.Burning ? 1 : 0;
        output.Progress = 0;
        m.Status = (byte)(m.Burning ? 2 : 0);   // engine choice: the original leaves its state at 2 once it has burnt
    }

    /// <summary>
    /// A burning furnace's step (<c>FurnaceBuilding::operate</c> <c>FUN_140299fa0</c>, <b>Observed</b>; it needs no operator, so it runs with skill factor 1 like any automatic machine): the
    /// work amount of <see cref="ProduceStep"/> comes off the level and onto the output's progress; at level 0.001 or progress 0.999 the output gets (int) the grid's metal
    /// (<see cref="FurnaceMetal"/>) Raw Iron, the grid is emptied and the furnace stops (<see cref="Incinerate"/>). Engine choice (the original's <c>operate</c> has no such test): while
    /// the output cannot take the load's iron it holds, status 1 (output full), and goes on once there is room.
    /// </summary>
    void FurnaceStep(World world, Machine m, float dt)
    {
        if (m.Storage is not { } grid || m.Output is not { } output) return;
        if (output.Count + Math.Max((int)FurnaceMetal(grid), 0) > output.Recipe.Capacity || output.Full)
        {
            m.Status = 1;
            return;
        }

        m.Status = 2;
        float amount = m.Yield * m.Data.Rate * WorkFactor(world, m) * dt * _constants.ProductionSpeed * Settings.ProductionOption;
        if (amount == 0) return;
        m.BurnLevel -= amount;
        output.Progress += amount;
        if (m.BurnLevel > 0.001f && output.Progress < 0.999f) return;
        int iron = (int)FurnaceMetal(grid);
        output.Count += Math.Max(iron, 0);
        grid.Items.Clear();
        Incinerate(m);
        world.Machines.Touch();
    }

    /// <summary>
    /// The metal of a furnace grid (<c>FUN_140299820</c>, the panel's <c>Total Metals:</c>): the sum of <see cref="MetalOf"/> over its items.
    /// </summary>
    public float FurnaceMetal(Inventory grid)
    {
        float total = 0;
        foreach (var item in grid.Items) total += MetalOf(item);
        return total;
    }

    /// <summary>
    /// <c>FurnaceBuilding::getInputValue</c> (<c>FUN_1402a5f40</c>, <b>Verified</b> constants by disassembly): an ITEM with <c>ingredients</c> its iron (<see cref="IronIn"/>) x quantity x 0.5;
    /// an ARMOUR of <c>material type</c> 2 or 3 (chain, plate) total coverage x 0.01 x (1 - <c>fabrics amount</c>) x 2; a WEAPON its unit weight x 0.2, kept within 0.5..5; anything else 0.
    /// </summary>
    float MetalOf(ItemInstance item)
    {
        if (_db.Find(item.Record) is not { } r) return 0;
        switch (item.Type)
        {
            case FcsRecordType.Item:
                return r.GetReferences("ingredients").Count > 0 ? IronIn(r, 0) * item.Quantity * 0.5f : 0;
            case FcsRecordType.Armour:
                if (r.GetInt("material type") is not (2 or 3)) return 0;
                return TotalCoverage(r) * 0.01f * (1 - r.GetFloat("fabrics amount", 0.2f)) * 2;
            case FcsRecordType.Weapon:
                return Math.Clamp(item.UnitWeight * 0.2f, 0.5f, 5);
            default:
                return 0;
        }
    }

    /// <summary>
    /// <c>FurnaceBuilding::getIronAmountInItem</c> (<c>FUN_1402a5750</c>, <b>Verified</b> by disassembly): over the record's <c>ingredients</c>, a Raw Iron entry adds its first value x 0.01,
    /// any other ingredient adds its own iron (whatever its amount). <paramref name="depth"/> guards against a looped list in mod data (engine choice).
    /// </summary>
    float IronIn(GameRecord record, int depth)
    {
        if (depth > 16) return 0;
        float iron = 0;
        foreach (var r in record.GetReferences("ingredients"))
        {
            if (r.TargetStringId == RawIron) iron += r.Values.Value0 * 0.01f;
            else if (_db.Find(r.TargetStringId) is { } ingredient) iron += IronIn(ingredient, depth + 1);
        }

        return iron;
    }

    /// <summary>
    /// An armour's <c>total coverage</c> (<c>FUN_14082fd80</c>, <b>Observed</b>; stored in the record's int table at run time): the sum of its <c>part coverage</c> percentages, the
    /// <see cref="WeightedPart"/> entry's x 1.5 (truncated), 100 when that sums to 0.
    /// </summary>
    static int TotalCoverage(GameRecord armour)
    {
        int total = 0;
        foreach (var r in armour.GetReferences("part coverage"))
            total += r.TargetStringId == WeightedPart ? (int)(r.Values.Value0 * 1.5) : r.Values.Value0;

        return total == 0 ? 100 : total;
    }
}