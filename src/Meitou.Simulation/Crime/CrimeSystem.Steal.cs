using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Crime;

/// <summary>
/// Stealing from another faction's container (docs/game/carry-loot.md "Theft and witnesses"): the move sets a pending crime (the call site's code),
/// then the steal roll (<c>FUN_140794810</c>, chance <c>FUN_1407944d0</c>, <b>Observed</b>) decides whether the container's watcher catches the
/// thief. Caught: the move is refused, the watcher says event 8 and remembers the thief (long-term STOLE_FROM_ME), and the pending crime becomes a
/// Theft with timer 30, which witnesses and the guards' event-8 lines answer as any other.
/// </summary>
public sealed partial class CrimeSystem
{
    /// <summary>
    /// Thievery XP: a steal roll gives 2 x 20 when it succeeds and 2 x 80 when caught; a stolen ground pick-up 20 (<b>Observed</b>).
    /// </summary>
    public const float StealXp = 40, CaughtXp = 160, PickUpXp = 20;
    /// <summary>
    /// The timer of the Theft a caught thief gets (<b>Observed</b>).
    /// </summary>
    public const float CaughtTimer = 30;
    /// <summary>
    /// The distance over which the watcher's distance term goes from 0.5 to 1 (<b>Observed</b>).
    /// </summary>
    public const float WatchDistance = 200;
    /// <summary>
    /// The dialogue events of the roll: caught (THIEF_CAUGHT_STEALING_FROM_ME) and the in-bed watcher's line.
    /// </summary>
    public const int CaughtEvent = 8, InBedEvent = 47;
    /// <summary>
    /// Trains the thief's thievery (null: no XP).
    /// </summary>
    public XpService? Xp { get; set; }

    /// <summary>
    /// The effective thievery: stat 10 times its body multiplier.
    /// </summary>
    public static float Thievery(CharacterCold c) => (c.Stats?[StatsEnumerated.Thieving] ?? 0) * (c.Medical?.StatMultiplier(StatsEnumerated.Thieving) ?? 1);

    /// <summary>
    /// The chance that a steal from a container goes unseen (<c>FUN_1407944d0</c>, <b>Observed</b>): t = lerp(0.5, 1, clamp(distance / 200)), halved
    /// when the watcher is not inside the container's building; p = lerp(0.3, 2.25, thievery / 100 x t); x1.5 when the watcher is on another floor.
    /// Not clamped: 1 or more always succeeds.
    /// </summary>
    public static float StealChance(float thievery, float distance, bool otherBuilding, bool otherFloor)
    {
        float t = 0.5f + 0.5f * Math.Clamp(distance / WatchDistance, 0, 1);
        if (otherBuilding) t *= 0.5f;
        float p = 0.3f + 1.95f * thievery * 0.01f * t;
        return otherFloor ? p * 1.5f : p;
    }

    /// <summary>
    /// The container's watcher (<c>FUN_1407944d0</c> -> <c>FUN_140792ce0</c>, <b>Observed</b>): the nearest to the thief of the members of the
    /// resident squad of the building the container is furniture of (<c>Building::residentSquad</c>, its active platoon), not down, those on the
    /// thief's floor first; none without a resident squad. -1: none. Engine: every squad living there counts (Meitou may house more than one).
    /// </summary>
    static int Watcher(World world, int thief, Locks.Fixture container)
    {
        var table = world.Characters;
        int floor = world.Sight.FloorOf(thief, table.Next[thief].Position);
        var at = table.Next[thief].Position;
        int same = -1, other = -1;
        float sameD = float.MaxValue, otherD = float.MaxValue;
        foreach (var squad in world.Squads.All)
        {
            if (squad.HomeBuilding?.Key != container.Building) continue;
            foreach (var id in squad.Members)
            {
                if (!table.TryResolveNext(id, out int s) || !table.Next[s].Alive || table.Cold(s) is not { Medical.Incapacitated: false, IsImprisoned: false } c || !c.CarriedBy.IsNone) continue;
                float d = Vector3.DistanceSquared(table.Next[s].Position, at);
                if (world.Sight.FloorOf(s, table.Next[s].Position) == floor) { if (d < sameD) (same, sameD) = (s, d); }
                else if (d < otherD) (other, otherD) = (s, d);
            }
        }
        return same >= 0 ? same : other;
    }

