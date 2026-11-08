using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Locks;

/// <summary>How a character gets shackles off: the original's context-menu verbs Unlock Shackles (task 201), Use Tools (185) and Use Strength (186).</summary>
public enum ShackleWork : byte { Pick, Cut, Break, PutOn }

/// <summary>A character working on <see cref="Wearer"/>'s shackles (its own or another's).</summary>
public sealed record ShackleJob(CharacterId Wearer, ShackleWork Work);

/// <summary>
/// Put shackles on <see cref="Wearer"/> (down or caged, wearing none): the first of the characters (the selection when empty) carrying unworn shackles walks to it and puts them on, locked.
/// <b>Engine</b> stand-in for the original's drag onto the target's armour slot in its inventory window, which Meitou does not have.
/// </summary>
public sealed record PutShacklesOrder(IReadOnlyList<CharacterId> Characters, CharacterId Wearer) : OrderCommand;

/// <summary>Work on <see cref="Wearer"/>'s locked shackles: the first of the characters (the selection when empty) who may, walking to the wearer unless it is its own.</summary>
public sealed record ShackleOrder(IReadOnlyList<CharacterId> Characters, CharacterId Wearer, ShackleWork Work) : OrderCommand;

/// <summary>
/// Shackles (docs/game/locks.md "Lockpicking", "Cutting and breaking"; docs/simulation.md "Shackles"): worn ARMOUR with <c>is locked</c> holds a lock that keeps it on. Picking it rolls
/// like a cage's lock, cutting wears its hardness down with a tool, breaking is one strength test. Once it is unlocked or broken the wearer can take it off.
/// </summary>
public sealed partial class LockSystem
{
    /// <summary>A worker beside the wearer works within this distance (engine: a cage's use range).</summary>
    public const float ShackleReach = 25;
    /// <summary>Dialogue events 13 (EV_UNLOCK_MY_CAGE_OR_SHACKLES) and 46 (EV_PRISONER_FREE_TO_GO).</summary>
    const int UnlockMyCageOrShackles = 13, UnlockMyCageAttempt = 14, PrisonerFreeToGo = 46;

    /// <summary>The worn locked shackles of a character (null: none).</summary>
    public static Items.ItemInstance? WornShackles(CharacterCold? c) => c?.Inventory?.Items.FirstOrDefault(i => i.LockedOn);

