using System.Globalization;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The machines of the player's buildings in a save (docs/simulation.md "Production"): the production and farm keys of each building's
/// GAMESTATE_BUILDING (docs/game/buildings-production.md "Production", "Farming" and "Power": <c>production amount</c>, <c>resource mult</c>,
/// <c>power on</c>, <c>gets battery</c>, <c>batterycharge</c>, <c>consumption amount-&lt;k&gt;</c>, <c>plant growth</c>, <c>plant death</c>, <c>plant
/// start</c>, <c>plants harvested</c>, <c>save time</c>), and its items in an INVENTORY_STATE named first by the building's instance states
/// (docs/formats/save.md "Zone files"). The items' sections are <c>in&lt;k&gt;</c>, <c>out</c> and, for the storage grid, <c>main</c> (the original's
/// storage section name is <b>Unknown</b>; no sample save holds a player building with items, so the original reading these is unverified). A loaded
/// farm's plants all take the farm's growth (the harvested ones -10): the stagger between plants is not saved.
/// </summary>
public static class MachineSave
{
    /// <summary>
    /// The suffix of a building's GAMESTATE_BUILDING state id.
    /// </summary>
    const string StateSuffix = "-S23";
    /// <summary>
    /// The suffix of a building's INVENTORY_STATE id.
    /// </summary>
    const string InventorySuffix = "-inventory";

    /// <summary>
    /// The item furnace's input section (<b>Verified</b>, <c>FurnaceBuilding::setupFromData</c>), Meitou's storage grid of a furnace.
    /// </summary>
    const string FurnaceSection = "in1";

    /// <summary>
    /// The key prefix of a remembered ingredient's string id (followed by its number).
    /// </summary>
    const string PartialSid = "craft partial item sid ";
    /// <summary>
    /// The key prefix of a remembered ingredient's level (followed by its number).
    /// </summary>
    const string PartialVal = "craft partial item val ";

    /// <summary>
    /// Writes every machine into its building's state (after <see cref="BuildingSave.Write"/>).
    /// </summary>
    public static void Write(SaveGame game, World world)
    {
        if (world.Machines.Count == 0) return;

        foreach (var m in world.Machines.All)
        {
            if (world.Buildings.Find(m.Site) is not { } site || !game.Zones.TryGetValue(site.Zone, out var zone) || zone.BuildingList is not { } list)
                continue;

            var file = zone.File;
            var state = file.Records.FirstOrDefault(r => r.StringId == site.Id + StateSuffix && r.RecordType == FcsRecordType.GamestateBuilding);
            var instance = list.Instances.FirstOrDefault(i => i.Id == site.Id);
            if (state is null || instance is null) continue;

            WriteKeys(state, m, world.GameHours);
            WriteInventory(file, site, instance, Items(m));
        }
    }

    /// <summary>
    /// Replaces the building's inventory record with its items, or removes the record when it holds none.
    /// </summary>
    static void WriteInventory(SaveFile file, BuildSite site, FcsInstance instance, List<SaveItem> saveItems)
    {
        string inventoryId = site.Id + InventorySuffix;
        var inventory = file.Records.FirstOrDefault(r => r.StringId == inventoryId && r.RecordType == FcsRecordType.InventoryState);
        instance.States.Remove(inventoryId);
        if (saveItems.Count == 0)
        {
            if (inventory is not null) SaveInventory.Replace(file, inventory, [], n => $"{n}-zone{site.Zone.X}.{site.Zone.Y}-INGAME");
            if (inventory is not null) file.Records.Remove(inventory);
            return;
        }

        if (inventory is null)
        {
            inventory = new FcsRecord { Type = (int)FcsRecordType.InventoryState, Id = SaveBuilder.NextNumber(file), Name = "0",
                StringId = inventoryId };
            file.Records.Add(inventory);
        }

        SaveInventory.Replace(file, inventory, saveItems,
            n => string.Create(CultureInfo.InvariantCulture, $"{n}-zone{site.Zone.X}.{site.Zone.Y}-INGAME"));
        instance.States.Insert(0, inventoryId);
    }

