using Meitou.Simulation;
using Meitou.Simulation.Combat;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

/// <summary>FightStarter's world (task 3.1): each <see cref="IFightWorld{TId, TOrders}"/> member is one PgEngine adapter method (the E1 table
/// in src/Game/FightStarter.cs). Bound to the world of each FightStarter call and reused; sim thread, serial steps only. Owned by the
/// FightStarter of one world: it keeps that world's <c>CombatSystem</c> (looked up once, not per fighter) and logs once when there is none.</summary>
sealed class PgFightWorld(Action<string>? ownLog = null) : IFightWorld<CharacterId, PgEngine.PgOrders>
{
    World? world;
    World? combatWorld;          // the world combat was looked up in (a new world, which should not happen for one FightStarter, looks again)
    CombatSystem? combat;
    bool combatMissLogged;

    internal PgFightWorld Bind(World w) { world = w; return this; }

    /// <summary>The bound world's combat system, found once; a world without one is logged once (the keep-alive then reads every fighter as
    /// without a target and not in melee combat).</summary>
    CombatSystem? Combat()
    {
        var w = Wd;
        if (combat is not null && ReferenceEquals(combatWorld, w)) return combat;
        if (!ReferenceEquals(combatWorld, w)) { combatWorld = w; combatMissLogged = false; }
        combat = PgEngine.FindCombat(w);
        if (combat is null && !combatMissLogged)
        {
            combatMissLogged = true;
            ownLog?.Invoke("FightStarter: no CombatSystem in world.Systems (logged once per world); attack targets and melee combat mode read as none");
        }
        return combat;
    }

    World Wd => world ?? throw new InvalidOperationException("PgFightWorld used before Bind");

    public long Tick => Wd.Tick;
    public CharacterId None => CharacterId.None;
    public bool IsNone(CharacterId c) => c.IsNone;
    public bool Exists(CharacterId c) => PgEngine.Exists(Wd, c);
    public bool IsDead(CharacterId c) => PgEngine.IsDead(Wd, c);
    public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(Wd, c);
    public bool IsPlayer(CharacterId c) => PgEngine.IsPlayer(Wd, c);

    public bool TryGetPosition(CharacterId c, out float x, out float y, out float z)
    {
        if (PgEngine.Position(Wd, c) is { } p) { x = p.X; y = p.Y; z = p.Z; return true; }
        x = y = z = 0f;
        return false;
    }

    public bool TryGetOrders(CharacterId c, out PgEngine.PgOrders orders)
    {
        if (PgEngine.Orders(Wd, c) is { } o) { orders = o; return true; }
        orders = default;
        return false;
    }

    public void SetCombatStance(CharacterId c) => PgEngine.SetCombatStance(Wd, c);
    public bool SetJobsOff(CharacterId c) => PgEngine.SetJobsOff(Wd, c);
    public void SetJobsOn(CharacterId c) => PgEngine.SetJobsOn(Wd, c);
    public void RestoreStance(CharacterId c, PgEngine.PgOrders saved) => PgEngine.RestoreStance(Wd, c, saved);
    public void Attack(CharacterId attacker, CharacterId target, bool player, bool queued) => PgEngine.Attack(Wd, attacker, target, player, queued);
    public void EndTask(CharacterId c) => PgEngine.EndTask(Wd, c);
    public void Stop(IReadOnlyList<CharacterId> fighters, int ticksAhead) => PgEngine.Stop(Wd, fighters, ticksAhead);
    public CharacterId AttackTarget(CharacterId c) => PgEngine.AttackTarget(Wd, Combat(), c);
    public bool InMeleeCombatMode(CharacterId c) => PgEngine.InMeleeCombatMode(Wd, Combat(), c);
    // task 3.5 (the stale-target sweep): CharacterCold.InCombat, or the running task is an attack (CharacterCold.Running, TaskStart.cs:166)
    public bool InFight(CharacterId c) => PgEngine.InCombat(Wd, c) || PgEngine.Running(Wd, c)?.Task == Meitou.Simulation.Movement.CharacterTask.Attack;
}
