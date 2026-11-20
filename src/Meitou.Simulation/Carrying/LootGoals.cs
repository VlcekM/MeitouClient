using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Carrying;

/// <summary>
/// NPC looting and arming as goals of the one chooser (docs/simulation.md "Carrying, rescue and looting", "Engine goals"): 177 "Looting the dead"
/// (plan <c>[1, 3, 177]</c>, the finder on the goal's own row) and 42 "Don't be unarmed" (plan <c>[1, 3, 8, 42]</c>, the finder on row 8). The
/// finders' picks are kept in a serial look for the think to read; a goal the chooser starts is acted out by a loot of the body or a pick-up of the
/// ground weapon, and is done when that ends.
/// </summary>
internal sealed class LootGoals
{
    #region Picks

    /// <summary>
    /// What a member's finder found at its last look: who looked, the body (none for a ground item), the ground item (-1 for a body) and where.
    /// </summary>
    readonly record struct Pick(CharacterId Id, CharacterId Body, int Item, Vector3 At)
    {
        /// <summary>
        /// The pick as a plan target: a character, or the place of a ground item (the item is this pick's, <see cref="ItemAt"/>).
        /// </summary>
        public PlanTarget Target => Body.IsNone ? PlanTarget.OfPlace(At) : PlanTarget.OfCharacter(Body, At);
    }

    /// <summary>
    /// The body each member that weighs 177 found at its last look (177's finder <c>FUN_14099e680</c>), by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, Pick> _bodies = [];

    /// <summary>
    /// The weapon each unarmed member that weighs 42 found at its last look (row 8's finder <c>AI::findWeapon</c>), by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, Pick> _weapons = [];

    /// <summary>
    /// The sets one look builds once, when a finder first needs them.
    /// </summary>
    struct LookScratch
    {
        /// <summary>
        /// The squads' goal lists (who works whom).
        /// </summary>
        public SquadGoals? Goals;
        /// <summary>
        /// The lootable characters.
        /// </summary>
        public List<int>? Bodies;
        /// <summary>
        /// The bodies being looted now.
        /// </summary>
        public HashSet<int>? Looted;
        /// <summary>
        /// The ground items being picked up now.
        /// </summary>
        public HashSet<int>? Claimed;
    }

    /// <summary>
    /// The finders' look (serial, the carry step), for each character on its slot's turn every <paramref name="every"/> ticks: a member with a
    /// mind of its own (<see cref="AiSystem.WeighsGoal"/>: its role, slave or race list holds the goal in the hour) keeps what 177's finder and,
    /// while it has no weapon, row 8's finder find; the think's desire, finder and states read the picks (engine: the original's finders run at
    /// each score, so a pick is at most a look period old).
    /// </summary>
    internal void Look(World world, CarrySystem carry, int every)
    {
        var table = world.Characters;
        var scratch = new LookScratch();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (world.Turn(s, every)) LookAt(world, carry, s, ref scratch);
        }

