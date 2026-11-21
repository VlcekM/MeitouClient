using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The escapes as goals of the one chooser (docs/simulation.md "Arrest and jail", "Engine goals"; facts: docs/game/ai-tasks.md "Finders of the jobs"): 256 Get out of
/// cage (opportunistic, plan <c>[256]</c>), 207 Escape cage (<c>[207]</c>) and 202 Escaping, tier 3, whose plan frees its own shackles (201 pick or
/// 185 cut, then 219 take them off) and gets out of its cage (133, 207 or 256). The act-out works the plan's front row and is done once that row's work
/// ends; 202, neither acting nor having an effect, is planned afresh at each think and so goes on to its next row, and is dropped once free.
/// </summary>
internal sealed class EscapeGoals(LockSystem locks)
{
    #region Act-outs

    /// <summary>
    /// The lock system that works the shackles and takes them off.
    /// </summary>
    readonly LockSystem _locks = locks;

    /// <summary>
    /// A character an escape goal holds: who, the goal, the row its act-out works, and whether that work was done at the start (taking the shackles
    /// off, leaving the cage).
    /// </summary>
    readonly record struct ActOut(CharacterId Id, TaskType Goal, TaskType Row, bool Instant);

    /// <summary>
    /// The characters an escape goal holds, by slot: added at the start, removed at the end or when done (hashed).
    /// </summary>
    readonly SortedDictionary<int, ActOut> _acting = [];

    /// <summary>
    /// The engine goals of the world (the AI's), resolved at the first step.
    /// </summary>
    DutyGoals? _duties;

    /// <summary>
    /// Scratch: the act-outs done in a step.
    /// </summary>
    readonly List<int> _done = [];

    /// <summary>
    /// Registers the three goals; 202 with the rows of its plan its act-out works (219, 201, 185, 133, 207, 256; 186 is no provider).
    /// </summary>
    internal void Register(DutyGoals goals)
    {
        var escaping = new Escaping(this);
        goals.Register(TaskType.GetOutOfCageOpportunistic, escaping);
        goals.Register(TaskType.GetOutOfCageEscape, escaping);
        goals.Register(TaskType.TotalEscape, escaping, TaskType.TakeOffMyShackles, TaskType.PickLockOnShackles, TaskType.CutShackles,
            TaskType.GetOutOfCageLegit, TaskType.GetOutOfCageEscape, TaskType.GetOutOfCageOpportunistic);
    }

    /// <summary>
    /// The act-outs' step (serial, the lock step): each character a goal still holds whose work has ended (done at the start, or its shackle job over)
    /// is done (<see cref="DutyGoals.Done"/>). One the goal no longer holds is left to the AI's sweep, which ends it.
    /// </summary>
    internal void Step(World world)
    {
        if (_acting.Count == 0) return;

        _duties ??= world.SystemOf<AiSystem>()?.Duties;
        _done.Clear();
        foreach (var (slot, act) in _acting)
            if (_duties?.Runs(world, slot, act.Goal) == true && Over(world, slot, act)) _done.Add(slot);

        foreach (int slot in _done)
        {
            var act = _acting[slot];
            _acting.Remove(slot);
            _duties!.Done(world, slot, act.Goal);
        }
    }

    /// <summary>
    /// Whether the act-out's work has ended: done at its start, or the shackle job no longer the character's.
    /// </summary>
    static bool Over(World world, int slot, in ActOut act) => act.Instant || world.Characters.Cold(slot)!.Shackles is null;

    /// <summary>
    /// The act-out's start (serial): the work of the plan's front row; false when it cannot be done now (no shackles to work or take off, a cage
    /// that stays shut).
    /// </summary>
    bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
    {
        if (plan.Count == 0 || world.Characters.Cold(ask.Slot) is not { } c) return false;

        var row = plan[0].Type;
        bool? instant = row switch
        {
            TaskType.TakeOffMyShackles => TakeOff(world, ask.Slot, c),
            TaskType.PickLockOnShackles => WorkShackles(world, ask, c, ShackleWork.Pick) ? false : null,
            TaskType.CutShackles => WorkShackles(world, ask, c, ShackleWork.Cut) ? false : null,
            TaskType.GetOutOfCageLegit => world.Fixtures.GetOutLegit(world, ask.Id, _locks.Crimes) ? true : null,
            TaskType.GetOutOfCageEscape or TaskType.GetOutOfCageOpportunistic => world.Fixtures.GetOut(world, ask.Id, _locks.Crimes) ? true : null,
            _ => null,
        };
        if (instant is not { } done) return false;

        _acting[ask.Slot] = new ActOut(ask.Id, ask.Type, row, done);
        return true;
    }

    /// <summary>
    /// 219 Removing Shackles (<see cref="LockSystem.TakeOffCuffs"/>): true when the character wears shackles no longer locked, which come off; null
    /// otherwise.
    /// </summary>
    bool? TakeOff(World world, int slot, CharacterCold c)
    {
        if (!LockSystem.Chained(c) || LockSystem.WornShackles(c)?.Lock is { EffectivelyLocked: true }) return null;

        _locks.TakeOffCuffs(world, c, world.Characters.Next[slot].Position);
        return true;
    }

