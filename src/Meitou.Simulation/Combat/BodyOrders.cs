using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Combat;

/// <summary>
/// What the combat system's body step saw happen to a body this tick (<see cref="CharacterCold.BodyEvents"/>).
/// </summary>
[Flags]
public enum BodyEvents : byte
{
    /// <summary>
    /// Nothing.
    /// </summary>
    None = 0,
    /// <summary>
    /// It went down: knocked out or dead (the ragdoll setter turning WHOLE on).
    /// </summary>
    Downed = 1,
    /// <summary>
    /// It woke: out cold or playing dead no more (the character tick's WHOLE-and-able branch).
    /// </summary>
    Woke = 2,
    /// <summary>
    /// It stood from staying low or crawling (the posture setter's change back to 0).
    /// </summary>
    StoodUp = 4,
    /// <summary>
    /// No event: it was down at the body step's last look (how <see cref="Downed"/> is told; a load starts it clear, so a body down at the save is
    /// wiped at its first step, as the original's ragdoll is turned on again after a load).
    /// </summary>
    Lying = 8,
}

/// <summary>
/// The orders the engine gives a character's body, any character's, the player's included (docs/game/ai-tasks.md "Order givers", docs/simulation.md
/// "Order queue"): getting up 65 and waking low 52 (<c>FUN_1405d00b0</c>, <c>FUN_1405c7e20</c>), stumbling 147 (<c>FUN_1404391f0</c> →
/// <c>FUN_140507cd0</c>) and getting out of bed 116 (a failed kidnap, <c>FUN_14034ffa0</c>). Their act-out is standing in place (<see cref="CharacterTask.Idle"/>
/// under the order's row) while the body's own hold lasts: the get-up's (<see cref="CharacterCold.GettingUpUntil"/>) and the stumble's
/// (<see cref="CharacterCold.StaggeredUntil"/>), which the body step and the hit set; the stumble's ends with its task, which the character's next
/// AI update ends (<see cref="UpdateEnds"/>). They release no duty (<see cref="OrderEntry.Interrupts"/>).
/// Saved as any queued order (the writer <c>FUN_1405090b0</c> has no row filter), their holds not. Serial phases, but <see cref="Stumbles"/>.
/// </summary>
public sealed class BodyOrders : IOrderOwner, ITaskHandler
{
    #region Giving

    /// <summary>
    /// The rows these orders run.
    /// </summary>
    internal static readonly TaskType[] Rows = [TaskType.GetUp, TaskType.Aoeu, TaskType.StumbleTaskForced, TaskType.GetOutOfBed];

    /// <summary>
    /// The serial pass after the body step (<see cref="CombatSystem.SlowWorld"/>): each character's <see cref="CharacterCold.BodyEvents"/> of this
    /// tick are acted on and cleared. Down: the ragdoll setter's wipe (<see cref="PlayerOrders.Wipe"/>). Woken: the wake order, given with the clear
    /// flag; one carried keeps it owed until it is put down (no wake while carried), one in a bed or a cage gets none (the ragdoll request is
    /// refused there, <c>FUN_1405cbd60</c>, so the WHOLE branch never runs). Stood up: 65, unless one is queued.
    /// </summary>
    internal void Pass(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { } cold || (cold.BodyEvents & ~BodyEvents.Lying) == 0) continue;

            var events = cold.BodyEvents;
            if ((events & BodyEvents.Downed) != 0) PlayerOrders.Wipe(cold);
            bool owed = (events & BodyEvents.Woke) != 0 && !cold.CarriedBy.IsNone && cold.Medical is { Incapacitated: false };
            cold.BodyEvents = owed ? events & (BodyEvents.Woke | BodyEvents.Lying) : events & BodyEvents.Lying;
            if (owed) continue;

