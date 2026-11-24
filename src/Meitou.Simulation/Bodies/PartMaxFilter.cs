namespace Meitou.Simulation.Bodies;

/// <summary>
/// A body part's maximum health as the game stores it (<see cref="IPartMaxFilter"/>): the tick, the character, the part's index in
/// <see cref="MedicalState.Parts"/>, its LOCATIONAL_DAMAGE record id and <see cref="HealthPart.StoredMaxHp"/> (or a filter before's answer).
/// </summary>
public readonly record struct PartMaxAsk(long Tick, CharacterId Character, int Part, string Record, float Max);

/// <summary>
/// A system asked the maximum health every read of a body part sees (docs/modding.md "Part maximum"), as a hook on the original's
/// <c>HealthPartStatus::maxHealth</c> getter: the stored maximum (and what is saved) stays the game's, and the medical tick, hits, the health
/// panel and every other reader of <see cref="HealthPart.MaxHp"/> see the answer. Asked once for each part of each character at the start of its
/// body step (before its medical tick), only for a stored maximum above 0; the answer holds as a factor on the stored maximum until the next step.
/// Filters run in system order, each seeing the one before's answer, on any thread and concurrently (the body step is parallel): quick,
/// thread-safe, a pure read of the ask and of world state the tick has settled. A throw or an answer that is not a finite number above 0 keeps the
/// maximum as it came; the filter is still asked for the rest of that tick, not from the next, and the fault enters the state hash. Engine API.
/// </summary>
public interface IPartMaxFilter : ITickSystem
{
    /// <summary>
    /// The maximum health to use for the part <paramref name="ask"/> names.
    /// </summary>
    float Max(World world, in PartMaxAsk ask);
}

/// <summary>
/// The world's part maximum filters (<see cref="IPartMaxFilter"/>); any thread.
/// </summary>
sealed class PartMaxFilters
{
    /// <summary>
    /// The registered filters.
    /// </summary>
    readonly ParallelHooks<IPartMaxFilter> _hooks = new(0x504D4646, "part maximum filter");

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is IPartMaxFilter filter) _hooks.Add(filter, system, systemIndex);
    }

    /// <summary>
    /// Adds the faulted filters to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// Sets each part's <see cref="HealthPart.MaxFactor"/> of the character in <paramref name="slot"/> from the filters; nothing without one.
    /// The character's own body step.
    /// </summary>
    internal void Apply(World world, int slot, MedicalState medical)
    {
        if (!_hooks.Active) return;

        var id = world.Characters.IdOf(slot);
        var parts = medical.Parts;
        for (int k = 0; k < parts.Count; k++)
        {
            var p = parts[k];
            float stored = p.StoredMaxHp;
            p.MaxFactor = stored > 0 ? Pass(world, id, k, p, stored) / stored : 1;
        }
    }

    /// <summary>
    /// The maximum of part <paramref name="k"/> after every filter not faulted before this tick.
    /// </summary>
    float Pass(World world, CharacterId id, int k, HealthPart p, float max)
    {
        long tick = world.Tick;
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks.Asked(i, tick)) max = Ask(world, i, new PartMaxAsk(tick, id, k, p.Template.StringId, max));
        }

        return max;
    }

    /// <summary>
    /// One filter's answer; the maximum as it came (and faulted) when the filter throws or answers a number that is not finite and above 0.
    /// </summary>
    float Ask(World world, int i, in PartMaxAsk ask)
    {
        try
        {
            float max = _hooks[i].Max(world, ask);
            if (float.IsFinite(max) && max > 0) return max;

            _hooks.Fault(i, ask.Tick, $"maximum {max} is not a finite number above 0");
        }
        catch (Exception e)
        {
            _hooks.Threw(i, ask.Tick, e);
        }

        return ask.Max;
    }
}