    void ShackleInput(World world, ShackleOrder order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Wearer, out int w) || WornShackles(table.Cold(w)) is null) return;
        foreach (var id in Who(world, order.Characters))
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false } c || !c.CarriedBy.IsNone) continue;
            if (c.IsImprisoned && id != order.Wearer) continue;   // from a cage only its own
            if (!TaskStart.Eligible(world, c, RowOf(order.Work))) continue;
            if (order.Work == ShackleWork.Cut && Lockpicking.BestTool(c) is null)
            {
                Say(world, c, "I don't have any tools");
                continue;
            }
            StartWork(world, s, id != order.Wearer ? CharacterTask.GoTo : CharacterTask.Idle, RowOf(order.Work), null, order.Wearer, order.Work, order.Queued);
            break;
        }
    }

    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    /// <summary>Rebuilds a character's fighter and look after its worn kit changed (null: headless worlds without data, nothing to rebuild).</summary>
    internal CharacterAssembly? Assembly { get; set; }

    /// <summary>Unworn shackles a character carries that can be put on someone (not broken; null: none).</summary>
    public static Items.ItemInstance? CarriedShackles(CharacterCold? c) =>
        c?.Inventory?.All().FirstOrDefault(i => i.IsLocked && !Items.InventoryCommands.IsEquipmentSection(i.Section) && i.Lock is { Broken: false } && i.Quantity == 1);

    /// <summary>Whether shackles can be put on <paramref name="c"/>: alive, down or caged, wearing no shackles, not carried.</summary>
    public static bool CanBeShackled(CharacterCold? c, bool alive) =>
        alive && c is { Inventory: not null, CarriedBy.IsNone: true } && c.Medical is not { Dead: true } && (c.IsImprisoned || c.Medical is { Incapacitated: true }) && !c.Inventory.Items.Any(i => i.IsLocked && Items.InventoryCommands.IsEquipmentSection(i.Section));

    void PutShacklesInput(World world, PutShacklesOrder order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Wearer, out int w) || !CanBeShackled(table.Cold(w), table.Next[w].Alive)) return;
        foreach (var id in Who(world, order.Characters))
        {
            if (id == order.Wearer || !table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, IsImprisoned: false } c || !c.CarriedBy.IsNone) continue;
            if (CarriedShackles(c) is null) continue;
            StartWork(world, s, CharacterTask.GoTo, RowOf(ShackleWork.PutOn), null, order.Wearer, ShackleWork.PutOn, order.Queued);
            return;
        }
        world.Post("No one selected carries shackles.");
    }

    /// <summary>
    /// The shackles go from the worker's inventory onto the wearer's armour slot, locked again (a picked pair locks when put on: engine choice, the original's re-lock is <b>Unknown</b>).
    /// No crime is booked (<b>Unknown</b> in the original). False when the slot is taken.
    /// </summary>
    bool PutOn(World world, CharacterCold worker, CharacterCold wearer, Items.ItemInstance item)
    {
        if (!worker.Inventory!.Remove(item)) return false;
        item.Lock!.Locked = true;
        item.Lock.PickTime = 0;
        item.Lock.NextRoll = LockState.FirstRoll;
        // What the wearer has on in that slot goes into its own inventory first (engine: the original's drop onto a taken slot swaps the two).
        var inventory = wearer.Inventory!;
        if (inventory.Items.FirstOrDefault(i => i.Section == item.EquipmentSection) is { } worn && !worn.LockedOn)
        {
            inventory.Items.Remove(worn);
            var (section, x, y) = (worn.Section, worn.X, worn.Y);
            worn.Section = "main";
            if (!inventory.Add(worn)) { (worn.Section, worn.X, worn.Y) = (section, x, y); inventory.Items.Add(worn); }
        }
        if (!Items.InventoryCommands.TryEquip(inventory, item))
        {
            item.Section = "main";
            worker.Inventory.Add(item);
            return false;
        }
        Assembly?.RefreshEquipment(wearer);
        return true;
    }

    /// <summary>Once a slow tick: each worker in reach of its wearer works the shackles; one out of reach walks on (a wearer that moved is followed).</summary>
    void ShackleWorkers(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { Shackles: { } job } c) continue;
            bool putOn = job.Work == ShackleWork.PutOn;
            Items.ItemInstance? item = null;
            if (c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone || !table.TryResolveNext(job.Wearer, out int w)
                || (putOn ? !CanBeShackled(table.Cold(w), next[w].Alive) || (item = CarriedShackles(c)) is null : (item = WornShackles(table.Cold(w))) is null))
            {
                c.Shackles = null;
                continue;
            }
            if (w != s)
            {
                float d = Vector2.Distance(Flat(next[s].Position), Flat(next[w].Position));
                if (d > ShackleReach)
                {
                    // Walking: a new order (another goal far from the wearer) ends it; a wearer that moved is followed.
                    if (next[s].Task != (byte)CharacterTask.GoTo) { c.Shackles = null; continue; }
                    if (Vector2.Distance(next[s].Goal, Flat(next[w].Position)) > ShackleReach) TaskStart.StepTo(table, s, Flat(next[w].Position), c.RunSpeed);
                    continue;
                }
                if (next[s].Task == (byte)CharacterTask.GoTo) AtWork(world, s);
            }
            if (c.StaggeredUntil > world.Tick) continue;   // a stagger interrupts the work
            switch (job.Work)
            {
                case ShackleWork.PutOn:
                    c.Shackles = null;
                    var wearer = table.Cold(w)!;
                    if (PutOn(world, c, wearer, item))
                    {
                        // The sound of the original's drop onto the armour slot (the interface's Item_Drop); only the player's order has one.
                        if (c.IsPlayer)
                        {
                            world.Post(Loc.Tr("{1} put {2} on {3}", c.Name, item.Name, wearer.Name));
                            world.EmitSound(SoundKind.ShacklesOn, s, next[s].Position, item.Record);
                        }
                    }
                    else if (c.IsPlayer) world.Post(Loc.Tr("{1} already wears something in that slot", wearer.Name));
                    break;
                case ShackleWork.Pick:
                    switch (Picking.StepShackles(world, s, job.Wearer, item, world.TickSeconds, Crimes))
                    {
                        case PickOutcome.Open: c.Shackles = null; break;
                        case PickOutcome.Success: c.Shackles = null; PickFloater(world, s, true); Freed(world, s, c, job.Wearer, null); break;
                        case PickOutcome.Failed: PickFloater(world, s, false); FailedPick(world, s, job.Wearer); break;
                        case PickOutcome.NotAllowed: c.Shackles = null; if (c.IsPlayer) Say(world, c, "I'm not skilled enough..."); break;
                    }
                    break;
                case ShackleWork.Cut:
                    if (Lockpicking.BestTool(c) is null) { c.Shackles = null; if (c.IsPlayer) Say(world, c, "I don't have any tools"); break; }
                    if (Picking.CutShackles(world, s, job.Wearer, item, world.TickSeconds, Crimes)) { c.Shackles = null; Freed(world, s, c, job.Wearer, "Shackles broken!"); }
                    break;
                case ShackleWork.Break:
                    c.Shackles = null;
                    if (Picking.BreakShackles(world, s, job.Wearer, item, Crimes)) BruteForced(world, s, c, job.Wearer);
                    else if (c.IsPlayer) Say(world, c, "I'm not strong enough to do that");
                    break;
            }
        }
    }

    /// <summary>A failed lockpick roll on shackles: the wearer says event 14 to the picker (Task_PickLock, FUN_140359160; no player-only test).</summary>
    static void FailedPick(World world, int s, CharacterId wearer)
    {
        var worker = world.Characters.IdOf(s);
        if (!wearer.IsNone && wearer != worker) world.Defer(new Dialogue.DialogueEvent(wearer, worker, UnlockMyCageAttempt) { Tick = world.Tick + 1 });
    }

    /// <summary>
    /// A lock or shackles broken by strength (Task_BruteForceLock / Shackles update <c>FUN_140352c50</c>, <b>Verified</b>): the sound, the log line "Shackles broken!" and the floating "*Crunck*"
    /// (both, whatever the lock), and the freed character (a cage's occupant, the shackles' wearer) thanks the breaker like any other release.
    /// </summary>
    static void BruteForced(World world, int s, CharacterCold c, CharacterId freed)
    {
        world.EmitSound(SoundKind.Unlocked, s, world.Characters.Next[s].Position);
        if (c.IsPlayer)
        {
            Say(world, c, "Shackles broken!");
            Say(world, c, "*Crunck*");
        }
        Thank(world, world.Characters.IdOf(s), freed);
    }

    static void Freed(World world, int s, CharacterCold c, CharacterId wearer, string? message)
    {
        world.EmitSound(SoundKind.Unlocked, s, world.Characters.Next[s].Position);
        if (c.IsPlayer && message is not null) Say(world, c, message);
        // An NPC freed by a player character thanks it (EV_UNLOCK_MY_CAGE_OR_SHACKLES, as for a player releasing a prisoner, economy.md 11.5).
        if (c.IsPlayer) Thank(world, world.Characters.IdOf(s), wearer);
    }

    static void Thank(World world, CharacterId worker, CharacterId freed)
    {
        if (freed != worker && world.Characters.TryResolveNext(freed, out int w) && world.Characters.Cold(w) is { IsPlayer: false })
            world.Defer(new Dialogue.DialogueEvent(freed, worker, UnlockMyCageOrShackles) { Tick = world.Tick + 1 });
    }
}