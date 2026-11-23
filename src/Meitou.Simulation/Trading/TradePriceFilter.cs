namespace Meitou.Simulation.Trading;

/// <summary>
/// A price about to be asked (<see cref="ITradePriceFilter"/>): the world's tick, the item record's string id, the units, whether the customer
/// sells to the shop (else buys from it), the customer (the player's character in the trade window, or an NPC shopper buying from a building's
/// stock) and the shop's squad (the trader's, or the squad living in the shop building; -1 none).
/// </summary>
public readonly record struct TradeDeal(long Tick, string Record, int Units, bool Sell, CharacterId Customer, int ShopSquad)
{
    /// <summary>
    /// The item priced (its <see cref="Items.ItemInstance.ModTag"/>, quality, level and maker among it): the stack in the window or the shop's
    /// unit; null for a deal built without one. A filter reads it and must not change it.
    /// </summary>
    public Items.ItemInstance? Item { get; init; }
}

/// <summary>
/// A system that adjusts trade prices (docs/modding.md "Trade prices"): asked for every price the game asks (the trade window's stack and unit
/// prices, the coins it takes, an NPC shopper's purchase), serially on the tick thread or the publish, in system order, each filter seeing the
/// price as the earlier ones left it. A buy-back (undoing a trade of the open window) keeps its coins and is not asked. It must be a pure read:
/// its answer follows from the world and the deal alone, since the same price is asked again for the window and the payment. A filter that
/// throws or answers a negative price is undone for that price and disabled for that world, and the fault enters the state hash. Engine API,
/// not in the original.
/// </summary>
public interface ITradePriceFilter : ITickSystem
{
    /// <summary>
    /// The price for <paramref name="deal"/>, given <paramref name="price"/> as the game and the earlier filters made it.
    /// </summary>
    long Price(World world, in TradeDeal deal, long price);
}
