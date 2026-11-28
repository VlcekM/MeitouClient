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
    /// <summary>
    /// The Set Free order: a player character frees a conscious prisoner from its cage.
    /// </summary>
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

    /// <summary>
    /// Whether a character is conscious (Set Free's precondition 125).
    /// </summary>
    static bool Conscious(CharacterCold c) => CharacterFacts.Conscious(c);   // Set Free's precondition 125

    /// <summary>
    /// The Get In order: the nearest player character able to gets into the free cage.
    /// </summary>
    void GetInInput(World world, GetInCageOrder order)
    {
        var table = world.Characters;
        if (world.Fixtures.Find(order.Cage) is not { Kind: FixtureKind.Cage } cage || !FixtureRegistry.IsFree(table, cage))
            return;

        int best = NearestWorker(world, order.Characters, cage, TaskType.UseCage);
        if (best < 0 || !table.Cold(best)!.Carrying.IsNone) return;
        var point = OrderEntry.AtBuilding(world, order, cage.Position);
        StartWork(world, best, CharacterTask.GoTo, TaskType.UseCage, cage.Id, queued: order.Queued, point: point);
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
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { Medical.Incapacitated: false, IsImprisoned: false } c
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

    /// <summary>
    /// Clears a character's lock work.
    /// </summary>
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
    /// 219 Removing Shackles (<c>Task_TakeOffCuffs</c>, <b>Verified</b>: its update queues <c>Character::setChainedMode(self, false)</c>,
    /// <c>FUN_14032e590</c>): the worn locked-armour item, in the boots section else the body one, leaves the equipment whatever its lock state.
    /// <b>Engine choice</b>: it goes to the character's inventory, else the ground (where the original sends it is <b>Unknown</b>); the planner's
    /// chained flag (<c>+0x320</c>) is read as wearing such an item.
    /// </summary>
    internal void TakeOffCuffs(World world, CharacterCold c, Vector3 at)
    {
        if (c.Inventory is not { } inv) return;
        var cuffs = inv.Items.FirstOrDefault(i => i.IsLocked && i.Section == "boots")
                ?? inv.Items.FirstOrDefault(i => i.IsLocked && i.Section == "armour");
        if (cuffs is not null) TakeOff(world, c, cuffs, at);
    }

    /// <summary>
    /// Worn shackles leave the equipment: into the wearer's inventory, else onto the ground at <paramref name="at"/> (engine choice, as
    /// <see cref="TakeOffCuffs"/>).
    /// </summary>
    internal void TakeOff(World world, CharacterCold c, Items.ItemInstance cuffs, Vector3 at)
    {
        var inv = c.Inventory!;
        inv.Items.Remove(cuffs);
        cuffs.Section = "main";
        if (!inv.Add(cuffs)) world.GroundItems.Drop(cuffs, at, 0);
        Assembly?.RefreshEquipment(c);
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

            if (!AtWorkSpot(world, cage, next[s], out var at, out float reach))
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

            if (FreeBy(world, table.IdOf(s), c.Faction, prisoner, Crimes))
                world.EmitSound(SoundKind.Unlocked, s, next[s].Position);
        }
    }

    /// <summary>
    /// The start of Task_ReleasePrisoner (<c>FUN_1403570a0</c>, Verified): the prisoner is marked freed (status <c>+0xea</c>, set unconditionally;
    /// the status update drops it outside every town, <see cref="Crime.ArrestSystem"/>), released, then a releaser of a faction that books bounties
    /// tells it it may go (event 46 from the releaser), else a releaser of the player's faction is thanked (event 13 from the prisoner); in those two
    /// cases the prisoner gets order 133 (<see cref="FreedPrisoner"/>). With <see cref="World.FreedSlaveFix"/> (Meitou fix) a slave a player-faction
    /// releaser frees stops obeying its owner.
    /// </summary>
    internal static bool FreeBy(World world, CharacterId releaser, int faction, CharacterId prisoner, CrimeSystem? crimes)
    {
        if (world.Characters.TryResolveNext(prisoner, out int p) && world.Characters.Cold(p) is { } pc) pc.Freed = true;
        if (!world.Fixtures.Release(world, prisoner, faction, crimes, jailer: true)) return false;

        if (faction == world.Player.Faction && world.FreedSlaveFix && world.Characters.Cold(p) is { SlaveState: 1 } slave)
            Kidnap.ObedientSlave.Disobey(world, slave);

        // The release message goes with Create_Character on the interface, for a player-faction prisoner with no slave state.
        if (world.Characters.Cold(p) is { IsPlayer: true, SlaveState: 0 })
            world.EmitSound(SoundKind.ReleasedFromPrison, p, world.Characters.Next[p].Position);
        var talk = world.SystemOf<Dialogue.DialogueSystem>();
        bool books = crimes?.Factions.Books(faction) == true;
        if (world.Characters.TryResolveNext(releaser, out int r) && talk is not null)
        {
            talk.Forgive(world, p, releaser);   // the prisoner forgives the releaser (FUN_14067aca0, both orders)
            if (books) talk.Forgive(world, r, prisoner);
        }

        if (books)
            world.Defer(new Dialogue.DialogueEvent(releaser, prisoner, PrisonerFreeToGo) { Tick = world.Tick + 1 });
        else if (faction == world.Player.Faction)
            world.Defer(new Dialogue.DialogueEvent(prisoner, releaser, UnlockMyCageOrShackles) { Tick = world.Tick + 1 });

        // In those two cases the prisoner is also ordered 133 (queue cleared); the hold is cleared already, so its leave finds no cage.
        if (books || faction == world.Player.Faction) FreedPrisoner.Give(world, p);

        return true;
    }
}
