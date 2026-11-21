using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Goal 208 Getting out of bed! (<c>get out of bed when in trouble</c>, docs/game/ai-tasks.md "Desire and commitment", row 98's desire) as a goal of the
/// one chooser: its desire is the danger score (AI slot 28 <c>FUN_1405a0b50</c>, <see cref="AiSystem.SleepDanger"/>), its plan the row alone
/// (<c>Task_GetOutOfBed</c>: no finder, no effect, list B 45 held), and it is a candidate only while the character lies in a bed (list A <c>!83</c>
/// drops it otherwise). The act-out is the get-out-of-bed every goal but sleep makes first (<see cref="AiSystem"/>'s act, <c>Task_GetOutOfBed</c>'s
/// clip); the plan step then ends the goal by its list A.
/// </summary>
internal sealed class WakeGoals
{
    /// <summary>
    /// Registers 208, whose plan has no row but its own (<see cref="DutyGoals.Register"/>).
    /// </summary>
    internal void Register(DutyGoals goals) => goals.Register(TaskType.GetOutOfBedIfItsEmergency, new Waking());

    /// <summary>
    /// Goal 208's chooser side: the danger score as its desire; started once its character is out of bed.
    /// </summary>
    sealed class Waking : IDutyGoal
    {
        /// <summary>
        /// The danger score of the sleeper (AI slot 28, <c>FUN_1405a0b50</c>, <b>Verified</b>: row 208's registration in <c>FUN_140319a60</c>): 2 with an
        /// enemy sensed or a threat on its list, 1.5 with an alarm of its town near, 1 with a stranger in its home, else 0. Parallel: the asker's own
        /// senses and <c>Previous</c> of the others (<see cref="AiSystem.SleepDanger"/>).
        /// </summary>
        public float Desire(World world, in DutyAsk ask)
        {
            if (world.SystemOf<AiSystem>() is not { } ai || ask.Squad.Ai is not { } board) return 0;

            return world.Characters.Cold(ask.Slot) is { } me ? ai.SleepDanger(world, board, ask.Slot, me) : 0;
        }

        /// <summary>
        /// The character chose to get up: the act took it out of its bed before asking (a goal other than sleep gets out first, the backwards clip of a
        /// <c>gotobed</c> bed played out over the thinks before), so nothing is left to start; the plan step ends the goal by its list A
        /// (<c>!83</c>) at the next think.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => true;

        /// <summary>
        /// Nothing to drop: the act-out holds nothing.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why)
        {
        }
    }
}
