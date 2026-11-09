using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Economy;

/// <summary>
/// A town's or a faction's culture object (docs/game/economy.md section 7.1, the reader <c>FUN_1407fd9a0</c>): the ITEMS_CULTURE records of its
/// <c>trade culture</c> list folded into one. <c>trade prices</c> gives an item's price factor v0 / 100 with 0 meaning 100 (<b>Verified</b> against
/// the data); when two listed cultures price the same item the later one wins (a map assignment). <c>illegal goods</c> and <c>forbidden items</c> are
/// kept as sets for the trade rules and the shop stock.
/// </summary>
public sealed class TradeCulture
{
    /// <summary>
    /// Price factors by item id (v0 / 100, 0 meaning 100); the later culture wins.
    /// </summary>
    readonly Dictionary<string, float> _prices = new(StringComparer.Ordinal);

    /// <summary>
    /// The <c>illegal goods</c> of every listed culture.
    /// </summary>
    readonly HashSet<string> _illegal = new(StringComparer.Ordinal);

    /// <summary>
    /// The <c>forbidden items</c> of every listed culture.
    /// </summary>
    readonly HashSet<string> _forbidden = new(StringComparer.Ordinal);

    /// <summary>
    /// Items with a price factor of their own.
    /// </summary>
    public IReadOnlyDictionary<string, float> Prices => _prices;

    /// <summary>
    /// <c>illegal goods</c>: contraband here.
    /// </summary>
    public IReadOnlySet<string> Illegal => _illegal;

    /// <summary>
    /// <c>forbidden items</c>: never spawn here.
    /// </summary>
    public IReadOnlySet<string> Forbidden => _forbidden;

    /// <summary>
    /// The price factor of an item record, if this culture names it.
    /// </summary>
    public bool TryPrice(string itemId, out float factor) => _prices.TryGetValue(itemId, out factor);

    /// <summary>
    /// The culture object of a <c>trade culture</c> list; null when the list has no ITEMS_CULTURE record (the game then has no object, and the price
    /// lookup falls through to the faction's, section 6).
    /// </summary>
    public static TradeCulture? From(GameDatabase db, IEnumerable<RecordLink> cultures)
    {
        TradeCulture? culture = null;

        foreach (var link in cultures)
        {
            if (db.Find(link.Id) is not { Type: FcsRecordType.ItemsCulture } record) continue;

            culture ??= new TradeCulture();
            foreach (var p in record.GetReferences("trade prices"))
                culture._prices[p.TargetStringId] = (p.Values.Value0 == 0 ? 100 : p.Values.Value0) * 0.01f;

            foreach (var p in record.GetReferences("illegal goods"))
                culture._illegal.Add(p.TargetStringId);

            foreach (var p in record.GetReferences("forbidden items"))
                culture._forbidden.Add(p.TargetStringId);
        }

        return culture;
    }
}