    /// <summary>
    /// Writes a machine's production, power, battery, input and farm keys into its building state.
    /// </summary>
    static void WriteKeys(FcsRecord state, Machine m, double gameHours)
    {
        state.Floats["resource mult"] = m.Yield;

        // The power keys (Observed, vfunc_17/18 of the building classes FUN_1402a14b0 / FUN_1402a1750).
        state.Bools["power on"] = m.PowerOn;
        state.Bools["gets battery"] = m.GetsBattery;
        state.Bools["broken"] = m.Broken;
        if (m.Data.IsBattery) state.Floats["batterycharge"] = m.Charge;
        if (m.Output is { } o) state.Floats["production amount"] = o.Progress;
        for (int k = 0; k < m.Inputs.Length; k++)
            state.Floats["consumption amount-" + k.ToString(CultureInfo.InvariantCulture)] = m.Inputs[k].Level;

        if (m.Data.IsFurnace) state.Floats["consumption amount-0"] = m.BurnLevel;   // its one input, the burn level
        if (m.Craft is { } c) WriteCraft(state, m, c);
        if (m.Farm is not { } f) return;

        state.Floats["plant growth"] = f.Growth;
        state.Floats["plant death"] = f.Death;
        state.Floats["plant start"] = f.Start;
        state.Floats["save time"] = (float)(double.IsNaN(f.LiveHours) ? gameHours : f.LiveHours);
        state.Ints["plants harvested"] = f.Harvested;
    }

    /// <summary>
    /// The crafting keys (<b>Observed</b>, <c>CraftingBuilding::vfunc_17</c>): <c>num crafts</c> (a float), <c>repeat</c>, and per queued craft k
    /// <c>current&lt;k&gt;</c> (the item), <c>currentExtraData&lt;k&gt;</c> (a weapon's model, else empty), <c>current prog&lt;k&gt;</c> and
    /// <c>crit&lt;k&gt;</c>, then the partly drawn items (<b>Verified</b>, <c>FUN_1402b8440</c>): every remembered level above 0, with the sections'
    /// own levels stored first, as strings <c>craft partial item sid &lt;k&gt;</c> (the item), floats <c>craft partial item val &lt;k&gt;</c> and the
    /// int <c>craft partial item size</c> (how many).
    /// </summary>
    static void WriteCraft(FcsRecord state, Machine m, CraftState c)
    {
        foreach (var key in state.Strings.Keys.Where(k => k.StartsWith("current", StringComparison.Ordinal)).ToList())
            state.Strings.Remove(key);

        foreach (var key in state.Floats.Keys.Where(k => k.StartsWith("current prog", StringComparison.Ordinal)).ToList())
            state.Floats.Remove(key);

        foreach (var key in state.Ints.Keys.Where(k => k.StartsWith("crit", StringComparison.Ordinal)).ToList())
            state.Ints.Remove(key);

        foreach (var key in state.Strings.Keys.Where(k => k.StartsWith(PartialSid, StringComparison.Ordinal)).ToList())
            state.Strings.Remove(key);

        foreach (var key in state.Floats.Keys.Where(k => k.StartsWith(PartialVal, StringComparison.Ordinal)).ToList())
            state.Floats.Remove(key);

        state.Floats["num crafts"] = c.Queue.Count;
        state.Bools["repeat"] = c.Repeat;
        for (int k = 0; k < c.Queue.Count; k++)
        {
            string n = k.ToString(CultureInfo.InvariantCulture);
            state.Strings["current" + n] = c.Queue[k].Option.Item;
            state.Strings["currentExtraData" + n] = c.Queue[k].Option.Model ?? "";
            state.Floats["current prog" + n] = c.Queue[k].Progress;
            state.Ints["crit" + n] = c.Queue[k].Critical;
        }

        WritePartial(state, m, c);
    }

