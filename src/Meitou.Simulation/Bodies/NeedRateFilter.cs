namespace Meitou.Simulation.Bodies;

/// <summary>
/// Which rate of a character's body a need rate filter is asked about (<see cref="NeedRateAsk.Kind"/>); room for more kinds.
/// </summary>
public enum NeedKind : byte
{
    /// <summary>
    /// The hunger drain: the race's <c>hunger rate</c> times the situation factor (resting, moving with its encumbrance, a machine's or chair's
    /// rate, idle), the multiplier on <c>hours / (starvation time x hunger time option)</c> taken from the hunger meter each body update.
    /// </summary>
    Hunger,
}

/// <summary>
/// A rate of a character's body about to be used (<see cref="INeedRateFilter"/>): the world's tick, the character, the kind and the rate as the
/// game computed it.
/// </summary>
public readonly record struct NeedRateAsk(long Tick, CharacterId Character, NeedKind Kind, float Rate);

/// <summary>
/// A system asked about the rates of a character's needs (docs/modding.md "Need rates"): every body update's hunger drain of every character in the
/// world, and the HUD's hunger figure. It answers the rate to use. The body update runs in the parallel act phase, so it runs on any thread,
/// concurrently: it must be quick, thread-safe and a pure read of the ask, of the character's own state and of world state the tick has settled.
/// A throw or an answer that is negative or not finite leaves the rate as it came; the filter is still asked for the rest of that tick, not from
/// the next, and the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface INeedRateFilter : ITickSystem
{
    /// <summary>
    /// The rate to use for <paramref name="ask"/>.
    /// </summary>
    float Rate(World world, in NeedRateAsk ask);
}

/// <summary>
/// The world's need rate filters (<see cref="INeedRateFilter"/>); asked from the parallel phases.
/// </summary>
sealed class NeedRateFilters
{
    /// <summary>
    /// The registered filters.
    /// </summary>
    readonly ParallelHooks<INeedRateFilter> _hooks = new(0x4E454544, "need rate filter");

    /// <summary>
    /// Whether any filter is registered; the rates are used as they are without one.
    /// </summary>
    internal bool Active => _hooks.Active;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is INeedRateFilter filter) _hooks.Add(filter, system, systemIndex);
    }

    /// <summary>
    /// Adds the faulted filters to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// <paramref name="rate"/> after every filter not faulted before this tick, each seeing the one before's answer. Any thread.
    /// </summary>
    internal float Pass(World world, CharacterId character, NeedKind kind, float rate)
    {
        long tick = world.Tick;
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (!_hooks.Asked(i, tick)) continue;

            rate = Ask(world, i, new NeedRateAsk(tick, character, kind, rate));
        }

        return rate;
    }

    /// <summary>
    /// One filter's answer; the rate as it came when the filter throws or answers a rate that is negative or not finite.
    /// </summary>
    float Ask(World world, int i, in NeedRateAsk ask)
    {
        try
        {
            float rate = _hooks[i].Rate(world, ask);
            if (float.IsFinite(rate) && rate >= 0) return rate;

            _hooks.Fault(i, ask.Tick, $"rate {rate} is negative or not a finite number");
        }
        catch (Exception e)
        {
            _hooks.Threw(i, ask.Tick, e);
        }

        return ask.Rate;
    }

    /// <summary>
    /// The hunger drain of the character <paramref name="ctx"/> is for (<see cref="MedicalContext.Needs"/>): <paramref name="rate"/> after the
    /// filters, or as it is when the context names no world to ask.
    /// </summary>
    internal static float Hunger(in MedicalContext ctx, float rate) =>
        ctx.Needs is { } world ? world.NeedRateFilters.Pass(world, ctx.NeedsOf, NeedKind.Hunger, rate) : rate;
}