    /// <summary>
    /// Takes from another faction's container (<paramref name="code"/>: the pending crime the move sets, Theft for a right click or a give, Looting
    /// for a drag), then the steal roll (<c>FUN_140794810</c>, <b>Observed</b>). True when the thief was caught, and the move is refused. No watcher,
    /// or one inside a building other than the container's while that has no open door: always unseen. Caught by a watcher in bed: half the time it
    /// only says event 47; else it gets up (also from staying low or playing dead), says event 8 and remembers the thief, and the thief gets a Theft
    /// (timer 30) against the container's owner, "Got caught stealing." for the player. Thievery trains unless the chance was 0.98 or more, the thief
    /// is imprisoned or its pending crime's timer (+0x180) is 10 or more after the roll, so a caught thief, whose Theft has 30, gets none. Floors are
    /// the sight map's (<c>getFloor</c>).
    /// </summary>
    public bool Steal(World world, int thief, Locks.Fixture container, CrimeCode code)
    {
        var table = world.Characters;
        if (table.Cold(thief) is not { } c || container.Owner < 0 || container.Owner == c.Faction || !IsStealing(world, container)) return false;
        SetPending(world, thief, code, container.Owner, CharacterId.None, MinTimer);
        float p = 1;
        int w = Watcher(world, thief, container);
        if (w >= 0)
        {
            var wat = table.Next[w].Position;
            bool otherBuilding = world.Sight.BuildingOf(w) != container.Building;
            if (!(otherBuilding && !world.Fixtures.HasOpenDoor(container.Building)))
                p = StealChance(Thievery(c), Vector3.Distance(wat, container.Position), otherBuilding,
                    world.Sight.FloorOf(w, wat) != world.Sight.FloorAt(container.Building, container.Position));
        }
        return Roll(world, thief, w, p, container.Owner, CharacterId.None, Rng.StableHash(container.Id));
    }

    /// <summary>
    /// How the item left its owner's window (the three transfer paths of docs/game/carry-loot.md "Theft and witnesses").
    /// </summary>
    public enum TakeKind { RightClick, Drag, Give }

    /// <summary>
    /// <c>InventoryGUI::isStealing</c> (<c>FUN_140711740</c>, <b>Verified</b>; docs/game/carry-loot.md "Stolen ownership") for a take out of a
    /// character: not from an animal without a backpack on, nor from one of a <c>not real</c> faction. Its crime, steal roll and stolen mark all need
    /// it. The callers keep the player's own windows out.
    /// </summary>
    public static bool IsStealing(World world, CharacterCold owner) =>
        !(CharacterFacts.Animal(owner) && owner.Inventory?.Backpack is null) && !NotReal(world, owner.Faction);

    /// <summary>
    /// <c>InventoryGUI::isStealing</c> for a take out of a container: not one of a <c>not real</c> faction, nor a building of <c>building
    /// category</c> CAMPING.
    /// </summary>
    public static bool IsStealing(World world, Locks.Fixture container) =>
        !NotReal(world, container.Owner) && world.SystemOf<Construction.ConstructionSystem>()?.Data(container.RecordId)?.Category != "CAMPING";

    static bool NotReal(World world, int faction) => world.SystemOf<CrimeSystem>()?.Factions.NotReal(faction) == true;

    /// <summary>
    /// The pending crime a take out of another faction's character sets (<b>Verified</b> call sites): a right click (<c>FUN_140713d20</c>) Theft from
    /// an owner not down, Looting from a down one unless the loot is allowed (<see cref="IsLootTheft"/>); a drag (<c>FUN_140715560</c>) Theft from a
    /// down owner unless allowed, Looting from one not down; a give (<c>FUN_140712490</c>) Theft. Down is the game's <c>isDown</c>: unconscious or
    /// dead, playing dead included. Null: no crime.
    /// </summary>
    public static CrimeCode? TakeCode(World world, CharacterCold thief, CharacterCold owner, TakeKind kind)
    {
        bool down = owner.Medical?.Incapacitated == true;
        return kind switch
        {
            TakeKind.Give => CrimeCode.Theft,
            TakeKind.RightClick => !down ? CrimeCode.Theft : IsLootTheft(world, thief, owner) ? CrimeCode.Looting : null,
            _ => !down ? CrimeCode.Looting : IsLootTheft(world, thief, owner) ? CrimeCode.Theft : null,
        };
    }