            if ((events & BodyEvents.Woke) != 0) Wake(world, i, cold);
            else if ((events & BodyEvents.StoodUp) != 0) StandUp(world, i, cold);
        }
    }

    /// <summary>
    /// The wake order (<c>FUN_1405d00b0</c>, <b>Verified</b>): an animal 65; else 52 when it wakes staying low or crawling (posture 1 or 2), 65
    /// standing (posture 0); none in a bed or a cage or when it is down again. The original pushes it on every tick the branch runs; the get-up's
    /// start ends that branch, so Meitou gives it once.
    /// </summary>
    void Wake(World world, int slot, CharacterCold cold)
    {
        if (cold.InSomething != Locks.HeldState.None || cold.Medical is not { Incapacitated: false } medical) return;

        bool low = !CharacterFacts.Animal(cold) && (medical.StayingLow || medical.Crawling);
        Give(world, slot, low ? TaskType.Aoeu : TaskType.GetUp);
    }

    /// <summary>
    /// The posture setter's 65 on a change back to standing (<c>FUN_1405c7e20</c>, <b>Verified</b>): unless a 65 is queued already. None in a bed or a
    /// cage (engine: the original's 65 would end at once there, but its clear flag would still wipe the queue).
    /// </summary>
    void StandUp(World world, int slot, CharacterCold cold)
    {
        if (cold.InSomething != Locks.HeldState.None) return;

        foreach (var queued in cold.OrderQueue)
        {
            if (queued.Row == TaskType.GetUp) return;
        }

        Give(world, slot, TaskType.GetUp);
    }

    /// <summary>
    /// A body order with the clear flag (<c>FUN_140508380</c> with clear 1: the queue and the second list emptied, the job list kept).
    /// </summary>
    void Give(World world, int slot, TaskType row) =>
        PlayerOrders.Give(world, slot, new OrderEntry(row, this) { Interrupts = true }, queued: false, show: false);

    /// <summary>
    /// The stagger outside a fight (<c>FUN_1404391f0</c> → <c>FUN_140507cd0</c>, <b>Verified</b>): 147 at the front of the queue, its stumble started
    /// with no plan; a second blow while one runs adds another front 147 and the running stumble goes on (<see cref="PlayerOrders.GiveFront"/>).
    /// Serial phases, after the hit set the stumble's hold. A character with no squad gets it too: the insert sets the body task itself, and the
    /// task-ended handling that pops it (<c>FUN_1405c7c30</c> → <c>FUN_14050d920</c>) runs without a chooser (<see cref="PlayerOrders.DropEnded"/>).
    /// </summary>
    internal void Stumble(World world, int slot) =>
        PlayerOrders.GiveFront(world, slot, new OrderEntry(TaskType.StumbleTaskForced, this) { Interrupts = true, Forced = true });

    /// <summary>
    /// The character's AI update after the stagger (the look, before the order step's pops): the front insert asked a replan, and an update with
    /// one pending stops the running task before its chooser (<c>FUN_140511130</c> → CharBody slot 12 <c>FUN_1405c6770</c>, <b>Verified</b>), so
    /// a running stumble ends there, its clip with it, and the order step starts the interrupted order again (<b>Observed</b>, docs/game/combat.md
    /// "Stagger outside a fight"). A character no update runs for keeps the clip's hold. Serial phases.
    /// </summary>
    internal static void UpdateEnds(World world, int slot)
    {
        var cold = world.Characters.Cold(slot)!;
        if (cold.OrderQueue.Front is not { Row: TaskType.StumbleTaskForced } front || !PlayerOrders.Runs(world, slot, front)) return;

        TaskStart.End(world, slot, TaskEnd.Done);
    }

    /// <summary>
    /// A failed kidnap of a sleeper (<c>FUN_14034ffa0</c>, <b>Verified</b>): 116 Getting out of bed with the clear flag (<c>FUN_1405d20d0</c>). The
    /// kidnap clears the bed state itself, so the order's list A (not in bed) holds and it leaves the queue at once.
    /// </summary>
    internal void OutOfBed(World world, int slot) => Give(world, slot, TaskType.GetOutOfBed);

    /// <summary>
    /// Whether the character runs a stagger's stumble now (a pure read, any phase): its task is 147's and the stumble's hold lasts.
    /// </summary>
    public static bool Stumbles(CharacterCold cold, long tick) =>
        cold.Running is { Source: TaskSource.Order, Row: TaskType.StumbleTaskForced } && cold.StaggeredUntil > tick;

    #endregion

    #region Order owner

    /// <summary>
    /// The body's orders always run: the repertoire and list tests are the queue's (147 has none, <see cref="OrderEntry.Forced"/>).
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order) => OrderCheck.Run;

    /// <summary>
    /// Starts the act-out: <c>Task_GetUp</c>'s start ends at once in a bed or a cage (<c>FUN_14033ca10</c>, <b>Verified</b>), so the order leaves;
    /// 116 gets the sleeper out of its bed first.
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        bool getUp = order.Row is TaskType.GetUp or TaskType.Aoeu;
        if (getUp && cold.InSomething != Locks.HeldState.None) return;
        if (order.Row == TaskType.GetOutOfBed && cold.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, table.IdOf(slot));

        var request = new TaskRequest(CharacterTask.Idle, order.Row, TaskSource.Order) { Forced = order.Forced };
        TaskStart.Start(world, slot, request);
    }

    #endregion

    #region Act-out

    /// <summary>
    /// The character stops and stands in place.
    /// </summary>
    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        MovementSystem.Stop(world.Characters, slot);
        world.Characters.Next[slot].TaskTime = 0;
    }

    /// <summary>
    /// A get-up's or a bed leave's end holds nothing: the body's hold runs out by itself. A stumble's is the hold's end, however the task ends (its
    /// clip done, an order that clears the queue, a task that replaces it, a wipe): <c>Task_Stumble</c>'s end unlocks the mover and clears the
    /// stumble clip (<c>FUN_140335040</c>, <b>Verified</b>), which is what held the character (<c>FUN_1405cfd90</c> → <c>FUN_140435570</c>
    /// locks the mover each frame only while that clip plays). A get-up's hold the stumble's shares stays.
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        var cold = world.Characters.Cold(slot)!;
        if (cold.Running?.Row != TaskType.StumbleTaskForced) return;

        long now = world.Tick;
        if (cold.StaggeredUntil > now) cold.StaggeredUntil = Math.Min(cold.StaggeredUntil, Math.Max(now, cold.GettingUpUntil));
        world.SystemOf<CombatSystem>()?.EndStumble(slot, (int)now);
    }

    /// <summary>
    /// Done once another task took over or the body's hold is over: the get-up's for 65 and 52 (<c>Task_GetUp</c>'s update, <c>FUN_140334a20</c>),
    /// the stumble's for 147 (<c>Task_Stumble</c> ends with its clip, so at once without one), out of the bed for 116.
    /// </summary>
    bool ITaskHandler.Done(World world, int slot, CharacterTask task)
    {
        var cold = world.Characters.Cold(slot)!;
        if (world.Characters.Next[slot].Task != (byte)task) return true;

        return cold.Running?.Row switch
        {
            TaskType.GetUp or TaskType.Aoeu => !CharacterFacts.GettingUp(cold, world.Tick),
            TaskType.StumbleTaskForced => cold.StaggeredUntil <= world.Tick,
            _ => cold.InSomething != Locks.HeldState.Bed,
        };
    }

    #endregion
}
