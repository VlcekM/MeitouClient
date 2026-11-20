using Meitou.Simulation.Ai;

namespace Meitou.Simulation.Aggro;

/// <summary>
/// The targets of the player characters whose look (or a squad mate's) found an enemy, for row 16 Attacking enemies: Hold's goal and the job of the
/// <see cref="AggroSystem"/> take them in the chooser. An offer of the slow world stays good through the next tick: the chooser's look asks it in
/// that tick's schedule step and its start in that tick's slow world (<see cref="DecisionSystem"/>), so this tick's offers and the last tick's are
/// both kept, the newer first. Hashed while any is kept.
/// </summary>
sealed class SightOffers
{
    /// <summary>
    /// The target offered to each player character at <see cref="_nowTick"/>, by slot.
    /// </summary>
    Dictionary<int, CharacterId> _now = [];

    /// <summary>
    /// The offers of the tick before <see cref="_nowTick"/>'s (kept when the next tick offered again), by slot.
    /// </summary>
    Dictionary<int, CharacterId> _last = [];

    /// <summary>
    /// The tick <see cref="_now"/> is of.
    /// </summary>
    long _nowTick = -1;

    /// <summary>
    /// The tick <see cref="_last"/> is of.
    /// </summary>
    long _lastTick = -1;

    /// <summary>
    /// The enemy this tick's or the last tick's look offered the player character for row 16 (this tick's first), -1 for none.
    /// </summary>
    public int Offered(World world, int slot)
    {
        if (Fresh(world, _nowTick) && _now.TryGetValue(slot, out var id) || Fresh(world, _lastTick) && _last.TryGetValue(slot, out id))
            return world.Characters.TryResolveNext(id, out int t) && world.Characters.Cold(t) is { Medical.Incapacitated: false } ? t : -1;

        return -1;
    }

    /// <summary>
    /// Whether offers of tick <paramref name="tick"/> are still good: made this tick or the last.
    /// </summary>
    static bool Fresh(World world, long tick) => tick >= 0 && world.Tick - tick <= 1;

    /// <summary>
    /// Offers <paramref name="targetId"/> to the <paramref name="fighters"/> (each keeps the first offer of the tick) and asks each a decision.
    /// </summary>
    public void Offer(World world, List<CharacterId> fighters, CharacterId targetId)
    {
        var table = world.Characters;
        if (_nowTick != world.Tick) Turn(world.Tick);

        foreach (var f in fighters)
        {
            _now.TryAdd(f.Slot, targetId);
            DecisionSystem.Request(table.Cold(f.Slot)!);
        }
    }

    /// <summary>
    /// A new tick's first offer: the current offers become the last tick's (dropped when older), and the current ones start empty.
    /// </summary>
    void Turn(long tick)
    {
        (_now, _last) = (_last, _now);
        _lastTick = _nowTick;
        _now.Clear();
        _nowTick = tick;
    }

    /// <summary>
    /// Adds the kept offers to the state hash (each generation's tick and its offers in slot order), nothing for an empty one.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (_now.Count > 0) HashOffers(ref hasher, _nowTick, _now);
        if (_last.Count > 0) HashOffers(ref hasher, _lastTick, _last);
    }

    /// <summary>
    /// Hashes one generation of offers in slot order.
    /// </summary>
    static void HashOffers(ref StateHasher hasher, long tick, Dictionary<int, CharacterId> offers)
    {
        hasher.Add(0x5369674F);   // "SigO"
        hasher.Add(tick);
        foreach (var slot in offers.Keys.Order())
        {
            hasher.Add(slot);
            hasher.Add(offers[slot]);
        }
    }
}
