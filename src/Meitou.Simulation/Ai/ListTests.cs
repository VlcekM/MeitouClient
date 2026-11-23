using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The general list tests of the original's order step and job pass (docs/game/ai-tasks.md "Finished tests" and "Perma-jobs", <b>Verified</b>):
/// the front order leaves when its row is done (<c>FUN_140510920</c>), a job is passed over when its row is done or has nothing to do
/// (<c>FUN_14050e5b0</c>). The states are <see cref="RowStates"/>'; a list holding a state it does not evaluate never counts as holding. Pure reads
/// (serial phases).
/// </summary>
public static class ListTests
{
    /// <summary>
    /// <c>FUN_140510920</c>'s list half of the pop: the order's list A (<c>FUN_14060f570</c>), its effect test (<see cref="EffectHolds"/>) and its
    /// class's finished test (slot 4) hold (the desire half is <see cref="OrderDesire"/>). Slot 4 is asked of the running task when the order runs it
    /// (<paramref name="running"/>, <c>FUN_140506b10</c>: same type and target), else of the order's own instance (<see cref="TaskFinished.Idle"/>: a move
    /// that ran stays finished). The lists are the
    /// original's order row's (<see cref="Saving.OrderSave.SavedRow"/>: Meitou's sleep order 98 is the original's 258, whose list A is empty).
    /// </summary>
    public static bool OrderDone(World world, int slot, OrderEntry order, bool running)
    {
        var type = Saving.OrderSave.SavedRow(order.Row);
        if (TaskDataTable.Of(type) is not { } row) return false;

        var target = Target(order);
        if (RowStates.Done(world, slot, row, target) != true || !EffectHolds(row)) return false;

        return running ? !TaskFinished.Unfinished(world, slot, type) : TaskFinished.Idle(world, slot, type, order.Target, order.Ran);
    }

    /// <summary>
    /// The plan step's list B test of a running order's row (<c>FUN_14050d970</c>, <b>Verified</b>: (e) in docs/game/ai-tasks.md "Precedence"):
    /// true when an entry of the row's list B that <see cref="RowStates"/> evaluates fails (<see cref="RowStates.NeedFails"/>), false when none
    /// does, null where it is not asked: a row with TaskData <c>+0x16</c> (<see cref="PlanSteps.KeepsHours"/>; the hours never end an order, whose
    /// task the order adds make with the hours 0 to 100, <c>FUN_1405086d0</c>), the walk to the row (the original's plan step of its own; a move is
    /// that walk), and a follow row's fight (31, 45, 289), a step of its plan (13) whose own list B is not asked
    /// (<see cref="Movement.MovementSystem.FightStepOver"/>).
    /// </summary>
    public static bool? OrderNeedsFail(World world, int slot, OrderEntry order)
    {
        var cold = world.Characters.Cold(slot)!;
        if (!order.IsMove && world.Characters.Next[slot].Task == (byte)Movement.CharacterTask.GoTo) return null;
        if (cold.Running is { Task: Movement.CharacterTask.Attack } run && PermaJobs.Follows(run.Row)) return null;

        return OrderNeedsFailNow(world, slot, order);
    }

    /// <summary>
    /// <see cref="OrderNeedsFail"/>'s list test without its walk and fight-step gates: at the order's start, whether the row could run at once
    /// (false) or a step of its plan comes first (true); null where the plan step does not ask it.
    /// </summary>
    public static bool? OrderNeedsFailNow(World world, int slot, OrderEntry order)
    {
        var type = Saving.OrderSave.SavedRow(order.Row);
        if (TaskDataTable.Of(type) is not { } row || PlanSteps.KeepsHours(type)) return null;

        return RowStates.NeedFails(world, slot, row, Target(order));
    }

