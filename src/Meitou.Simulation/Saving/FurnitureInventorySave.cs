using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The contents of a town building's storage furniture in a save as the original keeps them (docs/formats/save.md "Zone files", furniture inventories): a
/// generated piece of furniture is an entry of its zone's building list (<see cref="SavedPieces"/>, matched by record and position), and its items
/// lie in the INVENTORY_STATE that entry lists, section <c>backpack_content</c>. A container whose house state has no
/// <c>meitou container &lt;i&gt;</c> key (<see cref="ContainerSave"/>) takes them when it registers, and a save writes the container back into that
/// entry, the key removed; an entry whose items are as they were read stays as it was. Read only from a zone that is not loaded fresh and a house
/// with <c>serialised interior</c> 1 (otherwise the original makes the interior again from its layout and its pieces start empty).
/// </summary>
public static class FurnitureInventorySave
{
    /// <summary>
    /// The house state key that stops the building factory from making its interior again (docs/formats/save.md "Zone files", <b>Verified</b>).
    /// </summary>
    public const string SerialisedInterior = "serialised interior";

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
    /// Writes each registered town container that has an entry of the original's into that entry's inventory (after the fixtures' own keys,
    /// <c>SaveCapture.WriteLocks</c>), and removes the container's <c>meitou container</c> key from its house state; a home an override furnished anew
    /// is left to <see cref="RuinSave"/>, which removes its entries.
    /// </summary>
    public static void Write(SaveGame game, World world, IReadOnlyDictionary<string, FcsRecord> buildings)
    {
        SavedPieces? pieces = null;
        foreach (string building in world.Fixtures.Buildings)
        {
            if (world.Fixtures.IsRebuilt(building) || buildings.GetValueOrDefault(building) is not { } house || !Keeps(house.Ints)) continue;

            foreach (var f in world.Fixtures.OfBuilding(building))
            {
                if (f.Contents is not { } grid || ContainerSave.KeyOf(f, building) is not { } key) continue;

                pieces ??= new SavedPieces(game);
                if (pieces.Of(f.Id, f.RecordId, f.Position) is not { Fresh: false } entry) continue;

                WriteInto(entry, f, grid);
                house.Strings.Remove(key);
            }
        }
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
}
