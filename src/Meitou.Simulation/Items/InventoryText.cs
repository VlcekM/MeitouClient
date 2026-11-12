using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>
/// A text listing of an inventory for the debug HUD.
/// </summary>
public static class InventoryText
{
    /// <summary>
    /// The debug lines: the carried weight, then one line per item (quantity, name, section and cell, the nutrition of food).
    /// </summary>
    public static IReadOnlyList<string> Lines(Inventory inventory)
    {
        var lines = new List<string> { $"Carries {inventory.ContentWeight():0.0} kg" };
        foreach (var i in inventory.All())
        {
            string quantity = i.Quantity > 1 ? i.Quantity + " x " : "";
            lines.Add($"{quantity}{i.Name} ({i.Section} {i.X},{i.Y}){(i.IsFood ? $" food {i.Nutrition:0.00}" : "")}");
        }
        return lines;
    }
}