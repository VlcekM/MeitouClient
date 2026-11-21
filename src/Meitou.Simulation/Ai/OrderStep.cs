using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The chooser's order step (<c>checkOrders</c> <c>FUN_140510920</c>, <b>Verified</b>; docs/game/ai-tasks.md "Precedence" source 1) as it holds a
/// character: the same step for every character with a squad (<see cref="Ladder"/>'s first rung). Its pick ends the choice, so an order beats every
/// goal; what the order's hold means outside the chooser lives here: <see cref="CharacterCold.OrderWins"/> (the engine's reactions and the plan
/// steps' errands leave an NPC its order holds alone, <see cref="Ordered"/>), the NPC's routine kept for after the order
/// (<see cref="KeepRoutine"/>), and the task an engine start goes back to (<see cref="ResumesRoutine"/>). Serial phases but for the pure reads.
/// </summary>
internal static class OrderStep
{
    /// <summary>
    /// Whether the chooser runs for the character (the AI update runs the task system only while the character has a platoon, <c>FUN_1405112b0</c>,
    /// and not while it is carried or down, <c>FUN_1405d00b0</c>; it does in a bed or a cage): it has a squad, is not carried and is neither out cold
    /// nor dead; one playing dead (posture 3) is awake to its AI and takes orders. A pure read.
    /// </summary>
    public static bool Runs(CharacterCold cold) => cold.SquadId >= 0 && !Down(cold);

    /// <summary>
    /// Whether the chooser is off for the character (<c>FUN_1405d00b0</c>): it is carried, or out cold (not playing dead) or dead. A pure read.
    /// </summary>
    public static bool Down(CharacterCold cold) =>
        !cold.CarriedBy.IsNone || cold.Medical is { Dead: true } or { Unconscious: true, PlayingDead: false };

    /// <summary>
    /// Whether an NPC's order holds it now (<see cref="CharacterCold.OrderWins"/>, its queue not emptied since, and the sources do not decide it):
    /// its AI's act-outs, the engine's reactions and the plan steps' errands leave it alone. Any phase: it reads only the character's own state.
    /// </summary>
    public static bool Ordered(CharacterCold cold) => cold.OrderWins && !DecisionSystem.Decides(cold) && cold.OrderQueue.Count > 0;

    /// <summary>
    /// Whether an engine start's task goes back to the standing task the character was on (wandering, following), as for an NPC; a character the
    /// sources decide stands idle, its routine being the chooser's (the default task's source). A pure read.
    /// </summary>
    public static bool ResumesRoutine(CharacterCold cold) => !DecisionSystem.Decides(cold);

    /// <summary>
    /// Whether the order step concerns the character: an order in its queue, or the hold of one it just lost (<see cref="CharacterCold.OrderWins"/>,
    /// set anew at the next starts). A pure read.
    /// </summary>
    public static bool Concerns(CharacterCold cold) => cold.OrderQueue.Count > 0 || cold.OrderWins;

    /// <summary>
    /// Whether the starts set the NPC's order hold anew (<see cref="Hold"/>): the order step concerns it, or its kept routine waits for the order's
    /// last walk.
    /// </summary>
    public static bool HoldDue(CharacterCold cold) => Concerns(cold) || cold.OrderRoutine != CharacterTask.Idle;

    /// <summary>
    /// The front order is about to start (serial, <see cref="PlayerOrders"/>): a character its AI runs (<see cref="ResumesRoutine"/>) keeps the
    /// routine it is on for after the order (<see cref="KeepRoutine"/>), and its engine duties let go, so no holder's step stops or redirects the
    /// order's task; an order on its body (<paramref name="interrupts"/>: a stumble, a get-up) lets no duty go, the errands waiting it out, and ends
    /// an engine goal, as the original's update after it plans anew. A character the sources decide has no routine to keep; the engine goal its
    /// chooser picked ends the same way (<see cref="AiSystem.ReleaseChosen"/>).
    /// </summary>
    public static void TakeOver(World world, int slot, CharacterCold cold, bool interrupts)
    {
        if (!ResumesRoutine(cold))
        {
            world.SystemOf<AiSystem>()?.ReleaseChosen(world, slot, interrupts);
            return;
        }

        KeepRoutine(world.Characters, slot, cold);
        if (!interrupts) world.ReleaseDuties(slot);
        else world.SystemOf<AiSystem>()?.ReplanDuty(world, slot);
    }

