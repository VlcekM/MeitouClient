using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation.Carrying;

/// <summary>
/// The reaction of a player character to a failed kidnap or stealth knock-out (docs/game/carry-loot.md "Kidnapping", docs/game/combat.md "Stealth
/// knock-out"): the original does not push a fight task. The attacker is made known to the victim (forced sense <c>FUN_140858f40</c>, the engaged
/// list <c>FUN_1405cb0a0</c>, <c>Character::attackTarget</c> <c>FUN_140999f00</c>), a replan is asked (<c>FUN_14001a5b9</c>) and the chooser decides;
/// a sleeper's kidnap also gives order 116 with the queue cleared (<c>FUN_1405d20d0</c>). So the victim's fight is a goal of the decision step
/// (<see cref="EngagedFoe"/>, step 4 beside 32 Self preservation) that an order running at step 1 beats.
/// <para>
/// 32 Self preservation for the one a failed kidnap or knock-out engaged (priority 3, weight 1, key 32): it fights its attacker as row 13, a
/// defence, unless the step's first source, an order, runs or can start (the original's chooser runs the queue first; a kidnap's order 116 clears
/// it). Desire 1 within 40 of the attacker, else 0.7 as Self preservation's (<c>FUN_140599d20</c>). <b>Engine</b>: the attacker is offered here
/// instead of through the threats list, which the original fills from its intended target (Unknown for these two tasks).
/// </para>
/// </summary>
sealed class EngagedDefence : IDecisionSource
{
    #region Engagements

    /// <summary>
    /// The victims engaged now (<see cref="Engage"/>).
    /// </summary>
    readonly List<Engagement> _engagements = [];

    /// <summary>
    /// One victim engaged by an attacker until a tick (the forced sense's alert timer, <see cref="AggroSystem.AlarmSeconds"/>).
    /// </summary>
    readonly record struct Engagement(CharacterId Victim, CharacterId Attacker, long Until);

    /// <summary>
    /// Makes <paramref name="attacker"/> known to the player character <paramref name="victim"/> and asks the decision step to decide it again: the
    /// forced sense through <paramref name="aggro"/> (the attacker is a seen enemy of it at once) and the engagement its Self preservation goal reads.
    /// Serial phases only.
    /// </summary>
    public void Engage(World world, AggroSystem? aggro, int victim, int attacker)
    {
        var table = world.Characters;
        aggro?.Force(world, victim, attacker);
        var id = table.IdOf(victim);
        _engagements.RemoveAll(e => e.Victim == id || e.Until < world.Tick);
        _engagements.Add(new Engagement(id, table.IdOf(attacker), world.Tick + (long)MathF.Round(AggroSystem.AlarmSeconds / world.TickSeconds)));
        DecisionSystem.Request(world, victim);
    }

    /// <summary>
    /// The attacker slot player character <paramref name="slot"/> is engaged by now, -1 for none (a pure read): alive, awake and not expired.
    /// </summary>
    int EngagedFoe(World world, int slot)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        foreach (var e in _engagements)
            if (e.Victim == id && e.Until >= world.Tick && table.TryResolveNext(e.Attacker, out int a)
                && table.Cold(a) is { Medical.Incapacitated: false })
                return a;
        return -1;
    }

    #endregion

    #region Decision source

    public string Name => "32 Self preservation (engaged by a failed kidnap or knock-out)";
    public DecisionStep Step => DecisionStep.Goals;
    public int Key => 32;

    public DecisionCheck CanRun(World world, int slot)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (cold is not { Fighter.CanAttack: true, Fleeing: false, IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true })
            return DecisionCheck.No("cannot fight");
        return EngagedFoe(world, slot) >= 0 ? DecisionCheck.Run : DecisionCheck.No("not engaged");
    }

    public float Desire(World world, int slot)
    {
        var table = world.Characters;
        var d = table.Next[EngagedFoe(world, slot)].Position - table.Next[slot].Position;
        return d.X * d.X + d.Z * d.Z <= RetaliationSystem.ImmediateReach * RetaliationSystem.ImmediateReach ? 1 : 0.7f;
    }

    public void Start(World world, int slot)
    {
        var table = world.Characters;
        int a = EngagedFoe(world, slot);
        var id = table.IdOf(slot);
        _engagements.RemoveAll(e => e.Victim == id);
        world.Defer(new AttackOrder([id], table.IdOf(a)) { Tick = world.Tick + 1, Defensive = true, Source = TaskSource.Goal });
    }

    #endregion
}