    /// <summary>
    /// The partly drawn items of a craft: the sections' levels above 0 over the remembered ones, then the remembered ones still above 0.
    /// </summary>
    static void WritePartial(FcsRecord state, Machine m, CraftState c)
    {
        var partial = new Dictionary<string, float>(c.Partial, StringComparer.Ordinal);
        foreach (var input in m.Inputs)
        {
            if (input.Level > 0)
                partial[input.Item] = input.Level;
            else
                partial.Remove(input.Item);
        }

        int written = 0;
        foreach (var (item, level) in partial)
        {
            if (level <= 0) continue;

            string n = written++.ToString(CultureInfo.InvariantCulture);
            state.Strings[PartialSid + n] = item;
            state.Floats[PartialVal + n] = level;
        }

        state.Ints["craft partial item size"] = written;
    }

    /// <summary>
    /// The machine's items as saved: each input under <c>in&lt;k&gt;</c>, the output under <c>out</c>, then the crafting bench's finished items.
    /// </summary>
    static List<SaveItem> Items(Machine m)
    {
        var list = new List<SaveItem>();
        for (int k = 0; k < m.Inputs.Length; k++)
        {
            if (m.Inputs[k].Count > 0)
            {
                list.Add(new SaveItem { BaseId = m.Inputs[k].Item, Section = "in" + k.ToString(CultureInfo.InvariantCulture),
                    Quantity = m.Inputs[k].Count });
            }
        }

        if (m.Output is { Count: > 0 } o) list.Add(new SaveItem { BaseId = o.Recipe.Item, Section = "out", Quantity = o.Count });
        if (m.Craft is { } c) list.AddRange(c.Done.Select(SavedKit.FromItem));
        if (m.Storage is { } s)
        {
            list.AddRange(m.Data.IsFurnace
                ? s.Items.Select(i => SavedKit.FromItem(i, FurnaceSection))
                : SavedKit.FromInventory(s));
        }

        return list;
    }

    /// <summary>
    /// Reads the machines of the player buildings the world holds (after <see cref="BuildingSave.Read"/>); an unfinished one keeps its stock too (an
    /// upgrade being built).
    /// </summary>
    public static void Read(SaveGame save, World world, ProductionSystem production, ItemFactory factory)
    {
        var states = HeldSave.BuildingStates(save);
        foreach (var site in world.Buildings.All)
        {
            if (site.Owner != world.Player.Faction || production.Data(site.Record) is null) continue;
            if (production.Ensure(world, site) is not { } m) continue;

            if (states.TryGetValue(BuildingKey.Of(site.Zone, site.Id), out var state))
            {
                ReadKeys(state, m);
                if (m.Craft is not null) ReadCraft(state, m, production);
            }

            ReadInventory(save, site, m, factory);
        }

        world.Machines.Touch();
    }

    /// <summary>
    /// Puts the building's stored items back into its machine: the outputs, the inputs and the storage grid.
    /// </summary>
    static void ReadInventory(SaveGame save, BuildSite site, Machine m, ItemFactory factory)
    {
        if (!save.Zones.TryGetValue(site.Zone, out var zone) || zone.BuildingList?.Instances.FirstOrDefault(i => i.Id == site.Id) is not { } instance)
            return;

        var inventory = instance.States
            .Select(id => zone.File.Records.FirstOrDefault(r => r.StringId == id && r.RecordType == FcsRecordType.InventoryState))
            .FirstOrDefault(r => r is not null);
        if (inventory is null) return;

        var storage = new List<SaveItem>();
        foreach (var item in SaveInventory.Read(zone.File, inventory))
        {
            if (item.Section == "out" && m.Craft is { } craft)
            {
                if (SavedKit.ItemOf(item, factory) is { } done) craft.Done.Add(done);
            }
            else if (item.Section == "out" && m.Output is { } o && o.Recipe.Item == item.BaseId)
            {
                o.Count = Math.Min(o.Recipe.Capacity, o.Count + item.Quantity);
            }
            else if (item.Section.StartsWith("in", StringComparison.Ordinal)
                && int.TryParse(item.Section.AsSpan(2), CultureInfo.InvariantCulture, out int k)
                && k < m.Inputs.Length && (m.Inputs[k].Item == item.BaseId || m.Craft is not null && m.Inputs[k].Count == 0))
            {
                // A crafting bench's sections hold its last craft's ingredients, also with nothing queued.
                m.Inputs[k].Item = item.BaseId;
                m.Inputs[k].Count = Math.Min(m.Inputs[k].Recipe.Capacity, m.Inputs[k].Count + item.Quantity);
            }
            else
            {
                storage.Add(item);
            }
        }

        if (m.Storage is { } grid && storage.Count > 0)
        {
            foreach (var item in SavedKit.ToInventory(storage, factory).Items)
            {
                if (m.Data.IsFurnace) item.Section = "main";   // its in1 is Meitou's storage grid

                // A grid that is now smaller (a storage sized from its one item) gets an item that no longer fits at the next free cell, as the
                // original's loader does.
                bool fits = item.X >= 0 && item.Y >= 0 && item.X + item.Width <= grid.Width && item.Y + item.Height <= grid.Height
                    && grid.IsFree(item.Section, item.X, item.Y, item.Width, item.Height);
                if (fits || grid.Add(item) is false && item.Quantity > 0) grid.Items.Add(item);
            }
        }
    }

