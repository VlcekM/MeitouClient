using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The finders of the watch goals (docs/simulation.md "Town duties"): the shoo's stranger in the home building (158 / 283) and the ransack's
/// building (216), with the per-tick map of who is inside each building and the ransack's door planner.
/// </summary>
internal sealed class TownWatchFinders(PopulationData data)
{
    /// <summary>
    /// The salt of the ransack's target roll.
    /// </summary>
    const ulong RansackSalt = 0x5261_6E73_6163_6B21;

    /// <summary>
    /// The senses (resolved on first use), for the shoo finder.
    /// </summary>
    AggroSystem? _aggro;

    /// <summary>
    /// Who is inside each building, by building key, as of <see cref="_insidersTick"/> (the shoo finder's pass).
    /// </summary>
    readonly Dictionary<string, List<int>> _insiders = new(StringComparer.Ordinal);

    /// <summary>
    /// The tick <see cref="_insiders"/> was taken (-1: never).
    /// </summary>
    long _insidersTick = -1;

    /// <summary>
    /// The door planner of the ransack's way in (<see cref="EntryTo"/>).
    /// </summary>
    readonly DoorPlan _doorPlan = new();

    /// <summary>
    /// <c>AI::findStrangersInMyBuilding</c> (<c>FUN_1408ced70</c>, goals 158 / 283): in the squad's home building, the first character (slot order,
    /// the original takes its sensed-list order) neither of an allied faction nor an enemy, not down, dead or caged, not a slave of the shooer's
    /// faction and not law enforcement (AUTHORITY_FIGURE, engine reading); for an NPC squad also any of the player's characters there that is not
    /// sneaking or that the shooer has spotted. Not one another errand of <paramref name="duties"/> shoos. None: none.
    /// </summary>
    public CharacterId Stranger(World world, int slot, CharacterCold cold, Squad squad, IReadOnlyList<TownDuty> duties)
    {
        if (squad.HomeBuilding is not { } home) return CharacterId.None;
        _aggro ??= world.Systems.OfType<AggroSystem>().FirstOrDefault();
        TakeInsiders(world);
        if (!_insiders.TryGetValue(home.Key, out var inside)) return CharacterId.None;

        var table = world.Characters;
        foreach (int s in inside)
        {
            if (s == slot || table.Cold(s) is not { Medical.Incapacitated: false } c || c.IsImprisoned) continue;
            if (c.SlaveState == 1 && c.SlaveOwner == cold.Faction) continue;
            var id = table.IdOf(s);
            if (Shooed(duties, id)) continue;
            if (Neutral(cold, c) && c.NpcClass != Data.Gameplay.FundamentalType.LawEnforcement) return id;
            if (!cold.IsPlayer && c.IsPlayer && ((c.Orders & CharacterOrders.Sneak) == 0 || _aggro?.Spotted(slot, id) == true))
                return id;
        }

        return CharacterId.None;
    }

    /// <summary>
    /// <c>AI::findRandomBuildingInTown</c> (<c>FUN_1408cb510</c>, goal 216): the squad's town as <c>FUN_140269cd0</c> gives it (the override's mission
    /// town, a campaign's war package's the campaign target, <see cref="Blackboard.ContractMissionTown"/>; else the running package's mission town),
    /// else the town the member stands in; a player town is not drawn from; one of its standing buildings with an interior, drawn uniformly at random
    /// among those the planner would reach: row 156 Entering building (the only provider of effect 107, which goal 216 wants) needs
    /// DESTINATION_IS_ACCESSIBLE (59: <c>hasAnOpenDoor</c>) and DESTINATION_NOT_BLOCKED_BY_FORTIFICATIONS (90: no closed gate between), <b>Observed</b>.
    /// The door sub-steps are planned (<see cref="EntryTo"/>: open the door, row 72, or bash it, row 81); a building with no way in is left out of
    /// the draw. A closed gate on the route is planned as for an attacker (<see cref="LockSystem.GateEntry"/>: bash it, row 123 / 290, or pass an
    /// unlocked one, row 122). Null: none.
    /// </summary>
    public (Meitou.Data.World.TownBuilding Building, string? Door)? RansackTarget(World world, Squad squad, int slot, LockSystem? locks)
    {
        int site = squad.Ai is { ContractMissionTown.Site: >= 0 } b ? b.ContractMissionTown.Site
            : squad.Ai is { Destination: { Site: >= 0 } dest } ? dest.Site
            : AiSystem.TownAt(data, world, world.Characters.Next[slot].Position).Site;
        if (site < 0) return null;

        var rooms = WaysIn(world, slot, site, locks);
        if (rooms.Count == 0) return null;

        var roll = Rng.Hash(world.Seed, (ulong)Rng.Key(world.Characters.IdOf(slot)) ^ (ulong)world.Tick, RngPurpose.Slow, RansackSalt);
        return rooms[Rng.Int(roll, rooms.Count)];
    }

