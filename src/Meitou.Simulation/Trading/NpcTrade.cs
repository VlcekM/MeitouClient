using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using TownBuilding = Meitou.Data.World.TownBuilding;

namespace Meitou.Simulation.Trading;

/// <summary>
/// What one purchase attempt of a shopper came to (<see cref="NpcTrade.Buy"/>).
/// </summary>
public enum NpcSale : byte
{
    /// <summary>
    /// No seller of the building has an item of the wanted function.
    /// </summary>
    NoItem,
    /// <summary>
    /// The buyer could not pay or had no room: nothing moved (the unit still counts as bought on the shopping list).
    /// </summary>
    Failed,
    /// <summary>
    /// One unit changed hands.
    /// </summary>
    Sold,
}

/// <summary>
/// An NPC buying one unit from a building's stock (docs/game/ai-tasks.md goal 118 "Buying", docs/game/economy.md "NPC purchases"): the sale
/// <c>FUN_14074a630</c> that <c>FUN_1409a14b0</c> makes for <c>Task_Shopping</c>. Changes shared state, so it runs in a serial step only.
/// <list type="bullet">
/// <item><b>Sellers</b>: the containers of the building (<b>Observed</b>, <c>FUN_14054add0</c> / <c>FUN_1409a14b0</c> list only container objects,
/// never a trader's carried stock): the building's stock containers, a shop's or a house's. The first with an item of the function sells a random
/// one of them. Engine choice: a unique item (an artifact) is never sold.</item>
/// <item><b>Price</b> (<b>Verified</b>): the item's stack price (slot 82) of one unit, with the sell flag set only for a buyer of the player's
/// faction. With no trade window open the trader factor is 1 and no town's market factor applies (<b>Observed</b>), so it is
/// <see cref="Market.StackPrice"/> with <see cref="Trader.None"/>.</item>
/// <item><b>Money</b> (<b>Verified</b>): from the buyer's squad purse (the player's purse for a buyer of the player's faction; none: 0) to the squad
/// that owns the stock. The buyer must hold the price in full.</item>
/// <item><b>The item</b> (<b>Verified</b>): when the stock's owner is not of the player's faction and a roll is above <see cref="KeepChance"/> the
/// unit is paid for and leaves the stock without going anywhere; otherwise it goes into the buyer's inventory, and with no room the sale is called
/// off.</item>
/// </list>
/// </summary>
public static class NpcTrade
{
    /// <summary>
    /// The roll an NPC seller's unit must not exceed for the buyer to keep it (<c>DAT_1416885d8</c>, <b>Verified</b>: 0.2).
    /// </summary>
    public const float KeepChance = 0.2f;

    /// <summary>
    /// Tries to buy one unit of an item of the item function <paramref name="function"/> in <paramref name="building"/> for the character in slot
    /// <paramref name="buyer"/>. <paramref name="roll"/> picks the item and decides whether it is kept.
    /// </summary>
    public static NpcSale Buy(World world, Market market, int playerFaction, int buyer, TownBuilding building, int function, ulong roll)
    {
        var table = world.Characters;
        if (table.Cold(buyer) is not { } cold) return NpcSale.NoItem;

        foreach (var (stock, owner) in Sellers(world, building))
        {
            int count = 0;
            foreach (var item in stock.Items)
                if (Offered(market, item, function)) count++;
            if (count == 0) continue;

            int pick = Rng.Int(roll, count);
            ItemInstance? chosen = null;

            foreach (var item in stock.Items)
            {
                if (Offered(market, item, function) && pick-- == 0)
                {
                    chosen = item;
                    break;
                }
            }

            if (chosen is null) continue;

            bool player = playerFaction >= 0 && cold.Faction == playerFaction;
            var purse = player ? null : world.Squads.Find(cold.SquadId);
            var unit = chosen.WithQuantity(1);
            long price = market.StackPrice(unit, player, Trader.None, owner?.Faction ?? -1);
            if (world.TradePriceFilters.Active)
                price = world.TradePriceFilters.Pass(world, new TradeDeal(world.Tick, unit.Record, 1, false, table.IdOf(buyer), owner?.Id ?? -1), price);
            if ((player ? world.Player.Money : purse?.Money ?? 0) < price) return NpcSale.Failed;

            bool toBuyer = owner is { } own && playerFaction >= 0 && own.Faction == playerFaction || Rng.Float(Rng.Mix(roll)) <= KeepChance;
            if (toBuyer && (cold.Inventory is null || TradeSystem.Place(cold.Inventory, unit, 1) == 0))
                return NpcSale.Failed;

            TradeSystem.Take(stock, chosen, 1);
            if (player) world.Player.Money -= price;
            else if (purse is not null) purse.Money -= price;
            if (owner is not null) owner.Money += price;

            // FUN_1409a14b0: a sale worth more than nothing floats the price over the shopper and posts Sell_Item on it (both only while the interface
            // is shown, which the host decides).
            if (price > 0) Announce(world, buyer, price);
            return NpcSale.Sold;
        }

        return NpcSale.NoItem;
    }

    /// <summary>
    /// The price floater over the shopper and its <c>Sell_Item</c> (<see cref="SoundKind.NpcBought"/>). Display only.
    /// </summary>
    static void Announce(World world, int buyer, long price)
    {
        var at = world.Characters.Next[buyer].Position;
        world.Floaters?.Enqueue(Floater.Price(world.Characters.IdOf(buyer), at, price));
        world.EmitSound(SoundKind.NpcBought, buyer, at);
    }

    /// <summary>
    /// Whether the item is offered in an NPC trade: not unique, no full bag, and of item function <paramref name="function"/>.
    /// </summary>
    static bool Offered(Market market, ItemInstance item, int function) =>
        !item.Unique && item.Contents is not { Items.Count: > 0 } && market.Items.Info(item.Record)?.Function == function;

    /// <summary>
    /// The building's stock containers in its order, each with the squad that owns what it holds: the squad with a building shop living in it, else
    /// the first squad living in it (none: the money goes nowhere; the original pays the owner faction).
    /// </summary>
    static IEnumerable<(Inventory Stock, Squad? Owner)> Sellers(World world, TownBuilding building)
    {
        Squad? resident = null, shop = null;

        foreach (var squad in world.Squads.All)
        {
            if (!ReferenceEquals(squad.Ai?.OwnHome ?? squad.HomeBuilding, building)) continue;

            resident ??= squad;
            if (squad.Shop is { Building: not null }) shop ??= squad;
        }

        foreach (var f in building.Fixtures)
        {
            if (f.HoldsShopStock && world.Fixtures.Find(f.Id)?.Contents is { } box) yield return (box, shop ?? resident);
        }
    }
}