    /// <summary>
    /// 201 Lockpicking Shackles or 185 Cutting shackles on the character's own locked shackles, in place (the lock system's shackle job); false when it
    /// wears none locked or the row is not its own.
    /// </summary>
    static bool WorkShackles(World world, in DutyAsk ask, CharacterCold c, ShackleWork work)
    {
        if (LockSystem.WornShackles(c)?.Lock is not { EffectivelyLocked: true }) return false;

        var row = work == ShackleWork.Pick ? TaskType.PickLockOnShackles : TaskType.CutShackles;
        var request = new TaskRequest(CharacterTask.Idle, row, TaskSource.Goal) { Target = ask.Id, Detail = work };
        return TaskStart.Start(world, ask.Slot, request) == StartResult.Started;
    }

    /// <summary>
    /// The goal ended (displaced, an order, a replan, taken, gone): the character is no longer held.
    /// </summary>
    void End(int slot, CharacterId id)
    {
        if (_acting.TryGetValue(slot, out var act) && act.Id == id) _acting.Remove(slot);
    }

    /// <summary>
    /// Adds the act-outs to the state hash, only while there are any.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_acting.Count == 0) return;

        hasher.Add(0x45736361);   // "Esca"
        hasher.Add(_acting.Count);
        foreach (var (slot, act) in _acting)
        {
            hasher.Add(slot);
            hasher.Add(act.Id);
            hasher.Add((int)act.Goal);
            hasher.Add((int)act.Row);
            hasher.Add(act.Instant);
        }
    }

    #endregion

    #region Desires and states

    /// <summary>
    /// The desire of a row (<b>Verified</b>, the registration <c>FUN_140319a60</c> and its stubs): 202 and 219 2.0 (AI slot 47), 185 and 207 1.0
    /// (slot 19), 133 0.5 (slot 41), 201 0.1 (slot 39), 256 <see cref="OpportunityDesire"/>.
    /// </summary>
    static float Desire(World world, in DutyAsk ask) => ask.Row switch
    {
        TaskType.TotalEscape or TaskType.TakeOffMyShackles => 2f,
        TaskType.CutShackles or TaskType.GetOutOfCageEscape => 1f,
        TaskType.GetOutOfCageLegit => 0.5f,
        TaskType.PickLockOnShackles => 0.1f,
        TaskType.GetOutOfCageOpportunistic => OpportunityDesire(world, ask.Slot),
        _ => 0f,
    };

    /// <summary>
    /// 256's desire (AI slot 38, <c>FUN_140599260</c>, <b>Verified</b>): 1.0 for a prisoner that is not chained (<c>+0x320</c>) whose cage has a
    /// lock not effectively locked; else 0.
    /// </summary>
    static float OpportunityDesire(World world, int slot)
    {
        if (world.Characters.Cold(slot) is not { InSomething: HeldState.Imprisoned } c || LockSystem.Chained(c)) return 0;

        return Cage(world, c)?.Lock is { EffectivelyLocked: false } ? 1 : 0;
    }

    /// <summary>
    /// The states of the rows for the character itself, the target of 202 (parallel: cold state and fixtures only serial steps write): 1 at its own
    /// place; 134 chained (<c>FUN_1405a1db0</c>); 114 its worn shackles locked (<c>FUN_1405a1f00</c>); 122 a tool (<c>FUN_1405989e0</c>); 167 the skill
    /// for its shackles' lock (<c>FUN_1408ca120</c>); 79 imprisoned (<c>FUN_140599570</c>); 54 of itself its cage's lock (<c>FUN_1405a00c0</c>); null for
    /// the rest and for 1 and 54 of another target.
    /// </summary>
    bool? State(World world, in DutyAsk ask, StateType state)
    {
        var c = world.Characters.Cold(ask.Slot)!;
        bool self = ask.Target.Character == ask.Id;
        return state switch
        {
            StateType.IsAtLocation when self => true,
            StateType.IsChained => LockSystem.Chained(c),
            StateType.IsWearingShacklesLocked => LockSystem.WornShackles(c)?.Lock is { EffectivelyLocked: true },
            StateType.HasTools => Lockpicking.BestTool(c) is not null,
            StateType.IHaveSufficientLockSkillForShackles => LockSystem.WornShackles(c)?.Lock is { } l && _locks.Picking.Allowed(c, l.Level),
            StateType.AmImprisoned => c.InSomething == HeldState.Imprisoned,
            StateType.IsDoorLocked when self => Cage(world, c)?.Lock is { EffectivelyLocked: true },
            _ => null,
        };
    }

    /// <summary>
    /// The cage the character is held in; null when none.
    /// </summary>
    static Fixture? Cage(World world, CharacterCold c) =>
        c is { InSomething: HeldState.Imprisoned, InWhat: { } where } ? world.Fixtures.Find(where) : null;

    #endregion

    #region Goals

    /// <summary>
    /// Goals 256, 207 and 202: one goal object answers for all three and the rows of 202's plan.
    /// </summary>
    sealed class Escaping(EscapeGoals goals) : IDutyGoal
    {
        /// <summary>
        /// The row's desire (<see cref="EscapeGoals.Desire"/>).
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => EscapeGoals.Desire(world, ask);

        /// <summary>
        /// 202's target is the character itself (its TaskData row needs one and has no finder); factor 1.
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target)
        {
            target = PlanTarget.OfCharacter(ask.Id, world.Characters.Previous[ask.Slot].Position);
            return 1;
        }

        /// <summary>
        /// The rows' states for the character itself (<see cref="EscapeGoals.State"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => goals.State(world, ask, state);

        /// <summary>
        /// Works the plan's front row.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Start(world, ask, plan);

        /// <summary>
        /// The character is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.End(slot, id);
    }

    #endregion
}
