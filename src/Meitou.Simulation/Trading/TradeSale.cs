namespace Meitou.Simulation.Trading;

/// <summary>
/// How a deal was made (<see cref="TradeSale"/>).
/// </summary>
public enum TradeSaleKind : byte
{
    /// <summary>
    /// A shopper bought one unit from a building's stock or a shop counter the player built (<see cref="NpcTrade.Buy"/>).
    /// </summary>
    Shopping,
    /// <summary>
    /// The customer bought from the trader's shop in the trade window (a click, or an item dragged out of the shop).
    /// </summary>
    WindowBuy,
    /// <summary>
    /// The customer sold to the trader's shop in the trade window (a click, or an item dragged into the shop).
    /// </summary>
    WindowSell,
}

/// <summary>
/// One side of a deal: the character (none for a container's stock), its squad (-1 none) and its faction (-1 none).
/// </summary>
public readonly record struct TradeParty(CharacterId Character, int Squad, int Faction);

/// <summary>
/// A completed deal (<see cref="ITradeObserver"/>): the world's tick, how it was made, the item record's string id, the units, the price paid, the
/// buyer and the seller, the site the goods were sold from ("" for the trade window, whose stock is the trader's shop), whether the goods reached
/// the buyer (a shopper's unit can be paid for and leave the stock without reaching it), and whether it undid an earlier deal of the open trade
/// window at that deal's own coins (a buy-back: the item went back the way it came).
/// </summary>
public readonly record struct TradeSale(long Tick, TradeSaleKind Kind, string Record, int Units, long Price, TradeParty Buyer, TradeParty Seller,
    string Site, bool Delivered, bool Buyback);

/// <summary>
/// A system that hears of every completed deal (docs/modding.md "Sales"): a shopper's purchase from a building's stock or a shop counter the player
/// built, and the trade window's buys and sells, buy-backs included (<see cref="TradeSale.Buyback"/>). Called serially, in system order, once the
/// money and the goods have moved; a trade made in the paused interface is told between ticks on the host's thread, with the coming tick's number.
/// It may read the world and change its own state, and must not change items, characters or other world state in the call (gameplay changes go
/// through commands). One that throws is disabled for that world, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface ITradeObserver : ITickSystem
{
    /// <summary>
    /// A deal was made.
    /// </summary>
    void Traded(World world, in TradeSale sale);
}
