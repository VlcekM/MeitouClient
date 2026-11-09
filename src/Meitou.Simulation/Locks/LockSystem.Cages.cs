using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Set Free (110, Task_ReleasePrisoner): the nearest of the characters (the selection when empty) who may walks to <see cref="Prisoner"/>'s cage and
/// lets it out; refused while the cage is locked. Out of another faction's cage it is a crime like lifting the prisoner out.
/// </summary>
public sealed record SetFreeOrder(IReadOnlyList<CharacterId> Characters, CharacterId Prisoner) : OrderCommand;

/// <summary>
/// Get in (107, Task_UseCage): the nearest of the characters (the selection when empty) who may walks into the free, unlocked <see cref="Cage"/> and
/// is imprisoned there.
/// </summary>
public sealed record GetInCageOrder(IReadOnlyList<CharacterId> Characters, string Cage) : OrderCommand;

/// <summary>
/// The cage verbs of a clicked cage (docs/game/ui-input.md "Context menu verbs", cage rows): Set Free and Get in. Put in is
/// <see cref="Crime.PutInCageOrder.Cage"/>, Pick Lock of an empty cage <see cref="PickLockOrder.Cage"/>, Use Tools and Use Strength
/// <see cref="ForceLockOrder"/>.
/// </summary>
public sealed partial class LockSystem
{
    void SetFreeInput(World world, SetFreeOrder order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Prisoner, out int p) || table.Cold(p) is not { IsImprisoned: true, InWhat: { } where } pc
                || world.Fixtures.Find(where) is not { } cage)
            return;

        if (!Conscious(pc)) return;
        int best = NearestWorker(world, order.Characters, cage, TaskType.ReleasePrisoner);
        if (best >= 0)
            StartWork(world, best, CharacterTask.GoTo, TaskType.ReleasePrisoner, cage.Id, order.Prisoner, queued: order.Queued);
    }

    static bool Conscious(CharacterCold c) => CharacterFacts.Conscious(c);   // Set Free's precondition 125

    void GetInInput(World world, GetInCageOrder order)
    {
        var table = world.Characters;
        if (world.Fixtures.Find(order.Cage) is not { Kind: FixtureKind.Cage } cage || !FixtureRegistry.IsFree(table, cage))
            return;

        int best = NearestWorker(world, order.Characters, cage, TaskType.UseCage);
        if (best < 0 || !table.Cold(best)!.Carrying.IsNone) return;
        StartWork(world, best, CharacterTask.GoTo, TaskType.UseCage, cage.Id, queued: order.Queued);
    }

    /// <summary>
    /// The slot of the player's free, awake, uncarried character whose repertoire has <paramref name="row"/> nearest <paramref name="where"/> among
    /// <paramref name="who"/> (the selection when empty); -1 when none.
    /// </summary>
    static int NearestWorker(World world, IReadOnlyList<CharacterId> who, Fixture where, TaskType row)
    {
        var table = world.Characters;
        int best = -1;
        float bestDistance = float.MaxValue;
        foreach (var id in Who(world, who))
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, IsImprisoned: false } c
                    || !c.CarriedBy.IsNone
                || !TaskStart.Eligible(world, c, row)) continue;
            float d = Vector2.Distance(Flat(table.Next[s].Position), Flat(where.Position));
            if (d < bestDistance) (best, bestDistance) = (s, d);
        }

        return best;
    }

    /// <summary>
    /// Whether a character walking to a fixture's spot <paramref name="at"/> was given another order (a new goal) or its path failed. Its goal may
    /// lie off the spot by the floor search of <see cref="MovementSystem.GoToFloor"/>.
    /// </summary>
    static bool Redirected(in CharacterHot n, Vector2 at, float reach) =>
        n.Task != (byte)CharacterTask.GoTo || Vector2.Distance(n.Goal, at) > MathF.Max(reach, IWalkability.FloorSearch);

    static void ClearWork(CharacterCold c)
    {
        c.PickingLock = null;
        c.Shackles = null;
        c.ForcingLock = null;
        c.DoorJob = null;
        c.Freeing = CharacterId.None;
        c.EnteringCage = null;
    }

    /// <summary>
    /// Whether the character's role list in its squad's running package holds one of the goals in its hours.
    /// </summary>
    static bool Lists(Blackboard board, Squad squad, CharacterId id, CharacterCold c, int hour, TaskType a, TaskType b,
            TaskType other = TaskType.None)
    {
        var roles = board.Roles[(int)AiSystem.RoleOf(squad, id, c)];
        for (int g = 0; g < roles.Count; g++)
            if (roles[g].Task.Type is var t && (t == a || t == b || t == other) && roles[g].InWindow(hour)) return true;
        return false;
    }

    /// <summary>
    /// 202 Escaping (docs/game/ai-tasks.md "Finders of the jobs"; <c>Task_TakeOffCuffs</c> 219 provides its !134 through 201 / 185 / 186), every
    /// <see cref="EscapeEveryTicks"/> ticks: an awake AI character wearing locked shackles whose role list holds 202 sets to work on them, cutting
    /// with a tool, else picking (engine choice: the planner's pick among the three is <b>Unknown</b>). Caged, it first needs the cage open
    /// (<see cref="OpportunisticEscapes"/>). One that wears shackles no longer locked takes them off (<see cref="TakeOffCuffs"/>).
    /// </summary>
    void ShackleEscapes(World world)
    {
        if (world.Tick % EscapeEveryTicks != 0) return;
        var table = world.Characters;
        int hour = world.Hour;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive
                    || table.Cold(s) is not { IsImprisoned: false, IsPlayer: false, Shackles: null, Medical.Incapacitated: false,
                            CarriedBy.IsNone: true, Ai: { Squad: { Ai: { } board } squad } } c
                || !Lists(board, squad, table.IdOf(s), c, hour, TaskType.TotalEscape, TaskType.TotalEscape)) continue;
            if (WornShackles(c) is null)
            {
                TakeOffCuffs(world, c, table.Next[s].Position);
                continue;
            }

            c.PickingLock = null;
            c.Shackles = new ShackleJob(table.IdOf(s), Lockpicking.BestTool(c) is null ? ShackleWork.Pick : ShackleWork.Cut);
        }
    }

    const int EscapeEveryTicks = 30;

    /// <summary>
    /// 219 Removing Shackles (<c>Task_TakeOffCuffs</c>, <b>Verified</b>: its update queues <c>Character::setChainedMode(self, false)</c>,
    /// <c>FUN_14032e590</c>): the worn locked-armour item, in the boots section else the body one, leaves the equipment whatever its lock state.
    /// <b>Engine choice</b>: it goes to the character's inventory, else the ground (where the original sends it is <b>Unknown</b>); the planner's
    /// chained flag (<c>+0x320</c>) is read as wearing such an item.
    /// </summary>
    void TakeOffCuffs(World world, CharacterCold c, Vector3 at)
    {
        if (c.Inventory is not { } inv) return;
        var cuffs = inv.Items.FirstOrDefault(i => i.IsLocked && i.Section == "boots")
                ?? inv.Items.FirstOrDefault(i => i.IsLocked && i.Section == "armour");
        if (cuffs is null) return;
        inv.Items.Remove(cuffs);
        cuffs.Section = "main";
        if (!inv.Add(cuffs)) world.GroundItems.Drop(cuffs, at, 0);
        Assembly?.RefreshEquipment(c);
    }

    /// <summary>
    /// 256 Get out of cage and 207 Escape cage (opportunistic; <c>Task_GetOutOfCage</c> with the escape flag, docs/game/ai-tasks.md "Arrest and
    /// prison rows"): an AI prisoner, awake, whose role list holds the goal in its hours, walks out as soon as its cage is not effectively locked
    /// (<see cref="FixtureRegistry.GetOut"/> refuses a locked one; it is an escape unless it is a kidnap victim, whatever the sentence).
    /// </summary>
    void OpportunisticEscapes(World world)
    {
        var table = world.Characters;
        int hour = world.Hour;
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || FixtureRegistry.IsFree(table, f) || f.Lock is { Locked: true }
                    || !table.TryResolveNext(f.Occupant, out int s))
                continue;

            if (table.Cold(s) is not { IsImprisoned: true, IsPlayer: false, Medical.Incapacitated: false, Ai: { Squad: { Ai: { } board } squad } } c)
                continue;
            // 256, 207 (Escape cage: A !79, B !54, the same task with the escape flag) or 202 TOTAL_ESCAPE, whose !79 the cage rows (133 / 207 / 256)
            // provide once the cage is unlocked.
            if (Lists(board, squad, f.Occupant, c, hour, TaskType.GetOutOfCageOpportunistic, TaskType.TotalEscape, TaskType.GetOutOfCageEscape))
                world.Fixtures.GetOut(world, f.Occupant, Crimes);
        }
    }

    /// <summary>
    /// Each tick: a character in reach of the cage it was sent to lets its prisoner out or gets in; one given another order, downed or gone gives up.
    /// </summary>
    void CageWorkers(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { } c || c.Freeing.IsNone && c.EnteringCage is null) continue;
            Fixture? cage = null;
            if (!c.Freeing.IsNone)
            {
                cage = table.TryResolveNext(c.Freeing, out int p) && table.Cold(p) is { IsImprisoned: true, InWhat: { } where }
                        ? world.Fixtures.Find(where) : null;
            }
            else if (world.Fixtures.Find(c.EnteringCage!) is { Kind: FixtureKind.Cage } entering && FixtureRegistry.IsFree(table, entering))
                cage = entering;
            if (cage is null || c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone || c.IsImprisoned || !c.Carrying.IsNone
                    && c.EnteringCage is not null)
            {
                c.Freeing = CharacterId.None;
                c.EnteringCage = null;
                continue;
            }

            float reach = cage.UseRange + Reach;
            var at = Flat(cage.Position);
            if (Vector2.Distance(Flat(next[s].Position), at) > reach)
            {
                // Still walking; another order (a new goal) or a failed path ends it.
                if (Redirected(next[s], at, reach))
                {
                    c.Freeing = CharacterId.None;
                    c.EnteringCage = null;
                }

                continue;
            }

            if (next[s].Task == (byte)CharacterTask.GoTo) AtWork(world, s);
            if (c.Freeing.IsNone)
            {
                c.EnteringCage = null;
                if (cage.Lock is { EffectivelyLocked: true })
                {
                    if (c.IsPlayer) Say(world, c, "It's locked.");
                    continue;
                }

                world.Fixtures.Imprison(world, table.IdOf(s), cage, constants.PrisonTime);
                continue;
            }

            var prisoner = c.Freeing;
            c.Freeing = CharacterId.None;

            // A locked cage of its own faction is opened by the release itself (Task_ReleasePrisoner clears the lock, Verified); another faction's
            // must be opened first.
            if (cage.Lock is { EffectivelyLocked: true } && cage.Owner != c.Faction)
            {
                if (c.IsPlayer) Say(world, c, "It's locked.");
                continue;
            }

            if (!table.TryResolveNext(prisoner, out int ps) || table.Cold(ps) is not { } pc || !Conscious(pc)) continue;

            // Letting a prisoner out of another faction's cage is the crime of lifting it out (FixtureRegistry.LiftOut): a pending code 7 against the
            // cage's owner.
            if (cage.Owner >= 0 && cage.Owner != c.Faction)
                Crimes?.SetPending(world, s, CrimeCode.Terrorism, cage.Owner, prisoner, CrimeSystem.MinTimer);

            if (FreeBy(world, table.IdOf(s), c.Faction, prisoner))
                world.EmitSound(SoundKind.Unlocked, s, next[s].Position);
        }
    }

    /// <summary>
    /// The start of Task_ReleasePrisoner (<c>FUN_1403570a0</c>, Verified): the prisoner is marked freed (status <c>+0xea</c>, set unconditionally;
    /// the status update drops it outside every town, <see cref="Crime.ArrestSystem"/>), released, then a releaser of a faction that books bounties
    /// tells it it may go (event 46 from the releaser), else a releaser of the player's faction is thanked (event 13 from the prisoner).
    /// </summary>
    bool FreeBy(World world, CharacterId releaser, int faction, CharacterId prisoner)
    {
        if (world.Characters.TryResolveNext(prisoner, out int p) && world.Characters.Cold(p) is { } pc) pc.Freed = true;
        if (!world.Fixtures.Release(world, prisoner, faction, Crimes)) return false;
        var talk = world.SystemOf<Dialogue.DialogueSystem>();
        if (world.Characters.TryResolveNext(releaser, out int r) && talk is not null)
        {
            talk.Forgive(world, p, releaser);   // the prisoner forgives the releaser (FUN_14067aca0, both orders)
            if (Crimes?.Factions.Books(faction) == true) talk.Forgive(world, r, prisoner);
        }

        if (Crimes?.Factions.Books(faction) == true)
            world.Defer(new Dialogue.DialogueEvent(releaser, prisoner, PrisonerFreeToGo) { Tick = world.Tick + 1 });
        else if (faction == world.Player.Faction)
            world.Defer(new Dialogue.DialogueEvent(prisoner, releaser, UnlockMyCageOrShackles) { Tick = world.Tick + 1 });

        return true;
    }
}