    /// <summary>
    /// The start of an order whose list B fails (the orders step's plan, <c>FUN_140510920</c> → <c>FUN_14050fe10</c>, docs/game/ai-tasks.md
    /// "Planner"): the planner follows only the first entry that fails (<see cref="RowStates.FirstUnmet"/>, <c>FUN_14051b630</c>), and the plan fails
    /// when none of that state's providers leads to a step (<see cref="Provided"/>), so the order stays queued with that state's line
    /// (<see cref="StateFailureLines"/>, null: none). The front is planned with the search flag, which offers the restricted providers; the second
    /// list without it, so they count there only for a character not of the player's faction (<c>FUN_14051bae0</c>). False when list B holds or a
    /// provider plans a step first, which the act-out carries out (the walk, getting up, a follow row's fight, <see cref="OrderEntry.Planned"/>).
    /// </summary>
    public static bool OrderBlocked(World world, int slot, OrderEntry order, out string? line)
    {
        line = null;
        var type = Saving.OrderSave.SavedRow(order.Row);
        var target = Target(order);
        if (TaskDataTable.Of(type) is not { } row || RowStates.FirstUnmet(world, slot, row, target) is not { } unmet) return false;

        var cold = world.Characters.Cold(slot)!;
        bool restricted = !cold.SecondList.Contains(order) || !DestinationAccess.PlayerSide(world, cold);
        var search = new ProviderSearch(world, slot, Repertoire.Of(world, cold), restricted, target);
        if (search.Provided(unmet, [type])) return false;

        line = StateFailureLines.Of(unmet.State);
        return true;
    }

    /// <summary>
    /// Whether the order names a character or site that resolves to nothing now (<see cref="RowStates.Gone"/>) and its plan fails on that
    /// (<see cref="OrderBlocked"/>): the original's order keeps its hand, and the order step plans it again on every update with the hand looked up
    /// anew (<c>FUN_140510920</c>, <c>FUN_140284510</c>), so it stays queued with that state's line where its list B fails on a state no provider
    /// gives (a follow's <c>!7</c>, a bed's or machine's <c>73</c>, a lock's <c>70</c>). Its desire and list A were asked first (the order step's
    /// pop).
    /// </summary>
    public static bool BlockedOnGone(World world, int slot, OrderEntry order, out string? line)
    {
        line = null;
        return RowStates.Gone(world, Target(order)) && OrderBlocked(world, slot, order, out line);
    }

    /// <summary>
    /// The list B entry the first step of the order's plan provides (<see cref="RowStates.FirstUnmet"/> at its start, <c>FUN_14051b630</c>); null when
    /// its list B held, or where the plan step does not ask it (<see cref="OrderNeedsFailNow"/>).
    /// </summary>
    public static TaskCondition? PlannedNeed(World world, int slot, OrderEntry order)
    {
        var type = Saving.OrderSave.SavedRow(order.Row);
        if (TaskDataTable.Of(type) is not { } row || PlanSteps.KeepsHours(type)) return null;

        return RowStates.FirstUnmet(world, slot, row, Target(order));
    }

    /// <summary>
    /// Whether the steps of the order's plan are over, so its own row runs and a failing list B ends it (<c>FUN_14050d540</c> starts the row once the
    /// last provider step ended, without testing its list B; the next plan step's (e), <c>FUN_14050d970</c>, then does): the entry the first step
    /// provided (<see cref="OrderEntry.Planned"/>) holds; with none recorded (a queued move started by an arrival), list B holds
    /// (<paramref name="fails"/> false).
    /// </summary>
    public static bool StepsOver(World world, int slot, OrderEntry order, bool fails)
    {
        if (order.Planned is not { } need) return !fails;

        return RowStates.Holds(world, slot, need.State, Target(order)) == need.Holds;
    }

    /// <summary>
    /// The row target of an order.
    /// </summary>
    static RowTarget Target(OrderEntry order) => new(order.Target, order.Site, order.TaskPoint, order.Detail as Items.GroundPick);

