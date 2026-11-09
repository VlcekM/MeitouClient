using Meitou.Data.World;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Trading;

namespace Meitou.Simulation.Ai;

/// <summary>
/// Goal 118/119, the buying (docs/game/ai-tasks.md "Shopping"): the think starts the shopper's timer on arrival; this serial step buys, as it changes
/// the shops' stock and the squads' purses.
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// Set by a think that started a shopper's timer (AiMind.NextBuyTick); the step runs while any shopper is waiting.
    /// </summary>
    int _shopping;
    /// <summary>
    /// The trade system that does the buying, resolved on first use.
    /// </summary>
    TradeSystem? _trade;

    /// <summary>
    /// Ticks between two units (Task_Shopping's timer is 1.0 s, FUN_14033a4c0 / FUN_140337d00, Verified).
    /// </summary>
    static long BuyTicks(World world) => (long)MathF.Ceiling(1 / world.TickSeconds);

    /// <summary>
    /// Task_Shopping's update (<c>FUN_140337d00</c>) for every shopper whose timer ran out, in slot order: it buys one unit (<see cref="BuyOnce"/>)
    /// and waits another second; a call that buys nothing ends the task (as finished when it had bought, else as impossible: <b>Observed</b>, Meitou
    /// ends both alike), so the shopper picks its shop again at its next think. A shopper the AI no longer runs, or no longer at its shop, loses its
    /// timer.
    /// </summary>
    void BuyForShoppers(World world)
    {
        var table = world.Characters;
        bool waiting = false;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { NextBuyTick: not 0 } mind } cold) continue;
            ref var n = ref table.Next[s];
            if (!n.Alive || mind.Behaviour != AiBehaviour.Shop || mind.ShopAt is not { } shop || mind.Walking || !Holds(n, cold))
            {
                mind.NextBuyTick = 0;
                continue;
            }

            waiting = true;
            if (world.Tick < mind.NextBuyTick) continue;
            if (BuyOnce(world, s, mind, shop)) mind.NextBuyTick = world.Tick + BuyTicks(world);
            else
            {
                mind.NextBuyTick = 0;
                mind.Target = null;
                mind.ShopAt = null;
            }
        }

        if (waiting) Interlocked.Exchange(ref _shopping, 1);
    }

    /// <summary>
    /// One call of the buying step (<c>FUN_1409a14b0</c>, <b>Verified</b>): the first open entry of the list; each attempt counts a unit of it before
    /// the sale, and a sale that fails (cannot pay, no room) tries again while the entry has units left; a function no seller has an item of is set
    /// aside for the call and the next open entry is taken. True after the first unit bought; false when nothing was left to try.
    /// </summary>
    bool BuyOnce(World world, int slot, AiMind mind, TownBuilding shop)
    {
        _trade ??= world.SystemOf<TradeSystem>();
        if (_trade is null) return false;
        var market = _trade.Market(world);
        var list = mind.Shopping;
        var id = world.Characters.IdOf(slot);
        list.Begin(mind.Squad.Ai?.Town ?? -1, ShoppingList.DayOf(world.GameHours), world.Hour);
        Span<int> skipped = stackalloc int[list.Count];
        int skips = 0;
        int k = list.NextOpen(skipped[..0]);
        for (int attempt = 0; k >= 0; attempt++)
        {
            int function = list.FunctionOf(k);
            ulong roll = Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Trade, SeedSalts.AiShop ^ (ulong)world.Tick << 8 ^ (ulong)attempt);
            var sale = NpcTrade.Buy(world, market, data.PlayerFaction, slot, shop, function, roll);
            if (sale == NpcSale.NoItem)
            {
                skipped[skips++] = function;
                k = list.NextOpen(skipped[..skips]);
                continue;
            }

            list.Use(k);
            if (sale == NpcSale.Sold) return true;
            if (!list.IsOpen(k)) k = list.NextOpen(skipped[..skips]);
        }

        return false;
    }
}