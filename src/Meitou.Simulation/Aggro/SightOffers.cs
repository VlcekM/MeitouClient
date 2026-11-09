using Meitou.Simulation.Ai;

namespace Meitou.Simulation.Aggro;

/// <summary>
/// This tick's targets of the player characters whose look (or a squad mate's) found an enemy, for row 16 Attacking enemies: Hold's goal and the
/// job of the <see cref="AggroSystem"/> take them in the decision step.
/// </summary>
sealed class SightOffers
{
    /// <summary>
    /// The target offered to each player character this tick, by slot.
    /// </summary>
    readonly Dictionary<int, CharacterId> _offers = [];

    /// <summary>
    /// The tick the offers are of.
    /// </summary>
    long _offersTick = -1;

    /// <summary>
    /// The enemy this tick's look offered the player character for row 16, -1 for none.
    /// </summary>
    public int Offered(World world, int slot) =>
        _offersTick == world.Tick && _offers.TryGetValue(slot, out var id) && world.Characters.TryResolveNext(id, out int t)
        && world.Characters.Cold(t) is { Medical.Incapacitated: false } ? t : -1;

    /// <summary>
    /// Offers <paramref name="targetId"/> to the <paramref name="fighters"/> (each keeps the first offer of the tick) and asks each a decision.
    /// </summary>
    public void Offer(World world, List<CharacterId> fighters, CharacterId targetId)
    {
        var table = world.Characters;

        if (_offersTick != world.Tick)
        {
            _offers.Clear();
            _offersTick = world.Tick;
        }

        foreach (var f in fighters)
        {
            _offers.TryAdd(f.Slot, targetId);
            DecisionSystem.Request(table.Cold(f.Slot)!);
        }
    }
}
