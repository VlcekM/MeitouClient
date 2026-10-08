using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>A text listing of an inventory for the debug HUD.</summary>
public static class InventoryText
{
    public static IReadOnlyList<string> Lines(Inventory inventory)
    {
        var lines = new List<string> { $"Carries {inventory.ContentWeight():0.0} kg" };
        foreach (var i in inventory.All())
            lines.Add($"{(i.Quantity > 1 ? i.Quantity + " x " : "")}{i.Name} ({i.Section} {i.X},{i.Y}){(i.IsFood ? $" food {i.Nutrition:0.00}" : "")}");
        return lines;
    }
}
