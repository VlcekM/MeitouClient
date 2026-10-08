using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>
/// A character's inventory: its items in sections, with their grid cells. The main section is a grid of <see cref="MainWidth"/> x <see cref="MainHeight"/> cells (an engine
/// choice: the size of a character's own pockets is <b>Unknown</b>); a backpack's contents are an inventory of the CONTAINER's storage size. Only serial phases change it.
/// </summary>
public sealed class Inventory(int width, int height)
{
    public const int MainWidth = 8, MainHeight = 6;

    public Inventory() : this(MainWidth, MainHeight) { }

    public int Width { get; } = width;
    public int Height { get; } = height;
    public List<ItemInstance> Items { get; } = [];

    /// <summary>The worn backpack, if any.</summary>
    public ItemInstance? Backpack => Items.Find(i => i.Section == "backpack_attach");

    /// <summary>The weight of everything in this inventory (not including a carried person).</summary>
    public float ContentWeight()
    {
        float w = 0;
        foreach (var i in Items) w += i.Weight;
        return w;
    }

    /// <summary>First cell where a w x h footprint fits among the items of <paramref name="section"/>, or false.</summary>
    public bool FindCell(string section, int w, int h, out int x, out int y)
    {
        for (y = 0; y + h <= Height; y++)
            for (x = 0; x + w <= Width; x++)
                if (Free(section, x, y, w, h)) return true;
        x = y = 0;
        return false;
    }

    bool Free(string section, int x, int y, int w, int h)
    {
        foreach (var i in Items)
        {
            if (i.Section != section) continue;
            if (x < i.X + i.Width && i.X < x + w && y < i.Y + i.Height && i.Y < y + h) return false;
        }
        return true;
    }

    /// <summary>
    /// Puts an item in: a stackable item joins a stack of the same record and quality; otherwise it takes the first free cell of <c>main</c>, then of the backpack. False when
    /// there is no room (the caller drops the item).
    /// </summary>
    public bool Add(ItemInstance item)
    {
        if (item.Stackable)
            foreach (var other in Items)
                if (other.Record == item.Record && other.Stackable && other.Quality == item.Quality && other.Section is "main" or "backpack_content")
                {
                    other.Quantity += item.Quantity;
                    return true;
                }
        if (FindCell("main", item.Width, item.Height, out int x, out int y))
        {
            item.Section = "main";
            item.X = x;
            item.Y = y;
            Items.Add(item);
            return true;
        }
        if (Backpack?.Contents is { } bag && bag.FindCell("backpack_content", item.Width, item.Height, out x, out y))
        {
            item.Section = "backpack_content";
            item.X = x;
            item.Y = y;
            bag.Items.Add(item);
            return true;
        }
        return false;
    }

    /// <summary>Every item of this inventory and of the backpack, in order.</summary>
    public IEnumerable<ItemInstance> All()
    {
        foreach (var i in Items)
        {
            yield return i;
            if (i.Contents is { } bag) foreach (var inner in bag.All()) yield return inner;
        }
    }

    /// <summary>Removes one unit of an item (the whole item when it is the last), wherever it is.</summary>
    public bool RemoveOne(ItemInstance item)
    {
        if (Items.Contains(item))
        {
            if (item.Quantity > 1) item.Quantity--;
            else Items.Remove(item);
            return true;
        }
        foreach (var i in Items)
            if (i.Contents is { } bag && bag.RemoveOne(item)) return true;
        return false;
    }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Items.Count);
        foreach (var i in Items)
        {
            h.Add(Rng.StableHash(i.Record));
            h.Add(Rng.StableHash(i.Section));
            h.Add(i.X);
            h.Add(i.Y);
            h.Add(i.Quantity);
            h.Add(i.Level);
            h.Add(i.Quality);
            h.Add(i.Charges);
            i.Contents?.Hash(ref h);
        }
    }
}
