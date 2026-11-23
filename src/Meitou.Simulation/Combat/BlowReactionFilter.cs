using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The reaction a hit causes a victim still on its feet (<see cref="BlowReactionAsk.Kind"/>); room for more kinds.
/// </summary>
public enum BlowReactionKind : byte
{
    /// <summary>
    /// None: the victim keeps its swing, defence and task as if the hit had not staggered it.
    /// </summary>
    None,

    /// <summary>
    /// A light stagger: the victim's swing and defence are cut off and a small stumble clip plays, unless a stumble already plays.
    /// </summary>
    Light,

    /// <summary>
    /// A heavy stagger: as light, with the big stumble clip (its root motion knocks the victim back) even over a light stumble playing.
    /// </summary>
    Heavy,
}

/// <summary>
/// A hit's reaction about to be applied (<see cref="IBlowReactionFilter"/>): the tick, a melee blow or a bolt, the striker and the victim, the sequence
/// and blow as in <see cref="CombatHit"/>, the damage after armour, its stagger damage (cut, blunt, pierce and extra stun), the victim's stumble
/// threshold (past it a hit is heavy) and the reaction as the game and the earlier filters left it.
/// </summary>
public readonly record struct BlowReactionAsk(long Tick, CombatHitSource Source, CharacterId Attacker, CharacterId Victim, int Sequence, int Blow,
    HitDamage Damage, float StaggerDamage, float Threshold, BlowReactionKind Kind);

/// <summary>
/// A system asked about the reaction of every melee blow and bolt that lands on a victim it leaves on its feet (docs/modding.md "Hit reactions"):
/// it answers the reaction to use. Called serially where the hit lands, in system order, each filter seeing the earlier one's answer. It may read
/// the world and change its own hashed state, and must not change characters or other world state; its answer must follow from the world and the
/// ask alone. A throw or an answer that is no <see cref="BlowReactionKind"/> keeps the reaction as it came and disables the filter for that world;
/// the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface IBlowReactionFilter : ITickSystem
{
    /// <summary>
    /// The reaction to use.
    /// </summary>
    BlowReactionKind Reaction(World world, in BlowReactionAsk ask);
}

/// <summary>
/// The world's hit reaction filters (<see cref="IBlowReactionFilter"/>); serial phases only.
/// </summary>
sealed class BlowReactionFilters
{
    /// <summary>
    /// The registered filters.
    /// </summary>
    readonly SerialHooks<IBlowReactionFilter> _hooks = new(0x52454143, "reaction filter");

    /// <summary>
    /// Whether any filter is still asked.
    /// </summary>
    internal bool Active => _hooks.Active;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex) => _hooks.Register(system, systemIndex);

    /// <summary>
    /// Adds the disabled filters to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// The reaction after every enabled filter, each seeing the earlier one's answer; <paramref name="ask"/>'s own without one. Serial phases.
    /// </summary>
    internal BlowReactionKind Pass(World world, BlowReactionAsk ask)
    {
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks.Enabled(i)) ask = ask with { Kind = Ask(world, i, ask) };
        }

        return ask.Kind;
    }

    /// <summary>
    /// One filter's answer; the reaction as it came (and the filter disabled) when it throws or answers no kind.
    /// </summary>
    BlowReactionKind Ask(World world, int i, in BlowReactionAsk ask)
    {
        try
        {
            var kind = _hooks[i].Reaction(world, ask);
            if (Enum.IsDefined(kind)) return kind;

            _hooks.Disable(i, $"reaction {(byte)kind} is no BlowReactionKind");
        }
        catch (Exception e)
        {
            _hooks.Threw(i, e);
        }

        return ask.Kind;
    }
}
