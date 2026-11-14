using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Data.Save;
using Meitou.Simulation.Items;
using Meitou.Simulation.Saving;

namespace Meitou.Simulation.Locks;

/// <summary>
/// A container's contents in a save (docs/game/locks.md "Containers"): Meitou's own GAMESTATE_BUILDING string <c>meitou container &lt;layout
/// index&gt;</c> (the index of <see cref="Fixture.Id"/>, whose interior ids the original's keys cannot name), a JSON list of the items with their
/// cell, quality, level, material, maker, charge, colour, unique flag and turn (the flags only when set) and a bag's own contents. Present only while
/// the container holds something. A shackle's lock and a prosthetic's wear are not kept (they are not stored in chests).
/// </summary>
static class ContainerSave
{
    /// <summary>
    /// The key prefix of a container's contents (followed by its layout index).
    /// </summary>
    public const string KeyPrefix = "meitou container ";

    /// <summary>
    /// One saved item: record, quantity, cell, quality, level, material, maker, charges, section, a bag's contents, unique flag, colour and whether
    /// it lies turned (<see cref="ItemInstance.Rotated"/>).
    /// </summary>
    sealed record Entry(string R, int Q, int X, int Y, float Ql, int Lv, string M, string C, float Ch, string S, List<Entry>? I,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool U = false, string? Co = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool T = false);

    /// <summary>
    /// The key of the container <paramref name="f"/> of <paramref name="building"/>, or null when it is not an interior item of it.
    /// </summary>
    public static string? KeyOf(Fixture f, string building) =>
        f.Id.Length > building.Length + 1 && f.Id[building.Length] == '/' && f.Id.StartsWith(building, StringComparison.Ordinal) ? KeyPrefix
                + f.Id[(building.Length + 1)..] : null;

    /// <summary>
    /// The contents as text; null when the grid is empty.
    /// </summary>
    public static string? Write(Inventory grid) => grid.Items.Count == 0 ? null : JsonSerializer.Serialize(Entries(grid));

    /// <summary>
    /// The saved entries of a grid's items, a bag's contents nested.
    /// </summary>
    static List<Entry> Entries(Inventory grid)
            => [.. grid.Items.Select(i
                    => new Entry(i.Record, i.Quantity, i.X, i.Y, i.Quality, i.Level, i.MaterialId, i.CompanyId, i.Charges, i.Section,
        i.Contents is { Items.Count: > 0 } bag ? Entries(bag) : null, i.Unique, i.ColourId.Length > 0 ? i.ColourId : null, i.Rotated))];

    /// <summary>
    /// Puts the saved items into <paramref name="grid"/>; one whose cell is no longer free (a changed grid) goes to the first that is, dropped when
    /// none is or when the data lacks its record.
    /// </summary>
    public static void Read(string text, Inventory grid, ItemFactory factory)
    {
        List<Entry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<Entry>>(text);
        }
        catch (JsonException)
        {
            return;
        }

        if (entries is not null) Fill(entries, grid, factory, topLevel: true);
    }

    /// <summary>
    /// Puts saved entries into a grid (a bag's into the bag), each in its cell else the first free one; dropped when none is free or the data lacks
    /// its record.
    /// </summary>
    static void Fill(List<Entry> entries, Inventory grid, ItemFactory factory, bool topLevel)
    {
        foreach (var e in entries)
        {
            var saved = new SaveItem { BaseId = e.R, Quantity = e.Q, X = e.X, Y = e.Y, Quality = e.Ql, Level = e.Lv, MaterialId = e.M ?? "",
                    CompanyId = e.C ?? "", Charges = e.Ch, ColourId = e.Co ?? "", Section = topLevel ? "main" : e.S ?? "main", Unique = e.U,
                    Rotated = e.T };
            if (SavedKit.ItemOf(saved, factory) is not { } item) continue;
            if (e.I is { } inner && item.Contents is { } bag) Fill(inner, bag, factory, topLevel: false);
            if (topLevel && !grid.IsFree("main", item.X, item.Y, item.Width, item.Height))
            {
                grid.AddTo("main", item);
                continue;
            }

            grid.Items.Add(item);
        }
    }
}