        Prune(table, _bodies);
        Prune(table, _weapons);
    }

    /// <summary>
    /// The picks of the character at <paramref name="s"/>: an NPC with a mind and an inventory keeps what its finders find, any other none.
    /// </summary>
    void LookAt(World world, CarrySystem carry, int s, ref LookScratch scratch)
    {
        var table = world.Characters;
        var cold = table.Next[s].Alive ? table.Cold(s) : null;
        if (cold is not { IsPlayer: false, Inventory: not null, Ai.Player: false })
        {
            _bodies.Remove(s);
            _weapons.Remove(s);
            return;
        }

        LookForBody(world, carry, s, cold, ref scratch);
        LookForWeapon(world, carry, s, cold, ref scratch);
    }

    /// <summary>
    /// 177's pick of the member at <paramref name="slot"/>, or none.
    /// </summary>
    void LookForBody(World world, CarrySystem carry, int slot, CharacterCold cold, ref LookScratch scratch)
    {
        var table = world.Characters;
        if (!AiSystem.WeighsGoal(world, slot, TaskType.LootTheDead))
        {
            _bodies.Remove(slot);
            return;
        }

        scratch.Goals ??= SquadGoals.Of(world);
        scratch.Bodies ??= CarrySystem.LootableBodies(table);
        var memory = world.SystemOf<DialogueSystem>()?.Memory;
        int body = carry.LootTarget(world, table, slot, cold, scratch.Goals, scratch.Bodies, memory);
        if (body < 0) _bodies.Remove(slot);
        else _bodies[slot] = new Pick(table.IdOf(slot), table.IdOf(body), -1, table.Next[body].Position);
    }

    /// <summary>
    /// Row 8's pick of the member at <paramref name="slot"/> while it has no weapon (state 4's provider is wanted only then), or none.
    /// </summary>
    void LookForWeapon(World world, CarrySystem carry, int slot, CharacterCold cold, ref LookScratch scratch)
    {
        var table = world.Characters;
        if (CarrySystem.HasWeapon(cold) || !AiSystem.WeighsGoal(world, slot, TaskType.NotBeUnarmed)
            || world.SystemOf<PopulationSystem>() is not { } population)
        {
            _weapons.Remove(slot);
            return;
        }

        scratch.Looted ??= CarrySystem.Looting(table);
        scratch.Claimed ??= CarrySystem.Claimed(table);
        var (item, body) = carry.FindWeapon(world, population.Data, table, slot, cold, scratch.Looted, scratch.Claimed);
        var id = table.IdOf(slot);
        if (item >= 0) _weapons[slot] = new Pick(id, CharacterId.None, item, world.GroundItems.Find(item)!.Position);
        else if (body >= 0) _weapons[slot] = new Pick(id, table.IdOf(body), -1, table.Next[body].Position);
        else _weapons.Remove(slot);
    }

    /// <summary>
    /// Drops the picks of slots that no longer hold the character that looked.
    /// </summary>
    static void Prune(CharacterTable table, SortedDictionary<int, Pick> picks)
    {
        List<int>? stale = null;
        foreach (var (slot, pick) in picks)
        {
            if (!table.TryResolveNext(pick.Id, out int at) || at != slot) (stale ??= []).Add(slot);
        }

        if (stale is not null) foreach (int slot in stale) picks.Remove(slot);
    }

    /// <summary>
    /// The pick of the asker in <paramref name="picks"/>; null for none or another character's.
    /// </summary>
    static Pick? PickOf(SortedDictionary<int, Pick> picks, in DutyAsk ask) =>
        picks.TryGetValue(ask.Slot, out var pick) && pick.Id == ask.Id ? pick : null;

    #endregion

    #region Act-outs

    /// <summary>
    /// A member a goal holds: who, the goal, the body it loots (none for a ground item) or the ground item it picks up (-1 for a body), and where.
    /// </summary>
    readonly record struct ActOut(CharacterId Id, TaskType Goal, CharacterId Body, int Item, Vector3 At)
    {
        /// <summary>
        /// The act-out's target as the plan holds it (<see cref="Pick.Target"/>).
        /// </summary>
        public PlanTarget Target => Body.IsNone ? PlanTarget.OfPlace(At) : PlanTarget.OfCharacter(Body, At);
    }

    /// <summary>
    /// The members 177 or 42 holds, by slot: added at the start, removed at the end or when done (hashed).
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
    /// Registers 177 with its plan row 3 and 42 with its plan rows 8 and 3 (<see cref="DutyGoals.Register"/>; row 3 resolves to the goal whose plan
    /// holds it).
    /// </summary>
    internal void Register(DutyGoals goals)
    {
        goals.Register(TaskType.LootTheDead, new Scavenging(this), TaskType.Pickup);
        goals.Register(TaskType.NotBeUnarmed, new Arming(this), TaskType.FindWeapon, TaskType.Pickup);
    }

    /// <summary>
    /// The act-outs' step (serial, the carry step): each member a goal still holds whose loot or pick-up has ended (taken, done or given up) is done
    /// (<see cref="DutyGoals.Done"/>), and one whose walk was lost (<see cref="Lost"/>) ends as a lost walk (<see cref="DutyGoals.LostWalk"/>: a
    /// body is ignored for 20 s); one the goal no longer holds is left to the AI's sweep, which ends it. Either way its finders look again at once,
    /// so the think after the end sees the body its squad tagged <see cref="MemoryTag.Looted"/> as gone (the original's finder runs at the score).
    /// </summary>
    internal void Step(World world, CarrySystem carry)
    {
        if (_acting.Count == 0) return;

        _duties ??= world.SystemOf<AiSystem>()?.Duties;
        _done.Clear();
        foreach (var (slot, act) in _acting)
        {
            if (_duties?.Runs(world, slot, act.Goal) == true && (Over(world, slot, act) || Lost(world, carry, slot, act))) _done.Add(slot);
        }

        var scratch = new LookScratch();
        foreach (int slot in _done)
        {
            var act = _acting[slot];
            bool lost = Lost(world, carry, slot, act);
            _acting.Remove(slot);

            if (lost) _duties!.LostWalk(world, slot, act.Goal, DutyGoals.KeyOf(act.Target));
            else _duties!.Done(world, slot, act.Goal);

            LookAt(world, carry, slot, ref scratch);
        }
    }

    /// <summary>
    /// Whether the act-out's walk was lost (the plan step's rule, <c>FUN_14050d970</c>, <b>Verified</b>): a loot whose body no path reaches
    /// (<see cref="CarrySystem.LootWalkFails"/>), or a pick-up given up with its item still on the ground (its walk ended short of it).
    /// </summary>
    static bool Lost(World world, CarrySystem carry, int slot, in ActOut act)
    {
        if (act.Item < 0) return carry.LootWalkFails(world, slot);

        return world.Characters.Cold(slot)!.PickingUpItem != act.Item && world.GroundItems.Find(act.Item) is not null;
    }

    /// <summary>
    /// Whether the act-out has ended: the loot is no longer the character's task, or the pick-up no longer its item.
    /// </summary>
    static bool Over(World world, int slot, in ActOut act)
    {
        var table = world.Characters;
        if (act.Item < 0) return table.Next[slot].Task != (byte)CharacterTask.Loot;

        return table.Cold(slot)!.PickingUpItem != act.Item;
    }

    /// <summary>
    /// The act-out's start (serial): the loot of the plan's body (row <paramref name="goal"/>, so the carry system's loot runs it), or the pick-up
    /// of the plan's ground weapon (row 3); false when the target is gone or another member already works it (the original's finders skip a
    /// target a squad mate works, <c>FUN_1402690e0</c>). A carrier refuses (engine choice: the loot and the pick-up need free hands).
    /// </summary>
    bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan, TaskType goal, SortedDictionary<int, Pick> picks)
    {
        if (!world.Characters.Cold(ask.Slot)!.Carrying.IsNone) return false;
        if (PlanTargetOf(plan) is not { } target) return false;

        var body = target.Character ?? CharacterId.None;
        int item = body.IsNone ? ItemAt(picks, ask, target) ?? -1 : -1;
        if (body.IsNone && item < 0 || Worked(ask.Slot, body, item)) return false;
        if (goal == TaskType.LootTheDead && TaggedLooted(world, ask.Slot, body)) return false;

        bool started = !body.IsNone ? StartLoot(world, ask.Slot, goal, body) : StartPickUp(world, ask.Slot, item, target.Position);
        if (!started) return false;

        _acting[ask.Slot] = new ActOut(ask.Id, goal, body, item, target.Position);
        return true;
    }

    /// <summary>
    /// The target of the plan's row 3 (none: no such step).
    /// </summary>
    static PlanTarget? PlanTargetOf(IReadOnlyList<PlanStep> plan)
    {
        foreach (var step in plan)
            if (step.Type == TaskType.Pickup && !step.Target.IsNone) return step.Target;
        return null;
    }

    /// <summary>
    /// Whether the looter's squad tagged <paramref name="body"/> <see cref="MemoryTag.Looted"/>: 177's finder skips such a body
    /// (<see cref="CarrySystem.LootTarget"/>), so a pick made before a squad mate's loot tagged it is refused at the start.
    /// </summary>
    static bool TaggedLooted(World world, int slot, CharacterId body) =>
        world.Characters.Cold(slot) is { SquadId: >= 0 } looter
        && world.SystemOf<DialogueSystem>()?.Memory.HasShort(world, looter.SquadId, body, MemoryTag.Looted) == true;

    /// <summary>
    /// Whether another member's act-out works the body <paramref name="body"/> or the ground item <paramref name="item"/>.
    /// </summary>
    bool Worked(int slot, CharacterId body, int item)
    {
        foreach (var (other, act) in _acting)
        {
            if (other != slot && (body.IsNone ? act.Item == item : act.Body == body)) return true;
        }

        return false;
    }

    /// <summary>
    /// Starts the loot of <paramref name="body"/> as row <paramref name="goal"/>, a goal's task back to standing; false when the body is no longer
    /// lootable or the row is not the character's.
    /// </summary>
    static bool StartLoot(World world, int slot, TaskType goal, CharacterId body)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(body, out int b) || table.Cold(b) is not { } victim || !CarrySystem.CanBeLooted(victim) || !victim.CarriedBy.IsNone)
            return false;

        return CarrySystem.Start(world, slot, CharacterTask.Loot, goal, TaskSource.Goal, body, CharacterTask.Idle);
    }

    /// <summary>
    /// Starts the pick-up (row 3, <see cref="Items.PickUps"/>) of the ground item <paramref name="item"/>: a walk to it, or the take where the
    /// character stands within reach; false when it is gone, another character is fetching it or the row is not the character's.
    /// </summary>
    static bool StartPickUp(World world, int slot, int item, Vector3 at)
    {
        var table = world.Characters;
        if (world.GroundItems.Find(item) is null || CarrySystem.Claimed(table).Contains(item)) return false;

        var task = Items.PickUps.InReach(table.Next[slot].Position, at) ? CharacterTask.Idle : CharacterTask.GoTo;
        var request = new TaskRequest(task, TaskType.Pickup, TaskSource.Goal) { Point = at, Detail = item };
        return TaskStart.Start(world, slot, request) == StartResult.Started;
    }

    /// <summary>
    /// The goal ended (displaced, an order, a replan, taken, gone): the member is no longer held, and a pick-up of the act-out's item is given up.
    /// </summary>
    void End(World world, int slot, CharacterId id)
    {
        if (!_acting.TryGetValue(slot, out var act) || act.Id != id) return;

        _acting.Remove(slot);
        if (act.Item < 0 || world.Characters.IdOf(slot) != id) return;   // gone: the slot may hold another character now
        if (world.Characters.Cold(slot) is { } cold && cold.PickingUpItem == act.Item) cold.PickingUpItem = null;
    }

    /// <summary>
    /// The target the finder of <paramref name="picks"/>' goal gives the asker: its act-out's while the goal holds it (the original keeps the
    /// current goal's finder hand), else its pick; factor 1.0 with one (<c>DAT_14167c308</c>), else 0.
    /// </summary>
    float Find(SortedDictionary<int, Pick> picks, in DutyAsk ask, out PlanTarget target)
    {
        if (_acting.TryGetValue(ask.Slot, out var act) && act.Id == ask.Id && act.Goal == ask.Type) target = act.Target;
        else if (PickOf(picks, ask) is { } pick) target = pick.Target;
        else target = default;

        return target.IsNone ? 0 : 1;
    }

    /// <summary>
    /// The ground item of a place target: the act-out's or the pick's at that place; null for none.
    /// </summary>
    int? ItemAt(SortedDictionary<int, Pick> picks, in DutyAsk ask, in PlanTarget target)
    {
        if (_acting.TryGetValue(ask.Slot, out var act) && act.Id == ask.Id && act.Item >= 0 && act.At == target.Position) return act.Item;

        return PickOf(picks, ask) is { Item: >= 0 } pick && pick.At == target.Position ? pick.Item : null;
    }

    /// <summary>
    /// The states of row 3 for a finder's target, read live (parallel: the last tick's places, cold state only serial steps write): 1 at it (the
    /// loot's reach of a body, the pick-up's of an item), 15 still lootable or on the ground, 3 an item no longer on the ground (a body never is:
    /// <c>Task_Loot_AI</c> ends by itself); null for the rest.
    /// </summary>
    bool? State(World world, CarrySystem carry, SortedDictionary<int, Pick> picks, in DutyAsk ask, StateType state)
    {
        var target = ask.Target;
        if (target.Kind != PlanTarget.Found) return null;

        if (target.Character is { } body)
        {
            var table = world.Characters;
            var victim = table.IsAlive(body) ? table.Cold(body.Slot) : null;
            return state switch
            {
                StateType.IsAtLocation => victim is not null && carry.AtBody(world, ask.Slot, body.Slot),
                StateType.IsItemLootable => victim is not null && CarrySystem.CanBeLooted(victim) && victim.CarriedBy.IsNone,
                StateType.IsSubjectInInventory => false,
                _ => null,
            };
        }

        bool onGround = ItemAt(picks, ask, target) is { } item && world.GroundItems.Find(item) is not null;
        return state switch
        {
            StateType.IsAtLocation => Items.PickUps.InReach(world.Characters.Previous[ask.Slot].Position, target.Position),
            StateType.IsItemLootable => onGround,
            StateType.IsSubjectInInventory => !onGround,
            _ => null,
        };
    }

    #endregion

    #region Goals

    /// <summary>
    /// Goal 177 LOOT_THE_DEAD as the chooser weighs it (tier and weight from the package's AI_TASK record; no commitment): plan <c>[1, 3, 177]</c>.
    /// </summary>
    sealed class Scavenging(LootGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 177's desire, AI slot 62 <c>FUN_140597ae0</c> (<b>Verified</b>): 1.0 while the character's posture is 0 (vfunc <c>0x368</c>), else 0; row
        /// 3's, AI slot 72 <c>FUN_140792650</c> (<b>Verified</b>): 0 while the character is down (vfunc <c>0x30</c>), else 1.0.
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => world.Characters.Cold(ask.Slot) is not { } cold ? 0
            : ask.Row == TaskType.Pickup ? (CharacterFacts.Down(cold) ? 0 : 1)
            : CharacterFacts.PostureZero(cold) ? 1 : 0;

        /// <summary>
        /// 177's finder <c>FUN_14099e680</c> (<c>FUN_14099ae10(ai, 0)</c>, <b>Verified</b>): the body, factor 1.0, else 0.
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.Find(goals._bodies, ask, out target);

        /// <summary>
        /// Row 3's states for the body (<see cref="LootGoals.State"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) =>
            world.SystemOf<CarrySystem>() is { } carry ? goals.State(world, carry, goals._bodies, ask, state) : null;

        /// <summary>
        /// Loots the plan's body.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) =>
            goals.Start(world, ask, plan, TaskType.LootTheDead, goals._bodies);

        /// <summary>
        /// The member is no longer held.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.End(world, slot, id);
    }

    /// <summary>
    /// Goal 42 NOT_BE_UNARMED as the chooser weighs it: plan <c>[1, 3, 8, 42]</c> while state 4 (a weapon) does not hold; with it the scorer drops
    /// the goal (its row neither acts nor has an effect).
    /// </summary>
    sealed class Arming(LootGoals goals) : IDutyGoal
    {
        /// <summary>
        /// 42's desire, AI slot 58 <c>FUN_140641610</c>: 2.0; row 8's, slot 19: 1.0; row 3's as for 177 (<b>Verified</b>, the registration's stubs).
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => ask.Row switch
        {
            TaskType.NotBeUnarmed => 2,
            TaskType.Pickup => world.Characters.Cold(ask.Slot) is { } cold && !CharacterFacts.Down(cold) ? 1 : 0,
            _ => 1,
        };

        /// <summary>
        /// Row 8's finder <c>AI::findWeapon</c> (<c>FUN_14099b1d0</c>, <b>Verified</b>): a ground weapon or a body with one, factor 1.0, else 0.
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.Find(goals._weapons, ask, out target);

        /// <summary>
        /// Row 3's states for the weapon or body (<see cref="LootGoals.State"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) =>
            world.SystemOf<CarrySystem>() is { } carry ? goals.State(world, carry, goals._weapons, ask, state) : null;

        /// <summary>
        /// Loots the plan's body (the loot takes weapons first) or picks up its ground weapon.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) =>
            goals.Start(world, ask, plan, TaskType.NotBeUnarmed, goals._weapons);

        /// <summary>
        /// The member is no longer held; its pick-up is given up.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.End(world, slot, id);
    }

    #endregion

    /// <summary>
    /// Adds the picks and the act-outs to the state hash, only while there are any.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_bodies.Count == 0 && _weapons.Count == 0 && _acting.Count == 0) return;

        hasher.Add(0x4C6F6F74);   // "Loot"
        HashPicks(ref hasher, _bodies);
        HashPicks(ref hasher, _weapons);
        foreach (var (slot, act) in _acting)
        {
            hasher.Add(slot);
            hasher.Add(act.Id);
            hasher.Add((int)act.Goal);
            hasher.Add(act.Body);
            hasher.Add(act.Item);
            hasher.Add(act.At);
        }
    }

    /// <summary>
    /// Adds one pick list to the state hash.
    /// </summary>
    static void HashPicks(ref StateHasher hasher, SortedDictionary<int, Pick> picks)
    {
        hasher.Add(picks.Count);
        foreach (var (slot, pick) in picks)
        {
            hasher.Add(slot);
            hasher.Add(pick.Id);
            hasher.Add(pick.Body);
            hasher.Add(pick.Item);
            hasher.Add(pick.At);
        }
    }
}
