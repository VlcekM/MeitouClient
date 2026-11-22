using System.Runtime.InteropServices;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

/// <summary>
/// A machine's last published view and every value it was made from (docs/simulation.md "Snapshots"): each publish records the values again and
/// makes a new <see cref="MachineSnapshot"/> only when one differs, so an idle machine (a storage box) keeps its view and its item views. Exact:
/// numbers are compared by their bits and strings and data records by reference (all immutable), so no change goes unseen. A storage's items
/// are walked only for a machine the host shows (<see cref="Player.PlayerState.ShownMachines"/>).
/// </summary>
sealed class MachineViewCache
{
    /// <summary>
    /// The numbers the last view was made from, floats by their bits.
    /// </summary>
    List<long> _values = [];
    /// <summary>
    /// The strings and data records the last view was made from, compared by reference.
    /// </summary>
    List<object?> _refs = [];
    /// <summary>
    /// The numbers of this publish, swapped with <see cref="_values"/> when they differ.
    /// </summary>
    List<long> _nextValues = [];
    /// <summary>
    /// The references of this publish, swapped with <see cref="_refs"/> when they differ.
    /// </summary>
    List<object?> _nextRefs = [];
    /// <summary>
    /// The view last made (null: none yet).
    /// </summary>
    MachineSnapshot? _view;

    /// <summary>
    /// The view of <paramref name="m"/> now: the last one while nothing it shows changed, else a new one. Its storage's items are recorded and
    /// published only while <paramref name="shown"/>; an unshown storage is reused whatever its items, by its size and emptiness alone.
    /// </summary>
    internal MachineSnapshot ViewOf(Machine m, ItemFactory items, bool shown)
    {
        _nextValues.Clear();
        _nextRefs.Clear();
        Record(m, shown);
        if (_view is not null && Unchanged()) return _view;

        (_values, _nextValues) = (_nextValues, _values);
        (_refs, _nextRefs) = (_nextRefs, _refs);
        return _view = MachineSnapshot.Of(m, items, shown);
    }

    /// <summary>
    /// Whether this publish recorded what the last view was made from.
    /// </summary>
    bool Unchanged()
    {
        if (!CollectionsMarshal.AsSpan(_values).SequenceEqual(CollectionsMarshal.AsSpan(_nextValues))) return false;
        if (_refs.Count != _nextRefs.Count) return false;

        for (int i = 0; i < _refs.Count; i++)
            if (!ReferenceEquals(_refs[i], _nextRefs[i])) return false;

        return true;
    }

    /// <summary>
    /// Records everything <see cref="MachineSnapshot.Of"/> reads of the machine that can change (its data is immutable); keep the two in step.
    /// </summary>
    void Record(Machine m, bool shown)
    {
        RecordState(m);
        RecordPower(m);
        RecordSlots(m);
        RecordStorage(m.Storage, shown);
        if (m.Farm is { } farm) RecordFarm(farm);
        if (m.Craft is { } craft) RecordCraft(craft);
    }

    /// <summary>
    /// Whether the storage's items are published, its presence, size and emptiness, and its items only while <paramref name="shown"/>.
    /// </summary>
    void RecordStorage(Inventory? grid, bool shown)
    {
        Flag(shown);
        Flag(grid is not null);
        if (grid is null) return;

        Number(grid.Width);
        Number(grid.Height);
        Flag(grid.Items.Count == 0);
        if (shown) RecordGrid(grid);
    }

    /// <summary>
    /// The status, operators, sound, rotation, wind, research, broken, furnace and efficiency inputs.
    /// </summary>
    void RecordState(Machine m)
    {
        Number(m.Status);
        Number(m.Operators);
        Real(m.FarmerSkillTotal);
        Real(m.Yield);
        Real(m.CropMatch);
        Real(m.Efficiency);
        Flag(m.HasPower);
        Real(m.Rotation);
        Real(m.Wind);
        Real(m.WindSpeed);
        Real(m.WindRated);
        Real(m.ProductionMult);
        Flag(m.Broken);
        Flag(m.Burning);
        Real(m.Metal);
        Real(m.Health);
        Real(m.Power);
        Real(m.Rain);
        Flag(m.Active);
    }

    /// <summary>
    /// The power panel's inputs.
    /// </summary>
    void RecordPower(Machine m)
    {
        Flag(m.PowerOn);
        Flag(m.GetsBattery);
        Real(m.Demand);
        Real(m.Granted);
        Real(m.Usage);
        Real(m.Charge);
        Real(m.Store);
        Flag(m.OnGrid);
        Flag(m.GridCharging);
        Flag(m.GridDraining);
    }

    /// <summary>
    /// The input sections and the output.
    /// </summary>
    void RecordSlots(Machine m)
    {
        foreach (var input in m.Inputs)
        {
            Ref(input.Item);
            Number(input.Count);
            Real(input.Level);
            Real(input.Rate);
            Flag(input.Idle);
        }

        if (m.Output is not { } output) return;
        Number(output.Count);
        Real(output.Progress);
    }

    /// <summary>
    /// A shown storage grid's items.
    /// </summary>
    void RecordGrid(Inventory grid)
    {
        Number(grid.Items.Count);
        foreach (var item in grid.Items) RecordItem(item);
    }

    /// <summary>
    /// The crop's growth, plants and state.
    /// </summary>
    void RecordFarm(FarmState farm)
    {
        Real(farm.Growth);
        Real(farm.Death);
        Real(farm.Start);
        Number(farm.Harvested);
        Real(farm.HarvestProgress);
        Number((int)farm.Status);
        Number(farm.Plants.Length);
        foreach (float plant in farm.Plants) Real(plant);
    }

    /// <summary>
    /// The bench's queue, repeat flag and finished items.
    /// </summary>
    void RecordCraft(CraftState craft)
    {
        Number(craft.Queue.Count);
        foreach (var job in craft.Queue)
        {
            Ref(job.Recipe);
            Real(job.Progress);
        }

        Flag(craft.Repeat);
        Number(craft.Done.Count);
        foreach (var item in craft.Done) RecordItem(item);
    }

    /// <summary>
    /// Everything <see cref="InventoryItemView.Of"/> reads of an item; keep the two in step.
    /// </summary>
    void RecordItem(ItemInstance item)
    {
        Ref(item.Record);
        Ref(item.Name);
        Ref(item.Section);
        Ref(item.MaterialId);
        Ref(item.CompanyId);
        Ref(item.Crafter);
        Ref(item.ModTag);
        Ref(item.ColourId);
        Number(item.X);
        Number(item.Y);
        Number(item.Width);
        Number(item.Height);
        Number(item.Quantity);
        Real(item.Weight);
        Real(item.Quality);
        Flag(item.Rotated);
        Number(item.Level);
        Real(item.Charges);
        Real(item.MaxCharges);
        Flag(item.Unique);
        Number(item.Contents?.Items.Count ?? -1);
    }

    /// <summary>
    /// Records a whole number.
    /// </summary>
    void Number(int value) => _nextValues.Add(value);

    /// <summary>
    /// Records a float by its bits (a NaN equals itself).
    /// </summary>
    void Real(float value) => _nextValues.Add(BitConverter.SingleToInt32Bits(value));

    /// <summary>
    /// Records a flag.
    /// </summary>
    void Flag(bool value) => _nextValues.Add(value ? 1 : 0);

    /// <summary>
    /// Records an immutable string or data record by reference.
    /// </summary>
    void Ref(object? value) => _nextRefs.Add(value);
}