    /// <summary>
    /// Sets an NPC's <see cref="CharacterCold.OrderWins"/> from its queue (serial phases): its front runs (<see cref="PlayerOrders.Wins"/>; a front
    /// that could start has started by now) while the chooser runs for it. When it turns on, the AI's plan, goal and commitment go (the order's plan
    /// replaces them); when it turns off, the AI chooses again at once. While it is off a routine kept at the order's start is taken up again
    /// (<see cref="BackToRoutine"/>). An NPC with no order left keeps no replan bit (only its queue reads it). A character the sources decide holds no
    /// such flag.
    /// </summary>
    public static void Hold(World world, int slot)
    {
        var cold = world.Characters.Cold(slot)!;
        if (DecisionSystem.Decides(cold))
        {
            cold.OrderWins = false;
            return;
        }

        if (cold.OrderQueue.Count == 0) cold.Replan = false;
        bool wins = Runs(cold) && cold.OrderQueue.Front is { Started: true } && PlayerOrders.Wins(world, slot);
        if (!wins) BackToRoutine(world, slot, cold);
        if (wins == cold.OrderWins) return;

        cold.OrderWins = wins;
        if (cold.Ai is not { } mind) return;
        if (wins)
        {
            mind.ResetPlan();
            mind.Goal = null;
        }
        else
        {
            mind.Replan = true;
        }
    }

    /// <summary>
    /// An order is about to take an NPC with no mind (no squad; serial): the routine it is on, wandering or following its leader, is kept for
    /// <see cref="BackToRoutine"/>; a routine kept by an earlier order of the queue stays.
    /// </summary>
    public static void KeepRoutine(CharacterTable table, int slot, CharacterCold cold)
    {
        if (cold.Ai is not null) return;

        var task = (CharacterTask)table.Next[slot].Task;
        if (task is CharacterTask.Wander or CharacterTask.Follow) cold.OrderRoutine = task;
    }

    /// <summary>
    /// The order no longer holds the NPC: the kept routine waits while the order's task goes on (a move leaves the queue near its point and its walk
    /// goes on); then one standing idle, awake and held by nothing takes it up again (a follow only while its leader is there); it goes either way.
    /// </summary>
    static void BackToRoutine(World world, int slot, CharacterCold cold)
    {
        var routine = cold.OrderRoutine;
        if (routine == CharacterTask.Idle) return;
        if (world.Characters.Next[slot].Task != (byte)CharacterTask.Idle && cold.Running is { Source: TaskSource.Order }) return;

        cold.OrderRoutine = CharacterTask.Idle;
        if (!Free(world.Characters, slot, cold)) return;

        if (routine == CharacterTask.Wander)
            TaskStart.Become(world, slot, CharacterTask.Wander, TaskType.Wanderer, TaskSource.Goal);
        else if (world.Squads.Find(cold.SquadId) is { } squad && squad.Leader != world.Characters.IdOf(slot)
                && world.Characters.TryResolveNext(squad.Leader, out _))
            TaskStart.Become(world, slot, CharacterTask.Follow, TaskType.FollowSquadleader, TaskSource.Goal);
    }

    /// <summary>
    /// Whether the NPC stands idle, awake and held by nothing (no fight, flight, carry, bed or cage), so its routine may start.
    /// </summary>
    static bool Free(CharacterTable table, int slot, CharacterCold cold) =>
        table.Next[slot].Alive && table.Next[slot].Task == (byte)CharacterTask.Idle
        && cold is { InCombat: false, Fleeing: false, IsImprisoned: false, CarriedBy.IsNone: true, Carrying.IsNone: true }
        && cold.Medical is not { Incapacitated: true } && cold.InSomething == Locks.HeldState.None;

    /// <summary>
    /// Whether the order step asks for the character now, outside its think turn (the original's order step runs on every AI update): its front
    /// not tried yet or its task ended, or (not paused) a front that leaves at this update (<see cref="PlayerOrders.PopsNow"/>).
    /// </summary>
    public static bool Asks(World world, int slot, CharacterCold cold, bool paused)
    {
        bool front = cold.OrderQueue.Front is { } f && (!f.Tried || f.Started && !PlayerOrders.Runs(world, slot, f));
        return front || !paused && PlayerOrders.PopsNow(world, slot);
    }
}