    /// <summary>
    /// Takes who is inside each building, once a tick.
    /// </summary>
    void TakeInsiders(World world)
    {
        if (_insidersTick == world.Tick) return;
        _insidersTick = world.Tick;
        _insiders.Clear();

        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Next[s].Alive && world.Sight.BuildingOf(s) is { } key)
                (_insiders.TryGetValue(key, out var list) ? list : _insiders[key] = []).Add(s);
        }
    }

    /// <summary>
    /// Whether another errand already shoos <paramref name="id"/>.
    /// </summary>
    static bool Shooed(IReadOnlyList<TownDuty> duties, CharacterId id)
    {
        foreach (var o in duties)
        {
            if (o.Kind == TownDutyKind.Shoo && o.Body == id) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="c"/> is of another faction that is neither allied with nor an enemy of the shooer's.
    /// </summary>
    bool Neutral(CharacterCold cold, CharacterCold c)
    {
        if (c.Faction == cold.Faction) return false;
        if (!data.Relations.Contains(cold.Faction) || !data.Relations.Contains(c.Faction)) return true;
        return !data.Relations.IsAlly(cold.Faction, c.Faction) && !AggroSystem.IsEnemy(data.Relations, cold.Faction, c.Faction);
    }

    /// <summary>
    /// The town's standing buildings with an interior the member gets into, each with the door (or gate) it bashes first.
    /// </summary>
    List<(Meitou.Data.World.TownBuilding, string?)> WaysIn(World world, int slot, int site, LockSystem? locks)
    {
        var from = world.Characters.Next[slot].Position;
        var rooms = new List<(Meitou.Data.World.TownBuilding, string?)>();
        foreach (var x in data.Sites[site].Buildings)
        {
            if (world.Fixtures.IsDestroyed(x) || x.InteriorLayout.Length == 0 && x.InteriorLayouts.Count == 0) continue;
            var gate = locks?.GateEntry(world, slot, from, x.Position) ?? (DoorPlan.Entry.Walk, null);
            if (gate.Entry == DoorPlan.Entry.Blocked) continue;
            if (gate.Door is not null) rooms.Add((x, gate.Door));   // the gate is bashed first (rows 123 / 290)
            else if (EntryTo(world, slot, x, locks) is { } door) rooms.Add((x, door.Length == 0 ? null : door));
        }

        return rooms;
    }

    /// <summary>
    /// How the member gets into a building, by the planner (row 156's state 59 <c>hasAnOpenDoor</c>, <see cref="DoorPlan.PlansEntry"/>): "" to
    /// walk in (a door open, none, or a closed one the character opens as it passes, row 72), the id of the door it bashes first (row 81, a locked
    /// door of a building it is aggressive towards), null when the planner finds no way in.
    /// </summary>
    string? EntryTo(World world, int slot, Meitou.Data.World.TownBuilding building, LockSystem? locks)
    {
        if (world.Fixtures.HasOpenDoor(building.Key)) return "";
        if (LockSystem.MainGateDoor(world, building.Key) is not { } main) return "";
        var entry = _doorPlan.PlansEntry(world, world.SystemOf<Combat.CombatSystem>(), slot, building.Key, main.Door, main.Spot, locks);
        return entry switch { DoorPlan.Entry.Walk => "", DoorPlan.Entry.Bash => main.Door.Id, _ => null };
    }
}
