namespace Meitou.Simulation.Combat;

/// <summary>
/// Which target choice scores a candidate (<see cref="TargetScoreAsk.Kind"/>); room for more kinds.
/// </summary>
public enum TargetChoiceKind : byte
{
    /// <summary>
    /// A fighter choosing whom to fight next (<c>CombatSystem</c>'s own target choice, the original's <c>AI::chooseBestAttackTarget</c>).
    /// </summary>
    Fight,

    /// <summary>
    /// A character joining a fight choosing which of a subject's attackers to attack (<c>CombatSystem.BestAttackerOf</c>): the attacker of a
    /// protectee, a squad mate's or its own.
    /// </summary>
    Join,

    /// <summary>
    /// The aggro look's attack-on-sight choice.
    /// </summary>
    Look,

    /// <summary>
    /// An assault's choice of whom to attack (the AI's Assault orders and the assaults a think picks).
    /// </summary>
    Assault,
}

/// <summary>
/// A candidate of a target choice as the game scored it (<see cref="ITargetScoreFilter"/>): the tick, the chooser, the candidate, which choice asks,
/// the candidate's facts (<see cref="TargetCandidate"/>) and the score (<see cref="TargetScore.Score"/>, or a filter before's answer).
/// </summary>
public readonly record struct TargetScoreAsk(
    long Tick, CharacterId Chooser, CharacterId Target, TargetChoiceKind Kind, TargetCandidate Candidate, float Score)
{
    /// <summary>
    /// The choice's combat-mode flag, the original's last argument of <c>AI::chooseBestAttackTarget</c> (<c>FUN_14059e300</c> param_7, the name
    /// the original TauntExpansion logs it under): set by the in-fight chooser (<c>CombatClassAI::chooseAttackTarget</c>), clear in every goal
    /// finder and the sight look (<b>Verified</b>, docs/game/combat.md "Target score"). True for <see cref="TargetChoiceKind.Fight"/> only.
    /// </summary>
    public bool CombatMode => Kind == TargetChoiceKind.Fight;

    /// <summary>
    /// The choice's law-enforcer flag, the argument before (<c>FUN_14059e300</c> param_6, named so by the original TauntExpansion): set, the
    /// original adds a term from the candidate's state broadcast <c>+0xa0</c> and the chooser's faction (<b>Observed</b> decompile). Meitou's
    /// choices score no such term, so it is false in every kind.
    /// </summary>
    public bool LawEnforcer => false;
}

/// <summary>
/// A system asked about every scored candidate of every target choice (docs/modding.md "Target choice"); it answers the score to use (higher is
/// better; the best wins). A candidate the game drops is not asked. Filters run in system order, each seeing the one before's answer. It runs on any
/// thread, concurrently, so it must be quick, thread-safe and a pure read of the ask and of world state the tick has settled. A throw or an answer
/// that is not a finite number leaves the score as it came; the filter is still asked for the rest of that tick, not from the next, and the fault
/// enters the state hash. Engine API, not in the original.
/// </summary>
public interface ITargetScoreFilter : ITickSystem
{
    /// <summary>
    /// The score to use for the candidate <paramref name="ask"/> names.
    /// </summary>
    float Score(World world, in TargetScoreAsk ask);
}

/// <summary>
/// The world's target score filters (<see cref="ITargetScoreFilter"/>); any thread.
/// </summary>
sealed class TargetScoreFilters
{
    /// <summary>
    /// The registered filters.
    /// </summary>
    readonly ParallelHooks<ITargetScoreFilter> _hooks = new(0x54534346, "target score filter");

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is ITargetScoreFilter filter) _hooks.Add(filter, system, systemIndex);
    }

    /// <summary>
    /// Adds the faulted filters to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// The score of the slot <paramref name="target"/> for the slot <paramref name="chooser"/> after every filter not faulted before this tick;
    /// <paramref name="score"/> as it is without one or when dropped (null). Any thread.
    /// </summary>
    internal float? Pass(World world, int chooser, int target, TargetChoiceKind kind, in TargetCandidate candidate, float? score)
    {
        if (!_hooks.Active || score is not { } s) return score;

        var table = world.Characters;
        long tick = world.Tick;
        var chooserId = table.IdOf(chooser);
        var targetId = table.IdOf(target);
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (_hooks.Asked(i, tick)) s = Ask(world, i, new TargetScoreAsk(tick, chooserId, targetId, kind, candidate, s));
        }

        return s;
    }

    /// <summary>
    /// One filter's answer; the score as it came (and faulted) when the filter throws or answers a number that is not finite.
    /// </summary>
    float Ask(World world, int i, in TargetScoreAsk ask)
    {
        try
        {
            float score = _hooks[i].Score(world, ask);
            if (float.IsFinite(score)) return score;

            _hooks.Fault(i, ask.Tick, $"score {score} is not a finite number");
        }
        catch (Exception e)
        {
            _hooks.Threw(i, ask.Tick, e);
        }

        return ask.Score;
    }
}
