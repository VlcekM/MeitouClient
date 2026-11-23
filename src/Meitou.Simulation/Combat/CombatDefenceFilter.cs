using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>
/// A defender's choice of defence against a melee blow on its way (<see cref="ICombatDefenceFilter"/>): the tick, the defender and the attacker, the
/// attack's sequence and blow (as <see cref="CombatHit"/>'s), the side the blow comes from (the technique direction), whether the defender is
/// stumbling (only stumble dodges then), the game's block and dodge chances in percent (an armed fighter rolls the block, one that cannot block the
/// dodge) and the technique as the game and the earlier filters left it: an index into <see cref="CombatSystem.Techniques"/>, -1 for none.
/// </summary>
public readonly record struct DefenceChoiceAsk(long Tick, CharacterId Defender, CharacterId Attacker, int Sequence, int Blow, int Direction,
    bool Stumbling, float BlockChance, float DodgeChance, int Technique);

/// <summary>
/// A system asked which defence a melee fighter takes up against each blow it reacts to (docs/modding.md "Defences"): it answers the technique to use,
/// -1 for none (no defence is tried again for a while, as after a failed roll). An answer must be -1 or a block or dodge technique of
/// <see cref="CombatSystem.Techniques"/>, a block that is not a dodge only for a defender that can block. Asked in the combat act phase, on any thread
/// and concurrently, so a filter must be quick, thread-safe and a pure read of the ask and of world state the tick has settled; state of its own about
/// the defender it may keep only from that defender's asks (each defender chooses on one thread). A throw or an answer against the rule keeps the
/// technique as it came; the filter is still asked for the rest of that tick, not from the next, and the fault enters the state hash. Engine API, not
/// in the original.
/// </summary>
public interface ICombatDefenceFilter : ITickSystem
{
    /// <summary>
    /// The defence technique to use, -1 for none.
    /// </summary>
    int Defence(World world, in DefenceChoiceAsk ask);
}

/// <summary>
/// The world's defence choice filters (<see cref="ICombatDefenceFilter"/>); any thread.
/// </summary>
sealed class CombatDefenceFilters
{
    /// <summary>
    /// The registered filters.
    /// </summary>
    readonly ParallelHooks<ICombatDefenceFilter> _hooks = new(0x44464346, "defence filter");

    /// <summary>
    /// Whether any filter is registered.
    /// </summary>
    internal bool Active => _hooks.Active;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is ICombatDefenceFilter filter) _hooks.Add(filter, system, systemIndex);
    }

    /// <summary>
    /// Adds the faulted filters to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// The technique after every filter not faulted before this tick, each seeing the earlier one's answer. <paramref name="canBlock"/>: the defender
    /// may take up a block that is not a dodge. Any thread.
    /// </summary>
    internal int Pass(World world, IReadOnlyList<CombatTechnique> techniques, bool canBlock, DefenceChoiceAsk ask)
    {
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks.Asked(i, ask.Tick)) ask = ask with { Technique = Ask(world, i, techniques, canBlock, ask) };
        }

        return ask.Technique;
    }

    /// <summary>
    /// One filter's answer; the technique as it came (and the filter faulted) when it throws or answers against the rule.
    /// </summary>
    int Ask(World world, int i, IReadOnlyList<CombatTechnique> techniques, bool canBlock, in DefenceChoiceAsk ask)
    {
        try
        {
            int pick = _hooks[i].Defence(world, ask);
            if (Valid(techniques, canBlock, pick)) return pick;

            _hooks.Fault(i, ask.Tick, $"technique {pick} is not -1 or a defence the defender can take up");
        }
        catch (Exception e)
        {
            _hooks.Threw(i, ask.Tick, e);
        }

        return ask.Technique;
    }

    /// <summary>
    /// -1, or a block or dodge technique; a block that is not a dodge only when the defender can block.
    /// </summary>
    static bool Valid(IReadOnlyList<CombatTechnique> techniques, bool canBlock, int pick)
    {
        if (pick == -1) return true;
        if ((uint)pick >= (uint)techniques.Count) return false;

        var t = techniques[pick];
        return t.IsDodge || t.IsBlock && canBlock;
    }
}
