using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;

namespace Meitou.Simulation.Carrying;

/// <summary>
/// Goal 169 Putting down (<c>Put carried dude down when they can walk</c>, docs/game/carry-loot.md "Goal 169") as a goal of the one chooser: its
/// desire is <c>FUN_1405a0710</c> (<see cref="Desire"/>), its plan <c>[69, 169]</c> (list B <c>!47</c>, whose only provider is 69 Putting down, the
/// planner's carried step), so the carrier puts the woken body down through the plan step and the goal, which neither acts nor has an effect, is
/// then dropped. It holds nothing and starts nothing of its own.
/// </summary>
internal sealed class PutDownGoals(CarrySystem carry)
{
    /// <summary>
    /// The carry system, whose relation table the enemy test reads.
    /// </summary>
    readonly CarrySystem _carry = carry;

    /// <summary>
    /// Registers 169; its plan's step 69 is the planner's own (<see cref="DutyGoals.Register"/>).
    /// </summary>
    internal void Register(DutyGoals goals) => goals.Register(TaskType.PutDownCarriedDudeIfTheyCanWalk, new PuttingDown(this));

    /// <summary>
    /// <c>FUN_1405a0710</c> (<b>Verified</b>, decompile): 0 unless the character carries (<c>+0x348</c>) a character it resolves; then 1.0 when that body is
    /// not knocked out (<c>+0x5b9</c>), not dead (<c>+0x5bc</c>), not crippled (vfunc <c>0x360</c>, the crippled flag <c>+0x5b8</c>:
    /// <see cref="Bodies.MedicalState.Crawling"/>) and either chained (<c>+0x320</c>) or not regarded by the carrier as an enemy (vfunc <c>0x3e8</c>,
    /// <see cref="CarrySystem.WakesIntoPutDown"/>); else 0. Parallel: the carrier's own slot and the body's cold state, which serial steps write.
    /// </summary>
    internal float Desire(World world, int slot)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { Carrying.IsNone: false } carrier) return 0;
        if (!table.TryResolveNext(carrier.Carrying, out int b) || table.Cold(b) is not { Medical: { } medical } body) return 0;
        if (medical.Unconscious || medical.Dead || medical.Crawling) return 0;

        return _carry.WakesIntoPutDown(carrier, body) ? 1 : 0;
    }

    /// <summary>
    /// Goal 169's chooser side.
    /// </summary>
    sealed class PuttingDown(PutDownGoals owner) : IDutyGoal
    {
        /// <summary>
        /// The goal's desire (<see cref="PutDownGoals.Desire"/>).
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => owner.Desire(world, ask.Slot);

        /// <summary>
        /// Never reached with a body on the shoulder (the plan's step 69 comes first, and once the hands are empty the desire is 0); nothing to start.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => false;

        /// <summary>
        /// Nothing to drop: the goal holds nothing.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why)
        {
        }
    }
}
