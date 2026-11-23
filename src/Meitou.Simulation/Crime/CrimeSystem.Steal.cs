using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Crime;

/// <summary>
/// Stealing from another faction's container (docs/game/carry-loot.md "Theft and witnesses"): the move sets a pending crime against the container's
/// owner character (a resident of the building it stands in; none, and no roll, without one), then the steal roll (<c>FUN_140794810</c>, chance
/// <c>FUN_1407944d0</c>, <b>Observed</b>) decides whether the container's watcher catches the thief. Caught: the move is refused, the watcher says
/// event 8 and remembers the thief (long-term STOLE_FROM_ME), and the pending crime becomes a Theft with timer 30, which witnesses and the guards'
/// event-8 lines answer as any other.
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
    public static float Thievery(CharacterCold c) =>
        (c.Stats?[StatsEnumerated.Thieving] ?? 0) * (c.Medical?.StatMultiplier(StatsEnumerated.Thieving) ?? 1);

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
    /// resident squad (<see cref="ResidentSquad"/>) of the building the container is furniture of (<see cref="StealSource.StandsIn"/>), not down,
    /// those on the thief's floor first; none without a resident squad. -1: none.
    /// </summary>
    static int Watcher(World world, int thief, StealSource container)
    {
        if (container.StandsIn is not { } building || ResidentSquad(world, building) is not { } squad) return -1;

        var table = world.Characters;
        int floor = world.Sight.FloorOf(thief, table.Next[thief].Position);
        var at = table.Next[thief].Position;
        int same = -1, other = -1;
        float sameD = float.MaxValue, otherD = float.MaxValue;
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s) || !table.Next[s].Alive
                || table.Cold(s) is not { Medical.Incapacitated: false, IsImprisoned: false } c || !c.CarriedBy.IsNone)
            {
                continue;
            }
            float d = Vector3.DistanceSquared(table.Next[s].Position, at);
            if (world.Sight.FloorOf(s, table.Next[s].Position) == floor) { if (d < sameD) (same, sameD) = (s, d); }
            else if (d < otherD) (other, otherD) = (s, d);
        }
        return same >= 0 ? same : other;
    }

    /// <summary>
    /// The stolen mark of goods of <paramref name="building"/> owned by <paramref name="faction"/> (an item of one of its containers, a loose item
    /// of one of its slots): its resident squad's leader of that faction, else the faction alone. The original names a character (<b>Observed</b>,
    /// docs/game/economy.md "Fencing, stealing and smuggling": the item's <c>ownedby</c> is the bar's barman); that it is the leader rather than
    /// another resident is <b>Unknown</b>.
    /// </summary>
    public static StolenMark OwnerMark(World world, string building, int faction)
    {
        var table = world.Characters;
        foreach (var squad in world.Squads.All)
        {
            if (squad.HomeBuilding?.Key != building || squad.Faction != faction || !table.TryResolveNext(squad.Leader, out int s))
                continue;

            if (table.Cold(s) is { } leader && leader.Faction == faction) return StolenMark.Of(leader, squad.Leader);
        }

        return StolenMark.OfFaction(faction);
    }

    /// <summary>
    /// Takes from another faction's container by <paramref name="kind"/>: the pending crime against its owner character
    /// (<see cref="PendSteal(World, int, StealSource, TakeKind)"/>), then the steal roll (<c>FUN_140794810</c>, <b>Observed</b>). True when the thief
    /// was caught, and the move is refused. No watcher, or one inside a building other than the container's while that has no open door: always
    /// unseen. Caught by a watcher in bed: half the time it only says event 47; else it gets up (also from staying low or playing dead), says event 8
    /// and remembers the thief, and the thief gets a Theft (timer 30) against the container's owner, "Got caught stealing." for the player. Thievery
    /// trains unless the chance was 0.98 or more, the thief is imprisoned or its pending crime's timer (+0x180) is 10 or more after the roll, so a
    /// caught thief, whose Theft has 30, gets none. Floors are the sight map's (<c>getFloor</c>).
    /// </summary>
    public bool Steal(World world, int thief, Locks.Fixture container, TakeKind kind) => Steal(world, thief, StealSource.Of(container), kind);

    /// <summary>
    /// <see cref="Steal(World, int, Locks.Fixture, TakeKind)"/> out of any holder of another faction's goods: a container or a town building's
    /// machine. A give rolls on the owner character itself (<c>FUN_140712490</c> passes it to the roll, <b>Verified</b> decompile): the pickpocket
    /// chance, a caught thief's Theft against that character.
    /// </summary>
    public bool Steal(World world, int thief, StealSource container, TakeKind kind)
    {
        if (!PendSteal(world, thief, container, kind, out int owner)) return false;
        if (kind == TakeKind.Give) return RollOn(world, thief, owner, null);

        float p = ContainerChance(world, thief, world.Characters.Cold(thief)!, container, out int w);
        return Roll(world, thief, w, p, container.Owner, CharacterId.None, Rng.StableHash(container.Id), container.Id);
    }

    /// <summary>
    /// The pending crime of a take out of another faction's <paramref name="container"/> without the steal roll: the right click sets it before it
    /// asks for room, so a thief without room gets it but no roll (<c>FUN_140713d20</c>, <b>Verified</b> decompile). False when the take is no
    /// steal.
    /// </summary>
    public bool PendSteal(World world, int thief, Locks.Fixture container, TakeKind kind) =>
        PendSteal(world, thief, StealSource.Of(container), kind, out _);

    /// <summary>
    /// <see cref="PendSteal(World, int, Locks.Fixture, TakeKind)"/> out of any holder of another faction's goods (<c>FUN_140713d20</c>,
    /// <c>FUN_140715560</c>, <c>FUN_140712490</c>, <b>Verified</b> decompile): a take with no owner character (<see cref="OwnerCharacter"/>) or one
    /// of the player's faction is no steal, so it has neither a crime nor a roll. Else the crime (<see cref="HolderCode"/>) is set against the owner
    /// character's faction with it as the victim, and the roll follows.
    /// </summary>
    public bool PendSteal(World world, int thief, StealSource container, TakeKind kind) => PendSteal(world, thief, container, kind, out _);

    /// <summary>
    /// <see cref="PendSteal(World, int, StealSource, TakeKind)"/>, giving the owner character (-1 when the take is no steal).
    /// </summary>
    bool PendSteal(World world, int thief, StealSource container, TakeKind kind, out int owner)
    {
        owner = -1;
        var table = world.Characters;
        if (table.Cold(thief) is not { } c || container.Owner < 0 || container.Owner == c.Faction || !IsStealing(world, container))
            return false;

        int found = OwnerCharacter(world, container);
        if (found < 0 || table.Cold(found) is not { } o || o.Faction == world.Player.Faction) return false;

        owner = found;
        if (HolderCode(kind, CharacterFacts.Down(o)) is { } code) SetPending(world, thief, code, o.Faction, table.IdOf(found), MinTimer);
        return true;
    }

    /// <summary>
    /// The pending crime a take out of a building's window sets (<b>Verified</b> call sites): a right click Theft, none while the owner character is
    /// down (the loot permission <c>FUN_140793940</c> allows any subject that is no character); a drag Looting (its downed-owner branch is for a
    /// character's window alone); a give Theft.
    /// </summary>
    static CrimeCode? HolderCode(TakeKind kind, bool ownerDown) => kind switch
    {
        TakeKind.RightClick => ownerDown ? null : CrimeCode.Theft,
        TakeKind.Drag => CrimeCode.Looting,
        _ => CrimeCode.Theft,
    };

    /// <summary>
    /// The owner character of a take out of <paramref name="container"/> (<c>FUN_14070cef0</c>, <b>Verified</b> decompile, docs/game/carry-loot.md
    /// "Theft and witnesses"): Building slot 59 (<c>isIndoors</c>, <c>FUN_140547530</c>) gives the building the holder stands in only when its
    /// BUILDING record is <c>is interior furniture</c>, and that building's resident squad gives its effective leader
    /// (<see cref="Population.PopulationSystem.EffectiveLeader"/>, <c>FUN_140792b90</c>: the leader, else the kept backup, else a member up). -1:
    /// none (a well, a farm, a rain collector standing outdoors, a building nobody lives in).
    /// </summary>
    static int OwnerCharacter(World world, StealSource container)
    {
        if (container.StandsIn is not { } building
            || world.SystemOf<Construction.ConstructionSystem>()?.Data(container.RecordId) is not { IsInteriorFurniture: true })
        {
            return -1;
        }

        return ResidentLeader(world, building);
    }

    /// <summary>
    /// <see cref="ResidentLeader"/> as a character id, <see cref="CharacterId.None"/> when nobody lives in <paramref name="building"/>.
    /// </summary>
    static CharacterId ResidentVictim(World world, string building)
    {
        int leader = ResidentLeader(world, building);
        return leader >= 0 ? world.Characters.IdOf(leader) : CharacterId.None;
    }

    /// <summary>
    /// The effective leader of <paramref name="building"/>'s resident squad (<see cref="ResidentSquad"/>,
    /// <see cref="Population.PopulationSystem.EffectiveLeader"/>, <c>FUN_140547ba0</c> -> <c>FUN_140792b90</c>); -1 when nobody lives there.
    /// </summary>
    static int ResidentLeader(World world, string building)
    {
        if (ResidentSquad(world, building) is not { } squad) return -1;

        var lead = world.SystemOf<Population.PopulationSystem>()?.EffectiveLeader(world, squad) ?? squad.Leader;
        return world.Characters.TryResolveNext(lead, out int s) ? s : -1;
    }

    /// <summary>
    /// The resident squad of <paramref name="building"/>: the original keeps one residents handle (+0xD0), written together with the squad template
    /// the building carries by every squad that takes it as home (<c>FUN_140553210</c>, docs/game/factions-squads-towns.md "Whose template a home
    /// carries"), so it is the squad living there of that template (<see cref="Locks.FixtureRegistry.ResidentsTemplate"/>), the last made of them
    /// when several are. Null: none of that template lives there (nobody, or only housemates of another template).
    /// </summary>
    static Factions.Squad? ResidentSquad(World world, string building)
    {
        Factions.Squad? resident = null;
        foreach (var squad in world.Squads.All)
        {
            if (squad.HomeBuilding is { } home && home.Key == building && squad.TemplateId == world.Fixtures.ResidentsTemplate(home))
                resident = squad;
        }

        return resident;
    }

    /// <summary>
    /// The chance the inventory window prints for a take out of another faction's container (<c>FUN_140712920</c> "Stealing chance:", the roll's
    /// own p): null when the take is not a steal (<see cref="Steal(World, int, StealSource, TakeKind)"/>'s first gates). It is printed with no
    /// owner character too: the window asks only <c>isStealing</c> and the building's faction (<b>Verified</b> decompile).
    /// </summary>
    public static float? StealChanceOf(World world, int thief, StealSource container)
    {
        if (world.Characters.Cold(thief) is not { } c || container.Owner < 0 || container.Owner == c.Faction
            || container.Owner == world.Player.Faction || !IsStealing(world, container))
        {
            return null;
        }

        return ContainerChance(world, thief, c, container, out _);
    }

    /// <summary>
    /// The steal roll's p out of a container (<see cref="StealChance"/>) and its watcher <paramref name="w"/> (-1: none, p 1).
    /// </summary>
    static float ContainerChance(World world, int thief, CharacterCold c, StealSource container, out int w)
    {
        var table = world.Characters;
        w = Watcher(world, thief, container);
        if (w < 0) return 1;

        var wat = table.Next[w].Position;
        string building = container.StandsIn!;
        bool otherBuilding = world.Sight.BuildingOf(w) != building;
        if (otherBuilding && !world.Fixtures.HasOpenDoor(building)) return 1;

        bool otherFloor = world.Sight.FloorOf(w, wat) != world.Sight.FloorAt(building, container.Position);
        return StealChance(Thievery(c), Vector3.Distance(wat, container.Position), otherBuilding, otherFloor);
    }

    /// <summary>
    /// How the item left its owner's window (the three transfer paths of docs/game/carry-loot.md "Theft and witnesses").
    /// </summary>
    public enum TakeKind
    {
        /// <summary>
        /// A right click (<c>FUN_140713d20</c>).
        /// </summary>
        RightClick,
        /// <summary>
        /// A drag (<c>FUN_140715560</c>).
        /// </summary>
        Drag,
        /// <summary>
        /// A give (<c>FUN_140712490</c>).
        /// </summary>
        Give
    }

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
    public static bool IsStealing(World world, Locks.Fixture container) => IsStealing(world, StealSource.Of(container));

    /// <summary>
    /// <see cref="IsStealing(World, Locks.Fixture)"/> for any holder of goods (a container or a town building's machine).
    /// </summary>
    public static bool IsStealing(World world, StealSource container) =>
        !NotReal(world, container.Owner) && world.SystemOf<Construction.ConstructionSystem>()?.Data(container.RecordId)?.Category != "CAMPING";

    /// <summary>
    /// Whether <paramref name="faction"/> is <c>not real</c> (false without a crime system).
    /// </summary>
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
        if (!PendPickpocket(world, thief, owner, kind)) return false;

        return RollOn(world, thief, owner, kind == TakeKind.Drag ? null : item);
    }

    /// <summary>
    /// The steal roll on a character (<see cref="Pickpocket"/>'s): it watches with <see cref="PickpocketChance"/>, and a caught thief's Theft is
    /// against its faction with it as the victim. True when caught.
    /// </summary>
    bool RollOn(World world, int thief, int owner, Items.ItemInstance? item)
    {
        var table = world.Characters;
        var ownerId = table.IdOf(owner);
        float? p = PickpocketChance(table.Cold(thief)!, table.Cold(owner)!, item);
        return Roll(world, thief, p is null ? -1 : owner, p ?? 1, table.Cold(owner)!.Faction, ownerId, Rng.Key(ownerId));
    }

    /// <summary>
    /// The steal roll for a code mod's own theft (docs/modding.md "Crimes"): <paramref name="watcher"/> (none or gone: nobody, never caught)
    /// notices the thief unless a draw falls under <paramref name="chance"/> (0: always caught, 1 or more: never), with the game's outcome of
    /// <see cref="Steal(World, int, StealSource, TakeKind)"/>: a watcher in bed may only speak, else it gets up, says event 8 and remembers the
    /// thief, and the thief gets a Theft (timer 30) against <paramref name="faction"/> with <paramref name="victim"/> as its victim; thievery XP
    /// as the game's roll. The draw is the world seed's, the thief's, the victim's (else the watcher's) and the tick's. No pending crime is set
    /// before the roll (<see cref="SetPending"/> does that). True when caught. Serial phases.
    /// </summary>
    public bool StealRoll(World world, CharacterId thief, CharacterId watcher, float chance, int faction, CharacterId victim)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(thief, out int t)) throw new ArgumentException($"thief {thief} is not in the world", nameof(thief));
        if (!float.IsFinite(chance)) throw new ArgumentOutOfRangeException(nameof(chance), chance, "not a finite number");

        int w = table.TryResolveNext(watcher, out int found) ? found : -1;
        var salted = victim.IsNone ? watcher : victim;
        return Roll(world, t, w, chance, faction, victim, Rng.Key(salted));
    }

    /// <summary>
    /// The pending crime of a take out of another faction's character (<see cref="TakeCode"/>) without the steal roll, as
    /// <see cref="PendSteal(World, int, Locks.Fixture, TakeKind)"/> for a thief without room. False when the take is no steal.
    /// </summary>
    public bool PendPickpocket(World world, int thief, int owner, TakeKind kind)
    {
        var table = world.Characters;
        if (table.Cold(thief) is not { } c || table.Cold(owner) is not { } o || o.Faction == c.Faction || !IsStealing(world, o)) return false;

        if (TakeCode(world, c, o, kind) is { } code) SetPending(world, thief, code, o.Faction, table.IdOf(owner), MinTimer);
        return true;
    }

    /// <summary>
    /// The chance the inventory window prints for a take of <paramref name="item"/> out of another faction's character (<c>FUN_140712920</c>
    /// "Stealing chance:", <b>Observed</b>): the roll's p with the item passed, so an equipped item shows the x0.75 even though the drag rolls without
    /// it. Null when the take is not a steal (<see cref="Pickpocket"/>'s gates) or the owner does not watch (engine choice: the original's gate for a
    /// down owner is <b>Unknown</b>).
    /// </summary>
    public static float? StealChanceOf(World world, int thief, int owner, Items.ItemInstance item)
    {
        var table = world.Characters;
        if (table.Cold(thief) is not { } c || table.Cold(owner) is not { IsPlayer: false } o || o.Faction == c.Faction || !IsStealing(world, o))
            return null;

        return PickpocketChance(c, o, item);
    }

    /// <summary>
    /// The pickpocket roll's p (<see cref="Pickpocket"/>): 1.1 x thievery / 100, x0.75 for an equipped <paramref name="item"/>, x0.75 again for
    /// equipped armour; null when the owner does not watch.
    /// </summary>
    static float? PickpocketChance(CharacterCold c, CharacterCold o, Items.ItemInstance? item)
    {
        bool watches = o.Medical is not { Dead: true } && !(o.Medical is { Unconscious: true, PlayingDead: false })
            && o.CarriedBy.IsNone && !o.IsImprisoned
            && c.Medical is not { Incapacitated: true };
        if (!watches) return null;

        float p = 1.1f * Thievery(c) * 0.01f;
        if (item is null || !Items.InventoryCommands.IsEquipmentSection(item.Section)) return p;

        return p * (item.Type == Data.Fcs.FcsRecordType.Armour ? 0.75f * 0.75f : 0.75f);
    }

    /// <summary>
    /// The steal roll's outcome (<c>FUN_140794810</c>): <paramref name="w"/> the watcher (-1: none); a caught thief's Theft is against
    /// <paramref name="faction"/> with the rolled-on object as its victim: a character (<paramref name="victim"/>), or the holder a building's window
    /// shows (<paramref name="victimHolder"/>, its id).
    /// </summary>
    bool Roll(World world, int thief, int w, float p, int faction, CharacterId victim, ulong salt, string? victimHolder = null)
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
                else if (table.Next[w].Task == (byte)CharacterTask.Rest && !Ai.DecisionSystem.Ordered(watcher))   // an order's rest goes on
                    TaskStart.Become(world, w, CharacterTask.Idle, Data.Gameplay.Ai.TaskType.Idle, TaskSource.Npc);
                // One staying low or playing dead stands (posture 0, docs/game/ragdolls.md "Staying low").
                if (watcher.Medical is { } low) low.StandUp();
                world.Defer(new Dialogue.DialogueEvent(watcherId, thiefId, CaughtEvent) { Tick = world.Tick + 1 });
                if (c.IsPlayer)
                {
                    // the original's item moves close every inventory window with result 8 (FUN_140713d20, FUN_140715560, FUN_140712490)
                    world.Post("Got caught stealing.");
                    world.CloseInventories();
                }

                SetPending(world, thief, CrimeCode.Theft, faction, victim, CaughtTimer, viaWrapper: true, victimHolder);
                if (watcher.SquadId >= 0)
                    world.SystemOf<Dialogue.DialogueSystem>()?.Memory.Remember(world, watcher.SquadId, thiefId, Dialogue.MemoryTag.StoleFromMe);
            }
        }
        if (p < 0.98f && !c.IsImprisoned && (c.Pending?.Timer ?? 0) < 10 && c.Stats is { } stats && c.Race is { } race)
            Xp?.Continuous(stats, race, StatsEnumerated.Thieving, 1, caught ? CaughtXp : StealXp, ExperienceSource.Thievery);
        return caught;
    }

    /// <summary>
    /// A ground item of faction <paramref name="owner"/> picked up inside <paramref name="building"/> (<c>Task_Pickup</c> <c>FUN_14034f340</c>,
    /// <b>Verified</b> decompile): a pending Theft against the faction of that building (<see cref="Locks.FixtureRegistry.OwnerOf"/>) with its
    /// resident leader as the victim (<see cref="ResidentLeader"/>, none when nobody lives there), so that leader and its squad mates witness it as
    /// stolen from them; with <see cref="World.PickUpTheftFix"/> (Meitou) against <paramref name="owner"/> with the item's owner character
    /// <paramref name="ownerCharacter"/> as the victim. Thievery XP 20; no steal roll.
    /// </summary>
    public void StolePickUp(World world, int thief, int owner, CharacterId ownerCharacter, string building)
    {
        if (world.Characters.Cold(thief) is not { } c || owner < 0 || owner == c.Faction) return;

        var (faction, victim) = world.PickUpTheftFix ? (owner, ownerCharacter) : (world.Fixtures.OwnerOf(building), ResidentVictim(world, building));
        SetPending(world, thief, CrimeCode.Theft, faction, victim, MinTimer);
        if (c.Stats is { } stats && c.Race is { } race)
            Xp?.Continuous(stats, race, StatsEnumerated.Thieving, 1, PickUpXp, ExperienceSource.Thievery);
    }
}

/// <summary>
/// What goods are stolen out of: a container (<see cref="Locks.Fixture"/>) or a town building's machine, the building itself (the original's
/// inventory GUI asks the same of either): its id (the roll's seed), owner faction, the run-time key of the building it belongs to (whose resident
/// of that faction its stolen mark names), where it stands, its BUILDING record, and <paramref name="StandsIn"/>, the building it is furniture of
/// (<c>FUN_1400d5fa0</c>: an interior piece's house, a town building's <c>isInsideBuilding</c> parent; null for none), whose residents watch it
/// and own it.
/// </summary>
public readonly record struct StealSource(string Id, int Owner, string Building, Vector3 Position, string RecordId, string? StandsIn)
{
    /// <summary>
    /// A container's: an interior piece stands in its house (a building's own lock holds no goods).
    /// </summary>
    public static StealSource Of(Locks.Fixture container) =>
        new(container.Id, container.Owner, container.Building, container.Position, container.RecordId,
            container.Id != container.Building ? container.Building : null);
}
