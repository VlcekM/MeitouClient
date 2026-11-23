namespace Meitou.Simulation.Items;

/// <summary>
/// Where a new item came from (<see cref="ItemCreated.Kind"/>); room for more kinds.
/// </summary>
public enum ItemCreatedKind : byte
{
    /// <summary>
    /// Loot the world lays out: a camp's ground items (its <c>debris</c> and <c>loot spawn</c> lists) and a town's artifacts put in its buildings'
    /// containers.
    /// </summary>
    Loot,
    /// <summary>
    /// A crafting bench's finished item, before it joins the output (<see cref="ItemCreated.Character"/> the crafter).
    /// </summary>
    Crafted,
    /// <summary>
    /// A shop's fill (its first fill and every restock), each unit and special item before it is placed (<see cref="ItemCreated.Squad"/> the shop's
    /// squad).
    /// </summary>
    ShopStock,
    /// <summary>
    /// A new character's starting kit (worn, held and carried, the contents of what it carries too), once it is in the world
    /// (<see cref="ItemCreated.Character"/> the character).
    /// </summary>
    SpawnEquipment,
}

/// <summary>
/// An item the world just made (<see cref="IItemCreatedObserver"/>): the tick, the kind, the item itself, the character it is for or by (none) and
/// the squad (-1 none).
/// </summary>
public readonly record struct ItemCreated(long Tick, ItemCreatedKind Kind, ItemInstance Item, CharacterId Character, int Squad);

/// <summary>
/// A system told of every item the world makes of a <see cref="ItemCreatedKind"/> (docs/modding.md "Items created"), so a mod can tag or roll it:
/// it may set the item's <see cref="ItemInstance.ModTag"/> (and its own hashed state), and must not change other items, characters or world state.
/// Called serially in system order, in the step that made the item; a shop's units are told before they are stacked, so units tagged apart stay
/// apart. One that throws is disabled for that world, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IItemCreatedObserver : ITickSystem
{
    /// <summary>
    /// An item was made.
    /// </summary>
    void Created(World world, in ItemCreated item);
}

/// <summary>
/// The world's item-created observers (<see cref="IItemCreatedObserver"/>); serial phases only.
/// </summary>
sealed class ItemCreatedObservers
{
    /// <summary>
    /// The registered observers.
    /// </summary>
    readonly SerialHooks<IItemCreatedObserver> _hooks = new(0x4954454D, "item created observer");

    /// <summary>
    /// Whether any observer is still told; a site skips walking its items without one.
    /// </summary>
    internal bool Active => _hooks.Active;

    /// <summary>
    /// Registers <paramref name="system"/> when it is an observer.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex) => _hooks.Register(system, systemIndex);

    /// <summary>
    /// Adds the disabled observers to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// Tells every enabled observer of <paramref name="item"/>; nothing without one. Serial phases.
    /// </summary>
    internal static void Report(World world, ItemCreatedKind kind, ItemInstance item, CharacterId character, int squad = -1)
    {
        var observers = world.ItemCreatedObservers;
        if (!observers.Active) return;

        observers.Observe(world, new ItemCreated(world.Tick, kind, item, character, squad));
    }

    /// <summary>
    /// Tells of each item of <paramref name="items"/> and of the items inside them, in order.
    /// </summary>
    internal static void ReportAll(World world, ItemCreatedKind kind, IEnumerable<ItemInstance> items, CharacterId character, int squad = -1)
    {
        if (!world.ItemCreatedObservers.Active) return;

        foreach (var item in items)
        {
            Report(world, kind, item, character, squad);
            if (item.Contents is { } inside) ReportAll(world, kind, inside.Items, character, squad);
        }
    }

    /// <summary>
    /// Passes one item to every enabled observer, disabling one that throws.
    /// </summary>
    void Observe(World world, in ItemCreated item)
    {
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (!_hooks.Enabled(i)) continue;

            try
            {
                _hooks[i].Created(world, item);
            }
            catch (Exception e)
            {
                _hooks.Threw(i, e);
            }
        }
    }
}
