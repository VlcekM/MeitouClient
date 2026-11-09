namespace Meitou.Simulation.Items;

/// <summary>
/// Transient trade-window lifetime; closing it invalidates even receipts on moved items. Never saved.
/// </summary>
internal sealed class TradeSession
{
    /// <summary>
    /// Whether the trade window is still open (receipts of a closed one no longer count).
    /// </summary>
    public bool Active { get; set; } = true;
}

/// <summary>
/// Exact paid coins per unit, retained through stack splits. Sell is the direction that undoes the trade.
/// </summary>
internal sealed record TradeReceipt(TradeSession Session, bool Sell, long[] Coins)
{
    /// <summary>
    /// The coins paid for the first <paramref name="units"/> units.
    /// </summary>
    public long Price(int units) => Coins.Take(units).Sum();
    /// <summary>
    /// The receipt of the first <paramref name="units"/> units (a split's).
    /// </summary>
    public TradeReceipt Take(int units) => this with { Coins = Coins.Take(units).ToArray() };
    /// <summary>
    /// The receipt without the first <paramref name="units"/> units.
    /// </summary>
    public TradeReceipt Skip(int units) => this with { Coins = Coins.Skip(units).ToArray() };
    /// <summary>
    /// Units of the same trade and direction share a stack: their coins join.
    /// </summary>
    public bool Joins(TradeReceipt other) => ReferenceEquals(Session, other.Session) && Sell == other.Sell;
    /// <summary>
    /// The receipt with <paramref name="other"/>'s coins after its own.
    /// </summary>
    public TradeReceipt Append(TradeReceipt other) => this with { Coins = [.. Coins, .. other.Coins] };
}