using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The contents of a town building's storage furniture in a save as the original keeps them (docs/formats/save.md "Zone files", furniture inventories): a
/// generated piece of furniture is an entry of its zone's building list (<see cref="SavedPieces"/>, matched by record and position), and its items
/// lie in the INVENTORY_STATE that entry lists, section <c>backpack_content</c>. A container whose house state has no
/// <c>meitou container &lt;i&gt;</c> key (<see cref="ContainerSave"/>) takes them when it registers, and a save writes the container back into that
/// entry, the key removed; an entry whose items are as they were read stays as it was. Read only from a zone that is not loaded fresh and a house
/// with <c>serialised interior</c> 1 (otherwise the original makes the interior again from its layout and its pieces start empty), never into a
/// bought house (whose entries a save removes), an object a town machine owns or a building the level editor places. What was read only mirrors
/// the save (<see cref="Fixture.LoadedContents"/>): no simulation step reads it.
/// </summary>
public static class FurnitureInventorySave
{
    /// <summary>
    /// The house state key that stops the building factory from making its interior again (docs/formats/save.md "Zone files", <b>Verified</b>).
    /// </summary>
    public const string SerialisedInterior = "serialised interior";

    /// <summary>
    /// The prefix of a building state's handles of the objects inside it.
    /// </summary>
    const string InternalPrefix = "internalbuildings-";

    /// <summary>
    /// Whether a house's state keeps its saved interior (<see cref="SerialisedInterior"/> 1).
    /// </summary>
    public static bool Keeps(IReadOnlyDictionary<string, int> houseInts) => houseInts.GetValueOrDefault(SerialisedInterior) == 1;

    /// <summary>
    /// Puts the items of the original's entry <paramref name="entry"/> into a container's grid at their saved cells (what finds no room is lost, as the
    /// original's loader) and returns them as a save would write them now, for <see cref="Write"/>; null when the entry lists no inventory or its
    /// zone loads fresh.
    /// </summary>
    internal static List<SaveItem>? Read(SavedPiece entry, Inventory grid, ItemFactory factory)
    {
        if (entry.Fresh || MachineSave.ListedInventory(entry.Zone.File, entry.Instance) is not { } inventory) return null;

        MachineSave.PutInGrid(grid, SaveInventory.Read(entry.Zone.File, inventory), factory, furnace: false);
        return [.. MachineSave.StorageItems(grid)];
    }

    /// <summary>
    /// Whether the record's object is a town machine's (a farm, a producer such as the Rain Collector, or a battery bank:
    /// <c>ProductionSystem.IsPieceMachine</c>), whose machine reads and writes the entry's inventory (<see cref="TownMachineSave"/>).
    /// </summary>
    public static bool MachineOwns(GameDatabase db, string recordId) =>
        db.Find(recordId) is { } record && MachineData.From(db, record) is { } m && (m.Farm is not null || m.IsProducer || m.IsBattery);

    /// <summary>
    /// Writes each registered town container that has an entry of the original's into that entry's inventory (after the fixtures' own keys,
    /// <c>SaveCapture.WriteLocks</c>), and removes the container's <c>meitou container</c> key from its house state; a home an override furnished anew
    /// is left to <see cref="RuinSave"/>, which removes its entries, and a bought house's furniture entries go (<see cref="DropBought"/>).
    /// </summary>
    public static void Write(SaveGame game, World world, GameDatabase db, IReadOnlyDictionary<string, FcsRecord> buildings)
    {
        SavedPieces? pieces = null;
        var bought = new Dictionary<(int C, int I), FcsRecord>();
        foreach (string building in world.Fixtures.Buildings)
        {
            if (buildings.GetValueOrDefault(building) is not { } house) continue;
            if (world.Fixtures.IsEmptied(building))
            {
                if (Hand.Read(house, "handle") is { IsNull: false } hand) bought[(hand.C, hand.I)] = house;
                continue;
            }

            if (world.Fixtures.IsRebuilt(building) || !Keeps(house.Ints)) continue;

            foreach (var f in world.Fixtures.OfBuilding(building))
            {
                if (f.Contents is not { } grid || ContainerSave.KeyOf(f, building) is not { } key || MachineOwns(db, f.RecordId)) continue;

                pieces ??= new SavedPieces(game);
                if (pieces.Of(f.Id, f.RecordId, f.Position) is not { Fresh: false } entry) continue;

                WriteInto(entry, f, grid);
                house.Strings.Remove(key);
            }
        }

        if (bought.Count == 0) return;

        var gone = DropBought(game, world, bought.Keys.ToHashSet());
        foreach (var house in bought.Values) EmptyRecord(house, gone);
    }