    /// <summary>
    /// The planner's search below an order's first unmet entry (<c>FUN_14050f800</c>, node expansion <c>FUN_14051bae0</c>, <b>Verified</b>; acceptance
    /// read in docs/game/ai-tasks.md "Planner"), on the states <see cref="RowStates"/> evaluates (one it does not evaluate counts as holding): a
    /// provider leads to a step when its own first unmet entry has a provider that does, down to one whose list B holds. A restricted provider counts
    /// only with <c>Restricted</c>, one that needs a target only when the order names one; every child takes the order's target. A finder row counts
    /// (its finder's pick is not evaluated here), so does one repeating an ancestor (accepted). A provider's desire is not asked (engine stand-in).
    /// </summary>
    readonly record struct ProviderSearch(World World, int Slot, int Repertoire, bool Restricted, RowTarget Target)
    {
        /// <summary>
        /// Whether a provider of <paramref name="need"/> leads to a step; <paramref name="chain"/> holds the rows above it.
        /// </summary>
        public bool Provided(TaskCondition need, List<TaskType> chain)
        {
            foreach (var provider in TaskDataTable.Providers(Repertoire, need))
                if (Offered(provider) && Leads(provider, chain)) return true;

            return false;
        }

        /// <summary>
        /// Whether the provider is a child the node expansion makes: restricted only when offered, needing a target only with one.
        /// </summary>
        bool Offered(TaskDataRow provider)
        {
            if (provider.Is(TaskDataFlags.Restricted) && !Restricted) return false;

            return !provider.Is(TaskDataFlags.NeedsTarget) || !Target.Character.IsNone || Target.Site is not null || Target.Item is not null;
        }

        /// <summary>
        /// Whether the provider is accepted, or its own first unmet entry is provided in turn.
        /// </summary>
        bool Leads(TaskDataRow provider, List<TaskType> chain)
        {
            if (provider.Is(TaskDataFlags.Finder) || chain.Contains(provider.Type)) return true;
            if (RowStates.FirstUnmet(World, Slot, provider, Target) is not { } unmet) return true;

            chain.Add(provider.Type);
            bool provided = Provided(unmet, chain);
            chain.RemoveAt(chain.Count - 1);
            return provided;
        }
    }

    /// <summary>
    /// <c>FUN_14050e5b0</c>'s skips of job <paramref name="job"/>, after the jobs switch: a row without +0x14 (<see cref="TaskDataFlags.KeepsJob"/>)
    /// that can run (its list B holds, <c>FUN_14060f940</c>) but neither acts nor has an effect has nothing to do; a row whose fresh class says it
    /// is finished, whose list A holds and whose effect test holds is done. A row with an effect and a finder holds its effect test only when the
    /// finder scores 0 or less, when its desire x finder drops it anyway (its contributor finds nothing), so it is never skipped here. Job 187 of a
    /// slave that obeys (state 1, not sneaking) takes its own branch before these tests, so they never skip it (#961). True with the reason in
    /// <paramref name="why"/>.
    /// </summary>
    public static bool JobSkipped(World world, int slot, PermaJob job, out string? why)
    {
        why = null;
        if (job.Type == TaskType.SlaveObedience && ObedientSlave.Obeys(world.Characters.Cold(slot)!)) return false;
        if (TaskDataTable.Of(job.Type) is not { } row) return false;

        var target = new RowTarget(job.Character, job.Site);
        if (!row.Is(TaskDataFlags.KeepsJob) && !row.Is(TaskDataFlags.Acts) && row.Effect is null
            && RowStates.Needs(world, slot, row, target) == true)
        {
            why = "nothing to do";
            return true;
        }

        if (row.Effect is not null && row.Is(TaskDataFlags.Finder)) return false;
        if (RowStates.Done(world, slot, row, target) != true || !TaskFinished.Fresh(world, slot, job.Type, job.Character)) return false;

        why = "done";
        return true;
    }

    /// <summary>
    /// The effect test <c>FUN_14050c8e0</c> for a row Meitou tests here: it holds for a row without an effect, and for one with an effect but no
    /// finder; with a finder it needs the finder to score 0 or less, which these tests do not ask (false).
    /// </summary>
    static bool EffectHolds(TaskDataRow row) => row.Effect is null || !row.Is(TaskDataFlags.Finder);
}
