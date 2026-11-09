namespace Meitou.Simulation.Items;

/// <summary>
/// Transient trade-window lifetime; closing it invalidates even receipts on moved items. Never saved.
/// </summary>
internal sealed class TradeSession
{
    public bool Active { get; set; } = true;
}

/// <summary>
/// Exact paid coins per unit, retained through stack splits. Sell is the direction that undoes the trade.
/// </summary>
internal sealed record TradeReceipt(TradeSession Session, bool Sell, long[] Coins)
{
    public long Price(int units) => Coins.Take(units).Sum();
    public TradeReceipt Take(int units) => this with { Coins = Coins.Take(units).ToArray() };
    public TradeReceipt Skip(int units) => this with { Coins = Coins.Skip(units).ToArray() };
    /// <summary>
    /// Units of the same trade and direction share a stack: their coins join.
    /// </summary>
    public bool Joins(TradeReceipt other) => ReferenceEquals(Session, other.Session) && Sell == other.Sell;
    public TradeReceipt Append(TradeReceipt other) => this with { Coins = [.. Coins, .. other.Coins] };
}