    /// <summary>
    /// Replaces the entry's inventory with the container's items, unless they are as they were read (<see cref="Fixture.LoadedContents"/>).
    /// </summary>
    static void WriteInto(SavedPiece entry, Fixture f, Inventory grid)
    {
        List<SaveItem> items = [.. MachineSave.StorageItems(grid)];
        if (f.LoadedContents is { } loaded && SaveInventory.SameRecords(loaded, items)) return;

        MachineSave.WriteInventory(entry.Zone.File, entry.Zone.Coordinate, entry.Instance, null, items);
    }

    /// <summary>
    /// Removes the generated entries (ids <c>&lt;n&gt;-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c>) standing inside a bought house (<c>is inside building</c>, its
    /// <c>isInsideBuilding</c> handle), with their inventories and states: its furniture (a GAMESTATE_BUILDING) and its loose-item groups (an
    /// ITEM_PLACEMENT_GROUP), as <see cref="RuinSave"/> removes a ruin's furniture. The original's purchase destroys the interior's furniture and item
    /// groups with their items (docs/game/ui-screens.md "Selected building"), and its own save of a house bought there holds none of the old
    /// interior's objects or groups, lamps and machines included (<b>Observed</b>, docs/formats/save.md "Zone files"); Meitou takes those objects,
    /// slots and machines out at the purchase. The player's own buildings inside it (<see cref="World.Buildings"/>) and level buildings placed
    /// inside it (level ids, rebuilt from the level data with their saved state) stay. Returns the removed furniture's handles (container, slot).
    /// </summary>
    static HashSet<(int C, int I)> DropBought(SaveGame game, World world, HashSet<(int C, int I)> bought)
    {
        var gone = new HashSet<(int C, int I)>();
        foreach (var zone in game.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;

            var states = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
            foreach (var r in zone.File.Records)
                if (r.RecordType is FcsRecordType.GamestateBuilding or FcsRecordType.ItemPlacementGroup) states[r.StringId] = r;

            foreach (var instance in list.Instances.ToList())
            {
                var state = instance.States.Select(id => states.GetValueOrDefault(id)).FirstOrDefault(r => r is not null);
                if (state is null || !Generated(instance.Id) || world.Buildings.Find(instance.Id) is not null || !Inside(state, bought)) continue;

                if (Hand.Read(state, "handle") is { IsNull: false } hand) gone.Add((hand.C, hand.I));
                MachineSave.WriteInventory(zone.File, zone.Coordinate, instance, null, []);
                zone.File.Records.Remove(state);
                list.Instances.Remove(instance);
            }
        }

        return gone;
    }

    /// <summary>
    /// A bought house's own record as the original saves it after the purchase (<b>Observed</b>, docs/formats/save.md "Zone files"): no
    /// <c>interior layout name</c> (empty), and only the <c>internalbuildings-&lt;k&gt;</c> handles of the objects still inside it, numbered again from 0
    /// in their order (the readers stop at the first missing number, <c>SaveZoneBuilder</c>).
    /// </summary>
    static void EmptyRecord(FcsRecord house, HashSet<(int C, int I)> gone)
    {
        house.Strings[ResidentRuins.InteriorKey] = "";

        var kept = new List<Hand>();
        for (int k = 0; house.Ints.ContainsKey(InternalKey(k) + "I"); k++)
        {
            var hand = Hand.Read(house, InternalKey(k));
            if (!gone.Contains((hand.C, hand.I))) kept.Add(hand);
        }

        var old = house.Ints.Keys.Where(key => key.StartsWith(InternalPrefix, StringComparison.Ordinal)).ToList();
        foreach (string key in old) house.Ints.Remove(key);
        for (int k = 0; k < kept.Count; k++) kept[k].Write(house, InternalKey(k));
    }

    /// <summary>
    /// The key of the <paramref name="k"/>-th <c>internalbuildings</c> handle (its five int keys add <c>C</c>, <c>CS</c>, <c>I</c>, <c>S</c>, <c>TYPE</c>).
    /// </summary>
    static string InternalKey(int k) => InternalPrefix + k.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether an entry was made in play (<c>&lt;n&gt;-zone&lt;X&gt;.&lt;Y&gt;-INGAME</c>), as the generated furniture's entries are; a level building's id is
    /// its level id.
    /// </summary>
    static bool Generated(string id) => Construction.BuildingRegistry.TryParseId(id, out _, out _);

    /// <summary>
    /// Whether a building state stands inside one of <paramref name="houses"/>.
    /// </summary>
    static bool Inside(FcsRecord state, HashSet<(int C, int I)> houses) =>
        state.Bools.GetValueOrDefault("is inside building") && Hand.Read(state, "isInsideBuilding") is { IsNull: false } parent
        && houses.Contains((parent.C, parent.I));
}