    /// <summary>
    /// Takes an item from another faction's character (a body, a sleeper, a prisoner whose inventory is open): the pending crime of
    /// <see cref="TakeCode"/>, then the steal roll (<c>FUN_1407944d0</c>, <b>Observed</b>): the owner watches, p = 1.1 x thievery / 100, x0.75 for an
    /// equipped item, x0.75 again for equipped armour (the drag passes no item); an owner unconscious (not playing dead), dead, carried or imprisoned
    /// never notices, nor does anyone while the thief is down. Caught as <see cref="Steal"/>, the Theft against the owner's faction. True when
    /// caught.
    /// </summary>
    public bool Pickpocket(World world, int thief, int owner, Items.ItemInstance? item, TakeKind kind)
    {
        var table = world.Characters;
        if (table.Cold(thief) is not { } c || table.Cold(owner) is not { } o || o.Faction == c.Faction || !IsStealing(world, o)) return false;
        var ownerId = table.IdOf(owner);
        if (TakeCode(world, c, o, kind) is { } code) SetPending(world, thief, code, o.Faction, ownerId, MinTimer);
        if (kind == TakeKind.Drag) item = null;
        float p = 1;
        bool watches = o.Medical is not { Dead: true } && !(o.Medical is { Unconscious: true, PlayingDead: false }) && o.CarriedBy.IsNone && !o.IsImprisoned
            && c.Medical is not { Incapacitated: true };
        if (watches)
        {
            p = 1.1f * Thievery(c) * 0.01f;
            if (item is not null && Items.InventoryCommands.IsEquipmentSection(item.Section)) p *= item.Type == Data.Fcs.FcsRecordType.Armour ? 0.75f * 0.75f : 0.75f;
        }
        return Roll(world, thief, watches ? owner : -1, p, o.Faction, ownerId, Rng.Key(ownerId));
    }

    /// <summary>
    /// The steal roll's outcome (<c>FUN_140794810</c>): <paramref name="w"/> the watcher (-1: none); a caught thief's Theft is against
    /// <paramref name="faction"/>.
    /// </summary>
    bool Roll(World world, int thief, int w, float p, int faction, CharacterId victim, ulong salt)
    {
        var table = world.Characters;
        var c = table.Cold(thief)!;
        var thiefId = table.IdOf(thief);
        ulong key = Rng.Hash(world.Seed, Rng.Key(thiefId) ^ salt, RngPurpose.Steal, (ulong)world.Tick);
        bool caught = w >= 0 && Rng.Float(key) > p;
        if (caught)
        {
            var watcher = table.Cold(w)!;
            var watcherId = table.IdOf(w);
            if (watcher.InSomething == Locks.HeldState.Bed && Rng.Float(Rng.Mix(key + 1)) > 0.5f)
                world.Defer(new Dialogue.DialogueEvent(watcherId, thiefId, InBedEvent) { Tick = world.Tick + 1 });
            else
            {
                if (watcher.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, watcherId);
                else if (table.Next[w].Task == (byte)CharacterTask.Rest) TaskStart.Become(world, w, CharacterTask.Idle, Data.Gameplay.Ai.TaskType.Idle, TaskSource.Npc);
                // One staying low or playing dead stands (posture 0, docs/game/ragdolls.md "Staying low").
                if (watcher.Medical is { } low) low.StandUp();
                world.Defer(new Dialogue.DialogueEvent(watcherId, thiefId, CaughtEvent) { Tick = world.Tick + 1 });
                if (c.IsPlayer) world.Post("Got caught stealing.");
                SetPending(world, thief, CrimeCode.Theft, faction, victim, CaughtTimer, viaWrapper: true);
                if (watcher.SquadId >= 0) world.SystemOf<Dialogue.DialogueSystem>()?.Memory.Remember(world, watcher.SquadId, thiefId, Dialogue.MemoryTag.StoleFromMe);
            }
        }
        if (p < 0.98f && !c.IsImprisoned && (c.Pending?.Timer ?? 0) < 10 && c.Stats is { } stats && c.Race is { } race) Xp?.Continuous(stats, race, StatsEnumerated.Thieving, 1, caught ? CaughtXp : StealXp);
        return caught;
    }

    /// <summary>
    /// A ground item of another faction picked up inside a building (<c>Task_Pickup</c>, <b>Observed</b>): a pending Theft against the owner and
    /// thievery XP 20; no steal roll.
    /// </summary>
    public void StolePickUp(World world, int thief, int owner)
    {
        if (world.Characters.Cold(thief) is not { } c || owner < 0 || owner == c.Faction) return;
        SetPending(world, thief, CrimeCode.Theft, owner, CharacterId.None, MinTimer);
        if (c.Stats is { } stats && c.Race is { } race) Xp?.Continuous(stats, race, StatsEnumerated.Thieving, 1, PickUpXp);
    }
}