    /// <summary>
    /// Reads a crafting bench's repeat flag, remembered ingredient levels and finished crafts.
    /// </summary>
    static void ReadCraft(FcsRecord state, Machine m, ProductionSystem production)
    {
        m.Craft!.Repeat = state.Bools.GetValueOrDefault("repeat");

        // The remembered ingredient levels, read before any bind so the sections keep their saved levels (consumption amount-k).
        for (int k = 0, size = state.Ints.GetValueOrDefault("craft partial item size"); k < size; k++)
        {
            string p = k.ToString(CultureInfo.InvariantCulture);
            if (state.Strings.TryGetValue(PartialSid + p, out var sid))
                production.RestorePartial(m, sid, state.Floats.GetValueOrDefault(PartialVal + p));
        }

        int count = (int)state.Floats.GetValueOrDefault("num crafts");
        for (int k = 0; k < count; k++)
        {
            string n = k.ToString(CultureInfo.InvariantCulture);
            if (!state.Strings.TryGetValue("current" + n, out var item)) continue;

            string? model = state.Strings.GetValueOrDefault("currentExtraData" + n) is { Length: > 0 } x ? x : null;
            byte critical = (byte)Math.Clamp(state.Ints.GetValueOrDefault("crit" + n, 2), 0, 2);
            production.RestoreCraft(m, item, model, state.Floats.GetValueOrDefault("current prog" + n), critical);
        }
    }

    /// <summary>
    /// Reads a machine's production, power, battery, input and farm keys from its building state (a missing key leaves the default).
    /// </summary>
    static void ReadKeys(FcsRecord state, Machine m)
    {
        if (state.Floats.TryGetValue("resource mult", out float y)) m.Yield = y;

        m.PowerOn = state.Bools.GetValueOrDefault("power on", true);
        m.GetsBattery = state.Bools.GetValueOrDefault("gets battery", true);
        m.Broken = state.Bools.GetValueOrDefault("broken");
        if (m.Data.IsBattery) m.Charge = Math.Max(0, state.Floats.GetValueOrDefault("batterycharge"));
        if (m.Output is { } o) o.Progress = state.Floats.GetValueOrDefault("production amount");
        for (int k = 0; k < m.Inputs.Length; k++)
            m.Inputs[k].Level = state.Floats.GetValueOrDefault("consumption amount-" + k.ToString(CultureInfo.InvariantCulture));

        if (m.Data.IsFurnace) m.BurnLevel = state.Floats.GetValueOrDefault("consumption amount-0");
        if (m.Farm is not { } f || !state.Floats.ContainsKey("plant growth")) return;

        f.Growth = state.Floats.GetValueOrDefault("plant growth");
        f.Death = state.Floats.GetValueOrDefault("plant death");
        f.Start = state.Floats.GetValueOrDefault("plant start");
        f.Harvested = Math.Clamp(state.Ints.GetValueOrDefault("plants harvested"), 0, f.Plants.Length);
        if (state.Floats.TryGetValue("save time", out float saved)) f.LiveHours = saved;
        for (int i = 0; i < f.Plants.Length; i++) f.Plants[i] = i < f.Harvested ? -10 : f.Growth;
    }
}
